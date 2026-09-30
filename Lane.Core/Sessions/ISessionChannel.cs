using System.Diagnostics.CodeAnalysis;
using Lane.Core.Identity;
using Lane.Core.Messages;

namespace Lane.Core.Sessions;

/// <summary>
/// A surface's write-back handle for one session.
///
/// Output modes are fetched with <see cref="TryGetService{T}"/> rather than declared as
/// members, so a surface that gains a new output mode does not force every other surface
/// to implement a stub for it.
/// </summary>
public interface ISessionChannel : IAsyncDisposable
{
    SessionId           Id           { get; }
    SurfaceId           Surface      { get; }
    ChannelCapabilities Capabilities { get; }

    bool TryGetService<T>([NotNullWhen(true)] out T? service) where T : class;
}

public interface ITextOutput
{
    Task SendAsync(OutboundText text, CancellationToken ct);
}

public interface ITypingIndicator
{
    IDisposable BeginTyping();
}

public interface IReactionOutput
{
    /// <summary>Reacts to the message with the given platform id.</summary>
    Task ReactAsync(string messageExternalId, string emoji, CancellationToken ct);
}

public interface IPresenceOutput
{
    Task SetPresenceAsync(string presence, CancellationToken ct);
}

public sealed record OutboundText(
    string     Text,
    string?    ReplyToExternalId = null,
    IReadOnlyList<ContentPart>? Attachments = null);

/// <summary>Which of a session's attached channels an outbound message should reach.</summary>
public readonly record struct DeliveryTarget(DeliveryMode Mode, ChannelCapabilities Required = ChannelCapabilities.None)
{
    public static DeliveryTarget Primary { get; } = new(DeliveryMode.Primary);
    public static DeliveryTarget All     { get; } = new(DeliveryMode.AllChannels);

    public static DeliveryTarget Requiring(ChannelCapabilities capability) =>
        new(DeliveryMode.Capability, capability);
}

public enum DeliveryMode
{
    /// <summary>The first attached channel that can carry the message.</summary>
    Primary,

    /// <summary>Every attached channel that can carry it.</summary>
    AllChannels,

    /// <summary>Only channels with a specific capability — voice, say.</summary>
    Capability
}

/// <summary>
/// A minimal channel base that surfaces can derive from, handling the service lookup.
/// </summary>
public abstract class SessionChannelBase : ISessionChannel
{
    protected SessionChannelBase(SessionId id, SurfaceId surface, ChannelCapabilities capabilities)
    {
        Id = id;
        Surface = surface;
        Capabilities = capabilities;
    }

    public SessionId           Id           { get; }
    public SurfaceId           Surface      { get; }
    public ChannelCapabilities Capabilities { get; }

    public virtual bool TryGetService<T>([NotNullWhen(true)] out T? service) where T : class
    {
        service = this as T;
        return service is not null;
    }

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
