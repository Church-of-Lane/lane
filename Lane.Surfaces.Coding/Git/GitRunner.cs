using System.Text;
using System.Text.RegularExpressions;
using Lane.Core.Lifecycle;

namespace Lane.Surfaces.Coding.Git;

/// <summary>
/// The <c>git</c> CLI in one directory, as Lane. A GitHub token goes through <c>GIT_CONFIG_*</c>
/// environment variables, never argv or <c>.git/config</c>. Which token is decided by who owns <c>origin</c>.
/// </summary>
public sealed partial class GitRunner(
    string directory,
    string author,
    GitHubTokens? gitHubTokens,
    IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Instance;

    public string Directory => directory;

    public async Task<ProcessResult> RunAsync(CancellationToken ct, params string[] args) =>
        await RunAsync(await TokenAsync(ct).ConfigureAwait(false), ct, args).ConfigureAwait(false);

    private Task<ProcessResult> RunAsync(string? token, CancellationToken ct, params string[] args) =>
        _runner.RunAsync(new ProcessSpec
        {
            FileName         = "git",
            Arguments        = args,
            WorkingDirectory = directory,
            Environment      = Environment(token),
            Timeout          = TimeSpan.FromMinutes(2),
            MaxOutput        = 24_000
        }, ct);

    /// <summary>The token for whoever owns <c>origin</c> on GitHub, or the default one.</summary>
    internal async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (gitHubTokens is null) return null;

        if (!gitHubTokens.VaryByOwner) return gitHubTokens.For(null);

        return gitHubTokens.For(GitHubClient.ParseRemote(await OriginAsync(ct).ConfigureAwait(false))?.Owner);
    }

    internal IReadOnlyDictionary<string, string?> Environment(string? token)
    {
        (string name, string email) = ParseAuthor(author);

        Dictionary<string, string?> env = new()
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_AUTHOR_NAME"]     = name,
            ["GIT_AUTHOR_EMAIL"]    = email,
            ["GIT_COMMITTER_NAME"]  = name,
            ["GIT_COMMITTER_EMAIL"] = email
        };

        if (token is not null)
        {
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));

            env["GIT_CONFIG_COUNT"]   = "1";
            env["GIT_CONFIG_KEY_0"]   = "http.https://github.com/.extraheader";
            env["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: basic {basic}";
        }

        return env;
    }

    public static (string Name, string Email) ParseAuthor(string author)
    {
        Match match = AuthorPattern().Match(author);

        return match.Success
            ? (match.Groups["name"].Value.Trim(), match.Groups["email"].Value.Trim())
            : (author.Trim(), "lane@localhost");
    }

    /// <summary>The origin remote's URL, or null when there is none.</summary>
    public async Task<string?> OriginAsync(CancellationToken ct)
    {
        ProcessResult result = await RunAsync(null, ct, "remote", "get-url", "origin").ConfigureAwait(false);

        return result.Succeeded ? result.Output.Trim() : null;
    }

    [GeneratedRegex(@"^(?<name>[^<]+)<(?<email>[^>]+)>\s*$")]
    private static partial Regex AuthorPattern();
}
