using System.Diagnostics;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Agent;

public sealed record AgentRunRequest
{
    public required ILanguageModel             Model    { get; init; }
    public required IReadOnlyList<PromptBlock> System   { get; init; }
    public required IReadOnlyList<LaneMessage> Messages { get; init; }
    public required ToolSet                    Tools    { get; init; }
    public required ToolContext                ToolContext { get; init; }

    /// <summary>Lane, as the author of everything this run produces.</summary>
    public required Participant Lane { get; init; }

    public SessionId? Session { get; init; }

    public AgentBudget     Budget   { get; init; } = AgentBudget.Default;
    public IAgentObserver? Observer { get; init; }

    public int    MaxOutputTokens { get; init; } = 1024;
    public float  Temperature     { get; init; } = 1f;
    public string? CacheLineage   { get; init; }
}

public sealed record ToolCallRecord(string Name, bool IsError, TimeSpan Duration);

public sealed record AgentRunResult(
    IReadOnlyList<LaneMessage> NewMessages,
    IReadOnlyList<LaneMessage> Observations,
    string                     FinalText,
    StopReason                 Stop,
    TokenUsage                 TotalUsage,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    bool                       Cancelled,
    bool                       EndedByTool = false);

/// <summary>
/// Sees the run as it happens.
///
/// This is how speech starts before the reply is finished: text arrives in pieces, and the
/// first clause can be spoken while the rest is still being written.
/// </summary>
public interface IAgentObserver : IAsyncDisposable
{
    /// <summary>A fragment of text, as the model produces it.</summary>
    ValueTask OnTextAsync(string delta, CancellationToken ct);

    ValueTask OnToolStartAsync(string name, CancellationToken ct);

    ValueTask OnToolEndAsync(string name, ToolResult result, CancellationToken ct);

    /// <summary>The run is over. Anything held back should be flushed here.</summary>
    ValueTask OnFinishedAsync(CancellationToken ct);
}

/// <summary>Supplies an observer for a turn, or nothing when the turn needs none.</summary>
public interface IAgentObserverFactory
{
    IAgentObserver? Create(Session session, TurnKind kind);
}

/// <summary>
/// Drives the model until it stops asking for tools.
///
/// This lives in the kernel rather than in a provider adapter because everything it has to
/// coordinate — gating, budgets, memory writes, cancellation, telemetry — is a kernel
/// concern. Adapters stay pure translators, which is what keeps adding a provider cheap.
/// </summary>
public sealed class AgentLoop(IToolRegistry tools, ILogger<AgentLoop> log)
{
    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<LaneMessage> conversation = [.. request.Messages];
        List<LaneMessage> produced     = [];
        List<LaneMessage> observations = [];
        List<ToolCallRecord> calls     = [];

        TokenUsage usage = default;
        StopReason stop  = StopReason.EndTurn;
        bool cancelled   = false;
        bool endedByTool = false;

        // Held between requesting tools and answering them. If anything goes wrong in that
        // window, the finally block below still answers every outstanding call.
        IReadOnlyList<ToolUsePart>? unanswered = null;

        try
        {
            for (int step = 0; step < request.Budget.MaxSteps; step++)
            {
                ModelResponse response = await GenerateAsync(request, conversation, ct).ConfigureAwait(false);

                usage += response.Usage;
                stop   = response.Stop;

                LaneMessage assistant = LaneMessage.Assistant(
                    request.Session, request.Lane, response.Content, DateTimeOffset.UtcNow);

                conversation.Add(assistant);
                produced.Add(assistant);

                if (response.Stop != StopReason.ToolUse) break;

                IReadOnlyList<ToolUsePart> requested = response.ToolCalls;

                if (requested.Count == 0)
                {
                    // The provider said tool_use but sent no calls. Nothing to answer.
                    log.LogWarning("{Model} stopped for tool use but requested none", request.Model.Descriptor);
                    stop = StopReason.EndTurn;
                    break;
                }

                unanswered = requested;

                bool overBudget = calls.Count + requested.Count > request.Budget.MaxToolCalls;

                List<ToolOutcome> outcomes = overBudget
                    ? [.. requested.Select(c => Refused(c, "the tool budget for this turn is exhausted"))]
                    : await InvokeAllAsync(requested, request, ct).ConfigureAwait(false);

                LaneMessage results = LaneMessage.ToolResults(
                    request.Session, request.Lane, [.. outcomes.Select(o => o.Part)], DateTimeOffset.UtcNow);

                conversation.Add(results);
                produced.Add(results);

                unanswered = null;

                foreach (ToolOutcome outcome in outcomes)
                {
                    calls.Add(new ToolCallRecord(outcome.Name, outcome.Part.IsError, outcome.Duration));
                    observations.AddRange(ToMessages(outcome.Observations, request));
                }

                if (overBudget)
                {
                    log.LogWarning("Tool budget exhausted after {Count} call(s)", calls.Count);
                    break;
                }

                if (outcomes.FirstOrDefault(o => o.EndsTurn) is { } ending)
                {
                    log.LogInformation("{Tool} ended the run", ending.Name);
                    endedByTool = true;
                    stop        = StopReason.EndTurn;
                    break;
                }
            }

            if (stop == StopReason.ToolUse)
                log.LogWarning("Agent run hit its step budget of {Steps} with tools still pending",
                    request.Budget.MaxSteps);
        }
        catch (OperationCanceledException)
        {
            // Not rethrown. A cancelled run still has to hand back a paired, persistable
            // history — see the invariant below.
            cancelled = true;
            log.LogInformation("Agent run cancelled after {Steps} message(s)", produced.Count);
        }
        finally
        {
            // The invariant: every tool_use is answered by exactly one tool_result, in call
            // order, no matter how the run ended. An assistant message carrying an
            // unanswered call is not merely incomplete — every later request in that
            // session is rejected because of it, permanently.
            if (unanswered is { Count: > 0 })
            {
                produced.Add(LaneMessage.ToolResults(
                    request.Session, request.Lane,
                    [.. unanswered.Select(c => ToolResultPart.Text(
                        c.ToolCallId, "The turn ended before this tool finished.", isError: true))],
                    DateTimeOffset.UtcNow));

                log.LogWarning("Synthesised results for {Count} unanswered tool call(s)", unanswered.Count);
            }
        }

        if (request.Observer is not null)
        {
            try { await request.Observer.OnFinishedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "An agent observer threw on finish"); }
        }

        return new AgentRunResult(produced, observations, SpokenText(produced), stop, usage, calls, cancelled, endedByTool);
    }

    /// <summary>
    /// Streams when somebody is listening, and asks for the whole thing when nobody is.
    ///
    /// Streaming costs nothing extra, but it only earns its keep when there is an observer
    /// to hand fragments to — a text-only reply is delivered in one piece regardless.
    /// </summary>
    private async Task<ModelResponse> GenerateAsync(
        AgentRunRequest request, List<LaneMessage> conversation, CancellationToken ct)
    {
        ModelRequest built = BuildRequest(request, conversation);

        if (request.Observer is null || !request.Model.Descriptor.Supports(ModelCapabilities.Streaming))
        {
            ModelResponse whole = await request.Model.CompleteAsync(built, ct).ConfigureAwait(false);

            if (whole.Text.Length > 0 && request.Observer is not null)
                await request.Observer.OnTextAsync(whole.Text, ct).ConfigureAwait(false);

            return whole;
        }

        ModelResponse? completed = null;

        await foreach (ModelStreamEvent evt in request.Model.StreamAsync(built, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case ModelStreamEvent.TextDelta delta:
                    try
                    {
                        await request.Observer.OnTextAsync(delta.Text, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A failing observer must not cost the reply itself.
                        log.LogWarning(ex, "An agent observer threw handling streamed text");
                    }
                    break;

                case ModelStreamEvent.Completed done:
                    completed = done.Response;
                    break;
            }
        }

        return completed ?? new ModelResponse([], StopReason.Other, default);
    }

    /// <summary>
    /// Everything the model actually said across the run, not just its last turn.
    ///
    /// Taking only the final message loses real replies: a model that answers and calls a
    /// tool in the same turn, then returns nothing after the result, has said something
    /// worth delivering — and the caller would otherwise see silence.
    /// </summary>
    private static string SpokenText(IReadOnlyList<LaneMessage> produced)
    {
        List<string> said = [];

        foreach (LaneMessage message in produced)
        {
            if (message.Role != LaneRole.Assistant) continue;

            string text = message.TextContent.Trim();

            if (text.Length > 0) said.Add(text);
        }

        return string.Join("\n\n", said);
    }

    private static ModelRequest BuildRequest(AgentRunRequest request, List<LaneMessage> conversation) => new()
    {
        System          = request.System,
        Messages        = conversation,
        Tools           = request.Tools.Descriptors,
        MaxOutputTokens = request.MaxOutputTokens,
        Temperature     = request.Temperature,
        CacheLineage    = $"{request.CacheLineage}|tools:{request.Tools.Fingerprint}"
    };

    /// <summary>
    /// Runs every requested tool concurrently. Never throws and never returns fewer
    /// results than it was given calls.
    /// </summary>
    private async Task<List<ToolOutcome>> InvokeAllAsync(
        IReadOnlyList<ToolUsePart> requested, AgentRunRequest request, CancellationToken ct)
    {
        Task<ToolOutcome>[] running = [.. requested.Select(call => InvokeOneAsync(call, request, ct))];

        ToolOutcome[] outcomes = await Task.WhenAll(running).ConfigureAwait(false);

        return [.. outcomes];
    }

    private async Task<ToolOutcome> InvokeOneAsync(
        ToolUsePart call, AgentRunRequest request, CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();

        if (request.Observer is not null)
        {
            try { await request.Observer.OnToolStartAsync(call.ToolName, ct).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Agent observer threw on tool start"); }
        }

        ToolResult result;

        try
        {
            result = await tools
                .InvokeAsync(call.ToolName, call.ToolCallId, call.Arguments, request.ToolContext, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Swallowed deliberately: the call still needs an answer, and the loop's own
            // cancellation handling decides what happens to the run.
            result = ToolResult.Error($"{call.ToolName} was cancelled.");
        }
        catch (Exception ex)
        {
            result = ToolResult.Error($"{call.ToolName} failed: {ex.Message}");
        }

        if (request.Observer is not null)
        {
            try { await request.Observer.OnToolEndAsync(call.ToolName, result, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Agent observer threw on tool end"); }
        }

        return new ToolOutcome(
            call.ToolName,
            new ToolResultPart(call.ToolCallId, result.Content, result.IsError),
            result.Observations,
            Stopwatch.GetElapsedTime(started),
            result.EndsTurn);
    }

    private static ToolOutcome Refused(ToolUsePart call, string reason) => new(
        call.ToolName,
        ToolResultPart.Text(call.ToolCallId, $"Not run: {reason}.", isError: true),
        [],
        TimeSpan.Zero);

    private static IEnumerable<LaneMessage> ToMessages(
        IReadOnlyList<ToolObservation> observations, AgentRunRequest request) =>
        observations.Select(o => LaneMessage.Observation(
            o.Scope == MemoryScopeHint.Global ? null : request.Session,
            request.Lane,
            o.Text,
            DateTimeOffset.UtcNow));

    private sealed record ToolOutcome(
        string Name,
        ToolResultPart Part,
        IReadOnlyList<ToolObservation> Observations,
        TimeSpan Duration,
        bool EndsTurn = false);
}
