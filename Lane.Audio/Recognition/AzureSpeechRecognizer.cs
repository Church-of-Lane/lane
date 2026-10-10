using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Recognition;

public sealed class AzureSpeechOptions
{
    public string? Key    { get; set; }
    public string? Region { get; set; }

    public string Language { get; set; } = "en-US";

    /// <summary>
    /// How long a pause ends an utterance. Short, so turn-taking feels responsive — at the
    /// cost of occasionally cutting someone off mid-thought.
    /// </summary>
    public int SegmentationSilenceMs { get; set; } = 300;

    /// <summary>Interim results are what barge-in listens to; without them she cannot be interrupted.</summary>
    public bool EmitInterimResults { get; set; } = true;

    /// <summary>Words and names recognition is biased towards, such as her own name.</summary>
    public List<string> Phrases { get; set; } = [];

    internal void ApplyPhrases(Recognizer recognizer)
    {
        if (Phrases.Count == 0) return;

        PhraseListGrammar grammar = PhraseListGrammar.FromRecognizer(recognizer);

        foreach (string phrase in Phrases)
            grammar.AddPhrase(phrase);
    }
}

/// <summary>
/// Continuous speech recognition over any audio source.
///
/// Two changes from v2, both structural. It takes an <see cref="IAudioSource"/> rather than
/// a Discord-specific stream, so a microphone that is not Discord can be heard at all. And
/// it recognises continuously instead of one utterance per call: v2 looped
/// <c>RecognizeOnceAsync</c>, which drops whatever arrives between calls and never surfaces
/// the interim results barge-in depends on.
/// </summary>
public sealed class AzureSpeechRecognizer(AzureSpeechOptions options, ILogger<AzureSpeechRecognizer> log)
    : ISpeechRecognizer
{
    /// <summary>How far behind the write head an utterance's audio stays recoverable.</summary>
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

        AudioStreamFormat format = AudioStreamFormat.GetWaveFormatPCM(
            (uint)source.Format.SampleRate, (byte)source.Format.BitsPerSample, (byte)source.Format.Channels);

        using PushAudioInputStream push = AudioInputStream.CreatePushStream(format);
        using AudioConfig audio = AudioConfig.FromStreamInput(push);
        using SpeechRecognizer recognizer = new(config, audio);

        options.ApplyPhrases(recognizer);

        UtteranceBuffer heard = new(source.Format, Window);

        Channel<Transcript> transcripts = Channel.CreateUnbounded<Transcript>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason != ResultReason.RecognizedSpeech) return;
            if (string.IsNullOrWhiteSpace(e.Result.Text)) return;

            TimeSpan offset = e.Result.OffsetInTicks.Ticks();

            ReadOnlyMemory<byte> pcm = heard.Slice(offset, e.Result.Duration);

            transcripts.Writer.TryWrite(new Transcript(
                e.Result.Text.Trim(), IsFinal: true, offset,
                Audio: pcm.Length > 0 ? new AudioFrame(pcm, source.Format, DateTimeOffset.UtcNow) : null));
        };

        if (options.EmitInterimResults)
        {
            recognizer.Recognizing += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Result.Text)) return;

                transcripts.Writer.TryWrite(new Transcript(
                    e.Result.Text.Trim(), IsFinal: false, e.Result.OffsetInTicks.Ticks()));
            };
        }

        recognizer.Canceled += (_, e) =>
        {
            if (e.Reason == CancellationReason.Error)
                log.LogError("Recognition on {Source} failed: {Details}", source.Id, e.ErrorDetails);

            transcripts.Writer.TryComplete();
        };

        recognizer.SessionStopped += (_, _) => transcripts.Writer.TryComplete();

        await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

        // Feeding runs alongside reading, so a silent speaker never blocks a talkative one.
        Task pump = PumpAsync(source, push, heard, transcripts, ct);

        try
        {
            await foreach (Transcript transcript in transcripts.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return transcript;
        }
        finally
        {
            try { await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false); }
            catch (Exception ex) { log.LogDebug(ex, "Recogniser for {Source} did not stop cleanly", source.Id); }

            try { await pump.ConfigureAwait(false); } catch { /* already reported */ }
        }
    }

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

internal static class TickExtensions
{
    public static TimeSpan Ticks(this long ticks) => TimeSpan.FromTicks(ticks);
}
