using System.Text.Json;
using Lane.Core.Context;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Presence;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Pipeline.Stages;

public sealed class ResponsePolicyOptions
{
    /// <summary>
    /// Ask a small model whether this is worth answering before asking a large one to answer it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// At or below this, Lane stays quiet. The message is still remembered. Used wherever
    /// <see cref="ISessionThresholds"/> has no override for the conversation.
    /// </summary>
    public float DefaultThreshold { get; set; } = 0.2f;

    /// <summary>Template name for the classifier's instructions.</summary>
    public string Prompt { get; set; } = "Routing";

    /// <summary>
    /// Skip the gate in one-to-one conversations.
    ///
    /// A deliberate departure from v2, which gated everywhere. "Was that meant for me?" is a
    /// real question in a busy channel and a meaningless one in a DM — and being ignored by
    /// something you are talking to directly reads as broken rather than as tactful.
    /// </summary>
    public bool SkipInDirectSessions { get; set; } = true;

    /// <summary>Also let the classifier pick Lane's expression, as v2's router did.</summary>
    public bool SetPresence { get; set; } = true;

    public int MaxOutputTokens { get; set; } = 256;
}

/// <summary>
/// Whether to answer at all, and how warmly.
///
/// This is v2's "enthusiasm" router, which is the difference between an agent that sits in a
/// busy channel and one that replies to every message in it. Without it Lane answers
/// everything she can see.
///
/// The mechanism has changed. v2 prefilled the assistant turn with <c>```json</c> and used a
/// stop sequence to fish a JSON object back out, then parsed it and threw if the model had
/// been creative. Here the classifier is a forced tool call, so the shape is the provider's
/// problem and arrives already validated against a schema — and it works identically on
/// Anthropic and on anything OpenAI-compatible, which the prefill hack did not.
/// </summary>
public sealed class ResponsePolicyStage(
    ILanguageModelRegistry models,
    IPromptLibrary prompts,
    ITranscriptFormatter formatter,
    ISessionDescriptions descriptions,
    ISessionThresholds thresholds,
    IOptions<ResponsePolicyOptions> options,
    ILogger<ResponsePolicyStage> log,
    IPresenceSink? presence = null) : ITurnStage
{
    private readonly ResponsePolicyOptions _options = options.Value;

    /// <summary>The one tool the classifier is given, and is required to call.</summary>
    private static readonly ToolDescriptor Assess = new()
    {
        Name        = "assess",
        Description = "Report how much this message calls for a reply, and Lane's reaction to it.",
        InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "enthusiasm": {
                  "type": "number",
                  "description": "0 means do not reply at all; 1 means reply with real interest."
                },
                "emoticon": {
                  "type": "string",
                  "description": "A short text emoticon for Lane's face, such as ':3' or '( ._.)'."
                }
              },
              "required": ["enthusiasm", "emoticon"]
            }
            """).RootElement.Clone(),
        Availability = ToolAvailability.Anywhere
    };

    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        if (ShouldAssess(ctx)) await AssessAsync(ctx, ct).ConfigureAwait(false);

        await next().ConfigureAwait(false);
    }

    private bool ShouldAssess(TurnContext ctx)
    {
        if (!_options.Enabled || ctx.Suppressed) return false;

        // A directive is something Lane already decided to say, and a monologue is not
        // addressed to anyone. Neither is a question about whether to reply.
        if (ctx.Kind != TurnKind.Respond) return false;

        if (_options.SkipInDirectSessions && ctx.Descriptor.IsDirect) return false;

        // Nothing arrived — a nudge, or a turn with only tool traffic in it.
        return ctx.Incoming.Any(m => m.Role == LaneRole.User);
    }

    private async Task AssessAsync(TurnContext ctx, CancellationToken ct)
    {
        try
        {
            ILanguageModel model = models.Get(ModelRole.Routing, ctx.Descriptor);

            ModelResponse response = await model.CompleteAsync(new ModelRequest
            {
                System   = [new PromptBlock(Instructions(ctx, model.Descriptor), CacheHint.Ephemeral)],
                Messages = [.. ctx.Incoming],
                Tools    = [Assess],

                // Forced, so there is no branch where the model answers in prose and the
                // whole thing has to be parsed out of a sentence.
                ToolChoice      = ToolChoice.Specific(Assess.Name),
                MaxOutputTokens = _options.MaxOutputTokens,
                Temperature     = 0f,
                CacheLineage    = $"routing:{model.Descriptor.InstanceId}"
            }, ct).ConfigureAwait(false);

            if (Read(response) is not { } verdict)
            {
                log.LogWarning("The response classifier returned nothing usable; replying anyway");
                return;
            }

            if (_options.SetPresence && !string.IsNullOrWhiteSpace(verdict.Emoticon))
                presence?.Publish(new PresenceChanged(verdict.Emoticon, ctx.Session.Id));

            // Kept for the persona, so the reply is not merely allowed but pitched.
            ctx.Items["enthusiasm"] = verdict.Enthusiasm;

            float threshold = thresholds.For(ctx.Descriptor) ?? _options.DefaultThreshold;

            if (verdict.Enthusiasm > threshold)
            {
                log.LogInformation("Replying in {Session} (enthusiasm {Score:0.00}, threshold {Threshold:0.00})",
                    ctx.Session.Id, verdict.Enthusiasm, threshold);
                return;
            }

            log.LogInformation("Staying quiet in {Session} (enthusiasm {Score:0.00}, threshold {Threshold:0.00})",
                ctx.Session.Id, verdict.Enthusiasm, threshold);

            // Suppressed, not dropped: the later stages still write it to memory, so a
            // conversation Lane sat out is one she can still refer back to.
            ctx.Suppressed        = true;
            ctx.SuppressionReason = $"enthusiasm {verdict.Enthusiasm:0.00}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Replying anyway is the right failure. Silence caused by a broken classifier is
            // indistinguishable, from the outside, from Lane ignoring you.
            log.LogError(ex, "The response classifier failed; replying anyway");
        }
    }

    private static Verdict? Read(ModelResponse response)
    {
        foreach (ToolUsePart call in response.Content.OfType<ToolUsePart>())
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object) continue;

            if (!call.Arguments.TryGetProperty("enthusiasm", out JsonElement score)) continue;

            float value = score.ValueKind switch
            {
                JsonValueKind.Number => score.GetSingle(),

                // Some models hand back a number as a string when the schema says number.
                JsonValueKind.String when float.TryParse(score.GetString(), out float parsed) => parsed,

                _ => -1f
            };

            if (value < 0) continue;

            string emoticon = call.Arguments.TryGetProperty("emoticon", out JsonElement face)
                ? face.GetString() ?? ""
                : "";

            return new Verdict(Math.Clamp(value, 0f, 1f), emoticon.Trim());
        }

        return null;
    }

    private string Instructions(TurnContext ctx, ModelDescriptor model)
    {
        string description = Description(ctx);

        // The description goes before the closing instruction rather than after it: what the
        // classifier is being asked to do should still be the last thing it reads.
        if (Template(model) is not { } template) return Fallback + description + FallbackClose;

        return prompts.Render(template,
            ("session", ctx.Descriptor.DisplayName),
            ("description", description),
            ("transcript", Transcript(ctx)));
    }

    /// <summary>
    /// Prefers a <c>Routing.{provider}</c> variant over the generic template when one exists.
    ///
    /// Not every classifier is asked the question the same way. A System One provider takes its
    /// levels and options structurally and only wants the conversation as context — handing it
    /// the scoring bands as prose would be noise, since it never reads them.
    /// </summary>
    private string? Template(ModelDescriptor model)
    {
        string variant = $"{_options.Prompt}.{model.Provider}";

        if (prompts.Has(variant)) return variant;

        return prompts.Has(_options.Prompt) ? _options.Prompt : null;
    }

    /// <summary>
    /// What Lane has written down about this conversation, as context for whether to answer.
    ///
    /// "Everyone here speaks German" changes how a reply reads; "this channel is the D&amp;D
    /// game and I am running it" changes whether there should be one at all — a line of
    /// table talk aimed at nobody in particular is still hers to answer when she is running
    /// the table. Without this the gate is the one part of the turn that cannot see any of
    /// that, and it is the part that decides whether the rest happens.
    ///
    /// Written in the third person, unlike the copy the persona carries. This prompt talks
    /// *about* Lane to a small classifier, and a note in her own voice dropped into it would
    /// read as instructions to the classifier rather than as something she wrote. The
    /// leading blank lines are part of the value so that a conversation she has said nothing
    /// about leaves no gap in either the template or the fallback.
    /// </summary>
    private string Description(TurnContext ctx)
    {
        if (descriptions.For(ctx.Descriptor) is not { } description) return "";

        return $"\n\nLane has written this down about {ctx.Descriptor.DisplayName}, for herself:\n\n{description}";
    }

    /// <summary>The conversation so far, so "was that meant for Lane" has something to go on.</summary>
    private string Transcript(TurnContext ctx)
    {
        IReadOnlyList<LaneMessage> recent = ctx.Context.InlineMessages;

        if (recent.Count == 0) return "(nothing yet)";

        return formatter.Format(recent, new TranscriptFormatOptions(IncludeRelativeTime: false));
    }

    private const string Fallback =
        "Decide whether Lane should reply to the latest message, and how warmly. " +
        "Score 0 when it is clearly not addressed to her, is meaningless, or is unfinished. " +
        "Score low but non-zero for small talk directed at the group. " +
        "Score high when she is addressed by name, or the topic is one she cares about. " +
        "Also pick a short text emoticon for her reaction.";

    private const string FallbackClose = "\n\nCall the assess tool.";

    private readonly record struct Verdict(float Enthusiasm, string Emoticon);
}
