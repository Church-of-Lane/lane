using System.Text.Json.Nodes;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Sessions;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Providers.OpenAi;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

public sealed class AudioInputTests
{
    private static readonly byte[] Clip = [1, 2, 3, 4];

    private const ModelCapabilities Deaf =
        ModelCapabilities.Tools | ModelCapabilities.Streaming | ModelCapabilities.StopSequences;

    private static LaneMessage Heard(ModelRequest request) => request.Messages.Last(m => m.Role == LaneRole.User);

    private static async Task<(ModelRequest Request, RecordingTranscript Transcript)> HeardAsync(
        ModelCapabilities capabilities)
    {
        ScriptedLanguageModel model = new((_, _) => ScriptedLanguageModel.Text("ok"), capabilities: capabilities);

        RecordingTranscript transcript = new();

        await using LaneHarness harness = LaneHarness.Create(
            model, services => services.AddSingleton<ITranscriptStore>(transcript));

        RecordingChannel channel = harness.OpenSession("api", "room", SessionKind.Voice);

        Participant author = new(new ParticipantId(channel.Surface, "alice"), "alice");

        await harness.Kernel.SubmitAsync(new InboundEvent
        {
            Session = channel.Id,
            Author  = author,
            Message = LaneMessage.User(
                channel.Id, author, [new TextPart("hello"), new AudioPart(Clip, "wav")], DateTimeOffset.UtcNow)
        }, TestContext.Current.CancellationToken);

        await channel.WaitForAsync(1);

        // The reply is delivered before the turn is persisted.
        for (int i = 0; i < 200 && transcript.Speakers.Count == 0; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        return (model.Requests[^1], transcript);
    }

    [Fact]
    public async Task A_model_that_takes_audio_is_handed_the_utterance_itself()
    {
        (ModelRequest request, _) = await HeardAsync(Deaf | ModelCapabilities.Audio);

        AudioPart audio = Assert.Single(Heard(request).Content.OfType<AudioPart>());

        Assert.Equal(Clip, audio.Data.ToArray());
        Assert.Equal("hello", Heard(request).TextContent);
    }

    [Fact]
    public async Task A_model_that_does_not_gets_the_words_alone()
    {
        (ModelRequest request, _) = await HeardAsync(Deaf);

        Assert.DoesNotContain(request.Messages.SelectMany(m => m.Content), p => p is AudioPart);
        Assert.Equal("hello", Heard(request).TextContent);
    }

    [Fact]
    public async Task Audio_is_never_written_down()
    {
        (_, RecordingTranscript transcript) = await HeardAsync(Deaf | ModelCapabilities.Audio);

        IReadOnlyList<LaneMessage> stored = await transcript.ReadAsync(
            new TranscriptQuery(), TestContext.Current.CancellationToken);

        Assert.Contains(stored, m => m.TextContent == "hello");
        Assert.DoesNotContain(stored.SelectMany(m => m.Content), p => p is AudioPart);
    }

    [Fact]
    public void An_openai_compatible_request_carries_it_as_input_audio_beside_the_transcript()
    {
        SurfaceId surface = new("api");
        SessionId session = new(surface, SessionKind.Voice, "room");

        JsonArray messages = OpenAiMessageMapper.ToMessages([], [LaneMessage.User(
            session, new Participant(new ParticipantId(surface, "alice"), "alice"),
            [new TextPart("hello"), new AudioPart(Clip, "wav")], DateTimeOffset.UtcNow)]);

        JsonArray content = messages[0]!["content"]!.AsArray();

        Assert.Contains("alice: hello", (string?)content[0]!["text"]);
        Assert.Equal("input_audio", (string?)content[1]!["type"]);
        Assert.Equal("wav", (string?)content[1]!["input_audio"]!["format"]);
        Assert.Equal(Convert.ToBase64String(Clip), (string?)content[1]!["input_audio"]!["data"]);
    }
}
