using System.ComponentModel;
using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lane.Tests;

public sealed class ToolSchemaTests
{
    private sealed record SearchArgs(
        [property: Description("What to search for.")] string Query,
        [property: Description("How many results.")] int Count = 5,
        [property: Description("Optional site filter.")] string? Site = null);

    [Fact]
    public void A_schema_is_generated_from_the_arguments_record()
    {
        // The record is the single source of truth. A hand-written schema drifts from the
        // type it describes, and the drift is silent.
        JsonElement schema = ToolSchema.For<SearchArgs>();

        Assert.Equal("object", schema.GetProperty("type").GetString());

        JsonElement properties = schema.GetProperty("properties");

        Assert.Equal("string", properties.GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("count").GetProperty("type").GetString());

        // Descriptions come from [Description], so the model is told what a field means.
        Assert.Equal("What to search for.", properties.GetProperty("query").GetProperty("description").GetString());

        // Only parameters without defaults are required.
        Assert.Equal(["query"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void A_tool_with_no_arguments_still_advertises_an_object_schema()
    {
        JsonElement schema = ToolSchema.For<NoArgs>();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.TryGetProperty("properties", out _));
    }

    private sealed class StrictTool : Tool<SearchArgs>
    {
        protected override string Name => "strict";
        protected override string Description => "d";
        protected override ValueTask<ToolResult> InvokeAsync(SearchArgs a, ToolContext c, CancellationToken ct) =>
            ValueTask.FromResult(ToolResult.Ok($"{a.Query}/{a.Count}"));
    }

    [Fact]
    public async Task Arguments_are_read_leniently_even_though_they_are_advertised_strictly()
    {
        // The schema says integer; a model that sends "7" should still be understood
        // rather than bounced, since a rejection costs a whole extra round trip.
        ITool tool = new StrictTool();

        ToolResult result = await tool.InvokeAsync(new ToolInvocation(
            "c1",
            JsonSerializer.SerializeToElement(new { query = "x", count = "7" }),
            Context()), default);

        Assert.False(result.IsError);
        Assert.Equal("x/7", result.Text);
    }

    [Fact]
    public async Task Unparseable_arguments_become_an_error_result_not_an_exception()
    {
        // Returned to the model, which usually corrects itself on the next step.
        ITool tool = new StrictTool();

        ToolResult result = await tool.InvokeAsync(new ToolInvocation(
            "c1", JsonSerializer.SerializeToElement(new { count = "not a number" }), Context()), default);

        Assert.True(result.IsError);
        Assert.Contains("Invalid arguments", result.Text);
    }

    private static ToolContext Context() =>
        new() { Services = new ServiceCollection().BuildServiceProvider() };
}

public sealed class ToolGatingTests
{
    private static readonly SurfaceId Surface = new("discord.main");

    private sealed class Stub(string name, ToolAvailability? availability = null) : ITool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            Name = name, Description = "d", InputSchema = ToolSchema.Empty,
            Availability = availability ?? ToolAvailability.Anywhere
        };

        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct) =>
            ValueTask.FromResult(ToolResult.Ok("ran"));
    }

    private sealed class Source(params ITool[] tools) : IToolSource
    {
        public string SourceId => "test";
        public event Action<string>? ToolsChanged { add { } remove { } }
        public ValueTask<IReadOnlyList<ITool>> GetToolsAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<ITool>>(tools);
    }

    private static ToolRegistry Registry(ToolOptions options, params ITool[] tools) =>
        new([new Source(tools)], Options.Create(options), NullLogger<ToolRegistry>.Instance);

    private static SessionDescriptor Descriptor(
        ChannelCapabilities capabilities = ChannelCapabilities.Text, string key = "general") =>
        new()
        {
            Id = new SessionId(Surface, SessionKind.Text, key),
            DisplayName = key, MemoryGroup = $"g/{key}", Capabilities = capabilities
        };

    [Fact]
    public async Task Monologue_only_tools_are_not_offered_during_a_reply()
    {
        ToolRegistry registry = Registry(new ToolOptions(),
            new Stub("web_search"),
            new Stub("speak_to_session", new ToolAvailability { AllowedTurns = TurnKind.Monologue }));

        ToolSet offered = await registry.ResolveAsync(new ToolScope(Descriptor(), TurnKind.Respond), default);

        Assert.Equal(["web_search"], offered.Descriptors.Select(d => d.Name));
    }

    [Fact]
    public async Task A_tool_the_model_was_never_offered_is_refused_when_it_calls_it()
    {
        // The advertised list is a hint, not a boundary — a model can name anything.
        ToolRegistry registry = Registry(new ToolOptions(),
            new Stub("speak_to_session", new ToolAvailability { AllowedTurns = TurnKind.Monologue }));

        ToolResult result = await registry.InvokeAsync(
            "speak_to_session", "c1", JsonSerializer.SerializeToElement(new { }),
            new ToolContext
            {
                Turn = TurnKind.Respond, Descriptor = Descriptor(),
                Services = new ServiceCollection().BuildServiceProvider()
            }, default);

        Assert.True(result.IsError);
        Assert.Contains("not available here", result.Text);
    }

    [Fact]
    public async Task Deny_lists_remove_a_tool_everywhere()
    {
        ToolRegistry registry = Registry(
            new ToolOptions { Deny = ["fetch_*"] },
            new Stub("web_search"), new Stub("fetch_url"));

        ToolSet offered = await registry.ResolveAsync(new ToolScope(Descriptor(), TurnKind.Respond), default);

        Assert.Equal(["web_search"], offered.Descriptors.Select(d => d.Name));
    }

    [Fact]
    public async Task A_surface_can_deny_a_tool_that_others_keep()
    {
        ToolOptions options = new()
        {
            DenyBySurface = new Dictionary<string, List<string>> { ["discord.main"] = ["set_emoticon"] }
        };

        ToolRegistry registry = Registry(options, new Stub("web_search"), new Stub("set_emoticon"));

        ToolSet onDiscord = await registry.ResolveAsync(new ToolScope(Descriptor(), TurnKind.Respond), default);

        SessionDescriptor elsewhere = new()
        {
            Id = new SessionId(new SurfaceId("terminal"), SessionKind.Text, "local"),
            DisplayName = "terminal", MemoryGroup = "terminal/local"
        };

        ToolSet onTerminal = await registry.ResolveAsync(new ToolScope(elsewhere, TurnKind.Respond), default);

        Assert.DoesNotContain("set_emoticon", onDiscord.Descriptors.Select(d => d.Name));
        Assert.Contains("set_emoticon", onTerminal.Descriptors.Select(d => d.Name));
    }

    [Fact]
    public async Task Capability_gated_tools_keep_the_advertised_set_stable_and_are_refused_at_the_call()
    {
        // Varying the tool list per session silently destroys the prompt cache behind it,
        // so a capability requirement is enforced on invocation instead.
        ToolAvailability needsVoice = new() { RequiredCapabilities = ChannelCapabilities.Voice };

        ToolRegistry registry = Registry(
            new ToolOptions { PreferStableSet = true },
            new Stub("speak_in_voice", needsVoice));

        SessionDescriptor textOnly = Descriptor(ChannelCapabilities.Text);

        ToolSet offered = await registry.ResolveAsync(new ToolScope(textOnly, TurnKind.Respond), default);
        Assert.Contains("speak_in_voice", offered.Descriptors.Select(d => d.Name));

        ToolResult result = await registry.InvokeAsync(
            "speak_in_voice", "c1", JsonSerializer.SerializeToElement(new { }),
            new ToolContext
            {
                Turn = TurnKind.Respond, Descriptor = textOnly,
                Services = new ServiceCollection().BuildServiceProvider()
            }, default);

        Assert.True(result.IsError);
        Assert.Contains("Voice", result.Text);
    }

    [Fact]
    public async Task Turning_off_the_stable_set_filters_by_capability_up_front()
    {
        ToolAvailability needsVoice = new() { RequiredCapabilities = ChannelCapabilities.Voice };

        ToolRegistry registry = Registry(
            new ToolOptions { PreferStableSet = false },
            new Stub("speak_in_voice", needsVoice));

        ToolSet offered = await registry.ResolveAsync(
            new ToolScope(Descriptor(ChannelCapabilities.Text), TurnKind.Respond), default);

        Assert.Empty(offered.Descriptors);
    }

    [Fact]
    public async Task Session_tag_tools_are_only_offered_where_the_tag_is_even_with_a_stable_set()
    {
        ToolRegistry registry = Registry(
            new ToolOptions { PreferStableSet = true },
            new Stub("web_search"),
            new Stub("git", new ToolAvailability { RequiredSessionTag = "workspace" }));

        SessionDescriptor plain  = Descriptor();
        SessionDescriptor tagged = Descriptor() with
        {
            Tags = new Dictionary<string, string> { ["workspace"] = "self" }
        };

        ToolSet onPlain  = await registry.ResolveAsync(new ToolScope(plain, TurnKind.Respond), default);
        ToolSet onTagged = await registry.ResolveAsync(new ToolScope(tagged, TurnKind.Respond), default);

        Assert.Equal(["web_search"], onPlain.Descriptors.Select(d => d.Name));
        Assert.Equal(["git", "web_search"], onTagged.Descriptors.Select(d => d.Name));

        ToolResult refused = await registry.InvokeAsync(
            "git", "c1", JsonSerializer.SerializeToElement(new { }),
            new ToolContext
            {
                Turn = TurnKind.Respond, Descriptor = plain,
                Services = new ServiceCollection().BuildServiceProvider()
            }, default);

        Assert.True(refused.IsError);
        Assert.Contains("workspace", refused.Text);
    }

    [Fact]
    public async Task The_fingerprint_tracks_the_advertised_set()
    {
        // Requests carry it so a change that quietly invalidates the prompt cache shows up
        // in telemetry rather than only on the bill.
        ToolRegistry a = Registry(new ToolOptions(), new Stub("one"), new Stub("two"));
        ToolRegistry b = Registry(new ToolOptions(), new Stub("one"));

        ToolScope scope = new(Descriptor(), TurnKind.Respond);

        string first  = (await a.ResolveAsync(scope, default)).Fingerprint;
        string second = (await b.ResolveAsync(scope, default)).Fingerprint;

        Assert.NotEqual(first, second);
        Assert.Equal(first, (await a.ResolveAsync(scope, default)).Fingerprint);
    }
}

public sealed class MemorySanitizerTests
{
    private static readonly SessionId Session =
        new(new SurfaceId("terminal"), SessionKind.Text, "local");

    private static Participant Lane => Participant.Lane(new SurfaceId("terminal"));

    [Fact]
    public void An_assistant_turn_keeps_its_text_and_loses_its_tool_call()
    {
        // The hazard: a window stores the tool_use, later trims away the tool_result, and
        // every request built from that window is then rejected by the provider.
        LaneMessage message = LaneMessage.Assistant(Session, Lane,
        [
            new TextPart("Let me look that up."),
            new ToolUsePart("c1", "web_search", JsonSerializer.SerializeToElement(new { q = "x" }))
        ], DateTimeOffset.UtcNow);

        LaneMessage? stored = MemorySanitizer.ForMemory(message);

        Assert.NotNull(stored);
        Assert.Equal("Let me look that up.", stored.TextContent);
        Assert.Empty(stored.Content.OfType<ToolUsePart>());
    }

    [Fact]
    public void A_turn_that_is_nothing_but_a_tool_call_is_not_stored_at_all()
    {
        LaneMessage message = LaneMessage.Assistant(Session, Lane,
            [new ToolUsePart("c1", "web_search", JsonSerializer.SerializeToElement(new { }))],
            DateTimeOffset.UtcNow);

        Assert.Null(MemorySanitizer.ForMemory(message));
    }

    [Fact]
    public void Tool_result_turns_are_never_stored()
    {
        LaneMessage message = LaneMessage.ToolResults(Session, Lane,
            [ToolResultPart.Text("c1", "some results")], DateTimeOffset.UtcNow);

        Assert.Null(MemorySanitizer.ForMemory(message));
    }

    [Fact]
    public void Thinking_blocks_are_dropped_because_their_signatures_do_not_travel()
    {
        LaneMessage message = LaneMessage.Assistant(Session, Lane,
            [new ThinkingPart("hmm", "sig"), new TextPart("here you go")], DateTimeOffset.UtcNow);

        LaneMessage? stored = MemorySanitizer.ForMemory(message);

        Assert.NotNull(stored);
        Assert.Empty(stored.Content.OfType<ThinkingPart>());
        Assert.Equal("here you go", stored.TextContent);
    }

    [Fact]
    public void An_ordinary_message_passes_through_untouched()
    {
        LaneMessage message = LaneMessage.User(
            Session, new Participant(new ParticipantId(new SurfaceId("terminal"), "jahan"), "jahan"),
            "hello", DateTimeOffset.UtcNow);

        Assert.Same(message, MemorySanitizer.ForMemory(message));
    }
}
