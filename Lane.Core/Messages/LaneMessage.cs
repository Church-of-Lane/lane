using Lane.Core.Identity;

using Lane.Core.Models;

namespace Lane.Core.Messages;

/// <summary>Time-ordered so it doubles as a good database primary key.</summary>
public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString("n");
}

public enum LaneRole { System, User, Assistant, Tool }

public enum MessageKind
{
    /// <summary>Something said or heard in a session.</summary>
    Utterance,

    /// <summary>Lane's interior monologue. Belongs to no session.</summary>
    Thought,

    /// <summary>Tool output or a system note ("X joined the voice channel") promoted to memory.</summary>
    Observation,

    /// <summary>A rolling summary produced by a memory handler.</summary>
    Summary
}

/// <summary>
/// The one message type the whole harness passes around.
///
/// Deliberately absent, compared to v2's MessageContainer: any provider conversion
/// (that lives in Lane.Providers), any "Lane says:" string munging (identity is carried
/// structurally now), and any timestamp formatting (that is a rendering concern, see
/// <see cref="Lane.Core.Context.ITranscriptFormatter"/>).
/// </summary>
public sealed record LaneMessage
{
    public required MessageId   Id        { get; init; }

    /// <summary>Null for messages that belong to no conversation — a monologue thought.</summary>
    public SessionId?           Session   { get; init; }

    public required LaneRole    Role      { get; init; }
    public required MessageKind Kind      { get; init; }
    public required Participant Author    { get; init; }
    public required IReadOnlyList<ContentPart> Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Monotonic ordering, assigned by the transcript store on append. 0 = not yet stored.</summary>
    public long Sequence { get; init; }

    /// <summary>The originating platform's id, where there is one (a Discord message id).</summary>
    public string? ExternalId { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>All text parts concatenated. Empty when the message is purely tool traffic.</summary>
    public string TextContent => string.Concat(Content.OfType<TextPart>().Select(p => p.Text));

    public bool HasToolUse => Content.Any(p => p is ToolUsePart);

    public LaneMessage WithoutAudio() =>
        Content.Any(p => p is AudioPart) ? this with { Content = [.. Content.Where(p => p is not AudioPart)] } : this;

    /// <summary>
    /// <see cref="Metadata"/> key a surface sets when a message's text only describes the images
    /// on it, such as Discord's attachment listing, so it says nothing to a model that cannot see them.
    /// </summary>
    public const string ImagesOnlyKey = "images-only";

    /// <summary>Stands in for an image sent to a model without <see cref="ModelCapabilities.Images"/>.</summary>
    public const string UnseenImage = "[an image you cannot see]";

    /// <summary>True when nothing in the message can be understood without seeing its images.</summary>
    public bool NeedsImages =>
        Content.Any(p => p is ImagePart) &&
        (Metadata?.ContainsKey(ImagesOnlyKey) == true ||
         !Content.OfType<TextPart>().Any(t => !string.IsNullOrWhiteSpace(t.Text)));

    /// <summary>
    /// This message as a model with the given capabilities can take it. Audio is dropped;
    /// images, including those in tool results, become <see cref="UnseenImage"/>.
    /// </summary>
    public LaneMessage For(ModelCapabilities capabilities)
    {
        bool audio  = capabilities.HasFlag(ModelCapabilities.Audio);
        bool images = capabilities.HasFlag(ModelCapabilities.Images);

        if (audio && images) return this;
        if (!Content.Any(p => (p is AudioPart && !audio) || (!images && HasImage(p)))) return this;

        return this with { Content = Strip(Content, audio, images) };
    }

    private static bool HasImage(ContentPart part) =>
        part is ImagePart || part is ToolResultPart result && result.Content.Any(HasImage);

    private static List<ContentPart> Strip(IReadOnlyList<ContentPart> content, bool audio, bool images)
    {
        List<ContentPart> kept = [];

        foreach (ContentPart part in content)
        {
            switch (part)
            {
                case AudioPart when !audio:
                    break;

                case ImagePart when !images:
                    kept.Add(new TextPart(UnseenImage));
                    break;

                case ToolResultPart result when !images && result.Content.Any(HasImage):
                    kept.Add(result with { Content = Strip(result.Content, audio, images) });
                    break;

                default:
                    kept.Add(part);
                    break;
            }
        }

        return kept;
    }

    public static LaneMessage User(
        SessionId session, Participant author, string text, DateTimeOffset timestamp, string? externalId = null) =>
        new()
        {
            Id = MessageId.New(), Session = session, Role = LaneRole.User, Kind = MessageKind.Utterance,
            Author = author, Content = [new TextPart(text)], Timestamp = timestamp, ExternalId = externalId
        };

    public static LaneMessage User(
        SessionId session, Participant author, IReadOnlyList<ContentPart> content,
        DateTimeOffset timestamp, string? externalId = null) =>
        new()
        {
            Id = MessageId.New(), Session = session, Role = LaneRole.User, Kind = MessageKind.Utterance,
            Author = author, Content = content, Timestamp = timestamp, ExternalId = externalId
        };

    public static LaneMessage Assistant(
        SessionId? session, Participant lane, IReadOnlyList<ContentPart> content, DateTimeOffset timestamp) =>
        new()
        {
            Id = MessageId.New(), Session = session, Role = LaneRole.Assistant, Kind = MessageKind.Utterance,
            Author = lane, Content = content, Timestamp = timestamp
        };

    public static LaneMessage Assistant(SessionId? session, Participant lane, string text, DateTimeOffset timestamp) =>
        Assistant(session, lane, [new TextPart(text)], timestamp);

    /// <summary>
    /// The results of one round of tool calls, as a single user-role turn. Providers require
    /// results to arrive together and in call order, so they are never split across messages.
    /// </summary>
    public static LaneMessage ToolResults(
        SessionId? session, Participant lane, IReadOnlyList<ToolResultPart> results, DateTimeOffset timestamp) =>
        new()
        {
            Id = MessageId.New(), Session = session, Role = LaneRole.Tool, Kind = MessageKind.Observation,
            Author = lane, Content = [.. results], Timestamp = timestamp
        };

    public static LaneMessage Thought(Participant lane, string text, DateTimeOffset timestamp) =>
        new()
        {
            Id = MessageId.New(), Session = null, Role = LaneRole.Assistant, Kind = MessageKind.Thought,
            Author = lane, Content = [new TextPart(text)], Timestamp = timestamp
        };

    public static LaneMessage Observation(
        SessionId? session, Participant author, string text, DateTimeOffset timestamp) =>
        new()
        {
            Id = MessageId.New(), Session = session, Role = LaneRole.User, Kind = MessageKind.Observation,
            Author = author, Content = [new TextPart(text)], Timestamp = timestamp
        };
}
