using System.Net.WebSockets;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Nodes;
using Lane.Nodes.Protocol;
using Lane.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lane.Tests;

public sealed class NodeFallbackTests
{
    private static readonly ModelRequest Request = new() { System = [], Messages = [] };

    private static readonly NodesOptions Options = new()
    {
        Enabled        = true,
        AcquireTimeout = TimeSpan.FromMilliseconds(100),
        MaxAttempts    = 1
    };

    private static NodeConnection Node(string name, int maxConcurrency = 1) => new(
        name,
        new NodeHello(name, "p", "m", ModelCapabilities.Tools, maxConcurrency,
            new NodeIdentity(name, NodeProtocol.EcdsaP256Sha256, "")),
        WebSocket.CreateFromStream(Stream.Null, new WebSocketCreationOptions()),
        NullLogger.Instance);

    private static NodeLanguageModel Model(NodePool pool, ILanguageModel? fallback) => new(
        "nodes", "p", ModelCapabilities.Tools, pool, new AcceptAllNodeValidator(), Options,
        NullLogger<NodeLanguageModel>.Instance, fallback: fallback is null ? null : () => fallback);

    [Fact]
    public async Task An_empty_pool_is_answered_by_the_fallback_model()
    {
        ScriptedLanguageModel fallback = ScriptedLanguageModel.Echoing("from default", "default");

        ModelResponse response = await Model(new NodePool(), fallback)
            .CompleteAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal("from default", response.Text);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task The_fallback_answers_without_waiting_out_the_acquire_timeout()
    {
        NodePool pool = new();
        pool.Add(Node("busy"));

        using NodeLease held = await pool.AcquireAsync("p", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        ScriptedLanguageModel fallback = ScriptedLanguageModel.Echoing("from default", "default");

        ModelResponse response = await Model(pool, fallback)
            .CompleteAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal("from default", response.Text);
    }

    [Fact]
    public async Task A_fallback_that_cannot_take_audio_is_not_handed_any()
    {
        SurfaceId surface = new("api");
        SessionId session = new(surface, SessionKind.Voice, "room");

        ModelRequest heard = new()
        {
            System   = [],
            Messages = [LaneMessage.User(session, new Participant(new ParticipantId(surface, "u1"), "alice"),
                [new TextPart("hello"), new AudioPart(new byte[] { 1, 2, 3 }, "wav")], DateTimeOffset.UnixEpoch)]
        };

        ScriptedLanguageModel fallback = ScriptedLanguageModel.Echoing("from default", "default");

        await Model(new NodePool(), fallback).CompleteAsync(heard, TestContext.Current.CancellationToken);

        Assert.Equal("hello", Assert.Single(fallback.Requests).Messages[0].TextContent);
        Assert.DoesNotContain(fallback.Requests[0].Messages[0].Content, p => p is AudioPart);
    }

    [Fact]
    public async Task Without_a_fallback_an_empty_pool_still_reports_it()
    {
        await Assert.ThrowsAsync<NodeUnavailableException>(() =>
            Model(new NodePool(), null).CompleteAsync(Request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_fallback_streams_its_own_events()
    {
        ScriptedLanguageModel fallback = ScriptedLanguageModel.Echoing("from default", "default");

        List<ModelStreamEvent> events = [];

        await foreach (ModelStreamEvent streamed in Model(new NodePool(), fallback)
            .StreamAsync(Request, TestContext.Current.CancellationToken))
            events.Add(streamed);

        Assert.True(fallback.StreamedLast);
        Assert.Contains(events, e => e is ModelStreamEvent.Completed);
    }
}
