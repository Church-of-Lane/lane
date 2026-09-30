using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Tools;
using Lane.Providers.OpenAi;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The OpenAI-compatible wire format differs from Anthropic's in ways that are easy to get
/// subtly wrong: tool results are their own messages, arguments travel as a JSON string,
/// and the set of <c>finish_reason</c> values is open-ended.
/// </summary>
public sealed class OpenAiMappingTests
{
    private static readonly SurfaceId Surface = new("discord.main");
    private static readonly SessionId Session = new(Surface, SessionKind.Text, "general");

    private static Participant Speaker(string name) => new(new ParticipantId(Surface, name), name);

    private static LaneMessage FromUser(string speaker, string text) =>
        LaneMessage.User(Session, Speaker(speaker), text, DateTimeOffset.UtcNow);

    private static string RoleOf(JsonNode? node) => (string?)node?["role"] ?? "";

    [Fact]
    public void System_blocks_are_flattened_into_one_system_message()
    {
        // OpenAI has no addressable system blocks, so cache hints have nothing to attach to.
        JsonArray messages = OpenAiMessageMapper.ToMessages(
            [new PromptBlock("persona", CacheHint.Ephemeral), new PromptBlock("memory", CacheHint.Ephemeral)],
            [FromUser("alice", "hi")]);

        Assert.Equal("system", RoleOf(messages[0]));
        Assert.Equal("persona\n\nmemory", (string?)messages[0]!["content"]);
        Assert.Equal("user", RoleOf(messages[1]));
    }

    [Fact]
    public void Speakers_stay_attributed_the_same_way_they_do_for_anthropic()
    {
        // The convention lives in one shared place, so it cannot drift between providers.
        JsonArray messages = OpenAiMessageMapper.ToMessages([], [FromUser("alice", "hey Lane")]);

        Assert.Contains("alice: hey Lane", (string?)messages[0]!["content"]);
    }

    [Fact]
    public void Lanes_own_turns_carry_no_speaker_prefix()
    {
        JsonArray messages = OpenAiMessageMapper.ToMessages([],
            [FromUser("alice", "hi"), LaneMessage.Assistant(Session, Participant.Lane(Surface), "hello", DateTimeOffset.UtcNow)]);

        Assert.Equal("assistant", RoleOf(messages[1]));
        Assert.Equal("hello", (string?)messages[1]!["content"]);
    }

    [Fact]
    public void Tool_calls_are_serialised_with_arguments_as_a_json_string()
    {
        // Not an object — a string containing JSON. Sending an object is rejected.
        LaneMessage call = LaneMessage.Assistant(Session, Participant.Lane(Surface),
            [new ToolUsePart("c1", "web_search", JsonSerializer.SerializeToElement(new { query = "tide pools" }))],
            DateTimeOffset.UtcNow);

        JsonArray messages = OpenAiMessageMapper.ToMessages([], [FromUser("alice", "look"), call]);

        JsonNode toolCall = messages[1]!["tool_calls"]![0]!;

        Assert.Equal("c1", (string?)toolCall["id"]);
        Assert.Equal("function", (string?)toolCall["type"]);
        Assert.Equal("web_search", (string?)toolCall["function"]!["name"]);

        string arguments = (string?)toolCall["function"]!["arguments"] ?? "";
        Assert.Equal("tide pools", JsonDocument.Parse(arguments).RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void Each_tool_result_becomes_its_own_message()
    {
        // Anthropic carries every result inside one user turn; this API wants one message
        // per call, each tagged with the id it answers.
        LaneMessage results = LaneMessage.ToolResults(Session, Participant.Lane(Surface),
            [ToolResultPart.Text("c1", "first"), ToolResultPart.Text("c2", "second")],
            DateTimeOffset.UtcNow);

        JsonArray messages = OpenAiMessageMapper.ToMessages([], [FromUser("alice", "go"), results]);

        Assert.Equal(3, messages.Count);
        Assert.Equal("tool", RoleOf(messages[1]));
        Assert.Equal("c1", (string?)messages[1]!["tool_call_id"]);
        Assert.Equal("first", (string?)messages[1]!["content"]);
        Assert.Equal("c2", (string?)messages[2]!["tool_call_id"]);
    }

    [Fact]
    public void Tools_are_wrapped_in_the_function_envelope()
    {
        JsonElement schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" } },
            required = new[] { "query" }
        });

        JsonArray tools = OpenAiMessageMapper.ToTools(
            [new ToolDescriptor { Name = "web_search", Description = "Search.", InputSchema = schema }]);

        Assert.Equal("function", (string?)tools[0]!["type"]);
        Assert.Equal("web_search", (string?)tools[0]!["function"]!["name"]);
        Assert.Equal("string", (string?)tools[0]!["function"]!["parameters"]!["properties"]!["query"]!["type"]);
    }

    [Fact]
    public void A_response_with_text_and_tool_calls_is_read_back_whole()
    {
        JsonElement message = JsonDocument.Parse("""
            {
              "role": "assistant",
              "content": "Let me look.",
              "tool_calls": [
                { "id": "c1", "type": "function",
                  "function": { "name": "web_search", "arguments": "{\"query\":\"x\"}" } }
              ]
            }
            """).RootElement;

        List<Lane.Core.Messages.ContentPart> parts = OpenAiMessageMapper.FromMessage(message);

        Assert.Equal("Let me look.", Assert.IsType<TextPart>(parts[0]).Text);

        ToolUsePart call = Assert.IsType<ToolUsePart>(parts[1]);
        Assert.Equal("c1", call.ToolCallId);
        Assert.Equal("x", call.Arguments.GetProperty("query").GetString());
    }

    [Fact]
    public void Malformed_tool_arguments_become_an_empty_object_rather_than_throwing()
    {
        // Common enough from smaller models that it must not end the turn. The tool then
        // reports a validation error the model can usually correct on its next step.
        JsonElement message = JsonDocument.Parse("""
            {
              "tool_calls": [
                { "id": "c1", "type": "function",
                  "function": { "name": "web_search", "arguments": "{not json" } }
              ]
            }
            """).RootElement;

        ToolUsePart call = Assert.IsType<ToolUsePart>(Assert.Single(OpenAiMessageMapper.FromMessage(message)));

        Assert.Equal(JsonValueKind.Object, call.Arguments.ValueKind);
        Assert.Empty(call.Arguments.EnumerateObject());
    }

    [Fact]
    public void A_tool_call_with_no_id_still_gets_one()
    {
        // Without an id there is nothing to pair a result to, and the next request breaks.
        JsonElement message = JsonDocument.Parse("""
            {"tool_calls":[{"type":"function","function":{"name":"web_search","arguments":"{}"}}]}
            """).RootElement;

        ToolUsePart call = Assert.IsType<ToolUsePart>(Assert.Single(OpenAiMessageMapper.FromMessage(message)));

        Assert.False(string.IsNullOrWhiteSpace(call.ToolCallId));
    }

    [Theory]
    [InlineData("stop", StopReason.EndTurn)]
    [InlineData("tool_calls", StopReason.ToolUse)]
    [InlineData("function_call", StopReason.ToolUse)]
    [InlineData("length", StopReason.MaxTokens)]
    [InlineData("content_filter", StopReason.Refusal)]
    [InlineData(null, StopReason.EndTurn)]
    // An endpoint that proxies many providers returns reasons no fixed list covers. v2 had
    // to bypass its SDK entirely to stop this throwing.
    [InlineData("ERROR", StopReason.Other)]
    [InlineData("some_upstream_thing", StopReason.Other)]
    public void Unknown_finish_reasons_are_information_not_a_crash(string? reason, StopReason expected) =>
        Assert.Equal(expected, OpenAiMessageMapper.FromFinishReason(reason));

    [Fact]
    public void Empty_assistant_turns_are_dropped_rather_than_sent()
    {
        JsonArray messages = OpenAiMessageMapper.ToMessages([],
            [FromUser("alice", "hi"), LaneMessage.Assistant(Session, Participant.Lane(Surface), "", DateTimeOffset.UtcNow)]);

        Assert.Single(messages);
    }

    [Fact]
    public void An_image_a_tool_returns_follows_the_tool_messages_as_a_user_turn()
    {
        Participant lane = Participant.Lane(Surface);

        LaneMessage call = LaneMessage.Assistant(Session, lane,
            [new ToolUsePart("call_1", "view_image", JsonSerializer.SerializeToElement(new { name = "cat" }))],
            DateTimeOffset.UtcNow);

        LaneMessage result = LaneMessage.ToolResults(Session, lane,
            [new ToolResultPart("call_1", [new TextPart("cat"), new ImagePart(null, new byte[] { 1, 2, 3 }, "image/png")], false)],
            DateTimeOffset.UtcNow);

        JsonArray messages = OpenAiMessageMapper.ToMessages([], [FromUser("alice", "show me"), call, result]);

        Assert.Equal(["user", "assistant", "tool", "user"], messages.Select(RoleOf));
        Assert.Equal("cat", (string?)messages[2]!["content"]);
        Assert.Equal("data:image/png;base64,AQID", (string?)messages[3]!["content"]![1]!["image_url"]!["url"]);
    }
}
