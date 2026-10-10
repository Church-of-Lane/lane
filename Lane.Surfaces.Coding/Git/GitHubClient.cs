using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lane.Surfaces.Coding.Git;

public sealed record GitHubRepository(string Owner, string Name)
{
    public override string ToString() => $"{Owner}/{Name}";
}

/// <summary>Reads a repository's issues and creates repositories, with a personal access token.</summary>
public sealed partial class GitHubClient(HttpClient http, string token)
{
    public const string HttpClientName = "lane-github";

    private const int MaxBody        = 4_000;
    private const int MaxDescription = 350;

    /// <summary>Accepts https and ssh forms of a github.com remote; anything else is null.</summary>
    public static GitHubRepository? ParseRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        Match match = RemotePattern().Match(url.Trim());

        return match.Success ? new GitHubRepository(match.Groups["owner"].Value, match.Groups["repo"].Value) : null;
    }

    public async Task<string> ListIssuesAsync(
        GitHubRepository repo, string state, string? labels, int count, CancellationToken ct)
    {
        string query = $"state={Uri.EscapeDataString(state)}&per_page={Math.Clamp(count, 1, 50)}";

        if (!string.IsNullOrWhiteSpace(labels)) query += $"&labels={Uri.EscapeDataString(labels)}";

        using JsonDocument doc = await GetAsync($"repos/{repo}/issues?{query}", ct).ConfigureAwait(false);

        StringBuilder sb = new();

        foreach (JsonElement issue in doc.RootElement.EnumerateArray())
        {
            // The issues endpoint also returns pull requests.
            if (issue.TryGetProperty("pull_request", out _)) continue;

            sb.Append('#').Append(issue.GetProperty("number").GetInt32())
              .Append(" [").Append(issue.GetProperty("state").GetString()).Append("] ")
              .Append(issue.GetProperty("title").GetString());

            string[] labelNames = [.. issue.GetProperty("labels").EnumerateArray()
                .Select(l => l.TryGetProperty("name", out JsonElement n) ? n.GetString() : null).OfType<string>()];

            if (labelNames.Length > 0) sb.Append(" (").Append(string.Join(", ", labelNames)).Append(')');

            sb.Append(" — ").Append(issue.GetProperty("comments").GetInt32()).AppendLine(" comment(s)");
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : $"No {state} issues in {repo}.";
    }

    public async Task<string> ViewIssueAsync(GitHubRepository repo, int number, CancellationToken ct)
    {
        using JsonDocument issue = await GetAsync($"repos/{repo}/issues/{number}", ct).ConfigureAwait(false);

        JsonElement root = issue.RootElement;

        StringBuilder sb = new();

        sb.Append('#').Append(number).Append(' ').AppendLine(root.GetProperty("title").GetString())
          .Append("State: ").Append(root.GetProperty("state").GetString())
          .Append(" · opened by ").Append(root.GetProperty("user").GetProperty("login").GetString())
          .Append(" · ").AppendLine(root.GetProperty("html_url").GetString())
          .AppendLine()
          .AppendLine(Clip(root.TryGetProperty("body", out JsonElement body) ? body.GetString() : null));

        if (root.GetProperty("comments").GetInt32() > 0)
        {
            using JsonDocument comments = await GetAsync($"repos/{repo}/issues/{number}/comments?per_page=20", ct)
                .ConfigureAwait(false);

            foreach (JsonElement comment in comments.RootElement.EnumerateArray())
            {
                sb.AppendLine()
                  .Append("— ").Append(comment.GetProperty("user").GetProperty("login").GetString()).AppendLine(":")
                  .AppendLine(Clip(comment.GetProperty("body").GetString()));
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Creates an empty repository, in the token's own account when <paramref name="owner"/> is null or names it.</summary>
    public async Task<GitHubRepository> CreateRepositoryAsync(
        string? owner, string name, string? description, bool isPrivate, CancellationToken ct)
    {
        string path = "user/repos";

        if (owner is not null)
        {
            using JsonDocument user = await GetAsync("user", ct).ConfigureAwait(false);

            if (!string.Equals(user.RootElement.GetProperty("login").GetString(), owner, StringComparison.OrdinalIgnoreCase))
                path = $"orgs/{Uri.EscapeDataString(owner)}/repos";
        }

        Dictionary<string, object> body = new() { ["name"] = name, ["private"] = isPrivate };

        if (!string.IsNullOrWhiteSpace(description))
        {
            string flat = Whitespace().Replace(description.Trim(), " ");

            body["description"] = flat.Length <= MaxDescription ? flat : flat[..MaxDescription];
        }

        using JsonDocument created = await SendAsync(HttpMethod.Post, path, body, ct).ConfigureAwait(false);

        return new GitHubRepository(
            created.RootElement.GetProperty("owner").GetProperty("login").GetString()!,
            created.RootElement.GetProperty("name").GetString()!);
    }

    private Task<JsonDocument> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using HttpRequestMessage request = new(method, new Uri(new Uri("https://api.github.com/"), path));

        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Lane");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string detail = response.StatusCode switch
            {
                HttpStatusCode.NotFound     => "not found, or the token cannot see it",
                HttpStatusCode.Unauthorized => "the token was rejected",
                HttpStatusCode.Forbidden    => "forbidden, or rate limited",
                HttpStatusCode.UnprocessableEntity => await RefusalAsync(response, ct).ConfigureAwait(false),
                _                           => ((int)response.StatusCode).ToString()
            };

            throw new GitHubException($"GitHub said {detail}.");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            List<string?> parts = [doc.RootElement.TryGetProperty("message", out JsonElement m) ? m.GetString() : null];

            if (doc.RootElement.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
                parts.AddRange(errors.EnumerateArray().Select(e =>
                    e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out JsonElement em) ? em.GetString() : null));

            string detail = string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim().TrimEnd('.')));

            return detail.Length > 0 ? $"no: {detail}" : "the request was not valid";
        }
        catch (JsonException)
        {
            return "the request was not valid";
        }
    }

    private static string Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no description)";

        string trimmed = text.Trim();

        return trimmed.Length <= MaxBody ? trimmed : trimmed[..MaxBody] + "\n…(truncated)";
    }

    [GeneratedRegex(@"^(?:https://(?:[^@/]+@)?github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[\w.-]+)/(?<repo>[\w.-]+?)(?:\.git)?/?$")]
    private static partial Regex RemotePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed class GitHubException(string message) : Exception(message);
