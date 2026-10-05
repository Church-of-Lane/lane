using System.Diagnostics;
using System.Text.Json;
using Lane.Core.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Host.Lifecycle;

public enum RestartMode
{
    /// <summary>Supervisor under systemd or the launchd job install.sh writes; Relaunch otherwise.</summary>
    Auto,

    /// <summary>Exit with <see cref="RestartOptions.ExitCode"/> and let the service manager start Lane again.</summary>
    Supervisor,

    /// <summary>Start a new copy of this process, then exit.</summary>
    Relaunch
}

public sealed class RestartOptions
{
    public const string SectionName = "Lane:Restart";

    public RestartMode Mode { get; set; } = RestartMode.Auto;

    public int ExitCode { get; set; } = 75;

    public string Configuration { get; set; } = "Release";

    /// <summary>Built first, relative to the source directory.</summary>
    public string Solution { get; set; } = "Lane.slnx";

    /// <summary>Published over the running build, relative to the source directory.</summary>
    public string Project { get; set; } = "Lane.Host/Lane.Host.csproj";

    public TimeSpan BuildTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Starts of a new build that may fail before the previous build is put back.</summary>
    public int MaxFailedStarts { get; set; } = 2;

    /// <summary>How long a new build has to stay up to count as started.</summary>
    public TimeSpan HealthyAfter { get; set; } = TimeSpan.FromSeconds(60);

    public string LaunchdLabel { get; set; } = "com.lane.bot";

    public RestartMode ResolvedMode =>
        Mode != RestartMode.Auto ? Mode
        : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")) ||
          Environment.GetEnvironmentVariable("XPC_SERVICE_NAME") == LaunchdLabel
            ? RestartMode.Supervisor
            : RestartMode.Relaunch;
}

/// <summary>What the startup check found, before the host is built.</summary>
/// <param name="ExitNow">The previous build was just put back; exit so the service manager starts it.</param>
public sealed record RestartStartup(RestartRecord? Previous, bool ExitNow);

/// <summary>
/// Restarts Lane, and rebuilds her from source first when asked. A new build is laid over the
/// running one file by file, with what it replaced kept in <c>.rollback/</c>; if it then fails to
/// start <see cref="RestartOptions.MaxFailedStarts"/> times, the next start puts the old files back.
/// </summary>
public sealed class SelfUpdater(
    RestartOptions           options,
    RestartStartup           startup,
    string                   target,
    IHostApplicationLifetime lifetime,
    IProcessRunner           runner,
    ILogger<SelfUpdater>     log,
    Action<int>?             setExitCode = null) : IRestartCoordinator, IHostedService
{
    public const string MarkerFile  = "restart.json";
    public const string RollbackDir = ".rollback";
    public const string StagingDir  = ".staging";
    private const string Manifest   = "manifest.json";

    /// <summary>Configuration and data belong to the running instance and are never replaced.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "appsettings.json", "appsettings.local.json", ".env",
        "lane.db", "lane.db-wal", "lane.db-shm", "lane.log", MarkerFile
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly SemaphoreSlim _updating = new(1, 1);
    private CancellationTokenSource? _health;

    public RestartRecord? PreviousRestart => startup.Previous;

    public void Restart(string reason)
    {
        RestartMode mode = options.ResolvedMode;

        log.LogWarning("Restarting ({Mode}): {Reason}", mode, reason);

        _ = Task.Run(async () =>
        {
            // Lets a reply in flight be delivered first.
            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);

            if (mode == RestartMode.Relaunch) Relaunch();
            else (setExitCode ?? (code => Environment.ExitCode = code))(options.ExitCode);

            lifetime.StopApplication();
        });
    }

    public async Task<RestartOutcome> BuildAndRestartAsync(string sourceDirectory, string reason, CancellationToken ct)
    {
        if (!await _updating.WaitAsync(0, ct).ConfigureAwait(false))
            return new RestartOutcome(RestartStatus.Busy, "A rebuild is already in progress.");

        try
        {
            log.LogInformation("Rebuilding from {Source}", sourceDirectory);

            ProcessResult build = await DotnetAsync(sourceDirectory, ct,
                "build", Path.Combine(sourceDirectory, options.Solution), "-c", options.Configuration, "--nologo").ConfigureAwait(false);

            if (!build.Succeeded)
                return new RestartOutcome(RestartStatus.BuildFailed, "The build failed, so nothing was restarted.", Errors(build));

            if (options.ResolvedMode != RestartMode.Supervisor)
                return new RestartOutcome(RestartStatus.NotSupported,
                    "The build succeeded, but Lane is not running under a service manager, so she cannot replace herself.");

            string staging = Path.Combine(target, StagingDir);

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

            ProcessResult publish = await DotnetAsync(sourceDirectory, ct,
                "publish", Path.Combine(sourceDirectory, options.Project), "-c", options.Configuration,
                "-o", staging, "--nologo").ConfigureAwait(false);

            if (!publish.Succeeded)
                return new RestartOutcome(RestartStatus.PublishFailed, "Publishing failed, so nothing was restarted.", Errors(publish));

            string? commit = await CommitAsync(sourceDirectory, ct).ConfigureAwait(false);

            int replaced;

            try
            {
                replaced = Overlay(staging, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogError(ex, "Installing the new build failed; putting the previous build back");
                Rollback(target);

                return new RestartOutcome(RestartStatus.PublishFailed,
                    $"Installing the new build failed, so the previous build was put back: {ex.Message}");
            }

            await WriteMarkerAsync(target, new Marker(reason, commit, DateTimeOffset.UtcNow, Attempts: 0, RolledBack: false), ct)
                .ConfigureAwait(false);

            log.LogWarning("Installed a new build ({Commit}, {Count} files); restarting", commit ?? "unknown commit", replaced);

            Restart(reason);

            return new RestartOutcome(RestartStatus.Restarting,
                $"Built and installed {commit ?? "the new build"}. Restarting now; you will be told when you are back.");
        }
        finally { _updating.Release(); }
    }

    private Task<ProcessResult> DotnetAsync(string directory, CancellationToken ct, params string[] args) =>
        runner.RunAsync(new ProcessSpec
        {
            FileName         = "dotnet",
            Arguments        = args,
            WorkingDirectory = directory,
            Timeout          = options.BuildTimeout,
            MaxOutput        = 200_000
        }, ct);

    private async Task<string?> CommitAsync(string source, CancellationToken ct)
    {
        ProcessResult result = await runner.RunAsync(new ProcessSpec
        {
            FileName = "git", Arguments = ["log", "-1", "--format=%h %s"], WorkingDirectory = source
        }, ct).ConfigureAwait(false);

        return result.Succeeded && result.Output.Trim() is { Length: > 0 } line ? line : null;
    }

    /// <summary>The error lines of a failed build, or its tail when it printed none.</summary>
    internal static string Errors(ProcessResult result)
    {
        if (result.TimedOut) return "It timed out.";

        string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string[] errors = [.. lines.Where(l => l.Contains(" error ", StringComparison.Ordinal)).Distinct().Take(30)];

        return string.Join('\n', errors.Length > 0 ? errors : lines.TakeLast(30));
    }

    /// <summary>Lays every file in <paramref name="staging"/> over <paramref name="target"/>, keeping what it replaces.</summary>
    /// <returns>The number of files installed.</returns>
    internal static int Overlay(string staging, string target)
    {
        string rollback = Path.Combine(target, RollbackDir);

        if (Directory.Exists(rollback)) Directory.Delete(rollback, recursive: true);

        Directory.CreateDirectory(rollback);

        List<string> added = [];
        int count = 0;

        foreach (string source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(staging, source);

            if (Protected.Contains(Path.GetFileName(relative))) continue;

            string destination = Path.Combine(target, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination))
            {
                string kept = Path.Combine(rollback, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Copy(destination, kept, overwrite: true);
            }
            else
            {
                added.Add(relative);
            }

            // Replaced by rename, never overwritten in place.
            string temporary = destination + ".lane-new";
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);

            count++;
        }

        File.WriteAllText(Path.Combine(rollback, Manifest), JsonSerializer.Serialize(added, Json));

        return count;
    }

    /// <summary>Puts back what the last <see cref="Overlay"/> replaced, and removes what it added.</summary>
    internal static void Rollback(string target)
    {
        string rollback = Path.Combine(target, RollbackDir);

        if (!Directory.Exists(rollback)) return;

        string manifest = Path.Combine(rollback, Manifest);

        if (File.Exists(manifest))
        {
            foreach (string relative in JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [])
            {
                string added = Path.Combine(target, relative);
                if (File.Exists(added)) File.Delete(added);
            }
        }

        foreach (string kept in Directory.EnumerateFiles(rollback, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(rollback, kept);

            if (relative == Manifest) continue;

            string destination = Path.Combine(target, relative);
            string temporary   = destination + ".lane-new";

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(kept, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }

        Directory.Delete(rollback, recursive: true);
    }

    /// <summary>
    /// Run before the host is built. Counts this start against a pending self-update, and rolls
    /// the update back once it has failed to start too many times.
    /// </summary>
    public static RestartStartup CheckOnStartup(string target, RestartOptions options, TextWriter log)
    {
        string path = Path.Combine(target, MarkerFile);

        Marker? marker;

        try
        {
            marker = File.Exists(path) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            log.WriteLine($"Ignoring an unreadable {MarkerFile}: {ex.Message}");
            File.Delete(path);
            return new RestartStartup(null, ExitNow: false);
        }

        if (marker is null) return new RestartStartup(null, ExitNow: false);

        RestartRecord record = new(marker.Reason, marker.Commit, marker.RequestedAt, marker.RolledBack);

        if (marker.RolledBack) return new RestartStartup(record, ExitNow: false);

        Marker counted = marker with { Attempts = marker.Attempts + 1 };

        if (counted.Attempts > options.MaxFailedStarts && Directory.Exists(Path.Combine(target, RollbackDir)))
        {
            log.WriteLine($"The build installed for '{marker.Reason}' failed to start {marker.Attempts} times; putting the previous build back.");

            Rollback(target);

            File.WriteAllText(path, JsonSerializer.Serialize(counted with { RolledBack = true }, Json));

            return new RestartStartup(record with { RolledBack = true }, ExitNow: true);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(counted, Json));

        return new RestartStartup(record, ExitNow: false);
    }

    private static async Task WriteMarkerAsync(string target, Marker marker, CancellationToken ct) =>
        await File.WriteAllTextAsync(Path.Combine(target, MarkerFile), JsonSerializer.Serialize(marker, Json), ct)
            .ConfigureAwait(false);

    public Task StartAsync(CancellationToken ct)
    {
        if (startup.Previous is null) return Task.CompletedTask;

        _health = new CancellationTokenSource();

        CancellationToken token = _health.Token;

        lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(options.HealthyAfter, token).ConfigureAwait(false);

                File.Delete(Path.Combine(target, MarkerFile));

                string staging = Path.Combine(target, StagingDir);
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

                log.LogInformation("The new build has stayed up for {Period}; the update is complete", options.HealthyAfter);
            }
            catch (OperationCanceledException) { /* stopped before it counted as healthy */ }
            catch (Exception ex) { log.LogWarning(ex, "Could not clear the restart marker"); }
        }, token));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_health is not null) await _health.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>Starts a second copy of this process with the same command line.</summary>
    private void Relaunch()
    {
        try
        {
            string? exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                log.LogWarning("Could not determine the current executable; not relaunching");
                return;
            }

            string[] args = Environment.GetCommandLineArgs();

            // "dotnet lane.dll …" has dotnet as the process path and the dll as argument zero; a
            // published apphost's argument zero is itself.
            bool viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase);

            ProcessStartInfo psi = new(exe) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };

            foreach (string arg in viaDotnet ? args : args.Skip(1)) psi.ArgumentList.Add(arg);

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to relaunch");
        }
    }

    private sealed record Marker(string Reason, string? Commit, DateTimeOffset RequestedAt, int Attempts, bool RolledBack);
}
