using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Host;
using Lane.Host.Configuration;
using Lane.Host.Lifecycle;
using Lane.Host.Logging;
using Lane.Host.Migration;
using Lane.Host.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Secrets come from .env in development. Loaded before configuration binds so that
// "env:NAME" references resolve.
DotNetEnv.Env.TraversePath().Load();

// `say` speaks one line and gets out, building no host at all: hearing what a tempo change
// did should not need a model key, a Discord token or a database.
if (args.FirstOrDefault() == "say")
{
    Environment.ExitCode = await SayCommand.RunAsync(args, CancellationToken.None);
    return;
}

bool migrating = args.Contains("migrate");

BufferedLogSink logs = new();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddLaneSources()
    .AddCommandLine(args);

builder.Logging.ClearProviders();

// Every log line goes to the buffer that feeds the web dashboard's tail pane — it costs
// nothing and means the page opens with history.
builder.Logging.AddProvider(new BufferedLoggerProvider(logs));

// The terminal is just stdin and stdout now; logging goes to stderr so a piped
// conversation on stdout stays clean.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

RestartStartup? restart = null;

if (!migrating)
{
    RestartOptions restartOptions = new();
    builder.Configuration.GetSection(RestartOptions.SectionName).Bind(restartOptions);

    restart = SelfUpdater.CheckOnStartup(AppContext.BaseDirectory, restartOptions, Console.Error);

    // The previous build has just been put back, and this process is still the failed one.
    if (restart.ExitNow)
    {
        Environment.ExitCode = restartOptions.ExitCode;
        return;
    }
}

builder.AddLane(logs, runDashboard: !migrating, restart);

builder.Services.AddSingleton<V2Migrator>();

IHost host = builder.Build();

if (migrating)
{
    await RunMigrationAsync(host, args);
    return;
}

host.ValidateLane();

await host.RunAsync();

// ---------------------------------------------------------------------------

static async Task RunMigrationAsync(IHost host, string[] args)
{
    string? data = Argument(args, "--data") ?? DefaultDataPath();

    if (data is null || !File.Exists(data))
    {
        Console.Error.WriteLine(
            "Could not find v2's data.json. Pass --data <path>.\n" +
            "Usage: lane migrate --data <path> [--session <id>] [--dry-run]");

        Environment.ExitCode = 1;
        return;
    }

    string sessionText = Argument(args, "--session") ?? "terminal/Text/local";

    if (!SessionId.TryParse(sessionText, out SessionId? session))
    {
        Console.Error.WriteLine($"'{sessionText}' is not a session id, e.g. discord.main/Text/123456.");

        Environment.ExitCode = 1;
        return;
    }

    bool dryRun = args.Contains("--dry-run");

    V2Migrator migrator = host.Services.GetRequiredService<V2Migrator>();

    try
    {
        MigrationReport report = await migrator.MigrateAsync(data, session, dryRun, CancellationToken.None);

        Console.WriteLine($"{(dryRun ? "Would migrate" : "Migrated")} into {session}: {report}");

        if (dryRun) Console.WriteLine("Nothing was written. Run again without --dry-run to commit.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Migration failed: {ex.Message}");

        Environment.ExitCode = 1;
    }
}

static string? Argument(string[] args, string name)
{
    int index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>Where v2 wrote its data, if the legacy project is still beside us.</summary>
static string? DefaultDataPath()
{
    string candidate = Path.Combine(AppContext.BaseDirectory, "data.json");

    return File.Exists(candidate) ? candidate : null;
}
