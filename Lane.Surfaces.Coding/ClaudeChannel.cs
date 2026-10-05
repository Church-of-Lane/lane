using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Coding;

/// <summary>
/// Lane's side of the conversation, written to Claude Code. Lines are held until the turn that
/// wrote them ends, then sent as one message.
/// </summary>
internal sealed class ClaudeChannel : SessionChannelBase, ITextOutput
{
    private static readonly TimeSpan Fallback = TimeSpan.FromSeconds(30);

    private readonly Func<string, TurnKind, Task> _deliver;
    private readonly ILogger _log;
    private readonly List<string> _buffer = [];
    private readonly Lock _lock = new();
    private readonly IDisposable[] _subscriptions;
    private readonly Timer _fallback;

    /// <param name="deliver">Receives each whole message, with the kind of turn that produced it.</param>
    public ClaudeChannel(SessionId id, IEventBus bus, Func<string, TurnKind, Task> deliver, ILogger log)
        : base(id, id.Surface, ChannelCapabilities.Text)
    {
        _deliver = deliver;
        _log     = log;

        // Also flushes lines from a cancelled turn, which publishes neither event.
        _fallback = new Timer(_ => Flush(TurnKind.Respond), null, Timeout.Infinite, Timeout.Infinite);

        _subscriptions =
        [
            bus.Subscribe<TurnCompleted>(evt => { if (evt.Session == id) Flush(evt.Kind); }),
            bus.Subscribe<TurnFailed>(evt => { if (evt.Session == id) Flush(TurnKind.Respond); })
        ];
    }

    public Task SendAsync(OutboundText text, CancellationToken ct)
    {
        lock (_lock)
        {
            _buffer.Add(text.Text);
            _fallback.Change(Fallback, Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    private void Flush(TurnKind kind)
    {
        string message;

        lock (_lock)
        {
            _fallback.Change(Timeout.Infinite, Timeout.Infinite);

            if (_buffer.Count == 0) return;

            message = string.Join('\n', _buffer);
            _buffer.Clear();
        }

        _ = DeliverAsync(message, kind);
    }

    private async Task DeliverAsync(string message, TurnKind kind)
    {
        try { await _deliver(message, kind).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Could not pass Lane's message to Claude Code in {Session}", Id); }
    }

    public override ValueTask DisposeAsync()
    {
        foreach (IDisposable subscription in _subscriptions) subscription.Dispose();

        _fallback.Dispose();

        return ValueTask.CompletedTask;
    }
}
