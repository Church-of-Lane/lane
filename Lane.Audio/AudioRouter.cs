using System.Collections.Concurrent;
using Lane.Audio.Dsp;
using Lane.Audio.Voiceprints;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Lane.Audio;

/// <summary>
/// Which conversation an audio source belongs to, and who is speaking on it.
///
/// <paramref name="Attribution"/> is the difference between a Discord speaker and a
/// microphone in a room. When it is <see cref="SpeakerAttribution.Known"/>, <paramref
/// name="Speaker"/> is the answer; when it is <see cref="SpeakerAttribution.Diarized"/>,
/// it is only the fallback for utterances the audio cannot place.
/// </summary>
public sealed record AudioBinding(
    AudioSourceId Source,
    SessionId Session,
    Participant Speaker,
    SpeakerAttribution Attribution = SpeakerAttribution.Known);

/// <summary>
/// Listens to every microphone at once and turns what it hears into messages.
///
/// One recogniser per source, each on its own task. Two people in one voice channel are two
/// sources bound to one session, so their words arrive as one ordered conversation through
/// that session's pump — while a microphone bound elsewhere runs fully in parallel. v2
/// could only ever hear Discord, and only through a stream type its ear was built around.
///
/// A source need not be one person, though. Discord hands over one stream per speaker and
/// says who each one is; a microphone in a room hands over everybody at once and says
/// nothing. Those arrive through <see cref="RegisterShared"/> instead, and who spoke is
/// decided per utterance rather than settled at registration — see
/// <see cref="ISpeakerAttributor"/>. Everything downstream of here is identical either way:
/// a <see cref="Participant"/> on a message, which is all the kernel ever wanted.
/// </summary>
public sealed class AudioRouter(
    IAgentKernel kernel,
    Func<SpeakerAttribution, ISpeechRecognizer> recognizerFactory,
    IVoiceFloor floor,
    ILogger<AudioRouter> log,
    ISpeakerAttributor? attributor = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Listener> _listeners = new();

    private readonly ISpeakerAttributor _attributor = attributor ?? NullSpeakerAttributor.Instance;

    private CancellationTokenSource _lifetime = new();

    public int ActiveSources => _listeners.Count;

    /// <summary>Starts listening to a source and attributing it to a person in a conversation.</summary>
    public void Register(IAudioSource source, SessionId session, Participant speaker) =>
        Add(source, new AudioBinding(source.Id, session, speaker));

    /// <summary>
    /// Starts listening to a source that carries more than one person — a microphone in a
    /// room rather than one person's headset — and works out who spoke each utterance.
    ///
    /// <paramref name="fallback"/> is who the words belong to when that cannot be worked
    /// out: too short an utterance to place, or audio that arrived too late to examine.
    /// </summary>
    public void RegisterShared(IAudioSource source, SessionId session, Participant fallback) =>
        Add(source, new AudioBinding(source.Id, session, fallback, SpeakerAttribution.Diarized));

    private void Add(IAudioSource source, AudioBinding binding)
    {
        ArgumentNullException.ThrowIfNull(source);

        Listener listener = new(source, binding);

        if (!_listeners.TryAdd(source.Id.Value, listener))
        {
            log.LogDebug("Already listening to {Source}", source.Id);
            return;
        }

        listener.Task = Task.Run(() => ListenAsync(listener, _lifetime.Token), CancellationToken.None);

        if (binding.Attribution == SpeakerAttribution.Diarized)
            log.LogInformation("Listening to {Source} in {Session}, telling its speakers apart",
                source.Id, binding.Session);
        else
            log.LogInformation("Listening to {Source} as {Speaker} in {Session}",
                source.Id, binding.Speaker.DisplayName, binding.Session);
    }

    public async ValueTask UnregisterAsync(AudioSourceId id)
    {
        if (!_listeners.TryRemove(id.Value, out Listener? listener)) return;

        await listener.StopAsync().ConfigureAwait(false);

        // Whatever was learned about "Guest-1" on this source is about to be true of
        // somebody else, so it goes when the source does.
        _attributor.Release(id);

        log.LogInformation("Stopped listening to {Source}", id);
    }

    private async Task ListenAsync(Listener listener, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, listener.Stop.Token);

        await using ISpeechRecognizer recognizer = recognizerFactory(listener.Binding.Attribution);

        try
        {
            await foreach (Transcript transcript in
                recognizer.TranscribeAsync(listener.Source, linked.Token).ConfigureAwait(false))
            {
                if (transcript.Text.Length == 0) continue;

                if (!transcript.IsFinal)
                {
                    // Someone starting to talk while Lane is talking is the barge-in signal.
                    floor.NoticeSpeech(listener.Binding.Session, transcript);
                    continue;
                }

                await SubmitAsync(listener.Binding, transcript, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            // One microphone failing must not take the others down with it.
            log.LogError(ex, "Listening to {Source} failed", listener.Source.Id);
        }
    }

    private async Task SubmitAsync(AudioBinding binding, Transcript transcript, CancellationToken ct)
    {
        Participant speaker = await AttributeAsync(binding, transcript, ct).ConfigureAwait(false);

        log.LogInformation("[{Speaker}] heard: {Text}", speaker.DisplayName, transcript.Text);

        // Whoever was holding the floor has finished a sentence; anything Lane was saying
        // has already been cut off by the interim result that preceded this.
        floor.NoticeFinal(binding.Session);

        List<ContentPart> content = [new TextPart(transcript.Text)];

        if (transcript.Audio is { } audio)
            content.Add(new AudioPart(PcmConverter.ToWav(audio.Pcm.Span, audio.Format), "wav"));

        LaneMessage message = LaneMessage.User(binding.Session, speaker, content, DateTimeOffset.UtcNow);

        await kernel.SubmitAsync(new InboundEvent
        {
            Session = binding.Session,
            Author  = speaker,
            Message = message
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Who to write this line down as. A source with one person on it already knows, and
    /// a failure to work it out falls back to that same answer rather than dropping the
    /// words — something was said, and hearing it as the wrong person still beats not
    /// hearing it at all.
    /// </summary>
    private async ValueTask<Participant> AttributeAsync(
        AudioBinding binding, Transcript transcript, CancellationToken ct)
    {
        if (binding.Attribution != SpeakerAttribution.Diarized || transcript.Voice is not { } voice)
            return binding.Speaker;

        try
        {
            return await _attributor.AttributeAsync(binding, voice, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Could not tell who spoke on {Source}", binding.Source);

            return binding.Speaker;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        foreach (Listener listener in _listeners.Values) await listener.StopAsync().ConfigureAwait(false);

        _listeners.Clear();
        _lifetime.Dispose();
    }

    private sealed class Listener(IAudioSource source, AudioBinding binding)
    {
        public IAudioSource Source  { get; } = source;
        public AudioBinding Binding { get; } = binding;

        public CancellationTokenSource Stop { get; } = new();

        public Task? Task { get; set; }

        public async ValueTask StopAsync()
        {
            await Stop.CancelAsync().ConfigureAwait(false);

            if (Task is not null)
            {
                try { await Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { /* shutting down */ }
            }

            await Source.DisposeAsync().ConfigureAwait(false);

            Stop.Dispose();
        }
    }
}
