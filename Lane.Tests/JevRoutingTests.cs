using System.Net;
using System.Text;
using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Providers.TypeSafe;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Jev answers questions where every other provider writes prose, so the translation in
/// both directions is the whole adapter: the gate's tool schema going out as criteria, and
/// a score coming back as the number the response policy compares to its threshold.
/// </summary>
public sealed class JevRoutingTests
{
    private static readonly SurfaceId Surface = new("discord.main");
    private static readonly SessionId Session = new(Surface, SessionKind.Text, "general");

    private static readonly ToolDescriptor Assess = new()
    {
        Name        = "assess",
        Description = "Report how much this message calls for a reply.",
        InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "enthusiasm": { "type": "number" },
                "emoticon":   { "type": "string" }
              },
              "required": ["enthusiasm", "emoticon"]
            }
            """).RootElement.Clone(),
        Availability = ToolAvailability.Anywhere
    };

    /// <summary>Captures what was sent and replays canned responses in order.</summary>
    private sealed class Stub(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _next;

        public List<JsonElement> Sent { get; } = [];

        public int Calls => _next;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());

            (HttpStatusCode status, string body) = responses[Math.Min(_next++, responses.Length - 1)];

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private static JevModel Model(Stub stub, JevRoutingProfile? profile = null) => new(
        new HttpClient(stub) { BaseAddress = new Uri(JevModel.DefaultEndpoint + "/") },
        new JevOptions { InstanceId = "jev", ApiKey = "k", Profile = profile ?? new JevRoutingProfile() },
        NullLogger<JevModel>.Instance);

    private static ModelRequest GateRequest(params LaneMessage[] messages) => new()
    {
        System     = [new PromptBlock("Lane is in general.")],
        Messages   = messages,
        Tools      = [Assess],
        ToolChoice = ToolChoice.Specific(Assess.Name)
    };

    private static LaneMessage FromUser(string speaker, string text) =>
        LaneMessage.User(Session, new Participant(new ParticipantId(Surface, speaker), speaker), text,
            DateTimeOffset.UtcNow);

    private const string Answered =
        """
        {
          "model": "jev-1.13.0",
          "answers": {
            "enthusiasm": { "type": "score", "score": 1.5, "confidence": 0.8,
                            "probabilities": { "1": 0.5, "2": 0.5 } },
            "emoticon":   { "type": "choice", "choice": ":3", "confidence": 0.9,
                            "probabilities": { ":3": 0.9 } }
          },
          "usage": { "input_tokens": 120, "output_tokens": 8 }
        }
        """;

    [Fact]
    public async Task The_gate_tool_becomes_a_score_and_a_choice_carrying_the_configured_criteria()
    {
        Stub stub = new((HttpStatusCode.OK, Answered));

        await Model(stub).CompleteAsync(GateRequest(FromUser("alice", "hey Lane")), TestContext.Current.CancellationToken);

        JsonElement sent = Assert.Single(stub.Sent);
        JsonElement questions = sent.GetProperty("questions");

        // Both questions ride in one request; the API evaluates them in parallel.
        Assert.Equal("score",  questions.GetProperty("enthusiasm").GetProperty("type").GetString());
        Assert.Equal("choice", questions.GetProperty("emoticon").GetProperty("type").GetString());

        JevRoutingProfile profile = new();

        Assert.Equal(
            profile.Enthusiasm.Levels.Count,
            questions.GetProperty("enthusiasm").GetProperty("criteria").GetArrayLength());

        Assert.Equal(
            profile.Emoticon.Faces.Count,
            questions.GetProperty("emoticon").GetProperty("criteria").EnumerateObject().Count());
    }

    [Fact]
    public async Task The_conversation_travels_as_state_rather_than_as_a_prompt()
    {
        Stub stub = new((HttpStatusCode.OK, Answered));

        await Model(stub).CompleteAsync(
            GateRequest(FromUser("alice", "hey Lane")), TestContext.Current.CancellationToken);

        JsonElement state = Assert.Single(stub.Sent).GetProperty("state");

        Assert.Contains("Lane is in general.", state.GetProperty("context").GetString());

        JsonElement latest = Assert.Single(state.GetProperty("latest").EnumerateArray().ToList());

        // The speaker survives: "was that meant for Lane" is unanswerable without it.
        Assert.Equal("alice",    latest.GetProperty("speaker").GetString());
        Assert.Equal("hey Lane", latest.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_score_comes_back_as_the_forced_tool_call_the_policy_expects()
    {
        ModelResponse response = await Model(new Stub((HttpStatusCode.OK, Answered)))
            .CompleteAsync(GateRequest(FromUser("alice", "hey Lane")), TestContext.Current.CancellationToken);

        Assert.Equal(StopReason.ToolUse, response.Stop);

        ToolUsePart call = Assert.Single(response.ToolCalls);

        Assert.Equal("assess", call.ToolName);

        // 1.5 over four levels — a weighted mean, not a band — is half way up the range.
        Assert.Equal(0.5f, call.Arguments.GetProperty("enthusiasm").GetSingle(), 3);
        Assert.Equal(":3",  call.Arguments.GetProperty("emoticon").GetString());

        Assert.Equal(120, response.Usage.Input);
        Assert.Equal(8,   response.Usage.Output);
    }

    [Fact]
    public async Task A_request_that_is_not_the_gate_is_refused_rather_than_answered_badly()
    {
        ModelRequest prose = new()
        {
            System   = [new PromptBlock("You are Lane.")],
            Messages = [FromUser("alice", "tell me a story")]
        };

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            Model(new Stub((HttpStatusCode.OK, Answered)))
                .CompleteAsync(prose, TestContext.Current.CancellationToken));

        Assert.Contains("cannot generate text", error.Message);
    }

    [Fact]
    public async Task An_overloaded_service_is_retried_rather_than_dropping_the_gate()
    {
        // 529 is TypeSafe's own; a gate that fails open makes Lane answer everything.
        Stub stub = new(((HttpStatusCode)529, "{}"), (HttpStatusCode.OK, Answered));

        ModelResponse response = await Model(stub).CompleteAsync(
            GateRequest(FromUser("alice", "hey Lane")), TestContext.Current.CancellationToken);

        Assert.Equal(2, stub.Calls);
        Assert.Single(response.ToolCalls);
    }
}
