using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Lifecycle;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Monologue;
using Lane.Core.Sessions;
using Lane.Host.Logging;
using Lane.Nodes.Portal;
using Lane.Surfaces.Coding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Host.Web;

public sealed class DashboardOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary><c>HttpListener</c> host: <c>localhost</c> for loopback only, <c>+</c> for every interface.</summary>
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5090;

    /// <summary>
    /// Key id of the security-key node identity allowed to use the dashboard from anywhere but loopback,
    /// signed in through the node portal. Null refuses every non-loopback request.
    /// </summary>
    public string? OwnerKeyId { get; set; }

    /// <summary>The file the config editor reads and writes. Defaults beside the executable.</summary>
    public string ConfigPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>
    /// The name messages posted from the dashboard carry. They come from the account
    /// <c>dashboard:owner</c>, which the identity map can link to a person.
    /// </summary>
    public string OwnerName { get; set; } = "owner";
}

/// <summary>
/// A dashboard served over HTTP, replacing the old Terminal.Gui pane layout.
///
/// It never touches the process's console — the terminal surface goes back to plain
/// <c>Console.In</c>/<c>Console.Out</c> now that nothing else claims the screen. The browser
/// polls <c>/api/snapshot</c> rather than holding a streaming connection open, which keeps
/// this server as simple as <see cref="Lane.Host.Presence.FaceServer"/> beside it. Loopback
/// requests are unauthenticated; any other request to <c>/api/</c> needs a node portal sign-in
/// token belonging to <see cref="DashboardOptions.OwnerKeyId"/>.
/// </summary>
public sealed class WebDashboardServer(
    IEventBus bus,
    ISessionRegistry sessions,
    ILanguageModelRegistry models,
    BufferedLogSink logs,
    IMonologueScheduler monologue,
    IEnergyService energy,
    IOptions<DashboardOptions> options,
    IRestartCoordinator restart,
    IAgentKernel kernel,
    ITranscriptStore transcript,
    IIdentityResolver identity,
    ILogger<WebDashboardServer> log,
    NodePortal? portal = null,
    ICodingWorkspaces? coding = null) : BackgroundService
{
    private readonly DashboardOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, string> _sessionNotes = new();
    private readonly ConcurrentDictionary<string, ModelUsage> _usage = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static readonly SurfaceId Surface = new("dashboard");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        foreach (ILanguageModel model in models.All)
            _usage[model.Descriptor.InstanceId] = new ModelUsage(model.Descriptor.InstanceId, RolesFor(model.Descriptor.InstanceId));

        using IDisposable s1 = bus.Subscribe<TokenUsageEvent>(RecordUsage);
        using IDisposable s2 = bus.Subscribe<TurnStarted>(evt => _sessionNotes[evt.Session.Value] = "running");
        using IDisposable s3 = bus.Subscribe<TurnCompleted>(evt => _sessionNotes[evt.Session.Value] = "idle");
        using IDisposable s4 = bus.Subscribe<TurnFailed>(evt => _sessionNotes[evt.Session.Value] = "failed");
        using IDisposable s5 = bus.Subscribe<ToolInvokedEvent>(evt =>
        {
            if (evt.Session is not null) _sessionNotes[evt.Session] = $"tool: {evt.Tool}";
        });

        using HttpListener listener = new();
        listener.Prefixes.Add($"http://{_options.Host}:{_options.Port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Dashboard could not listen on port {Port}", _options.Port);
            return;
        }

        log.LogInformation("Dashboard at http://{Host}:{Port}/", _options.Host, _options.Port);

        using CancellationTokenRegistration stop = stoppingToken.Register(listener.Close);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Dashboard dropped a connection");
                continue;
            }

            _ = HandleAsync(context, stoppingToken);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            string path   = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            if (path.StartsWith("/api/", StringComparison.Ordinal) && Unauthorised(context.Request) is { } refusal)
            {
                context.Response.StatusCode = 401;
                await WriteJsonAsync(context, new { error = refusal }, ct).ConfigureAwait(false);
                context.Response.Close();
                return;
            }

            switch (path)
            {
                case "/api/snapshot" when method == "GET":
                    await WriteJsonAsync(context, BuildSnapshot(), ct).ConfigureAwait(false);
                    break;

                case "/api/config" when method == "GET":
                    await GetConfigAsync(context, ct).ConfigureAwait(false);
                    break;

                case "/api/config" when method == "POST":
                    await SaveConfigAsync(context, ct).ConfigureAwait(false);
                    break;

                case "/api/restart" when method == "POST":
                    await WriteJsonAsync(context, new { restarting = true }, ct).ConfigureAwait(false);
                    restart.Restart("requested from the dashboard");
                    break;

                case "/api/session/messages" when method == "GET":
                    await GetMessagesAsync(context, ct).ConfigureAwait(false);
                    break;

                case "/api/session/messages" when method == "POST":
                    await PostMessageAsync(context, ct).ConfigureAwait(false);
                    break;

                case "/config":
                    await WriteHtmlAsync(context, ConfigPage.Html, ct).ConfigureAwait(false);
                    break;

                case "/auth.js":
                    await WriteBytesAsync(context, "text/javascript; charset=utf-8", DashboardAuth.Script, ct).ConfigureAwait(false);
                    break;

                default:
                    await WriteHtmlAsync(context, DashboardPage.Html, ct).ConfigureAwait(false);
                    break;
            }

            context.Response.Close();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Dashboard failed to serve a request");
        }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext context, T value, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = json.Length;

        await context.Response.OutputStream.WriteAsync(json, ct).ConfigureAwait(false);
    }

    private static Task WriteHtmlAsync(HttpListenerContext context, string html, CancellationToken ct) =>
        WriteBytesAsync(context, "text/html; charset=utf-8", html, ct);

    private static async Task WriteBytesAsync(HttpListenerContext context, string contentType, string text, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);

        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>Why the request may not use the API, or null if it may.</summary>
    private string? Unauthorised(HttpListenerRequest request)
    {
        if (IPAddress.IsLoopback(request.RemoteEndPoint.Address)) return null;

        if (portal is null)
            return "Signing in from another network needs the node listener (Lane:Nodes:Enabled).";

        if (string.IsNullOrWhiteSpace(_options.OwnerKeyId))
            return "No security key owns this dashboard (Lane:Dashboard:OwnerKeyId).";

        string header = request.Headers["Authorization"] ?? "";
        string? token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;

        return portal.SignedInKeyId(token) switch
        {
            null => "Sign in with the dashboard owner's security key.",
            { } keyId when string.Equals(keyId, _options.OwnerKeyId.Trim(), StringComparison.OrdinalIgnoreCase) => null,
            _ => "That security key does not own this dashboard."
        };
    }

    private async Task GetConfigAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!File.Exists(_options.ConfigPath))
        {
            context.Response.StatusCode = 404;
            await WriteJsonAsync(context, new { error = $"'{_options.ConfigPath}' does not exist." }, ct)
                .ConfigureAwait(false);
            return;
        }

        string content = await File.ReadAllTextAsync(_options.ConfigPath, ct).ConfigureAwait(false);

        await WriteJsonAsync(context, new { path = _options.ConfigPath, content }, ct).ConfigureAwait(false);
    }

    private async Task SaveConfigAsync(HttpListenerContext context, CancellationToken ct)
    {
        using StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding);
        string body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        // Caught rather than left to throw: a bad edit should tell the person in the
        // browser what is wrong, not take the dashboard's request thread down with it.
        try
        {
            using JsonDocument _ = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { error = $"Not valid JSON: {ex.Message}" }, ct).ConfigureAwait(false);
            return;
        }

        await File.WriteAllTextAsync(_options.ConfigPath, body, ct).ConfigureAwait(false);

        log.LogInformation("Configuration saved to {Path} from the dashboard", _options.ConfigPath);

        await WriteJsonAsync(context, new { saved = true }, ct).ConfigureAwait(false);
    }

    private bool TryReadSession(HttpListenerContext context, out SessionId? id) =>
        SessionId.TryParse(context.Request.QueryString["id"], out id) && sessions.TryGet(id, out _);

    private async Task GetMessagesAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!TryReadSession(context, out SessionId? id))
        {
            context.Response.StatusCode = 404;
            await WriteJsonAsync(context, new { error = "No such open session." }, ct).ConfigureAwait(false);
            return;
        }

        int limit = int.TryParse(context.Request.QueryString["limit"], out int n) ? Math.Clamp(n, 1, 500) : 100;

        IReadOnlyList<LaneMessage> messages = await transcript
            .ReadAsync(new TranscriptQuery { Session = id, Limit = limit, Descending = true }, ct)
            .ConfigureAwait(false);

        MessageRow[] rows = [.. messages
            .Where(m => m.Role != LaneRole.Tool && m.TextContent.Length > 0)
            .OrderBy(m => m.Sequence)
            .Select(m => new MessageRow(m.Sequence, m.Author.DisplayName, m.Author.IsLane, m.Kind.ToString(), m.TextContent, m.Timestamp))];

        await WriteJsonAsync(context, rows, ct).ConfigureAwait(false);
    }

    private async Task PostMessageAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!TryReadSession(context, out SessionId? id))
        {
            context.Response.StatusCode = 404;
            await WriteJsonAsync(context, new { error = "No such open session." }, ct).ConfigureAwait(false);
            return;
        }

        string? text;

        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: ct).ConfigureAwait(false);
            text = body.RootElement.TryGetProperty("text", out JsonElement t) ? t.GetString()?.Trim() : null;
        }
        catch (JsonException)
        {
            text = null;
        }

        if (string.IsNullOrEmpty(text))
        {
            context.Response.StatusCode = 400;
            await WriteJsonAsync(context, new { error = "Send {\"text\": \"…\"}." }, ct).ConfigureAwait(false);
            return;
        }

        Participant author = identity.Resolve(new ParticipantId(Surface, "owner"), _options.OwnerName);

        coding?.NoteHumanActivity(id!);

        await kernel.SubmitAsync(new InboundEvent
        {
            Session = id!,
            Author  = author,
            Message = LaneMessage.User(id!, author, text, DateTimeOffset.UtcNow)
        }, ct).ConfigureAwait(false);

        await WriteJsonAsync(context, new { sent = true }, ct).ConfigureAwait(false);
    }

    private void RecordUsage(TokenUsageEvent evt)
    {
        ModelUsage usage = _usage.GetOrAdd(evt.ModelInstanceId, id => new ModelUsage(id, RolesFor(id)));

        usage.Record(evt.Usage);
    }

    private string RolesFor(string instance) => string.Join(",", models.RoleBindings
        .Where(kv => string.Equals(kv.Value, instance, StringComparison.OrdinalIgnoreCase))
        .Select(kv => kv.Key));

    private DashboardSnapshot BuildSnapshot()
    {
        Session[] active = [.. sessions.Active.OrderBy(s => s.Id.Value, StringComparer.Ordinal)];

        SessionRow[] sessionRows = [.. active.Select(s => new SessionRow(
            s.Id.Value,
            s.Descriptor.DisplayName,
            s.State.ToString(),
            _sessionNotes.GetValueOrDefault(s.Id.Value, "-"),
            s.LastActivity))];

        ModelRow[] modelRows = [.. _usage.Values.Select(u => u.ToRow())];

        EnergyState energyState = energy.Current;

        EnergySnapshot energySnapshot = new(
            energyState.Fraction,
            energyState.Remaining,
            energyState.Budget,
            energyState.Asleep,
            energyState.Asleep ? energy.TimeUntilRested?.TotalSeconds : null,
            energyState.Tier.ToString());

        MonologueStatus status = monologue.Status;

        MonologueSnapshot monologueSnapshot = new(
            status.NextThoughtAt,
            status.LastThought,
            status.ThoughtCount,
            status.Thinking);

        string[] logLines = [.. logs.Snapshot().TakeLast(300).Select(e => e.Short)];

        return new DashboardSnapshot(sessionRows, modelRows, energySnapshot, monologueSnapshot, logLines, BuildCoding());
    }

    private CodingRow[] BuildCoding()
    {
        if (coding is null) return [];

        return [.. coding.All.Select(w => new CodingRow(
            w.SessionId.Value,
            w.Name,
            w.Path,
            w.SelfHosted,
            w.Claude.Busy ? "working" : w.Claude.Running ? "idle" : "not started",
            w.Claude.TotalCostUsd,
            w.Surface?.Paused == true,
            [.. coding.PendingPermissions(w.Id).Select(r => $"{r.Id}: {r.ToolName}")]))];
    }

    private sealed class ModelUsage(string instance, string roles)
    {
        private readonly Lock _gate = new();

        private int _calls;
        private long _input;
        private long _output;
        private long _cacheRead;
        private long _cacheWrite;
        private TimeSpan _lastLatency;

        public void Record(TokenUsage usage)
        {
            lock (_gate)
            {
                _calls++;
                _input       += usage.Input;
                _output      += usage.Output;
                _cacheRead   += usage.CacheRead;
                _cacheWrite  += usage.CacheWrite;
                _lastLatency  = usage.Latency;
            }
        }

        public ModelRow ToRow()
        {
            lock (_gate)
            {
                string cacheRate = _input == 0 ? "-" : $"{100 * _cacheRead / _input}%";

                return new ModelRow(instance, roles, _calls, _input, _output, _cacheRead, cacheRate,
                    _lastLatency == TimeSpan.Zero ? null : (int)_lastLatency.TotalMilliseconds);
            }
        }
    }
}

public sealed record SessionRow(string Id, string Name, string State, string Activity, DateTimeOffset LastActivity);

public sealed record ModelRow(
    string Instance, string Roles, int Calls, long Input, long Output, long CacheRead, string CacheRate, int? LastLatencyMs);

public sealed record EnergySnapshot(
    double Fraction, long Remaining, long Budget, bool Asleep, double? RestedInSeconds, string Tier);

public sealed record MonologueSnapshot(
    DateTimeOffset? NextThoughtAt, string? LastThought, int ThoughtCount, bool Thinking);

public sealed record DashboardSnapshot(
    IReadOnlyList<SessionRow> Sessions,
    IReadOnlyList<ModelRow> Models,
    EnergySnapshot Energy,
    MonologueSnapshot Monologue,
    IReadOnlyList<string> Logs,
    IReadOnlyList<CodingRow> Coding);

public sealed record MessageRow(long Sequence, string Author, bool IsLane, string Kind, string Text, DateTimeOffset Timestamp);

public sealed record CodingRow(
    string Session, string Name, string Path, bool SelfHosted, string Claude, decimal CostUsd, bool Paused,
    IReadOnlyList<string> PendingPermissions);
