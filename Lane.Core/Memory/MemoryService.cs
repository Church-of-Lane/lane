using System.Text;
using Lane.Core.Context;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Memory;

public interface IMemoryService
{
    ValueTask RememberAsync(LaneMessage message, MemoryContext ctx, CancellationToken ct);

    ValueTask<AssembledContext> AssembleAsync(MemoryContext ctx, LaneMessage? cue, CancellationToken ct);

    ValueTask FlushAsync(CancellationToken ct);
}

/// <summary>Used before memory is configured, and by tests that do not need it.</summary>
public sealed class NullMemoryService : IMemoryService
{
    public ValueTask RememberAsync(LaneMessage message, MemoryContext ctx, CancellationToken ct) =>
        ValueTask.CompletedTask;

    public ValueTask<AssembledContext> AssembleAsync(MemoryContext ctx, LaneMessage? cue, CancellationToken ct) =>
        ValueTask.FromResult(AssembledContext.Empty);

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>
/// Routes every remember and recall to the right handler instances.
///
/// The scope of each handler decides which instance a given conversation touches, so the
/// same configuration can hold a per-conversation window and a shared long-term store side
/// by side without either knowing about the other.
/// </summary>
public sealed class MemoryService(
    MemoryHandlerPool pool,
    ITranscriptFormatter formatter,
    ILogger<MemoryService> log) : IMemoryService
{
    public async ValueTask RememberAsync(LaneMessage message, MemoryContext ctx, CancellationToken ct)
    {
        foreach (MemoryHandlerOptions options in pool.Handlers)
        {
            if (!Applies(options, ctx)) continue;

            ScopeKey key = ScopeKeys.Derive(options.Scope, ctx);

            try
            {
                ScopedHandler scoped = await pool.GetAsync(options, key).ConfigureAwait(false);

                await scoped.WriteAsync(h => h.RememberAsync(new MemoryWrite(message, ctx, key), ct), ct)
                            .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken handler must not cost the others the message.
                log.LogError(ex, "Handler {Handler} failed to remember at {Key}", options.Id, key);
            }
        }
    }

    public async ValueTask<AssembledContext> AssembleAsync(MemoryContext ctx, LaneMessage? cue, CancellationToken ct)
    {
        List<(MemoryHandlerOptions Options, MemoryRecall Recall)> recalled = [];

        foreach (MemoryHandlerOptions options in pool.Handlers)
        {
            if (!Applies(options, ctx)) continue;

            ScopeKey key = ScopeKeys.Derive(options.Scope, ctx);

            try
            {
                ScopedHandler scoped = await pool.GetAsync(options, key).ConfigureAwait(false);

                MemoryRecall recall = await scoped.ReadAsync(
                    h => h.RecallAsync(new MemoryQuery
                    {
                        Context = ctx,
                        Key     = key,
                        Cue     = cue,
                        Kinds   = options.Kinds,
                        Limit   = options.MaxMessages
                    }, ct), ct).ConfigureAwait(false);

                if (recall.Messages.Count > 0 || !string.IsNullOrWhiteSpace(recall.RenderedText))
                    recalled.Add((options, recall));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed recall degrades the reply; it should never fail the turn.
                log.LogError(ex, "Handler {Handler} failed to recall at {Key}", options.Id, key);
            }
        }

        List<PromptBlock> stable   = [];
        List<PromptBlock> volatiles = [];
        List<LaneMessage> inline   = [];

        foreach ((MemoryHandlerOptions options, MemoryRecall recall) in recalled.OrderBy(r => r.Options.Order))
        {
            switch (options.Slot)
            {
                case MemorySlot.Inline:
                    inline.AddRange(recall.Messages);
                    break;

                case MemorySlot.Stable:
                    stable.Add(new PromptBlock(Render(options, recall, ctx), CacheHint.Ephemeral));
                    break;

                case MemorySlot.Volatile:
                    volatiles.Add(new PromptBlock(Render(options, recall, ctx), CacheHint.None));
                    break;
            }
        }

        // Inline turns come from several handlers and must end up in one coherent order.
        inline.Sort(static (a, b) =>
        {
            int byTime = a.Timestamp.CompareTo(b.Timestamp);
            return byTime != 0 ? byTime : a.Sequence.CompareTo(b.Sequence);
        });

        return new AssembledContext(stable, volatiles, inline);
    }

    public ValueTask FlushAsync(CancellationToken ct) => pool.FlushAsync(ct);

    /// <summary>
    /// Whether a handler participates in this context at all.
    ///
    /// The direct-session guard is the important one: a User-scoped handler recalled into a
    /// group channel would disclose what someone said in private. Excluded from writes as
    /// well as reads, so a DM-only memory only ever accumulates from DMs.
    /// </summary>
    private static bool Applies(MemoryHandlerOptions options, MemoryContext ctx)
    {
        if (!options.DirectSessionsOnly) return true;

        return ctx.Session?.IsDirect == true;
    }

    private string Render(MemoryHandlerOptions options, MemoryRecall recall, MemoryContext ctx)
    {
        string body = recall.RenderedText ?? formatter.Format(recall.Messages, new TranscriptFormatOptions(
            IncludeRelativeTime: true,
            // Global and Surface scopes mix conversations, so the reader needs to know
            // which one each line came from.
            IncludeSessionLabels: options.Scope is MemoryScope.Global or MemoryScope.Surface));

        if (string.IsNullOrWhiteSpace(options.SectionTitle)) return body;

        StringBuilder sb = new();
        sb.Append("## ").AppendLine(options.SectionTitle).AppendLine();
        sb.Append(body);

        return sb.ToString();
    }
}
