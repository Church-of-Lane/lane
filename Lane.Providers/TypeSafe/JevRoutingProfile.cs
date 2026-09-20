using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lane.Providers.TypeSafe;

/// <summary>
/// The two questions Jev is asked in place of the routing prompt.
///
/// For a System One model the criteria *are* the prompt: there is no prose to be persuasive
/// in, only levels and options. So the score bands that used to live in Routing.md live here
/// instead, and the emoticon — which a chat model invented freely — becomes a closed roster,
/// because a Choice question has to enumerate what it is choosing between.
///
/// Loadable from JSON so the roster can be retuned without a rebuild, which is the same
/// bargain the prompt templates make.
/// </summary>
public sealed record JevRoutingProfile
{
    [JsonPropertyName("enthusiasm")]
    public JevScoreQuestion Enthusiasm { get; init; } = new();

    [JsonPropertyName("emoticon")]
    public JevChoiceQuestion Emoticon { get; init; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true
    };

    /// <summary>Reads a profile from disk, or returns the defaults when the file is absent.</summary>
    public static JevRoutingProfile Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new JevRoutingProfile();

        JevRoutingProfile? loaded = JsonSerializer.Deserialize<JevRoutingProfile>(File.ReadAllText(path), ReadOptions);

        return loaded?.Validated(path) ?? new JevRoutingProfile();
    }

    private JevRoutingProfile Validated(string path)
    {
        if (Enthusiasm.Levels.Count is < 2 or > 10)
            throw new InvalidOperationException(
                $"{path}: a score question takes between 2 and 10 levels, not {Enthusiasm.Levels.Count}.");

        if (Emoticon.Faces.Count == 0)
            throw new InvalidOperationException($"{path}: the emoticon roster is empty.");

        if (Emoticon.Faces.Count > 255)
            throw new InvalidOperationException(
                $"{path}: a choice question takes at most 255 options, not {Emoticon.Faces.Count}.");

        return this;
    }
}

/// <param name="Levels">Ordered low to high. Each is evaluated on its own — the model never sees
/// its neighbours or its position — so no description may lean on another to make sense.</param>
public sealed record JevScoreQuestion
{
    public string Instructions { get; init; } =
        "How much does the latest message call for a reply from Lane? Lane is a person in this " +
        "conversation rather than an assistant waiting to be summoned: answering everything would " +
        "make her tiresome, answering nothing would make her absent.";

    public IReadOnlyList<string> Levels { get; init; } =
    [
        "The message is addressed to someone else by name, or it is empty, a duplicate, or cut off " +
        "mid-thought, or it is a bare reaction with nothing in it to answer.",

        "The message is small talk addressed to the room as a whole, about a subject that has " +
        "nothing to do with Lane.",

        "The message continues a conversation Lane is already taking part in, or asks a " +
        "straightforward question she could answer.",

        "The message addresses Lane by name, asks her something directly, concerns a subject she " +
        "cares about, or is personal or expressive in a way that makes a reply matter."
    ];
}

public sealed record JevChoiceQuestion
{
    public string Instructions { get; init; } =
        "Which face best reflects how Lane takes the latest message? Choose for her reaction to " +
        "what was said, not for how eager she is to reply.";

    /// <summary>Face to what it conveys. Both the key and the description are shown to the model.</summary>
    public IReadOnlyDictionary<string, string> Faces { get; init; } = new Dictionary<string, string>
    {
        [":3"]     = "Pleased and fond. Something charming, playful, or aimed warmly at her.",
        [":D"]     = "Delighted and excited. Good news, or a subject she is enthusiastic about.",
        ["^_^"]    = "Happy and at ease. Friendly chatter she is glad to be part of.",
        ["( ._.)"] = "Quiet and subdued. Nothing much to react to, or she is unsure of her footing.",
        ["o_o"]    = "Surprised and caught off guard. Something unexpected or startling.",
        [">_>"]    = "Sceptical, side-eyeing. A claim she doubts, or teasing she sees coming.",
        [">:3"]    = "Mischievous and smug. She has the upper hand, or an opening she intends to take.",
        ["-_-"]    = "Unamused. A tired joke, a repetition, or someone being deliberately obtuse.",
        [";_;"]    = "Moved or sympathetic. Somebody is upset, or something landed emotionally.",
        ["@_@"]    = "Overwhelmed or lost. Too much at once, or she cannot follow what was said.",
        [":/"]     = "Awkward and uncomfortable. Tension in the room, or a subject she would rather avoid.",
        ["..."]    = "Deliberately blank. Nothing worth reacting to, or a silence that says more."
    };
}
