using Microsoft.Extensions.Logging.Abstractions;
using Lane.Tools.Replies;
using System.Text.Json;
using Lane.Core.Events;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Tools;
using Lane.Memory;
using Lane.Memory.Sqlite;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Tools running through the whole kernel, where the interesting question is not whether
/// a tool ran but what it leaves behind for the next turn.
/// </summary>
public sealed class ToolPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class EchoTool(string name) : ITool
    {
        public int Calls;

        public ToolDescriptor Descriptor { get; } = new()
        {
            Name = name, Description = "d", InputSchema = ToolSchema.Empty
        };

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);

            return ValueTask.FromResult(
                ToolResult.Ok("tide pools are rocky shore habitats")
                          .RememberAs("[searched: tide pools]", MemoryScopeHint.Global));
        }
    }

    private sealed class Source(ITool tool) : IToolSource
    {
        public string SourceId => "test";
        public event Action<string>? ToolsChanged { add { } remove { } }
        public ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ITool>>([tool]);
    }

    private static MemoryHandlerOptions Recent() => new()
    {
        Id = "recent", Type = "SlidingWindow", Scope = MemoryScope.Session,
        Slot = MemorySlot.Inline, MaxMessages = 30, Kinds = [MessageKind.Utterance]
    };

    [Fact]
    public async Task A_tool_turn_leaves_no_orphaned_tool_call_in_the_next_turns_context()
    {
        // The failure this guards against is permanent rather than transient: a stored
        // tool_use with no matching tool_result makes every later request in the session
        // invalid, so it must never reach the window in the first place.
        EchoTool tool = new("web_search");

        ScriptedLanguageModel model = new((_, call) => call switch
        {
            0 => ScriptedLanguageModel.ToolCall("web_search", new { query = "tide pools" }, "c1"),
            1 => ScriptedLanguageModel.Text("They're rocky shore habitats."),
            _ => ScriptedLanguageModel.Text("Still here.")
        });

        await using LaneHarness harness = LaneHarness.Create(model, services =>
        {
            services.AddLaneMemory(new SqliteOptions { InMemory = true }, o => o.Handlers = [Recent()]);
            services.AddSingleton<IToolSource>(new Source(tool));
        });

        RecordingChannel channel = harness.OpenSession("terminal", "local", memoryGroup: "terminal/local");

        await harness.SendAsync(channel, "jahan", "what are tide pools?");
        await channel.WaitForAsync(1, Timeout);

        Assert.Equal(1, tool.Calls);
        Assert.Equal("They're rocky shore habitats.", channel.Texts[0]);

        await harness.SendAsync(channel, "jahan", "and what else?");
        await channel.WaitForAsync(2, Timeout);

        IReadOnlyList<LaneMessage> replayed = harness.Model.Requests[^1].Messages;

        // The recalled window carries the answer but none of the machinery that produced it.
        Assert.Empty(replayed.SelectMany(m => m.Content).OfType<ToolUsePart>());
        Assert.Empty(replayed.SelectMany(m => m.Content).OfType<ToolResultPart>());

        string text = string.Join("\n", replayed.Select(m => m.TextContent));

        Assert.Contains("what are tide pools?", text);
        Assert.Contains("They're rocky shore habitats.", text);
    }

    [Fact]
    public async Task The_transcript_keeps_the_full_record_that_memory_strips()
    {
        // Memory holds what is safe to replay; the transcript holds what actually happened.
        EchoTool tool = new("web_search");

        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.ToolCall("web_search", new { query = "x" }, "c1")
            : ScriptedLanguageModel.Text("done"));

        await using LaneHarness harness = LaneHarness.Create(model, services =>
        {
            services.AddLaneMemory(new SqliteOptions { InMemory = true }, o => o.Handlers = [Recent()]);
            services.AddSingleton<IToolSource>(new Source(tool));
        });

        RecordingChannel channel = harness.OpenSession("terminal", "local", memoryGroup: "terminal/local");

        // The reply streams to the channel before the turn is persisted.
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable _ = harness.Services.GetRequiredService<IEventBus>()
            .Subscribe<TurnCompleted>(evt => { if (evt.Session == channel.Id) finished.TrySetResult(); });

        await harness.SendAsync(channel, "jahan", "look something up");
        await finished.Task.WaitAsync(Timeout);

        IReadOnlyList<LaneMessage> logged = await harness.Services
            .GetRequiredService<ITranscriptStore>()
            .ReadAsync(new TranscriptQuery { Session = channel.Id, Limit = 50 }, default);

        Assert.Contains(logged.SelectMany(m => m.Content).OfType<ToolUsePart>(), p => p.ToolName == "web_search");
        Assert.Contains(logged.SelectMany(m => m.Content).OfType<ToolResultPart>(), p => p.ToolCallId == "c1");
    }

    [Fact]
    public async Task An_observation_from_a_tool_reaches_global_memory()
    {
        // What v2 achieved by writing search results straight into memory from the
        // orchestrator, without the loop knowing that searching exists.
        EchoTool tool = new("web_search");

        MemoryHandlerOptions everything = new()
        {
            Id = "everything", Type = "SlidingWindow", Scope = MemoryScope.Global,
            Slot = MemorySlot.Volatile, Order = 10, MaxMessages = 50, Pinned = true,
            SectionTitle = "What you have found out",
            Kinds = [MessageKind.Observation]
        };

        ScriptedLanguageModel model = new((_, call) => call == 0
            ? ScriptedLanguageModel.ToolCall("web_search", new { query = "tide pools" }, "c1")
            : ScriptedLanguageModel.Text("done"));

        await using LaneHarness harness = LaneHarness.Create(model, services =>
        {
            services.AddLaneMemory(new SqliteOptions { InMemory = true },
                o => o.Handlers = [Recent(), everything]);
            services.AddSingleton<IToolSource>(new Source(tool));
        });

        RecordingChannel channel = harness.OpenSession("terminal", "local", memoryGroup: "terminal/local");

        await harness.SendAsync(channel, "jahan", "look it up");
        await channel.WaitForAsync(1, Timeout);

        await harness.SendAsync(channel, "jahan", "still there?");
        await channel.WaitForAsync(2, Timeout);

        string system = string.Join("\n", harness.Model.Requests[^1].System.Select(s => s.Text));

        Assert.Contains("[searched: tide pools]", system);
        Assert.Contains("What you have found out", system);
    }

    [Fact]
    public async Task Tools_are_offered_to_the_model_with_their_schemas()
    {
        EchoTool tool = new("web_search");

        await using LaneHarness harness = LaneHarness.Create(
            ScriptedLanguageModel.Echoing("ok"),
            services => services.AddSingleton<IToolSource>(new Source(tool)));

        RecordingChannel channel = harness.OpenSession("terminal", "local");

        await harness.SendAsync(channel, "jahan", "hello");
        await channel.WaitForAsync(1, Timeout);

        ModelRequest request = harness.Model.Requests[0];

        ToolDescriptor offered = Assert.Single(request.Tools);
        Assert.Equal("web_search", offered.Name);
        Assert.Equal(JsonValueKind.Object, offered.InputSchema.ValueKind);

        // The advertised set is fingerprinted into the cache lineage, so a change that
        // invalidates the prompt cache is visible rather than merely expensive.
        Assert.Contains("tools:", request.CacheLineage);
    }

    [Fact]
    public async Task Stop_responding_ends_the_turn_without_saying_anything()
    {
        ScriptedLanguageModel model = new((_, call) => call switch
        {
            0 => ScriptedLanguageModel.ToolCall("stop_responding", new { reason = "not for me" }, "c1"),
            _ => ScriptedLanguageModel.Text("This should never be said.")
        });

        await using LaneHarness harness = LaneHarness.Create(model, services =>
            services.AddSingleton<IToolSource>(
                new Source(new StopRespondingTool(NullLogger<StopRespondingTool>.Instance))));

        RecordingChannel channel = harness.OpenSession("terminal", "local");

        await harness.SendAsync(channel, "jahan", "talking to someone else");
        await channel.QuietAsync();

        Assert.Empty(channel.Sent);
        Assert.Equal(1, model.CallCount);
    }
}
