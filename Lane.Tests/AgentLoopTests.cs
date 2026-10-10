using System.Text.Json;
using Lane.Core.Agent;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The agent loop, driven entirely from a script. No network, no provider, no clock.
/// </summary>
public sealed class AgentLoopTests
{
    private static readonly SurfaceId Surface = new("terminal");
    private static readonly SessionId Session = new(Surface, SessionKind.Text, "local");

    // ---- fixtures ----------------------------------------------------------

    private sealed class FakeTool(
        string name,
        Func<JsonElement, CancellationToken, ValueTask<ToolResult>> body,
        ToolAvailability? availability = null,
        TimeSpan? timeout = null) : ITool
    {
        public int Calls;

        public ToolDescriptor Descriptor { get; } = new()
        {
            Name         = name,
            Description  = $"the {name} tool",
            InputSchema  = ToolSchema.Empty,
            Availability = availability ?? ToolAvailability.Anywhere,
            Timeout      = timeout ?? TimeSpan.FromSeconds(30)
        };

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return body(invocation.Arguments, ct);
        }
    }

    private sealed class FixedSource(params ITool[] tools) : IToolSource
    {
        public string SourceId => "test";
        public event Action<string>? ToolsChanged { add { } remove { } }
        public ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ITool>>(tools);
    }

    private static ToolRegistry Registry(ToolOptions? options, params ITool[] tools) =>
        new([new FixedSource(tools)], Options.Create(options ?? new ToolOptions()),
            NullLogger<ToolRegistry>.Instance);

    private static AgentLoop Loop(IToolRegistry registry) => new(registry, NullLogger<AgentLoop>.Instance);

    private static async Task<AgentRunRequest> RequestAsync(
        ILanguageModel model, IToolRegistry registry, AgentBudget? budget = null, string text = "go on then")
    {
        Participant lane = Participant.Lane(Surface);
        Participant user = new(new ParticipantId(Surface, "jahan"), "jahan");

        SessionDescriptor descriptor = new()
        {
            Id = Session, DisplayName = "terminal", MemoryGroup = "terminal/local"
        };

        return new AgentRunRequest
        {
            Model    = model,
            System   = [new PromptBlock("you are Lane")],
            Messages = [LaneMessage.User(Session, user, text, DateTimeOffset.UtcNow)],
            Tools    = await registry.ResolveAsync(new ToolScope(descriptor, TurnKind.Respond, user), default),
            ToolContext = new ToolContext
            {
                Session = Session, Descriptor = descriptor, Requester = user, Turn = TurnKind.Respond,
                Services = new ServiceCollection().BuildServiceProvider()
            },
            Lane    = lane,
            Session = Session,
            Budget  = budget ?? AgentBudget.Default
        };
    }

    // ---- the golden run ----------------------------------------------------

    [Fact]
    public async Task A_three_step_tool_run_produces_a_correctly_paired_history()
    {
        FakeTool search = new("web_search", (_, _) =>
            ValueTask.FromResult(ToolResult.Ok("tide pools are rocky shore habitats")
                                            .RememberAs("[searched: tide pools]", MemoryScopeHint.Global)));

        FakeTool fetch = new("fetch_url", (_, _) =>
            ValueTask.FromResult(ToolResult.Ok("a long article about anemones")));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCall("web_search", new { query = "tide pools" }, "c1"),
            ScriptedLanguageModel.ToolCall("fetch_url", new { url = "https://example.test" }, "c2"),
            ScriptedLanguageModel.Text("Tide pools are rocky shore habitats full of anemones."));

        ToolRegistry registry = Registry(null, search, fetch);

        AgentRunResult result = await Loop(registry)
            .RunAsync(await RequestAsync(model, registry), default);

        Assert.False(result.Cancelled);
        Assert.Equal(StopReason.EndTurn, result.Stop);
        Assert.Equal(3, model.CallCount);
        Assert.Equal(1, search.Calls);
        Assert.Equal(1, fetch.Calls);

        // assistant(tool_use) → tool_result → assistant(tool_use) → tool_result → assistant(text)
        Assert.Equal(5, result.NewMessages.Count);
        AssertPaired(result.NewMessages);

        Assert.Equal("Tide pools are rocky shore habitats full of anemones.", result.FinalText);

        // Usage accumulates across every step, not just the last one.
        Assert.True(result.TotalUsage.Input >= 30);

        // The observation the tool asked for is carried out, global rather than per-session.
        LaneMessage observation = Assert.Single(result.Observations);
        Assert.Contains("[searched: tide pools]", observation.TextContent);
        Assert.Null(observation.Session);
    }

    [Fact]
    public async Task Parallel_tool_calls_are_answered_in_the_order_they_were_requested()
    {
        // Providers match results to calls by id, but ordering mistakes still show up as
        // subtly wrong answers, so the loop preserves request order exactly.
        FakeTool slow = new("slow", async (_, ct) =>
        {
            await Task.Delay(60, ct);
            return ToolResult.Ok("slow done");
        });

        FakeTool fast = new("fast", (_, _) => ValueTask.FromResult(ToolResult.Ok("fast done")));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCalls(
                new ToolUsePart("c1", "slow", JsonSerializer.SerializeToElement(new { })),
                new ToolUsePart("c2", "fast", JsonSerializer.SerializeToElement(new { }))),
            ScriptedLanguageModel.Text("both done"));

        ToolRegistry registry = Registry(null, slow, fast);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), default);

        ToolResultPart[] results = [.. result.NewMessages[1].Content.OfType<ToolResultPart>()];

        Assert.Equal(["c1", "c2"], results.Select(r => r.ToolCallId));
        Assert.Contains("slow done", results[0].Content.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public async Task Text_said_alongside_a_tool_call_is_not_lost_when_the_final_turn_is_empty()
    {
        // Seen in a live run: the model answered *and* called a tool in one turn, then
        // returned nothing after the result. Taking only the last assistant message meant
        // a perfectly good reply was silently dropped and the user saw nothing at all.
        FakeTool tool = new("set_emoticon", (_, _) => ValueTask.FromResult(ToolResult.Ok("done")));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            new ModelResponse(
                [
                    new TextPart("Got it. I'll send \"anemone\" in a minute."),
                    new ToolUsePart("c1", "set_emoticon", JsonSerializer.SerializeToElement(new { }))
                ],
                StopReason.ToolUse,
                default),
            new ModelResponse([], StopReason.EndTurn, default));

        ToolRegistry registry = Registry(null, tool);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), default);

        Assert.Contains("anemone", result.FinalText);
        AssertPaired(result.NewMessages);
    }

    [Fact]
    public async Task Everything_said_across_several_steps_is_kept_in_order()
    {
        FakeTool tool = new("look", (_, _) => ValueTask.FromResult(ToolResult.Ok("found")));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            new ModelResponse(
                [new TextPart("Let me check."),
                 new ToolUsePart("c1", "look", JsonSerializer.SerializeToElement(new { }))],
                StopReason.ToolUse, default),
            ScriptedLanguageModel.Text("It was there after all."));

        ToolRegistry registry = Registry(null, tool);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), default);

        Assert.Equal("Let me check.\n\nIt was there after all.", result.FinalText);
    }

    // ---- the invariant -----------------------------------------------------

    [Fact]
    public async Task A_throwing_tool_becomes_an_error_result_rather_than_ending_the_run()
    {
        FakeTool broken = new("broken", (_, _) => throw new InvalidOperationException("kaboom"));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCall("broken", new { }, "c1"),
            ScriptedLanguageModel.Text("that did not work"));

        ToolRegistry registry = Registry(null, broken);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), default);

        AssertPaired(result.NewMessages);

        ToolResultPart error = result.NewMessages[1].Content.OfType<ToolResultPart>().Single();
        Assert.True(error.IsError);
        Assert.Contains("kaboom", error.Content.OfType<TextPart>().Single().Text);

        Assert.Equal("that did not work", result.FinalText);
    }

    [Fact]
    public async Task A_cancelled_run_still_answers_every_tool_call_it_requested()
    {
        // This is the invariant that matters most. An assistant message carrying a
        // tool_use with no matching tool_result does not merely lose a turn — every later
        // request in that session is rejected, permanently, until it rolls out of history.
        using CancellationTokenSource cts = new();

        FakeTool hangs = new("hangs", async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Ok("never");
        });

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCall("hangs", new { }, "c1"),
            ScriptedLanguageModel.Text("unreachable"));

        ToolRegistry registry = Registry(null, hangs);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), cts.Token);

        Assert.True(result.Cancelled);
        AssertPaired(result.NewMessages);

        ToolResultPart answer = result.NewMessages.Last().Content.OfType<ToolResultPart>().Single();
        Assert.Equal("c1", answer.ToolCallId);
        Assert.True(answer.IsError);
    }

    [Fact]
    public async Task A_tool_that_overruns_its_timeout_is_answered_with_an_error()
    {
        FakeTool slow = new("slow", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Ok("never");
        }, timeout: TimeSpan.FromMilliseconds(80));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCall("slow", new { }, "c1"),
            ScriptedLanguageModel.Text("moving on"));

        ToolRegistry registry = Registry(null, slow);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), default);

        Assert.False(result.Cancelled);
        AssertPaired(result.NewMessages);

        ToolResultPart answer = result.NewMessages[1].Content.OfType<ToolResultPart>().Single();
        Assert.True(answer.IsError);
        Assert.Contains("timed out", answer.Content.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public async Task A_call_to_an_unknown_tool_is_answered_rather_than_ignored()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCall("does_not_exist", new { }, "c1"),
            ScriptedLanguageModel.Text("fine"));

        ToolRegistry registry = Registry(null);

        AgentRunResult result = await Loop(registry).RunAsync(await RequestAsync(model, registry), default);

        AssertPaired(result.NewMessages);
        Assert.True(result.NewMessages[1].Content.OfType<ToolResultPart>().Single().IsError);
    }

    // ---- budgets -----------------------------------------------------------

    [Fact]
    public async Task The_step_budget_stops_a_model_that_never_stops_calling_tools()
    {
        FakeTool loop = new("again", (_, _) => ValueTask.FromResult(ToolResult.Ok("and again")));

        // Always asks for another tool; only the budget ends this.
        ScriptedLanguageModel model = new((_, i) =>
            ScriptedLanguageModel.ToolCall("again", new { }, $"c{i}"));

        ToolRegistry registry = Registry(null, loop);

        AgentRunResult result = await Loop(registry)
            .RunAsync(await RequestAsync(model, registry, new AgentBudget(MaxSteps: 3)), default);

        Assert.Equal(3, model.CallCount);
        AssertPaired(result.NewMessages);
    }

    [Fact]
    public async Task The_tool_call_budget_refuses_further_calls_and_lets_the_model_reply()
    {
        FakeTool tool = new("again", (_, _) => ValueTask.FromResult(ToolResult.Ok("ok")));

        ScriptedLanguageModel model = ScriptedLanguageModel.Sequence(
            ScriptedLanguageModel.ToolCall("again", new { }, "c1"),
            ScriptedLanguageModel.ToolCall("again", new { }, "c2"),
            ScriptedLanguageModel.ToolCall("again", new { }, "c3"),
            ScriptedLanguageModel.Text("fine, I'll stop"));

        ToolRegistry registry = Registry(null, tool);

        AgentRunResult result = await Loop(registry)
            .RunAsync(await RequestAsync(model, registry, new AgentBudget(MaxSteps: 6, MaxToolCalls: 2)), default);

        Assert.Equal(2, tool.Calls);
        Assert.Equal(4, model.CallCount);
        AssertPaired(result.NewMessages);

        ToolResultPart refused = result.NewMessages.SelectMany(m => m.Content).OfType<ToolResultPart>().Last();
        Assert.True(refused.IsError);
        Assert.Equal("Max tool calls reached.", refused.Content.OfType<TextPart>().Single().Text);

        Assert.Equal(StopReason.EndTurn, result.Stop);
        Assert.Equal("fine, I'll stop", result.FinalText);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Every tool_use is answered by exactly one tool_result, in call order.</summary>
    private static void AssertPaired(IReadOnlyList<LaneMessage> messages)
    {
        List<string> requested = [.. messages.SelectMany(m => m.Content).OfType<ToolUsePart>()
                                             .Select(p => p.ToolCallId)];

        List<string> answered = [.. messages.SelectMany(m => m.Content).OfType<ToolResultPart>()
                                            .Select(p => p.ToolCallId)];

        Assert.Equal(requested, answered);
    }
}
