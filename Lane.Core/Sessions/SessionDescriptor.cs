using System.Collections.Immutable;
using Lane.Core.Identity;

namespace Lane.Core.Sessions;

/// <summary>
/// What a session's attached channels can actually do. Tools are gated on these, so a
/// "speak in voice" tool is simply never offered where there is no voice channel.
/// </summary>
[Flags]
public enum ChannelCapabilities
{
    None      = 0,
    Text      = 1 << 0,
    Voice     = 1 << 1,
    Images    = 1 << 2,
    Typing    = 1 << 3,
    Presence  = 1 << 4,
    Files     = 1 << 5,
    Interrupt = 1 << 6,
    Reactions = 1 << 7
}

/// <summary>
/// Everything the kernel knows about one conversation. Surfaces build these; the kernel
/// never invents them.
/// </summary>
public sealed record SessionDescriptor
{
    public required SessionId Id { get; init; }

    /// <summary>Human-readable, for logs, the TUI, and the monologue's world view: "#general".</summary>
    public required string DisplayName { get; init; }

    public ChannelCapabilities Capabilities { get; init; } = ChannelCapabilities.Text;

    /// <summary>
    /// Sessions sharing a memory group share Session-scoped memory. A Discord text channel
    /// and the voice channel beside it are two sessions with one group, so Lane remembers
    /// one conversation across both. This is deliberately independent of <see cref="Id"/>:
    /// cohesion is enforced by the session pump, memory sharing is configured here.
    /// </summary>
    public required string MemoryGroup { get; init; }

    public IReadOnlyList<Participant> KnownParticipants { get; init; } = [];

    /// <summary>True for 1:1 conversations, where User-scoped memory is safe to surface.</summary>
    public bool IsDirect { get; init; }

    public IReadOnlyDictionary<string, string> Tags { get; init; } = ImmutableDictionary<string, string>.Empty;

    public bool Supports(ChannelCapabilities capability) => (Capabilities & capability) == capability;
}
