using System.ComponentModel;
using Lane.Core.Lifecycle;
using Lane.Core.Tools;
using Lane.Surfaces.Coding.Git;

namespace Lane.Surfaces.Coding.Tools;

[LaneTool]
public sealed class GitTool : Tool<GitTool.Args>
{
    public sealed record Args(
        [property: Description(
            "status | diff | log | commit | branches | switch | pull | push | set_remote. " +
            "commit stages every change first. switch needs name, and create to make a new branch. " +
            "set_remote needs url.")]
        string Action,
        [property: Description("Commit message, for commit.")] string? Message = null,
        [property: Description("Branch name, for switch.")] string? Name = null,
        [property: Description("For switch: create the branch.")] bool Create = false,
        [property: Description("For diff: only what is staged.")] bool Staged = false,
        [property: Description("For diff: limit to one path.")] string? Path = null,
        [property: Description("For log: how many commits.")] int Count = 15,
        [property: Description("For set_remote: an https URL for origin.")] string? Url = null);

    protected override string Name => "git";

    protected override string Description =>
        "Use git in this coding session's repository, as yourself. Pushing uses the configured GitHub token. " +
        "There is no force-push, reset or clean.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override TimeSpan Timeout => TimeSpan.FromMinutes(3);

    protected override ToolAvailability Availability => Workspaces.InWorkspace;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error)) return error;

        GitRunner git = workspace.Git;

        switch (args.Action.Trim().ToLowerInvariant())
        {
            case "status":
                return Report(await git.RunAsync(ct, "status", "--short", "--branch").ConfigureAwait(false), "Nothing to report.");

            case "diff":
            {
                List<string> diff = ["diff"];
                if (args.Staged) diff.Add("--staged");
                diff.Add("--stat");
                diff.Add("--patch");
                if (!string.IsNullOrWhiteSpace(args.Path)) { diff.Add("--"); diff.Add(args.Path.Trim()); }

                return Report(await git.RunAsync(ct, [.. diff]).ConfigureAwait(false), "No changes.");
            }

            case "log":
                return Report(await git.RunAsync(ct, "log", "--oneline", "--decorate", "-n",
                    Math.Clamp(args.Count, 1, 100).ToString()).ConfigureAwait(false), "No commits yet.");

            case "commit":
            {
                if (string.IsNullOrWhiteSpace(args.Message)) return ToolResult.Error("A commit needs a message.");

                ProcessResult add = await git.RunAsync(ct, "add", "-A").ConfigureAwait(false);
                if (!add.Succeeded) return Report(add, "");

                ProcessResult commit = await git.RunAsync(ct, "commit", "-m", args.Message.Trim()).ConfigureAwait(false);
                if (!commit.Succeeded) return Report(commit, "");

                return Report(await git.RunAsync(ct, "log", "-1", "--stat", "--oneline").ConfigureAwait(false), "Committed.");
            }

            case "branches":
                return Report(await git.RunAsync(ct, "branch", "--list", "-vv").ConfigureAwait(false), "No branches yet.");

            case "switch":
            {
                if (string.IsNullOrWhiteSpace(args.Name)) return ToolResult.Error("switch needs a branch name.");

                string[] command = args.Create ? ["switch", "-c", args.Name.Trim()] : ["switch", args.Name.Trim()];

                return Report(await git.RunAsync(ct, command).ConfigureAwait(false), $"On {args.Name.Trim()}.");
            }

            case "pull":
                return Report(await git.RunAsync(ct, "pull", "--ff-only").ConfigureAwait(false), "Up to date.");

            case "push":
                return Report(await git.RunAsync(ct, "push", "-u", "origin", "HEAD").ConfigureAwait(false), "Pushed.");

            case "set_remote":
            {
                if (args.Url is not { } url || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme != "https")
                    return ToolResult.Error("set_remote needs an https URL.");

                bool exists = await git.OriginAsync(ct).ConfigureAwait(false) is not null;

                string[] command = exists
                    ? ["remote", "set-url", "origin", uri.ToString()]
                    : ["remote", "add", "origin", uri.ToString()];

                return Report(await git.RunAsync(ct, command).ConfigureAwait(false), $"origin is now {uri}.");
            }

            default:
                return ToolResult.Error($"Unknown action '{args.Action}'.");
        }
    }

    private static ToolResult Report(ProcessResult result, string quiet)
    {
        string output = result.Output.Trim();

        if (result.TimedOut) return ToolResult.Error("git timed out.");

        if (!result.Succeeded) return ToolResult.Error(output.Length > 0 ? output : $"git exited with {result.ExitCode}.");

        return ToolResult.Ok(output.Length > 0 ? output : quiet);
    }
}

[LaneTool]
public sealed class GitHubIssuesTool : Tool<GitHubIssuesTool.Args>
{
    public sealed record Args(
        [property: Description("list or view.")] string Action = "list",
        [property: Description("For view: the issue number.")] int? Number = null,
        [property: Description("For list: open, closed or all.")] string State = "open",
        [property: Description("For list: comma-separated labels to filter by.")] string? Labels = null,
        [property: Description("For list: how many.")] int Count = 20);

    protected override string Name => "github_issues";

    protected override string Description =>
        "Read the GitHub issues of this coding session's repository: list them, or view one with its comments.";

    protected override TimeSpan Timeout => TimeSpan.FromSeconds(45);

    protected override ToolAvailability Availability => Workspaces.InWorkspace;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Workspaces.TryResolve(context, out CodingWorkspace? workspace, out ToolResult? error)) return error;

        if (workspace.GitHub is null) return ToolResult.Error("No GitHub token is configured for this workspace.");

        GitHubRepository? repo = GitHubClient.ParseRemote(await workspace.Git.OriginAsync(ct).ConfigureAwait(false));

        if (repo is null) return ToolResult.Error("This repository's origin is not on GitHub.");

        try
        {
            return args.Action.Trim().ToLowerInvariant() switch
            {
                "list" => ToolResult.Ok(await workspace.GitHub
                    .ListIssuesAsync(repo, NormaliseState(args.State), args.Labels, args.Count, ct).ConfigureAwait(false)),

                "view" when args.Number is { } number => ToolResult.Ok(await workspace.GitHub
                    .ViewIssueAsync(repo, number, ct).ConfigureAwait(false)),

                "view" => ToolResult.Error("view needs an issue number."),

                _ => ToolResult.Error($"Unknown action '{args.Action}'.")
            };
        }
        catch (GitHubException ex)
        {
            return ToolResult.Error(ex.Message);
        }
    }

    private static string NormaliseState(string state) => state.Trim().ToLowerInvariant() switch
    {
        "closed" => "closed",
        "all"    => "all",
        _        => "open"
    };
}
