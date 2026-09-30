using System.ComponentModel;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Replies;

/// <summary>
/// Lets Lane back out of a turn the response policy let through — a message that turned out
/// not to be for her, or that needs nothing from her.
/// </summary>
[LaneTool]
public sealed class StopRespondingTool(ILogger<StopRespondingTool> log) : Tool<StopRespondingTool.Args>
{
    public sealed record Args(
        [property: Description("Why you are not replying, in a few words. Only you see it.")] string? Reason = null);

    protected override string Name => "stop_responding";

    protected override string Description =>
        "Decide not to reply after all: the message was not meant for you, or needs nothing from you. " +
        "Call it before writing anything — your turn ends straight away and nothing more is sent.";

    protected override ToolAvailability Availability =>
        new() { AllowedTurns = TurnKind.Respond, RequiresSession = true };

    protected override ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        log.LogInformation("Not replying in {Session}: {Reason}",
            context.Session, string.IsNullOrWhiteSpace(args.Reason) ? "(no reason given)" : args.Reason.Trim());

        return ValueTask.FromResult(ToolResult.Ok("Not replying.") with { EndsTurn = true });
    }
}
