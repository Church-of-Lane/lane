using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Lane.Audio.Capture;
using Lane.Audio.Dsp;
using Microsoft.Extensions.Logging;

namespace Lane.Audio.Recognition;

public sealed class OpenRouterSpeechOptions
{
    public string? ApiKey { get; set; }

    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";

    public string Model { get; set; } = "openai/whisper-large-v3-turbo";

    /// <summary>ISO-639-1. Empty lets the model detect it.</summary>
    public string Language { get; set; } = "en";

    /// <summary>How loud a frame has to be to count as speech, in dBFS.</summary>
    public double SpeechLevel { get; set; } = -45;

    /// <summary>How long a pause ends an utterance.</summary>
    public int SegmentationSilenceMs { get; set; } = 500;

    /// <summary>Utterances with less speech in them than this are never sent.</summary>
    public TimeSpan MinimumSpeech { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>An utterance still going at this length is sent as it stands.</summary>
    public TimeSpan MaxUtterance { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Audio kept from before the frame that started an utterance.</summary>
    public TimeSpan PreRoll { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How much speech into an utterance its first part is transcribed as an interim result,
    /// which is what barge-in listens to. Costs a second request per utterance; zero disables it.
    /// </summary>
    public TimeSpan InterimAfter { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Speech recognition through OpenRouter's transcription endpoint.
///
/// The endpoint takes one clip and returns its text, so utterances are cut here, by level,
/// and each is uploaded whole. Transcripts are yielded in the order they were spoken.
/// </summary>
/// <param name="sampleVoices">
/// Attach each utterance's audio under a label of its own. The endpoint does not diarize,
/// so on a shared source a voiceprint is the only thing that can tell speakers apart.
/// </param>
public sealed class OpenRouterSpeechRecognizer(
    HttpClient http,
    OpenRouterSpeechOptions options,
    ILogger<OpenRouterSpeechRecognizer> log,
    bool sampleVoices = false) : ISpeechRecognizer
{
    public async IAsyncEnumerable<Transcript> TranscribeAsync(
        IAudioSource source, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);

        Channel<Task<Transcript?>> pending = Channel.CreateUnbounded<Task<Transcript?>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task pump = SegmentAsync(source, pending.Writer, stop.Token);

        try
        {
            await foreach (Task<Transcript?> recognition in pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (await recognition.ConfigureAwait(false) is { } transcript)
                    yield return transcript;
            }
        }
        finally
        {
            stop.Cancel();

            try { await pump.ConfigureAwait(false); } catch { /* already reported */ }
        }
    }

    private async Task SegmentAsync(
        IAudioSource source, ChannelWriter<Task<Transcript?>> pending, CancellationToken ct)
    {
        AudioFormat format  = source.Format;
        TimeSpan    silence = TimeSpan.FromMilliseconds(options.SegmentationSilenceMs);

        Queue<AudioFrame> preRoll     = new();
        TimeSpan          preRollHeld = TimeSpan.Zero;

        MemoryStream?            utterance = null;
        CancellationTokenSource? interim   = null;

        TimeSpan position = TimeSpan.Zero, start = TimeSpan.Zero, voiced = TimeSpan.Zero, quiet = TimeSpan.Zero;

        int  spoken      = 0;
        bool interimSent = false;

        void Finish()
        {
            // An interim result still on its way would only hold the final one up.
            interim?.Cancel();
            interim?.Dispose();
            interim = null;

            if (voiced >= options.MinimumSpeech)
            {
                byte[] pcm = utterance!.ToArray();

                VoiceSample? voice = sampleVoices
                    ? new VoiceSample($"Utterance-{++spoken}", pcm, format)
                    : null;

                pending.TryWrite(RecognizeAsync(source.Id, pcm, format, start, isFinal: true, voice, ct));
            }

            utterance = null;
        }

        try
        {
            await foreach (AudioFrame frame in source.ReadAsync(ct).ConfigureAwait(false))
            {
                if (frame.Pcm.Length == 0) continue;

                TimeSpan length = format.DurationOf(frame.Pcm.Length);
                bool     speech = NoiseGate.Level(frame) >= options.SpeechLevel;

                if (utterance is null)
                {
                    if (!speech)
                    {
                        preRoll.Enqueue(frame);
                        preRollHeld += length;

                        while (preRollHeld > options.PreRoll && preRoll.TryDequeue(out AudioFrame? old))
                            preRollHeld -= format.DurationOf(old.Pcm.Length);

                        position += length;
                        continue;
                    }

                    utterance = new MemoryStream();
                    start     = position - preRollHeld;

                    while (preRoll.TryDequeue(out AudioFrame? held)) utterance.Write(held.Pcm.Span);

                    preRollHeld = voiced = quiet = TimeSpan.Zero;
                    interimSent = false;
                }

                utterance.Write(frame.Pcm.Span);
                position += length;

                if (speech)
                {
                    voiced += length;
                    quiet   = TimeSpan.Zero;
                }
                else quiet += length;

                if (!interimSent && options.InterimAfter > TimeSpan.Zero && voiced >= options.InterimAfter)
                {
                    interimSent = true;
                    interim     = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    pending.TryWrite(RecognizeAsync(
                        source.Id, utterance.ToArray(), format, start, isFinal: false, voice: null, interim.Token));
                }

                if (quiet >= silence || position - start >= options.MaxUtterance) Finish();
            }

            // The source ended mid-sentence; what was said is still worth having.
            if (utterance is not null) Finish();
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading audio from {Source} failed", source.Id);
        }
        finally
        {
            interim?.Dispose();

            pending.TryComplete();
        }
    }

    /// <summary>Null when nothing was heard, or the request failed — a failure is logged, never thrown.</summary>
    private async Task<Transcript?> RecognizeAsync(
        AudioSourceId source,
        byte[] pcm,
        AudioFormat format,
        TimeSpan offset,
        bool isFinal,
        VoiceSample? voice,
        CancellationToken ct)
    {
        try
        {
            JsonObject body = new()
            {
                ["model"]       = options.Model,
                ["input_audio"] = new JsonObject
                {
                    ["data"]   = Convert.ToBase64String(PcmConverter.ToWav(pcm, format)),
                    ["format"] = "wav",
                },
            };

            if (!string.IsNullOrWhiteSpace(options.Language)) body["language"] = options.Language;

            using HttpRequestMessage request = new(
                HttpMethod.Post, $"{options.Endpoint.TrimEnd('/')}/audio/transcriptions");

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Headers.Add("X-Title", "Lane");
            request.Content = JsonContent.Create(body);

            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                log.LogError("Recognition on {Source} failed: {Status} {Details}",
                    source, (int)response.StatusCode, json);

                return null;
            }

            using JsonDocument parsed = JsonDocument.Parse(json);

            string? text = parsed.RootElement.TryGetProperty("text", out JsonElement heard)
                ? heard.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(text)) return null;

            return new Transcript(text.Trim(), isFinal, offset, Voice: voice,
                Audio: isFinal ? AudioFrame.Of(pcm, format) : null);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            log.LogError(ex, "Recognition on {Source} failed", source);

            return null;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
