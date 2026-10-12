# HTTP API

The API surface lets your own app talk to Lane. It covers per-app keys, conversations you name yourself, replies
streamed as they're written, a live feed of what Lane is doing, and a voice socket.

Without a `Urls` setting it listens on `http://127.0.0.1:5080`, loopback only. The shipped `appsettings.json` binds
`http://0.0.0.0:5080` so it is reachable from other networks; every request still needs a client key. All request and response bodies are JSON with camelCase property names, and properties that would be
`null` are left out.

```bash
export LANE=http://127.0.0.1:5080
export LANE_KEY=...   # your client key

curl -H "Authorization: Bearer $LANE_KEY" -H 'Content-Type: application/json' \
     -d '{"text":"what are you up to?"}' \
     "$LANE/v1/sessions/main/messages?stream=true" -N
```

---

## Authentication

Every endpoint except `GET /v1/health` needs a **client key**. There are three ways to send it:

| Where | Form | Accepted on |
|---|---|---|
| Header | `Authorization: Bearer <key>` | everything |
| Header | `X-Lane-Key: <key>` | everything |
| Query | `?key=<key>` | **only** the voice WebSocket, because browsers can't set headers on a WebSocket handshake |

Keys come from one of two places:

- **Configured clients.** The operator adds an entry under the API surface's `Clients`, with an `Id`, a `Name` and a
  secret. The client id becomes your namespace (see below).
- **Sponsored clients.** Someone with node credits creates a client in the [node portal](nodes.md#sponsoring) and gets
  a `lane_…` key, shown once. Every message and every voice socket opened is charged to its sponsors. When none of them
  can pay, the request fails with `402 out_of_credits`. Sponsored client ids are prefixed `sponsored.`.

A missing or wrong key gets `401 unauthorized`. If the operator has enabled anonymous access, which only works on a
loopback address, requests without a key are let in as the client `anonymous`.

### Health

```
GET /v1/health   →   200 { "status": "ok", "surface": "api" }
```

### Errors

Errors come back as `{ "error": "<code>", "detail": "<human-readable explanation>" }`.

| Status | `error` | When |
|---|---|---|
| 400 | `invalid_key` | The session key contains characters that aren't allowed (see below) |
| 400 | `empty_message` | The message has neither text nor an image |
| 400 | `invalid_image` | An image is malformed, too large, or there are too many (see [Images](#images)) |
| 400 | `not_a_websocket`, `unsupported_format`, `conflicting_speaker` | Voice socket problems (see below) |
| 401 | `unauthorized` | No key, or a key Lane doesn't recognise |
| 402 | `out_of_credits` | A sponsored client whose sponsors can't pay |
| 403 | `forbidden` | Asking for `?all=true` without permission to observe other sessions |
| 503 | `voice_unavailable` | Audio isn't configured on this instance |

---

## Sessions

A **session** is one conversation. You name it yourself, with a **session key** such as `main`, `kitchen` or
`user-42`. There's nothing to create first: sending a message to a key that doesn't exist yet opens that session.

- Session keys can contain letters, digits, `-`, `_` and `.`, up to 96 characters.
- **Keys are namespaced by client.** Two apps that both use `main` get two separate conversations, and neither can
  read or post into the other's.
- **`shared:` opts out of the namespace.** Every client that uses `shared:lobby` lands in the same conversation. It's
  the only way for two apps to meet.

Each session has a canonical **session id** of the form `surface/Kind/localKey`, for example `api/Api/ios/main` or
`api/Api/shared:lobby`. Lane returns it in responses, and it's what the event feed refers to.

Lane handles one turn at a time per session. Messages that arrive close together are **coalesced** into a single turn
rather than dropped, and a message that arrives while she's replying is answered afterwards. Different sessions run
concurrently.

### Create or configure a session

```
POST /v1/sessions
{ "key": "main", "displayName": "Kitchen tablet", "memoryGroup": null, "direct": true }
```

Every field is optional. `key` defaults to `main`.

| Field | Meaning |
|---|---|
| `displayName` | What the session is called in Lane's session list and the dashboard. |
| `memoryGroup` | Which conversation memory this session shares. It defaults to `api/<client>/<key>`. Two sessions given the same group share a summary and recent history. |
| `direct` | Whether this is a one-to-one conversation. It defaults to **true**. Direct sessions skip the "was that meant for me?" gate, and some identity tools only work in them. Set it to `false` for a session that relays a group. |

This returns the session:

```json
{
  "id": "api/Api/ios/main",
  "key": "main",
  "displayName": "Kitchen tablet",
  "surface": "api",
  "kind": "Api",
  "memoryGroup": "api/ios/main",
  "state": "Idle",
  "mine": true,
  "lastActivity": "2026-09-15T14:02:11Z",
  "participants": ["Jahan"],
  "capabilities": ["..."]
}
```

### List sessions

```
GET /v1/sessions            your client's live sessions, most recent first
GET /v1/sessions?all=true   every live session, including Discord and the terminal
```

`?all=true` is only allowed for clients the operator has marked `CanObserveAllSessions`. It gives you a read-only view:
seeing a Discord channel in the list doesn't let you post into it.

### Read history

```
GET /v1/sessions/{key}/messages?limit=50&after=<sequence>&scope=session
```

```json
{
  "sessionId": "api/Api/ios/main",
  "messages": [
    { "id": "…", "sequence": 41, "role": "User", "kind": "Utterance", "author": "Jahan",
      "text": "what are you up to?", "timestamp": "…", "externalId": "msg-17" },
    { "id": "…", "sequence": 42, "role": "Assistant", "kind": "Utterance", "author": "Lane",
      "text": "Reading.", "timestamp": "…" }
  ]
}
```

- Messages always come back **oldest first**.
- Without `after`, you get the most recent `limit` messages. With `after`, you get the messages after that sequence
  number, which lets you page forward or poll for new ones.
- `limit` defaults to 50 and is capped at the operator's `MaxHistory` (500 by default).
- By default, history covers the whole **memory group**, so a text session and the voice session beside it read as one
  conversation. Add `scope=session` to get only this session's messages.

### Interrupt

```
POST /v1/sessions/{key}/cancel   →   { "cancelled": true }
```

This stops whatever Lane is in the middle of saying in that conversation, in both the text session and its voice
session. `cancelled` is `false` if she wasn't saying anything.

### Close

```
DELETE /v1/sessions/{key}   →   204
```

This closes the live session. Its history and memory stay, and sending another message to the same key reopens it.

---

## Sending messages

```
POST /v1/sessions/{key}/messages
{ "text": "what are you up to?", "author": { "id": "alice", "name": "Alice" }, "requiresResponse": true, "externalId": "msg-17" }
```

| Field | Meaning |
|---|---|
| `text` | The message. Required unless there are `images`. |
| `images` | Pictures to show Lane with the message. See [Images](#images). |
| `author` | Who's speaking, if your app relays more than one person. Leave it out and the message comes from your client itself. See [identities](identities.md#api-clients). |
| `requiresResponse` | Defaults to `true`. Set it to `false` for things Lane should hear and remember without answering, like context from your app or someone else's side of a conversation. |
| `externalId` | Your own id for the message. It's stored with the message and returned in history. |

**Lane can choose not to reply.** In a session that isn't direct, a small model first decides whether the message was
meant for her. When she's out of energy, she sleeps through anything that doesn't use her name. Your app should treat
"no reply" as a normal outcome.

### Images

```
POST /v1/sessions/{key}/messages
{
  "text": "what is this?",
  "images": [
    { "url": "https://example.com/cat.png" },
    { "data": "iVBORw0KGgo…", "mediaType": "image/png" }
  ]
}
```

Each image is given one of two ways:

- **`url`**: an absolute `http` or `https` link. Lane doesn't download it; the model's provider does, so it has to be
  reachable from the public internet.
- **`data`**: the file's bytes in base64 (plain base64, not a `data:` URL), with a `mediaType` of `image/png`,
  `image/jpeg`, `image/gif` or `image/webp`.

A message can carry up to `MaxImages` images (4 by default), and an image sent as `data` can be up to `MaxImageBytes`
(5 MiB by default) once decoded. Anything else gets `400 invalid_image`, and nothing is charged.

Whether Lane actually sees a picture depends on the model behind her; one without vision ignores or rejects it.
In [history](#read-history), a message's images come back as `images: [{ url?, mediaType }]`. The bytes of an
image sent as `data` are stored with the conversation but aren't returned.

### Fire and forget

Without `stream`, the endpoint returns as soon as the message is queued:

```
202 { "sessionId": "api/Api/ios/main", "messageId": "…" }
```

Pick up the reply from [history](#read-history), the [event feed](#event-feed), or a stream on a later message.

### Streaming a reply

Add `?stream=true` and the response becomes [server-sent events](https://html.spec.whatwg.org/multipage/server-sent-events.html)
that follow the turn as it happens:

```
event: accepted
data: {"sessionId":"api/Api/ios/main","messageId":"…"}

event: tool
data: {"name":"web_search","phase":"start"}

event: tool
data: {"name":"web_search","phase":"end","isError":false}

event: delta
data: {"text":"Reading "}

event: delta
data: {"text":"Solaris again."}

event: message
data: {"sessionId":"api/Api/ios/main","text":"Reading Solaris again.","replyTo":"…"}

event: done
data: {"silent":false}
```

| Event | Payload | Meaning |
|---|---|---|
| `accepted` | `{ sessionId, messageId }` | Your message has been queued. This is sent immediately. |
| `delta` | `{ text }` | The next piece of the reply as it's generated. Replies from [node](nodes.md)-backed models arrive in a single delta. |
| `tool` | `{ name, phase: "start" \| "end", isError? }` | Lane is using a tool. |
| `message` | `{ sessionId, text, replyTo? }` | A finished message Lane delivered. A turn can produce more than one. |
| `error` | `{ error }` | The turn failed. This ends the stream. |
| `done` | `{ silent, reason? }` | The turn is over. `silent: true` means she chose not to reply. `reason: "timeout"` means the stream hit its time limit (`StreamTimeout`, 2 minutes by default). This ends the stream. |

Lines starting with `:` are keep-alive comments, sent every 15 seconds while nothing else is happening. Ignore them.

**Hanging up doesn't cancel anything.** The turn belongs to the conversation, not to your request, so the reply is
still delivered and remembered, and you can read it from history. Use [`/cancel`](#interrupt) to actually stop her.

---

## Event feed

```
GET /v1/events?types=turn.started,turn.completed,presence
```

This is a server-sent event stream of everything happening across all of Lane, on every surface. It's the same feed
the dashboard and the face page read. Leave out `types` to get everything. The stream starts with
`event: ready` and runs until you disconnect.

| Event | Payload |
|---|---|
| `turn.started` | `{ session, kind, incoming }` |
| `turn.completed` | `{ session, kind, suppressed, toolCalls, durationMs }` |
| `turn.failed` | `{ session, error }` |
| `session` | `{ session, kind, detail }`: a session opened, closed or changed |
| `tool.invoked` | `{ tool, session }` |
| `tool.completed` | `{ tool, isError, durationMs }` |
| `presence` | `{ emoticon }`: Lane's face changed |
| `surface` | `{ surface, connected, detail }` |
| `tokens` | `{ model, role, input, output, cacheRead, cacheWrite }`, sent **only** to clients allowed to observe all sessions |

The feed covers all of Lane, not just your sessions, so filter on `session` if you only care about your own. If you
read too slowly, the oldest events are dropped rather than holding Lane up.

---

## Voice socket

```
WS /v1/sessions/{key}/voice?rate=16000&channels=1&outRate=24000&outChannels=1&speaker=Alice
```

The socket carries a microphone and a speaker for one conversation. You stream audio in, and Lane sends back her voice
as audio along with JSON events for what she said. You can have several sockets open at once, including several on
the same conversation, which Lane hears as one room.

The voice conversation is its own session (`api/Voice/<client>/<key>`), so it takes turns separately from text. It
**shares the memory group** of the text session with the same key, though, so what's said out loud appears in that
key's history and Lane remembers both as one conversation. If a socket closes, the session stays open, and
reconnecting picks it back up.

### Query parameters

| Parameter | Default | Meaning |
|---|---|---|
| `key` | | Your client key, if you can't send a header |
| `rate`, `channels` | `16000`, `1` | What you send. Accepted formats are **16 kHz mono**, or **48 kHz mono or stereo**. Anything else gets `400 unsupported_format`. |
| `outRate`, `outChannels` | operator default (`24000`, `1`) | What Lane sends back |
| `speaker` | your client | The one person on this microphone. Their words are attributed to them. |
| `diarize` | `false` | This microphone is in a room with several people, so Lane should tell voices apart. This needs voiceprints enabled on the instance ([Voices](identities.md#voices)). It can't be combined with `speaker`. |

### Frames

**You send:**

- **Binary frames:** raw 16-bit signed little-endian PCM in the format you declared. Frame size doesn't matter.
- **Text frames:** control messages. `{"type":"cancel"}` or `{"type":"interrupt"}` makes Lane stop talking, the same as
  if someone had spoken over her.

**Lane sends:**

- **Binary frames:** her voice, as 16-bit signed little-endian PCM in the output format.
- **Text frames:** JSON of the form `{ "type": "...", "data": { ... } }`.

| `type` | `data` |
|---|---|
| `ready` | `{ session, input: { rate, channels, encoding }, output: { rate, channels, encoding }, diarize }`, sent once when the socket opens |
| `speaking` | `{ state: "start" \| "end" }`, sent around each stretch of audio she sends |
| `message` | `{ text }`: what she said, for showing a transcript next to the audio |

Speaking over Lane interrupts her, the same way it would in a room. A socket with no traffic is closed after
`IdleTimeout` (30 minutes by default).

---

## CORS

Browsers can only call the API from origins the operator lists in `AllowedOrigins`. If that list is empty, CORS is off
and only non-browser clients (or same-origin pages) can use the API.
