# Babel Deck

[![Sponsor](https://img.shields.io/github/sponsors/mta1124-1629472?label=Sponsor&logo=GitHub)](https://github.com/sponsors/mta1124-1629472)
[![CI](https://github.com/mta1124-1629472/Babel-Deck/actions/workflows/ci.yml/badge.svg)](https://github.com/mta1124-1629472/Babel-Deck/actions/workflows/ci.yml)
[![GitHub Release](https://img.shields.io/github/v/release/mta1124-1629472/Babel-Deck)](https://github.com/mta1124-1629472/Babel-Deck/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Alpha](https://img.shields.io/badge/status-early%20alpha-orange)](#status)

> **Early alpha.** Core workflow is functional end-to-end, but the app is under active development. Expect rough edges, missing polish, and breaking changes between builds.

Babel Deck is a Windows desktop dubbing workstation. Load a local video, generate a timed transcript via Whisper, translate the dialogue, synthesize dubbed speech via TTS, and preview the result in-context — all from a single session.

![Babel Deck preview](Assets/preview.png)

---

## What it does today

The end-to-end workflow runs from source media through to playable dubbed audio:

1. **Load media** — open any local video file (MP4, MKV, AVI, WebM, MOV)
2. **Transcribe** — generate a timed source-language transcript using Whisper (via Python)
3. **Translate** — produce target-language dialogue adapted for spoken delivery
4. **Generate dubbed audio** — synthesize TTS audio per segment
5. **Preview** — scrub the source video, toggle dub mode to hear TTS follow the video in real time, and click any segment in the sidebar to jump directly to that timestamp
6. **Persist sessions** — work is saved between launches; artifacts are restored without re-running the pipeline

Segment selection in the sidebar is live: the active segment highlights as the video plays and the list scrolls to track it automatically.

---

## What it does not do yet

To be direct about current limits:

- **No audio mixing** — dubbed TTS and source audio are not mixed; they play independently
- **No sync guarantee** — TTS follows the video segment-by-segment, not frame-accurate lip sync
- **No export** — there is no way to export a dubbed video file yet
- **No settings UI** — Python/FFmpeg paths are detected automatically or must be configured manually in the session store
- **Windows only** — libmpv is loaded via P/Invoke from a bundled DLL; macOS and Linux are not supported
- **No offline TTS** — TTS currently requires a configured cloud or local Python inference endpoint
- **No multi-language UI** — the interface is English only

---

## Requirements

| Dependency | Notes |
|-----------|-------|
| Windows 10/11 x64 | Only tested platform |
| [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | Required to run |
| Python 3.10+ | Required for transcription, translation, and TTS |
| Whisper-compatible Python environment | Transcription backend |
| FFmpeg | Audio extraction; placed in `tools/win-x64/ffmpeg.exe` or on `PATH` |
| libmpv-2.dll | Bundled in `native/win-x64/` — GPU video output via `vo=gpu` |

---

## Run from source

```bash
git clone https://github.com/mta1124-1629472/Babel-Deck.git
cd Babel-Deck
dotnet build
dotnet run --project BabelDeck.csproj
```

Tests:

```bash
dotnet test
```

---

## How the pipeline works

```
source video
    └─ ingest (copy to session artifact dir)
        └─ transcribe (Whisper via Python subprocess)
            └─ translate (Python inference)
                └─ TTS generation (per segment, Python)
                    └─ preview (libmpv + Avalonia UI)
```

All artifacts are stored in a session directory under `%APPDATA%\BabelDeck\sessions\`. The session survives restarts; switching between multiple source files within a run caches prior work in memory and restores it without re-running the pipeline.

---

## Status

Milestone 7 (Dub Session Workflow) is the current completed milestone. The core chain — load → transcribe → translate → TTS → playback — works end to end. The next focus is refinement tooling, per-segment editing, and export.

This is early alpha software. The architecture is intentionally narrow and scope-controlled. Features are added in verified vertical slices, not as partially-wired stubs.

---

## Contributing

Read these before touching anything:

- [`AGENTS.md`](AGENTS.md) — operating rules and scope discipline
- [`PLAN.md`](PLAN.md) — milestone order and gates
- [`docs/architecture.md`](docs/architecture.md) — structural boundaries and state ownership
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — contributor workflow and verification expectations

The short version: a feature is not done because it compiles. It is done when it has build, tests, and a written smoke result.
