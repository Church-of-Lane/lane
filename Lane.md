# Lane v3

The rewrite of the harness that used to live in `Wizard/`, which was deleted at M10. Full
design and milestone plan: `~/.claude/plans/this-is-an-ai-concurrent-naur.md`.

## Why

v2 could swap a part but not have several of them. One body was chosen from an enum at
startup, a single global `Bot` held one conversation's worth of state, and `bool
isResponding` silently dropped any message that arrived mid-reply. Abilities were `if`
blocks reading fields out of a JSON blob.

v3 inverts the ownership. A kernel owns the models, tools and memory; surfaces are thin
transports that own nothing. Cohesion — never answering one person inside someone else's
conversation — comes from the **session**: a per-conversation queue with a single pump.
Memory sharing is a separate, independently configured axis, so Lane can remember
everything globally while still replying only where she was addressed.

## Layout

| Project | Holds |
|---|---|
| `Lane.Core` | Identity, `LaneMessage`, session pump, turn pipeline, prompt library, and the `ILanguageModel` / `ITool` / memory *abstractions*. Depends on nothing provider-shaped — that constraint is what makes "N of a kind" enforceable. |
| `Lane.Providers` | Anthropic, OpenAI-compatible (OpenRouter, DeepSeek, any compatible endpoint) and TypeSafe System One adapters. |
| `Lane.Audio` | Ported DSP, continuous recognition, streaming synthesis, the audio router and the voice floor. |
| `Lane.Memory` | SQLite state / transcript / key-value stores, the sliding-window, summary and profile handlers, the flush and maintenance service. |
| `Lane.Tools` | The built-in abilities: `web_search`, `fetch_url`, `read_book`, `list_books`, `set_emoticon`, `list_voice_channels`, `join_voice_channel`, `link_identity`, `set_my_name`, `set_session_description`, and the scratchpad (`write_note`, `read_note`, `list_notes`, `delete_note`). |
| `Lane.Tools.Mcp` | The MCP client: one supervised connection per configured server, its tools namespaced and sanitised. |
| `Lane.Surfaces.Discord` | One Discord bot per configured instance: sessions, mentions, replies, attachments, embeds. |
| `Lane.Surfaces.Terminal` | The keyboard as a surface. |
| `Lane.Surfaces.Api` | Lane over HTTP: per-client keys, sessions, streamed replies, an event feed and a voice socket. |
| `Lane.Testing` | `ScriptedLanguageModel`, `RecordingChannel`, `LaneHarness` — the kernel runs fully offline. Also an executable, so `FakeMcpServer` can be launched as a real child process. |
| `Lane.Tests` | xUnit. |
| `Lane.Host` | Generic Host composition root, config, secret resolution, prompt templates, TUI dashboard. |

## Running

```bash
dotnet test  Lane.Tests/Lane.Tests.csproj      # 419 tests, no network
dotnet run --project Lane.Host                 # dashboard, if stdout is a terminal
dotnet run --project Lane.Host -- --no-tui     # plain stdin/stdout
dotnet run --project Lane.Host -- migrate --data <v2 data.json> --dry-run
dotnet run --project Lane.Host -- say "hello"  # speak one line and exit
```

In a real terminal you get the dashboard. Piped or with `--no-tui` it falls back to a plain
read loop, with logs on stderr so the conversation on stdout stays clean
(`… 2>/dev/null`). Memory lands in `lane.db` beside the binary; drop an
`appsettings.local.json` next to `appsettings.json` to override anything locally.

Keys: `ANTHROPIC_API_KEY`, optionally `OPENROUTER_API_KEY`, `BRAVE_API_KEY`,
`DISCORD_API_KEY` and `LANE_API_KEY` (one per API client app). A surface whose token is missing logs and is skipped; the rest still run.
Voice is off by default and needs `AZURE_KEY`, `AZURE_REGION` and — unless she is speaking
through flite — `ELEVENLABS_KEY` (`Lane:Audio:Enabled`, plus `Voice:Enabled` on a Discord
surface). Where she sits is then hers: `list_voice_channels` and `join_voice_channel` are
tools, so she walks into a channel when someone asks her to rather than at startup, and
leaves again after `Voice:IdleTimeout` (five minutes) with nothing heard and nothing said.

## Talking to the API

```bash
# every request carries a client key; health is the one exception
curl -H "Authorization: Bearer $LANE_API_KEY" http://127.0.0.1:5080/v1/sessions

# send and wait
curl -H "Authorization: Bearer $LANE_API_KEY" -H 'Content-Type: application/json' \
     -d '{"text":"what are you up to?"}' \
     http://127.0.0.1:5080/v1/sessions/main/messages

# send and watch the reply arrive
curl -N -H "Authorization: Bearer $LANE_API_KEY" -H 'Content-Type: application/json' \
     -d '{"text":"what are you up to?"}' \
     'http://127.0.0.1:5080/v1/sessions/main/messages?stream=true'
```

`main` is the client's own name for a conversation, not a server-assigned id — it is
namespaced by client id, so two apps using the same name get two conversations. A client
relaying several people names them per message with `"author": {"id":"…","name":"…"}`;
those ids are namespaced under the client too.

Browsers cannot set headers on a WebSocket handshake, so the voice socket — and only the
voice socket — also accepts `?key=`. It takes 16 kHz mono or 48 kHz PCM binary frames and
sends back audio frames plus JSON text frames for what she said.

## Status: complete — M0 through M10

**Discord and the terminal run in one process**, which is the thing v2 could not do at all,
and **Lane now thinks between conversations** — one loop for all of her, able to volunteer a
remark into a conversation she names. She replies on whichever provider her `respond` role
is bound to, uses tools when she needs them, and still knows what you said, and what she
found out, after a restart. Every conversation runs concurrently and stays its own.

Proven by tests rather than asserted:

**Cohesion and concurrency (M0)**
- replies reach only the session that produced them, across three concurrent sessions
- a burst of messages coalesces into one turn without any being discarded
- **a message arriving mid-turn is answered, not dropped** — the direct fix for v2's `isResponding`
- one session never runs two turns at once; different sessions do run concurrently
- the global turn budget caps concurrency across all sessions
- a failed turn does not stop the session from answering the next one
- a volunteered utterance (the shape the monologue's `speak_to_session` will use) is
  delivered through the named session and costs no model call
- the Anthropic mapper keeps speakers attributed and separated, preserves tool_use/
  tool_result pairing, and never reorders cache breakpoints

**Memory (M1)**
- **a conversation continues across a restart**, and a *different* conversation does not
  inherit it
- session-scoped windows stay separate while a global handler sees every conversation,
  labelled by session so the mix stays legible
- a `User`-scoped handler marked `DirectSessionsOnly` never reaches a group channel —
  enforced on writes as well as reads, so DM-only memory only accumulates from DMs
- scope keys: `Session` keys on the *memory group*, so a text channel and its voice
  channel share one memory; two Discord instances do not; unlinked users still get their own
- handler state round-trips, tolerates a configuration that has since tightened, and
  discards unreadable rows instead of refusing to start
- every content part — text, image, tool_use, tool_result, thinking signature — survives
  serialisation

**Tools and the agent loop (M2)**
- a golden three-step run — search, fetch, answer — produces a correctly paired history,
  offline, with no network
- **every `tool_use` is answered by exactly one `tool_result`**, in call order, when a tool
  throws, times out, exceeds its budget, does not exist, or the whole turn is cancelled
- **tool traffic never reaches replayed memory**, so a window that trims between a call and
  its result cannot poison every later request; the transcript keeps the full record
- parallel calls are answered in the order they were requested
- step and tool-call budgets stop a model that will not stop calling tools
- gating: monologue-only tools are not offered during a reply, deny lists work globally and
  per surface, and a tool the model was never offered is refused when it calls one anyway
- capability-gated tools keep the advertised set stable and are refused at the call — a
  deliberate prompt-cache decision, since varying the tool list destroys the cached prefix
- schemas are generated from the arguments record, with `[Description]` text, defaults
  correctly optional, lenient on input and strict in what they advertise
- a book title cannot escape the books directory; an oversized emoticon is refused

**Providers, telemetry and the dashboard (M3)**
- one adapter serves every OpenAI-compatible endpoint; **an unfamiliar `finish_reason` is
  information, not a crash** — the reason v2 had to bypass its SDK entirely
- tool results become their own messages with `tool_call_id`, arguments travel as a JSON
  string, and malformed arguments become an empty object rather than ending the turn
- speaker attribution is identical across both providers, because it lives in one place
- **a tool-using role bound to a model without tool support fails at startup**, naming the
  role and the model — with no prompt-JSON fallback, silence here means finding out
  mid-conversation
- the event bus delivers to any number of subscribers, isolates a throwing one, and does
  not deadlock when a subscriber publishes while handling
- every model reports usage through a decorator, so a provider added later gets telemetry
  without knowing the bus exists

Verified live: OpenRouter answering with a real `web_search` round trip, and the dashboard
showing per-instance token counts, role bindings, the live session, and a conversation
typed into its own pane.

The chat pane submits on Terminal.Gui's `Accepting` command rather than by comparing a key
against `Key.Enter`. Matching one specific key value means any driver that reports Enter
with a modifier bit set — or as a newline — does nothing when you press it, and drivers
differ by terminal. The transcript is `CanFocus = false` for the same class of reason: a
focusable transcript can take focus away from the input, after which typing goes nowhere.
The driver in use is logged at startup, since it is the first thing worth knowing when
input misbehaves on one terminal but not another.

**Discord, multi-instance (M4)**
- **two Discord instances and a terminal all run at once without cross-talk**, and the same
  channel id on two different bot instances stays two separate conversations with two
  separate memories
- global memory still spans every surface, session-labelled so the mix stays legible
- back-to-back messages in one channel lose nothing while other surfaces keep running
- one surface failing to start costs only that surface — the others still run
- mentions resolve to names (raw `<@2313…>` tells the model nothing), replies carry what
  they replied to, and an unknown mention is left alone rather than mangled
- a reply over Discord's 2000-character limit is split at paragraph, then line, then word
  boundaries instead of being rejected outright
- Lane cannot ping `@everyone` or a role by happening to write the words
- other bots are remembered but never answered, so two of them cannot loop
- identities link across surfaces by configuration or by proof, never by a guess from a
  matching display name, since merging two strangers' memories is worse than not linking at
  all — see the note on `link_identity` below

Verified live: `Discord surface discord.main ready as Lane` and `Terminal surface terminal
ready` in one process, holding a terminal conversation while the gateway was connected,
then shutting both down cleanly.

**The monologue (M5)**
- one global loop, not one per conversation — she is a single continuous person, and a loop
  per session would fragment that and multiply the bill by however many channels are open
- **a thought is the model's own words**; v2 asked for a JSON object and parsed a `thought`
  field back out of it
- thoughts belong to no conversation (`session = null`, `kind = Thought`), so they land in
  the global thought window rather than anyone's transcript
- `speak_to_session` posts to that session's *queue*, so a volunteered remark waits behind a
  reply already in flight and is remembered in the right conversation
- speaking into a conversation that is not open is refused, never redirected to whichever
  channel happened to be handy
- monologue-only tools are not offered while replying, so she cannot answer you in a
  different conversation than the one you spoke in
- she schedules her own next thought, clamped — a model asking to think every second would
  otherwise be a billing incident
- someone talking **pushes the next thought back**, never pulls it in: thinking the moment a
  conversation starts means interrupting it
- a failed thought does not end her inner life
- the face server is a bus subscriber, so the face, the dashboard and anything else can all
  see her expression; in v2 exactly one listener could exist

Verified live: she looked around with `list_sessions`, read a book, got absorbed in it, and
volunteered a comment about it into the terminal unprompted — then scheduled her next
thought herself.

**Long-term memory (M6)**
- **memory outlives the recent window**: with a four-message window, facts from six
  messages ago were still answered correctly, from the profile and the summary
- the model calls that maintain them run **off the turn path**, on a timer — messages are
  committed before they are delivered, so summarising inline would sit between Lane
  deciding what to say and saying it
- a failed maintenance pass keeps its buffered messages for the next attempt, rather than
  losing the conversation it was meant to record
- an empty answer leaves the previous summary or fact list standing — far likelier to be a
  bad call than a person about whom nothing is true
- facts are read back tolerantly (bullets, numbering, stray prose) and deduplicated
- tool traffic never reaches either handler
- `lane migrate` brings v2's `data.json` into the transcript: deduplicated across handlers
  that held the same message, roles preserved, thoughts still belonging to no conversation,
  book positions into the key-value store, with `--dry-run`

**Audio (M7)**
- the 48 kHz → 16 kHz conversion is **byte-identical to v2's**, checked against a copy of
  the original held in the test — this code moved projects, and getting it subtly wrong
  would show up as Lane mishearing consonants, not as a crash
- the anti-aliasing is measured, not assumed: a 15 kHz tone is attenuated below 5% while
  1 kHz survives above 85%
- recognition is continuous over any `IAudioSource`, so a microphone that is not Discord can
  be heard at all — v2's ear took a Discord stream type in its constructor
- **two people in one channel are two sources bound to one session**, arriving as one
  ordered conversation; microphones bound elsewhere run in parallel, and one failing does
  not silence the others
- interim results never become messages; they exist to drive barge-in
- barge-in has both a word gate and a minimum speaking time, because interim transcripts are
  noisy and a microphone in the room hears Lane's own first syllable
- the floor is per session, so people talking over each other in one room cannot silence her
  in another
- **real token streaming from Anthropic**, with the first clause cut shorter than later ones
  and forced breaks preferring a comma over an arbitrary space

Measured end to end against the live APIs: **first speakable clause at 0.9–1.1 s, first
audio at 1.3–2.4 s**, against roughly 3.3 s for waiting out the whole reply. Sub-second
audio was the stated target and is *not* reached — the remaining cost is time-to-first-token
plus synthesis, both external. Cutting the first clause shorter took first-speakable from
2.4 s to 1.0 s, which was the single biggest win available.

**HTTP API (M8)**
- `POST /v1/sessions`, `GET /v1/sessions`, `POST /v1/sessions/{key}/messages`,
  `GET /v1/sessions/{key}/messages`, `POST /v1/sessions/{key}/cancel`,
  `DELETE /v1/sessions/{key}`, `GET /v1/events`, `WS /v1/sessions/{key}/voice`,
  and an unauthenticated `GET /v1/health`
- **two client apps hold independent conversations while Discord and the terminal are live**
  — sessions are namespaced by the key a client authenticates with, so two apps that both
  call their conversation "main" get two conversations and neither can post into the other's
- meeting in one conversation is possible but has to be asked for: a key prefixed `shared:`
- `?stream=true` upgrades a message to server-sent events — `accepted`, then `delta` per
  token, `tool` as she uses one, `message` with the delivered reply, then `done`
- a turn nobody is watching is not streamed at all, so the cost is paid only when someone
  is looking
- `GET /v1/events` is the same bus the dashboard and the face server read; token usage is
  only sent to a client trusted to observe every conversation
- the voice socket takes PCM in and sends audio *and* text back, so a client showing a
  transcript beside the audio needs one connection rather than two
- client keys are compared in constant time, and a key can only invent participants inside
  its own namespace — it can never produce one belonging to another client, or to Discord
- anonymous access is refused outright on anything but a loopback address

**MCP (M9)**
- servers are named in configuration and nowhere else — nothing is discovered, because
  connecting means running code somebody else controls and putting text they wrote into
  Lane's prompt
- tools arrive as `mcp__{server}__{tool}`, so a server can never shadow a built-in or
  another server's tool
- **a server that is down contributes no tools rather than failing a turn**; it is retried
  with exponential backoff and jitter, and the first failure logs at Error while the retries
  log at Warning, so a server that is simply not installed does not fill the log forever
- `notifications/tools/list_changed` is acted on live: the tool list is re-read and the
  registry's cache invalidated, so a tool that appears mid-session is offered on the next turn
- reconnecting to a server offering *the same* tools raises no change at all — the tool list
  is part of the prompt prefix providers hash, so a flapping server would otherwise bill you
  for a cache miss every few seconds
- **MCP tools are kept out of the monologue unless a server opts in.** The monologue runs
  unattended on a timer; a description written by a third party is at its most dangerous
  exactly where nobody is reading the output
- names and descriptions are treated as hostile input: names reduced to `[a-z0-9_]`,
  descriptions stripped of control characters, zero-width marks and bidirectional overrides,
  newlines flattened so a description cannot impersonate a section of the prompt, and length
  capped because it is sent on every request
- two server-side names that sanitise to the same thing: the second is dropped, since two
  tools with indistinguishable calls is worse than one missing tool
- child-process stderr goes to the Lane logger, not to the terminal the dashboard owns

Configured under `Lane:Tools:Mcp:Servers`, empty by default:

```jsonc
{ "Id": "fs", "Transport": "stdio", "Command": "npx",
  "Args": ["-y", "@modelcontextprotocol/server-filesystem", "/some/sandbox"] }
```

`Env` and `Headers` values may be secret references (`env:NAME`), resolved once at
composition like every other credential.

**Parity and cleanup (M10)**

The pass that mattered: going through v2 feature by feature found one thing the rewrite had
quietly dropped and one it had never wired up.

- **The enthusiasm gate was missing entirely.** v2 asked a small model whether a message was
  worth answering, and stayed quiet below 0.2. Eight milestones of v3 had no equivalent, so
  Lane answered every message she could see — which nothing failed on, because replying is
  never an error. It is back as `ResponsePolicyStage`, and it is the difference between an
  agent that sits in a busy channel and one that ruins it.
- The mechanism is new. v2 prefilled `` ```json `` and used a stop sequence to fish an object
  back out. This is a forced tool call, so the shape arrives already validated and it works
  the same on Anthropic and on anything OpenAI-compatible — which the prefill hack did not.
- A broken classifier makes her **talkative, not silent**. Silence from a failed gate is
  indistinguishable, from the outside, from Lane ignoring you.
- One-to-one conversations are not gated by default — a deliberate change from v2, which
  gated everywhere. "Was that meant for me?" is a real question in a channel and a
  meaningless one in a DM.
- Her face follows the conversation again: between M5 and M9 the only thing that could change
  her expression was her own `set_emoticon` tool.
- **The timezone offset was never configured**, so she had been reporting UTC. `-04:00:00`,
  matching v2's `HourShift`.

Deliberate differences from v2, not gaps: the read/search cadence knobs
(`ReadThoughtInterval`, `SearchThoughtInterval`) are gone because the monologue schedules
itself with `schedule_next_thought`; and vector search is not built — see the note on
long-term memory below.

**Soak.** `SoakTests` drives ~1,500 turns across 24 concurrent conversations and one
conversation hammered by eight writers at once, then checks what would accumulate over a
day: no failed turns, no cross-talk, memory instances evicted rather than growing one per
conversation, per-speaker ordering preserved. This is **not** the 24-hour four-surface run
the plan asked for, and does not substitute for it — what a real day catches is provider
flakiness and clock drift, which nothing here simulates.

`Wizard/` is gone. Its books moved to `Resources/Books/` first: they were gitignored, so
deleting the folder would have destroyed them outright rather than leaving them in history.

### Known gaps

`Docs/` still describes v2 — `IEar`, `IMouth`, `Settings.instance`, the single-body model.
The prose is stale against a codebase that no longer exists and wants rewriting against the
kernel-and-surfaces shape.

Vector search is deliberately absent — see the note on long-term memory below.

## Conventions worth knowing

**Stage order is registration order.** Each `ITurnStage` does its work then calls `next()`,
so `AddLaneCore` reads top to bottom: record what arrived, produce a reply, commit it, send
it. A moderation or rate-limiting stage is one line there.

**Speaker attribution lives in `PromptText`, not in the message.** No provider's wire
format has a per-message author field, so when a coalesced batch or a multi-party channel
puts several speakers in one turn, attribution has to be carried in the text. Every adapter
shares that one implementation. Lane's own turns are never prefixed — doing so teaches her
to emit the prefix, which is the bug v2 papered over by stripping `"Lane says:"` off
everything.

**Memory scope and conversational cohesion are separate axes.** A session's pump decides
*where a reply goes*; a handler's `Scope` decides *what it can draw on*. That is why Lane
can hold a private window per conversation and a shared long-term store at the same time,
configured independently:

```jsonc
{ "Id": "recent",   "Scope": "Session", "Slot": "Inline",   "Kinds": ["Utterance"] },
{ "Id": "thoughts", "Scope": "Global",  "Slot": "Volatile", "Kinds": ["Thought"], "Pinned": true }
```

`Slot` decides where recall lands: `Inline` becomes real conversation turns, `Stable` a
cached system block, `Volatile` an uncached one. The recent window **must** be `Inline` —
tool_use/tool_result pairs only survive as structured turns, and flattening them to text
(what v2 did with all memory) leaves a tool call nobody answered.

**Handlers are per-scope instances, and the pool owns their locking.** One
`(handler id, scope key)` pair is one live object, created on first touch, loaded from
SQLite, flushed on a timer, released when idle. Implementations hold plain mutable state
and never synchronise — a `Global` handler is written by every session pump and the
monologue at once, and serialising in one place beats asking every handler to get it right.

**A new ability is one class.** Derive from `Tool<TArgs>`, mark it `[LaneTool]`, and
assembly scanning finds it. The schema comes from the arguments record, so it cannot drift
from what the tool actually reads:

```csharp
[LaneTool]
public sealed class WeatherTool(HttpClient http) : Tool<WeatherTool.Args>
{
    public sealed record Args([property: Description("City name")] string City);

    protected override string Name => "get_weather";
    protected override string Description => "Current weather for a city.";

    protected override async ValueTask<ToolResult> InvokeAsync(Args a, ToolContext c, CancellationToken ct)
        => ToolResult.Ok(await Look(a.City, ct)).RememberAs($"[checked the weather in {a.City}]");
}
```

Nothing in the kernel, the pipeline or the prompt changes. Compare v2, where teaching Lane
to read a book meant editing the orchestrator's dispatch chain, the monologue prompt and
the settings model together.

**Nothing holds a list of the things it observes.** The dashboard shows every model,
session and surface without a reference to any of them — it subscribes to the event bus and
pivots. v2's token view had to be handed each model in order to add a handler to each, and
the emoticon was wired from the orchestrator into one HTTP server, so exactly one listener
could ever exist. Both shapes stop working the moment "how many are there" becomes a
configuration question.

**Speech starts before the reply is finished.** Text streams from the model, a chunker turns
it into clauses, and each clause is synthesised and played while the next is still being
written. The first clause is deliberately cut shorter than the rest — everything before she
starts talking is dead air, and a listener cannot tell a long opening clause from a fault.

**Her voice is a choice, and one of them needs nothing.** `Lane:Audio:Provider` selects
between `elevenlabs` and `flite` — [festvox/flite](https://github.com/festvox/flite), the
small local synthesiser Festival was cut down into. It is run as a child process rather than
bound as a library: flite ships as C, and a P/Invoke would mean shipping a native build per
platform for what is a fallback voice. The process costs a few milliseconds against
synthesis that is roughly a hundred times faster than real time — a whole clause comes back
in ~20 ms cold and under 10 ms warm, against 1.3–2.4 s to first audio through ElevenLabs.

ElevenLabs sounds better and always will. What flite buys is that she still talks with no
key, no network and no bill: a dead API, an unpaid account, a machine with no outbound route,
or a test that is not allowed to reach the internet. Which is also why an unknown provider
name throws at registration rather than falling back — silently reverting to the paid
provider is the wrong direction to fail in, and finding out at the first clause means
finding out mid-conversation.

```jsonc
{ "Provider": "flite",
  "Flite": { "ExecutablePath": "flite", "Voice": "slt", "DurationStretch": 0 } }
```

The binary is not installed by the build (`brew install flite`, `apt install flite`). `Voice`
left empty means flite's own default. **A voice flite does not have is refused rather than
substituted**: flite ignores an unknown `-voice` silently — no message, exit 0, its default
speaks instead — so a typo is not a failure, it is Lane sounding like somebody else, and
that gets blamed on the setting being ignored rather than on the name being wrong. The name
is checked against `flite -lv` once, before the first clause, and the error lists what this
build actually has. Both synthesisers then go through one `SpeechShaper`, because the tempo and pitch
chain is a large part of what makes the voice hers and a copy per provider is two voices as
soon as one of them is touched. **The rate comes from each clip's WAV header, not from
configuration**: flite's rate belongs to the voice — 8 kHz for the default diphone `kal`,
16 kHz for the clustergen ones — so assuming either resamples the other by a factor of two,
which is a chipmunk or a drawl depending on which way round. `Lane:Audio:Speech` is shared
between providers and its defaults are tuned for ElevenLabs; flite wants its own tempo and
pitch, or `DurationStretch`, which stretches during synthesis with the phonemes still in
hand rather than stretching the finished waveform.

**A voice is judged by ear, so `say` builds no host.** `lane say "…"` synthesises one line,
plays it out of the local speakers and exits — no model key, no Discord token, no database,
no dashboard. That matters because the thing being tested is a *feel*: tempo and pitch are
settled by trying six values in a row, and a command that costs a full startup each time is
one nobody runs twice.

```bash
lane say "Tide pools are full of things worth looking at."
lane say "Same line, slower." --voice slt --stretch 1.2 --pitch -1
lane say "Compare." --provider elevenlabs --out /tmp/lane.wav   # write instead of play
```

What it does share is the composition: the synthesiser comes out of `AddLaneAudio` reading
the same configuration the running bot reads, so what you hear is what a channel hears —
the flags are overrides on top of that, never a second code path. It reports what it used
and how long synthesis took against the length of the clip, since a voice being *fast* is
half of whether it is usable. All of that goes to stderr, following the same rule the
headless read loop does.

Playback is `SystemSpeaker`, an `IVoiceOutput` like any other, which hands a WAV to the
platform's own player (`afplay`, `paplay`/`aplay`, PowerShell's `SoundPlayer`). NAudio only
has output devices on Windows, and the alternative is a native audio dependency per platform
to play a test clip. Being an `IVoiceOutput` rather than a helper inside the command means a
desktop surface later gets local audio without any of this moving.

**Long-term memory stores facts, not messages.** v2 embedded individual chat lines and
searched with the latest one. A single message carries so little topical content that what
dominates its embedding is *style*, so similarity search mostly recovered who was talking
rather than what about. The unit that works is a self-contained statement — "Jahan keeps a
cuttlefish called Marlow" means something without the conversation around it, and a profile
of a dozen such facts costs almost nothing to carry in the cached block every turn.

**`link_identity` proves a link rather than asserting one.** Joining two accounts is the one
thing that can merge two people's memories, which is why the configured map never guesses.
A tool that let the model say "these two are the same person" would be that guess with extra
steps — models are agreeable, and "it's me, I'm also Jahan on Discord" is a sentence anyone
can type. So the tool takes no account name at all. Called bare it issues a code to whoever
is speaking; called with a code it links the account speaking *now* to the one that code was
issued to. Each half can only ever act on the participant actually in the turn, so the worst
a model can do with it is offer a code to the person in front of it.

The rest follows from where a code can be seen and what a link costs to undo. Both halves
must happen in a one-to-one session, because a code read aloud in a channel is a code a
bystander can claim onto their own account. Codes are single-use, expire in ten minutes, and
are consumed even by a failed attempt. Two accounts that are *both* already people are
refused rather than merged: memory is written under both ids by then and nothing here could
unpick which half belonged to whom afterwards, so that stays an operator's decision. A new
id is minted only when neither side has one, and it is checked against everyone who already
exists — a mint that landed on a configured person would be the silent merge the whole design
avoids. Configuration still wins over anything agreed in conversation, and runtime links are
stored separately, so "who did an operator link" stays a different question from "who linked
themselves". Links are read into memory at startup because `Resolve` is synchronous and runs
for every message that arrives.

What it does not do is move memory already written. A link takes effect from the next
message; anything `User`-scoped that accumulated under the unlinked key stays there, and old
transcript rows keep the global id they were written with.

**`set_my_name` renames the speaker, and only the speaker.** Someone asking to be called
something else is an ordinary request, but a name is how the transcript says who spoke, so
the tool takes no account to act on — the same rule as linking, for the same reason. It is
stored against the *person* rather than the account, so a name chosen on Discord is the name
in the terminal, and applied in `IdentityResolver` rather than at each call site, so one
request covers attribution, the transcript, `list_sessions` and the dashboard at once. A
surface keeps handing over whatever the account is called; the resolver has the last word.

A chosen name is hostile input, because it becomes the literal `"{name}: "` prefix on every
one of that person's turns. Newlines and control characters come out — a name with a newline
in it makes one message read as two, the second in somebody else's voice — along with every
Unicode **format** character. That last part is by category rather than by a list of the
marks worth worrying about, because the list this codebase already had was incomplete: the
left-to-right and right-to-left marks sit outside the override range `Lane.Tools.Mcp`
enumerates, and went straight through until a test went looking. **`McpNaming.CleanDescription`
still has that gap**, on text a third-party server wrote. Names are capped, cannot be Lane's
own — her turns are deliberately unprefixed, so a second Lane makes the transcript ambiguous
to her as much as to a reader — and cannot be one somebody else in the conversation already
answers to, or one another person has already chosen.

**The scratchpad is the memory she writes on purpose.** Handlers decide what Lane carries;
`write_note` is the one store she chooses the contents of, and it survives the sliding
window, the summariser and a restart untouched. It is `IKeyValueStore` under `note:`, at
global scope like book positions — writing in one conversation and reading in the next, or
during the monologue, is the entire point, so scoping notes per session would remove it. A
title is matched case- and whitespace-insensitively, because she will not write it back
character for character and "no such note" over a capital letter is worse than no
scratchpad. Notes are capped at 64 × 4000 characters and an overlong write is **refused
rather than truncated** — a note silently cut in half is one she reads back later and acts
on as though it were whole — which is why `delete_note` exists: a scratchpad that fills up
and then refuses everything new is the state a scratchpad is for avoiding.

**`set_session_description` is the other half of that, for what she cannot afford to go and
look up.** A note only exists when she thinks to read it, which is the wrong shape for
"everyone here speaks German" or "this channel is the D&D game and I am running it" — those
have to hold for every reply, including the first one after a restart, so they belong in the
prompt rather than behind a tool call. One description per conversation, keyed by *memory
group* rather than session id, so describing a Discord text channel also describes the voice
channel beside it and what she remembers of a conversation cannot come apart from what she
has written about it. Stored under one global scope with a `session-description:` prefix
rather than under each session's own scope, because `ListKeysAsync` enumerates within a
scope: per-session keys can be read one conversation at a time but never swept up at startup
without already knowing every group that exists. Read into memory at startup, like identity
links, since it is wanted while a prompt is being built on every turn.

It is **folded into the persona block** rather than added as a block of its own — it belongs
to the same slowly-changing prefix, and a separate block would spend one of the handful of
cache breakpoints a provider allows on a sentence — and appended in code rather than rendered
into the template, so a checkout with no template files keeps it and a conversation she has
said nothing about adds nothing at all. The prompt labels it as something she wrote for
herself, which is the honest framing: the text arrives from a conversation, by way of a model
that was asked to write it, and it lands in the system prompt.

**The routing gate carries it too**, in the third person and through a `{{description}}` slot
in `Routing.md` rather than by appending, because in that prompt the last thing read should
be the instruction to call `assess` — the fallback splits around it for the same reason.
Otherwise the one part of the turn that could see nothing she had written about a conversation
would be the part deciding whether the rest of the turn happens at all: "everyone here speaks
German" only changes how a reply reads, but "this is the D&D game and I am running it" changes
whether a line of table talk aimed at nobody in particular is hers to answer. The wording is
about her rather than in her voice, since a first-person note dropped into a prompt that talks
*about* Lane to a small classifier reads as instructions to the classifier. Cleaning removes control and
Unicode format characters — newlines stay, since this is a paragraph rather than a name, but
a run of blank lines that would push it far enough down to read as a section of its own does
not — and it is capped at 500 characters, **refused rather than truncated**. What none of
that can do is stop the description from *saying* anything in particular: someone in the
conversation can talk her into writing a standing instruction to herself. The cap, the label,
and the fact that she can be asked to erase it are the answer, not character stripping. The
monologue is deliberately excluded even though it can see every session — a thought that
quietly rewrote what Lane believes about a channel she is not in would be
indistinguishable, from the inside, from having always believed it.

Vector search is deliberately **not** built. A rolling summary plus a small profile covers
most of what "she remembers me" means; retrieval earns its place once history outgrows what
summaries can hold, and that decision is easier to make with these in place.

**Energy is a rate, not a quota.** `Lane:Energy` gives her a token budget over a rolling
window — twenty million a day — which every model call debits and which accrues back a
hundred-and-forty-fourth at a time, every ten minutes. What is left as a fraction of the
budget is her energy, and it changes how she behaves rather than whether she is allowed to
act: above 70% nothing is different; below it her replies get a lower ceiling, her agent loop
gets fewer steps and tool calls, and her thoughts spread out. Running out is **sleep**, which
is still not a hard stop — she costs money the moment somebody says her name.

Accrual is **lazy, from wall time**. The stored state is `(remaining, accruedThrough)` and any
read replays however many whole intervals have elapsed since, advancing the phase by whole
steps rather than to *now* so that reading the number often does not drag the next accrual
forward. A restart, a suspended laptop and a test with a fake clock therefore all behave
identically, and the ten-minute timer is a heartbeat for the dashboard rather than the
mechanism — a missed tick costs nothing. One `TokenUsageEvent` subscription catches respond,
monologue, routing and summarize together, because every model in the host is already wrapped
in `TelemetryLanguageModel`.

**Sleep is latched, not derived.** It is set when a debit takes her to zero and cleared only by
accrual reaching 70% or by being named with something still in the tank. That is what makes
"asleep at 30%" and "awake at 60%" both representable — being dragged out to answer one message
must not count as having woken up, and `Wake` returning false is exactly that case: she answers
and goes straight back under. Remaining may go negative, because the budget is a rate and
several concurrent turns can each start with tokens in hand and all finish; it is floored at a
whole window's debt so one runaway turn cannot put her under for a week.

`SleepGateStage` sits between ingest and context assembly. After ingest, because that is what
puts the incoming messages into `Produced` and a message she slept through must still reach the
transcript; before assembly, because one she is going to ignore should not cost a memory read;
and before the response policy, because that gate is a model call and asking a small model
whether a message was meant for her is paying to find out something that cannot matter.
Sleeping through a message costs **zero** model calls. Her name is matched as a plain
word-boundary pattern rather than anything surface-specific, since the Discord surface already
resolves a mention of Lane to the literal word "Lane" in the message text — so one rule covers
being @-mentioned, being typed at, the terminal and the API. A hyphen is a word boundary, so
"multi-lane" does reach her; that is a known cost of the simple rule.

A slept-through turn is the one thing in the harness that is **written down but not remembered**
— `TurnContext.RecordToMemory`, distinct from `Suppressed`, which has until now meant both. The
transcript stays complete, so nothing said to her is ever lost, while no handler runs: no
summariser, no profiler, and so no model calls at all. Being suppressed by the routing gate is
still remembered, because sitting a conversation out is not the same as not being there.

**Tiredness reaches the tools by refusal, not by hiding them.** A tier names globs it will not
run — `web_*` while worn out — and `ToolRegistry.Gate` enforces that only when a call arrives,
never when advertising. Withholding the tool would change `ToolSet.Fingerprint`, and with it the
cache lineage of every request behind it: a prompt-cache miss bought in exchange for saving
tokens, at the moment she is trying to spend less, flapping each time she crosses a threshold.
This is the same argument `ToolOptions.PreferStableSet` makes about capabilities, and the
refusal is text she can read rather than a tool that silently vanished. A tier's ceilings are
clamped *against* the configured budget rather than replacing it, so tiredness can only ever
lower a limit — a tier configured generously is not a way around `AgentBudget`.

One number for all of her, like the monologue loop: a DM and a busy channel share it. Worth
knowing, because the first surprise is "she answered me on Discord but ignored me on the
terminal".

**Her inner life is one loop that names its target.** The monologue has no session of its
own; when a thought is worth saying it calls `speak_to_session`, which posts onto that
session's queue rather than writing to its channel. That single rule is why a volunteered
remark cannot cut across a reply in flight, and why it gets remembered in the conversation
it was said in.

**A surface owns nothing.** The Discord surface has no model, no memory, no tools and no
say in whether Lane replies — it turns gateway events into `InboundEvent`s and attaches a
channel the kernel writes back through, exactly as the terminal does. That symmetry is what
lets them run in one process, and it is why adding the HTTP API cost one project and two
lines in the host: a keyed factory registration, and the streaming observer.

**Her Discord status is a line several things share.** Discord gives a bot one string, so the
status is composed rather than written: each source owns a `DiscordStatusSlot` and sets or
clears only its own part, and the enum's order is the order they render in. Her face is the
first slot and reads off the event bus like the face server does — the surface is told
nothing directly, and it deliberately does not filter by session, since a mood she reached in
another conversation is still her mood. Adding "reading *Solaris*" or who she is listening to
later is a slot and a subscription, with nothing already on the line touched.

Writes are coalesced, not queued: presence is rate limited per gateway session and her
expression changes with every turn, so only the latest line survives the wait — an emoticon
from three moods ago is not worth a slot in that budget. A failed update is logged and
dropped, because a status is how she looks and not how she works, and the line is sent again
on `Ready`, since Discord forgets a bot's presence when the session re-identifies.

**Model capabilities are declared per instance, not per provider.** OpenRouter fronts
hundreds of models whose tool support differs completely, so a model says what it can do
and startup holds it to that:

```jsonc
{ "Id": "default", "Provider": "openrouter", "Model": "deepseek/deepseek-v4-flash",
  "KeyRef": "env:OPENROUTER_API_KEY", "Capabilities": ["Tools", "Streaming"] }
```

**One role does not want prose at all.** The response policy gate asks whether Lane should
reply and how warmly — a decision, not a sentence. TypeSafe's Jev is a System One model
built for exactly that: it evaluates typed *questions* against a *state* and returns
structured answers, so the gate's two fields become a Score and a Choice in a single call.
It cannot generate text, so `ValidateEvaluatorRoles` refuses to let it serve any role but
`routing`, and its criteria live in `Prompts/Routing.typesafe.json` rather than in a prompt.
`ViaOpenRouter` sends the same body to OpenRouter instead of TypeSafe, which fronts System
One on its native shape rather than as chat completions:

```jsonc
{ "Id": "jev", "Provider": "typesafe", "Model": "jev-latest",
  "ViaOpenRouter": true, "KeyRef": "env:OPENROUTER_API_KEY" }
```

**The transcript and memory are different things, deliberately.** The transcript is the
complete record — every `tool_use` and `tool_result`, exactly as it happened. Memory holds
only what is safe to replay as conversation turns, which means `MemorySanitizer` strips
tool traffic on the way in. That is not tidiness: a stored `tool_use` whose `tool_result`
later falls out of a sliding window makes *every* subsequent request in that conversation
invalid, and it stays broken until the orphan rolls out of history.
