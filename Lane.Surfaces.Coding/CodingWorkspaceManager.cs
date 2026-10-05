using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Lifecycle;
using Lane.Core.Memory;
using Lane.Core.Sessions;
using Lane.Surfaces.Coding.Claude;
using Lane.Surfaces.Coding.Git;
using Lane.Surfaces.Coding.Permissions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lane.Surfaces.Coding;

public interface ICodingWorkspaces
{
    IReadOnlyCollection<CodingWorkspace> All { get; }

    bool TryGet(SurfaceId surface, [NotNullWhen(true)] out CodingWorkspace? workspace);

    CodingWorkspace? Find(string id);

    /// <summary>
    /// Opens the project with this name: an open one is returned as it is, a closed one is
    /// reopened, and otherwise a new directory with a git repository is created.
    /// </summary>
    /// <param name="gitHubRepo">"owner/name" to set as origin of a new project, or null.</param>
    Task<CodingWorkspaceOpening> OpenAsync(string name, string purpose, string? gitHubRepo, CancellationToken ct);

    /// <summary>Stops a surface Lane opened and forgets it. The directory is kept.</summary>
    Task<bool> CloseAsync(string id, CancellationToken ct);

    /// <summary>A person spoke in this session; the loop guard starts counting again.</summary>
    void NoteHumanActivity(SessionId session);

    IReadOnlyList<PermissionRequest> PendingPermissions(string id);
}

public enum OpeningOutcome { Created, Reopened, AlreadyOpen }

public sealed record CodingWorkspaceOpening(CodingWorkspace Workspace, OpeningOutcome Outcome);

public sealed partial class CodingWorkspaceManager(
    CodingOptions        options,
    IServiceProvider     services,
    IKeyValueStore       store,
    PermissionBroker     broker,
    PermissionMcpServer? permissions,
    IHttpClientFactory   http,
    ILoggerFactory       loggers) : ICodingWorkspaces, IHostedService, IAsyncDisposable
{
    private static readonly ScopeKey Scope = new("global");

    private const string WorkspacePrefix = "coding-workspace:";
    private const string SessionPrefix   = "coding-claude-session:";

    private readonly ConcurrentDictionary<string, CodingWorkspace> _workspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _opening = new(1, 1);
    private readonly ILogger _log = loggers.CreateLogger<CodingWorkspaceManager>();

    public IReadOnlyCollection<CodingWorkspace> All => [.. _workspaces.Values.OrderBy(w => w.Id, StringComparer.Ordinal)];

    public bool TryGet(SurfaceId surface, [NotNullWhen(true)] out CodingWorkspace? workspace)
    {
        workspace = null;

        return surface.Value.StartsWith(CodingWorkspace.SurfacePrefix, StringComparison.Ordinal) &&
               _workspaces.TryGetValue(surface.Value[CodingWorkspace.SurfacePrefix.Length..], out workspace);
    }

    public CodingWorkspace? Find(string id) => _workspaces.GetValueOrDefault(id.Trim());

    public void NoteHumanActivity(SessionId session)
    {
        if (TryGet(session.Surface, out CodingWorkspace? workspace)) workspace.Surface?.Resume();
    }

    public IReadOnlyList<PermissionRequest> PendingPermissions(string id) => broker.PendingFor(id);

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (CodingWorkspaceOptions configured in options.Workspaces)
        {
            if (!IsValidId(configured.Id))
            {
                _log.LogError("Coding workspace id '{Id}' must be 1–32 letters, digits, '-' or '_'; skipping it", configured.Id);
                continue;
            }

            string path = CodingOptions.ExpandHome(configured.Path);

            if (!Directory.Exists(path))
            {
                _log.LogError("Coding workspace '{Id}' points at {Path}, which does not exist; skipping it", configured.Id, path);
                continue;
            }

            await StartWorkspaceAsync(Build(
                configured.Id, configured.Name ?? configured.Id, path, configured.Purpose,
                configured.SelfHosted, dynamic: false,
                configured.GitHubToken ?? options.GitHubToken, configured.Model), ct).ConfigureAwait(false);
        }

        foreach (string key in await store.ListKeysAsync(Scope, WorkspacePrefix, ct).ConfigureAwait(false))
        {
            WorkspaceRecord? record = await store.GetAsync<WorkspaceRecord>(Scope, key, ct).ConfigureAwait(false);

            if (record is null || _workspaces.ContainsKey(record.Id)) continue;

            if (!Directory.Exists(record.Path))
            {
                _log.LogWarning("Coding workspace '{Id}' was at {Path}, which is gone; not reopening it", record.Id, record.Path);
                continue;
            }

            await StartWorkspaceAsync(Build(
                record.Id, record.Name, record.Path, record.Purpose,
                selfHosted: false, dynamic: true, options.GitHubToken, model: null), ct).ConfigureAwait(false);
        }

        if (!_workspaces.IsEmpty)
            _log.LogInformation("{Count} coding workspace(s): {Ids}", _workspaces.Count, string.Join(", ", _workspaces.Keys.Order()));
    }

    public async Task<CodingWorkspaceOpening> OpenAsync(string name, string purpose, string? gitHubRepo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A coding surface needs a name.");

        GitHubRepository? remote = null;

        if (!string.IsNullOrWhiteSpace(gitHubRepo))
        {
            remote = GitHubClient.ParseRemote($"https://github.com/{gitHubRepo.Trim().Trim('/')}")
                     ?? throw new ArgumentException($"'{gitHubRepo}' is not an owner/name GitHub repository.");
        }

        await _opening.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            string id   = Slug(name);
            string path = Path.Combine(options.ResolvedWorkspacesRoot, id);

            if (_workspaces.TryGetValue(id, out CodingWorkspace? open))
                return new CodingWorkspaceOpening(open, OpeningOutcome.AlreadyOpen);

            OpeningOutcome outcome;

            if (Directory.Exists(Path.Combine(path, ".git")))
            {
                outcome = OpeningOutcome.Reopened;
            }
            else if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new InvalidOperationException($"{path} already exists, is not empty, and is not a git repository.");
            }
            else
            {
                outcome = OpeningOutcome.Created;
                Directory.CreateDirectory(path);
            }

            CodingWorkspace workspace = Build(
                id, name.Trim(), path, purpose, selfHosted: false, dynamic: true, options.GitHubToken, model: null);

            if (outcome == OpeningOutcome.Created) await InitialiseRepositoryAsync(workspace, remote, ct).ConfigureAwait(false);

            await store.SetAsync(Scope, WorkspacePrefix + id,
                new WorkspaceRecord(id, workspace.Name, path, purpose, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            await StartWorkspaceAsync(workspace, ct).ConfigureAwait(false);

            if (!_workspaces.ContainsKey(id))
                throw new InvalidOperationException($"The coding surface for {path} failed to start; see the log.");

            _log.LogInformation("{Outcome} coding workspace '{Id}' at {Path}", outcome, id, path);

            return new CodingWorkspaceOpening(workspace, outcome);
        }
        finally { _opening.Release(); }
    }

    private async Task InitialiseRepositoryAsync(CodingWorkspace workspace, GitHubRepository? remote, CancellationToken ct)
    {
        await RequireAsync(workspace.Git, ct, "init", "-b", "main").ConfigureAwait(false);

        StringBuilder readme = new();
        readme.Append("# ").AppendLine(workspace.Name);

        if (!string.IsNullOrWhiteSpace(workspace.Purpose)) readme.AppendLine().AppendLine(workspace.Purpose.Trim());

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "README.md"), readme.ToString(), ct).ConfigureAwait(false);

        await RequireAsync(workspace.Git, ct, "add", "-A").ConfigureAwait(false);
        await RequireAsync(workspace.Git, ct, "commit", "-m", $"Start {workspace.Name}").ConfigureAwait(false);

        if (remote is not null)
            await RequireAsync(workspace.Git, ct, "remote", "add", "origin", $"https://github.com/{remote}.git").ConfigureAwait(false);
    }

    private static async Task RequireAsync(GitRunner git, CancellationToken ct, params string[] args)
    {
        ProcessResult result = await git.RunAsync(ct, args).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Output.Trim()}");
    }

    public async Task<bool> CloseAsync(string id, CancellationToken ct)
    {
        if (!_workspaces.TryGetValue(id.Trim(), out CodingWorkspace? workspace)) return false;

        if (!workspace.Dynamic)
            throw new InvalidOperationException($"'{workspace.Id}' is configured, not opened by you, so it cannot be closed.");

        _workspaces.TryRemove(workspace.Id, out _);

        await store.RemoveAsync(Scope, WorkspacePrefix + workspace.Id, ct).ConfigureAwait(false);

        await StopWorkspaceAsync(workspace).ConfigureAwait(false);

        _log.LogInformation("Closed coding workspace '{Id}'; {Path} is left in place", workspace.Id, workspace.Path);

        return true;
    }

    private CodingWorkspace Build(
        string id, string name, string path, string? purpose,
        bool selfHosted, bool dynamic, string? gitHubToken, string? model)
    {
        ClaudeLaunchSettings launch = new()
        {
            ClaudePath         = options.ClaudePath,
            WorkingDirectory   = path,
            Model              = model ?? options.Model,
            AutoAllow          = options.AutoAllow,
            AlwaysDeny         = options.AlwaysDeny,
            MaxBudgetUsd       = options.MaxBudgetUsd,
            AppendSystemPrompt = ClaudeInstructions(selfHosted),
            McpConfig          = permissions is null ? null : () => permissions.McpConfigFor(id),
            PermissionTool     = $"mcp__{PermissionMcpServer.ServerName}__{PermissionMcpServer.ToolName}",
            PermissionTimeout  = options.PermissionTimeout,
            PassEnvironment    = options.PassEnvironment
        };

        return new CodingWorkspace
        {
            Id         = id,
            Name       = name,
            Path       = path,
            Purpose    = purpose,
            SelfHosted = selfHosted,
            Dynamic    = dynamic,
            Claude     = new ClaudeCodeProcess(
                launch, new StoredClaudeSession(store, SessionPrefix + id), loggers.CreateLogger($"Lane.Coding.{id}")),
            Git        = new GitRunner(path, options.GitAuthor, gitHubToken),
            GitHub     = string.IsNullOrWhiteSpace(gitHubToken)
                ? null
                : new GitHubClient(http.CreateClient(GitHubClient.HttpClientName), gitHubToken)
        };
    }

    private static string ClaudeInstructions(bool selfHosted)
    {
        StringBuilder sb = new();

        sb.Append("You are being driven by Lane, an AI chatbot, through a headless session. ")
          .Append("Lane's messages are your user messages, and Lane, not a human, answers your permission requests. ")
          .Append("Keep replies short and concrete: say what you changed and whether it builds and passes its tests. ")
          .Append("Do not push to remotes; Lane handles that.");

        if (selfHosted)
            sb.Append(" This repository is Lane's own source code, and Lane is running from a build of it. ")
              .Append("Lane rebuilds and restarts herself when you are done; never stop, restart or redeploy the running bot yourself.");

        return sb.ToString();
    }

    private async Task StartWorkspaceAsync(CodingWorkspace workspace, CancellationToken ct)
    {
        try
        {
            await StartSurfaceAsync(workspace, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _workspaces.TryRemove(workspace.Id, out _);
            _log.LogError(ex, "Coding workspace '{Id}' failed to start and will be unavailable", workspace.Id);
        }
    }

    private async Task StartSurfaceAsync(CodingWorkspace workspace, CancellationToken ct)
    {
        CodingSurface surface = new(
            workspace,
            options,
            services.GetRequiredService<IAgentKernel>(),
            services.GetRequiredService<ISessionRegistry>(),
            services.GetRequiredService<ISessionDescriptions>(),
            services.GetRequiredService<IEventBus>(),
            broker,
            services.GetService<IRestartCoordinator>(),
            loggers.CreateLogger($"Lane.Coding.{workspace.Id}"));

        workspace.Surface = surface;
        _workspaces[workspace.Id] = workspace;

        await surface.StartAsync(ct).ConfigureAwait(false);
    }

    private async Task StopWorkspaceAsync(CodingWorkspace workspace)
    {
        try
        {
            if (workspace.Surface is not null) await workspace.Surface.StopAsync(CancellationToken.None).ConfigureAwait(false);

            await workspace.Claude.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Coding workspace '{Id}' did not stop cleanly", workspace.Id);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await Task.WhenAll(_workspaces.Values.Select(StopWorkspaceAsync)).ConfigureAwait(false);

        _workspaces.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _opening.Dispose();
    }

    internal static string Slug(string name)
    {
        string slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');

        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');

        return slug.Length > 0 ? slug : "workspace";
    }

    internal static bool IsValidId(string id) => ValidId().IsMatch(id);

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ValidId();

    private sealed record WorkspaceRecord(string Id, string Name, string Path, string? Purpose, DateTimeOffset CreatedAt);

    private sealed class StoredClaudeSession(IKeyValueStore store, string key) : IClaudeSessionStore
    {
        public ValueTask<string?> LoadAsync(CancellationToken ct) => store.GetAsync<string>(Scope, key, ct);

        public ValueTask SaveAsync(string? sessionId, CancellationToken ct) => sessionId is null
            ? store.RemoveAsync(Scope, key, ct)
            : store.SetAsync(Scope, key, sessionId, ct);
    }
}
