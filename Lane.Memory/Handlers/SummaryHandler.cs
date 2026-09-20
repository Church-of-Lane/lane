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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lane.Memory.Handlers;

/// <summary>
/// A rolling account of a conversation, kept current as it goes.
///
/// This is what carries a conversation past the point the recent window can hold it. The
/// window forgets in order; the summary forgets by compression, which is the difference
/// between "she has no idea what we were doing" and "she remembers the gist".
///
/// The model call happens during maintenance rather than while remembering, because
/// messages are committed before they are delivered — summarising inline would put a whole
/// round trip between Lane deciding what to say and saying it.
/// </summary>
public sealed class SummaryHandler(
    MemoryHandlerOptions options,
    ILanguageModelRegistry models,
    IPromptLibrary prompts,
    ITranscriptFormatter formatter,
    ILogger logger) : IMemoryHandler, IMaintainedMemory
{
    private readonly List<LaneMessage> _pending = [];

    private string _summary = "";

    public string      Id    => options.Id;
    public MemoryScope Scope => options.Scope;
    public MemorySlot  Slot  => options.Slot;
    public int         Order => options.Order;
    public string?     SectionTitle => options.SectionTitle;

    /// <summary>How many messages accumulate before it is worth spending a call.</summary>
    private int Threshold => Math.Max(1, options.MaxMessages);

    public bool NeedsMaintenance => _pending.Count >= Threshold;

    public ValueTask RememberAsync(MemoryWrite write, CancellationToken ct)
    {
        LaneMessage? storable = MemorySanitizer.ForMemory(write.Message);

        if (storable is null || storable.TextContent.Length == 0) return ValueTask.CompletedTask;

        _pending.Add(storable);

        // A hard ceiling in case maintenance never runs — an unbounded buffer would grow
        // until the process did.
        int excess = _pending.Count - Threshold * 4;
        if (excess > 0) _pending.RemoveRange(0, excess);

        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryRecall> RecallAsync(MemoryQuery query, CancellationToken ct)
    {
        if (_summary.Length == 0) return ValueTask.FromResult(MemoryRecall.Empty);

        // Rendered rather than returned as messages: a summary is prose about the past, not
        // turns to be replayed as though they had just been said.
        return ValueTask.FromResult(new MemoryRecall([], _summary));
    }

    public async ValueTask MaintainAsync(CancellationToken ct)
    {
        if (_pending.Count == 0) return;

        LaneMessage[] batch = [.. _pending];

        string transcript = formatter.Format(batch, new TranscriptFormatOptions(
            IncludeRelativeTime: false, IncludeSessionLabels: Scope is MemoryScope.Global));

        ILanguageModel model = models.Get(ModelRole.Summarize);

        string prompt = prompts.Has(options.PromptName)
            ? prompts.Render(options.PromptName,
                ("summary", _summary.Length == 0 ? "(nothing yet)" : _summary),
                ("transcript", transcript))
            : Fallback(_summary, transcript);

        ModelResponse response = await model.CompleteAsync(new ModelRequest
        {
            System          = [new PromptBlock(prompt)],
            Messages        = [Instruction("Update the summary.")],
            MaxOutputTokens = 400,
            CacheLineage    = $"summarize:{model.Descriptor.InstanceId}"
        }, ct).ConfigureAwait(false);

        string updated = response.Text.Trim();

        if (updated.Length == 0)
        {
            logger.LogWarning("Summariser returned nothing for {Handler}; keeping the previous summary", Id);
            return;
        }

        _summary = updated;

        // Cleared only on success, so a failed call retries with the same batch next tick
        // rather than losing the conversation it was meant to record.
        _pending.Clear();

        logger.LogDebug("Summarised {Count} message(s) for {Handler}", batch.Length, Id);
    }

    private static string Fallback(string summary, string transcript) =>
        new StringBuilder()
            .AppendLine("Update the running summary of this conversation. Keep it under 150 words.")
            .AppendLine("Keep what still matters, drop what does not. Write plainly, in the third person.")
            .AppendLine()
            .AppendLine("## Summary so far").AppendLine(summary.Length == 0 ? "(nothing yet)" : summary)
            .AppendLine()
            .AppendLine("## Since then").AppendLine(transcript)
            .AppendLine()
            .AppendLine("Reply with the updated summary and nothing else.")
            .ToString();

    public ValueTask<JsonNode?> SaveStateAsync(CancellationToken ct) =>
        ValueTask.FromResult(JsonSerializer.SerializeToNode(new State
        {
            Summary  = _summary,
            Pending  = [.. _pending.Select(Lane.Core.Serialization.LaneJson.ToStored)]
        }, Lane.Core.Serialization.LaneJson.Options));

    public ValueTask LoadStateAsync(JsonNode state, CancellationToken ct)
    {
        State? restored = state.Deserialize<State>(Lane.Core.Serialization.LaneJson.Options);

        _summary = restored?.Summary ?? "";

        _pending.Clear();

        if (restored?.Pending is { } pending)
            _pending.AddRange(pending.Select(Lane.Core.Serialization.LaneJson.FromStored));

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
        public string? Summary { get; init; }
        public Lane.Core.Serialization.StoredMessage[]? Pending { get; init; }
    }
}

public sealed class SummaryFactory : IMemoryHandlerFactory
{
    public string TypeName => "Summary";

    public IMemoryHandler Create(MemoryHandlerOptions options, ScopeKey key, IServiceProvider services) =>
        new SummaryHandler(
            options,
            services.GetRequiredService<ILanguageModelRegistry>(),
            services.GetRequiredService<IPromptLibrary>(),
            services.GetRequiredService<ITranscriptFormatter>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger($"Lane.Memory.Summary.{options.Id}"));
}
