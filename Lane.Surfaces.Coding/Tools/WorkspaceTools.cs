using System.ComponentModel;
using Lane.Core.Lifecycle;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Lane.Surfaces.Coding.Tools;

[LaneTool]
public sealed class RestartSelfTool : Tool<RestartSelfTool.Args>
{
    public sealed record Args(
        [property: Description("What changed, in a phrase. You are told it when you come back.")] string Reason);

    protected override string Name => "restart_self";

    protected override string Description =>
        "Rebuild yourself from this source and restart onto the new build. Nothing happens unless the build " +
        "succeeds; then you go offline for a minute or so. Use when Claude Code has finished a change and it builds.";

    protected override ToolSafety Safety => ToolSafety.Dangerous;

    protected override TimeSpan Timeout => TimeSpan.FromMinutes(15);

    protected override ToolAvailability Availability =>
        new() { RequiredSessionTag = CodingSurface.SelfTag, AllowedTurns = TurnKind.Respond };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error)) return error;

        if (!workspace.SelfHosted) return ToolResult.Error("This workspace is not your own source.");

        if (workspace.Claude.Busy) return ToolResult.Error("Claude Code is still working. Wait for it to finish, or interrupt it.");

        IRestartCoordinator? restart = context.Services.GetService<IRestartCoordinator>();

        if (restart is null) return ToolResult.Error("Nothing here can restart you.");

        RestartOutcome outcome = await restart
            .BuildAndRestartAsync(workspace.Path, string.IsNullOrWhiteSpace(args.Reason) ? "no reason given" : args.Reason.Trim(), ct)
            .ConfigureAwait(false);

        string text = outcome.Log is { Length: > 0 } log ? $"{outcome.Message}\n\n{log}" : outcome.Message;

        return outcome.Status == RestartStatus.Restarting ? ToolResult.Ok(text) : ToolResult.Error(text);
    }
}

[LaneTool]
public sealed class OpenCodingSurfaceTool : Tool<OpenCodingSurfaceTool.Args>
{
    public sealed record Args(
        [property: Description("A short name for the project.")] string Name,
        [property: Description("What it is for, in a sentence or two.")] string Purpose,
        [property: Description("Optional existing GitHub repository, owner/name, to use as its origin.")] string? GitHubRepo = null);

    protected override string Name => "open_coding_surface";

    protected override string Description =>
        "Start a new coding project: a fresh directory and git repository, with Claude Code to work in it. " +
        "It appears as a conversation you can speak into with speak_to_session to tell Claude Code what to build.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override TimeSpan Timeout => TimeSpan.FromMinutes(1);

    protected override ToolAvailability Availability => new() { AllowedTurns = TurnKind.Monologue };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        ICodingWorkspaces? workspaces = context.Services.GetService<ICodingWorkspaces>();

        if (workspaces is null) return ToolResult.Error("Coding is not enabled.");

        try
        {
            CodingWorkspace workspace = await workspaces.OpenAsync(args.Name, args.Purpose, args.GitHubRepo, ct).ConfigureAwait(false);

            return ToolResult.Ok($"Opened {workspace.Path}. Its conversation is {workspace.SessionId.Value}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}

[LaneTool]
public sealed class CloseCodingSurfaceTool : Tool<CloseCodingSurfaceTool.Args>
{
    public sealed record Args(
        [property: Description("The workspace id: the part after 'coding.' in its session id.")] string Id);

    protected override string Name => "close_coding_surface";

    protected override string Description =>
        "Close a coding project you opened. Its directory and repository are kept; only the conversation ends.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => new() { AllowedTurns = TurnKind.Monologue };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        ICodingWorkspaces? workspaces = context.Services.GetService<ICodingWorkspaces>();

        if (workspaces is null) return ToolResult.Error("Coding is not enabled.");

        try
        {
            return await workspaces.CloseAsync(args.Id, ct).ConfigureAwait(false)
                ? ToolResult.Ok($"Closed {args.Id}.")
                : ToolResult.Error($"No coding project '{args.Id}' is open.");
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }
}
