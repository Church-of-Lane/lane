using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Surfaces.Api;
using Lane.Testing;
using Xunit;

namespace Lane.Tests;

/// <summary>
/// Lane reachable over HTTP by several client applications at once.
///
/// The milestone this covers is not "there is a web server" — it is that two unrelated apps
/// can hold their own conversations through one kernel without ever seeing each other's,
/// which is the same cohesion guarantee Discord and the terminal already rely on.
/// </summary>
public sealed class ApiSurfaceTests
{
    [Fact]
    public async Task Two_client_apps_hold_separate_conversations()
    {
        // Each reply repeats what was said, so a leak between the two would be visible in
        // the text rather than merely inferred from counters.
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Transforming(said => $"heard: {said}"));

        HttpClient alpha = api.Client("alpha-key");
        HttpClient beta  = api.Client("beta-key");

        HttpResponseMessage first  = await ApiFixture.SendAsync(alpha, "main", new { text = "alpha speaking" });
        HttpResponseMessage second = await ApiFixture.SendAsync(beta,  "main", new { text = "beta speaking" });

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);

        JsonElement firstBody  = await ApiFixture.JsonAsync(first);
        JsonElement secondBody = await ApiFixture.JsonAsync(second);

        // Both said "main". They are not the same conversation.
        Assert.NotEqual(firstBody.GetProperty("sessionId").GetString(),
                        secondBody.GetProperty("sessionId").GetString());

        Assert.Contains("alpha/main", firstBody.GetProperty("sessionId").GetString());
        Assert.Contains("beta/main",  secondBody.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task A_client_only_sees_its_own_conversations()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpClient alpha = api.Client("alpha-key");
        HttpClient beta  = api.Client("beta-key");

        await ApiFixture.SendAsync(alpha, "private", new { text = "hello" });
        await ApiFixture.SendAsync(beta,  "private", new { text = "hello" });

        JsonElement listed = await ApiFixture.JsonAsync(await beta.GetAsync("/v1/sessions"));

        Assert.Equal(1, listed.GetArrayLength());
        Assert.Contains("beta/private", listed[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Two_clients_can_meet_in_one_conversation_when_they_ask_to()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpClient alpha = api.Client("alpha-key");
        HttpClient beta  = api.Client("beta-key");

        HttpResponseMessage one = await ApiFixture.SendAsync(alpha, "shared:lounge", new { text = "over here" });
        HttpResponseMessage two = await ApiFixture.SendAsync(beta,  "shared:lounge", new { text = "coming" });

        JsonElement first  = await ApiFixture.JsonAsync(one);
        JsonElement second = await ApiFixture.JsonAsync(two);

        // Sharing is deliberate and looks deliberate, rather than being what happens by
        // accident when two apps pick the same name.
        Assert.Equal(first.GetProperty("sessionId").GetString(), second.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task A_request_without_a_key_is_refused()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpClient anonymous = api.Client(key: null);

        HttpResponseMessage response = await ApiFixture.SendAsync(anonymous, "main", new { text = "let me in" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_the_wrong_key_is_refused()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpResponseMessage response = await ApiFixture.SendAsync(
            api.Client("not-a-real-key"), "main", new { text = "let me in" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_needs_no_key()
    {
        // It exists to be polled by something that has no credentials.
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpResponseMessage response = await api.Client(key: null).GetAsync("/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_client_cannot_speak_as_somebody_from_another_client()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        await ApiFixture.SendAsync(api.Client("beta-key"), "main",
            new { text = "hi", author = new { id = "alpha", name = "Alpha" } });

        await Task.Delay(200);

        // The forged id is namespaced under the client that supplied it, so it can never
        // collide with another client's participants — or with a Discord account.
        Participant author = await FirstAuthorAsync(api, "api/Api/beta/main");

        Assert.Equal("api:beta:alpha", author.Id.ToString());
        Assert.Null(author.GlobalUserId);
    }

    [Fact]
    public async Task Observing_every_conversation_takes_permission()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await api.Client("beta-key").GetAsync("/v1/sessions?all=true")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await api.Client("alpha-key").GetAsync("/v1/sessions?all=true")).StatusCode);
    }

    [Fact]
    public async Task An_observing_client_sees_conversations_on_other_surfaces()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        // A Discord channel, as far as the kernel is concerned.
        api.Harness.OpenSession("discord.main", "general");

        JsonElement listed = await ApiFixture.JsonAsync(
            await api.Client("alpha-key").GetAsync("/v1/sessions?all=true"));

        JsonElement discord = listed.EnumerateArray()
            .Single(s => s.GetProperty("surface").GetString() == "discord.main");

        // Visible, but not this client's to speak into.
        Assert.False(discord.GetProperty("mine").GetBoolean());
    }

    [Fact]
    public async Task A_malformed_session_key_is_rejected_rather_than_used()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        // A key ends up in a session id, a database row and a URL path. Rejecting is much
        // cheaper than escaping it correctly in all three.
        HttpResponseMessage response = await api.Client()
            .PostAsJsonAsync("/v1/sessions", new { key = "../../etc/passwd" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_message_is_rejected()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpResponseMessage response = await ApiFixture.SendAsync(api.Client(), "main", new { text = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Images_reach_the_model()
    {
        ScriptedLanguageModel model = ScriptedLanguageModel.Echoing("ok");

        await using ApiFixture api = await ApiFixture.StartAsync(model);

        HttpClient client = api.Client();

        byte[] pixels = [1, 2, 3, 4];

        HttpResponseMessage response = await ApiFixture.SendAsync(client, "main", new
        {
            text   = "what is this?",
            images = new object[]
            {
                new { url = "https://example.test/cat.png" },
                new { data = Convert.ToBase64String(pixels), mediaType = "image/png" }
            }
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        List<ImagePart> seen = [];

        for (int attempt = 0; attempt < 150 && seen.Count == 0; attempt++)
        {
            await Task.Delay(20);

            seen = [.. model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.Content).OfType<ImagePart>().Take(2)];
        }

        Assert.Equal(2, seen.Count);
        Assert.Equal(new Uri("https://example.test/cat.png"), seen[0].Url);
        Assert.Equal(pixels, seen[1].Data!.Value.ToArray());
        Assert.Equal("image/png", seen[1].MediaType);
    }

    [Fact]
    public async Task An_image_needs_no_text_beside_it()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpResponseMessage response = await ApiFixture.SendAsync(api.Client(), "main",
            new { images = new[] { new { url = "https://example.test/cat.png" } } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Unusable_images_are_rejected()
    {
        await using ApiFixture api = await ApiFixture.StartAsync(configure: o =>
        {
            o.MaxImages     = 2;
            o.MaxImageBytes = 8;
        });

        HttpClient client = api.Client();

        string small = Convert.ToBase64String(new byte[8]);

        object[] bad =
        [
            new { },
            new { url = "https://example.test/a.png", data = small, mediaType = "image/png" },
            new { url = "file:///etc/passwd" },
            new { url = "cat.png" },
            new { data = small },
            new { data = small, mediaType = "image/svg+xml" },
            new { data = "not base64!", mediaType = "image/png" },
            new { data = Convert.ToBase64String(new byte[9]), mediaType = "image/png" }
        ];

        foreach (object image in bad)
        {
            HttpResponseMessage response = await ApiFixture.SendAsync(client, "main", new { text = "look", images = new[] { image } });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_image", (await ApiFixture.JsonAsync(response)).GetProperty("error").GetString());
        }

        object ok = new { data = small, mediaType = "image/png" };

        Assert.Equal(HttpStatusCode.Accepted,
            (await ApiFixture.SendAsync(client, "main", new { text = "look", images = new[] { ok, ok } })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await ApiFixture.SendAsync(client, "main", new { text = "look", images = new[] { ok, ok, ok } })).StatusCode);
    }

    [Fact]
    public async Task Creating_a_session_describes_it()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpResponseMessage response = await api.Client()
            .PostAsJsonAsync("/v1/sessions", new { key = "work", displayName = "Work chat" });

        JsonElement body = await ApiFixture.JsonAsync(response);

        Assert.Equal("Work chat", body.GetProperty("displayName").GetString());
        Assert.Equal("api/Api/alpha/work", body.GetProperty("id").GetString());
        Assert.True(body.GetProperty("mine").GetBoolean());
    }

    [Fact]
    public async Task Anonymous_access_is_refused_off_loopback()
    {
        // Getting this wrong once puts an unauthenticated agent on the network, so it is a
        // startup failure rather than a warning.
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiFixture.StartAsync(configure: o =>
            {
                o.AllowAnonymous = true;
                o.Urls           = "http://0.0.0.0:5099";
                o.Clients        = [];
            }));

        Assert.Contains("loopback", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("http://127.0.0.1:5080;http://localhost:5081", true)]
    [InlineData("http://0.0.0.0:5080", false)]
    [InlineData("http://*:5080", false)]
    [InlineData("http://127.0.0.1:5080;http://0.0.0.0:5081", false)]
    public void Loopback_detection_never_accepts_a_wildcard(string urls, bool expected) =>
        Assert.Equal(expected, ApiSurface.IsLoopbackOnly(urls));

    [Fact]
    public async Task A_surface_with_no_clients_and_no_anonymous_access_refuses_to_start()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ApiFixture.StartAsync(configure: o => o.Clients = []));

        Assert.Contains("no clients", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_client_apps_and_a_terminal_talk_at_once_without_crossing()
    {
        // The milestone, stated plainly: several unrelated ways of reaching Lane, live in
        // one process, each getting its own answer and nobody else's.
        await using ApiFixture api = await ApiFixture.StartAsync(
            ScriptedLanguageModel.Transforming(said => $"re: {said}"));

        StringWriter terminalOut = new();

        await using Lane.Surfaces.Terminal.TerminalSurface terminal = new(
            new SurfaceId("terminal"),
            api.Harness.Kernel,
            api.Harness.Sessions,
            new Lane.Surfaces.Terminal.TerminalSurfaceOptions
            {
                UserName = "jahan", SessionKey = "local", ExitOnEndOfInput = false, ShowPrompt = false
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Lane.Surfaces.Terminal.TerminalSurface>.Instance,
            input: new StringReader("typed at the keyboard\n"),
            output: terminalOut);

        await terminal.StartAsync(CancellationToken.None);

        HttpClient alpha = api.Client("alpha-key");
        HttpClient beta  = api.Client("beta-key");

        await Task.WhenAll(
            ApiFixture.SendAsync(alpha, "main", new { text = "from alpha" }),
            ApiFixture.SendAsync(beta,  "main", new { text = "from beta" }));

        await WaitUntilQuietAsync(api);

        // Three conversations, three surfaces, one kernel.
        List<string> live = [.. api.Harness.Sessions.Active.Select(s => s.Id.Value).Order()];

        Assert.Contains("api/Api/alpha/main", live);
        Assert.Contains("api/Api/beta/main",  live);
        Assert.Contains("terminal/Text/local", live);

        // The keyboard got its own answer, and neither client's.
        string typed = terminalOut.ToString();

        Assert.Contains("Lane: re: typed at the keyboard", typed);
        Assert.DoesNotContain("from alpha", typed);
        Assert.DoesNotContain("from beta",  typed);
    }

    [Fact]
    public async Task The_event_stream_reports_turns_as_they_happen()
    {
        await using ApiFixture api = await ApiFixture.StartAsync();

        HttpClient client = api.Client();

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));

        Task<List<string>> events = ReadEventNamesAsync(client, stop.Token);

        // Give the subscription time to land before making something happen.
        await Task.Delay(300, stop.Token);

        await ApiFixture.SendAsync(client, "watched", new { text = "hello" });

        List<string> seen = await events;

        Assert.Equal("ready", seen[0]);
        Assert.Contains("turn.started",   seen);
        Assert.Contains("turn.completed", seen);
    }

    private static async Task<List<string>> ReadEventNamesAsync(HttpClient client, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/v1/events", HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(ct));

        List<string> names = [];

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("event: ", StringComparison.Ordinal)) continue;

            names.Add(line["event: ".Length..]);

            if (names.Contains("turn.completed")) break;
        }

        return names;
    }

    private static async Task WaitUntilQuietAsync(ApiFixture api)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            await Task.Delay(20);

            if (api.Harness.Sessions.Active.Count >= 3 &&
                api.Harness.Sessions.Active.All(s => s.State == Lane.Core.Sessions.SessionState.Idle))
                return;
        }

        Assert.Fail("Sessions never settled.");
    }

    private static async Task<Participant> FirstAuthorAsync(ApiFixture api, string sessionId)
    {
        Lane.Core.Sessions.Session session = api.Harness.Sessions.Active
            .Single(s => s.Id.Value == sessionId);

        await Task.Yield();

        return session.Descriptor.KnownParticipants.Single(p => !p.IsLane);
    }
}
