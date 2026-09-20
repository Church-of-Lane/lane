using System.ComponentModel;
using System.Text;
using Lane.Core.Context;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Monologue;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Lane.Tools.Monologue;

/// <summary>
/// Lane's view of where she could speak. Without it <c>speak_to_session</c> has nothing to
/// name, and she would be choosing a conversation blind.
/// </summary>
[LaneTool]
public sealed class ListSessionsTool : Tool<NoArgs>
{
    protected override string Name => "list_sessions";

    protected override string Description =>
        "List the conversations currently open, with who is in them and how long they have been quiet. " +
        "Use before speak_to_session so you know what you are speaking into.";

    protected override ToolAvailability Availability => new() { AllowedTurns = TurnKind.Monologue };

    protected override ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        ISessionRegistry? sessions = context.Sessions;

        if (sessions is null) return ValueTask.FromResult(ToolResult.Error("No session registry is available."));

        Session[] active = [.. sessions.Active.OrderBy(s => s.Id.Value, StringComparer.Ordinal)];

        if (active.Length == 0) return ValueTask.FromResult(ToolResult.Ok("(no open conversations)"));

        StringBuilder sb = new();

        foreach (Session session in active)
        {
            TimeSpan idle = DateTimeOffset.UtcNow - session.LastActivity;

            string people = string.Join(", ", session.Descriptor.KnownParticipants
                .Where(p => !p.IsLane)
                .Select(p => p.DisplayName));

            sb.Append(session.Id.Value)
              .Append(" — ").Append(session.Descriptor.DisplayName)
              .Append(people.Length > 0 ? $"; with {people}" : "; nobody named yet")
              .Append($"; {session.State}, quiet for {(int)idle.TotalMinutes}m")
              .AppendLine();
        }

        return ValueTask.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
    }
}

/// <summary>
/// Replaces v2's <c>speak</c> and <c>message</c> JSON fields, which could only ever reach
/// whichever channel happened to be most recent.
/// </summary>
[LaneTool]
public sealed class SpeakToSessionTool : Tool<SpeakToSessionTool.Args>
{
    public sealed record Args(
        [property: Description("Session id exactly as list_sessions gave it.")] string SessionId,
        [property: Description("What to say, in your own voice. Not a description of what you would say.")] string Message);

    protected override string Name => "speak_to_session";

    protected override string Description =>
        "Say something unprompted in one of the open conversations. Use sparingly — only when " +
        "you actually have something worth interrupting for.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => new() { AllowedTurns = TurnKind.Monologue };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Message)) return ToolResult.Error("There is nothing to say.");

        if (!SessionId.TryParse(args.SessionId, out SessionId? id))
            return ToolResult.Error($"'{args.SessionId}' is not a session id. Call list_sessions first.");

        if (context.Sessions?.TryGet(id, out Session? session) != true)
            return ToolResult.Error($"No conversation '{args.SessionId}' is open. Call list_sessions first.");

        IAgentKernel? kernel = context.Services.GetService<IAgentKernel>();

        if (kernel is null) return ToolResult.Error("No kernel is available to speak through.");

        // Posted to that session's queue rather than written to its channel. That is what
        // makes a volunteered remark wait its turn behind a reply already in flight, and be
        // remembered in the right conversation.
        await kernel.PostAsync(
            id, new SessionWorkItem.Speak(args.Message.Trim(), DeliveryTarget.Primary, "monologue"), ct)
            .ConfigureAwait(false);

        return ToolResult.Ok($"Said to {session!.Descriptor.DisplayName}.");
    }
}

/// <summary>Replaces v2's <c>next_thought_in_seconds</c> field.</summary>
[LaneTool]
public sealed class ScheduleNextThoughtTool : Tool<ScheduleNextThoughtTool.Args>
{
    public sealed record Args(
        [property: Description("Seconds until you next want to think.")] int Seconds,
        [property: Description("Why — one short phrase.")] string Reason = "");

    protected override string Name => "schedule_next_thought";

    protected override string Description =>
        "Decide when to think next. Sooner if something is unfolding, much later if nothing is.";

    protected override ToolAvailability Availability => new() { AllowedTurns = TurnKind.Monologue };

    protected override ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (args.Seconds <= 0) return ValueTask.FromResult(ToolResult.Error("That has to be a positive number."));

        IMonologueScheduler? scheduler = context.Services.GetService<IMonologueScheduler>();

        if (scheduler is null) return ValueTask.FromResult(ToolResult.Error("Nothing is scheduling thoughts."));

        // Clamped inside the scheduler, so a model asking to think every second cannot
        // turn her inner life into a billing incident.
        scheduler.Schedule(TimeSpan.FromSeconds(args.Seconds),
            string.IsNullOrWhiteSpace(args.Reason) ? "she decided" : args.Reason.Trim());

        DateTimeOffset? next = scheduler.Status.NextThoughtAt;

        if (next is null) return ValueTask.FromResult(ToolResult.Ok("Scheduled."));

        ITranscriptFormatter? formatter = context.Services.GetService<ITranscriptFormatter>();

        string when = formatter is null
            ? next.Value.ToString("HH:mm:ss")
            : formatter.FormatTime(next.Value, new TranscriptFormatOptions(IncludeRelativeTime: false));

        return ValueTask.FromResult(ToolResult.Ok($"Next thought at {when}."));
    }
}
