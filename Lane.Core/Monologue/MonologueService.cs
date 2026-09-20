using System.Text;
using Lane.Core.Agent;
using Lane.Core.Context;
using Lane.Core.Energy;
using Lane.Core.Events;
using Lane.Core.Identity;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Pipeline;
using Lane.Core.Prompts;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lane.Core.Monologue;

/// <summary>
/// Lane's inner life: one loop, for all of her.
///
/// Deliberately not one per conversation. She is a single continuous person who happens to
/// be reachable from several places, and running a thought loop per session would both
/// fragment that and multiply the bill by however many channels happen to be open.
///
/// When a thought is worth saying out loud she names the conversation to say it in, and it
/// is *posted to that session's queue* rather than written to a channel. That one rule is
/// what keeps a volunteered remark from cutting across a reply already in flight.
/// </summary>
public sealed class MonologueService : BackgroundService, IMonologueScheduler
{
    private readonly ILanguageModelRegistry _models;
    private readonly IToolRegistry          _tools;
    private readonly AgentLoop              _loop;
    private readonly IMemoryService         _memory;
    private readonly ITranscriptStore       _transcript;
    private readonly ISessionRegistry       _sessions;
    private readonly IPromptLibrary         _prompts;
    private readonly ITranscriptFormatter   _formatter;
    private readonly IEventBus              _bus;
    private readonly IServiceProvider       _services;
    private readonly MonologueOptions       _options;
    private readonly IEnergyService         _energy;
    private readonly EnergyOptions          _energyOptions;
    private readonly TimeProvider           _time;
    private readonly ILogger<MonologueService> _log;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lock _scheduleLock = new();

    private DateTimeOffset? _nextThoughtAt;
    private string?         _lastThought;
    private DateTimeOffset? _lastThoughtAt;
    private int             _thoughtCount;
    private volatile bool   _thinking;

    public MonologueService(
        ILanguageModelRegistry models,
        IToolRegistry tools,
        AgentLoop loop,
        IMemoryService memory,
        ITranscriptStore transcript,
        ISessionRegistry sessions,
        IPromptLibrary prompts,
        ITranscriptFormatter formatter,
        IEventBus bus,
        IServiceProvider services,
        IOptions<MonologueOptions> options,
        IEnergyService energy,
        IOptions<EnergyOptions> energyOptions,
        ILogger<MonologueService> log,
        TimeProvider? time = null)
    {
        _models     = models;
        _tools      = tools;
        _loop       = loop;
        _memory     = memory;
        _transcript = transcript;
        _sessions   = sessions;
        _prompts    = prompts;
        _formatter  = formatter;
        _bus        = bus;
        _services   = services;
        _options    = options.Value;
        _energy     = energy;
        _energyOptions = energyOptions.Value;
        _time       = time ?? TimeProvider.System;
        _log        = log;
    }

    public MonologueStatus Status =>
        new(_nextThoughtAt, _lastThought, _lastThoughtAt, _thoughtCount, _thinking);

    public void Schedule(TimeSpan delay, string reason)
    {
        TimeSpan clamped = delay < _options.MinInterval ? _options.MinInterval
                         : delay > _options.MaxInterval ? _options.MaxInterval
                         : delay;

        SetNext(_time.GetUtcNow() + clamped, reason);
    }

    public void Interrupt(string reason)
    {
        // Someone is talking. Push the next thought out rather than pulling it in: thinking
        // the moment a conversation starts means interrupting it.
        DateTimeOffset proposed = _time.GetUtcNow() + _options.AfterMessage;

        lock (_scheduleLock)
        {
            // Only ever delays. A thought already due sooner than this stays due.
            if (_nextThoughtAt is { } current && current >= proposed) return;
        }

        SetNext(proposed, reason);
    }

    private void SetNext(DateTimeOffset at, string reason)
    {
        lock (_scheduleLock) _nextThoughtAt = at;

        _bus.Publish(new MonologueTick(at, reason));

        // Release only if nobody is already holding a pending wake.
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogInformation("Monologue is disabled");
            return;
        }

        SetNext(_time.GetUtcNow() + _options.StartupDelay, "startup");

        // Inbound activity reschedules. Subscribing to the bus rather than being called
        // directly keeps the pipeline unaware that an inner life exists at all.
        using IDisposable subscription = _bus.Subscribe<TurnStarted>(evt =>
        {
            // Directive turns are her own volunteered remarks; reacting to those would have
            // her interrupt herself in a loop.
            if (evt.Kind == TurnKind.Respond) Interrupt("someone is talking");
        });

        _log.LogInformation("Monologue started; first thought in {Delay}", _options.StartupDelay);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await WaitForNextAsync(stoppingToken).ConfigureAwait(false)) continue;

                // Asleep is asleep: no thoughts, and no bill for having them. Scheduling for
                // the moment accrual will wake her — rather than for the usual cadence — means
                // she starts thinking again when she is actually rested, and going through
                // Schedule keeps the Min/Max clamp and still publishes a tick, so a sleeping
                // loop reads as sleeping rather than as stuck.
                if (_energy.Current.Asleep)
                {
                    Schedule(_energy.TimeUntilRested ?? _options.Interval, "asleep");
                    continue;
                }

                await ThinkAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed thought must never end her inner life. v2 restarted the whole
                // loop after ten seconds; the schedule handles it here.
                _log.LogError(ex, "A thought failed");

                Schedule(_options.Interval, "recovering from a failed thought");
            }
        }

        _log.LogInformation("Monologue stopped");
    }

    /// <summary>
    /// Waits until the next thought is due. Returns false when the schedule moved while
    /// waiting, so the caller re-reads it rather than thinking early.
    /// </summary>
    private async Task<bool> WaitForNextAsync(CancellationToken ct)
    {
        DateTimeOffset due;
        lock (_scheduleLock) due = _nextThoughtAt ?? _time.GetUtcNow() + _options.Interval;

        TimeSpan wait = due - _time.GetUtcNow();

        if (wait <= TimeSpan.Zero) return true;

        // Returns true when signalled by a reschedule, false on timeout.
        bool rescheduled = await _wake.WaitAsync(wait, ct).ConfigureAwait(false);

        return !rescheduled;
    }

    private async Task ThinkAsync(CancellationToken ct)
    {
        ILanguageModel model = _models.Get(ModelRole.Monologue);

        EnergyTierOptions? tired = _energyOptions.For(_energy.Current.Tier);

        _thinking = true;

        try
        {
            MemoryContext context = MemoryContext.Monologue;

            AssembledContext assembled = await _memory.AssembleAsync(context, null, ct).ConfigureAwait(false);

            ToolSet tools = await _tools
                .ResolveAsync(new ToolScope(null, TurnKind.Monologue), ct)
                .ConfigureAwait(false);

            Participant lane = Participant.LaneInternal;

            AgentRunResult result = await _loop.RunAsync(new AgentRunRequest
            {
                Model  = model,
                System = [
                    new PromptBlock(BuildPrompt(), CacheHint.Ephemeral),
                    .. assembled.SystemBlocks,
                    .. await DescribeAllMessagesAsync(ct).ConfigureAwait(false)],

                // The interior monologue has no conversation to continue, so it is opened
                // with a plain nudge rather than a transcript.
                Messages = [LaneMessage.User(
                    new SessionId(new SurfaceId("internal"), SessionKind.Internal, "monologue"),
                    lane, "(think)", _time.GetUtcNow())],

                Tools       = tools,
                ToolContext = new ToolContext
                {
                    Session   = null,
                    Turn      = TurnKind.Monologue,
                    Memory    = context,
                    Services  = _services,
                    Sessions  = _sessions
                },
                Lane            = lane,
                Session         = null,

                // A tired thought should be a short thought, on the same ladder her replies use.
                Budget          = tired?.Scale(_options.Budget) ?? _options.Budget,
                MaxOutputTokens = tired?.Scale(_options.MaxOutputTokens, 128) ?? _options.MaxOutputTokens,

                CacheLineage    = $"monologue:{model.Descriptor.InstanceId}"
            }, ct).ConfigureAwait(false);

            await RecordAsync(result, context).ConfigureAwait(false);
        }
        finally
        {
            _thinking = false;
        }

        // Nothing rescheduled during the thought — she did not use the tool — so fall back
        // to the configured cadence, stretched by however tired she is. Only the fallback:
        // a cadence she chose for herself with schedule_next_thought is a decision, and
        // second-guessing it here would make that tool mean something different when tired.
        lock (_scheduleLock)
        {
            if (_nextThoughtAt is { } at && at > _time.GetUtcNow()) return;
        }

        Schedule(Stretch(_options.Interval), "default cadence");
    }

    /// <summary>
    /// The cadence, slowed by tiredness. Still passed through <see cref="Schedule"/>, so
    /// <c>MaxInterval</c> remains the last word on how long she can go without a thought.
    /// </summary>
    private TimeSpan Stretch(TimeSpan interval) =>
        _energyOptions.For(_energy.Current.Tier) is { MonologueScale: > 0 and var scale }
            ? interval * scale
            : interval;

    private async Task RecordAsync(AgentRunResult result, MemoryContext context)
    {
        bool spoke = result.ToolCalls.Any(c => c.Name == "speak_to_session" && !c.IsError);

        // What she did while thinking, not just what she concluded. Without this a quiet
        // loop is indistinguishable from a stuck one.
        if (result.ToolCalls.Count > 0)
            _log.LogInformation("[thinking] used {Tools}",
                string.Join(", ", result.ToolCalls.Select(c => c.IsError ? $"{c.Name} (failed)" : c.Name)));

        string thought = result.FinalText.Trim();

        if (thought.Length > 0)
        {
            // The assistant's own text *is* the thought. v2 asked the model for a JSON
            // object with a "thought" field and parsed it back out.
            LaneMessage message = LaneMessage.Thought(Participant.LaneInternal, thought, _time.GetUtcNow());

            long sequence = await _transcript.AppendAsync(message, CancellationToken.None).ConfigureAwait(false);

            await _memory.RememberAsync(
                sequence > 0 ? message with { Sequence = sequence } : message,
                context, CancellationToken.None).ConfigureAwait(false);

            _lastThought   = thought;
            _lastThoughtAt = _time.GetUtcNow();

            _log.LogInformation("[thought] {Thought}", thought);
        }
        else
        {
            // Nothing said at all. Usually the budget ran out mid-tool; worth seeing rather
            // than looking like a loop that quietly stopped running.
            _log.LogWarning("A thought produced no words (stopped: {Stop})", result.Stop);
        }

        foreach (LaneMessage observation in result.Observations)
            await _memory.RememberAsync(observation, context, CancellationToken.None).ConfigureAwait(false);

        Interlocked.Increment(ref _thoughtCount);

        _bus.Publish(new ThoughtHad(thought, spoke));
    }

    /// <summary>
    /// The prompt, filled with the clock and a view of every conversation she could speak
    /// into. Without the world view <c>speak_to_session</c> has nothing to name.
    /// </summary>
    private string BuildPrompt()
    {
        string time = _formatter.FormatTime(
            _time.GetUtcNow(), new TranscriptFormatOptions(IncludeRelativeTime: false));

        if (!_prompts.Has(_options.Prompt))
        {
            return "You are Lane, thinking to yourself. Say what is actually on your mind, briefly. " +
                   $"The time is {time}.\n\n{DescribeSessions()}";
        }

        return _prompts.Render(_options.Prompt,
            ("time", time),
            ("sessions", DescribeSessions()),
            ("interval", $"{_options.Interval.TotalMinutes:0.#} minutes"));
    }

    private string DescribeSessions()
    {
        Session[] active = [.. _sessions.Active.OrderBy(s => s.Id.Value, StringComparer.Ordinal)];

        if (active.Length == 0) return "(nobody is around right now)";

        StringBuilder sb = new();

        foreach (Session session in active)
        {
            TimeSpan idle = _time.GetUtcNow() - session.LastActivity;

            string people = session.Descriptor.KnownParticipants.Count > 0
                ? string.Join(", ", session.Descriptor.KnownParticipants.Where(p => !p.IsLane).Select(p => p.DisplayName))
                : "nobody named yet";

            sb.Append("- ").Append(session.Id.Value)
              .Append("  (").Append(session.Descriptor.DisplayName).Append("; ")
              .Append(people).Append("; quiet for ")
              .Append(Describe(idle)).AppendLine(")");
        }

        return sb.ToString().TrimEnd();
    }

    private async ValueTask<PromptBlock[]> DescribeAllMessagesAsync(CancellationToken ct)
    {
        if (!_options.SeeAllMessages || _options.AllMessagesLimit <= 0) return [];

        // The transcript has no kind filter, so over-fetch to leave room for thoughts.
        IReadOnlyList<LaneMessage> recent = await _transcript
            .ReadAsync(new TranscriptQuery { Limit = _options.AllMessagesLimit * 4 }, ct)
            .ConfigureAwait(false);

        LaneMessage[] utterances = [.. recent
            .Where(m => m.Kind == MessageKind.Utterance && m.Session is not null)
            .TakeLast(_options.AllMessagesLimit)];

        if (utterances.Length == 0) return [];

        string body = _formatter.Format(utterances, new TranscriptFormatOptions(
            IncludeRelativeTime: true, IncludeSessionLabels: true));

        return [new PromptBlock($"## Recent messages across all conversations\n\n{body}", CacheHint.None)];
    }

    private static string Describe(TimeSpan idle) => idle switch
    {
        { TotalSeconds: < 90 }  => $"{(int)idle.TotalSeconds} seconds",
        { TotalMinutes: < 90 }  => $"{(int)idle.TotalMinutes} minutes",
        _                       => $"{(int)idle.TotalHours} hours"
    };

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
