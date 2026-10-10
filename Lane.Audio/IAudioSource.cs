namespace Lane.Audio;

/// <summary>
/// Somewhere audio comes from.
///
/// The abstraction exists so recognition never learns what it is listening to. v2's ear
/// took a Discord-specific stream type in its constructor, which meant a microphone that
/// was not Discord could not be heard at all.
/// </summary>
public interface IAudioSource : IAsyncDisposable
{
    AudioSourceId Id { get; }

    AudioFormat Format { get; }

    /// <summary>A name for whoever is speaking, when the transport happens to know one.</summary>
    string? SpeakerHint { get; }

    IAsyncEnumerable<AudioFrame> ReadAsync(CancellationToken ct);
}

/// <summary>
/// One utterance's worth of audio, and what the recogniser called whoever spoke it.
///
/// The label distinguishes speakers within a live recognition session and nothing beyond
/// it — it is reassigned the moment that session ends. The audio is what outlives it: it is
/// the only thing a voiceprint can be taken from, and it has to be carried here because a
/// recogniser is the last place that still knows which samples belonged to which utterance.
/// </summary>
public sealed record VoiceSample(string Label, ReadOnlyMemory<byte> Pcm, AudioFormat Format)
{
    public TimeSpan Duration => Format.DurationOf(Pcm.Length);

    /// <summary>True when the audio was lost — the label is all there is to go on.</summary>
    public bool IsLabelOnly => Pcm.Length == 0;
}

public sealed record Transcript(
    string Text,
    bool IsFinal,
    TimeSpan Offset,
    float? Confidence = null,

    /// <summary>
    /// Null when the transport already knows who is speaking, which is the common case:
    /// Discord gives one source per speaker, and a named socket says who it is up front.
    /// Set only when one source carries several people and the words alone cannot say which.
    /// </summary>
    VoiceSample? Voice = null,

    /// <summary>The utterance itself, on a final result whose recogniser still had it.</summary>
    AudioFrame? Audio = null);

/// <summary>How a source's speakers are told apart.</summary>
public enum SpeakerAttribution
{
    /// <summary>One source, one person, settled before a word is said.</summary>
    Known,

    /// <summary>One microphone, several people, decided per utterance.</summary>
    Diarized,
}

/// <summary>
/// Turns one audio source into words.
///
/// Continuous rather than one utterance at a time: recognising once per call drops
/// whatever arrives between calls, and interim results — which barge-in depends on — never
/// surface at all.
/// </summary>
public interface ISpeechRecognizer : IAsyncDisposable
{
    IAsyncEnumerable<Transcript> TranscribeAsync(IAudioSource source, CancellationToken ct);
}

public sealed record SpeechOptions(
    string VoiceId,
    float  Stability     = 0.84f,
    float  Similarity    = 0.74f,
    float  Tempo         = 25f,
    float  Pitch         = -2.5f,
    float  Rate          = 0f,
    bool   TuneForSpeech = true,

    /// <summary>What the listener wants back. Null means the synthesiser's own format.</summary>
    AudioFormat? TargetFormat = null);

/// <summary>
/// Somewhere Lane's voice comes out.
///
/// Declared here rather than in the kernel because it speaks in <see cref="AudioFrame"/>,
/// and Core deliberately knows nothing about audio. A session resolves it the same way it
/// resolves text output — by capability lookup — so the kernel never needs the type.
/// </summary>
public interface IVoiceOutput
{
    /// <summary>What this channel wants to be handed. Synthesis converts to it.</summary>
    AudioFormat Format { get; }

    Task PlayAsync(IAsyncEnumerable<AudioFrame> audio, CancellationToken ct);

    Task StopAsync();
}

public interface ISpeechSynthesizer
{
    AudioFormat NativeFormat { get; }

    IAsyncEnumerable<AudioFrame> SynthesizeAsync(string text, SpeechOptions options, CancellationToken ct);
}
