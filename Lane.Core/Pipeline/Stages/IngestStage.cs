using Lane.Core.Messages;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Records what arrived before anything decides what to do about it, so a message is
/// remembered even when Lane chooses not to reply or the turn later fails.
/// </summary>
public sealed class IngestStage(ILogger<IngestStage> log) : ITurnStage
{
    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        foreach (LaneMessage message in ctx.Incoming)
        {
            log.LogInformation("[{Session}] {Author}: {Text}",
                ctx.Session.Id, message.Author.DisplayName, message.TextContent);

            ctx.Produced.Add(message.WithoutAudio());
        }

        await next().ConfigureAwait(false);
    }
}
