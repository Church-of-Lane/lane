using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Testing;
using Lane.Tools.Images;
using System.Text.Json;
using Lane.Core.Memory;
using Lane.Core.Presence;
using Lane.Core.Tools;
using Lane.Memory.Sqlite;
using Lane.Tools.Notes;
using Lane.Tools.Presence;
using Lane.Tools.Reading;
using Lane.Tools.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// The abilities v2 dispatched with an if-chain in the orchestrator, now as ordinary
/// classes that the kernel knows nothing about.
/// </summary>
public sealed class BuiltinToolTests : IDisposable
{
    private readonly string _books = Path.Combine(Path.GetTempPath(), $"lane-books-{Guid.NewGuid():n}");

    public BuiltinToolTests()
    {
        Directory.CreateDirectory(_books);

        File.WriteAllLines(Path.Combine(_books, "sisyphus.txt"),
            ["one must imagine", "sisyphus", "happy", "", "42", "the end"]);

        File.WriteAllText(Path.Combine(_books, "wired.txt"), "hello from the wired");
    }

    private static ToolContext Context() =>
        new() { Services = new ServiceCollection().BuildServiceProvider() };

    private static ValueTask<ToolResult> Invoke(ITool tool, object args) =>
        tool.InvokeAsync(new ToolInvocation("c1", JsonSerializer.SerializeToElement(args), Context()), default);

    private IKeyValueStore Positions() =>
        new SqliteKeyValueStore(new LaneDatabase(
            new SqliteOptions { InMemory = true }, NullLogger<LaneDatabase>.Instance));

    // ---- reading -----------------------------------------------------------

    [Fact]
    public async Task Reading_advances_through_a_book_and_wraps_at_the_end()
    {
        ReadBookTool tool = new(Positions(), new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        // Blank lines and bare page numbers are skipped, as they were in v2.
        Assert.Equal("one must imagine\nsisyphus", (await Invoke(tool, new { title = "sisyphus", lines = 2 })).Text);
        Assert.Equal("happy\nthe end", (await Invoke(tool, new { title = "sisyphus", lines = 2 })).Text);

        // A book she has finished is one she can read again.
        Assert.Equal("one must imagine", (await Invoke(tool, new { title = "sisyphus", lines = 1 })).Text);
    }

    [Fact]
    public async Task Each_book_keeps_its_own_position()
    {
        IKeyValueStore positions = Positions();
        ReadBookTool tool = new(positions, new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        await Invoke(tool, new { title = "sisyphus", lines = 2 });

        Assert.Equal("hello from the wired", (await Invoke(tool, new { title = "wired", lines = 1 })).Text);
        Assert.Equal("happy", (await Invoke(tool, new { title = "sisyphus", lines = 1 })).Text);
    }

    [Fact]
    public async Task What_was_read_is_remembered_globally()
    {
        // Something Lane read belongs to her, not to whichever conversation prompted it.
        ReadBookTool tool = new(Positions(), new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        ToolResult result = await Invoke(tool, new { title = "sisyphus", lines = 1 });

        ToolObservation observation = Assert.Single(result.Observations);
        Assert.Equal(MemoryScopeHint.Global, observation.Scope);
        Assert.Contains("one must imagine", observation.Text);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..\\..\\secrets")]
    public async Task A_title_cannot_escape_the_books_directory(string title)
    {
        // Titles come from the model, so they are treated as untrusted input.
        ReadBookTool tool = new(Positions(), new BookOptions { Directory = _books },
            NullLogger<ReadBookTool>.Instance);

        ToolResult result = await Invoke(tool, new { title, lines = 1 });

        Assert.True(result.IsError);
        Assert.Contains("no book", result.Text);
    }

    [Fact]
    public async Task Listing_books_shows_what_is_on_the_shelf()
    {
        ITool tool = new ListBooksTool(new BookOptions { Directory = _books });

        ToolResult result = await Invoke(tool, new { });

        Assert.Contains("sisyphus", result.Text);
        Assert.Contains("wired", result.Text);
    }

    // ---- scratchpad --------------------------------------------------------

    [Fact]
    public async Task A_note_survives_to_be_read_back()
    {
        IKeyValueStore store = Positions();

        ITool write = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);
        ITool read  = new ReadNoteTool(store);

        await Invoke(write, new { title = "Marlow", text = "Jahan's cuttlefish." });

        Assert.Equal("Jahan's cuttlefish.", (await Invoke(read, new { title = "Marlow" })).Text);
    }

    [Fact]
    public async Task A_title_is_matched_however_it_was_capitalised_or_spaced()
    {
        // She will not write the title back character for character, and a scratchpad that
        // answers "no such note" to a capital letter is worse than no scratchpad.
        IKeyValueStore store = Positions();

        ITool write = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);
        ITool read  = new ReadNoteTool(store);

        await Invoke(write, new { title = "Tide Pools", text = "Rocky shore habitats." });

        Assert.Equal("Rocky shore habitats.", (await Invoke(read, new { title = "  tide   pools " })).Text);
    }

    [Fact]
    public async Task Writing_a_title_again_replaces_it_unless_appending()
    {
        IKeyValueStore store = Positions();

        ITool write = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);
        ITool read  = new ReadNoteTool(store);

        await Invoke(write, new { title = "plan", text = "one" });
        await Invoke(write, new { title = "plan", text = "two" });

        Assert.Equal("two", (await Invoke(read, new { title = "plan" })).Text);

        await Invoke(write, new { title = "plan", text = "three", append = true });

        Assert.Equal("two\nthree", (await Invoke(read, new { title = "plan" })).Text);
    }

    [Fact]
    public async Task Appending_past_the_size_limit_is_refused_rather_than_truncated()
    {
        // A note silently cut in half is one she reads back later and acts on as whole.
        IKeyValueStore store = Positions();

        ITool write = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);
        ITool read  = new ReadNoteTool(store);

        await Invoke(write, new { title = "long", text = new string('x', 3990) });

        ToolResult result = await Invoke(write, new { title = "long", text = new string('y', 100), append = true });

        Assert.True(result.IsError);
        Assert.Equal(new string('x', 3990), (await Invoke(read, new { title = "long" })).Text);
    }

    [Fact]
    public async Task Reading_a_note_that_was_never_written_says_so()
    {
        ToolResult result = await Invoke(new ReadNoteTool(Positions()), new { title = "nothing" });

        Assert.True(result.IsError);
        Assert.Contains("list_notes", result.Text);
    }

    [Fact]
    public async Task An_untitled_note_is_refused()
    {
        IKeyValueStore store = Positions();

        ITool write = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);

        Assert.True((await Invoke(write, new { title = "   ", text = "something" })).IsError);
        Assert.True((await Invoke(write, new { title = "ok", text = "  " })).IsError);

        Assert.Equal("(nothing on your scratchpad)", (await Invoke(new ListNotesTool(store), new { })).Text);
    }

    [Fact]
    public async Task Listing_shows_the_most_recently_written_first()
    {
        IKeyValueStore store = Positions();

        ITool write = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);

        await Invoke(write, new { title = "older", text = "first thing" });
        await Invoke(write, new { title = "newer", text = "second thing\nand more" });

        string listed = (await Invoke(new ListNotesTool(store), new { })).Text;

        Assert.True(listed.IndexOf("newer", StringComparison.Ordinal)
                  < listed.IndexOf("older", StringComparison.Ordinal));

        // A preview, not the note: the list is read to choose what to read next.
        Assert.Contains("second thing", listed);
        Assert.DoesNotContain("and more", listed);
    }

    [Fact]
    public async Task The_scratchpad_fills_up_and_can_be_emptied_again()
    {
        IKeyValueStore store = Positions();

        ITool write  = new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance);
        ITool delete = new DeleteNoteTool(store);

        for (int i = 0; i < 64; i++) await Invoke(write, new { title = $"note {i}", text = "x" });

        // Rewriting one she already has is not a new note, so the limit does not block it.
        Assert.False((await Invoke(write, new { title = "note 3", text = "y" })).IsError);

        ToolResult full = await Invoke(write, new { title = "one too many", text = "x" });

        Assert.True(full.IsError);
        Assert.Contains("delete_note", full.Text);

        Assert.False((await Invoke(delete, new { title = "note 3" })).IsError);
        Assert.False((await Invoke(write, new { title = "one too many", text = "x" })).IsError);
    }

    [Fact]
    public async Task Deleting_a_note_that_is_not_there_is_an_error_not_a_shrug()
    {
        ToolResult result = await Invoke(new DeleteNoteTool(Positions()), new { title = "nothing" });

        Assert.True(result.IsError);
        Assert.Contains("no note", result.Text);
    }

    [Fact]
    public async Task What_was_written_down_is_remembered_globally()
    {
        // Tool traffic is stripped from memory, so without the observation the fact that
        // she wrote anything down is gone by the next turn.
        IKeyValueStore store = Positions();

        ToolResult result = await Invoke(
            new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance),
            new { title = "Marlow", text = "Jahan's cuttlefish." });

        ToolObservation observation = Assert.Single(result.Observations);

        Assert.Equal(MemoryScopeHint.Global, observation.Scope);
        Assert.Contains("Jahan's cuttlefish.", observation.Text);
    }

    [Fact]
    public async Task Notes_are_hers_rather_than_one_conversation_s()
    {
        // The point of a scratchpad is writing in one conversation and reading in the next,
        // or during the monologue, which belongs to no conversation at all.
        IKeyValueStore store = Positions();

        await Invoke(new WriteNoteTool(store, NullLogger<WriteNoteTool>.Instance),
            new { title = "Marlow", text = "Jahan's cuttlefish." });

        Assert.Equal(["note:marlow"], await store.ListKeysAsync(new ScopeKey("global"), "note:", default));
    }

    // ---- presence ----------------------------------------------------------

    [Fact]
    public async Task Setting_an_emoticon_publishes_it()
    {
        RecordingPresence presence = new();
        SetEmoticonTool tool = new(presence);

        ToolResult result = await Invoke(tool, new { emoticon = "( ._.)" });

        Assert.False(result.IsError);
        Assert.Equal("( ._.)", Assert.Single(presence.Published).Emoticon);
    }

    [Fact]
    public async Task An_absurdly_long_emoticon_is_refused()
    {
        RecordingPresence presence = new();
        SetEmoticonTool tool = new(presence);

        ToolResult result = await Invoke(tool, new { emoticon = new string('x', 200) });

        Assert.True(result.IsError);
        Assert.Empty(presence.Published);
    }

    private sealed class RecordingPresence : IPresenceSink
    {
        public List<PresenceChanged> Published { get; } = [];
        public void Publish(PresenceChanged change) => Published.Add(change);
    }

    // ---- web ---------------------------------------------------------------

    [Fact]
    public async Task Search_results_are_formatted_and_remembered_globally()
    {
        StubSearch search = new([new SearchHit("Tide pools", "https://example.test/a", "Rocky shore habitats.")]);

        WebSearchTool tool = new(search);

        ToolResult result = await Invoke(tool, new { query = "tide pools", count = 3 });

        Assert.Contains("Tide pools (https://example.test/a): Rocky shore habitats.", result.Text);
        Assert.Equal(3, search.LastCount);
        Assert.Equal(MemoryScopeHint.Global, Assert.Single(result.Observations).Scope);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 10)]
    public async Task The_result_count_is_clamped_to_something_sensible(int requested, int expected)
    {
        StubSearch search = new([]);
        WebSearchTool tool = new(search);

        await Invoke(tool, new { query = "x", count = requested });

        Assert.Equal(expected, search.LastCount);
    }

    [Fact]
    public async Task An_empty_query_is_refused_before_any_request_is_made()
    {
        StubSearch search = new([]);
        WebSearchTool tool = new(search);

        Assert.True((await Invoke(tool, new { query = "   " })).IsError);
        Assert.Equal(0, search.Searches);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test")]
    public async Task Fetching_refuses_anything_that_is_not_http(string url)
    {
        StubSearch search = new([]);
        FetchUrlTool tool = new(search);

        ToolResult result = await Invoke(tool, new { url });

        Assert.True(result.IsError);
        Assert.Equal(0, search.Fetches);
    }

    private sealed class StubSearch(IReadOnlyList<SearchHit> hits) : IWebSearch
    {
        public int Searches;
        public int Fetches;
        public int LastCount;

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int count, CancellationToken ct)
        {
            Searches++;
            LastCount = count;
            return Task.FromResult(hits);
        }

        public Task<string> FetchAsync(string url, int maxLength, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult("page text");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_books)) Directory.Delete(_books, recursive: true);
    }

    // ---- saved images ------------------------------------------------------

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private sealed class StubHttp(byte[] body) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }

    [Fact]
    public async Task A_saved_image_can_be_listed_viewed_and_thrown_away()
    {
        IKeyValueStore store = Positions();

        SaveImageTool save = new(store, new StubHttp(Png), NullLogger<SaveImageTool>.Instance);

        ToolResult saved = await Invoke(save, new { url = "https://cdn.example/cat.png", name = "Cat", caption = "a cat" });
        Assert.False(saved.IsError, saved.Text);

        Assert.True((await Invoke(save, new { url = "https://cdn.example/cat.png", name = "cat" })).IsError);

        Assert.Contains("Cat (just now): a cat", (await Invoke(new ListImagesTool(store), new { })).Text);

        ToolResult viewed = await Invoke(new ViewImageTool(store), new { name = "  CAT " });
        ImagePart image = Assert.IsType<ImagePart>(viewed.Content[^1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(Png, image.Data!.Value.ToArray());

        Assert.False((await Invoke(new DeleteImageTool(store), new { name = "cat" })).IsError);
        Assert.True((await Invoke(new ViewImageTool(store), new { name = "cat" })).IsError);
        Assert.Empty(await store.ListKeysAsync(new ScopeKey("global"), "image", default));
    }

    private sealed class CapturingChannel(SessionId id)
        : SessionChannelBase(id, id.Surface, ChannelCapabilities.Text | ChannelCapabilities.Images), ITextOutput
    {
        public List<OutboundText> Sent { get; } = [];

        public Task SendAsync(OutboundText text, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_saved_image_can_be_sent_into_the_conversation()
    {
        IKeyValueStore store = Positions();

        SaveImageTool save = new(store, new StubHttp(Png), NullLogger<SaveImageTool>.Instance);
        Assert.False((await Invoke(save, new { url = "https://cdn.example/cat.png", name = "Cat" })).IsError);

        await using LaneHarness harness = LaneHarness.Create();

        SessionId room = new(new SurfaceId("discord.main"), SessionKind.Text, "general");

        harness.Sessions.GetOrCreate(new SessionDescriptor
        {
            Id           = room,
            DisplayName  = "#general",
            MemoryGroup  = "g1",
            Capabilities = ChannelCapabilities.Text | ChannelCapabilities.Images
        });

        CapturingChannel channel = new(room);
        using IDisposable attachment = harness.Sessions.Attach(channel);

        ToolContext context = Context() with { Session = room, Sessions = harness.Sessions, TriggerExternalId = "42" };

        ToolResult sent = await ((ITool)new SendImageTool(store)).InvokeAsync(
            new ToolInvocation("c1", JsonSerializer.SerializeToElement(new { name = "cat", message = "look" }), context), default);

        Assert.False(sent.IsError, sent.Text);

        OutboundText message = Assert.Single(channel.Sent);
        Assert.Equal("look", message.Text);
        Assert.Equal("42", message.ReplyToExternalId);

        ImagePart image = Assert.IsType<ImagePart>(Assert.Single(message.Attachments!));
        Assert.Equal(Png, image.Data!.Value.ToArray());
    }

    [Fact]
    public async Task Something_that_is_not_an_image_is_not_saved()
    {
        IKeyValueStore store = Positions();

        SaveImageTool save = new(store, new StubHttp("<html></html>"u8.ToArray()), NullLogger<SaveImageTool>.Instance);

        Assert.True((await Invoke(save, new { url = "https://example.com/page", name = "page" })).IsError);
        Assert.True((await Invoke(save, new { url = "file:///etc/passwd", name = "passwd" })).IsError);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void Images_are_only_downloaded_from_public_addresses(string address, bool allowed)
    {
        Assert.Equal(allowed, ImageShelf.IsPublic(System.Net.IPAddress.Parse(address)));
    }
}
