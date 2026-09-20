using Lane.Core.Agent;
using Lane.Core.Energy;
using Lane.Core.Memory;
using Lane.Core.Tools;
using Lane.Core.Context;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Produces Lane's reply, running the agent loop so she can use tools to get there.
/// </summary>
public sealed class ModelTurnStage(
    ILanguageModelRegistry models,
    IToolRegistry          tools,
    AgentLoop              loop,
    IEnumerable<IAgentObserverFactory> observerFactories,
    IPromptLibrary         prompts,
    ITranscriptFormatter   formatter,
    ISessionDescriptions   descriptions,
    IServiceProvider       services,
    IOptions<AgentOptions> options,
    IOptions<EnergyOptions> energy,
    ILogger<ModelTurnStage> log) : ITurnStage
{
    private readonly AgentOptions  _options = options.Value;
    private readonly EnergyOptions _energy  = energy.Value;

    /// <summary>
    /// Never below this, however tired she is. A ceiling low enough to cut her off mid-sentence
    /// is a bug that reads as one, rather than as brevity.
    /// </summary>
    private const int MinOutputTokens = 128;

    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        if (ctx.Suppressed)
        {
            log.LogInformation("Turn suppressed: {Reason}", ctx.SuppressionReason ?? "(unspecified)");
            await next().ConfigureAwait(false);
            return;
        }

        Participant lane = Participant.Lane(ctx.Session.Id.Surface);

        // A directive is text Lane already decided to say — the monologue volunteering
        // something. It still flows through delivery and persistence, but never the model.
        if (ctx.Kind == TurnKind.Directive)
        {
            string text = ctx.DirectiveText ?? "";

            LaneMessage spoken = LaneMessage.Assistant(ctx.Session.Id, lane, text, DateTimeOffset.UtcNow);

            ctx.Result = new TurnResult(text, [spoken], StopReason.EndTurn, default);
            ctx.Produced.Add(spoken);

            await next().ConfigureAwait(false);
            return;
        }

        await RunModelAsync(ctx, lane, ct).ConfigureAwait(false);

        await next().ConfigureAwait(false);
    }

    private async Task RunModelAsync(TurnContext ctx, Participant lane, CancellationToken ct)
    {
        ILanguageModel model = models.Get(ModelRole.Respond, ctx.Descriptor);

        List<LaneMessage> messages = [.. ctx.Context.InlineMessages, .. ctx.Incoming];

        if (messages.Count == 0)
        {
            log.LogDebug("Nothing to respond to in {Session}", ctx.Session.Id);
            return;
        }

        EnergyTier tier = TierOf(ctx);

        // Carried on the scope but not used to filter the advertised list — see the note in
        // ToolRegistry.Gate. What it does is let the registry refuse an expensive call.
        ToolScope scope = new(ctx.Descriptor, ctx.Kind, RequesterOf(ctx), tier);

        ToolSet available = await tools.ResolveAsync(scope, ct).ConfigureAwait(false);

        // A voice conversation gets an observer that speaks as the reply is written; a text
        // one gets nothing and pays nothing. Every factory that offers one is taken, not
        // just the first: an API voice session wants audio *and* text deltas, and picking
        // one would leave the other silent depending on registration order.
        // Last in the list, so the flush at the end of the run finds the voice observer
        // already done speaking: a line that arrives as text before it has been said out
        // loud reads as Lane talking over herself.
        StreamingDeliveryObserver? streaming = StreamingDeliveryObserver.For(ctx, log);

        await using IAgentObserver? observer = CompositeAgentObserver.Of(
            [.. observerFactories.Select(f => f.Create(ctx.Session, ctx.Kind))
                .Append(streaming)
                .OfType<IAgentObserver>()], log);

        AgentRunResult result = await loop.RunAsync(new AgentRunRequest
        {
            Model    = model,
            System   = [new PromptBlock(BuildPersona(ctx), CacheHint.Ephemeral), .. ctx.Context.SystemBlocks],
            Messages = messages,
            Tools    = available,
            ToolContext = new ToolContext
            {
                Session    = ctx.Session.Id,
                Descriptor = ctx.Descriptor,
                Requester  = RequesterOf(ctx),
                Turn       = ctx.Kind,
                Energy     = tier,
                Memory     = ctx.Items.TryGetValue("memory", out object? m) && m is MemoryContext mc ? mc : new(),
                Services   = services,
                Sessions   = services.GetService<ISessionRegistry>()
            },
            Lane            = lane,
            Session         = ctx.Session.Id,

            // Tiredness in the two places it is actually enforced rather than merely asked
            // for: a lower ceiling on the reply, and fewer steps and tool calls to reach it.
            // AgentLoop answers an over-budget call with a tool_result the model reads, so she
            // is told she has run out rather than quietly truncated.
            Budget          = _energy.For(tier)?.Scale(_options.Budget) ?? _options.Budget,
            MaxOutputTokens = _energy.For(tier)?.Scale(_options.MaxOutputTokens, MinOutputTokens)
                              ?? _options.MaxOutputTokens,

            Temperature     = _options.Temperature,
            Observer        = observer,
            CacheLineage    = $"respond:{model.Descriptor.InstanceId}"
        }, ct).ConfigureAwait(false);

        ctx.Result = new TurnResult(result.FinalText, result.NewMessages, result.Stop, result.TotalUsage);

        // The run is over, so every line the observer queued has been sent by now.
        if (streaming is { Delivered: true }) ctx.Items[DeliveryStage.StreamedKey] = true;

        // Everything the run produced is persisted, including the intermediate tool turns:
        // the transcript keeps the full record, and memory strips what it must not replay.
        ctx.Produced.AddRange(result.NewMessages);
        ctx.Produced.AddRange(result.Observations);

        if (result.Cancelled)
        {
            // Persist what happened, but do not say a half-finished thing out loud.
            ctx.Suppressed        = true;
            ctx.SuppressionReason = "turn cancelled";
        }

        if (result.ToolCalls.Count > 0)
            log.LogInformation("Used {Count} tool call(s): {Tools}",
                result.ToolCalls.Count, string.Join(", ", result.ToolCalls.Select(c => c.Name)));
    }

    private static Participant? RequesterOf(TurnContext ctx) =>
        ctx.Incoming.LastOrDefault(m => m.Role == LaneRole.User)?.Author;

    /// <summary>
    /// The persona template, filled with who and where, plus whatever Lane has written down
    /// about this particular conversation.
    /// </summary>
    private string BuildPersona(TurnContext ctx) => Persona(ctx) + Description(ctx);

    /// <summary>
    /// What <c>set_session_description</c> last wrote about this conversation.
    ///
    /// Folded into the persona block rather than added as a block of its own: it belongs to
    /// the same slowly-changing prefix, and a separate block would spend one of the handful
    /// of cache breakpoints a provider allows on a sentence. Appended in code rather than
    /// rendered into the template so a checkout with no template files keeps it too — and so
    /// that a conversation she has said nothing about adds nothing at all, rather than an
    /// empty heading.
    ///
    /// It is labelled as her own note on purpose. The text arrives from a conversation, by
    /// way of a model that was asked to write it, and it lands in the system prompt: saying
    /// where it came from is the difference between a note she wrote and an instruction she
    /// has no way to place.
    /// </summary>
    private string Description(TurnContext ctx)
    {
        if (descriptions.For(ctx.Descriptor) is not { } description) return "";

        return $"\n\nYou have written this down about {ctx.Descriptor.DisplayName}, for yourself, " +
               $"and can change it with set_session_description:\n\n{description}";
    }

    /// <summary>
    /// Falls back to the inline option when no template file is present, so tests and a bare
    /// checkout still run.
    /// </summary>
    private string Persona(TurnContext ctx)
    {
        // The fallback still gets the mood and the tiredness appended. Otherwise a checkout
        // with no template files silently loses the pitch of every reply, which is the sort of
        // difference nobody thinks to look for.
        if (!prompts.Has(_options.PersonaPrompt)) return _options.Persona + Mood(ctx) + Energy(ctx);

        IEnumerable<string> people = ctx.Descriptor.KnownParticipants
            .Where(p => !p.IsLane)
            .Select(p => p.DisplayName);

        // Whoever is actually speaking counts as present even if the roster is stale.
        people = people.Concat(ctx.Incoming.Where(m => m.Role == LaneRole.User).Select(m => m.Author.DisplayName));

        string participants = string.Join(", ", people.Distinct(StringComparer.OrdinalIgnoreCase));

        return prompts.Render(_options.PersonaPrompt,
            ("participants", string.IsNullOrWhiteSpace(participants) ? "someone" : participants),
            ("session", ctx.Descriptor.DisplayName),
            ("mood", Mood(ctx)),
            ("energy", Energy(ctx)),
            ("time", formatter.FormatTime(DateTimeOffset.UtcNow,
                new TranscriptFormatOptions(IncludeRelativeTime: false))));
    }

    /// <summary>
    /// How much the turn is worth, as the response policy judged it.
    ///
    /// v2 passed this as "high" / "neutral" / "low" and it is the difference between a reply
    /// that was merely permitted and one that is pitched right: a message that scraped past
    /// the threshold should get an acknowledgement, not an essay. Absent when no policy ran.
    /// </summary>
    private static string Mood(TurnContext ctx)
    {
        if (!ctx.Items.TryGetValue("enthusiasm", out object? value) || value is not float score) return "";

        return score switch
        {
            >= 0.8f => " This one interests you; reply properly.",
            >= 0.5f => "",
            _       => " This barely warrants a reply; keep it short and low-key."
        };
    }

    /// <summary>
    /// How worn out she is, as the sleep gate found her.
    ///
    /// A slot of its own rather than more text in <c>{{mood}}</c>: mood is a judgement about
    /// the message in front of her, energy is a standing condition about her, and the two
    /// compose — a message worth answering properly, answered briefly because she is tired, is
    /// a real combination. Absent entirely when no energy service is wired, so a container
    /// running on BoundlessEnergy produces exactly the prompt it always did.
    /// </summary>
    private string Energy(TurnContext ctx)
    {
        // Being woken up outranks the tier: she has just been dragged out of sleep, and how
        // much is left in the tank is not the notable thing about that.
        if (ctx.Items.TryGetValue(SleepGateStage.RousedKey, out object? roused) && roused is false)
            return " Someone has just woken you up and you are barely conscious. " +
                   "Answer them in one sentence and go back to sleep.";

        return TierOf(ctx) switch
        {
            EnergyTier.Tired =>
                " You are getting tired. Keep this shorter than usual, and do not go looking " +
                "things up unless it actually matters.",

            EnergyTier.Weary =>
                " You are exhausted and running on empty. Short answers. Do not use tools " +
                "unless the question is genuinely unanswerable without one.",

            _ => ""
        };
    }

    /// <summary>
    /// What the sleep gate measured, or rested when nothing did — a turn that never passed
    /// through the gate, which is every turn in a container with no energy service.
    /// </summary>
    private static EnergyTier TierOf(TurnContext ctx) =>
        ctx.Items.TryGetValue(SleepGateStage.EnergyKey, out object? value) && value is EnergyState state
            ? state.Tier
            : EnergyTier.Rested;
}
