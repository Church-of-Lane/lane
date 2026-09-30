# Lane

![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![C#](https://img.shields.io/badge/C%23-14.0-239120)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-blue)](https://lane-bot.readthedocs.io)
![Surfaces](https://img.shields.io/badge/surfaces-Discord%20%7C%20terminal%20%7C%20HTTP-5865F2)
![enbyware](https://pride-badges.pony.workers.dev/static/v1?label=enbyware&labelColor=%23555&stripeWidth=8&stripeColors=FCF434%2CFFFFFF%2C9C59D1%2C2C2C2C)

Lane is a Discord/terminal chatbot designed to feel authentic and personality-driven, following in the spirit of the Twitch streamer [Neuro-sama](https://www.twitch.tv/vedal987). She selectively responds to messages, maintains memory across conversations, initiates her own conversations, and can speak and listen in voice channels.

---

## Table of Contents
- [Features](#features)
- [Layout](#layout)
- [Tech Stack](#tech-stack)
- [Self-Hosting](#self-hosting)
- [Documentation](#documentation)
- [Support](#support)

---

## Features

- **Many surfaces, one Lane** — Discord (any number of bot instances), the terminal and an HTTP API run concurrently in one process, without cross-talk
  ![Bot in VC](Resources/Images/VC.png)
- **Selective responses** — a small routing model decides whether a message is worth answering, rather than replying to everything in a channel
  ![Not responding](Resources/Images/NotResponding.png)
- **Memory as configuration** — sliding windows, rolling summaries and per-person profiles, each with its own scope (session, user, global) and prompt slot; all of it survives a restart in SQLite
- **Internal monologue** — one global loop, not one per conversation. She thinks in her own words, schedules her own next thought, and can volunteer a remark into a conversation she names
- **Energy** — a rolling token budget that changes how she behaves rather than whether she may act: shorter replies, fewer tool steps and slower thoughts when low, sleep at zero, and waking when named
- **Identities** — one person across Discord, the terminal and the API, linked by configuration or by a proven code, never guessed from a matching name. She can be asked to call you something else, and can recognise you by voice
- **Tools** — web search, page fetching, book reading, a scratchpad she writes on purpose, saved images, per-conversation descriptions, voice-channel control, emoticons, Discord reactions, and backing out of a reply the router let through. A new ability is one class
- **MCP** — configured servers contribute their tools, namespaced and sanitised, retried when down, kept out of the unattended monologue unless a server opts in
- **Nodes** — other people can lend Lane a model over a WebSocket, earn credits for answering, and spend them to sponsor where she listens
- **Voice** — streaming TTS through ElevenLabs or local [flite](https://github.com/festvox/flite), Azure STT, barge-in and a per-session floor. She walks into a voice channel when asked rather than at startup
- **Image understanding** — she can see and interpret images shared with her
  ![Viewing an image](Resources/Images/Seeing.png)
- **Time awareness** — keeps track of the current time
  ![Being used as timer](Resources/Images/Time.png)
- **Web dashboard and face** — live logs, token usage per model, sessions, role bindings and config on one page, plus a separate face endpoint for her expression
- **Extensive configuration** — surfaces, models, memory handlers, energy and prompts are all `appsettings.json`

---

## Layout

| Project | Holds |
|---|---|
| `Lane.Core` | Identity, messages, the session pump, the turn pipeline, prompts, and the model / tool / memory *abstractions* |
| `Lane.Providers` | Anthropic, OpenAI-compatible (OpenRouter, DeepSeek, anything compatible) and TypeSafe System One adapters |
| `Lane.Memory` | SQLite state, transcript and key-value stores; the window, summary and profile handlers |
| `Lane.Audio` | Resampling, continuous recognition, streaming synthesis, the audio router and the voice floor |
| `Lane.Tools`, `Lane.Tools.Mcp` | The built-in abilities, and the MCP client |
| `Lane.Surfaces.Discord`, `.Terminal`, `.Api` | The transports |
| `Lane.Nodes`, `Lane.Nodes.Protocol`, `Lane.Node.Sdk` | The node listener, credits and portal; the wire protocol; the SDK for writing a node |
| `Lane.Host` | Composition root, configuration, secrets, prompt templates, web dashboard and face |
| `Lane.Testing`, `Lane.Tests` | An offline harness, and xUnit tests that need no network |
| `Deploy` | `collect.sh` / `install.sh` for moving an instance to another machine |

---

## Tech Stack

| Component | Technology |
|---|---|
| Language | C# (.NET 10) |
| LLM | Anthropic, any OpenAI-compatible endpoint (OpenRouter, DeepSeek), TypeSafe System One, or community nodes |
| Storage | SQLite |
| TTS | ElevenLabs or flite (local) |
| STT | Azure |

---

## Self-Hosting

### 1. Prerequisites

- **Required:** an API key for at least one model provider
- **For Discord:** a Discord bot application and its token
- **For voice:** Azure speech key and region; ElevenLabs key unless she speaks through flite (`brew install flite` / `apt install flite`)
- **For search:** a Brave Search API key

### 2. Configure your `.env` file

Secrets are referenced from configuration as `env:NAME`, so the names are yours. These are the ones the shipped `appsettings.json` expects:

```env
OPENROUTER_API_KEY=  # or ANTHROPIC_API_KEY, if a model instance points at it
DISCORD_API_KEY=     # Required for Discord
BRAVE_API_KEY=       # Required for web search
AZURE_KEY=           # Required for STT
AZURE_REGION=        # Required for STT
ELEVENLABS_KEY=      # Required for TTS unless using flite
LANE_IOS_KEY=        # One per API client app
```

A surface whose token is missing logs and is skipped; the rest still run.

### 3. Configure and run

Edit `Lane.Host/appsettings.json`, or drop an `appsettings.local.json` beside it to override locally. Memory lands in `lane.db` next to the binary.

```bash
dotnet test Lane.Tests/Lane.Tests.csproj     # offline, no network
dotnet run --project Lane.Host               # every enabled surface, in one process
dotnet run --project Lane.Host -- say "hi"   # speak one line and exit, no host at all
dotnet run --project Lane.Host -- migrate --data <v2 data.json> --dry-run
```

The terminal surface is stdin and stdout, with logs on stderr so a piped conversation stays clean. The dashboard is a web page — `Lane:Dashboard`, port 5090 by default — and her face is served separately on port 5050.

Moving an instance to another machine is `./Deploy/collect.sh` on the old one and `./Deploy/install.sh` on the new one; see [Deploy/README.md](Deploy/README.md).

---

## Documentation

- [HTTP API](Docs/api.md) — sessions, streamed replies, the event feed and the voice socket
- [Nodes](Docs/nodes.md) — lending Lane a model, credits and sponsorship
- [Identities](Docs/identities.md) — how she decides who is speaking
- [Lane.md](Lane.md) — the design of v3, and why each part is shaped the way it is
- [v2 docs](Docs/v2/index.md) — the single-surface bot that came before the rewrite, kept for reference