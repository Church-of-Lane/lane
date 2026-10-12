using System.Threading.Channels;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Surfaces.Api.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lane.Surfaces.Api.Endpoints;

public static class MessageEndpoints
{
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/v1/sessions/{key}/messages", SendAsync);

    private static async Task<IResult> SendAsync(
        HttpContext context,
        string key,
        SendMessageRequest body,
        ApiSessionMap map,
        IAgentKernel kernel,
        TurnStreamHub hub,
        ApiSurfaceOptions options,
        bool? stream,
        CancellationToken ct)
    {
        ApiClient client = ApiAuth.Require(context);

        if (!ApiKeys.IsValid(key)) return Errors.BadKey(key);

        List<ContentPart> content = [];

        if (!string.IsNullOrWhiteSpace(body?.Text)) content.Add(new TextPart(body.Text.Trim()));

        if (ReadImages(body?.Images, options, content) is { } problem)
            return Results.BadRequest(new ErrorResponse("invalid_image", problem));

        if (content.Count == 0)
            return Results.BadRequest(new ErrorResponse("empty_message", "A message needs some text or an image."));

        if (!await ApiAuth.ChargeAsync(context, client, ct).ConfigureAwait(false)) return ApiAuth.OutOfCredits(client);

        Participant author = ResolveAuthor(client, map.Surface, body.Author);

        ApiSessionEntry entry = map.Ensure(client, key, SessionKind.Api, author);

        LaneMessage message = LaneMessage.User(entry.Id, author, content, DateTimeOffset.UtcNow, body.ExternalId);

        InboundEvent inbound = new()
        {
            Session          = entry.Id,
            Author           = author,
            Message          = message,
            ExternalId       = body.ExternalId,
            RequiresResponse = body.RequiresResponse
        };

        if (stream != true)
        {
            await kernel.SubmitAsync(inbound, ct).ConfigureAwait(false);

            // Accepted, not OK: the reply happens on the session's own pump, and may be
            // coalesced with whatever else is arriving. The client reads it from the stream
            // or from history.
            return Results.Accepted(value: new AcceptedResponse(entry.Id.Value, message.Id.ToString()));
        }

        await StreamReplyAsync(context, inbound, kernel, hub, options, ct).ConfigureAwait(false);

        return Results.Empty;
    }

    /// <summary>
    /// Sends the message and streams the reply back as it is produced.
    ///
    /// Subscribing happens strictly before submitting. That ordering is what makes streaming
    /// work at all: the pipeline asks the hub whether anyone is watching in order to decide
    /// whether to stream from the model, and a subscription that arrived after the turn
    /// started would arrive after that decision was made.
    /// </summary>
    private static async Task StreamReplyAsync(
        HttpContext context,
        InboundEvent inbound,
        IAgentKernel kernel,
        TurnStreamHub hub,
        ApiSurfaceOptions options,
        CancellationToken ct)
    {
        SseWriter.PrepareHeaders(context.Response);

        await using SseWriter writer = new(context.Response);

        Channel<TurnStreamEvent> queue = Channel.CreateUnbounded<TurnStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true });

        using IDisposable subscription = hub.Subscribe(inbound.Session, evt => queue.Writer.TryWrite(evt));

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(options.StreamTimeout);

        await kernel.SubmitAsync(inbound, ct).ConfigureAwait(false);

        // Sent immediately so the client knows the connection is live and its message landed,
        // rather than watching an empty response while the batch window and the model run.
        await writer.SendAsync("accepted", new AcceptedResponse(
            inbound.Session.Value, inbound.Message.Id.ToString()), ct).ConfigureAwait(false);

        try
        {
            await PumpAsync(writer, queue, inbound.Session.Value, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client hung up. The turn carries on; it is the conversation's, not the
            // request's, and its reply is still delivered and still remembered.
        }
        catch (OperationCanceledException)
        {
            await TrySendAsync(writer, "done", new DoneFrame(Silent: false, "timeout")).ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        SseWriter writer, Channel<TurnStreamEvent> queue, string session, CancellationToken ct)
    {
        while (true)
        {
            TurnStreamEvent evt;

            try
            {
                // The keep-alive matters on a slow first token: an idle proxy will close a
                // response that has produced no bytes for long enough.
                evt = await queue.Reader.ReadAsync(ct)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15), ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await writer.KeepAliveAsync(ct).ConfigureAwait(false);
                continue;
            }

            switch (evt)
            {
                case TurnStreamEvent.Delta delta:
                    await writer.SendAsync("delta", new DeltaFrame(delta.Text), ct).ConfigureAwait(false);
                    break;

                case TurnStreamEvent.ToolStarted started:
                    await writer.SendAsync("tool", new ToolFrame(started.Name, "start"), ct).ConfigureAwait(false);
                    break;

                case TurnStreamEvent.ToolFinished finished:
                    await writer.SendAsync("tool", new ToolFrame(finished.Name, "end", finished.IsError), ct)
                                .ConfigureAwait(false);
                    break;

                case TurnStreamEvent.Message spoken:
                    await writer.SendAsync("message", new MessageFrame(
                        session, spoken.Text, spoken.ReplyTo), ct).ConfigureAwait(false);
                    break;

                case TurnStreamEvent.Failed failed:
                    await writer.SendAsync("error", new ErrorFrame(failed.Error), ct).ConfigureAwait(false);
                    return;

                case TurnStreamEvent.Done done:
                    await writer.SendAsync("done", new DoneFrame(done.Silent, done.Reason), ct).ConfigureAwait(false);
                    return;
            }
        }
    }

    private static async Task TrySendAsync<T>(SseWriter writer, string name, T payload)
    {
        try { await writer.SendAsync(name, payload, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* the client is already gone */ }
    }

    /// <summary>Appends each image to <paramref name="content"/>. Returns what is wrong with the first unusable one.</summary>
    private static string? ReadImages(IReadOnlyList<ApiImage>? images, ApiSurfaceOptions options, List<ContentPart> content)
    {
        if (images is not { Count: > 0 }) return null;

        if (images.Count > options.MaxImages) return $"A message can carry at most {options.MaxImages} images.";

        foreach (ApiImage? image in images)
        {
            bool hasUrl  = !string.IsNullOrWhiteSpace(image?.Url);
            bool hasData = !string.IsNullOrWhiteSpace(image?.Data);

            if (image is null || hasUrl == hasData) return "Each image needs either a url or data, not both.";

            string? type = image.MediaType?.Trim().ToLowerInvariant();

            if (type is not null && !ImageTypes.Contains(type))
                return $"Image media types are {string.Join(", ", ImageTypes)}.";

            if (hasUrl)
            {
                if (!Uri.TryCreate(image.Url!.Trim(), UriKind.Absolute, out Uri? url) ||
                    (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                    return "Image urls must be absolute http or https links.";

                content.Add(new ImagePart(url, null, type ?? "image/*"));
                continue;
            }

            if (type is null) return "An image sent as data needs a mediaType.";

            string encoded = image.Data!.Trim();

            if (encoded.Length / 4 * 3 > options.MaxImageBytes + 2) return TooLarge(options);

            byte[] bytes;

            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { return "Image data must be base64."; }

            if (bytes.Length == 0) return "Image data must be base64.";

            if (bytes.Length > options.MaxImageBytes) return TooLarge(options);

            content.Add(new ImagePart(null, bytes, type));
        }

        return null;
    }

    private static string TooLarge(ApiSurfaceOptions options) =>
        $"Images sent as data can be at most {options.MaxImageBytes} bytes.";

    /// <summary>
    /// Who is speaking.
    ///
    /// A client relaying several people can name them, but only inside its own namespace:
    /// the id it supplies is prefixed with its own client id, so possessing one key can
    /// never produce a participant belonging to another client — or to Discord.
    /// </summary>
    private static Participant ResolveAuthor(ApiClient client, SurfaceId surface, ApiAuthor? author)
    {
        if (author is null || string.IsNullOrWhiteSpace(author.Id)) return client.Participant;

        ParticipantId id = new(surface, $"{client.Id}:{author.Id}");

        return new Participant(id, string.IsNullOrWhiteSpace(author.Name) ? author.Id : author.Name);
    }
}
