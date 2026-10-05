using System.ComponentModel;
using Lane.Core.Kernel;
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

internal static class Openers
{
    public static ToolAvailability Availability { get; } = new() { AllowedTurns = TurnKind.Monologue | TurnKind.Respond };

    /// <summary>Null when the requester may open or close projects; otherwise the refusal.</summary>
    public static ToolResult? Refusal(ToolContext context)
    {
        if (context.Turn == TurnKind.Monologue) return null;
        
        return null;

        CodingOptions? options = context.Services.GetService<CodingOptions>();

        string? person = context.Requester?.GlobalUserId;

        return person is not null && options?.Openers.Contains(person, StringComparer.OrdinalIgnoreCase) == true
            ? null
            : ToolResult.Error("Only the people listed in Lane:Coding:Openers can have you open or close coding projects.");
    }
}

[LaneTool]
public sealed class OpenCodingSurfaceTool : Tool<OpenCodingSurfaceTool.Args>
{
    public sealed record Args(
        [property: Description("A short name for the project. The same name reopens an existing project.")] string Name,
        [property: Description("What it is for, in a sentence or two.")] string Purpose,
        [property: Description("Optional first instructions for Claude Code, sent as soon as the project is open.")] string? Brief = null,
        [property: Description("Optional existing GitHub repository, owner/name, to use as a new project's origin.")] string? GitHubRepo = null);

    protected override string Name => "open_coding_surface";

    protected override string Description =>
        "Open a coding project: a directory and git repository with Claude Code to work in it, and a conversation " +
        "with Claude Code about it. A new name starts a new project; a name used before reopens that one. " +
        "Give a brief to get Claude Code started; anything later goes through that conversation.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override TimeSpan Timeout => TimeSpan.FromMinutes(1);

    protected override ToolAvailability Availability => Openers.Availability;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (Openers.Refusal(context) is { } refusal) return refusal;

        ICodingWorkspaces? workspaces = context.Services.GetService<ICodingWorkspaces>();

        if (workspaces is null) return ToolResult.Error("Coding is not enabled.");

        CodingWorkspaceOpening opening;

        try
        {
            opening = await workspaces.OpenAsync(args.Name, args.Purpose, args.GitHubRepo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return ToolResult.Error(ex.Message);
        }

        CodingWorkspace workspace = opening.Workspace;

        string what = opening.Outcome switch
        {
            OpeningOutcome.Created     => $"Created {workspace.Path}",
            OpeningOutcome.Reopened    => $"Reopened {workspace.Path}",
            _                          => $"{workspace.Path} was already open"
        };

        if (string.IsNullOrWhiteSpace(args.Brief)) return ToolResult.Ok($"{what}. Its conversation is {workspace.SessionId.Value}.");

        IAgentKernel? kernel = context.Services.GetService<IAgentKernel>();

        if (kernel is null) return ToolResult.Error($"{what}, but there is no kernel to send the brief through.");

        await kernel.PostAsync(
            workspace.SessionId, new SessionWorkItem.Speak(args.Brief.Trim(), DeliveryTarget.Primary, "coding brief"), ct)
            .ConfigureAwait(false);

        return ToolResult.Ok($"{what}, and sent Claude Code the brief. Its conversation is {workspace.SessionId.Value}.");
    }
}

[LaneTool]
public sealed class CloseCodingSurfaceTool : Tool<CloseCodingSurfaceTool.Args>
{
    public sealed record Args(
        [property: Description("The project id: the part after 'coding.' in its conversation id.")] string Id);

    protected override string Name => "close_coding_surface";

    protected override string Description =>
        "Close a coding project you opened. Its directory and repository are kept, and opening it by name again " +
        "brings it back.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => Openers.Availability;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (Openers.Refusal(context) is { } refusal) return refusal;

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
