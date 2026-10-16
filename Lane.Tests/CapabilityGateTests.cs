using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Lane.Surfaces.Discord;
using Lane.Testing;
using Xunit;

namespace Lane.Tests;

/// <summary>Messages that need something the responding model does not have (#12).</summary>
public sealed class CapabilityGateTests
{
    private static readonly ImagePart Cat = new(new Uri("https://example.com/cat.png"), null, "image/png");

    private static ScriptedLanguageModel Model(ModelCapabilities extra = ModelCapabilities.None) =>
        new((_, _) => ScriptedLanguageModel.Text("nice"),
            capabilities: ModelCapabilities.Tools | ModelCapabilities.Streaming | extra);

    private static ValueTask SendAsync(LaneHarness harness, RecordingChannel channel, IReadOnlyList<ContentPart> content)
    {
        Participant author = new(new ParticipantId(channel.Surface, "alice"), "alice");

        return harness.Kernel.SubmitAsync(new InboundEvent
        {
            Session = channel.Id,
            Author  = author,
            Message = LaneMessage.User(channel.Id, author, content, DateTimeOffset.UtcNow)
        });
    }

    [Fact]
    public async Task An_image_she_cannot_see_gets_no_reply_and_costs_no_model_call()
    {
        await using LaneHarness harness = LaneHarness.Create(Model());

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await SendAsync(harness, channel, [Cat]);

        await channel.QuietAsync();

        Assert.Empty(channel.Sent);
        Assert.Equal(0, harness.Model.CallCount);
    }

    [Fact]
    public async Task An_image_she_can_see_is_answered()
    {
        await using LaneHarness harness = LaneHarness.Create(Model(ModelCapabilities.Images));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await SendAsync(harness, channel, [Cat]);

        await channel.WaitForAsync(1);

        Assert.Contains(harness.Model.Requests[^1].Messages, m => m.Content.Any(p => p is ImagePart));
    }

    [Fact]
    public async Task Words_beside_an_image_are_answered_with_the_image_described_instead()
    {
        await using LaneHarness harness = LaneHarness.Create(Model());

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await SendAsync(harness, channel, [new TextPart("look at this"), Cat]);

        await channel.WaitForAsync(1);

        IReadOnlyList<LaneMessage> sent = harness.Model.Requests[^1].Messages;

        Assert.DoesNotContain(sent, m => m.Content.Any(p => p is ImagePart));
        Assert.Contains(sent, m => m.TextContent.Contains(LaneMessage.UnseenImage));
    }

    [Fact]
    public void Text_that_only_lists_the_images_still_needs_them()
    {
        Participant alice = new(new ParticipantId(new SurfaceId("discord.main"), "alice"), "alice");

        LaneMessage listed = LaneMessage.User(
            new SessionId(new SurfaceId("discord.main"), SessionKind.Text, "1"), alice,
            [new TextPart("[image: cat.png] https://example.com/cat.png"), Cat], DateTimeOffset.UtcNow);

        Assert.False(listed.NeedsImages);
        Assert.True((listed with
        {
            Metadata = new Dictionary<string, string> { [LaneMessage.ImagesOnlyKey] = "true" }
        }).NeedsImages);
    }

    [Fact]
    public void Images_in_tool_results_are_described_for_a_model_that_cannot_see_them()
    {
        Participant lane = new(new ParticipantId(new SurfaceId("lane"), "lane"), "Lane");

        LaneMessage results = LaneMessage.ToolResults(null, lane,
            [new ToolResultPart("1", [new TextPart("found it"), Cat], false)], DateTimeOffset.UtcNow);

        ToolResultPart stripped = Assert.IsType<ToolResultPart>(
            Assert.Single(results.For(ModelCapabilities.Tools).Content));

        Assert.Equal(["found it", LaneMessage.UnseenImage], stripped.Content.OfType<TextPart>().Select(t => t.Text));
        Assert.Same(results, results.For(ModelCapabilities.Images));
    }

    [Fact]
    public void A_discord_post_with_no_words_of_its_own_says_nothing_but_images()
    {
        Assert.True(DiscordMapper.SaysNothingButImages("", [new EmbedView { ImageUrl = "https://x/y.png" }]));
        Assert.False(DiscordMapper.SaysNothingButImages("look", []));
        Assert.False(DiscordMapper.SaysNothingButImages("", [new EmbedView { Title = "An article" }]));
    }
}
