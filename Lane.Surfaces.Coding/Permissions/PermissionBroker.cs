using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lane.Surfaces.Coding.Permissions;

public sealed record PermissionRequest(
    string         Id,
    string         WorkspaceId,
    string         ToolName,
    JsonElement    Input,
    DateTimeOffset At);

public sealed record PermissionVerdict(bool Allow, string? Message)
{
    /// <summary>The text Claude Code's <c>--permission-prompt-tool</c> contract expects back.</summary>
    public string ToToolResult(JsonElement input) => Allow
        ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = JsonNode.Parse(input.GetRawText()) }.ToJsonString()
        : new JsonObject { ["behavior"] = "deny", ["message"] = Message ?? "Lane denied this." }.ToJsonString();
}

/// <summary>Permission requests from Claude Code, waiting for Lane to answer them.</summary>
public sealed class PermissionBroker(TimeSpan timeout, TimeProvider? time = null)
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyz";

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Pending(PermissionRequest Request, TaskCompletionSource<PermissionVerdict> Verdict);

    /// <summary>Raised once per request, before anything waits on it.</summary>
    public event Action<PermissionRequest>? Requested;

    /// <summary>Raised once per request when it is answered, denied by timeout, or abandoned.</summary>
    public event Action<PermissionRequest, PermissionVerdict>? Resolved;

    public IReadOnlyList<PermissionRequest> PendingFor(string workspaceId) =>
        [.. _pending.Values.Select(p => p.Request).Where(r => r.WorkspaceId == workspaceId).OrderBy(r => r.At)];

    /// <summary>Waits for <see cref="Answer"/>; denies when the timeout passes first.</summary>
    public async Task<PermissionVerdict> RequestAsync(
        string workspaceId, string toolName, JsonElement input, CancellationToken ct)
    {
        Pending pending = new(
            new PermissionRequest(NewId(), workspaceId, toolName, input.Clone(), _time.GetUtcNow()),
            new TaskCompletionSource<PermissionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously));

        _pending[pending.Request.Id] = pending;

        PermissionVerdict verdict = new(false, "The request was abandoned.");

        try
        {
            Requested?.Invoke(pending.Request);

            verdict = await pending.Verdict.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            verdict = new PermissionVerdict(false, $"Nobody answered within {timeout.TotalMinutes:0.#} minutes.");
        }
        finally
        {
            _pending.TryRemove(pending.Request.Id, out _);

            Resolved?.Invoke(pending.Request, verdict);
        }

        return verdict;
    }

    /// <summary>False when no such request is open in that workspace.</summary>
    public bool Answer(string workspaceId, string requestId, bool allow, string? reason)
    {
        if (!_pending.TryGetValue(requestId.Trim().ToLowerInvariant(), out Pending? pending) ||
            pending.Request.WorkspaceId != workspaceId) return false;

        return pending.Verdict.TrySetResult(new PermissionVerdict(allow, allow ? null : reason));
    }

    /// <summary>Denies everything open in a workspace, as when its surface closes.</summary>
    public void DenyAll(string workspaceId, string reason)
    {
        foreach (Pending pending in _pending.Values.Where(p => p.Request.WorkspaceId == workspaceId))
            pending.Verdict.TrySetResult(new PermissionVerdict(false, reason));
    }

    private string NewId()
    {
        while (true)
        {
            string id = string.Create(5, Random.Shared, static (span, random) =>
            {
                for (int i = 0; i < span.Length; i++) span[i] = Alphabet[random.Next(Alphabet.Length)];
            });

            if (!_pending.ContainsKey(id)) return id;
        }
    }
}
