# Nodes

A **node** is a process that answers model requests for Lane. It connects to Lane over a WebSocket, joins a named
**pool**, and gets the same requests Lane would otherwise send to a model provider: the system prompt, the
conversation, and the tools on offer. It answers with a model response. Lane sends each request to the least busy node
in the pool.

Nodes are how people outside the operator lend Lane a model. Each accepted response earns the node's identity
**credits**. Credits can be sent to other identities, or spent to **sponsor** a Discord channel or an API client so
that Lane listens there.

---

## How nodes fit in

The operator enables the node listener and binds a model instance to a pool. From then on, any role bound to that
instance is answered by nodes:

```jsonc
"Nodes": { "Enabled": true, "Urls": "http://0.0.0.0:5070" },
"Models": {
  "Instances": [
    { "Id": "community", "Provider": "node", "Pool": "default", "Capabilities": ["Tools", "Streaming"] }
  ],
  "Roles": { "monologue": "community" }
}
```

What this means for a node:

- **Capabilities are a promise the pool makes.** If your node joins a pool without declaring a capability the pool's
  model instance promises (for example `Tools`), Lane logs a warning but still sends you requests. Declare only what you
  can actually do.
- **Replies are not streamed.** Lane waits for your whole response and hands it on in one piece.
- **An empty pool falls through.** A model instance whose pool has no nodes connected is served by the pool named
  `default`; if that one is empty too, the request goes to the model instance with the id `default`, so a host with no
  nodes online keeps answering on an ordinary provider. Requests only wait out `AcquireTimeout` when the pool does
  have nodes and they are all busy.
- **Timeouts.** You have 10 seconds after connecting to send your hello (`HelloTimeout`). A request waits up to 30
  seconds for a free slot in the pool (`AcquireTimeout`), and your node then has 5 minutes to answer
  (`RequestTimeout`).
- **Tools run on Lane, not on the node.** If the model wants a tool, return the tool calls with `StopReason.ToolUse`.
  Lane runs the tools and sends a follow-up request that includes the results.

`GET /v1/nodes` on the listener lists every connected node by pool: name, model, key id, capabilities, and in-flight
and maximum concurrent requests. It needs no authentication.

---

## The node SDK

`Lane.Node.Sdk` (.NET 10) handles the connection, reconnecting, concurrency, cancellation and signing. All you supply
is a key and a function that turns a `ModelRequest` into a `ModelResponse`. Reference the project directly: it depends
on `Lane.Core` and `Lane.Nodes.Protocol`.

```csharp
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Node.Sdk;

using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

using FileNodeKey key = FileNodeKey.LoadOrCreate("node-key.pem");

LaneNode node = new(
    new LaneNodeOptions
    {
        LaneUrl = "ws://lane.example.com:5070",
        Model   = "my-model",
        Pool    = "default"
    },
    key,
    async (request, ct) =>
    {
        string text = await MyModel.CompleteAsync(request, ct);

        return new ModelResponse([new TextPart(text)], StopReason.EndTurn, default);
    });

node.Connected    += connection => Console.WriteLine($"Connected as {node.Identity.KeyId} ({connection})");
node.Disconnected += reason     => Console.WriteLine($"Disconnected: {reason}");

await node.RunAsync(stop.Token);
```

`RunAsync` stays connected until the token is cancelled. It reconnects with exponential backoff, starting at
`ReconnectMin` and capped at `ReconnectMax`. The backoff resets after each connection Lane accepts.

### Wrapping an existing provider

The adapters in `Lane.Providers` already speak `ModelRequest` and `ModelResponse`, so a node that forwards to an
OpenAI-compatible API is just the adapter plus a key:

```csharp
using HttpClient http = new();

OpenAiCompatibleModel model = new(http, new OpenAiCompatibleOptions
{
    InstanceId   = "node",
    Model        = "deepseek/deepseek-v4-flash",
    Endpoint     = "https://openrouter.ai/api/v1",
    ApiKey       = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")!,
    Capabilities = ModelCapabilities.Tools | ModelCapabilities.StopSequences
}, logger);

LaneNode node = new(options, key, (request, ct) => model.CompleteAsync(request, ct));
```

### Options

| `LaneNodeOptions` | Default | Meaning |
|---|---|---|
| `LaneUrl` | *required* | Lane's node listener, e.g. `ws://lane.local:5070`. `http`/`https` become `ws`/`wss`. A bare host gets `/v1/nodes/connect` added. |
| `Model` | *required* | What you answer with. Shown on the leaderboard and in `/v1/nodes`, and nothing else. |
| `Pool` | `default` | The pool to join. It must match the `Pool` of a model instance, or nothing is sent to you. |
| `Name` | machine name | Shown next to your identity. |
| `Capabilities` | `Tools, Streaming, StopSequences` | What your handler supports. See `ModelCapabilities`: `Tools`, `ParallelTools`, `Streaming`, `Images`, `PromptCaching`, `StructuredOutput`, `StopSequences`, `Thinking`, `Audio`. |
| `MaxConcurrency` | `4` | The most requests Lane will send you at once. |
| `ReconnectMin` / `ReconnectMax` | 1 s / 30 s | Reconnect backoff bounds. |

### Writing the handler

The handler gets a `ModelRequest`:

| Field | Meaning |
|---|---|
| `System` | Ordered `PromptBlock`s, each with a `CacheHint`. Keep them in order. |
| `Messages` | The conversation as `LaneMessage`s. Content parts can be `TextPart`, `ImagePart`, `ToolUsePart`, `ToolResultPart` or `ThinkingPart` — and `AudioPart`, a WAV of what was just said beside its transcript, if both your node and the pool declare `Audio`. |
| `Tools`, `ToolChoice` | The tools on offer, and whether one must be called. |
| `ResponseFormat` | A JSON schema, when structured output is requested. |
| `MaxOutputTokens`, `Temperature`, `StopSequences` | Generation settings. |

It returns a `ModelResponse(Content, Stop, Usage)`:

- Put text in `TextPart`s and tool calls in `ToolUsePart`s. When you return tool calls, stop with `StopReason.ToolUse`.
- Fill in `TokenUsage` (input, output, cache read, cache write, latency) as honestly as you can. Lane's telemetry and
  energy budget read it. Leave `ModelInstanceId` empty, because Lane replaces it.
- Never set `Origin`. The SDK clears it and Lane fills it in.

A handler that **throws** sends a failure with the exception message, and Lane's model call fails. The
`CancellationToken` is cancelled when Lane stops waiting for the request or when the connection drops. A handler that
exits because it was cancelled sends nothing.

Up to `MaxConcurrency` handler calls can run at once, so the handler must be thread-safe.

---

## Node identity

Every node connects as an **identity**: an ECDSA P-256 public key. Its **key id** is the lowercase hex of the first 16
bytes of the SHA-256 hash of the key's SubjectPublicKeyInfo. That key id is what the leaderboard shows, what earns
credits, and what signs in to the portal. The node's name can change freely, but the key id cannot. **Losing the key
means losing the identity, and every credit it holds.**

Every response is signed. The SDK signs the request id, a newline, and the response JSON (with `Origin` removed), then
sends the signature with the reply. Lane attaches the identity and signature to the response as its `Origin`, so a
response can always be traced back to the identity that produced it. The default validator accepts every reply. It
does not reject replies because of their signature.

There are two kinds of identity.

### Key file

```csharp
FileNodeKey key = FileNodeKey.LoadOrCreate("node-key.pem");   // or Create(path), Load(path), Generate()
```

The file holds an unencrypted PKCS#8 PEM P-256 private key. `Create` refuses to overwrite an existing file. On
Unix-like systems it creates the file readable only by you. **Whoever holds the file is the identity**, so back it up
and keep it private.

### Security key (WebAuthn)

A FIDO2 security key can be the identity instead, so the private key never leaves the hardware. Security keys can only
sign through a browser, so the SDK has the key sign **once per connection**: it vouches for a fresh session key that
lives in memory, and the session key signs every response.

```csharp
using WebAuthnSession session = new();

// 1. In a browser, have the security key sign session.Challenge (see below).
// 2. Hand the assertion back:
WebAuthnNodeKey key = session.Complete(
    credentialPublicKey,          // SubjectPublicKeyInfo from registration: AuthenticatorAttestationResponse.getPublicKey()
    authenticatorData,
    clientDataJson,
    signature,
    credentialId);                // lets the portal find this identity when you sign in with the key
```

In the page:

```js
const assertion = await navigator.credentials.get({
  publicKey: {
    challenge,                                               // session.Challenge bytes
    allowCredentials: [{ type: "public-key", id: credentialId }],
    userVerification: "discouraged"
  }
});

// Send these back to the node:
//   assertion.response.authenticatorData
//   assertion.response.clientDataJSON
//   assertion.response.signature
```

Some things to know:

- Only ES256 (P-256) credentials work. Check a registered key with `WebAuthnSession.IsSupportedCredentialKey`.
- The identity is the **credential's** public key, so keep the SubjectPublicKeyInfo from when you registered it.
  Lane never sees the registration.
- A `WebAuthnSession` completes exactly once. `Complete` throws `ArgumentException` if the assertion does not match
  the session, and the session key is lost when the process exits, so every restart needs another touch.
- Browsers only offer WebAuthn in a secure context: `https://`, or `http://localhost`.

---

## Credits, sponsorship and the portal

The node listener serves a portal at its root, for example `http://lane.example.com:5070/`.

**Public:** the leaderboard (identities ranked by responses, and who is online), Lane's current status (surfaces,
face, energy, nodes online), and reading the forum.

**After signing in:** your wallet, your nickname, transfers, sponsorships, and posting to the forum. You can sign in
with the security key, or with a key-file identity by signing the challenge. Only identities that have connected a
node at least once can sign in. A sign-in lasts an hour.

### Earning and sending

- Every response Lane accepts earns the node's identity `CreditsPerResponse` credits (1 by default).
- Transfers go to another identity's key id and can carry a note of up to 140 characters. Balances never go below
  zero.
- A nickname can be up to 32 characters, and no two identities can share one.

### Sponsoring

Sponsoring spends your credits so that Lane listens somewhere she otherwise wouldn't. Each sponsored message costs
`CreditsPerSponsoredRequest` (1 by default).

| Target | What it does |
|---|---|
| **Discord channel** | Lane answers in a channel that isn't in her configured channel list, as long as some sponsor can pay. Each message she receives there costs one charge. Channels the operator has excluded can't be sponsored. |
| **API client** | Creates an API client with the id you choose, and gives you its key (`lane_…`) **exactly once**. That key works like any other [API key](api.md#authentication). Each message sent, and each voice socket opened, costs one charge. Once no sponsor can pay, requests fail with `402 out_of_credits`. |

A target can have several sponsors. Each charge goes to the sponsor charged least recently that still has the balance
and daily allowance to pay. Each sponsor can set a **daily limit**, which resets at midnight UTC. Withdrawing stops
your share immediately.

### Portal API

Everything the page does is available as JSON. Endpoints under `/api/me` need `Authorization: Bearer <token>` from a
sign-in.

| Method | Path | |
|---|---|---|
| `GET` | `/api/leaderboard` | Ranked identities |
| `GET` | `/api/stats` | Lane's status |
| `POST` | `/api/sign-in/challenge` | Returns `{ challenge, credentialIds }` |
| `POST` | `/api/sign-in` | Security key: `{ credentialId, authenticatorData, clientDataJson, signature }`, all base64url |
| `POST` | `/api/sign-in/key` | Key file: `{ publicKey, challenge, signature }`, all base64url; the signature is IEEE P1363 over `"lane-portal-sign-in\n"` followed by the challenge bytes |
| `POST` | `/api/sign-out` | |
| `GET` | `/api/me` | Balance and the last 50 ledger entries |
| `PUT` | `/api/me/nickname` | `{ nickname }`; null clears it |
| `POST` | `/api/me/transfers` | `{ to, amount, memo }` |
| `GET` | `/api/me/sponsorships` | Every sponsored target and its sponsors |
| `POST` | `/api/me/sponsorships` | `{ kind: "DiscordChannel" \| "ApiClient", target, dailyLimit, name }` |
| `PUT` | `/api/me/sponsorships/{kind}/{target}` | `{ dailyLimit }` |
| `DELETE` | `/api/me/sponsorships/{kind}/{target}` | Withdraw |
| `GET` | `/api/forum/posts?sort=bumped\|newest` | |
| `GET` | `/api/forum/posts/{id}` | A post with its comments |
| `POST` | `/api/me/forum/posts` | `{ title, description }` |
| `POST` | `/api/me/forum/posts/{id}/comments` | `{ body }` |

Errors come back as `{ "error": "…" }`.

---

## Wire protocol

Use the SDK if you can. If you're writing a node in another language, this is what goes over the socket.

Connect a WebSocket to `/v1/nodes/connect`. Every frame is one JSON text message with a `type` field. Property names
are camelCase and enums are strings. The current protocol version is **2**.

```
node → lane   hello      { name, pool, model, capabilities, maxConcurrency, identity, delegation?, protocolVersion: 2 }
lane → node   welcome    { connectionId }
lane → node   request    { requestId, request: ModelRequest }
lane → node   cancel     { requestId }
node → lane   response   { requestId, response: ModelResponse, signature }
node → lane   failure    { requestId, message }
```

- `identity` is `{ keyId, algorithm, publicKey }`. `algorithm` is `ecdsa-p256-sha256` for a key that signs directly,
  or `webauthn-es256` for a security key. `publicKey` is base64 SubjectPublicKeyInfo.
- `delegation` is sent only for security keys: `{ sessionPublicKey, authenticatorData, clientDataJson, signature,
  credentialId }`. The security key's WebAuthn challenge is the SHA-256 hash of the session public key.
- `capabilities` is a comma-separated flags string, such as `"Tools, Streaming, StopSequences"`.
- Content parts carry a `$kind` of `text`, `image`, `tool_use`, `tool_result` or `thinking`.
- `signature` is base64 DER ECDSA/SHA-256 over `requestId + "\n" + <response JSON without origin>`. The JSON is Lane's
  own serialisation of the response, so a node in another language has to reproduce it byte for byte. That is the main
  reason to prefer the SDK.
- A hello with the wrong protocol version, or no hello within the timeout, closes the socket with a policy violation.

---

## Response recording

To build a classifier that checks node answers, Lane can record every model call it makes, including calls answered by
providers and by nodes. Turn it on with `Lane:Recording`:

```jsonc
"Recording": { "Enabled": true, "Directory": "training", "QueueCapacity": 1024 }
```

Each call is one line in `<Directory>/responses-yyyy-MM-dd.jsonl` (UTC date). A relative `Directory` is resolved
against the build output. Records are written on a background task. A call that fails isn't recorded, and records that
arrive while `QueueCapacity` are already waiting are dropped with a warning.

| Field | Meaning |
|---|---|
| `v` | Record format version, currently `1`. |
| `id`, `trace_id`, `timestamp` | Record id, the request's trace id, and when the model answered. |
| `task` | What the call was for: `respond`, `monologue`, `routing`, `summarize` or `profile`. |
| `model` | `{ instance, provider, id }`. |
| `source` | `provider` or `node`. Node records also carry `node: { key_id, algorithm }`. |
| `params` | `max_output_tokens`, `temperature`, `tool_choice`, and `stop_sequences` and `response_format` when set. |
| `tools` | `{ name, description, parameters }` for every tool offered. |
| `messages` | The prompt as an OpenAI-style chat array: one `system` message, then `user`, `assistant` (with `tool_calls`) and `tool` messages. User messages carry the same `Name: ` speaker prefix the model saw. Images become `[image]`. |
| `completion` | The answer as an `assistant` message, with `reasoning` when the model returned thinking. |
| `stop_reason`, `usage` | How the call ended, and its token counts and latency. |

**Names are redacted.** Before a record is written, every display name and global user id Lane knows is replaced with
a placeholder such as `[NAME_1]`. That means the authors of the request's messages and everyone in a currently active
session. Within one record, every name a person goes by gets the same placeholder. Matching is by whole word and
ignores case, and `Lane` is left alone. A name Lane doesn't know about isn't redacted, such as a friend someone
mentions in passing, or someone who appears in memory but isn't in any active session.
