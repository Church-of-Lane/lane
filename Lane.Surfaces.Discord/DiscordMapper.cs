using Lane.Core.Identity;
using Lane.Core.Sessions;

namespace Lane.Surfaces.Discord;

/// <summary>One row of an embed's field table.</summary>
internal sealed record EmbedFieldView(string? Name, string? Value);

/// <summary>
/// A Discord embed reduced to the parts worth reading.
///
/// The gateway type carries layout with it — colours, widths, proxy urls — none of which
/// mean anything to Lane, and depending on it would drag NetCord into the tests. This is
/// what survives the trip.
/// </summary>
internal sealed record EmbedView
{
    public string? Title        { get; init; }
    public string? Url          { get; init; }
    public string? Description  { get; init; }
    public string? AuthorName   { get; init; }
    public string? AuthorUrl    { get; init; }
    public string? Provider     { get; init; }
    public string? Footer       { get; init; }
    public string? ImageUrl     { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? VideoUrl     { get; init; }

    public IReadOnlyList<EmbedFieldView> Fields { get; init; } = [];
}

/// <summary>
/// The parts of the Discord surface that are pure text and identity work.
///
/// Kept apart from the gateway glue so they can be tested without a live connection —
/// mention resolution and message splitting are exactly the things that break quietly and
/// only in production otherwise.
/// </summary>
internal static class DiscordMapper
{
    /// <summary>Discord rejects anything longer than this.</summary>
    public const int MessageLimit = 2000;

    public static string BuildMemoryGroup(string template, string surface, ulong? guildId, ulong channelId) =>
        template
            .Replace("{surface}", surface, StringComparison.Ordinal)
            .Replace("{guild}", guildId?.ToString() ?? "dm", StringComparison.Ordinal)
            .Replace("{channel}", channelId.ToString(), StringComparison.Ordinal);

    public static SessionDescriptor Describe(
        SurfaceId surface,
        ulong channelId,
        ulong? guildId,
        string channelName,
        string memoryGroupTemplate,
        IReadOnlyList<Participant> participants)
    {
        bool isDirect = guildId is null;

        return new SessionDescriptor
        {
            Id          = new SessionId(surface, SessionKind.Text, channelId.ToString()),
            DisplayName = isDirect ? $"DM: {channelName}" : $"#{channelName}",
            MemoryGroup = BuildMemoryGroup(memoryGroupTemplate, surface.Value, guildId, channelId),

            // Images are accepted; voice arrives with the audio work.
            Capabilities = ChannelCapabilities.Text | ChannelCapabilities.Images | ChannelCapabilities.Typing |
                           ChannelCapabilities.Reactions,

            IsDirect          = isDirect,
            KnownParticipants = participants
        };
    }

    /// <summary>
    /// Turns raw mention markup into readable names.
    ///
    /// Without this the model sees <c>&lt;@2313…&gt;</c> and has no idea it was addressed,
    /// or by whom.
    /// </summary>
    public static string ResolveMentions(
        string content,
        IReadOnlyDictionary<ulong, string> users,
        IReadOnlyDictionary<ulong, string> roles)
    {
        if (string.IsNullOrEmpty(content)) return content;

        foreach ((ulong id, string name) in users)
        {
            content = content.Replace($"<@{id}>", $"@{name}", StringComparison.Ordinal)
                             .Replace($"<@!{id}>", $"@{name}", StringComparison.Ordinal);
        }

        foreach ((ulong id, string name) in roles)
            content = content.Replace($"<@&{id}>", $"@{name}", StringComparison.Ordinal);

        return content;
    }

    /// <summary>Adds the quoted message a reply was aimed at, so the reference is not lost.</summary>
    public static string AppendReplyContext(string content, string? replyAuthor, string? replyContent)
    {
        if (string.IsNullOrWhiteSpace(replyAuthor) || string.IsNullOrWhiteSpace(replyContent)) return content;

        return $"{content}\n\n(replying to {replyAuthor}: \"{Truncate(replyContent, QuoteLimit)}\")";
    }

    /// <summary>
    /// A unicode emoji as its name, or a custom one written <c>&lt;:name:id&gt;</c>,
    /// <c>&lt;a:name:id&gt;</c> or <c>name:id</c> as its name and id. Null when there is nothing to react with.
    /// </summary>
    public static (string Name, ulong? Id)? ParseEmoji(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji)) return null;

        string text = emoji.Trim();

        if (text.StartsWith('<') && text.EndsWith('>')) text = text[1..^1];
        if (text.StartsWith("a:", StringComparison.Ordinal)) text = text[2..];

        string[] pieces = text.Trim(':').Split(':');

        if (pieces.Length == 2 && pieces[0].Length > 0 && ulong.TryParse(pieces[1], out ulong id))
            return (pieces[0], id);

        // Discord answers a shortcode such as :thumbsup: with Unknown Emoji.
        if (text.Contains(':') || text.Any(char.IsAsciiLetter)) return null;

        return (text, null);
    }

    /// <summary>One line per attachment, so the url of an image or file is something she can quote and pass on.</summary>
    public static string RenderAttachments(IReadOnlyList<(string FileName, string Url, string? ContentType)> attachments)
    {
        if (attachments.Count == 0) return "";

        return string.Join('\n', attachments.Select(a =>
            a.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                ? $"[image: {a.FileName}] {a.Url}"
                : $"[file: {a.FileName}] {a.Url}"));
    }

    /// <summary>A file name for an outgoing image, with the extension Discord needs to show it inline.</summary>
    public static string AttachmentFileName(int index, string mediaType) =>
        $"image{(index == 0 ? "" : $"-{index + 1}")}.{mediaType switch
        {
            "image/jpeg" => "jpg",
            "image/gif"  => "gif",
            "image/webp" => "webp",
            _            => "png"
        }}";

    // ---- embeds ------------------------------------------------------------

    /// <summary>Embeds rendered per message. Discord allows ten; past a few it is spam.</summary>
    public const int MaxEmbeds = 5;

    private const int QuoteLimit      = 300;
    private const int DescriptionLimit = 600;
    private const int FieldValueLimit  = 200;
    private const int MaxFields        = 10;

    /// <summary>
    /// Every embed on a message, as one readable block.
    ///
    /// A link someone dropped, a bot's answer, an article worth reading: all of it arrives
    /// as structure hanging off an otherwise empty message. Unrendered, Lane sees people
    /// posting nothing and replying to nothing.
    /// </summary>
    public static string RenderEmbeds(IReadOnlyList<EmbedView> embeds)
    {
        if (embeds.Count == 0) return "";

        List<string> rendered = [];

        foreach (EmbedView embed in embeds.Take(MaxEmbeds))
        {
            string block = RenderEmbed(embed);

            if (block.Length > 0) rendered.Add(block);
        }

        if (rendered.Count == 0) return "";

        int hidden = embeds.Count - MaxEmbeds;

        if (hidden > 0) rendered.Add($"(+{hidden} more embed{(hidden == 1 ? "" : "s")})");

        return string.Join("\n\n", rendered);
    }

    /// <summary>
    /// One embed as the lines a person would take from the card.
    ///
    /// Links are kept beside the text they belong to rather than stripped: half of what an
    /// embed is worth is the thing it points at.
    /// </summary>
    public static string RenderEmbed(EmbedView embed)
    {
        List<string> lines = [];

        if (Linked(embed.Title, embed.Url) is { } title) lines.Add(title);

        if (Linked(embed.AuthorName, embed.AuthorUrl) is { } author) lines.Add($"by {author}");

        if (!string.IsNullOrWhiteSpace(embed.Description))
            lines.Add(Truncate(embed.Description, DescriptionLimit));

        foreach (EmbedFieldView field in embed.Fields.Take(MaxFields))
        {
            bool hasName  = !string.IsNullOrWhiteSpace(field.Name);
            bool hasValue = !string.IsNullOrWhiteSpace(field.Value);

            if (!hasName && !hasValue) continue;

            string value = hasValue ? Truncate(field.Value!, FieldValueLimit) : "";

            lines.Add(hasName ? $"{field.Name!.Trim()}: {value}".TrimEnd() : value);
        }

        if (embed.Fields.Count > MaxFields) lines.Add($"(+{embed.Fields.Count - MaxFields} more fields)");

        if (!string.IsNullOrWhiteSpace(embed.Footer)) lines.Add(Truncate(embed.Footer, FieldValueLimit));

        // The image is also handed over as an ImagePart where the url looks like one, but the
        // link itself still belongs in the text: not every embed image is a format she can see.
        if (Url(embed.ImageUrl) is { } image)         lines.Add($"image: {image}");
        if (Url(embed.ThumbnailUrl) is { } thumbnail) lines.Add($"thumbnail: {thumbnail}");
        if (Url(embed.VideoUrl) is { } video)         lines.Add($"video: {video}");

        if (lines.Count == 0) return "";

        string header = string.IsNullOrWhiteSpace(embed.Provider)
            ? "[embed]"
            : $"[embed from {embed.Provider.Trim()}]";

        return $"{header}\n{string.Join('\n', lines)}";
    }

    /// <summary>
    /// The picture an embed carries, where it is one Lane can actually look at.
    ///
    /// The full image is preferred over the thumbnail — they are usually the same picture,
    /// and the thumbnail is the version too small to read anything off.
    /// </summary>
    public static (Uri Url, string MediaType)? EmbedImage(EmbedView embed)
    {
        foreach (string? candidate in new[] { embed.ImageUrl, embed.ThumbnailUrl })
        {
            if (Url(candidate) is not { } url) continue;
            if (ImageTypeOf(url) is not { } type) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)) continue;

            return (parsed, type);
        }

        return null;
    }

    /// <summary>
    /// The media type a url names, or null when it names nothing she can see.
    ///
    /// Guessed from the path because an embed carries no content type — Discord's cdn hangs
    /// a signature on the query string, so that has to come off first.
    /// </summary>
    public static string? ImageTypeOf(string url)
    {
        int query = url.IndexOfAny(['?', '#']);

        ReadOnlySpan<char> path = query < 0 ? url : url.AsSpan(0, query);

        int dot = path.LastIndexOf('.');
        if (dot < 0) return null;

        return path[(dot + 1)..].ToString().ToLowerInvariant() switch
        {
            "png"          => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif"          => "image/gif",
            "webp"         => "image/webp",
            _              => null
        };
    }

    private static string? Url(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    /// <summary>Text with its link beside it, whichever of the two there is.</summary>
    private static string? Linked(string? text, string? url) =>
        (string.IsNullOrWhiteSpace(text), Url(url)) switch
        {
            (false, { } link) => $"{text!.Trim()} ({link})",
            (false, null)     => text!.Trim(),
            (true, { } link)  => link,
            _                 => null
        };

    private static string Truncate(string text, int limit) =>
        text.Length > limit ? text[..limit].TrimEnd() + "…" : text;

    /// <summary>
    /// Breaks a reply into pieces Discord will accept, preferring paragraph then line then
    /// word boundaries. A reply over the limit is otherwise rejected outright, which loses
    /// the whole turn rather than trimming it.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int limit = MessageLimit)
    {
        if (string.IsNullOrEmpty(text)) return [];
        if (text.Length <= limit) return [text];

        List<string> parts = [];
        ReadOnlySpan<char> remaining = text.AsSpan();

        while (remaining.Length > limit)
        {
            int cut = FindBreak(remaining, limit);

            parts.Add(remaining[..cut].ToString().TrimEnd());

            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0) parts.Add(remaining.ToString());

        return parts;
    }

    private static int FindBreak(ReadOnlySpan<char> text, int limit)
    {
        ReadOnlySpan<char> window = text[..limit];

        int paragraph = window.LastIndexOf("\n\n");
        if (paragraph > limit / 4) return paragraph;

        int line = window.LastIndexOf('\n');
        if (line > limit / 4) return line;

        int space = window.LastIndexOf(' ');
        if (space > limit / 4) return space;

        // A single unbroken run longer than the limit — a URL or a wall of code.
        return limit;
    }

    /// <summary>
    /// The display name a person should be known by: their chosen global name where they
    /// have one, otherwise their handle.
    /// </summary>
    public static string DisplayNameOf(string? globalName, string username) =>
        string.IsNullOrWhiteSpace(globalName) ? username : globalName;
}
