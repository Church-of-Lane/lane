using System.Text.Json;
using Anthropic.Models.Messages;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Providers.Anthropic;
using Xunit;
using LaneStopReason = Lane.Core.Models.StopReason;
using WireStopReason = Anthropic.Models.Messages.StopReason;

namespace Lane.Tests;

/// <summary>
/// The mapper is the one place where Lane's model meets a wire format, and its mistakes
/// are the expensive kind — a lost speaker, a dropped tool result, a broken cache prefix.
/// </summary>
public sealed class AnthropicMappingTests
{
    private static readonly SurfaceId Surface = new("discord.main");
    private static readonly SessionId Session = new(Surface, SessionKind.Text, "general");

    private static Participant Speaker(string name) => new(new ParticipantId(Surface, name), name);

    private static LaneMessage FromUser(string speaker, string text) =>
        LaneMessage.User(Session, Speaker(speaker), text, DateTimeOffset.UtcNow);

    [Fact]
    public void Merged_user_messages_keep_their_speakers_and_stay_separated()
    {
        // A coalesced batch and a busy channel both land several speakers in one turn.
        // Without attribution the model sees one run-on utterance from nobody in particular.
        List<MessageParam> mapped = AnthropicMessageMapper.ToMessages(
        [
            FromUser("alice", "hey Lane"),
            FromUser("bob",   "what's up")
        ]);

        MessageParam turn = Assert.Single(mapped);
        Assert.Equal(Role.User, (Role)turn.Role);

        string text = TextOf(turn);

        Assert.Contains("alice: hey Lane", text);
        Assert.Contains("bob: what's up", text);
        Assert.DoesNotContain("hey Lanebob", text);
    }

    [Fact]
    public void Lanes_own_turns_are_not_prefixed()
    {
        // Prefixing the assistant's own turns teaches it to emit the prefix — the exact bug
        // v2 compensated for by stripping a leading "Lane says:" from every message.
        Participant lane = Participant.Lane(Surface);

        List<MessageParam> mapped = AnthropicMessageMapper.ToMessages(
        [
            FromUser("alice", "hi"),
            LaneMessage.Assistant(Session, lane, "hello", DateTimeOffset.UtcNow)
        ]);

        Assert.Equal(2, mapped.Count);
        Assert.Equal("hello", TextOf(mapped[1]));
    }

    [Fact]
    public void Consecutive_same_role_messages_become_one_turn()
    {
        List<MessageParam> mapped = AnthropicMessageMapper.ToMessages(
        [
            FromUser("alice", "one"),
            FromUser("alice", "two"),
            LaneMessage.Assistant(Session, Participant.Lane(Surface), "ok", DateTimeOffset.UtcNow),
            FromUser("alice", "three")
        ]);

        Assert.Equal(3, mapped.Count);
        Assert.Equal(Role.User,      (Role)mapped[0].Role);
        Assert.Equal(Role.Assistant, (Role)mapped[1].Role);
        Assert.Equal(Role.User,      (Role)mapped[2].Role);
    }

    [Fact]
    public void A_leading_assistant_message_is_dropped()
    {
        // Anthropic requires the conversation to open on a user turn, and a recalled window
        // can easily begin with one of Lane's own replies.
        List<MessageParam> mapped = AnthropicMessageMapper.ToMessages(
        [
            LaneMessage.Assistant(Session, Participant.Lane(Surface), "stale", DateTimeOffset.UtcNow),
            FromUser("alice", "hi")
        ]);

        MessageParam turn = Assert.Single(mapped);
        Assert.Equal(Role.User, (Role)turn.Role);
    }

    [Fact]
    public void Tool_calls_and_their_results_survive_the_round_trip()
    {
        // If a tool_use loses its id, or a tool_result goes missing, every subsequent
        // request in the session is rejected. This is the invariant worth guarding.
        Participant lane = Participant.Lane(Surface);

        LaneMessage call = LaneMessage.Assistant(Session, lane,
            [new ToolUsePart("call_1", "web_search", JsonSerializer.SerializeToElement(new { query = "tide pools" }))],
            DateTimeOffset.UtcNow);

        LaneMessage result = LaneMessage.ToolResults(Session, lane,
            [ToolResultPart.Text("call_1", "some results")], DateTimeOffset.UtcNow);

        List<MessageParam> mapped = AnthropicMessageMapper.ToMessages([FromUser("alice", "look it up"), call, result]);

        Assert.Equal(3, mapped.Count);

        ToolUseBlockParam use = Assert.IsType<ToolUseBlockParam>(BlocksOf(mapped[1]).Single().Value);
        Assert.Equal("call_1", use.ID);
        Assert.Equal("web_search", use.Name);
        Assert.Equal("tide pools", use.Input["query"].GetString());

        // The result rides in a user turn — that is the API's shape, not a Lane choice.
        Assert.Equal(Role.User, (Role)mapped[2].Role);

        ToolResultBlockParam answer = Assert.IsType<ToolResultBlockParam>(BlocksOf(mapped[2]).Single().Value);
        Assert.Equal("call_1", answer.ToolUseID);
    }

    [Fact]
    public void Cache_hints_become_breakpoints_without_reordering_blocks()
    {
        // Anthropic hashes the prefix in order, so a reordered block silently destroys the
        // cache rather than failing loudly.
        List<TextBlockParam> blocks = AnthropicMessageMapper.ToSystemBlocks(
        [
            new PromptBlock("persona", CacheHint.Ephemeral),
            new PromptBlock("long-term memory", CacheHint.Ephemeral),
            new PromptBlock("the clock", CacheHint.None)
        ]);

        Assert.Equal(3, blocks.Count);
        Assert.Equal(["persona", "long-term memory", "the clock"], blocks.Select(b => b.Text));

        Assert.NotNull(blocks[0].CacheControl);
        Assert.NotNull(blocks[1].CacheControl);
        Assert.Null(blocks[2].CacheControl);
    }

    [Fact]
    public void Empty_system_blocks_are_skipped()
    {
        List<TextBlockParam> blocks = AnthropicMessageMapper.ToSystemBlocks(
            [new PromptBlock("persona"), new PromptBlock("   "), new PromptBlock("")]);

        Assert.Single(blocks);
    }

    [Fact]
    public void Tool_schemas_carry_properties_and_required_fields()
    {
        JsonElement schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" }, count = new { type = "integer" } },
            required = new[] { "query" }
        });

        List<ToolUnion> tools = AnthropicMessageMapper.ToTools(
        [
            new Core.Tools.ToolDescriptor { Name = "web_search", Description = "Search.", InputSchema = schema }
        ]);

        Tool tool = Assert.IsType<Tool>(Assert.Single(tools).Value);

        Assert.Equal("web_search", tool.Name);
        Assert.Equal(["count", "query"], tool.InputSchema.Properties.Keys.Order());
        Assert.Equal(["query"], tool.InputSchema.Required);
    }

    [Theory]
    [InlineData(WireStopReason.EndTurn,      LaneStopReason.EndTurn)]
    [InlineData(WireStopReason.ToolUse,      LaneStopReason.ToolUse)]
    [InlineData(WireStopReason.MaxTokens,    LaneStopReason.MaxTokens)]
    [InlineData(WireStopReason.StopSequence, LaneStopReason.StopSequence)]
    [InlineData(WireStopReason.Refusal,      LaneStopReason.Refusal)]
    [InlineData(WireStopReason.PauseTurn,    LaneStopReason.Other)]
    public void Stop_reasons_map_across(WireStopReason wire, LaneStopReason expected) =>
        Assert.Equal(expected, AnthropicMessageMapper.FromStopReason(wire));

    private static IReadOnlyList<ContentBlockParam> BlocksOf(MessageParam turn)
    {
        Assert.True(turn.Content.TryPickContentBlockParams(out IReadOnlyList<ContentBlockParam>? blocks));
        return blocks!;
    }

    private static string TextOf(MessageParam turn) =>
        string.Concat(BlocksOf(turn).Select(b => b.Value).OfType<TextBlockParam>().Select(b => b.Text));

    [Fact]
    public void An_image_a_tool_returns_is_sent_inside_its_result()
    {
        Participant lane = Participant.Lane(Surface);

        LaneMessage call = LaneMessage.Assistant(Session, lane,
            [new ToolUsePart("call_1", "view_image", JsonSerializer.SerializeToElement(new { name = "cat" }))],
            DateTimeOffset.UtcNow);

        LaneMessage result = LaneMessage.ToolResults(Session, lane,
            [new ToolResultPart("call_1", [new TextPart("cat"), new ImagePart(null, new byte[] { 1, 2, 3 }, "image/png")], false)],
            DateTimeOffset.UtcNow);

        List<MessageParam> mapped = AnthropicMessageMapper.ToMessages([FromUser("alice", "show me"), call, result]);

        ToolResultBlockParam answer = Assert.IsType<ToolResultBlockParam>(BlocksOf(mapped[2]).Single().Value);

        Assert.True(answer.Content!.TryPickBlocks(out IReadOnlyList<Block>? blocks));
        Assert.True(blocks[1].TryPickImageBlockParam(out ImageBlockParam? image));
        Assert.True(image.Source.TryPickBase64Image(out Base64ImageSource? source));
        Assert.Equal("AQID", source.Data);
    }
}
