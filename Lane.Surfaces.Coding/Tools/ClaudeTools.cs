using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Lane.Core.Lifecycle;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Surfaces.Coding.Permissions;
using Microsoft.Extensions.DependencyInjection;

namespace Lane.Surfaces.Coding.Tools;

internal static class Workspaces
{
    public static ToolAvailability InWorkspace { get; } =
        new() { RequiredSessionTag = CodingSurface.WorkspaceTag, AllowedTurns = TurnKind.Respond };

    public static bool TryResolve(
        ToolContext context, [NotNullWhen(true)] out CodingWorkspace? workspace, [NotNullWhen(false)] out ToolResult? error)
    {
        workspace = null;
        error     = null;

        ICodingWorkspaces? workspaces = context.Services.GetService<ICodingWorkspaces>();

        if (workspaces is null || context.Descriptor is null || !workspaces.TryGet(context.Descriptor.Id.Surface, out workspace))
        {
            error = ToolResult.Error("This conversation is not a coding session.");
            return false;
        }

        return true;
    }
}

[LaneTool]
public sealed class ClaudeStatusTool : Tool<NoArgs>
{
    protected override string Name => "claude_status";

    protected override string Description =>
        "Show what Claude Code is doing in this coding session: whether it is busy, what it has cost, " +
        "any permission requests waiting on you, and the git branch and changed files.";

    protected override ToolAvailability Availability => Workspaces.InWorkspace;

    protected override async ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error)) return error;

        StringBuilder sb = new();

        sb.Append("Claude Code: ").Append(workspace.Claude.Busy ? "working" : workspace.Claude.Running ? "idle" : "not started")
          .Append(CultureInfo.InvariantCulture, $", ${workspace.Claude.TotalCostUsd:0.00} so far")
          .AppendLine(workspace.Claude.SessionId is { } id ? $", session {id}" : "");

        if (workspace.Surface?.Paused == true)
            sb.AppendLine("Paused: Claude Code's latest reply is held until someone else speaks here.");

        PermissionBroker? broker = context.Services.GetService<PermissionBroker>();

        foreach (PermissionRequest request in broker?.PendingFor(workspace.Id) ?? [])
            sb.Append("Waiting on you: permission request ").Append(request.Id).Append(" for ").AppendLine(request.ToolName);

        ProcessResult status = await workspace.Git.RunAsync(ct, "status", "--short", "--branch").ConfigureAwait(false);

        sb.AppendLine().Append(status.Succeeded ? Clip(status.Output, 40) : "(not a git repository)");

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static string Clip(string text, int lines)
    {
        string[] all = text.TrimEnd().Split('\n');

        return all.Length <= lines ? string.Join('\n', all) : string.Join('\n', all.Take(lines)) + $"\n…and {all.Length - lines} more";
    }
}

[LaneTool]
public sealed class ClaudeInterruptTool : Tool<NoArgs>
{
    protected override string Name => "claude_interrupt";

    protected override string Description => "Stop what Claude Code is doing in this coding session, part way through.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => Workspaces.InWorkspace;

    protected override async ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error)) return error;

        bool interrupted = await workspace.Claude.InterruptAsync(ct).ConfigureAwait(false);

        return ToolResult.Ok(interrupted ? "Interrupted." : "Claude Code was not doing anything.");
    }
}

[LaneTool]
public sealed class ClaudeNewSessionTool : Tool<ClaudeNewSessionTool.Args>
{
    public sealed record Args(
        [property: Description("Optional first message for the new session.")] string? OpeningMessage = null);

    protected override string Name => "claude_new_session";

    protected override string Description =>
        "End the current Claude Code conversation in this coding session and start a fresh one with no memory of it. " +
        "Use when its context is cluttered or you are moving to an unrelated task.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => Workspaces.InWorkspace;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error)) return error;

        await workspace.Claude.ResetAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(args.OpeningMessage)) return ToolResult.Ok("Started fresh. Your next message begins the new session.");

        await workspace.Claude.SendAsync(args.OpeningMessage.Trim(), ct).ConfigureAwait(false);

        return ToolResult.Ok("Started fresh and sent your opening message.");
    }
}

[LaneTool]
public sealed class AnswerClaudePermissionTool : Tool<AnswerClaudePermissionTool.Args>
{
    public sealed record Args(
        [property: Description("The five-letter id from the permission request.")] string RequestId,
        [property: Description("True to let Claude Code go ahead.")] bool Allow,
        [property: Description("When denying, why, so Claude Code can do something else.")] string? Reason = null);

    protected override string Name => "answer_claude_permission";

    protected override string Description =>
        "Allow or deny something Claude Code asked permission to do. Deny anything destructive, anything outside " +
        "the workspace, or anything you do not understand.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => Workspaces.InWorkspace;

    protected override ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error))
            return ValueTask.FromResult(error);

        PermissionBroker? broker = context.Services.GetService<PermissionBroker>();

        if (broker is null || !broker.Answer(workspace.Id, args.RequestId, args.Allow, args.Reason))
            return ValueTask.FromResult(ToolResult.Error($"No open permission request '{args.RequestId}' here. It may have timed out."));

        return ValueTask.FromResult(ToolResult.Ok(args.Allow ? "Allowed." : "Denied."));
    }
}
