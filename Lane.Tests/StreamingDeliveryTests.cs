using Lane.Core.Sessions;
using Lane.Testing;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// A reply written as several lines arrives as several messages, each one sent the moment
/// it is finished rather than when the whole turn is.
/// </summary>
public sealed class StreamingDeliveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_reply_written_in_lines_arrives_as_one_message_per_line()
    {
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Echoing("first thought\nsecond thought\nthird thought"));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "alice", "say something");

        Assert.Equal(
            ["first thought", "second thought", "third thought"],
            await channel.WaitForAsync(3, Timeout));
    }

    [Fact]
    public async Task Blank_lines_are_a_gap_between_messages_rather_than_an_empty_one()
    {
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Echoing("one\n\n\ntwo\n"));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "alice", "say something");

        Assert.Equal(["one", "two"], await channel.WaitForAsync(2, Timeout));

        await Task.Delay(200);

        Assert.Equal(2, channel.Sent.Count);
    }

    [Fact]
    public async Task Only_the_first_message_answers_the_message_it_is_replying_to()
    {
        // The rest continue from it. Three replies to the same message reads as three
        // separate answers rather than one thought in three parts.
        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Echoing("here\nand here"));

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.SendAsync(channel, "alice", "where", externalId: "42");

        await channel.WaitForAsync(2, Timeout);

        IReadOnlyList<OutboundText> sent = channel.Sent;

        Assert.Equal("42", sent[0].ReplyToExternalId);
        Assert.Null(sent[1].ReplyToExternalId);
    }

    [Fact]
    public async Task A_volunteered_thought_is_split_the_same_way()
    {
        // A directive never touches the model, so it is delivery — not streaming — that has
        // to honour the line breaks for it.
        await using LaneHarness harness = LaneHarness.Create();

        RecordingChannel channel = harness.OpenSession("discord.main", "general");

        await harness.Kernel.PostAsync(
            channel.Id,
            new SessionWorkItem.Speak("I was thinking\nabout tide pools", DeliveryTarget.Primary, "monologue"));

        Assert.Equal(["I was thinking", "about tide pools"], await channel.WaitForAsync(2, Timeout));
    }
}
