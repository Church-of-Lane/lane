using Lane.Core.Identity;
using Lane.Surfaces.Coding.Claude;
using Lane.Surfaces.Coding.Git;

namespace Lane.Surfaces.Coding;

/// <summary>One directory Lane works in with Claude Code, and everything attached to it.</summary>
public sealed class CodingWorkspace
{
    public const string SurfacePrefix = "coding.";

    public required string Id   { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }

    public string? Purpose { get; init; }

    public bool SelfHosted { get; init; }

    /// <summary>Opened at runtime by Lane, rather than configured.</summary>
    public bool Dynamic { get; init; }

    public required IClaudeCodeTransport Claude { get; init; }

    public required GitRunner Git { get; init; }

    /// <summary>Null when no GitHub token is configured for this workspace.</summary>
    public GitHubClient? GitHub { get; init; }

    public SurfaceId SurfaceId => new(SurfacePrefix + Id);

    public SessionId SessionId => new(SurfaceId, SessionKind.Text, "claude");

    public CodingSurface? Surface { get; internal set; }
}
