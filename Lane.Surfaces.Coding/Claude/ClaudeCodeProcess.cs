using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Coding.Claude;

public sealed record ClaudeLaunchSettings
{
    public required string ClaudePath       { get; init; }
    public required string WorkingDirectory { get; init; }

    public string? Model { get; init; }
    public IReadOnlyList<string> AutoAllow  { get; init; } = [];
    public IReadOnlyList<string> AlwaysDeny { get; init; } = [];
    public decimal MaxBudgetUsd { get; init; }
    public string? AppendSystemPrompt { get; init; }

    /// <summary>The <c>--mcp-config</c> JSON for the permission server; null runs without one.</summary>
    public Func<string>? McpConfig { get; init; }

    public string PermissionTool { get; init; } = "mcp__lane__permission_prompt";
    public TimeSpan PermissionTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public IReadOnlyList<string> PassEnvironment { get; init; } = [];
}

/// <summary>
/// A headless <c>claude -p</c> speaking stream-json, started on the first message and
/// resumed by session id whenever it has to be started again.
/// </summary>
public sealed class ClaudeCodeProcess(
    ClaudeLaunchSettings settings,
    IClaudeSessionStore  store,
    ILogger              log) : IClaudeCodeTransport
{
    private static readonly string[] SecretMarkers = ["KEY", "TOKEN", "SECRET", "PASSWORD"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private readonly List<string> _activity = [];
    private readonly List<string> _unanswered = [];
    private readonly Queue<string> _stderr = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _control = new();

    private Process?      _process;
    private StreamWriter? _stdin;
    private int           _generation;
    private bool          _sawInit;
    private bool          _resuming;
    private string?       _sessionId;
    private bool          _sessionLoaded;
    private decimal       _cost;
    private volatile bool _busy;

    public bool Busy => _busy;

    public bool Running => _process is { HasExited: false };

    public string? SessionId => _sessionId;

    public decimal TotalCostUsd => _cost;

    public event Action<ClaudeTurn>? TurnCompleted;

    public async Task SendAsync(string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await EnsureStartedAsync(ct).ConfigureAwait(false);

            lock (_state) _unanswered.Add(text);

            await WriteAsync(StreamJson.UserMessage(text), ct).ConfigureAwait(false);

            _busy = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> InterruptAsync(CancellationToken ct)
    {
        if (!_busy || !Running) return false;

        string id = $"interrupt-{Guid.NewGuid():n}";
        TaskCompletionSource<bool> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _control[id] = answered;

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { await WriteAsync(StreamJson.Interrupt(id), ct).ConfigureAwait(false); }
            finally { _gate.Release(); }

            return await answered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally { _control.TryRemove(id, out _); }
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await StopProcessAsync().ConfigureAwait(false);

            _sessionId     = null;
            _sessionLoaded = true;
            _cost          = 0;
            _busy          = false;

            lock (_state) { _unanswered.Clear(); _activity.Clear(); }

            await store.SaveAsync(null, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(string line, CancellationToken ct)
    {
        StreamWriter stdin = _stdin ?? throw new InvalidOperationException("Claude Code is not running.");

        await stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await stdin.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Caller holds the gate.</summary>
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (Running) return;

        if (!_sessionLoaded)
        {
            _sessionId     = await store.LoadAsync(ct).ConfigureAwait(false);
            _sessionLoaded = true;
        }

        Start(resume: _sessionId);
    }

    private void Start(string? resume)
    {
        ProcessStartInfo psi = new(settings.ClaudePath)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = settings.WorkingDirectory
        };

        if (resume is null) _sessionId = Guid.NewGuid().ToString();

        foreach (string arg in Arguments(resume, _sessionId!)) psi.ArgumentList.Add(arg);

        ScrubEnvironment(psi.Environment);

        // How long an MCP call, including a permission prompt, may wait.
        psi.Environment["MCP_TOOL_TIMEOUT"] =
            ((long)(settings.PermissionTimeout + TimeSpan.FromMinutes(1)).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        Process process = new() { StartInfo = psi, EnableRaisingEvents = true };

        process.Start();

        int generation = Interlocked.Increment(ref _generation);

        _process  = process;
        _stdin    = process.StandardInput;
        _sawInit  = false;
        _resuming = resume is not null;

        lock (_state) _stderr.Clear();

        _ = Task.Run(() => ReadStdoutAsync(process, generation));
        _ = Task.Run(() => ReadStderrAsync(process));

        log.LogInformation("Started Claude Code in {Directory} ({Mode})",
            settings.WorkingDirectory, resume is null ? $"new session {_sessionId}" : $"resuming {resume}");
    }

    /// <param name="sessionId">Used for a new conversation, when <paramref name="resume"/> is null.</param>
    internal IReadOnlyList<string> Arguments(string? resume, string sessionId)
    {
        List<string> args =
        [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", "default"
        ];

        args.AddRange(resume is not null ? ["--resume", resume] : ["--session-id", sessionId]);

        if (settings.AutoAllow.Count > 0)  args.AddRange(["--allowedTools", string.Join(',', settings.AutoAllow)]);
        if (settings.AlwaysDeny.Count > 0) args.AddRange(["--disallowedTools", string.Join(',', settings.AlwaysDeny)]);

        if (settings.McpConfig is not null)
        {
            args.AddRange(["--mcp-config", settings.McpConfig(), "--strict-mcp-config"]);
            args.AddRange(["--permission-prompt-tool", settings.PermissionTool]);
        }

        if (!string.IsNullOrWhiteSpace(settings.AppendSystemPrompt))
            args.AddRange(["--append-system-prompt", settings.AppendSystemPrompt]);

        if (settings.MaxBudgetUsd > 0)
            args.AddRange(["--max-budget-usd", settings.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture)]);

        if (!string.IsNullOrWhiteSpace(settings.Model)) args.AddRange(["--model", settings.Model]);

        return args;
    }

    private void ScrubEnvironment(IDictionary<string, string?> environment)
    {
        foreach (string name in environment.Keys.ToList())
        {
            if (settings.PassEnvironment.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            if (SecretMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase)))
                environment.Remove(name);
        }
    }

    private async Task ReadStdoutAsync(Process process, int generation)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (generation != Volatile.Read(ref _generation)) return;

                foreach (ClaudeEvent evt in StreamJson.Parse(line, settings.WorkingDirectory))
                    await HandleAsync(evt).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reading Claude Code's output failed");
        }

        if (generation != Volatile.Read(ref _generation)) return;

        await OnExitedAsync(process).ConfigureAwait(false);
    }

    private async Task ReadStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                log.LogDebug("[claude stderr] {Line}", line);

                lock (_state)
                {
                    _stderr.Enqueue(line);
                    while (_stderr.Count > 20) _stderr.Dequeue();
                }
            }
        }
        catch (Exception) { /* the process went away */ }
    }

    private async Task HandleAsync(ClaudeEvent evt)
    {
        switch (evt)
        {
            case ClaudeEvent.Init init:
                _sawInit = true;

                if (_sessionId != init.SessionId) _sessionId = init.SessionId;

                await store.SaveAsync(init.SessionId, CancellationToken.None).ConfigureAwait(false);
                break;

            case ClaudeEvent.ToolUse use:
                lock (_state) _activity.Add(use.Summary);
                break;

            case ClaudeEvent.ControlResponse response:
                if (_control.TryGetValue(response.RequestId, out TaskCompletionSource<bool>? waiter))
                    waiter.TrySetResult(response.Success);
                break;

            case ClaudeEvent.Result result:
                _cost = result.TotalCostUsd;
                _busy = result.QueuedTurns > 0;

                Complete(new ClaudeTurn(
                    result.Text?.Trim() ?? "",
                    result.IsError,
                    Outcome(result),
                    result.TotalCostUsd,
                    TakeActivity()), answered: !_busy);
                break;
        }
    }

    private static string Outcome(ClaudeEvent.Result result) => result switch
    {
        { IsError: false }                                   => "completed",
        { TerminalReason: { } reason } when reason.StartsWith("aborted", StringComparison.Ordinal) => "interrupted",
        _                                                    => result.Subtype
    };

    private async Task OnExitedAsync(Process process)
    {
        int? code = null;
        try { await process.WaitForExitAsync().ConfigureAwait(false); code = process.ExitCode; }
        catch (Exception) { /* already gone */ }

        string[] unanswered;
        lock (_state) unanswered = [.. _unanswered];

        // A resume that fails before init starts a new conversation and resends what was unanswered.
        if (_resuming && !_sawInit)
        {
            log.LogWarning("Claude Code could not resume session {Session}; starting a new one", _sessionId);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _sessionId = null;
                await store.SaveAsync(null, CancellationToken.None).ConfigureAwait(false);

                if (unanswered.Length > 0)
                {
                    Start(resume: null);
                    foreach (string text in unanswered) await WriteAsync(StreamJson.UserMessage(text), CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }
            finally { _gate.Release(); }
        }

        _process = null;
        _stdin   = null;

        log.LogInformation("Claude Code exited with code {Code}", code);

        if (!_busy) return;

        _busy = false;

        string tail;
        lock (_state) tail = string.Join('\n', _stderr);

        Complete(new ClaudeTurn(
            $"Claude Code exited (code {code}) before finishing." + (tail.Length > 0 ? $"\n{tail}" : ""),
            IsError: true, Outcome: "exited", _cost, TakeActivity()), answered: true);
    }

    private IReadOnlyList<string> TakeActivity()
    {
        lock (_state)
        {
            string[] taken = [.. _activity];
            _activity.Clear();
            return taken;
        }
    }

    private void Complete(ClaudeTurn turn, bool answered)
    {
        if (answered) lock (_state) _unanswered.Clear();

        try { TurnCompleted?.Invoke(turn); }
        catch (Exception ex) { log.LogError(ex, "A Claude turn subscriber threw"); }
    }

    private async Task StopProcessAsync()
    {
        Process? process = _process;

        Interlocked.Increment(ref _generation);

        _process = null;

        if (_stdin is { } stdin)
        {
            try { stdin.Close(); } catch (Exception) { /* already closed */ }
        }

        _stdin = null;

        if (process is null) return;

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try { await StopProcessAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }

        _gate.Dispose();
    }
}
