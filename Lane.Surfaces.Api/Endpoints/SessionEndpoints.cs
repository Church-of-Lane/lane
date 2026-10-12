using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lane.Surfaces.Api.Endpoints;

public static class SessionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/sessions", Create);
        app.MapGet("/v1/sessions", List);
        app.MapGet("/v1/sessions/{key}/messages", History);
        app.MapPost("/v1/sessions/{key}/cancel", Cancel);
        app.MapDelete("/v1/sessions/{key}", Close);
    }

    private static IResult Create(
        HttpContext context, CreateSessionRequest? body, ApiSessionMap map, ISessionRegistry sessions)
    {
        ApiClient client = ApiAuth.Require(context);

        string key = body?.Key ?? "main";

        if (!ApiKeys.IsValid(key)) return Errors.BadKey(key);

        ApiSessionEntry entry = map.Ensure(
            client, key, SessionKind.Api, client.Participant,
            displayName: body?.DisplayName,
            memoryGroup: body?.MemoryGroup,
            direct: body?.Direct);

        return sessions.TryGet(entry.Id, out Session? session)
            ? Results.Ok(Describe(session, entry.Key, mine: true))
            : Results.Ok(new { id = entry.Id.Value, key = entry.Key });
    }

    /// <summary>
    /// The client's own conversations by default. <c>?all=true</c> widens it to every live
    /// session — Discord channels included — for a client permitted to observe them. That is
    /// a read-only view: posting still goes through this client's own namespace, so seeing a
    /// conversation never confers the ability to speak in it.
    /// </summary>
    private static IResult List(HttpContext context, ISessionRegistry sessions, ApiSessionMap map, bool? all)
    {
        ApiClient client = ApiAuth.Require(context);

        bool everything = all == true;

        if (everything && !client.CanObserveAllSessions)
            return Errors.Forbidden("This client is not permitted to observe other conversations.");

        List<SessionResponse> found = [];

        foreach (Session session in sessions.Active)
        {
            map.TryGet(session.Id, out ApiSessionEntry? entry);

            bool mine = entry is not null && entry.BelongsTo(client);

            if (!mine && !everything) continue;

            found.Add(Describe(session, entry?.Key ?? session.Id.LocalKey, mine));
        }

        return Results.Ok(found.OrderByDescending(s => s.LastActivity).ToList());
    }

    private static async Task<IResult> History(
        HttpContext context,
        string key,
        ApiSessionMap map,
        ITranscriptStore transcript,
        ApiSurfaceOptions options,
        int? limit,
        long? after,
        string? scope,
        CancellationToken ct)
    {
        ApiClient client = ApiAuth.Require(context);

        if (!ApiKeys.IsValid(key)) return Errors.BadKey(key);

        SessionId id = map.IdFor(client, key);

        // By memory group by default, so a client that has been talking and speaking sees
        // one conversation rather than two halves of it — the same thing Lane remembers.
        bool sessionOnly = string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase);

        TranscriptQuery query = new()
        {
            Session       = sessionOnly ? id : null,
            MemoryGroup   = sessionOnly ? null : map.MemoryGroupFor(client, key),
            AfterSequence = after ?? 0,
            Limit         = Math.Clamp(limit ?? 50, 1, options.MaxHistory),
            Descending    = after is null
        };

        IReadOnlyList<LaneMessage> messages = await transcript.ReadAsync(query, ct).ConfigureAwait(false);

        // Always handed back oldest-first, whichever direction it was read in: a client
        // appending to a transcript should not have to know how paging worked.
        List<MessageResponse> ordered = [.. messages
            .OrderBy(m => m.Sequence)
            .ThenBy(m => m.Timestamp)
            .Select(ToResponse)];

        return Results.Ok(new HistoryResponse(id.Value, ordered));
    }

    /// <summary>Barge-in over HTTP: stop whatever she is in the middle of saying here.</summary>
    private static async Task<IResult> Cancel(HttpContext context, string key, ApiSessionMap map, IAgentKernel kernel)
    {
        ApiClient client = ApiAuth.Require(context);

        if (!ApiKeys.IsValid(key)) return Errors.BadKey(key);

        bool text  = await kernel.CancelTurnAsync(map.IdFor(client, key), "client cancelled").ConfigureAwait(false);
        bool voice = await kernel.CancelTurnAsync(
            map.IdFor(client, key, SessionKind.Voice), "client cancelled").ConfigureAwait(false);

        return Results.Ok(new { cancelled = text || voice });
    }

    private static async Task<IResult> Close(HttpContext context, string key, ApiSessionMap map)
    {
        ApiClient client = ApiAuth.Require(context);

        if (!ApiKeys.IsValid(key)) return Errors.BadKey(key);

        await map.CloseAsync(map.IdFor(client, key), "client closed").ConfigureAwait(false);

        return Results.NoContent();
    }

    internal static SessionResponse Describe(Session session, string key, bool mine) => new(
        session.Id.Value,
        key,
        session.Descriptor.DisplayName,
        session.Id.Surface.Value,
        session.Id.Kind.ToString(),
        session.Descriptor.MemoryGroup,
        session.State.ToString(),
        mine,
        session.LastActivity,
        [.. session.Descriptor.KnownParticipants.Where(p => !p.IsLane).Select(p => p.DisplayName)],
        [.. Enum.GetValues<ChannelCapabilities>()
             .Where(c => c != ChannelCapabilities.None && session.Descriptor.Supports(c))
             .Select(c => c.ToString())]);

    internal static MessageResponse ToResponse(LaneMessage message) => new(
        message.Id.ToString(),
        message.Sequence,
        message.Role.ToString(),
        message.Kind.ToString(),
        message.Author.DisplayName,
        message.TextContent,
        message.Timestamp,
        message.ExternalId,
        message.Content.OfType<ImagePart>().ToList() is { Count: > 0 } images
            ? [.. images.Select(image => new ImageResponse(image.Url?.ToString(), image.MediaType))]
            : null);
}

internal static class Errors
{
    public static IResult BadKey(string key) => Results.BadRequest(new ErrorResponse(
        "invalid_key",
        $"'{key}' is not a usable session key. Use letters, digits, '-', '_' or '.', " +
        $"optionally prefixed '{ApiKeys.SharedPrefix}'."));

    public static IResult Forbidden(string detail) =>
        Results.Json(new ErrorResponse("forbidden", detail), ApiJson.Options, statusCode: StatusCodes.Status403Forbidden);
}
