namespace Lane.Surfaces.Coding.Git;

/// <summary>Lane's personal access tokens: one used by default, and others for particular repository owners.</summary>
public sealed class GitHubTokens(string? fallback, IEnumerable<KeyValuePair<string, string>>? byOwner = null)
{
    private readonly string? _fallback = string.IsNullOrWhiteSpace(fallback) ? null : fallback;

    private readonly Dictionary<string, string> _byOwner = new(
        (byOwner ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => KeyValuePair.Create(p.Key.Trim(), p.Value)),
        StringComparer.OrdinalIgnoreCase);

    public bool Any => _fallback is not null || _byOwner.Count > 0;

    public bool VaryByOwner => _byOwner.Count > 0;

    /// <summary>The token for <paramref name="owner"/>'s repositories; the default one when it has none of its own.</summary>
    public string? For(string? owner) =>
        owner is not null && _byOwner.TryGetValue(owner, out string? token) ? token : _fallback;
}
