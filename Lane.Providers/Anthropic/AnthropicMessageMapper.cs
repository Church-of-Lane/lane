using System.Text.Json;
using Anthropic.Models.Messages;
using Lane.Core.Context;
using Lane.Core.Messages;
using Lane.Core.Models;
using LaneContent = Lane.Core.Messages.ContentPart;
using LaneTool = Lane.Core.Tools.ToolDescriptor;
using LaneToolChoice = Lane.Core.Models.ToolChoice;
using LaneStopReason = Lane.Core.Models.StopReason;
using AnthropicToolChoice = Anthropic.Models.Messages.ToolChoice;
using AnthropicStopReason = Anthropic.Models.Messages.StopReason;

namespace Lane.Providers.Anthropic;

/// <summary>
/// Translates between Lane's message model and the Anthropic wire types.
///
/// All provider knowledge lives here rather than on <c>LaneMessage</c> — v2 put
/// <c>Anthropic()</c> and <c>OpenAI()</c> methods on the message itself, which meant the
/// core model could not change without touching every provider and vice versa.
/// </summary>
internal static class AnthropicMessageMapper
{
    public static List<TextBlockParam> ToSystemBlocks(IReadOnlyList<PromptBlock> blocks)
    {
        List<TextBlockParam> result = [];

        foreach (PromptBlock block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;

            // Cache breakpoints are per block and order-sensitive: everything before the
            // last marked block is cached. Blocks are never reordered here.
            result.Add(block.Cache == CacheHint.None
                ? new TextBlockParam { Text = block.Text }
                : new TextBlockParam { Text = block.Text, CacheControl = new CacheControlEphemeral() });
        }

        return result;
    }

    /// <summary>
    /// Maps Lane messages onto Anthropic turns, merging consecutive same-role messages.
    /// A coalesced batch arrives as several user messages; the API wants one turn.
    /// </summary>
    public static List<MessageParam> ToMessages(IReadOnlyList<LaneMessage> messages)
    {
        List<MessageParam> result = [];
        List<ContentBlockParam> pending = [];
        Role? pendingRole = null;

        foreach (LaneMessage message in messages)
        {
            Role role = ToRole(message.Role);

            List<ContentBlockParam> blocks = ToBlocks(message);
            if (blocks.Count == 0) continue;

            // The conversation must open on a user turn; a leading assistant message
            // (a stale reply at the head of a recalled window) is dropped rather than sent.
            if (result.Count == 0 && pendingRole is null && role == Role.Assistant) continue;

            if (pendingRole == role)
            {
                pending.AddRange(blocks);
                continue;
            }

            Flush(result, pending, pendingRole);

            pendingRole = role;
            pending     = blocks;
        }

        Flush(result, pending, pendingRole);

        return result;
    }

    private static void Flush(List<MessageParam> into, List<ContentBlockParam> blocks, Role? role)
    {
        if (role is null || blocks.Count == 0) return;

        into.Add(new MessageParam { Role = role.Value, Content = blocks });
    }

    /// <summary>Tool results ride in a user turn — that is the API's shape, not a Lane choice.</summary>
    private static Role ToRole(LaneRole role) => role switch
    {
        LaneRole.Assistant => Role.Assistant,
        _                  => Role.User
    };

    private static List<ContentBlockParam> ToBlocks(LaneMessage message)
    {
        List<ContentBlockParam> blocks = [];

        bool wroteText = false;

        foreach (LaneContent part in message.Content)
        {
            switch (part)
            {
                case TextPart { Text: var text } when !string.IsNullOrEmpty(text):
                    // Only the first text part carries the speaker's name; a message with
                    // several text parts is still one utterance.
                    blocks.Add(new TextBlockParam { Text = PromptText.Render(message, text, !wroteText) });
                    wroteText = true;
                    break;

                case ImagePart image when ToImageBlock(image) is { } imageBlock:
                    blocks.Add(imageBlock);
                    break;

                case ToolUsePart tool:
                    blocks.Add(new ToolUseBlockParam
                    {
                        ID    = tool.ToolCallId,
                        Name  = tool.ToolName,
                        Input = ToInputDictionary(tool.Arguments)
                    });
                    break;

                case ToolResultPart result:
                    blocks.Add(new ToolResultBlockParam
                    {
                        ToolUseID = result.ToolCallId,
                        IsError   = result.IsError,
                        Content   = FlattenToolResult(result)
                    });
                    break;

                case ThinkingPart { Signature: not null } thinking:
                    // Signatures must round-trip byte for byte or the follow-up turn is rejected.
                    blocks.Add(new ThinkingBlockParam
                    {
                        Thinking  = thinking.Text,
                        Signature = thinking.Signature
                    });
                    break;
            }
        }

        return blocks;
    }

    private static ToolResultBlockParamContent FlattenToolResult(ToolResultPart result)
    {
        string text = string.Concat(result.Content.OfType<TextPart>().Select(p => p.Text));

        if (!result.Content.Any(p => p is ImagePart)) return text;

        List<Block> blocks = [];

        if (text.Length > 0) blocks.Add(new TextBlockParam { Text = text });

        foreach (ImagePart image in result.Content.OfType<ImagePart>())
            if (ToImageBlock(image) is { } block) blocks.Add(block);

        return blocks;
    }

    private static ImageBlockParam? ToImageBlock(ImagePart image)
    {
        if (image.Url is not null) return new ImageBlockParam { Source = new(new UrlImageSource(image.Url.ToString())) };

        if (image.Data is not { } data) return null;

        return new ImageBlockParam
        {
            Source = new(new Base64ImageSource
            {
                Data      = Convert.ToBase64String(data.Span),
                MediaType = image.MediaType switch
                {
                    "image/png"  => MediaType.ImagePng,
                    "image/gif"  => MediaType.ImageGif,
                    "image/webp" => MediaType.ImageWebP,
                    _            => MediaType.ImageJpeg
                }
            })
        };
    }

    private static Dictionary<string, JsonElement> ToInputDictionary(JsonElement arguments)
    {
        Dictionary<string, JsonElement> input = [];

        if (arguments.ValueKind != JsonValueKind.Object) return input;

        foreach (JsonProperty property in arguments.EnumerateObject()) input[property.Name] = property.Value;

        return input;
    }

    public static List<ToolUnion> ToTools(IReadOnlyList<LaneTool> tools)
    {
        List<ToolUnion> result = [];

        foreach (LaneTool tool in tools)
        {
            result.Add(new ToolUnion(new Tool
            {
                Name        = tool.Name,
                Description = tool.Description,
                InputSchema = ToInputSchema(tool.InputSchema)
            }, null));
        }

        return result;
    }

    private static InputSchema ToInputSchema(JsonElement schema)
    {
        Dictionary<string, JsonElement> properties = [];
        List<string> required = [];

        if (schema.TryGetProperty("properties", out JsonElement props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in props.EnumerateObject()) properties[property.Name] = property.Value;
        }

        if (schema.TryGetProperty("required", out JsonElement req) && req.ValueKind == JsonValueKind.Array)
        {
            required.AddRange(req.EnumerateArray().Select(e => e.GetString()).OfType<string>());
        }

        return new InputSchema
        {
            Type       = JsonSerializer.SerializeToElement("object"),
            Properties = properties,
            Required   = required
        };
    }

    public static AnthropicToolChoice? ToToolChoice(LaneToolChoice choice) => choice.Mode switch
    {
        ToolChoiceMode.Auto     => null,                                        // the API default
        ToolChoiceMode.None     => new AnthropicToolChoice(new ToolChoiceNone(), null),
        ToolChoiceMode.Required => new AnthropicToolChoice(new ToolChoiceAny(), null),
        ToolChoiceMode.Specific => new AnthropicToolChoice(new ToolChoiceTool { Name = choice.ToolName! }, null),
        _                       => null
    };

    public static List<LaneContent> FromContent(IReadOnlyList<ContentBlock> content)
    {
        List<LaneContent> parts = [];

        foreach (ContentBlock block in content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                parts.Add(new TextPart(text.Text));
            }
            else if (block.TryPickToolUse(out ToolUseBlock? tool))
            {
                parts.Add(new ToolUsePart(
                    tool.ID,
                    tool.Name,
                    JsonSerializer.SerializeToElement(tool.Input)));
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                parts.Add(new ThinkingPart(thinking.Thinking, thinking.Signature));
            }
        }

        return parts;
    }

    public static LaneStopReason FromStopReason(AnthropicStopReason reason) => reason switch
    {
        AnthropicStopReason.EndTurn      => LaneStopReason.EndTurn,
        AnthropicStopReason.ToolUse      => LaneStopReason.ToolUse,
        AnthropicStopReason.MaxTokens    => LaneStopReason.MaxTokens,
        AnthropicStopReason.StopSequence => LaneStopReason.StopSequence,
        AnthropicStopReason.Refusal      => LaneStopReason.Refusal,
        _                                => LaneStopReason.Other
    };
}
