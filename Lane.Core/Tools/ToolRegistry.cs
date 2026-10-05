using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Tools;

public sealed class ToolOptions
{
    /// <summary>Glob patterns of tool names to offer. <c>*</c> matches everything.</summary>
    public List<string> Allow { get; set; } = ["*"];

    public List<string> Deny { get; set; } = [];

    /// <summary>Per-surface deny lists, keyed by surface id.</summary>
    public Dictionary<string, List<string>> DenyBySurface { get; set; } = [];

    /// <summary>
    /// Advertise the same tool list to every session of a given turn kind, and enforce
    /// capability requirements when a call arrives instead.
    ///
    /// This is a prompt-cache decision. Providers hash the request prefix in tool order,
    /// so varying the advertised list per session silently destroys the cached system
    /// blocks behind it — the cost does not show up as an error, only as a bill. Turn off
    /// only if a surface genuinely needs a different set of tools rather than a different
    /// set of capabilities.
    /// </summary>
    public bool PreferStableSet { get; set; } = true;
}

/// <summary>Who is asking, from where, and with how much left in the tank.</summary>
public sealed record ToolScope(
    SessionDescriptor? Session,
    TurnKind           Turn,
    Participant?       Requester = null,
    EnergyTier         Energy    = EnergyTier.Rested);

/// <param name="Fingerprint">
/// Stable hash of the advertised names and schemas. Requests carry it in their cache
/// lineage so a change that quietly invalidates the prompt cache is visible in telemetry.
/// </param>
public sealed record ToolSet(IReadOnlyList<ToolDescriptor> Descriptors, string Fingerprint)
{
    public static ToolSet Empty { get; } = new([], "empty");

    public int Count => Descriptors.Count;
}

public interface IToolRegistry
{
    ValueTask<ToolSet> ResolveAsync(ToolScope scope, CancellationToken ct);

    ValueTask<ToolResult> InvokeAsync(
        string name, string callId, JsonElement arguments, ToolContext context, CancellationToken ct);

    event Action<ToolEvent>? Events;
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly IReadOnlyList<IToolSource> _sources;
    private readonly ToolOptions _options;
    private readonly EnergyOptions _energy;
    private readonly ILogger<ToolRegistry> _log;
    private readonly IEventBus _bus;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Dictionary<string, ITool>? _tools;

    public ToolRegistry(
        IEnumerable<IToolSource> sources,
        IOptions<ToolOptions> options,
        ILogger<ToolRegistry> log,
        IEventBus? bus = null,
        IOptions<EnergyOptions>? energy = null)
    {
        _bus     = bus ?? new NullEventBus();
        _sources = [.. sources];
        _options = options.Value;
        _energy  = energy?.Value ?? new EnergyOptions();
        _log     = log;

        foreach (IToolSource source in _sources)
            source.ToolsChanged += _ => Invalidate();
    }

    public event Action<ToolEvent>? Events;

    public async ValueTask<ToolSet> ResolveAsync(ToolScope scope, CancellationToken ct)
    {
        Dictionary<string, ITool> all = await GetToolsAsync(ct).ConfigureAwait(false);

        List<ToolDescriptor> offered = [];

        foreach (ITool tool in all.Values)
        {
            if (Gate(tool.Descriptor, scope, advertising: true) is not null) continue;

            offered.Add(tool.Descriptor);
        }

        offered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        return new ToolSet(offered, Fingerprint(offered));
    }

    public async ValueTask<ToolResult> InvokeAsync(
        string name, string callId, JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        Dictionary<string, ITool> all = await GetToolsAsync(ct).ConfigureAwait(false);

        if (!all.TryGetValue(name, out ITool? tool))
        {
            Raise(new ToolEvent.Rejected(name, "unknown"));
            return ToolResult.Error($"No tool named '{name}'.");
        }

        // Re-checked here on purpose. The advertised list is a hint to the model, not a
        // security boundary — a model can name a tool it was never offered.
        ToolScope scope = new(context.Descriptor, context.Turn, context.Requester, context.Energy);

        string? refusal = Gate(tool.Descriptor, scope, advertising: false);

        if (refusal is not null)
        {
            Raise(new ToolEvent.Rejected(name, refusal));
            return ToolResult.Error($"{name} is not available here: {refusal}.");
        }

        Raise(new ToolEvent.Invoked(name, callId, context.Session?.Value));

        long started = Stopwatch.GetTimestamp();

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(tool.Descriptor.Timeout);

        try
        {
            ToolResult result = await tool
                .InvokeAsync(new ToolInvocation(callId, arguments, context), timeout.Token)
                .ConfigureAwait(false);

            Raise(new ToolEvent.Completed(name, callId, result.IsError, Stopwatch.GetElapsedTime(started)));

            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _log.LogWarning("Tool {Tool} timed out after {Timeout}", name, tool.Descriptor.Timeout);

            Raise(new ToolEvent.Completed(name, callId, true, Stopwatch.GetElapsedTime(started)));

            return ToolResult.Error($"{name} timed out after {tool.Descriptor.Timeout.TotalSeconds:0.#}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throwing tool must never break the agent loop: the model asked for this
            // call and the provider requires an answer to it, error or not.
            _log.LogError(ex, "Tool {Tool} threw", name);

            Raise(new ToolEvent.Completed(name, callId, true, Stopwatch.GetElapsedTime(started)));

            return ToolResult.Error($"{name} failed: {ex.Message}");
        }
    }

    /// <summary>Null when the tool is allowed; otherwise the reason it is not.</summary>
    private string? Gate(ToolDescriptor descriptor, ToolScope scope, bool advertising)
    {
        if (!IsAllowedByConfig(descriptor.Name, scope)) return "disabled by configuration";

        // Tiredness is enforced only when a call arrives, never by withholding the tool.
        // Hiding one would change ToolSet.Fingerprint, and with it the cache lineage of every
        // request behind it — a prompt-cache miss bought in exchange for saving tokens, at
        // exactly the moment she is trying to spend less, and flapping each time she crosses a
        // threshold. Refusing costs one step that MaxToolCalls already bounds, and she is told
        // why rather than left to wonder where the tool went.
        if (!advertising && IsTooTiredFor(descriptor.Name, scope.Energy))
            return "you are too tired for that right now";

        if (!descriptor.Availability.AllowedTurns.HasFlag(scope.Turn))
            return $"only available during {descriptor.Availability.AllowedTurns} turns";

        if (descriptor.Availability.RequiresSession && scope.Session is null)
            return "requires a conversation";

        if (descriptor.Availability.RequiredSessionTag is { } tag &&
            scope.Session?.Tags.ContainsKey(tag) != true)
            return $"only available in '{tag}' conversations";

        ChannelCapabilities required = descriptor.Availability.RequiredCapabilities;

        if (required != ChannelCapabilities.None)
        {
            // When keeping the advertised set stable, capability checks are deferred to
            // invocation so the tool list does not vary from session to session.
            if (advertising && _options.PreferStableSet) return null;

            if (scope.Session is null || !scope.Session.Supports(required))
                return $"needs a channel supporting {required}";
        }

        return null;
    }

    private bool IsTooTiredFor(string name, EnergyTier tier) =>
        _energy.For(tier) is { DenyTools: { Count: > 0 } denied } &&
        denied.Any(pattern => Matches(name, pattern));

    private bool IsAllowedByConfig(string name, ToolScope scope)
    {
        if (_options.Deny.Any(pattern => Matches(name, pattern))) return false;

        if (scope.Session is not null &&
            _options.DenyBySurface.TryGetValue(scope.Session.Id.Surface.Value, out List<string>? denied) &&
            denied.Any(pattern => Matches(name, pattern)))
            return false;

        return _options.Allow.Count == 0 || _options.Allow.Any(pattern => Matches(name, pattern));
    }

    private static bool Matches(string name, string pattern)
    {
        if (pattern == "*") return true;

        if (pattern.EndsWith('*'))
            return name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);

        return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private async ValueTask<Dictionary<string, ITool>> GetToolsAsync(CancellationToken ct)
    {
        Dictionary<string, ITool>? cached = Volatile.Read(ref _tools);
        if (cached is not null) return cached;

        await _refresh.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            cached = Volatile.Read(ref _tools);
            if (cached is not null) return cached;

            Dictionary<string, ITool> built = new(StringComparer.OrdinalIgnoreCase);

            foreach (IToolSource source in _sources)
            {
                IReadOnlyList<ITool> tools;

                try
                {
                    tools = await source.GetToolsAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A source that is down contributes nothing rather than failing the turn.
                    _log.LogError(ex, "Tool source {Source} could not be read", source.SourceId);
                    continue;
                }

                foreach (ITool tool in tools)
                {
                    if (built.TryAdd(tool.Descriptor.Name, tool)) continue;

                    _log.LogWarning(
                        "Two tools are named '{Tool}'; keeping the one from {Kept} and ignoring {Ignored}",
                        tool.Descriptor.Name, built[tool.Descriptor.Name].Descriptor.SourceId, source.SourceId);
                }
            }

            _log.LogInformation("{Count} tool(s) available: {Names}",
                built.Count, string.Join(", ", built.Keys.Order()));

            Volatile.Write(ref _tools, built);

            return built;
        }
        finally { _refresh.Release(); }
    }

    private void Invalidate()
    {
        Volatile.Write(ref _tools, null);
        _log.LogInformation("Tool list invalidated");
    }

    private static string Fingerprint(IReadOnlyList<ToolDescriptor> descriptors)
    {
        if (descriptors.Count == 0) return "empty";

        StringBuilder sb = new();

        foreach (ToolDescriptor descriptor in descriptors)
            sb.Append(descriptor.Name).Append('').Append(descriptor.InputSchema.GetRawText()).Append('');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));

        return Convert.ToHexStringLower(hash)[..12];
    }

    private void Raise(ToolEvent evt)
    {
        try { Events?.Invoke(evt); }
        catch (Exception ex) { _log.LogWarning(ex, "A tool event subscriber threw"); }

        switch (evt)
        {
            case ToolEvent.Invoked invoked:
                _bus.Publish(new ToolInvokedEvent(invoked.Name, invoked.Session));
                break;

            case ToolEvent.Completed completed:
                _bus.Publish(new ToolCompletedEvent(completed.Name, completed.IsError, completed.Duration));
                break;
        }
    }
}
