using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Sits out a turn when everything said in it needs something the responding model does not
/// have — today, images sent to a model that cannot see them. The messages are still
/// remembered; only the reply is skipped.
/// </summary>
public sealed class CapabilityGateStage(ILanguageModelRegistry models, ILogger<CapabilityGateStage> log) : ITurnStage
{
    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        Gate(ctx);

        await next().ConfigureAwait(false);
    }

    private void Gate(TurnContext ctx)
    {
        if (ctx.Suppressed || ctx.Kind != TurnKind.Respond) return;

        List<LaneMessage> said = [.. ctx.Incoming.Where(m => m.Role == LaneRole.User && m.Kind == MessageKind.Utterance)];

        if (said.Count == 0 || !said.All(m => m.NeedsImages)) return;

        if (models.Get(ModelRole.Respond, ctx.Descriptor).Descriptor.Supports(ModelCapabilities.Images)) return;

        ctx.Suppressed        = true;
        ctx.SuppressionReason = "cannot see images";

        log.LogInformation("Skipping {Count} image-only message(s) in {Session}: the model cannot see images",
            said.Count, ctx.Session.Id);
    }
}
