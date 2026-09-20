using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Shows the surface's "is typing" indicator for as long as the rest of the turn takes.
///
/// Placed after the response policy so a message Lane decides to ignore never makes her
/// look like she is about to answer it, and wrapped around every later stage so the
/// indicator survives a long agent loop and only drops once the reply is out.
/// </summary>
public sealed class TypingStage(ILogger<TypingStage> log) : ITurnStage
{
    public async Task ExecuteAsync(TurnContext ctx, Func<Task> next, CancellationToken ct)
    {
        if (ctx.Suppressed || ctx.Kind == TurnKind.Directive)
        {
            await next().ConfigureAwait(false);
            return;
        }

        List<IDisposable> scopes = [];

        foreach (ITypingIndicator indicator in ctx.Session.ResolveOutputs<ITypingIndicator>(ctx.Target))
        {
            try
            {
                scopes.Add(indicator.BeginTyping());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Could not start the typing indicator on {Session}", ctx.Session.Id);
            }
        }

        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            foreach (IDisposable scope in scopes)
            {
                try { scope.Dispose(); }
                catch (Exception ex) { log.LogDebug(ex, "Could not stop the typing indicator on {Session}", ctx.Session.Id); }
            }
        }
    }
}
