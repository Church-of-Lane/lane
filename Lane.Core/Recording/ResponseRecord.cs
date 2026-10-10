using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Context;
using Lane.Core.Messages;
using Lane.Core.Models;

namespace Lane.Core.Recording;

/// <summary>
/// One model call as a JSONL line. The prompt is an OpenAI-style chat <c>messages</c> array, with
/// user turns speaker-prefixed as providers see them. The answer is <c>completion</c>, in the same
/// shape as an assistant message. Images are replaced with <c>[image]</c>.
/// </summary>
public static class ResponseRecord
{
    public const int Version = 1;

    public static JsonObject Build(
        ModelDescriptor model, ModelRequest request, ModelResponse response, NameRedactor redactor, DateTimeOffset at)
    {
        JsonArray tools = new();
        foreach (Lane.Core.Tools.ToolDescriptor tool in request.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"]        = tool.Name,
                ["description"] = tool.Description,
                ["parameters"]  = JsonSerializer.SerializeToNode(tool.InputSchema)
            });
        }

        return new JsonObject
        {
            ["v"]           = Version,
            ["id"]          = Guid.CreateVersion7(at).ToString("n"),
            ["trace_id"]    = request.TraceId,
            ["timestamp"]   = at.ToString("O"),
            ["task"]        = request.CacheLineage?.Split([':', '|'], 2)[0],
            ["model"]       = new JsonObject
            {
                ["instance"] = model.InstanceId,
                ["provider"] = model.Provider,
                ["id"]       = model.ModelId
            },
            ["source"]      = response.Origin is null ? "provider" : "node",
            ["node"]        = response.Origin is { } origin
                ? new JsonObject { ["key_id"] = origin.Identity.KeyId, ["algorithm"] = origin.Identity.Algorithm }
                : null,
            ["params"]      = Parameters(request),
            ["tools"]       = tools,
            ["messages"]    = Messages(request, redactor),
            ["completion"]  = Assistant(response.Content, redactor),
            ["stop_reason"] = Snake(response.Stop.ToString()),
            ["usage"]       = new JsonObject
            {
                ["input"]       = response.Usage.Input,
                ["output"]      = response.Usage.Output,
                ["cache_read"]  = response.Usage.CacheRead,
                ["cache_write"] = response.Usage.CacheWrite,
                ["latency_ms"]  = (long)response.Usage.Latency.TotalMilliseconds
            }
        };
    }

    private static JsonObject Parameters(ModelRequest request)
    {
        JsonObject parameters = new()
        {
            ["max_output_tokens"] = request.MaxOutputTokens,
            ["temperature"]       = request.Temperature,
            ["tool_choice"]       = request.ToolChoice.Mode == ToolChoiceMode.Specific
                ? new JsonObject { ["type"] = "tool", ["name"] = request.ToolChoice.ToolName }
                : (JsonNode)Snake(request.ToolChoice.Mode.ToString())
        };

        if (request.StopSequences is { Count: > 0 } stops)
        {
            JsonArray sequences = new();
            foreach (string stop in stops) sequences.Add(stop);

            parameters["stop_sequences"] = sequences;
        }

        if (request.ResponseFormat is { } format)
        {
            parameters["response_format"] = new JsonObject
            {
                ["name"]   = format.Name,
                ["strict"] = format.Strict,
                ["schema"] = JsonSerializer.SerializeToNode(format.Schema)
            };
        }

        return parameters;
    }

    private static JsonArray Messages(ModelRequest request, NameRedactor redactor)
    {
        JsonArray messages = new();

        string system = string.Join("\n\n", request.System.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

        if (system.Length > 0)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = redactor.Redact(system) });

        foreach (LaneMessage message in request.Messages)
        {
            switch (message.Role)
            {
                case LaneRole.Tool:
                    foreach (ToolResultPart result in message.Content.OfType<ToolResultPart>())
                        messages.Add(ToolResult(result, redactor));
                    break;

                case LaneRole.Assistant:
                    messages.Add(Assistant(message.Content, redactor));
                    break;

                default:
                    if (User(message, redactor) is { } user) messages.Add(user);
                    break;
            }
        }

        return messages;
    }

    private static JsonObject? User(LaneMessage message, NameRedactor redactor)
    {
        StringBuilder content = new();
        bool wroteText = false;

        foreach (ContentPart part in message.Content)
        {
            switch (part)
            {
                case TextPart { Text: { Length: > 0 } text }:
                    content.Append(PromptText.Render(message, text, !wroteText));
                    wroteText = true;
                    break;

                case ImagePart:
                    content.Append("[image]\n");
                    break;

                case AudioPart:
                    content.Append("[audio]\n");
                    break;
            }
        }

        return content.Length == 0
            ? null
            : new JsonObject { ["role"] = "user", ["content"] = redactor.Redact(content.ToString()) };
    }

    private static JsonObject ToolResult(ToolResultPart result, NameRedactor redactor)
    {
        JsonObject tool = new()
        {
            ["role"]         = "tool",
            ["tool_call_id"] = result.ToolCallId,
            ["content"]      = redactor.Redact(string.Concat(result.Content.OfType<TextPart>().Select(p => p.Text)))
        };

        if (result.IsError) tool["is_error"] = true;

        return tool;
    }

    private static JsonObject Assistant(IReadOnlyList<ContentPart> content, NameRedactor redactor)
    {
        string text     = string.Concat(content.OfType<TextPart>().Select(p => p.Text));
        string thinking = string.Concat(content.OfType<ThinkingPart>().Select(p => p.Text));

        JsonObject assistant = new()
        {
            ["role"]    = "assistant",
            ["content"] = text.Length > 0 ? redactor.Redact(text) : null
        };

        if (thinking.Length > 0) assistant["reasoning"] = redactor.Redact(thinking);

        JsonArray calls = new();

        foreach (ToolUsePart call in content.OfType<ToolUsePart>())
        {
            calls.Add(new JsonObject
            {
                ["id"]       = call.ToolCallId,
                ["type"]     = "function",
                ["function"] = new JsonObject
                {
                    ["name"]      = call.ToolName,
                    ["arguments"] = call.Arguments.ValueKind == JsonValueKind.Undefined
                        ? new JsonObject()
                        : redactor.Redact(JsonSerializer.SerializeToNode(call.Arguments))
                }
            });
        }

        if (calls.Count > 0) assistant["tool_calls"] = calls;

        return assistant;
    }

    private static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
