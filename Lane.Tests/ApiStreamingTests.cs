using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Surfaces.Api.Streaming;
using Lane.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Watching a reply arrive rather than waiting for it.
///
/// This is what makes a client app feel like a conversation instead of a form submission,
/// and it is the same streaming path the voice observer uses — so it also proves a turn can
/// be watched by two things at once.
/// </summary>
public sealed class ApiStreamingTests
{
    [Fact]
    public async Task A_streamed_reply_arrives_in_pieces_and_then_whole()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Echoing("this reply arrives in several pieces"));

        List<(string Event, JsonElement Data)> frames =
            await ReadStreamAsync(api.Client(), "main", "say something");

        // Accepted first, so the client knows its message landed before anything else happens.
        Assert.Equal("accepted", frames[0].Event);

        List<string> deltas = [.. frames.Where(f => f.Event == "delta")
                                        .Select(f => f.Data.GetProperty("text").GetString()!)];

        Assert.NotEmpty(deltas);

        // The pieces, reassembled, are the reply.
        Assert.Equal("this reply arrives in several pieces", string.Concat(deltas).Trim());

        (string Event, JsonElement Data) message = frames.Single(f => f.Event == "message");

        Assert.Equal("this reply arrives in several pieces", message.Data.GetProperty("text").GetString());

        // And the stream ends on its own rather than hanging until a timeout.
        Assert.Equal("done", frames[^1].Event);
        Assert.False(frames[^1].Data.GetProperty("silent").GetBoolean());
    }

    [Fact]
    public async Task A_turn_with_nobody_watching_is_still_streamed_for_the_channel_itself()
    {
        // A channel Lane can write to is an audience of its own: delivery sends each line
        // as it is finished, which it can only do if the reply arrives in pieces.
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("fine");

        await using ApiFixture api = await ApiFixture.StartAsync(model);

        await ApiFixture.SendAsync(api.Client(), "quiet", new { text = "hello" });

        await WaitForTurnAsync(api, "api/Api/alpha/quiet");

        Assert.True(model.StreamedLast);
    }

    [Fact]
    public async Task A_watched_turn_is_streamed()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("fine");

        await using ApiFixture api = await ApiFixture.StartAsync(model);

        await ReadStreamAsync(api.Client(), "watched", "hello");

        Assert.True(model.StreamedLast);
    }

    [Fact]
    public async Task Tool_use_is_visible_while_it_happens()
    {
        // A client showing "searching…" needs to know before the answer arrives, not after.
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Sequence(
                ScriptedLanguageModel.ToolCall("echo", new { text = "hi" }),
                ScriptedLanguageModel.Text("found it")),
            services: s => s.AddSingleton<Lane.Core.Tools.ITool>(new EchoTool()));

        List<(string Event, JsonElement Data)> frames = await ReadStreamAsync(api.Client(), "tools", "look it up");

        List<(string Event, JsonElement Data)> tools = [.. frames.Where(f => f.Event == "tool")];

        Assert.Equal("start", tools[0].Data.GetProperty("phase").GetString());
        Assert.Equal("echo",  tools[0].Data.GetProperty("name").GetString());
        Assert.Equal("end",   tools[1].Data.GetProperty("phase").GetString());
        Assert.False(tools[1].Data.GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task A_thought_volunteered_into_the_session_does_not_end_the_stream()
    {
        // The monologue can speak into any conversation at any moment. Ending a client's
        // stream on that would cut off the reply it is actually waiting for.
        await using ApiFixture api = await ApiFixture.StartAsync();

        TurnStreamHub hub = api.Harness.Services.GetRequiredService<TurnStreamHub>();
        IEventBus bus = api.Harness.Services.GetRequiredService<IEventBus>();

        SessionId session = new(new SurfaceId("api"), SessionKind.Api, "alpha/main");

        List<TurnStreamEvent> seen = [];

        using IDisposable subscription = hub.Subscribe(session, seen.Add);

        bus.Publish(new TurnCompleted(session, TurnKind.Directive, false, 0, TimeSpan.Zero));

        Assert.Empty(seen);

        bus.Publish(new TurnCompleted(session, TurnKind.Respond, false, 0, TimeSpan.Zero));

        Assert.Single(seen.OfType<TurnStreamEvent.Done>());
    }

    [Fact]
    public async Task A_stream_only_carries_its_own_conversation()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        TurnStreamHub hub = api.Harness.Services.GetRequiredService<TurnStreamHub>();

        SessionId mine     = new(new SurfaceId("api"), SessionKind.Api, "alpha/main");
        SessionId somebodys = new(new SurfaceId("discord.main"), SessionKind.Text, "general");

        List<TurnStreamEvent> seen = [];

        using IDisposable subscription = hub.Subscribe(mine, seen.Add);

        hub.Publish(somebodys, new TurnStreamEvent.Delta("not for you"));

        Assert.Empty(seen);
        Assert.False(hub.IsWatched(somebodys));
        Assert.True(hub.IsWatched(mine));
    }

    [Fact]
    public async Task History_is_returned_oldest_first()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Echoing("noted"),
            services: s => s.AddSingleton<ITranscriptStore>(new InMemoryTranscriptStore()));

        HttpClient client = api.Client();

        await ApiFixture.SendAsync(client, "log", new { text = "first" });
        await WaitForTurnAsync(api, "api/Api/alpha/log");

        await ApiFixture.SendAsync(client, "log", new { text = "second" });
        await WaitForTurnAsync(api, "api/Api/alpha/log");

        JsonElement body = await ApiFixture.JsonAsync(await client.GetAsync("/v1/sessions/log/messages"));

        List<string> text = [.. body.GetProperty("messages").EnumerateArray()
                                    .Select(m => m.GetProperty("text").GetString()!)];

        Assert.Equal(["first", "noted", "second", "noted"], text);
    }

    [Fact]
    public async Task A_client_cannot_read_another_clients_history()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(
            services: s => s.AddSingleton<ITranscriptStore>(new InMemoryTranscriptStore()));

        await ApiFixture.SendAsync(api.Client("alpha-key"), "notes", new { text = "something private" });

        await WaitForTurnAsync(api, "api/Api/alpha/notes");

        // Same key name, different client: the history query is scoped by the caller's own
        // namespace, so there is nothing there to read.
        JsonElement body = await ApiFixture.JsonAsync(
            await api.Client("beta-key").GetAsync("/v1/sessions/notes/messages"));

        Assert.Empty(body.GetProperty("messages").EnumerateArray());
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<List<(string Event, JsonElement Data)>> ReadStreamAsync(
        HttpClient client, string key, string text)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/sessions/{key}/messages?stream=true")
        {
            Content = JsonContent.Create(new { text })
        };

        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);

        List<(string, JsonElement)> frames = [];

        string? name = null;
        StringBuilder data = new();

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
                continue;
            }

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');

                data.Append(line["data: ".Length..]);
                continue;
            }

            if (line.Length != 0 || name is null) continue;

            frames.Add((name, JsonDocument.Parse(data.ToString()).RootElement.Clone()));

            if (name is "done" or "error") break;

            name = null;
            data.Clear();
        }

        return frames;
    }

    /// <summary>Waits for the session to go quiet, rather than sleeping and hoping.</summary>
    private static async Task WaitForTurnAsync(ApiFixture api, string sessionId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(20);

            Session? session = api.Harness.Sessions.Active.FirstOrDefault(s => s.Id.Value == sessionId);

            if (session is { State: SessionState.Idle }) return;
        }

        Assert.Fail($"Session {sessionId} never went idle.");
    }

    private sealed class EchoTool : Lane.Core.Tools.ITool
    {
        public Lane.Core.Tools.ToolDescriptor Descriptor { get; } = new()
        {
            Name        = "echo",
            Description = "Repeats what it is given.",
            InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone()
        };

        public ValueTask<Lane.Core.Tools.ToolResult> InvokeAsync(
            Lane.Core.Tools.ToolInvocation invocation, CancellationToken ct) =>
            ValueTask.FromResult(Lane.Core.Tools.ToolResult.Ok("echoed"));
    }

    /// <summary>A transcript that lives only as long as the test.</summary>
    private sealed class InMemoryTranscriptStore : ITranscriptStore
    {
        private readonly List<LaneMessage> _messages = [];
        private readonly Lock _gate = new();

        private long _sequence;

        public ValueTask<long> AppendAsync(LaneMessage message, CancellationToken ct)
        {
            lock (_gate)
            {
                long sequence = ++_sequence;

                _messages.Add(message with { Sequence = sequence });

                return ValueTask.FromResult(sequence);
            }
        }

        public ValueTask<IReadOnlyList<LaneMessage>> ReadAsync(TranscriptQuery query, CancellationToken ct)
        {
            lock (_gate)
            {
                IEnumerable<LaneMessage> found = _messages.Where(m => m.TextContent.Length > 0);

                if (query.Session is not null)
                    found = found.Where(m => m.Session?.Value == query.Session.Value);

                if (query.MemoryGroup is not null)
                    found = found.Where(m => m.Session is not null && Group(m.Session) == query.MemoryGroup);

                found = found.Where(m => m.Sequence > query.AfterSequence);

                found = query.Descending
                    ? found.OrderByDescending(m => m.Sequence).Take(query.Limit)
                    : found.OrderBy(m => m.Sequence).Take(query.Limit);

                return ValueTask.FromResult<IReadOnlyList<LaneMessage>>([.. found]);
            }
        }

        /// <summary>Mirrors how the API surface derives a memory group from a session id.</summary>
        private static string Group(SessionId id) => $"{id.Surface.Value}/{id.LocalKey}";
    }
}
