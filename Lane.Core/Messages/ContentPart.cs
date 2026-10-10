using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lane.Core.Messages;

/// <summary>
/// One piece of a message. Modelled as parts rather than a single string because
/// tool_use/tool_result blocks must survive as structure — flattening them to text
/// breaks the next request to the provider.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$kind")]
[JsonDerivedType(typeof(TextPart),       "text")]
[JsonDerivedType(typeof(ImagePart),      "image")]
[JsonDerivedType(typeof(AudioPart),      "audio")]
[JsonDerivedType(typeof(ToolUsePart),    "tool_use")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
[JsonDerivedType(typeof(ThinkingPart),   "thinking")]
public abstract record ContentPart;

public sealed record TextPart(string Text) : ContentPart;

/// <summary>An image by reference or by value. Exactly one of <paramref name="Url"/> or
/// <paramref name="Data"/> is expected to be set.</summary>
public sealed record ImagePart(Uri? Url, ReadOnlyMemory<byte>? Data, string MediaType) : ContentPart;

/// <summary>
/// A clip of audio by value. Only ever in flight: it is stripped before a message is
/// persisted, and before a request to a model without <c>ModelCapabilities.Audio</c>.
/// </summary>
/// <param name="Format">The container, as providers name it — "wav", "mp3".</param>
public sealed record AudioPart(ReadOnlyMemory<byte> Data, string Format) : ContentPart;

/// <summary>The model asking for a tool to run.</summary>
public sealed record ToolUsePart(string ToolCallId, string ToolName, JsonElement Arguments) : ContentPart;

/// <summary>The answer to exactly one <see cref="ToolUsePart"/>. Every tool_use must get one.</summary>
public sealed record ToolResultPart(string ToolCallId, IReadOnlyList<ContentPart> Content, bool IsError) : ContentPart
{
    public static ToolResultPart Text(string toolCallId, string text, bool isError = false) =>
        new(toolCallId, [new TextPart(text)], isError);
}

/// <summary>Extended-thinking passthrough. <paramref name="Signature"/> must round-trip
/// verbatim or providers reject the follow-up turn.</summary>
public sealed record ThinkingPart(string Text, string? Signature) : ContentPart;
