using System.Text.Json;
using Lane.Core.Messages;
using Lane.Core.Tools;

namespace Lane.Core.Models;

public enum CacheHint
{
    None,

    /// <summary>Short-lived cache breakpoint (Anthropic's 5-minute ephemeral).</summary>
    Ephemeral,

    /// <summary>Long-lived breakpoint (1 hour) — worth it for the persona and long-term memory.</summary>
    Persistent
}

/// <summary>
/// One addressable chunk of the system prompt. Blocks exist rather than a single string
/// because cache breakpoints are per-block: the persona and long-term memory are cached
/// while the volatile tail (clock, roster) is not. Adapters must not reorder them.
/// </summary>
public sealed record PromptBlock(string Text, CacheHint Cache = CacheHint.None);

public enum ToolChoiceMode { Auto, None, Required, Specific }

public readonly record struct ToolChoice(ToolChoiceMode Mode, string? ToolName = null)
{
    public static ToolChoice Auto     { get; } = new(ToolChoiceMode.Auto);
    public static ToolChoice None     { get; } = new(ToolChoiceMode.None);
    public static ToolChoice Required { get; } = new(ToolChoiceMode.Required);

    public static ToolChoice Specific(string toolName) => new(ToolChoiceMode.Specific, toolName);
}

/// <summary>Structured-output request. Used by the response policy instead of v2's
/// prefill-plus-stop-sequence JSON hack.</summary>
public sealed record ResponseFormat(string Name, JsonElement Schema, bool Strict = true);

public sealed record ModelRequest
{
    public required IReadOnlyList<PromptBlock> System   { get; init; }
    public required IReadOnlyList<LaneMessage> Messages { get; init; }

    /// <summary>This request as a model with the given capabilities can take it.</summary>
    public ModelRequest For(ModelCapabilities capabilities) =>
        this with { Messages = [.. Messages.Select(m => m.For(capabilities))] };

    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = [];
    public ToolChoice     ToolChoice     { get; init; } = ToolChoice.Auto;
    public ResponseFormat? ResponseFormat { get; init; }

    public int    MaxOutputTokens { get; init; } = 2048;
    public float  Temperature     { get; init; } = 1f;
    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary>
    /// Groups requests that should share a prompt cache prefix (role + tool fingerprint +
    /// scope). Telemetry pivots hit rate on this, which is the only practical way to notice
    /// that a change quietly stopped the cache from working.
    /// </summary>
    public string? CacheLineage { get; init; }

    public string TraceId { get; init; } = Guid.NewGuid().ToString("n");
}
