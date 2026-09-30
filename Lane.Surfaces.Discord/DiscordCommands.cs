using NetCord;
using NetCord.Rest;

namespace Lane.Surfaces.Discord;

/// <summary>The bot's slash commands and what it replies to each.</summary>
public static class DiscordCommands
{
    public const string TermsOfServiceUrl = "https://lane.readthedocs.io/en/latest/discord_tos/";
    public const string PrivacyPolicyUrl  = "https://lane.readthedocs.io/en/latest/discord_privacy_policy/";

    public static IReadOnlyList<SlashCommandProperties> Definitions =>
    [
        Command("tos", "Read Lane's Terms of Service"),
        Command("privacy-policy", "Read Lane's Privacy Policy"),
        Command("opt-in", "Let Lane read and respond to your messages"),
        Command("opt-out", "Make Lane ignore your messages again")
    ];

    /// <summary>The reply to a command, or null for a command this bot does not have.</summary>
    public static string? ReplyTo(string command) => command switch
    {
        "tos"            => $"Lane's Terms of Service: {TermsOfServiceUrl}",
        "privacy-policy" => $"Lane's Privacy Policy: {PrivacyPolicyUrl}",
        "opt-in"         => $"You're opted in. Lane will now read and respond to your messages. " +
                            $"By using Lane you agree to the Terms of Service ({TermsOfServiceUrl}) " +
                            $"and Privacy Policy ({PrivacyPolicyUrl}). Run /opt-out at any time to stop.",
        "opt-out"        => "You're opted out. Lane will ignore your messages until you run /opt-in again.",
        _                => null
    };

    /// <summary>Sent when someone who has not opted in mentions the bot.</summary>
    public const string OptInRequired =
        $"Before Lane can read or respond to your messages, you need to accept the Terms of Service " +
        $"(<{TermsOfServiceUrl}>) and Privacy Policy (<{PrivacyPolicyUrl}>). Run /opt-in to accept them.";

    /// <summary>True for /opt-in, false for /opt-out, null for commands that do not change consent.</summary>
    public static bool? ConsentSetBy(string command) => command switch
    {
        "opt-in"  => true,
        "opt-out" => false,
        _         => null
    };

    private static SlashCommandProperties Command(string name, string description) => new(name, description)
    {
        Contexts         = [InteractionContextType.Guild, InteractionContextType.BotDMChannel],
        IntegrationTypes = [ApplicationIntegrationType.GuildInstall]
    };
}
