using Lane.Core.Messages;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Writes the turn's reply out through the session's own attached channels.
///
/// This is the second half of the cohesion guarantee: nothing else in the harness holds a
/// channel handle, so a reply can only ever reach the conversation that produced it.
/// </summary>
public sealed class DeliveryStage(ILogger<DeliveryStage> log) : ITurnStage
{
    /// <summary>
    /// Set by the model turn when <see cref="StreamingDeliveryObserver"/> already wrote the
    /// reply out line by line, so this stage does not say it a second time.
    /// </summary>
    public const string StreamedKey = "delivery.streamed";

    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        await DeliverAsync(ctx, ct).ConfigureAwait(false);

        await next().ConfigureAwait(false);
    }

    private async Task DeliverAsync(TurnContext ctx, CancellationToken ct)
    {
        if (ctx.Suppressed || ctx.Result is null) return;

        if (ctx.Items.TryGetValue(StreamedKey, out object? streamed) && streamed is true) return;

        string text = ctx.Result.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        IReadOnlyList<ITextOutput> outputs = ctx.Session.ResolveOutputs<ITextOutput>(ctx.Target);

        if (outputs.Count == 0)
        {
            log.LogWarning("No text output attached to {Session}; dropping reply", ctx.Session.Id);
            return;
        }

        // Thread the reply to the last message of the batch — a coalesced turn answers the
        // most recent thing said, not the one that happened to open the window.
        string? replyTo = ctx.Incoming.LastOrDefault()?.ExternalId;

        // A line break is a message boundary, the same rule streaming delivery follows, so a
        // reply that never streamed — a directive, or a model that cannot stream — still
        // arrives as the several messages it was written as.
        bool first = true;

        foreach (string line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            OutboundText outbound = new(line, first ? replyTo : null);

            first = false;

            foreach (ITextOutput output in outputs)
            {
                try
                {
                    await output.SendAsync(outbound, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One dead channel must not stop the others, nor fail the turn.
                    log.LogError(ex, "Failed to deliver to a channel on {Session}", ctx.Session.Id);
                }
            }
        }
    }
}
