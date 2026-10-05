using Lane.Core.Models;

namespace Lane.Surfaces.Coding.Claude;

/// <summary>What Claude Code said and did in one turn.</summary>
/// <param name="Outcome">"completed", "interrupted", or the error kind.</param>
/// <param name="Activity">One phrase per tool call, in order.</param>
/// <param name="Usage">Tokens spent by this turn alone; empty when it never reported.</param>
public sealed record ClaudeTurn(
    string                Text,
    bool                  IsError,
    string                Outcome,
    decimal               TotalCostUsd,
    IReadOnlyList<string> Activity,
    TokenUsage            Usage = default);

/// <summary>One Claude Code conversation, which may outlive the process running it.</summary>
public interface IClaudeCodeTransport : IAsyncDisposable
{
    /// <summary>A message has been sent and its turn has not finished.</summary>
    bool Busy { get; }

    bool Running { get; }

    string? SessionId { get; }

    /// <summary>Claude Code's own estimate for the current conversation.</summary>
    decimal TotalCostUsd { get; }

    event Action<ClaudeTurn>? TurnCompleted;

    /// <summary>Starts the process if it is not running. Queued by Claude Code when a turn is in progress.</summary>
    Task SendAsync(string text, CancellationToken ct);

    /// <summary>False when nothing was running to interrupt.</summary>
    Task<bool> InterruptAsync(CancellationToken ct);

    /// <summary>Ends the process and forgets the conversation, so the next message starts a new one.</summary>
    Task ResetAsync(CancellationToken ct);
}

/// <summary>Where the Claude Code session id is kept between runs of Lane.</summary>
public interface IClaudeSessionStore
{
    ValueTask<string?> LoadAsync(CancellationToken ct);

    ValueTask SaveAsync(string? sessionId, CancellationToken ct);
}
