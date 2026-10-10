using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Recognition;

/// <summary>
/// Continuous recognition over a source that carries more than one person.
///
/// The sibling of <see cref="AzureSpeechRecognizer"/>, and the same shape end to end — the
/// difference is which question the service is being asked. A <c>SpeechRecognizer</c> is
/// told there is one speaker and returns words; a <c>ConversationTranscriber</c> is told
/// there may be several and returns words tagged with which of them said it. Pointing one
/// microphone at a room and using the first would attribute everyone in it to whoever the
/// source was registered as, so the choice is made per source rather than globally.
///
/// What comes back is a label — <c>Guest-1</c>, <c>Guest-2</c> — that is only meaningful
/// inside this one transcription session and is reassigned the next time one opens. Turning
/// it into a person who is still that person tomorrow is somebody else's job; this class's
/// contribution is the label and the audio it was said in, which is everything that job needs.
/// </summary>
public sealed class DiarizingSpeechRecognizer(AzureSpeechOptions options, ILogger<DiarizingSpeechRecognizer> log)
    : ISpeechRecognizer
{
    /// <summary>
    /// How far behind the write head an utterance's audio stays recoverable.
    ///
    /// Bounded because it costs memory per open microphone — 30 s of 16 kHz mono is under a
    /// megabyte — and generous because the alternative to having the audio is not having a
    /// voiceprint at all. Azure reports an utterance within a second or so of it ending.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    public async IAsyncEnumerable<Transcript> TranscribeAsync(
        IAudioSource source, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Region);

        SpeechConfig config = SpeechConfig.FromSubscription(options.Key, options.Region);

        config.SpeechRecognitionLanguage = options.Language;
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs,
            options.SegmentationSilenceMs.ToString());

        // Intermediate results are deliberately left undiarized. They exist to drive
        // barge-in, which cares that somebody started talking and not which somebody, and
        // asking for speaker ids on them mostly yields "Unknown" until the service settles.

        AudioStreamFormat format = AudioStreamFormat.GetWaveFormatPCM(
            (uint)source.Format.SampleRate, (byte)source.Format.BitsPerSample, (byte)source.Format.Channels);

        using PushAudioInputStream push = AudioInputStream.CreatePushStream(format);
        using AudioConfig audio = AudioConfig.FromStreamInput(push);
        using ConversationTranscriber transcriber = new(config, audio);

        options.ApplyPhrases(transcriber);

        UtteranceBuffer heard = new(source.Format, Window);

        Channel<Transcript> transcripts = Channel.CreateUnbounded<Transcript>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        transcriber.Transcribed += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech) return;
            if (string.IsNullOrWhiteSpace(e.Result.Text)) return;

            TimeSpan offset = e.Result.OffsetInTicks.Ticks();

            ReadOnlyMemory<byte> pcm = heard.Slice(offset, e.Result.Duration);

            if (pcm.Length == 0)
                log.LogDebug("Audio for an utterance on {Source} had already rolled past", source.Id);

            transcripts.Writer.TryWrite(new Transcript(
                e.Result.Text.Trim(), IsFinal: true, offset,
                Voice: new VoiceSample(Label(e.Result.SpeakerId), pcm, source.Format),
                Audio: pcm.Length > 0 ? new AudioFrame(pcm, source.Format, DateTimeOffset.UtcNow) : null));
        };

        if (options.EmitInterimResults)
        {
            transcriber.Transcribing += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Result.Text)) return;

                transcripts.Writer.TryWrite(new Transcript(
                    e.Result.Text.Trim(), IsFinal: false, e.Result.OffsetInTicks.Ticks()));
            };
        }

        transcriber.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                log.LogError("Transcription on {Source} failed: {Details}", source.Id, e.ErrorDetails);

            transcripts.Writer.TryComplete();
        };

        transcriber.SessionStopped += (_, _) => transcripts.Writer.TryComplete();

        await transcriber.StartTranscribingAsync().ConfigureAwait(false);

        Task pump = PumpAsync(source, push, heard, transcripts, ct);

        try
        {
            await foreach (Transcript transcript in transcripts.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return transcript;
        }
        finally
        {
            try { await transcriber.StopTranscribingAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "Transcriber for {Source} did not stop cleanly", source.Id); }

            try { await pump.ConfigureAwait(false); } catch { /* already reported */ }
        }
    }

    /// <summary>
    /// A speaker the service has not made its mind up about yet comes back as "Unknown", or
    /// occasionally as nothing at all. Both mean the same thing and both are kept rather than
    /// dropped — the words were still said, and attribution can fall back on the audio.
    /// </summary>
    private static string Label(string? speakerId) =>
        string.IsNullOrWhiteSpace(speakerId) ? "Unknown" : speakerId.Trim();

    private async Task PumpAsync(
        IAudioSource source,
        PushAudioInputStream push,
        UtteranceBuffer heard,
        Channel<Transcript> transcripts,
        CancellationToken ct)
    {
        try
        {
            await foreach (AudioFrame frame in source.ReadAsync(ct).ConfigureAwait(false))
            {
                if (frame.Pcm.Length == 0) continue;

                byte[] pcm = frame.Pcm.ToArray();

                // Kept before it is handed over, so the window and the recogniser's offsets
                // are measured from the same first byte.
                heard.Write(pcm);

                push.Write(pcm);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading audio from {Source} failed", source.Id);
        }
        finally
        {
            // Tells Azure the utterance is over so it emits whatever it was holding.
            try { push.Close(); } catch { /* already closed */ }

            transcripts.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
