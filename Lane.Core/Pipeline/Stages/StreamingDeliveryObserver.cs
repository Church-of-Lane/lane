using System.Text;
using Lane.Core.Agent;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Core.Pipeline.Stages;

/// <summary>
/// Writes a reply out a line at a time, as it is written.
///
/// A line break is where Lane finished a thought, so that is where a message ends: each
/// completed line goes out while the model is still producing the next, and a reply that
/// came in three lines arrives as three messages rather than one wall of text after the
/// turn is over.
///
/// Sending is serialised behind one task — messages out of order read worse than messages
/// arriving late — and one dead channel never stops the rest, exactly as <see cref="DeliveryStage"/>
/// does for a whole reply.
/// </summary>
public sealed class StreamingDeliveryObserver(
    Session                    session,
    IReadOnlyList<ITextOutput> outputs,
    string?                    replyTo,
    ILogger                    log) : IAgentObserver
{
    private readonly LineChunker _lines = new();

    private Task _sending = Task.CompletedTask;
    private bool _first   = true;
    private bool _withdrawn;

    /// <summary>Whether anything actually reached a channel, so delivery knows not to repeat it.</summary>
    public bool Delivered { get; private set; }

    /// <summary>Null when the turn has nowhere to write, which is every turn on a voice-only session.</summary>
    public static StreamingDeliveryObserver? For(TurnContext ctx, ILogger log)
    {
        IReadOnlyList<ITextOutput> outputs = ctx.Session.ResolveOutputs<ITextOutput>(ctx.Target);

        if (outputs.Count == 0) return null;

        return new StreamingDeliveryObserver(ctx.Session, outputs, ctx.Incoming.LastOrDefault()?.ExternalId, log);
    }

    public ValueTask OnTextAsync(string delta, CancellationToken ct)
    {
        foreach (string line in _lines.Add(delta)) Enqueue(line);

        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolStartAsync(string name, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask OnToolEndAsync(string name, ToolResult result, CancellationToken ct)
    {
        if (result.EndsTurn) _withdrawn = true;

        return ValueTask.CompletedTask;
    }

    public async ValueTask OnFinishedAsync(CancellationToken ct)
    {
        // Lines already sent cannot be taken back, but a half-written one can still be dropped.
        if (_lines.Flush() is { } remaining && !_withdrawn) Enqueue(remaining);

        await DrainAsync().ConfigureAwait(false);
    }

    private void Enqueue(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        _sending = _sending.ContinueWith(
            async _ => await SendAsync(line).ConfigureAwait(false),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Deliberately uncancellable: a line half-way to a channel is better finished than
    /// abandoned, and the run is over by the time this runs anyway.
    /// </summary>
    private async Task SendAsync(string line)
    {
        OutboundText outbound = new(line, _first ? replyTo : null);

        _first = false;

        foreach (ITextOutput output in outputs)
        {
            try
            {
                await output.SendAsync(outbound, CancellationToken.None).ConfigureAwait(false);
                Delivered = true;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to stream a line to a channel on {Session}", session.Id);
            }
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await _sending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Streaming delivery on {Session} failed", session.Id);
        }
    }

    public async ValueTask DisposeAsync() => await DrainAsync().ConfigureAwait(false);
}

/// <summary>
/// Accumulates streamed fragments and hands back whole lines as they complete.
/// </summary>
internal sealed class LineChunker
{
    private readonly StringBuilder _buffer = new();

    /// <summary>Every line the fragment completed, in order; empty until one does.</summary>
    public IReadOnlyList<string> Add(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return [];

        _buffer.Append(delta);

        List<string> lines = [];

        int start = 0;

        for (int i = 0; i < _buffer.Length; i++)
        {
            if (_buffer[i] != '\n') continue;

            lines.Add(_buffer.ToString(start, i - start).Trim());
            start = i + 1;
        }

        if (start > 0) _buffer.Remove(0, start);

        return lines;
    }

    /// <summary>Whatever the reply ended on without a trailing line break.</summary>
    public string? Flush()
    {
        string rest = _buffer.ToString().Trim();

        _buffer.Clear();

        return rest.Length == 0 ? null : rest;
    }
}
