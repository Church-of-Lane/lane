using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Lifecycle;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Lane.Surfaces.Coding.Claude;
using Lane.Surfaces.Coding.Permissions;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Coding;

/// <summary>
/// A conversation between Lane and Claude Code. What Lane says here is sent to Claude Code, and
/// each finished Claude Code turn comes back as a message from "Claude Code".
/// </summary>
public sealed class CodingSurface : ISurface
{
    public const string WorkspaceTag = "workspace";
    public const string SelfTag      = "self";

    private const int MaxActivity = 15;

    private readonly CodingWorkspace      _workspace;
    private readonly CodingOptions        _options;
    private readonly IAgentKernel         _kernel;
    private readonly ISessionRegistry     _sessions;
    private readonly ISessionDescriptions _descriptions;
    private readonly IEventBus            _bus;
    private readonly PermissionBroker     _broker;
    private readonly IRestartCoordinator? _restart;
    private readonly ILogger              _log;
    private readonly TimeProvider         _time;

    private readonly Lock _guard = new();
    private int         _unattended;
    private ClaudeTurn? _held;

    private IDisposable?   _attachment;
    private ClaudeChannel? _channel;

    public CodingSurface(
        CodingWorkspace      workspace,
        CodingOptions        options,
        IAgentKernel         kernel,
        ISessionRegistry     sessions,
        ISessionDescriptions descriptions,
        IEventBus            bus,
        PermissionBroker     broker,
        IRestartCoordinator? restart,
        ILogger              log,
        TimeProvider?        time = null)
    {
        _workspace    = workspace;
        _options      = options;
        _kernel       = kernel;
        _sessions     = sessions;
        _descriptions = descriptions;
        _bus          = bus;
        _broker       = broker;
        _restart      = restart;
        _log          = log;
        _time         = time ?? TimeProvider.System;
    }

    public SurfaceId Id => _workspace.SurfaceId;

    public Participant Claude => new(new ParticipantId(Id, "claude"), "Claude Code");

    /// <summary>Claude Code has replied too many times in a row with nobody else speaking, and its last reply is held.</summary>
    public bool Paused { get { lock (_guard) return _held is not null; } }

    public SessionDescriptor Descriptor => new()
    {
        Id                = _workspace.SessionId,
        DisplayName       = $"coding: {_workspace.Name}",
        MemoryGroup       = $"coding/{_workspace.Id}",
        Capabilities      = ChannelCapabilities.Text,
        IsDirect          = true,
        KnownParticipants = [Claude],
        Tags              = _workspace.SelfHosted
            ? ImmutableDictionary<string, string>.Empty.Add(WorkspaceTag, _workspace.Id).Add(SelfTag, "true")
            : ImmutableDictionary<string, string>.Empty.Add(WorkspaceTag, _workspace.Id)
    };

    public async Task StartAsync(CancellationToken ct)
    {
        SessionDescriptor descriptor = Descriptor;

        _sessions.GetOrCreate(descriptor);

        _channel    = new ClaudeChannel(descriptor.Id, _bus, OnLaneMessageAsync, _log);
        _attachment = _sessions.Attach(_channel);

        _workspace.Claude.TurnCompleted += OnClaudeTurn;
        _broker.Requested += OnPermissionRequested;
        _broker.Resolved  += OnPermissionResolved;

        if (_descriptions.Durable && _descriptions.For(descriptor) is null)
            await _descriptions.SetAsync(descriptor.MemoryGroup, Describe(), ct).ConfigureAwait(false);

        if (_workspace.SelfHosted && _restart?.PreviousRestart is { } previous)
            AnnounceRestart(previous);

        _log.LogInformation("Coding surface {Surface} ready in {Path}", Id, _workspace.Path);
    }

    /// <summary>Someone other than Claude Code spoke here: the unattended count starts again, and a held reply is released.</summary>
    public void Resume()
    {
        ClaudeTurn? held;

        lock (_guard)
        {
            _unattended = 0;
            held        = _held;
            _held       = null;
        }

        if (held is not null) Submit(Format(held));
    }

    private async Task OnLaneMessageAsync(string text, TurnKind kind)
    {
        // Directive turns are the monologue speaking in.
        if (kind == TurnKind.Directive) Resume();

        try
        {
            await _workspace.Claude.SendAsync(text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not start Claude Code in {Surface}", Id);

            Submit($"[Claude Code could not be started: {ex.Message}]", requiresResponse: false);
        }
    }

    private void OnClaudeTurn(ClaudeTurn turn)
    {
        bool submit = false, justPaused = false;
        int count;

        lock (_guard)
        {
            if (_options.MaxUnattendedExchanges > 0 && _unattended >= _options.MaxUnattendedExchanges)
            {
                justPaused = _held is null;
                _held      = _held is null ? turn : Merge(_held, turn);
            }
            else
            {
                _unattended++;
                submit = true;
            }

            count = _unattended;
        }

        if (submit)
        {
            Submit(Format(turn));
        }
        else if (justPaused)
        {
            _log.LogWarning("Coding surface {Surface} paused after {Count} unattended exchanges", Id, count);

            Submit($"[Paused: Claude Code has replied {count} times in a row with nobody else here. " +
                   "Its latest reply is held until someone speaks in this conversation.]", requiresResponse: false);
        }
    }

    private static ClaudeTurn Merge(ClaudeTurn first, ClaudeTurn second) => second with
    {
        Text     = $"{first.Text}\n\n{second.Text}".Trim(),
        Activity = [.. first.Activity, .. second.Activity]
    };

    private void OnPermissionRequested(PermissionRequest request)
    {
        if (request.WorkspaceId != _workspace.Id) return;

        Submit($"[Permission request {request.Id}] Claude Code wants to use {request.ToolName}: {Preview(request)}\n" +
               "Answer with answer_claude_permission.");
    }

    /// <summary>Records the outcome in the conversation.</summary>
    private void OnPermissionResolved(PermissionRequest request, PermissionVerdict verdict)
    {
        if (request.WorkspaceId != _workspace.Id) return;

        string outcome = verdict.Allow ? "allowed" : $"denied ({verdict.Message ?? "no reason given"})";

        Submit($"[Permission request {request.Id}: {outcome}.]", requiresResponse: false);
    }

    private static string Preview(PermissionRequest request)
    {
        JsonElement input = request.Input;

        if (request.ToolName == "Bash" && input.ValueKind == JsonValueKind.Object &&
            input.TryGetProperty("command", out JsonElement command) && command.ValueKind == JsonValueKind.String)
        {
            string description = input.TryGetProperty("description", out JsonElement d) && d.ValueKind == JsonValueKind.String
                ? $" ({d.GetString()})"
                : "";

            return $"`{command.GetString()}`{description}";
        }

        string raw = input.ValueKind == JsonValueKind.Undefined ? "{}" : input.GetRawText();

        return raw.Length <= 1500 ? raw : raw[..1500] + "…";
    }

    internal static string Format(ClaudeTurn turn)
    {
        StringBuilder sb = new();

        switch (turn.Outcome)
        {
            case "completed":
                sb.Append(turn.Text.Length > 0 ? turn.Text : "(Claude Code finished without saying anything.)");
                break;

            case "interrupted":
                sb.Append("[Claude Code was interrupted.]");
                break;

            default:
                sb.Append("[Claude Code stopped: ").Append(turn.Outcome).Append(".]");
                if (turn.Text.Length > 0) sb.Append(' ').Append(turn.Text);
                break;
        }

        List<string> footer = [];

        if (turn.Activity.Count > 0)
        {
            string shown = string.Join("; ", turn.Activity.Take(MaxActivity));

            footer.Add(turn.Activity.Count > MaxActivity
                ? $"Activity: {shown}; and {turn.Activity.Count - MaxActivity} more"
                : $"Activity: {shown}");
        }

        if (turn.TotalCostUsd > 0)
            footer.Add(string.Create(CultureInfo.InvariantCulture, $"${turn.TotalCostUsd:0.00} so far"));

        if (footer.Count > 0) sb.Append("\n\n[").Append(string.Join(" · ", footer)).Append(']');

        return sb.ToString();
    }

    private void Submit(string text, bool requiresResponse = true)
    {
        Participant author = Claude;

        InboundEvent evt = new()
        {
            Session          = _workspace.SessionId,
            Author           = author,
            Message          = LaneMessage.User(_workspace.SessionId, author, text, _time.GetUtcNow()),
            RequiresResponse = requiresResponse
        };

        _ = SubmitAsync(evt);
    }

    private async Task SubmitAsync(InboundEvent evt)
    {
        try { await _kernel.SubmitAsync(evt).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Could not hand Claude Code's message to Lane in {Surface}", Id); }
    }

    private void AnnounceRestart(RestartRecord previous)
    {
        string commit = previous.Commit is null ? "" : $" ({previous.Commit})";

        string text = previous.RolledBack
            ? $"[Your restart onto the new build{commit} failed to start, and the previous build was put back. " +
              "The change that caused it is still in the source.]"
            : $"[You restarted onto the new build{commit}. Reason given: {previous.Reason}]";

        Submit(text, requiresResponse: previous.RolledBack);
    }

    /// <summary>The standing explanation of this conversation, written into Lane's prompt.</summary>
    private string Describe()
    {
        StringBuilder sb = new();

        sb.Append("A coding session. You are talking with Claude Code, an AI coding agent working in ")
          .Append(_workspace.Path);

        if (_workspace.SelfHosted) sb.Append(", which is your own source code");

        sb.Append('.');

        if (!string.IsNullOrWhiteSpace(_workspace.Purpose)) sb.Append(" Purpose: ").Append(_workspace.Purpose.Trim().TrimEnd('.')).Append('.');

        sb.Append(" What you say here is sent to Claude Code as instructions, and its replies come back from \"Claude Code\".")
          .Append(" When you have nothing more to ask of it, call stop_responding rather than replying.")
          .Append(" Answer its permission requests with answer_claude_permission.")
          .Append(" You can also use git, github_issues, claude_status, claude_interrupt and claude_new_session.");

        if (_workspace.SelfHosted)
            sb.Append(" restart_self rebuilds you from this source and restarts you, but only if the build succeeds.");

        return sb.ToString();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _workspace.Claude.TurnCompleted -= OnClaudeTurn;
        _broker.Requested -= OnPermissionRequested;
        _broker.Resolved  -= OnPermissionResolved;
        _broker.DenyAll(_workspace.Id, "The coding session closed.");

        await _sessions.CloseAsync(_workspace.SessionId, "surface stopped").ConfigureAwait(false);

        _attachment?.Dispose();
        _attachment = null;

        if (_channel is not null) await _channel.DisposeAsync().ConfigureAwait(false);
        _channel = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
