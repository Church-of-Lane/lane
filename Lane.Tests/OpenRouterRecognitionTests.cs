using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lane.Audio;
using Lane.Audio.Recognition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class OpenRouterRecognitionTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(100);

    private static AudioFrame Sound(double amplitude)
    {
        const int samples = 1600;

        byte[] pcm = new byte[samples * 2];

        for (int i = 0; i < samples; i++)
        {
            short value = (short)(Math.Sin(i * 0.3) * amplitude * short.MaxValue);

            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), value);
        }

        return AudioFrame.Of(pcm, AudioFormat.Pcm16kMono);
    }

    private static IEnumerable<AudioFrame> Talking(int frames) => Enumerable.Range(0, frames).Select(_ => Sound(0.25));
    private static IEnumerable<AudioFrame> Quiet(int frames)   => Enumerable.Range(0, frames).Select(_ => Sound(0));

    private static OpenRouterSpeechOptions Options(TimeSpan? interimAfter = null) => new()
    {
        ApiKey       = "k",
        InterimAfter = interimAfter ?? TimeSpan.Zero,
    };

    private static async Task<List<Transcript>> HeardAsync(
        Stub stub, OpenRouterSpeechOptions options, IEnumerable<AudioFrame> frames, bool sampleVoices = false)
    {
        OpenRouterSpeechRecognizer recognizer = new(
            new HttpClient(stub), options, NullLogger<OpenRouterSpeechRecognizer>.Instance, sampleVoices);

        List<Transcript> heard = [];

        await foreach (Transcript transcript in recognizer.TranscribeAsync(
            new Recording([.. frames]), TestContext.Current.CancellationToken))
        {
            heard.Add(transcript);
        }

        return heard;
    }

    [Fact]
    public async Task Each_pause_ends_an_utterance_and_each_utterance_is_one_request()
    {
        Stub stub = new("hello there", "how are you");

        List<Transcript> heard = await HeardAsync(stub, Options(),
            [.. Quiet(5), .. Talking(10), .. Quiet(8), .. Talking(6), .. Quiet(8)]);

        Assert.Equal(["hello there", "how are you"], heard.Select(t => t.Text));
        Assert.All(heard, t => Assert.True(t.IsFinal));
        Assert.Equal(2, stub.Sent.Count);

        // Two frames of pre-roll come with the first, so it starts 300 ms in rather than 500.
        Assert.Equal(TimeSpan.FromMilliseconds(300), heard[0].Offset);
    }

    [Fact]
    public async Task The_request_is_a_wav_of_the_utterance_in_the_shape_the_endpoint_takes()
    {
        Stub stub = new("hello");

        await HeardAsync(stub, Options(), [.. Talking(10), .. Quiet(8)]);

        Assert.Equal("https://openrouter.ai/api/v1/audio/transcriptions", stub.Uris.Single().ToString());
        Assert.Equal("Bearer k", stub.Authorization.Single());

        JsonElement sent = stub.Sent.Single();

        Assert.Equal("openai/whisper-large-v3-turbo", sent.GetProperty("model").GetString());
        Assert.Equal("en", sent.GetProperty("language").GetString());
        Assert.Equal("wav", sent.GetProperty("input_audio").GetProperty("format").GetString());

        byte[] wav = sent.GetProperty("input_audio").GetProperty("data").GetBytesFromBase64();

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));

        // Ten frames of speech and the five of silence that ended it.
        Assert.Equal(44 + 15 * AudioFormat.Pcm16kMono.BytesFor(Frame), wav.Length);
    }

    [Fact]
    public async Task A_sound_too_short_to_be_a_word_is_never_sent()
    {
        Stub stub = new("unused");

        List<Transcript> heard = await HeardAsync(stub, Options(), [.. Talking(2), .. Quiet(8)]);

        Assert.Empty(heard);
        Assert.Empty(stub.Sent);
    }

    [Fact]
    public async Task A_long_utterance_surfaces_an_interim_result_before_its_final_one()
    {
        Stub stub = new("hello", "hello there friend");

        List<Transcript> heard = await HeardAsync(stub, Options(interimAfter: TimeSpan.FromMilliseconds(500)),
            [.. Talking(12), .. Quiet(8)]);

        Assert.Equal([false, true], heard.Select(t => t.IsFinal));
        Assert.Null(heard[0].Audio);
        Assert.Equal(TimeSpan.FromMilliseconds(1700), heard[1].Audio!.Duration);
        Assert.Equal("hello there friend", heard[1].Text);
    }

    [Fact]
    public async Task A_failed_request_loses_that_utterance_and_not_the_ones_after_it()
    {
        Stub stub = new((HttpStatusCode.BadGateway, """{"error":{"message":"upstream"}}"""),
                        (HttpStatusCode.OK, """{"text":"still here"}"""));

        List<Transcript> heard = await HeardAsync(stub, Options(),
            [.. Talking(10), .. Quiet(8), .. Talking(10), .. Quiet(8)]);

        Assert.Equal("still here", Assert.Single(heard).Text);
    }

    [Fact]
    public async Task On_a_shared_source_each_utterance_carries_its_audio_under_its_own_label()
    {
        Stub stub = new("one", "two");

        List<Transcript> heard = await HeardAsync(stub, Options(),
            [.. Talking(10), .. Quiet(8), .. Talking(10), .. Quiet(8)], sampleVoices: true);

        Assert.Equal(2, heard.Select(t => t.Voice!.Label).Distinct().Count());
        Assert.All(heard, t => Assert.False(t.Voice!.IsLabelOnly));
    }

    [Fact]
    public void The_recogniser_is_chosen_by_name()
    {
        ServiceCollection services = new();

        services.AddLogging();
        services.AddLaneAudio(new AudioSetupOptions
        {
            Enabled    = true,
            Provider   = "flite",
            Recognizer = "openrouter",
            OpenRouter = Options(),
        });

        Func<SpeakerAttribution, ISpeechRecognizer> factory = services.BuildServiceProvider()
            .GetRequiredService<Func<SpeakerAttribution, ISpeechRecognizer>>();

        Assert.IsType<OpenRouterSpeechRecognizer>(factory(SpeakerAttribution.Known));
        Assert.IsType<OpenRouterSpeechRecognizer>(factory(SpeakerAttribution.Diarized));
    }

    [Fact]
    public void A_misspelled_recogniser_fails_at_registration()
    {
        ServiceCollection services = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => services.AddLaneAudio(
            new AudioSetupOptions { Enabled = true, Provider = "flite", Recognizer = "open-router" }));

        Assert.Contains("open-router", ex.Message);
    }

    private sealed class Recording(AudioFrame[] frames) : IAudioSource
    {
        public AudioSourceId Id => new("microphone/room");
        public AudioFormat Format => AudioFormat.Pcm16kMono;
        public string? SpeakerHint => null;

        public async IAsyncEnumerable<AudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            foreach (AudioFrame frame in frames)
            {
                await Task.Yield();
                yield return frame;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _responses;
        private int _next;

        public Stub(params string[] texts) =>
            _responses = [.. texts.Select(t => (HttpStatusCode.OK, JsonSerializer.Serialize(new { text = t })))];

        public Stub(params (HttpStatusCode Status, string Body)[] responses) => _responses = responses;

        public List<JsonElement> Sent { get; } = [];
        public List<Uri> Uris { get; } = [];
        public List<string> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!);
            Authorization.Add(request.Headers.Authorization!.ToString());

            Sent.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());

            (HttpStatusCode status, string body) = _responses[Math.Min(_next++, _responses.Length - 1)];

            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
