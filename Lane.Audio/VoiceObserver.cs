using Lane.Core.Agent;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Audio;

/// <summary>
/// Speaks a reply as it is being written.
///
/// Text arrives in fragments, the chunker turns them into clauses, and each clause is
/// synthesised and played while the model is still producing the next. That pipelining is
/// the whole reason first audio arrives in a few hundred milliseconds instead of after the
/// entire reply.
///
/// Playback is serialised behind one task: two clauses spoken at once is noise, not speech.
/// </summary>
public sealed class VoiceObserver(
    Session session,
    IVoiceOutput output,
    ISpeechSynthesizer synthesizer,
    SpeechOptions options,
    IVoiceFloor floor,
    ILogger logger) : IAgentObserver
{
    private readonly SentenceChunker _chunker = new();
    private readonly CancellationTokenSource _speech = new();

    private IDisposable? _held;
    private Task _playback = Task.CompletedTask;
    private bool _withdrawn;

    public ValueTask OnTextAsync(string delta, CancellationToken ct)
    {
        foreach (string sentence in _chunker.Add(delta)) Enqueue(sentence);

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
        if (_chunker.Flush() is { } remaining && !_withdrawn) Enqueue(remaining);

        try
        {
            await _playback.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Speaking in {Session} failed", session.Id);
        }
    }

    /// <summary>Chains each clause onto the last, so they are spoken in order and never at once.</summary>
    private void Enqueue(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return;

        // The floor is claimed on the first clause and held until the whole reply is out,
        // so an interruption cancels the rest of it rather than just the current sentence.
        _held ??= floor.BeginSpeaking(session.Id, _speech);

        _playback = _playback.ContinueWith(
            async _ => await SpeakAsync(sentence).ConfigureAwait(false),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    private async Task SpeakAsync(string sentence)
    {
        if (_speech.IsCancellationRequested) return;

        try
        {
            SpeechOptions withFormat = options with { TargetFormat = output.Format };

            await output.PlayAsync(
                synthesizer.SynthesizeAsync(sentence, withFormat, _speech.Token),
                _speech.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Speech in {Session} was cut off", session.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not speak in {Session}", session.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _playback.ConfigureAwait(false); } catch { /* already reported */ }

        _held?.Dispose();
        _speech.Dispose();
    }
}

/// <summary>
/// Supplies a voice observer for conversations that can actually be heard in.
///
/// A text channel gets nothing, and pays nothing — the turn runs exactly as it did before
/// voice existed.
/// </summary>
public sealed class VoiceObserverFactory(
    ISpeechSynthesizer synthesizer,
    SpeechOptions options,
    IVoiceFloor floor,
    ILoggerFactory loggers) : IAgentObserverFactory
{
    public IAgentObserver? Create(Session session, TurnKind kind)
    {
        IReadOnlyList<IVoiceOutput> outputs = session.ResolveOutputs<IVoiceOutput>(DeliveryTarget.Primary);

        if (outputs.Count == 0) return null;

        return new VoiceObserver(
            session, outputs[0], synthesizer, options, floor, loggers.CreateLogger("Lane.Audio.Voice"));
    }
}
