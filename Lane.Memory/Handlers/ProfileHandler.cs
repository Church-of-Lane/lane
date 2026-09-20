using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lane.Core.Context;
using Lane.Core.Identity;
using Lane.Core.Sessions;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Prompts;
using Lane.Core.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Memory.Handlers;

/// <summary>
/// A short list of durable facts — about a person, usually.
///
/// This is the piece that makes Lane feel like she knows you, and it is deliberately small
/// and always present rather than retrieved. A dozen self-contained statements cost almost
/// nothing to carry every turn and sit in the cached prompt block.
///
/// The unit matters. Facts are stored, not messages: "Jahan keeps a cuttlefish called
/// Marlow" means something on its own, where the chat line it came from does not. v2's
/// long-term memory embedded individual messages, which have so little topical content
/// that similarity search mostly recovered *who was talking* rather than what about.
/// </summary>
public sealed class ProfileHandler(
    MemoryHandlerOptions options,
    ILanguageModelRegistry models,
    IPromptLibrary prompts,
    ITranscriptFormatter formatter,
    ILogger logger) : IMemoryHandler, IMaintainedMemory
{
    private readonly List<LaneMessage> _pending = [];
    private readonly List<string> _facts = [];

    public string      Id    => options.Id;
    public MemoryScope Scope => options.Scope;
    public MemorySlot  Slot  => options.Slot;
    public int         Order => options.Order;
    public string?     SectionTitle => options.SectionTitle;

    private int Threshold => Math.Max(1, options.MaxMessages);

    public bool NeedsMaintenance => _pending.Count >= Threshold;

    public ValueTask RememberAsync(MemoryWrite write, CancellationToken ct)
    {
        LaneMessage? storable = MemorySanitizer.ForMemory(write.Message);

        if (storable is null || storable.TextContent.Length == 0) return ValueTask.CompletedTask;

        _pending.Add(storable);

        int excess = _pending.Count - Threshold * 4;
        if (excess > 0) _pending.RemoveRange(0, excess);

        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryRecall> RecallAsync(MemoryQuery query, CancellationToken ct)
    {
        if (_facts.Count == 0) return ValueTask.FromResult(MemoryRecall.Empty);

        StringBuilder sb = new();

        foreach (string fact in _facts) sb.Append("- ").AppendLine(fact);

        return ValueTask.FromResult(new MemoryRecall([], sb.ToString().TrimEnd()));
    }

    public async ValueTask MaintainAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;

        LaneMessage[] batch = [.. _pending];

        string transcript = formatter.Format(batch, new TranscriptFormatOptions(IncludeRelativeTime: false));

        ILanguageModel model = models.Get(ModelRole.Summarize);

        string prompt = prompts.Has(options.PromptName)
            ? prompts.Render(options.PromptName,
                ("facts", _facts.Count == 0 ? "(nothing yet)" : string.Join("\n", _facts.Select(f => $"- {f}"))),
                ("transcript", transcript),
                ("limit", options.MaxEntries.ToString()))
            : Fallback(_facts, transcript, options.MaxEntries);

        ModelResponse response = await model.CompleteAsync(new ModelRequest
        {
            System          = [new PromptBlock(prompt)],
            Messages        = [Instruction("Update the list of facts.")],
            MaxOutputTokens = 500,
            CacheLineage    = $"profile:{model.Descriptor.InstanceId}"
        }, ct).ConfigureAwait(false);

        List<string> updated = ParseFacts(response.Text, options.MaxEntries);

        if (updated.Count == 0)
        {
            // An empty answer is far more likely to be a bad call than a person about whom
            // nothing is true, so the existing facts stand.
            logger.LogWarning("Profile pass returned no facts for {Handler}; keeping what was there", Id);

            _pending.Clear();
            return;
        }

        _facts.Clear();
        _facts.AddRange(updated);

        _pending.Clear();

        logger.LogDebug("Profile for {Handler} now holds {Count} fact(s)", Id, _facts.Count);
    }

    /// <summary>
    /// Reads back a bulleted list, tolerating the numbering and stray prose models add.
    /// A malformed answer costs one stale cycle, not the whole profile.
    /// </summary>
    internal static List<string> ParseFacts(string text, int limit)
    {
        List<string> facts = [];

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0) continue;

            if (line.StartsWith("- ", StringComparison.Ordinal) ||
                line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = line[2..].Trim();
            }
            else if (line.Length > 2 && char.IsAsciiDigit(line[0]) && (line[1] == '.' || line[1] == ')'))
            {
                line = line[2..].Trim();
            }
            else
            {
                // Anything that is not a list item is commentary, not a fact.
                continue;
            }

            if (line.Length == 0) continue;

            // Models repeat themselves across passes; a profile of duplicates is useless.
            if (facts.Any(f => string.Equals(f, line, StringComparison.OrdinalIgnoreCase))) continue;

            facts.Add(line);

            if (facts.Count >= limit) break;
        }

        return facts;
    }

    private static string Fallback(IReadOnlyList<string> facts, string transcript, int limit) =>
        new StringBuilder()
            .AppendLine($"Keep a list of at most {limit} durable facts about the person below.")
            .AppendLine("Each fact must stand on its own without the conversation around it.")
            .AppendLine("Add what is new, correct what has changed, drop what no longer holds.")
            .AppendLine("Record what persists — who they are, what they are doing, what they prefer —")
            .AppendLine("not passing details of one conversation.")
            .AppendLine()
            .AppendLine("## Known so far")
            .AppendLine(facts.Count == 0 ? "(nothing yet)" : string.Join("\n", facts.Select(f => $"- {f}")))
            .AppendLine()
            .AppendLine("## Recent conversation").AppendLine(transcript)
            .AppendLine()
            .AppendLine("Reply with the full updated list as plain '- ' bullets, and nothing else.")
            .ToString();

    public ValueTask<JsonNode?> SaveStateAsync(CancellationToken ct) =>
        ValueTask.FromResult(JsonSerializer.SerializeToNode(new State
        {
            Facts   = [.. _facts],
            Pending = [.. _pending.Select(LaneJson.ToStored)]
        }, LaneJson.Options));

    public ValueTask LoadStateAsync(JsonNode state, CancellationToken ct)
    {
        State? restored = state.Deserialize<State>(LaneJson.Options);

        _facts.Clear();
        _pending.Clear();

        if (restored?.Facts is { } facts) _facts.AddRange(facts.Take(options.MaxEntries));
        if (restored?.Pending is { } pending) _pending.AddRange(pending.Select(LaneJson.FromStored));

        return ValueTask.CompletedTask;
    }


    /// <summary>
    /// A one-shot instruction to open the request with.
    ///
    /// The material is already in the system prompt, so what goes here only has to be a
    /// valid opening turn. Reusing the last buffered message does not work: when it is one
    /// of Lane's own replies, providers that require a conversation to start on a user turn
    /// drop it and the request arrives empty.
    /// </summary>
    private static LaneMessage Instruction(string text) => LaneMessage.User(
        new SessionId(new SurfaceId("internal"), SessionKind.Internal, "maintenance"),
        Participant.LaneInternal, text, DateTimeOffset.UtcNow);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class State
    {
        public int V { get; init; } = 1;
        public string[]? Facts { get; init; }
        public StoredMessage[]? Pending { get; init; }
    }
}

public sealed class ProfileFactory : IMemoryHandlerFactory
{
    public string TypeName => "Profile";

    public IMemoryHandler Create(MemoryHandlerOptions options, ScopeKey key, IServiceProvider services) =>
        new ProfileHandler(
            options,
            services.GetRequiredService<ILanguageModelRegistry>(),
            services.GetRequiredService<IPromptLibrary>(),
            services.GetRequiredService<ITranscriptFormatter>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger($"Lane.Memory.Profile.{options.Id}"));
}
