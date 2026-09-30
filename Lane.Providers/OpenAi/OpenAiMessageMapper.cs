using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Context;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Tools;

namespace Lane.Providers.OpenAi;

/// <summary>
/// Translates between Lane's message model and the OpenAI chat-completions wire format.
///
/// Built on <see cref="JsonNode"/> rather than an SDK's typed models. The reason is
/// specific: OpenRouter proxies dozens of providers and returns <c>finish_reason</c> values
/// no SDK enum knows about, which is exactly why v2 had to drop to the protocol layer to
/// stop the client throwing. Reading the response as JSON means an unfamiliar value is
/// just a string we map to <see cref="StopReason.Other"/>.
/// </summary>
internal static class OpenAiMessageMapper
{
    /// <summary>
    /// OpenAI takes one system string, not addressable blocks. Cache hints are dropped
    /// because there is nothing to attach them to — providers on this API that cache
    /// (DeepSeek, for one) do it automatically on the prefix.
    /// </summary>
    public static string ToSystemPrompt(IReadOnlyList<PromptBlock> blocks) =>
        string.Join("\n\n", blocks.Select(b => b.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

    public static JsonArray ToMessages(IReadOnlyList<PromptBlock> system, IReadOnlyList<LaneMessage> messages)
    {
        JsonArray result = [];

        string prompt = ToSystemPrompt(system);

        if (!string.IsNullOrWhiteSpace(prompt))
            result.Add(new JsonObject { ["role"] = "system", ["content"] = prompt });

        foreach (LaneMessage message in messages)
        {
            // Tool results are their own top-level messages here, one per call — unlike
            // Anthropic, which carries them all inside a single user turn.
            if (message.Role == LaneRole.Tool)
            {
                foreach (ToolResultPart part in message.Content.OfType<ToolResultPart>())
                {
                    result.Add(new JsonObject
                    {
                        ["role"]         = "tool",
                        ["tool_call_id"] = part.ToolCallId,
                        ["content"]      = string.Concat(part.Content.OfType<TextPart>().Select(p => p.Text))
                    });
                }

                // Tool messages carry text only, so images a tool returned follow as a user turn.
                JsonArray images = [.. message.Content.OfType<ToolResultPart>()
                    .SelectMany(p => p.Content.OfType<ImagePart>())
                    .Select(ToImageContent)
                    .OfType<JsonNode>()];

                if (images.Count > 0)
                {
                    images.Insert(0, new JsonObject { ["type"] = "text", ["text"] = "(the image returned by the tool above)" });
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = images });
                }

                continue;
            }

            JsonObject? mapped = message.Role == LaneRole.Assistant
                ? ToAssistant(message)
                : ToUser(message);

            if (mapped is not null) result.Add(mapped);
        }

        return result;
    }

    private static JsonObject? ToUser(LaneMessage message)
    {
        List<ContentPart> parts = [.. message.Content.Where(p => p is TextPart or ImagePart)];

        if (parts.Count == 0) return null;

        // A message that is only text uses the simple string form, which every
        // OpenAI-compatible provider accepts; the array form is not universally supported.
        if (parts.All(p => p is TextPart))
        {
            return new JsonObject
            {
                ["role"]    = "user",
                ["content"] = PromptText.Render(message, string.Concat(parts.OfType<TextPart>().Select(p => p.Text)), true)
            };
        }

        JsonArray content = [];
        bool wroteText = false;

        foreach (ContentPart part in parts)
        {
            switch (part)
            {
                case TextPart text:
                    content.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = PromptText.Render(message, text.Text, !wroteText)
                    });
                    wroteText = true;
                    break;

                case ImagePart image when ToImageContent(image) is { } mapped:
                    content.Add(mapped);
                    break;
            }
        }

        return new JsonObject { ["role"] = "user", ["content"] = content };
    }

    private static JsonObject? ToImageContent(ImagePart image)
    {
        string? url = image.Url?.ToString()
            ?? (image.Data is { } data ? $"data:{image.MediaType};base64,{Convert.ToBase64String(data.Span)}" : null);

        if (url is null) return null;

        return new JsonObject
        {
            ["type"]      = "image_url",
            ["image_url"] = new JsonObject { ["url"] = url }
        };
    }

    private static JsonObject? ToAssistant(LaneMessage message)
    {
        string text = string.Concat(message.Content.OfType<TextPart>().Select(p => p.Text));

        JsonArray toolCalls = [];

        foreach (ToolUsePart call in message.Content.OfType<ToolUsePart>())
        {
            toolCalls.Add(new JsonObject
            {
                ["id"]   = call.ToolCallId,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.ToolName,
                    // Arguments travel as a JSON *string*, not an object.
                    ["arguments"] = call.Arguments.GetRawText()
                }
            });
        }

        if (text.Length == 0 && toolCalls.Count == 0) return null;

        JsonObject assistant = new() { ["role"] = "assistant" };

        // Assistant turns are never speaker-prefixed: teaching the model to emit its own
        // name is how v2 ended up stripping "Lane says:" off everything it read back.
        assistant["content"] = text.Length > 0 ? text : null;

        if (toolCalls.Count > 0) assistant["tool_calls"] = toolCalls;

        return assistant;
    }

    public static JsonArray ToTools(IReadOnlyList<ToolDescriptor> tools)
    {
        JsonArray result = [];

        foreach (ToolDescriptor tool in tools)
        {
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"]        = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"]  = JsonNode.Parse(tool.InputSchema.GetRawText())
                }
            });
        }

        return result;
    }

    public static JsonNode? ToToolChoice(ToolChoice choice) => choice.Mode switch
    {
        ToolChoiceMode.Auto     => null,
        ToolChoiceMode.None     => JsonValue.Create("none"),
        ToolChoiceMode.Required => JsonValue.Create("required"),
        ToolChoiceMode.Specific => new JsonObject
        {
            ["type"]     = "function",
            ["function"] = new JsonObject { ["name"] = choice.ToolName! }
        },
        _ => null
    };

    public static List<ContentPart> FromMessage(JsonElement message)
    {
        List<ContentPart> parts = [];

        if (message.TryGetProperty("content", out JsonElement content) &&
            content.ValueKind == JsonValueKind.String &&
            content.GetString() is { Length: > 0 } text)
            parts.Add(new TextPart(text));

        if (!message.TryGetProperty("tool_calls", out JsonElement calls) ||
            calls.ValueKind != JsonValueKind.Array)
            return parts;

        foreach (JsonElement call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("function", out JsonElement function)) continue;

            string id   = call.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? "" : "";
            string name = function.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";

            if (name.Length == 0) continue;

            parts.Add(new ToolUsePart(
                id.Length > 0 ? id : $"call_{Guid.NewGuid():n}"[..12],
                name,
                ParseArguments(function)));
        }

        return parts;
    }

    /// <summary>
    /// Arguments arrive as a JSON string. A model that emits malformed JSON here is common
    /// enough that it must not throw — an empty object reaches the tool, which reports a
    /// validation error the model can usually correct on its next step.
    /// </summary>
    private static JsonElement ParseArguments(JsonElement function)
    {
        if (!function.TryGetProperty("arguments", out JsonElement arguments)) return EmptyObject();

        string raw = arguments.ValueKind == JsonValueKind.String
            ? arguments.GetString() ?? ""
            : arguments.GetRawText();

        if (string.IsNullOrWhiteSpace(raw)) return EmptyObject();

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// Never an enum. OpenRouter fronts many providers and returns reasons no fixed list
    /// covers; an unknown one is information, not a crash.
    /// </summary>
    public static StopReason FromFinishReason(string? reason) => reason switch
    {
        "stop"            => StopReason.EndTurn,
        "tool_calls"      => StopReason.ToolUse,
        "function_call"   => StopReason.ToolUse,
        "length"          => StopReason.MaxTokens,
        "max_tokens"      => StopReason.MaxTokens,
        "content_filter"  => StopReason.Refusal,
        null or ""        => StopReason.EndTurn,
        _                 => StopReason.Other
    };
}
