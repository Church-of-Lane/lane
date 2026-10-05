using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lane.Surfaces.Coding.Claude;

/// <summary>One thing Claude Code reported on stdout, reduced to what Lane uses.</summary>
public abstract record ClaudeEvent
{
    public sealed record Init(string SessionId) : ClaudeEvent;

    /// <summary>A tool call in the main conversation. Subagents' calls are not reported.</summary>
    public sealed record ToolUse(string Name, string Summary) : ClaudeEvent;

    public sealed record Result(
        string  SessionId,
        string  Subtype,
        bool    IsError,
        string? Text,
        decimal TotalCostUsd,
        string? TerminalReason,
        int     QueuedTurns) : ClaudeEvent;

    public sealed record ControlResponse(string RequestId, bool Success) : ClaudeEvent;
}

/// <summary>Claude Code's <c>--input-format stream-json</c> and <c>--output-format stream-json</c> lines.</summary>
public static class StreamJson
{
    public static string UserMessage(string text) => new JsonObject
    {
        ["type"]    = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text }
    }.ToJsonString();

    public static string Interrupt(string requestId) => new JsonObject
    {
        ["type"]       = "control_request",
        ["request_id"] = requestId,
        ["request"]    = new JsonObject { ["subtype"] = "interrupt" }
    }.ToJsonString();

    /// <summary>Unparseable or uninteresting lines yield nothing.</summary>
    /// <param name="workspace">Paths under it are shown relative to it.</param>
    public static IReadOnlyList<ClaudeEvent> Parse(string line, string? workspace = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return [];

        JsonElement root;

        try { root = JsonDocument.Parse(line).RootElement; }
        catch (JsonException) { return []; }

        if (root.ValueKind != JsonValueKind.Object) return [];

        switch (String(root, "type"))
        {
            case "system" when String(root, "subtype") == "init" && String(root, "session_id") is { } id:
                return [new ClaudeEvent.Init(id)];

            case "assistant" when !HasParent(root):
                return [.. ToolUses(root, workspace)];

            case "result":
                return [new ClaudeEvent.Result(
                    String(root, "session_id") ?? "",
                    String(root, "subtype") ?? "",
                    root.TryGetProperty("is_error", out JsonElement err) && err.ValueKind == JsonValueKind.True,
                    String(root, "result"),
                    root.TryGetProperty("total_cost_usd", out JsonElement cost) && cost.TryGetDecimal(out decimal c) ? c : 0,
                    String(root, "terminal_reason"),
                    root.TryGetProperty("queued_turn_count", out JsonElement q) && q.TryGetInt32(out int n) ? n : 0)];

            case "control_response" when root.TryGetProperty("response", out JsonElement response):
                return [new ClaudeEvent.ControlResponse(
                    String(response, "request_id") ?? "",
                    String(response, "subtype") == "success")];

            default:
                return [];
        }
    }

    private static bool HasParent(JsonElement root) =>
        root.TryGetProperty("parent_tool_use_id", out JsonElement parent) && parent.ValueKind == JsonValueKind.String;

    private static IEnumerable<ClaudeEvent> ToolUses(JsonElement root, string? workspace)
    {
        if (!root.TryGetProperty("message", out JsonElement message) ||
            !message.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array) yield break;

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (String(block, "type") != "tool_use" || String(block, "name") is not { } name) continue;

            JsonElement input = block.TryGetProperty("input", out JsonElement i) ? i : default;

            yield return new ClaudeEvent.ToolUse(name, Summarise(name, input, workspace));
        }
    }

    /// <summary>One short phrase for a tool call: "ran `dotnet build`", "edited src/X.cs".</summary>
    public static string Summarise(string tool, JsonElement input, string? workspace = null)
    {
        string? Arg(string name) => input.ValueKind == JsonValueKind.Object ? String(input, name) : null;

        string Path(string name) => Relative(Arg(name) ?? "?", workspace);

        return tool switch
        {
            "Bash"                      => $"ran `{Clip(Arg("command") ?? "?", 120)}`",
            "Edit" or "MultiEdit"       => $"edited {Path("file_path")}",
            "Write"                     => $"wrote {Path("file_path")}",
            "NotebookEdit"              => $"edited {Path("notebook_path")}",
            "Read"                      => $"read {Path("file_path")}",
            "Glob"                      => $"listed {Clip(Arg("pattern") ?? "?", 80)}",
            "Grep"                      => $"searched for {Clip(Arg("pattern") ?? "?", 80)}",
            "Task" or "Agent"           => $"delegated: {Clip(Arg("description") ?? "?", 80)}",
            "WebFetch"                  => $"fetched {Clip(Arg("url") ?? "?", 100)}",
            "WebSearch"                 => $"searched the web for {Clip(Arg("query") ?? "?", 80)}",
            "TodoWrite"                 => "updated its todo list",
            _                           => $"used {tool}"
        };
    }

    private static string Relative(string path, string? workspace)
    {
        if (workspace is null) return path;

        string root = workspace.TrimEnd('/') + "/";

        return path.StartsWith(root, StringComparison.Ordinal) ? path[root.Length..] : path;
    }

    private static string Clip(string text, int max)
    {
        string flat = text.ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
