using System.Text.Json;
using Lane.Core.Sessions;

namespace Lane.Core.Tools;

public enum ToolSafety { ReadOnly, Mutating, Dangerous }

/// <summary>
/// Where a tool makes sense. Gating is applied both when the tool list is built and again
/// when a call arrives — the model's choice is never trusted on its own.
/// </summary>
public sealed record ToolAvailability
{
    /// <summary>The session must offer these, so "speak in voice" only exists where voice does.</summary>
    public ChannelCapabilities RequiredCapabilities { get; init; } = ChannelCapabilities.None;

    public TurnKind AllowedTurns { get; init; } = TurnKind.All;

    /// <summary>True for tools that cannot run outside a conversation.</summary>
    public bool RequiresSession { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Only offered in sessions whose <see cref="SessionDescriptor.Tags"/> contain this key.
    /// Filtered when advertising, even under a stable set.
    /// </summary>
    public string? RequiredSessionTag { get; init; }

    public static ToolAvailability Anywhere { get; } = new();
}

/// <summary>
/// Everything the model is told about a tool, plus everything the kernel needs to police it.
/// Produced once per tool and cached — schema generation is not free.
/// </summary>
public sealed record ToolDescriptor
{
    /// <summary>snake_case and globally unique. MCP tools are prefixed <c>mcp__{server}__</c>.</summary>
    public required string Name { get; init; }

    public required string Description { get; init; }

    /// <summary>JSON Schema (2020-12), object-typed. Generated from the args record, never hand-written.</summary>
    public required JsonElement InputSchema { get; init; }

    public ToolAvailability Availability { get; init; } = ToolAvailability.Anywhere;
    public ToolSafety       Safety       { get; init; } = ToolSafety.ReadOnly;
    public TimeSpan         Timeout      { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Which <c>IToolSource</c> produced this — "builtin", "mcp:fs", a plugin id.</summary>
    public string SourceId { get; init; } = "builtin";
}
