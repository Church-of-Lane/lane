using System.Collections.Concurrent;
using Lane.Audio;
using Lane.Core.Credits;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Messages;
using Lane.Core.Presence;
using Lane.Core.Sessions;
using Lane.Core.Surfaces;
using Lane.Core.Voice;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Rest;
using Lane.Surfaces.Discord.Presence;
using Lane.Surfaces.Discord.Voice;

namespace Lane.Surfaces.Discord;

/// <summary>
/// One Discord bot.
///
/// Everything that made v2's Discord body the centre of the harness is gone: it owns no
/// model, no memory, no ear and no mouth, and it decides nothing about whether or how Lane
/// replies. It turns gateway events into <c>InboundEvent</c>s and attaches a channel the
/// kernel can write back through — the same contract the terminal surface has, which is
/// what lets both run in one process instead of being an either/or at startup.
///
/// Two of these can run side by side. Nothing here is static, and the token, the channel
/// filter and the memory grouping all come from this instance's own options.
///
/// It is also an <see cref="IVoiceChannelHost"/>: sitting in a voice channel is something
/// Lane decides through a tool, not something this surface does to every guild it is added
/// to the moment it connects.
/// </summary>
public sealed class DiscordSurface : ISurface, IVoiceChannelHost
{
    private readonly GatewayClient          _client;
    private readonly DiscordSurfaceOptions  _options;
    private readonly IAgentKernel           _kernel;
    private readonly ISessionRegistry       _sessions;
    private readonly IIdentityResolver      _identity;
    private readonly IEventBus              _bus;
    private readonly AudioRouter?           _router;
    private readonly ISponsoredAccess?      _sponsored;
    private readonly DiscordConsent         _consent;
    private readonly VoiceChannelHosts?      _voiceHosts;
    private readonly ILogger<DiscordSurface> _log;

    /// <summary>Channels Lane has already attached to, so a second message does not attach twice.</summary>
    private readonly ConcurrentDictionary<ulong, IDisposable> _attached = new();

    private readonly ConcurrentDictionary<ulong, SessionId> _openSessions = new();

    /// <summary>One voice connection per guild, which is all Discord allows a bot anyway.</summary>
    private readonly ConcurrentDictionary<ulong, DiscordVoiceConnection> _voiceConnections = new();

    /// <summary>Guilds she is in, by name, so she can be told where a voice channel lives.</summary>
    private readonly ConcurrentDictionary<ulong, string> _guilds = new();

    /// <summary>Who is sitting in which voice channel. One channel each — Discord's rule, not ours.</summary>
    private readonly ConcurrentDictionary<ulong, ulong> _inVoice = new();

    /// <summary>What this bot shows about itself, composed from one slot per source.</summary>
    private readonly DiscordStatusPublisher _status;

    private IDisposable? _presence;

    private IDisposable? _energy;

    private IDisposable? _voiceHost;

    private CancellationTokenSource? _lifetime;

    private int _commandsRegistered;

    public DiscordSurface(
        SurfaceId id,
        string token,
        DiscordSurfaceOptions options,
        IAgentKernel kernel,
        ISessionRegistry sessions,
        IIdentityResolver identity,
        IEventBus bus,
        ILogger<DiscordSurface> log,
        DiscordConsent consent,
        AudioRouter? router = null,
        ISponsoredAccess? sponsored = null,
        VoiceChannelHosts? voiceHosts = null)
    {
        _voiceHosts = voiceHosts;
        _consent   = consent;
        _router    = router;
        _sponsored = sponsored;
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        Id        = id;
        _options  = options;
        _kernel   = kernel;
        _sessions = sessions;
        _identity = identity;
        _bus      = bus;
        _log      = log;

        _client = new GatewayClient(
            new BotToken(token),
            new GatewayClientConfiguration
            {
                Intents = GatewayIntents.AllNonPrivileged | GatewayIntents.MessageContent
            });

        _status = new DiscordStatusPublisher(WriteStatusAsync, log);

        _client.MessageCreate    += OnMessageCreate;
        _client.Ready            += OnReady;
        _client.Disconnect       += OnDisconnect;
        _client.GuildCreate      += OnGuildCreate;
        _client.VoiceStateUpdate += OnVoiceStateUpdate;
        _client.InteractionCreate += OnInteractionCreate;
    }

    public SurfaceId Id { get; }

    /// <summary>Channels in the include list that are not also excluded. Null when there is no include list, so every channel is.</summary>
    public int? IncludedChannelCount => _options.Channels.Include.Count == 0
        ? null
        : _options.Channels.Include.Distinct().Count(channel => !_options.Channels.Exclude.Contains(channel));

    public async Task StartAsync(CancellationToken ct)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        StartStatus(_lifetime.Token);

        StartVoice(_lifetime.Token);

        await _client.StartAsync(cancellationToken: ct).ConfigureAwait(false);

        _log.LogInformation("Discord surface {Surface} connecting", Id);
    }

    /// <summary>
    /// Wires whatever Lane shows about herself to the one line Discord gives a bot.
    ///
    /// Her face is the first thing on it, read off the event bus like every other observer —
    /// the surface owns none of this and is told nothing directly. A mood she reached in
    /// another conversation, on another surface or in her own monologue is still her mood,
    /// so this deliberately does not filter by session: one person, one face.
    /// </summary>
    private void StartStatus(CancellationToken ct)
    {
        if (!_options.Status.Enabled) return;

        _status.Start(ct);

        _presence = _bus.Subscribe<PresenceChanged>(
            change => _status.Set(DiscordStatusSlot.Face, change.Emoticon));

        // Null clears the slot, and the line reports whether it actually changed — so a
        // heartbeat every ten minutes that finds her still rested costs no gateway write.
        _energy = _bus.Subscribe<EnergyChanged>(change => _status.Set(DiscordStatusSlot.Sleep,
            change.State.Asleep                      ? "zzz"
          : change.State.Tier == EnergyTier.Weary    ? "worn out"
          : change.State.Tier == EnergyTier.Tired    ? "tired"
          :                                            null));
    }

    /// <summary>Sends the composed line to the gateway. An empty line clears the status.</summary>
    private async Task WriteStatusAsync(string line, CancellationToken ct)
    {
        PresenceProperties presence = new(UserStatusType.Online);

        if (line.Length > 0)
            presence = presence.WithActivities(
                [new UserActivityProperties("Custom Status", UserActivityType.Custom).WithState(line)]);

        await _client.UpdatePresenceAsync(presence, cancellationToken: ct).ConfigureAwait(false);
    }

    private ValueTask OnReady(ReadyEventArgs args)
    {
        _log.LogInformation("Discord surface {Surface} ready as {User}", Id, args.User.Username);

        _bus.Publish(new SurfaceStateChanged(Id, Connected: true, args.User.Username));

        // Presence belongs to the gateway session, so a reconnect starts her blank-faced
        // unless the line is sent again.
        _status.Refresh();

        if (Interlocked.Exchange(ref _commandsRegistered, 1) == 0) _ = RegisterCommandsAsync(args.ApplicationId);

        return default;
    }

    /// <summary>Replaces the application's global commands with <see cref="DiscordCommands.Definitions"/>.</summary>
    private async Task RegisterCommandsAsync(ulong applicationId)
    {
        try
        {
            await _client.Rest.BulkOverwriteGlobalApplicationCommandsAsync(
                applicationId,
                DiscordCommands.Definitions,
                cancellationToken: _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _commandsRegistered, 0);
            _log.LogWarning(ex, "Could not register slash commands for Discord surface {Surface}", Id);
        }
    }

    private ValueTask OnInteractionCreate(Interaction interaction)
    {
        if (interaction is not SlashCommandInteraction command) return default;
        if (DiscordCommands.ReplyTo(command.Data.Name) is not { } reply) return default;

        _ = Task.Run(async () =>
        {
            try
            {
                if (DiscordCommands.ConsentSetBy(command.Data.Name) is { } optedIn)
                    await _consent.SetAsync(command.User.Id, optedIn, _lifetime?.Token ?? CancellationToken.None)
                        .ConfigureAwait(false);

                await command.SendResponseAsync(InteractionCallback.Message(
                    new InteractionMessageProperties().WithContent(reply).WithFlags(MessageFlags.Ephemeral)))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not answer /{Command}", command.Data.Name);
            }
        });

        return default;
    }

    private ValueTask OnDisconnect(DisconnectEventArgs args)
    {
        _log.LogWarning("Discord surface {Surface} disconnected", Id);

        _bus.Publish(new SurfaceStateChanged(Id, Connected: false));

        return default;
    }

    /// <summary>
    /// Notes the guild and who is already sitting in its voice channels.
    ///
    /// She does not join anything here. Walking into a voice channel on every server she
    /// has ever been added to, the moment she connects, is an open ear nobody asked for —
    /// so where she sits is a thing she is asked for and decides, through
    /// <c>list_voice_channels</c> and <c>join_voice_channel</c>.
    /// </summary>
    private ValueTask OnGuildCreate(GuildCreateEventArgs args)
    {
        if (args.Guild is not { } guild) return default;

        _guilds[guild.Id] = guild.Name;

        foreach (VoiceState state in guild.VoiceStates.Values)
        {
            if (state.UserId == _client.Id) continue;

            if (state.ChannelId is { } channel) _inVoice[state.UserId] = channel;
        }

        return default;
    }

    /// <summary>Follows people in and out of voice channels, and stops listening to whoever left hers.</summary>
    private ValueTask OnVoiceStateUpdate(VoiceState state)
    {
        if (state.UserId == _client.Id) return default;

        if (state.ChannelId is { } joined) _inVoice[state.UserId] = joined;
        else _inVoice.TryRemove(state.UserId, out _);

        foreach (DiscordVoiceConnection connection in _voiceConnections.Values)
        {
            // Somebody arriving is the channel being used, and keeps her from leaving in
            // the seconds before they say hello.
            if (state.ChannelId == connection.ChannelId) connection.Touch();
            else _ = connection.ForgetAsync(state.UserId);
        }

        return default;
    }

    public SurfaceId Surface => Id;

    /// <summary>True when she could join something: audio is wired up and voice is not switched off.</summary>
    private bool VoiceAvailable => _router is not null && _options.Voice.Enabled;

    public async ValueTask<IReadOnlyList<VoiceChannelSummary>> ListChannelsAsync(CancellationToken ct)
    {
        if (!VoiceAvailable) return [];

        List<VoiceChannelSummary> summaries = [];

        foreach ((ulong guildId, string guildName) in _guilds)
        {
            foreach (VoiceGuildChannel channel in await VoiceChannelsOfAsync(guildId, ct).ConfigureAwait(false))
                summaries.Add(new VoiceChannelSummary
                {
                    Surface   = Id,
                    Id        = channel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name      = channel.Name,
                    Space     = guildName,
                    Occupants = _inVoice.Count(seat => seat.Value == channel.Id),

                    Joined = _voiceConnections.TryGetValue(guildId, out DiscordVoiceConnection? here) &&
                             here.ChannelId == channel.Id
                });
        }

        return summaries;
    }

    public async ValueTask<VoiceJoinResult> JoinAsync(string channelId, CancellationToken ct)
    {
        if (_router is null) return new VoiceJoinResult(false, "This bot has no audio configured, so it cannot hear or speak.");
        if (!_options.Voice.Enabled) return new VoiceJoinResult(false, "Voice is switched off on this bot.");

        if (!ulong.TryParse(channelId, System.Globalization.CultureInfo.InvariantCulture, out ulong id))
            return new VoiceJoinResult(false, $"\"{channelId}\" is not a Discord channel id.");

        (ulong guildId, string? name) = await FindVoiceChannelAsync(id, ct).ConfigureAwait(false);

        if (name is null)
            return new VoiceJoinResult(false, "That voice channel is not one this bot can see.");

        if (_voiceConnections.TryGetValue(guildId, out DiscordVoiceConnection? existing))
        {
            if (existing.ChannelId == id)
            {
                existing.Touch();

                return new VoiceJoinResult(true, $"Already sitting in {name}.");
            }

            // One connection per guild: moving means leaving the old channel first.
            _voiceConnections.TryRemove(guildId, out _);

            await existing.DisposeAsync().ConfigureAwait(false);
        }

        DiscordVoiceConnection connection = new(
            Id, _client, _sessions, _router, _identity, _options, _log);

        if (!_voiceConnections.TryAdd(guildId, connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            return new VoiceJoinResult(false, "Something else was joining a channel on that server at the same time.");
        }

        try
        {
            await connection.JoinAsync(guildId, id, name, _lifetime?.Token ?? ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _voiceConnections.TryRemove(guildId, out _);

            await connection.DisposeAsync().ConfigureAwait(false);

            _log.LogError(ex, "Could not join voice channel {Channel}", id);

            return new VoiceJoinResult(false, $"Could not join {name}: {ex.Message}");
        }

        return new VoiceJoinResult(true,
            $"Joined {name}. You will leave on your own after {Describe(_options.Voice.IdleTimeout)} of quiet.");
    }

    /// <summary>The guild a voice channel belongs to, and its name. Null name when no guild has it.</summary>
    private async Task<(ulong Guild, string? Name)> FindVoiceChannelAsync(ulong channelId, CancellationToken ct)
    {
        foreach (ulong guildId in _guilds.Keys)
        {
            foreach (VoiceGuildChannel channel in await VoiceChannelsOfAsync(guildId, ct).ConfigureAwait(false))
                if (channel.Id == channelId) return (guildId, channel.Name);
        }

        return (0, null);
    }

    /// <summary>
    /// Read over REST rather than off the gateway's guild, so a channel made after she
    /// connected is one she can still be sent to.
    /// </summary>
    private async Task<IReadOnlyList<VoiceGuildChannel>> VoiceChannelsOfAsync(ulong guildId, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<IGuildChannel> channels =
                await _client.Rest.GetGuildChannelsAsync(guildId, cancellationToken: ct).ConfigureAwait(false);

            return [.. channels.OfType<VoiceGuildChannel>()];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not list the channels of guild {Guild}", guildId);

            return [];
        }
    }

    /// <summary>Offers this bot's voice channels to her tools, and starts the clock that empties them.</summary>
    private void StartVoice(CancellationToken ct)
    {
        if (!VoiceAvailable) return;

        _voiceHost = _voiceHosts?.Register(this);

        if (_options.Voice.IdleTimeout <= TimeSpan.Zero) return;

        _ = Task.Run(async () =>
        {
            // Swept far more often than the timeout, so leaving is prompt without the timer
            // itself being something anyone pays for.
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));

            try
            {
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    await LeaveIdleVoiceAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    /// <summary>Leaves every voice channel that has heard nothing for longer than the timeout.</summary>
    private async Task LeaveIdleVoiceAsync()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _options.Voice.IdleTimeout;

        foreach ((ulong guildId, DiscordVoiceConnection connection) in _voiceConnections)
        {
            if (connection.LastActivity > cutoff) continue;
            if (!_voiceConnections.TryRemove(guildId, out _)) continue;

            _log.LogInformation("Leaving voice channel {Channel}: nothing for {Timeout}",
                connection.ChannelName, _options.Voice.IdleTimeout);

            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogWarning(ex, "Did not leave {Channel} cleanly", connection.ChannelName); }
        }
    }

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} minutes" : $"{(int)span.TotalSeconds} seconds";

    private ValueTask OnMessageCreate(Message message)
    {
        // The gateway dispatch must return immediately; anything slower stalls every other
        // event on the socket, including the ones for other channels.
        _ = Task.Run(() => IngestAsync(message));

        return default;
    }

    private async Task IngestAsync(Message message)
    {
        try
        {
            if (message.Author.Id == _client.Id) return;                       // her own words
            if (message.Author.IsBot && !_options.RespondToBots) return;

            if (_options.RequireOptIn && !message.Author.IsBot &&
                !await _consent.IsOptedInAsync(message.Author.Id, _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false))
                return;

            bool isDirect = message.GuildId is null;

            if (isDirect && !_options.AllowDirectMessages) return;
            if (!isDirect && !_options.Channels.Allows(message.ChannelId) && !await SponsoredAsync(message.ChannelId).ConfigureAwait(false))
                return;

            Participant author = ToParticipant(message.Author);

            SessionDescriptor descriptor = DiscordMapper.Describe(
                Id,
                message.ChannelId,
                message.GuildId,
                ChannelNameOf(message),
                _options.MemoryGroupTemplate,
                [author]);

            _sessions.GetOrCreate(descriptor);
            _openSessions[message.ChannelId] = descriptor.Id;

            Attach(descriptor.Id, message.ChannelId);

            LaneMessage inbound = LaneMessage.User(
                descriptor.Id,
                author,
                BuildContent(message, descriptor.Id, author),
                message.CreatedAt,
                message.Id.ToString());

            await _kernel.SubmitAsync(new InboundEvent
            {
                Session    = descriptor.Id,
                Author     = author,
                Message    = inbound,
                ExternalId = message.Id.ToString(),

                // Other bots are remembered but never answered, so two of them cannot get
                // stuck talking to each other.
                RequiresResponse = !message.Author.IsBot
            }, _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Failed to ingest a Discord message in {Channel}", message.ChannelId);
        }
    }

    /// <summary>Charges the channel's sponsors for one message. Excluded channels are never sponsored.</summary>
    private async ValueTask<bool> SponsoredAsync(ulong channelId) =>
        _sponsored is not null &&
        !_options.Channels.Exclude.Contains(channelId) &&
        await _sponsored.TryChargeAsync(
            SponsoredKind.DiscordChannel,
            channelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _lifetime?.Token ?? CancellationToken.None).ConfigureAwait(false);

    private IReadOnlyList<ContentPart> BuildContent(Message message, SessionId session, Participant author)
    {
        Dictionary<ulong, string> users = [];

        foreach (User mentioned in message.MentionedUsers)
            users[mentioned.Id] = DiscordMapper.DisplayNameOf(mentioned.GlobalName, mentioned.Username);

        // Lane's own id resolves to her name rather than a number, so being addressed reads
        // as being addressed.
        users.TryAdd(_client.Id, "Lane");

        Dictionary<ulong, string> roles = [];

        foreach (ulong roleId in message.MentionedRoleIds)
            roles[roleId] = RoleNameOf(message, roleId);

        string text = DiscordMapper.ResolveMentions(message.Content, users, roles);

        IReadOnlyList<EmbedView> embeds = ToViews(message.Embeds);

        // An embed is often the whole message — a posted link, a bot's answer — so the card
        // is folded into the text rather than left as structure nobody reads.
        if (DiscordMapper.RenderEmbeds(embeds) is { Length: > 0 } cards)
            text = string.IsNullOrWhiteSpace(text) ? cards : $"{text}\n\n{cards}";

        string files = DiscordMapper.RenderAttachments(
            [.. message.Attachments.Select(a => (a.FileName, a.Url, a.ContentType))]);

        if (files.Length > 0) text = string.IsNullOrWhiteSpace(text) ? files : $"{text}\n\n{files}";

        text = DiscordMapper.AppendReplyContext(
            text,
            message.ReferencedMessage is { } replied
                ? DiscordMapper.DisplayNameOf(replied.Author.GlobalName, replied.Author.Username)
                : null,
            message.ReferencedMessage is { } quoted ? WithEmbeds(quoted.Content, quoted.Embeds) : null);

        List<ContentPart> parts = [];

        if (!string.IsNullOrWhiteSpace(text)) parts.Add(new TextPart(text));

        foreach (Attachment attachment in message.Attachments)
        {
            if (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) continue;

            parts.Add(new ImagePart(new Uri(attachment.Url), null, attachment.ContentType));
        }

        foreach (EmbedView embed in embeds.Take(DiscordMapper.MaxEmbeds))
        {
            if (DiscordMapper.EmbedImage(embed) is not { } image) continue;

            parts.Add(new ImagePart(image.Url, null, image.MediaType));
        }

        // An image with no caption is still something worth reacting to.
        if (parts.Count == 0) parts.Add(new TextPart("(no text)"));

        return parts;
    }

    /// <summary>Drops the gateway's layout detail, keeping what a reader would take from the card.</summary>
    private static IReadOnlyList<EmbedView> ToViews(IReadOnlyList<Embed>? embeds)
    {
        if (embeds is not { Count: > 0 }) return [];

        return [.. embeds.Select(embed => new EmbedView
        {
            Title        = embed.Title,
            Url          = embed.Url,
            Description  = embed.Description,
            AuthorName   = embed.Author?.Name,
            AuthorUrl    = embed.Author?.Url,
            Provider     = embed.Provider?.Name,
            Footer       = embed.Footer?.Text,
            ImageUrl     = embed.Image?.Url,
            ThumbnailUrl = embed.Thumbnail?.Url,
            VideoUrl     = embed.Video?.Url,

            Fields = embed.Fields is { } fields
                ? [.. fields.Select(field => new EmbedFieldView(field.Name, field.Value))]
                : []
        })];
    }

    /// <summary>A message as one string, so a quoted card is not quoted as nothing.</summary>
    private static string WithEmbeds(string? content, IReadOnlyList<Embed>? embeds)
    {
        string cards = DiscordMapper.RenderEmbeds(ToViews(embeds));

        if (cards.Length == 0) return content ?? "";

        return string.IsNullOrWhiteSpace(content) ? cards : $"{content}\n\n{cards}";
    }

    private void Attach(SessionId session, ulong channelId)
    {
        _attached.GetOrAdd(channelId, _ =>
            _sessions.Attach(new DiscordTextChannel(session, _client.Rest, channelId, _options, _log)));
    }

    private Participant ToParticipant(User user) => _identity.Resolve(
        new ParticipantId(Id, user.Id.ToString()),
        DiscordMapper.DisplayNameOf(user.GlobalName, user.Username));

    private static string ChannelNameOf(Message message)
    {
        if (message.Channel is TextGuildChannel guild && !string.IsNullOrWhiteSpace(guild.Name))
            return guild.Name;

        return message.GuildId is null
            ? DiscordMapper.DisplayNameOf(message.Author.GlobalName, message.Author.Username)
            : message.ChannelId.ToString();
    }

    private static string RoleNameOf(Message message, ulong roleId)
    {
        if (message.Guild is { } guild && guild.Roles.TryGetValue(roleId, out Role? role)) return role.Name;

        return "role";
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_lifetime is not null) await _lifetime.CancelAsync().ConfigureAwait(false);

        _presence?.Dispose();
        _presence = null;

        _energy?.Dispose();
        _energy = null;

        _voiceHost?.Dispose();
        _voiceHost = null;

        await _status.DisposeAsync().ConfigureAwait(false);

        // Sessions are closed before their channels detach: closing drains whatever is
        // still queued, and a reply produced during that drain still needs somewhere to go.
        foreach (SessionId session in _openSessions.Values)
            await _sessions.CloseAsync(session, "surface stopped").ConfigureAwait(false);

        foreach (DiscordVoiceConnection connection in _voiceConnections.Values)
            await connection.DisposeAsync().ConfigureAwait(false);

        _voiceConnections.Clear();

        foreach (IDisposable attachment in _attached.Values) attachment.Dispose();

        _attached.Clear();
        _openSessions.Clear();

        try { await _client.CloseAsync(cancellationToken: ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogDebug(ex, "Discord surface {Surface} did not close cleanly", Id); }

        _bus.Publish(new SurfaceStateChanged(Id, Connected: false, "stopped"));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _lifetime?.Dispose();
        _client.Dispose();
    }
}
