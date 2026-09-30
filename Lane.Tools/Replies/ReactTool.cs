using System.ComponentModel;
using Lane.Core.Sessions;
using Lane.Core.Tools;

namespace Lane.Tools.Replies;

/// <summary>Reacts to the message the turn is answering. Only offered where the channel supports reactions.</summary>
[LaneTool]
public sealed class ReactTool : Tool<ReactTool.Args>
{
    public sealed record Args(
        [property: Description("One emoji, e.g. 👍 or 😭, or a server emoji written <:name:id>.")] string Emoji);

    protected override string Name => "react";

    protected override string Description =>
        "React to the message you are answering with an emoji. A reaction can be the whole reply: " +
        "if it is all you want to say, call stop_responding after it.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => new()
    {
        RequiredCapabilities = ChannelCapabilities.Reactions,
        AllowedTurns         = TurnKind.Respond,
        RequiresSession      = true
    };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string emoji = args.Emoji?.Trim() ?? "";

        if (emoji.Length == 0) return ToolResult.Error("Which emoji?");

        if (context.TriggerExternalId is not { } message)
            return ToolResult.Error("There is no message here to react to.");

        if (context.Session is not { } id || context.Sessions is null ||
            !context.Sessions.TryGet(id, out Session? session))
            return ToolResult.Error("I cannot tell which conversation this is.");

        IReadOnlyList<IReactionOutput> outputs =
            session.ResolveOutputs<IReactionOutput>(DeliveryTarget.Requiring(ChannelCapabilities.Reactions));

        if (outputs.Count == 0) return ToolResult.Error("You cannot react to messages here.");

        try
        {
            await outputs[0].ReactAsync(message, emoji, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        return ToolResult.Ok($"Reacted with {emoji}.");
    }
}
