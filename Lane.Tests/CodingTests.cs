using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Lifecycle;
using Lane.Core.Memory;
using Lane.Core.Models;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Host;
using Lane.Memory.Sqlite;
using Lane.Surfaces.Coding;
using Lane.Surfaces.Coding.Claude;
using Lane.Surfaces.Coding.Git;
using Lane.Surfaces.Coding.Permissions;
using Lane.Surfaces.Coding.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class StreamJsonTests
{
    // Lines as Claude Code 2.1.278 wrote them, trimmed to the fields that matter.
    private const string Init =
        """{"type":"system","subtype":"init","cwd":"/w","session_id":"e5298a54-e08f-4c69-9c9e-2c5ea1837bc8","tools":["Bash"],"model":"claude-haiku-4-5-20251001"}""";

    private const string ToolUse =
        """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Building."},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"dotnet build","description":"Build"}},{"type":"tool_use","id":"t2","name":"Edit","input":{"file_path":"/w/src/A.cs"}}]},"parent_tool_use_id":null,"session_id":"s"}""";

    private const string SubagentToolUse =
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/w/x"}}]},"parent_tool_use_id":"toolu_1","session_id":"s"}""";

    private const string Success =
        """{"type":"result","subtype":"success","is_error":false,"result":"PONG","total_cost_usd":0.058815,"terminal_reason":"completed","queued_turn_count":0,"session_id":"s","duration_ms":3371,"usage":{"input_tokens":10,"cache_creation_input_tokens":28803,"cache_read_input_tokens":120,"output_tokens":47}}""";

    private const string Interrupted =
        """{"type":"result","subtype":"error_during_execution","is_error":true,"total_cost_usd":0.000974,"terminal_reason":"aborted_streaming","queued_turn_count":0,"session_id":"s"}""";

    private const string Control =
        """{"type":"control_response","response":{"subtype":"success","request_id":"req1","response":{"still_queued":[]}}}""";

    [Fact]
    public void The_session_id_comes_from_init()
    {
        ClaudeEvent evt = Assert.Single(StreamJson.Parse(Init));

        Assert.Equal("e5298a54-e08f-4c69-9c9e-2c5ea1837bc8", Assert.IsType<ClaudeEvent.Init>(evt).SessionId);
    }

    [Fact]
    public void Tool_calls_in_the_main_conversation_are_summarised_relative_to_the_workspace()
    {
        IReadOnlyList<ClaudeEvent> events = StreamJson.Parse(ToolUse, "/w");

        Assert.Equal(["ran `dotnet build`", "edited src/A.cs"],
            events.Cast<ClaudeEvent.ToolUse>().Select(e => e.Summary));

        Assert.Empty(StreamJson.Parse(SubagentToolUse, "/w"));
    }

    [Fact]
    public void A_result_carries_its_text_cost_and_queue()
    {
        ClaudeEvent.Result result = Assert.IsType<ClaudeEvent.Result>(Assert.Single(StreamJson.Parse(Success)));

        Assert.False(result.IsError);
        Assert.Equal("PONG", result.Text);
        Assert.Equal(0.058815m, result.TotalCostUsd);
        Assert.Equal(0, result.QueuedTurns);
        Assert.Equal(new TokenUsage(10, 47, 120, 28803, "claude-code", TimeSpan.FromMilliseconds(3371)), result.Usage);

        ClaudeEvent.Result interrupted = Assert.IsType<ClaudeEvent.Result>(Assert.Single(StreamJson.Parse(Interrupted)));

        Assert.True(interrupted.IsError);
        Assert.Equal("aborted_streaming", interrupted.TerminalReason);
    }

    [Fact]
    public void Control_responses_and_noise_are_told_apart()
    {
        ClaudeEvent.ControlResponse response =
            Assert.IsType<ClaudeEvent.ControlResponse>(Assert.Single(StreamJson.Parse(Control)));

        Assert.Equal("req1", response.RequestId);
        Assert.True(response.Success);

        Assert.Empty(StreamJson.Parse("not json"));
        Assert.Empty(StreamJson.Parse("""{"type":"rate_limit_event"}"""));
        Assert.Empty(StreamJson.Parse(""));
    }

    [Fact]
    public void Outbound_lines_have_the_shapes_claude_code_reads()
    {
        using JsonDocument user = JsonDocument.Parse(StreamJson.UserMessage("hi\nthere"));

        Assert.Equal("user", user.RootElement.GetProperty("type").GetString());
        Assert.Equal("hi\nthere", user.RootElement.GetProperty("message").GetProperty("content").GetString());

        using JsonDocument interrupt = JsonDocument.Parse(StreamJson.Interrupt("r1"));

        Assert.Equal("control_request", interrupt.RootElement.GetProperty("type").GetString());
        Assert.Equal("interrupt", interrupt.RootElement.GetProperty("request").GetProperty("subtype").GetString());
    }
}

public sealed class PermissionBrokerTests
{
    private static readonly JsonElement Input = JsonSerializer.SerializeToElement(new { command = "rm x" });

    [Fact]
    public async Task An_allowed_request_returns_its_input_unchanged()
    {
        PermissionBroker broker = new(TimeSpan.FromMinutes(1));
        broker.Requested += r => broker.Answer(r.WorkspaceId, r.Id, allow: true, reason: null);

        PermissionVerdict verdict = await broker.RequestAsync("w", "Bash", Input, TestContext.Current.CancellationToken);

        using JsonDocument result = JsonDocument.Parse(verdict.ToToolResult(Input));

        Assert.Equal("allow", result.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("rm x", result.RootElement.GetProperty("updatedInput").GetProperty("command").GetString());
    }

    [Fact]
    public async Task A_denial_carries_the_reason()
    {
        PermissionBroker broker = new(TimeSpan.FromMinutes(1));
        broker.Requested += r => broker.Answer(r.WorkspaceId, r.Id.ToUpperInvariant(), allow: false, reason: "too risky");

        PermissionVerdict verdict = await broker.RequestAsync("w", "Bash", Input, TestContext.Current.CancellationToken);

        using JsonDocument result = JsonDocument.Parse(verdict.ToToolResult(Input));

        Assert.Equal("deny", result.RootElement.GetProperty("behavior").GetString());
        Assert.Equal("too risky", result.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Nobody_answering_is_a_denial()
    {
        PermissionBroker broker = new(TimeSpan.FromMilliseconds(50));

        PermissionVerdict verdict = await broker.RequestAsync("w", "Bash", Input, TestContext.Current.CancellationToken);

        Assert.False(verdict.Allow);
        Assert.Empty(broker.PendingFor("w"));
    }

    [Fact]
    public async Task A_request_can_only_be_answered_from_its_own_workspace()
    {
        PermissionBroker broker = new(TimeSpan.FromMinutes(1));
        PermissionRequest? seen = null;
        broker.Requested += r => seen = r;

        Task<PermissionVerdict> pending = broker.RequestAsync("a", "Bash", Input, TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal(5, seen.Id.Length);
        Assert.DoesNotContain('l', seen.Id);
        Assert.Single(broker.PendingFor("a"));

        Assert.False(broker.Answer("b", seen.Id, allow: true, reason: null));
        Assert.True(broker.Answer("a", seen.Id, allow: true, reason: null));

        Assert.True((await pending).Allow);
    }
}

public sealed class CodingSurfaceTests
{
    internal sealed class RecordingClaude : IClaudeCodeTransport
    {
        public bool Busy => false;
        public bool Running => false;
        public string? SessionId => null;
        public decimal TotalCostUsd => 0;
        public event Action<ClaudeTurn>? TurnCompleted { add { } remove { } }
        public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> InterruptAsync(CancellationToken ct) => Task.FromResult(false);
        public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClaude : IClaudeCodeTransport
    {
        public List<string> Sent { get; } = [];
        public bool Busy => false;
        public bool Running => true;
        public string? SessionId => "s";
        public decimal TotalCostUsd => 0;
        public event Action<ClaudeTurn>? TurnCompleted;

        public void Reply(string text, TokenUsage usage = default) =>
            TurnCompleted?.Invoke(new ClaudeTurn(text, false, "completed", 0.25m, ["ran `dotnet build`"], usage));

        public Exception? Failure { get; set; }

        public Task SendAsync(string text, CancellationToken ct)
        {
            if (Failure is not null) throw Failure;
            lock (Sent) Sent.Add(text);
            return Task.CompletedTask;
        }
        public Task<bool> InterruptAsync(CancellationToken ct) => Task.FromResult(false);
        public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class RecordingKernel : IAgentKernel
    {
        public List<InboundEvent> Submitted { get; } = [];
        public List<(SessionId Session, SessionWorkItem Item)> Posted { get; } = [];

        public ValueTask SubmitAsync(InboundEvent evt, CancellationToken ct = default)
        {
            lock (Submitted) Submitted.Add(evt);
            return ValueTask.CompletedTask;
        }

        public ValueTask PostAsync(SessionId session, SessionWorkItem item, CancellationToken ct = default)
        {
            lock (Posted) Posted.Add((session, item));
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> CancelTurnAsync(SessionId session, string reason) => ValueTask.FromResult(false);
    }

    internal sealed class CapturingRegistry : ISessionRegistry
    {
        public SessionDescriptor? Descriptor { get; private set; }
        public ISessionChannel? Channel { get; private set; }

        public Session GetOrCreate(SessionDescriptor descriptor) { Descriptor = descriptor; return null!; }
        public bool TryGet(SessionId id, [NotNullWhen(true)] out Session? session) { session = null; return false; }
        public IReadOnlyCollection<Session> Active => [];
        public IDisposable Attach(ISessionChannel channel) { Channel = channel; return new Detach(); }
        public ValueTask CloseAsync(SessionId id, string reason) => ValueTask.CompletedTask;
        public event Action<SessionLifecycleEvent>? Lifecycle { add { } remove { } }

        private sealed class Detach : IDisposable { public void Dispose() { } }
    }

    internal sealed class MemoryDescriptions : ISessionDescriptions
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? For(SessionDescriptor? session) => session is null ? null : Values.GetValueOrDefault(session.MemoryGroup);
        public bool Durable => true;
        public ValueTask SetAsync(string group, string? description, CancellationToken ct)
        {
            if (description is null) Values.Remove(group); else Values[group] = description;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Rig(
        CodingSurface Surface, FakeClaude Claude, RecordingKernel Kernel, CapturingRegistry Registry,
        EventBus Bus, PermissionBroker Broker, MemoryDescriptions Descriptions, CodingWorkspace Workspace);

    private static async Task<Rig> StartAsync(int maxUnattended = 20, bool selfHosted = false)
    {
        FakeClaude claude = new();

        CodingWorkspace workspace = new()
        {
            Id = "self", Name = "Lane", Path = "/w", SelfHosted = selfHosted,
            Claude = claude, Git = new GitRunner("/w", "Lane <lane@localhost>", null)
        };

        RecordingKernel kernel = new();
        CapturingRegistry registry = new();
        EventBus bus = new();
        PermissionBroker broker = new(TimeSpan.FromMinutes(1));
        MemoryDescriptions descriptions = new();

        CodingSurface surface = new(
            workspace, new CodingOptions { MaxUnattendedExchanges = maxUnattended },
            kernel, registry, descriptions, bus, broker, restart: null, NullLogger.Instance);

        workspace.Surface = surface;

        await surface.StartAsync(TestContext.Current.CancellationToken);

        return new Rig(surface, claude, kernel, registry, bus, broker, descriptions, workspace);
    }

    private static async Task<T> Eventually<T>(Func<T?> probe) where T : class
    {
        for (int i = 0; i < 100; i++)
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }

        throw new TimeoutException("Nothing arrived.");
    }

    [Fact]
    public async Task The_session_is_direct_tagged_and_described()
    {
        Rig rig = await StartAsync(selfHosted: true);

        SessionDescriptor descriptor = rig.Registry.Descriptor!;

        Assert.True(descriptor.IsDirect);
        Assert.Equal("coding.self/Text/claude", descriptor.Id.Value);
        Assert.Equal("self", descriptor.Tags[CodingSurface.WorkspaceTag]);
        Assert.True(descriptor.Tags.ContainsKey(CodingSurface.SelfTag));
        Assert.Contains("your own source code", rig.Descriptions.Values["coding/self"]);
    }

    [Fact]
    public async Task A_reply_split_across_lines_reaches_claude_as_one_message_when_the_turn_ends()
    {
        Rig rig = await StartAsync();
        ITextOutput output = Assert.IsAssignableFrom<ITextOutput>(rig.Registry.Channel);

        await output.SendAsync(new OutboundText("Add a test."), TestContext.Current.CancellationToken);
        await output.SendAsync(new OutboundText("Then build."), TestContext.Current.CancellationToken);

        Assert.Empty(rig.Claude.Sent);

        rig.Bus.Publish(new TurnCompleted(rig.Workspace.SessionId, TurnKind.Respond, false, 0, TimeSpan.Zero));

        string sent = await Eventually(() => rig.Claude.Sent.FirstOrDefault());

        Assert.Equal("Add a test.\nThen build.", sent);
    }

    [Fact]
    public async Task Lane_is_told_when_claude_code_cannot_start()
    {
        Rig rig = await StartAsync();
        rig.Claude.Failure = new InvalidOperationException("claude: not found");

        ITextOutput output = Assert.IsAssignableFrom<ITextOutput>(rig.Registry.Channel);
        await output.SendAsync(new OutboundText("Build it."), TestContext.Current.CancellationToken);
        rig.Bus.Publish(new TurnCompleted(rig.Workspace.SessionId, TurnKind.Respond, false, 0, TimeSpan.Zero));

        InboundEvent evt = await Eventually(() => rig.Kernel.Submitted.FirstOrDefault());

        Assert.Equal("[Claude Code could not be started: claude: not found]", evt.Message.TextContent);
        Assert.False(evt.RequiresResponse);
    }

    [Fact]
    public async Task Claude_replies_come_back_as_messages_from_claude_code()
    {
        Rig rig = await StartAsync();

        rig.Claude.Reply("Done; it builds.");

        InboundEvent evt = await Eventually(() => rig.Kernel.Submitted.FirstOrDefault());

        Assert.Equal("Claude Code", evt.Author.DisplayName);
        Assert.Equal(rig.Workspace.SessionId, evt.Session);
        Assert.StartsWith("Done; it builds.", evt.Message.TextContent);
        Assert.Contains("ran `dotnet build`", evt.Message.TextContent);
        Assert.Contains("$0.25 so far", evt.Message.TextContent);
    }

    [Fact]
    public async Task Claude_codes_tokens_are_reported_so_energy_pays_for_them()
    {
        Rig rig = await StartAsync();

        List<TokenUsageEvent> reported = [];
        using IDisposable _ = rig.Bus.Subscribe<TokenUsageEvent>(reported.Add);

        TokenUsage usage = new(10, 47, 28_000, 300, "claude-code", TimeSpan.FromSeconds(3));

        rig.Claude.Reply("Done.", usage);
        rig.Claude.Reply("Interrupted, so nothing was spent.");

        TokenUsageEvent evt = Assert.Single(reported);

        Assert.Equal("claude-code", evt.ModelInstanceId);
        Assert.Equal("coding", evt.Role);
        Assert.Equal(usage, evt.Usage);
    }

    [Fact]
    public async Task Too_many_unattended_exchanges_hold_the_reply_until_someone_speaks()
    {
        Rig rig = await StartAsync(maxUnattended: 2);

        rig.Claude.Reply("one");
        rig.Claude.Reply("two");
        rig.Claude.Reply("three");
        rig.Claude.Reply("four");

        await Eventually(() => rig.Kernel.Submitted.Count >= 3 ? rig.Kernel.Submitted : null);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        string[] texts;
        lock (rig.Kernel.Submitted) texts = [.. rig.Kernel.Submitted.Select(e => e.Message.TextContent)];

        Assert.Equal(3, texts.Length);
        Assert.StartsWith("one", texts[0]);
        Assert.StartsWith("two", texts[1]);
        Assert.StartsWith("[Paused", texts[2]);
        Assert.False(rig.Kernel.Submitted[2].RequiresResponse);
        Assert.True(rig.Surface.Paused);

        rig.Surface.Resume();

        InboundEvent released = await Eventually(() => rig.Kernel.Submitted.Count >= 4 ? rig.Kernel.Submitted[3] : null);

        Assert.StartsWith("three\n\nfour", released.Message.TextContent);
        Assert.False(rig.Surface.Paused);
    }

    [Fact]
    public async Task The_monologue_speaking_in_counts_as_attention()
    {
        Rig rig = await StartAsync(maxUnattended: 1);

        rig.Claude.Reply("one");
        rig.Claude.Reply("two");

        await Eventually(() => rig.Surface.Paused ? rig : null);

        ITextOutput output = Assert.IsAssignableFrom<ITextOutput>(rig.Registry.Channel);
        await output.SendAsync(new OutboundText("Carry on."), TestContext.Current.CancellationToken);
        rig.Bus.Publish(new TurnCompleted(rig.Workspace.SessionId, TurnKind.Directive, false, 0, TimeSpan.Zero));

        await Eventually(() => rig.Claude.Sent.FirstOrDefault());

        Assert.False(rig.Surface.Paused);
        Assert.Contains(rig.Kernel.Submitted, e => e.Message.TextContent.StartsWith("two", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_permission_request_is_put_to_lane_with_its_id()
    {
        Rig rig = await StartAsync();

        Task<PermissionVerdict> pending = rig.Broker.RequestAsync(
            "self", "Bash", JsonSerializer.SerializeToElement(new { command = "make install" }), TestContext.Current.CancellationToken);

        InboundEvent evt = await Eventually(() => rig.Kernel.Submitted.FirstOrDefault());
        PermissionRequest request = Assert.Single(rig.Broker.PendingFor("self"));

        Assert.Contains($"[Permission request {request.Id}]", evt.Message.TextContent);
        Assert.Contains("`make install`", evt.Message.TextContent);

        rig.Broker.Answer("self", request.Id, allow: false, reason: "too broad");
        Assert.False((await pending).Allow);

        InboundEvent resolved = await Eventually(() => rig.Kernel.Submitted.Count >= 2 ? rig.Kernel.Submitted[1] : null);

        Assert.Equal($"[Permission request {request.Id}: denied (too broad).]", resolved.Message.TextContent);
        Assert.False(resolved.RequiresResponse);
    }
}

public sealed class CodingWorkspaceManagerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lane-workspaces-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Http : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private readonly IKeyValueStore _store = new SqliteKeyValueStore(new LaneDatabase(
        new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance));

    private CodingWorkspaceManager Manager(CodingOptions options)
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IAgentKernel, CodingSurfaceTests.RecordingKernel>()
            .AddSingleton<ISessionRegistry, CodingSurfaceTests.CapturingRegistry>()
            .AddSingleton<ISessionDescriptions, CodingSurfaceTests.MemoryDescriptions>()
            .AddSingleton<IEventBus, EventBus>()
            .BuildServiceProvider();

        return new CodingWorkspaceManager(
            options, services, _store, new PermissionBroker(TimeSpan.FromMinutes(1)),
            permissions: null, new Http(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task A_project_is_created_once_survives_a_restart_and_reopens_by_name_after_closing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        CodingOptions options = new() { WorkspacesRoot = _root, GitAuthor = "Lane <lane@example.com>" };

        await using CodingWorkspaceManager first = Manager(options);

        CodingWorkspaceOpening opening = await first.OpenAsync("A Tiny Game", "A game about tiny things.", "jahan/tiny-game", ct);
        CodingWorkspace opened = opening.Workspace;

        Assert.Equal(OpeningOutcome.Created, opening.Outcome);

        Assert.Equal("a-tiny-game", opened.Id);
        Assert.Equal(Path.Combine(_root, "a-tiny-game"), opened.Path);
        Assert.Equal("coding.a-tiny-game/Text/claude", opened.SessionId.Value);
        Assert.Contains("A game about tiny things.", await File.ReadAllTextAsync(Path.Combine(opened.Path, "README.md"), ct));

        ProcessResult log = await opened.Git.RunAsync(ct, "log", "--format=%an %s");
        Assert.Equal("Lane Start A Tiny Game", log.Output.Trim());
        Assert.Equal("https://github.com/jahan/tiny-game.git", await opened.Git.OriginAsync(ct));

        CodingWorkspaceOpening again = await first.OpenAsync("a tiny game", "Something else.", null, ct);
        Assert.Equal(OpeningOutcome.AlreadyOpen, again.Outcome);
        Assert.Same(opened, again.Workspace);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Manager(options).OpenAsync("x", "y", "not a repo", ct));

        await using CodingWorkspaceManager second = Manager(options);
        await second.StartAsync(ct);

        CodingWorkspace? reopened = second.Find("a-tiny-game");
        Assert.NotNull(reopened);
        Assert.True(reopened.Dynamic);
        Assert.Equal("A game about tiny things.", reopened.Purpose);

        Assert.True(await second.CloseAsync("a-tiny-game", ct));
        Assert.True(Directory.Exists(opened.Path));

        await using CodingWorkspaceManager third = Manager(options);
        await third.StartAsync(ct);

        Assert.Null(third.Find("a-tiny-game"));

        CodingWorkspaceOpening back = await third.OpenAsync("A Tiny Game", "A game about tiny things.", null, ct);

        Assert.Equal(OpeningOutcome.Reopened, back.Outcome);
        Assert.Equal("Lane Start A Tiny Game", (await back.Workspace.Git.RunAsync(ct, "log", "--format=%an %s")).Output.Trim());
    }

    [Fact]
    public async Task A_non_empty_directory_that_is_not_a_repository_is_left_alone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        Directory.CreateDirectory(Path.Combine(_root, "notes"));
        await File.WriteAllTextAsync(Path.Combine(_root, "notes", "todo.txt"), "mine", ct);

        await using CodingWorkspaceManager manager = Manager(new CodingOptions { WorkspacesRoot = _root });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.OpenAsync("Notes", "x", null, ct));
    }

    [Fact]
    public async Task A_configured_workspace_cannot_be_closed_and_a_bad_one_is_skipped()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        CodingOptions options = new()
        {
            WorkspacesRoot = _root,
            Workspaces =
            [
                new CodingWorkspaceOptions { Id = "self", Path = _root, SelfHosted = true },
                new CodingWorkspaceOptions { Id = "gone", Path = Path.Combine(_root, "missing") },
                new CodingWorkspaceOptions { Id = "bad id!", Path = _root }
            ]
        };

        await using CodingWorkspaceManager manager = Manager(options);
        await manager.StartAsync(ct);

        Assert.Equal(["self"], manager.All.Select(w => w.Id));
        Assert.True(manager.TryGet(new SurfaceId("coding.self"), out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CloseAsync("self", ct));
    }
}

public sealed class OpenCodingSurfaceToolTests
{
    private sealed class FakeWorkspaces : ICodingWorkspaces
    {
        public List<string> Opened { get; } = [];

        public IReadOnlyCollection<CodingWorkspace> All => [];
        public bool TryGet(SurfaceId surface, [NotNullWhen(true)] out CodingWorkspace? workspace) { workspace = null; return false; }
        public CodingWorkspace? Find(string id) => null;
        public Task<bool> CloseAsync(string id, CancellationToken ct) => Task.FromResult(true);
        public void NoteHumanActivity(SessionId session) { }
        public IReadOnlyList<PermissionRequest> PendingPermissions(string id) => [];

        public Task<CodingWorkspaceOpening> OpenAsync(string name, string purpose, string? gitHubRepo, CancellationToken ct)
        {
            Opened.Add(name);

            CodingWorkspace workspace = new()
            {
                Id = "game", Name = name, Path = "/w/game",
                Claude = new CodingSurfaceTests.RecordingClaude(), Git = new GitRunner("/w/game", "Lane", null)
            };

            return Task.FromResult(new CodingWorkspaceOpening(workspace, OpeningOutcome.Created));
        }
    }

    private static readonly SurfaceId Discord = new("discord.main");

    private static (ITool Tool, ToolContext Context, FakeWorkspaces Workspaces, CodingSurfaceTests.RecordingKernel Kernel) Build(
        TurnKind turn, string? requester, params string[] openers)
    {
        FakeWorkspaces workspaces = new();
        CodingSurfaceTests.RecordingKernel kernel = new();

        ServiceProvider services = new ServiceCollection()
            .AddSingleton<ICodingWorkspaces>(workspaces)
            .AddSingleton<IAgentKernel>(kernel)
            .AddSingleton(new CodingOptions { Openers = [.. openers] })
            .BuildServiceProvider();

        ToolContext context = new()
        {
            Turn      = turn,
            Services  = services,
            Requester = requester is null ? null : new Participant(new ParticipantId(Discord, "1"), requester, requester)
        };

        return (new OpenCodingSurfaceTool(), context, workspaces, kernel);
    }

    private static ValueTask<ToolResult> Invoke(ITool tool, ToolContext context, object args) =>
        tool.InvokeAsync(new ToolInvocation("c1", JsonSerializer.SerializeToElement(args), context), TestContext.Current.CancellationToken);

    [Fact]
    public void It_is_offered_in_replies_as_well_as_the_monologue()
    {
        TurnKind allowed = new OpenCodingSurfaceTool().Descriptor.Availability.AllowedTurns;

        Assert.True(allowed.HasFlag(TurnKind.Respond));
        Assert.True(allowed.HasFlag(TurnKind.Monologue));
    }

    [Fact]
    public async Task In_a_reply_only_listed_openers_can_have_a_project_opened()
    {
        (ITool tool, ToolContext context, FakeWorkspaces workspaces, _) = Build(TurnKind.Respond, "stranger", "jahan");

        ToolResult result = await Invoke(tool, context, new { name = "Game", purpose = "fun" });

        Assert.True(result.IsError);
        Assert.Contains("Openers", result.Text);
        Assert.Empty(workspaces.Opened);
    }

    [Fact]
    public async Task An_opener_gets_the_project_and_the_brief_is_sent_to_claude_code()
    {
        (ITool tool, ToolContext context, FakeWorkspaces workspaces, CodingSurfaceTests.RecordingKernel kernel) =
            Build(TurnKind.Respond, "jahan", "jahan");

        ToolResult result = await Invoke(tool, context, new { name = "Game", purpose = "fun", brief = "Make a snake game." });

        Assert.False(result.IsError, result.Text);
        Assert.Equal(["Game"], workspaces.Opened);

        (SessionId session, SessionWorkItem item) = Assert.Single(kernel.Posted);

        Assert.Equal("coding.game/Text/claude", session.Value);
        Assert.Equal("Make a snake game.", Assert.IsType<SessionWorkItem.Speak>(item).Text);
    }

    [Fact]
    public async Task The_monologue_needs_no_permission()
    {
        (ITool tool, ToolContext context, FakeWorkspaces workspaces, _) = Build(TurnKind.Monologue, requester: null);

        ToolResult result = await Invoke(tool, context, new { name = "Game", purpose = "fun" });

        Assert.False(result.IsError, result.Text);
        Assert.Equal(["Game"], workspaces.Opened);
    }
}

public sealed class ClaudeCodeProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lane-claude-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class MemorySessionStore : IClaudeSessionStore
    {
        public string? Value { get; set; }
        public ValueTask<string?> LoadAsync(CancellationToken ct) => ValueTask.FromResult(Value);
        public ValueTask SaveAsync(string? sessionId, CancellationToken ct) { Value = sessionId; return ValueTask.CompletedTask; }
    }

    /// <summary>A stand-in for claude that answers each stream-json line with one tool call and a result.</summary>
    private string FakeClaude()
    {
        string path = Path.Combine(_dir, "fake-claude");

        File.WriteAllText(path, """
            #!/bin/bash
            sid=""
            while [ $# -gt 0 ]; do case "$1" in --session-id|--resume) sid="$2"; shift 2;; *) shift;; esac; done
            echo "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"$sid\"}"
            n=0
            while IFS= read -r line; do
              n=$((n+1))
              echo "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"echo $n\"}}]},\"parent_tool_use_id\":null}"
              echo "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"reply $n\",\"total_cost_usd\":0.5,\"session_id\":\"$sid\",\"queued_turn_count\":0}"
            done
            """.ReplaceLineEndings("\n"));

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    [Fact]
    public void The_command_line_wires_permissions_through_lane()
    {
        ClaudeCodeProcess process = new(new ClaudeLaunchSettings
        {
            ClaudePath = "claude", WorkingDirectory = _dir,
            AutoAllow = ["Read", "Bash(dotnet build *)"], AlwaysDeny = ["Bash(git push *)"],
            McpConfig = () => "{}", MaxBudgetUsd = 2.5m
        }, new MemorySessionStore(), NullLogger.Instance);

        IReadOnlyList<string> fresh  = process.Arguments(resume: null, sessionId: "new-id");
        IReadOnlyList<string> resume = process.Arguments(resume: "old-id", sessionId: "new-id");

        Assert.Equal("new-id", fresh[fresh.ToList().IndexOf("--session-id") + 1]);
        Assert.Equal("old-id", resume[resume.ToList().IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--session-id", resume);

        Assert.Equal("Read,Bash(dotnet build *)", fresh[fresh.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Equal("mcp__lane__permission_prompt", fresh[fresh.ToList().IndexOf("--permission-prompt-tool") + 1]);
        Assert.Contains("--strict-mcp-config", fresh);
        Assert.Equal("2.5", fresh[fresh.ToList().IndexOf("--max-budget-usd") + 1]);
    }

    [Fact]
    public async Task Each_message_gets_a_turn_and_the_session_survives_a_restart_of_the_process()
    {
        if (OperatingSystem.IsWindows()) return;

        MemorySessionStore store = new();
        ClaudeLaunchSettings settings = new() { ClaudePath = FakeClaude(), WorkingDirectory = _dir };

        List<ClaudeTurn> turns = [];
        TaskCompletionSource<bool> second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (ClaudeCodeProcess process = new(settings, store, NullLogger.Instance))
        {
            process.TurnCompleted += t => { lock (turns) { turns.Add(t); if (turns.Count == 2) second.TrySetResult(true); } };

            await process.SendAsync("first", TestContext.Current.CancellationToken);
            await process.SendAsync("second", TestContext.Current.CancellationToken);

            await second.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(["reply 1", "reply 2"], turns.Select(t => t.Text));
            Assert.Equal(["ran `echo 1`"], turns[0].Activity);
            Assert.Equal(0.5m, process.TotalCostUsd);
            Assert.False(process.Busy);
        }

        string? session = store.Value;
        Assert.False(string.IsNullOrEmpty(session));

        TaskCompletionSource<ClaudeTurn> resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using ClaudeCodeProcess again = new(settings, store, NullLogger.Instance);
        again.TurnCompleted += t => resumed.TrySetResult(t);

        await again.SendAsync("third", TestContext.Current.CancellationToken);
        await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(session, again.SessionId);
    }
}

public sealed class GitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lane-git-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_token_travels_in_the_environment_not_the_arguments()
    {
        GitRunner git = new(_dir, "Lane <lane@example.com>", "ghp_secret");

        IReadOnlyDictionary<string, string?> env = git.Environment();

        string header = env["GIT_CONFIG_VALUE_0"]!;
        string encoded = header["AUTHORIZATION: basic ".Length..];

        Assert.Equal("x-access-token:ghp_secret", Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.Equal("http.https://github.com/.extraheader", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Lane", env["GIT_AUTHOR_NAME"]);
        Assert.Equal("lane@example.com", env["GIT_COMMITTER_EMAIL"]);

        Assert.False(new GitRunner(_dir, "Lane", null).Environment().ContainsKey("GIT_CONFIG_COUNT"));
    }

    [Fact]
    public async Task Commits_are_made_as_lane()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        GitRunner git = new(_dir, "Lane <lane@example.com>", null);

        Assert.True((await git.RunAsync(ct, "init", "-b", "main")).Succeeded);

        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "hello", ct);

        Assert.True((await git.RunAsync(ct, "add", "-A")).Succeeded);
        Assert.True((await git.RunAsync(ct, "commit", "-m", "First")).Succeeded);

        ProcessResult log = await git.RunAsync(ct, "log", "-1", "--format=%an <%ae> %s");

        Assert.Equal("Lane <lane@example.com> First", log.Output.Trim());
        Assert.Null(await git.OriginAsync(ct));
    }

    [Theory]
    [InlineData("https://github.com/jahan/lane.git", "jahan/lane")]
    [InlineData("https://github.com/jahan/lane", "jahan/lane")]
    [InlineData("git@github.com:jahan/lane.git", "jahan/lane")]
    [InlineData("ssh://git@github.com/jahan/my.repo.git", "jahan/my.repo")]
    [InlineData("https://gitlab.com/jahan/lane.git", null)]
    [InlineData(null, null)]
    public void GitHub_remotes_are_recognised(string? url, string? expected) =>
        Assert.Equal(expected, GitHubClient.ParseRemote(url)?.ToString());

    [Theory]
    [InlineData("A Tiny Game!", "a-tiny-game")]
    [InlineData("   ", "workspace")]
    [InlineData("Ünïcode Things", "n-code-things")]
    public void Workspace_names_become_directory_slugs(string name, string expected) =>
        Assert.Equal(expected, CodingWorkspaceManager.Slug(name));
}

public sealed class GitHubClientTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static readonly GitHubRepository Repo = new("jahan", "lane");

    [Fact]
    public async Task Listing_skips_pull_requests_and_sends_the_token()
    {
        Handler handler = new(HttpStatusCode.OK, """
            [
              {"number":3,"state":"open","title":"Crash on start","labels":[{"name":"bug"}],"comments":2},
              {"number":4,"state":"open","title":"A pull request","labels":[],"comments":0,"pull_request":{}}
            ]
            """);

        GitHubClient client = new(new HttpClient(handler), "ghp_x");

        string text = await client.ListIssuesAsync(Repo, "open", "bug", 10, TestContext.Current.CancellationToken);

        Assert.Equal("#3 [open] Crash on start (bug) — 2 comment(s)", text);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_x", request.Headers.Authorization?.Parameter);
        Assert.Contains("labels=bug", request.RequestUri!.Query);
    }

    [Fact]
    public async Task A_missing_repository_is_explained()
    {
        GitHubClient client = new(new HttpClient(new Handler(HttpStatusCode.NotFound, "{}")), "ghp_x");

        GitHubException ex = await Assert.ThrowsAsync<GitHubException>(() =>
            client.ListIssuesAsync(Repo, "open", null, 10, TestContext.Current.CancellationToken));

        Assert.Contains("not found", ex.Message);
    }
}

public sealed class CodingConfigurationTests
{
    private static IConfigurationSection Section(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build()
            .GetSection("Coding");

    [Fact]
    public void Omitted_rule_lists_take_their_defaults_and_empty_ones_are_taken_at_their_word()
    {
        CodingOptions omitted = LaneHostBuilderExtensions.ReadCodingOptions(Section("""{"Coding":{"Enabled":true}}"""));

        Assert.Equal(CodingOptions.DefaultAutoAllow, omitted.AutoAllow);
        Assert.Equal(CodingOptions.DefaultAlwaysDeny, omitted.AlwaysDeny);

        CodingOptions emptied = LaneHostBuilderExtensions.ReadCodingOptions(
            Section("""{"Coding":{"AutoAllow":[],"AlwaysDeny":["Bash(sudo *)"]}}"""));

        Assert.Empty(emptied.AutoAllow);
        Assert.Equal(["Bash(sudo *)"], emptied.AlwaysDeny);
    }

    [Fact]
    public void The_shipped_configuration_keeps_coding_off_and_names_lanes_own_source()
    {
        IConfigurationSection section = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build()
            .GetSection("Lane:Coding");

        CodingOptions options = LaneHostBuilderExtensions.ReadCodingOptions(section);

        Assert.False(options.Enabled);
        Assert.Contains(options.Workspaces, w => w.SelfHosted);
        Assert.Contains("Bash(git push *)", options.AlwaysDeny);
    }
}
