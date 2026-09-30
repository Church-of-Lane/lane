using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;
using NetCord.Rest;

namespace Lane.Surfaces.Discord;

/// <summary>
/// Lane's voice in one Discord channel.
///
/// The only thing in the process that can write to this channel, which is what makes the
/// cohesion guarantee hold: a reply can reach a channel only by going through the session
/// that channel is attached to.
/// </summary>
internal sealed class DiscordTextChannel(
    SessionId id,
    RestClient rest,
    ulong channelId,
    DiscordSurfaceOptions options,
    ILogger log)
    : SessionChannelBase(id, id.Surface,
        ChannelCapabilities.Text | ChannelCapabilities.Images | ChannelCapabilities.Typing | ChannelCapabilities.Reactions),
      ITextOutput, ITypingIndicator, IReactionOutput
{
    public async Task SendAsync(OutboundText text, CancellationToken ct)
    {
        IReadOnlyList<string> parts = DiscordMapper.Split(text.Text);

        bool first = true;

        foreach (string part in parts)
        {
            MessageProperties message = new()
            {
                Content = part,

                // Lane must not be able to ping a role or @everyone by happening to write
                // the words. Anything she says that looks like a mention stays inert text.
                AllowedMentions = AllowedMentionsProperties.None
            };

            // Only the first part answers the original message; the rest continue from it.
            if (first && options.ReplyInThread &&
                ulong.TryParse(text.ReplyToExternalId, out ulong replyTo))
                message.MessageReference = MessageReferenceProperties.Reply(replyTo, failIfNotExists: false);

            try
            {
                await rest.SendMessageAsync(channelId, message, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Failed to send to Discord channel {Channel}", channelId);
                return;
            }

            first = false;
        }
    }

    public async Task ReactAsync(string messageExternalId, string emoji, CancellationToken ct)
    {
        if (!ulong.TryParse(messageExternalId, out ulong messageId))
            throw new ArgumentException($"'{messageExternalId}' is not a Discord message id.", nameof(messageExternalId));

        if (DiscordMapper.ParseEmoji(emoji) is not { } parsed)
            throw new ArgumentException($"'{emoji}' is not an emoji.", nameof(emoji));

        ReactionEmojiProperties reaction = parsed.Id is { } id
            ? new ReactionEmojiProperties(parsed.Name, id)
            : new ReactionEmojiProperties(parsed.Name);

        await rest.AddMessageReactionAsync(channelId, messageId, reaction, cancellationToken: ct).ConfigureAwait(false);
    }

    public IDisposable BeginTyping()
    {
        if (!options.ShowTyping) return NoTyping.Instance;

        try
        {
            return rest.EnterTypingScope(channelId);
        }
        catch (Exception ex)
        {
            // Cosmetic. Never worth failing a turn over.
            log.LogDebug(ex, "Could not start typing in {Channel}", channelId);
            return NoTyping.Instance;
        }
    }

    private sealed class NoTyping : IDisposable
    {
        public static NoTyping Instance { get; } = new();
        public void Dispose() { }
    }
}
