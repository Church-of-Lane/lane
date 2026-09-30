using System.Text.Json;
using Lane.Core.Energy;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Sessions;

namespace Lane.Core.Tools;

/// <summary>Where a tool's observation should be remembered.</summary>
public enum MemoryScopeHint
{
    /// <summary>Belongs to the conversation that triggered it.</summary>
    CurrentSession,

    /// <summary>Belongs to Lane rather than to any one conversation — something she read or looked up.</summary>
    Global
}

/// <summary>
/// Something a tool wants written to memory beyond its own reply to the model.
///
/// This is what lets a tool do what v2's <c>read</c> and <c>search</c> fields did — inject
/// their results into memory as first-class observations — without the loop knowing that
/// books or searches exist.
/// </summary>
public sealed record ToolObservation(string Text, MemoryScopeHint Scope = MemoryScopeHint.CurrentSession);

public sealed record ToolResult
{
    public required IReadOnlyList<ContentPart> Content { get; init; }

    public bool IsError { get; init; }

    public IReadOnlyList<ToolObservation> Observations { get; init; } = [];

    /// <summary>Ends the agent run after this round of calls, and suppresses whatever reply has not been sent yet.</summary>
    public bool EndsTurn { get; init; }

    public string Text => string.Concat(Content.OfType<TextPart>().Select(p => p.Text));

    public static ToolResult Ok(string text) => new() { Content = [new TextPart(text)] };

    public static ToolResult Error(string message) =>
        new() { Content = [new TextPart(message)], IsError = true };

    /// <summary>Also record this in memory, so the next turn still knows what was found.</summary>
    public ToolResult RememberAs(string observation, MemoryScopeHint hint = MemoryScopeHint.CurrentSession) =>
        this with { Observations = [.. Observations, new ToolObservation(observation, hint)] };
}

public sealed record ToolInvocation(string ToolCallId, JsonElement Arguments, ToolContext Context);

/// <summary>What a tool is allowed to know about the turn invoking it.</summary>
public sealed record ToolContext
{
    public SessionId?         Session    { get; init; }
    public SessionDescriptor? Descriptor { get; init; }

    /// <summary>The person whose turn this is. Null in a monologue.</summary>
    public Participant? Requester { get; init; }

    public TurnKind Turn { get; init; } = TurnKind.Respond;

    /// <summary>The platform id of the latest message this turn answers, where it has one.</summary>
    public string? TriggerExternalId { get; init; }

    /// <summary>How worn out she is, so the registry can refuse an expensive call she cannot afford.</summary>
    public EnergyTier Energy { get; init; } = EnergyTier.Rested;

    public MemoryContext Memory { get; init; } = new();

    public required IServiceProvider Services { get; init; }

    /// <summary>For tools that act on other conversations — the monologue's <c>speak_to_session</c>.</summary>
    public ISessionRegistry? Sessions { get; init; }
}

public interface ITool
{
    ToolDescriptor Descriptor { get; }

    ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct);
}

/// <summary>Marks a tool for assembly scanning by <c>AddLaneTools</c>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class LaneToolAttribute : Attribute;
