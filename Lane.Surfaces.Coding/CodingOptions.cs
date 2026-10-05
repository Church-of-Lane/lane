namespace Lane.Surfaces.Coding;

public sealed class CodingOptions
{
    public bool Enabled { get; set; }

    public string ClaudePath { get; set; } = "claude";

    /// <summary>Passed to <c>--model</c> when set.</summary>
    public string? Model { get; set; }

    /// <summary>Where <c>open_coding_surface</c> creates directories. A leading <c>~</c> is the home directory.</summary>
    public string WorkspacesRoot { get; set; } = "~/lane-workspaces";

    public string? GitHubTokenRef { get; set; }

    /// <summary>Resolved from <see cref="GitHubTokenRef"/> at composition.</summary>
    public string? GitHubToken { get; set; }

    /// <summary>"Name &lt;email&gt;", used for Lane's own commits.</summary>
    public string GitAuthor { get; set; } = "Lane <lane@localhost>";

    /// <summary>Loopback port of the MCP server Claude Code asks for permission through.</summary>
    public int PermissionPort { get; set; } = 5085;

    /// <summary>How long Claude Code waits for Lane to answer a permission request before it is denied.</summary>
    public TimeSpan PermissionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Per Claude Code process, passed to <c>--max-budget-usd</c>. Zero or less means no cap.</summary>
    public decimal MaxBudgetUsd { get; set; } = 5;

    /// <summary>Claude replies handed to Lane in a row, with nobody else speaking, before the surface pauses.</summary>
    public int MaxUnattendedExchanges { get; set; } = 20;

    /// <summary>Permission rules Claude Code applies without asking (<c>--allowedTools</c>).</summary>
    public List<string> AutoAllow { get; set; } = [];

    /// <summary>Permission rules Claude Code refuses without asking (<c>--disallowedTools</c>).</summary>
    public List<string> AlwaysDeny { get; set; } = [];

    /// <summary>
    /// Environment variables Claude Code inherits even though they look like secrets. Every other
    /// inherited variable whose name contains KEY, TOKEN, SECRET or PASSWORD is removed.
    /// </summary>
    public List<string> PassEnvironment { get; set; } = [];

    /// <summary>
    /// Global user ids who may have Lane open or close coding projects in conversation. The
    /// monologue may always; with this empty, nobody else may.
    /// </summary>
    public List<string> Openers { get; set; } = [];

    public List<CodingWorkspaceOptions> Workspaces { get; set; } = [];

    public static IReadOnlyList<string> DefaultAutoAllow { get; } =
    [
        "Read", "Glob", "Grep", "Edit", "Write", "TodoWrite",
        "Bash(dotnet build *)", "Bash(dotnet test *)",
        "Bash(git status *)", "Bash(git diff *)", "Bash(git log *)", "Bash(git show *)"
    ];

    public static IReadOnlyList<string> DefaultAlwaysDeny { get; } =
    [
        "Bash(git push *)", "Bash(rm -rf *)", "Bash(sudo *)"
    ];

    public static IReadOnlyList<string> DefaultPassEnvironment { get; } =
    [
        "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"
    ];

    public string ResolvedWorkspacesRoot => ExpandHome(WorkspacesRoot);

    public static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/'))
            : path;
}

public sealed class CodingWorkspaceOptions
{
    /// <summary>Letters, digits, '-' and '_'. Becomes the surface id <c>coding.{Id}</c>.</summary>
    public string Id { get; set; } = "";

    public string? Name { get; set; }

    public string Path { get; set; } = "";

    public string? Purpose { get; set; }

    /// <summary>This is Lane's own source. Enables <c>restart_self</c>.</summary>
    public bool SelfHosted { get; set; }

    public string? GitHubTokenRef { get; set; }

    /// <summary>Resolved from <see cref="GitHubTokenRef"/> at composition.</summary>
    public string? GitHubToken { get; set; }

    public string? Model { get; set; }
}
