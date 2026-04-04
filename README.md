# Babel Player

[![Sponsor](https://img.shields.io/github/sponsors/mta1124-1629472?label=Sponsor&logo=GitHub)](https://github.com/sponsors/mta1124-1629472)
[![CI](https://github.com/mta1124-1629472/Babel-Player/actions/workflows/ci.yml/badge.svg)](https://github.com/mta1124-1629472/Babel-Player/actions/workflows/ci.yml)
[![GitHub Release](https://img.shields.io/github/v/release/mta1124-1629472/Babel-Player)](https://github.com/mta1124-1629472/Babel-Player/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/mta1124-1629472/Babel-Player)](LICENSE)

**Babel Player is a Windows desktop dubbing workstation.** Load source media, generate a timed transcript, translate the dialogue, produce a spoken dub, and preview the result in context — all without leaving the app.

```
source media → timed transcript → translated dialogue → spoken dubbed output → in-context preview
```

> Babel Player is built and maintained by a solo developer.
> If you find it useful, consider sponsoring:
>
> [![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/R5R01WOOYW)

![Babel Player preview](Assets/preview.png)

---

## Table of Contents

- [What It Does](#what-it-does)
- [Feature Overview](#feature-overview)
- [Provider Support](#provider-support)
- [Compute Modes](#compute-modes)
- [Requirements](#requirements)
- [Installation](#installation)
- [First Run](#first-run)
- [Source Build](#source-build)
- [Dependencies](#dependencies)
- [Current Limitations](#current-limitations)
- [Roadmap](#roadmap)
- [Project Layout](#project-layout)
- [Contributing](#contributing)

---

## What It Does

Babel Player is a dubbing workstation, not a subtitle editor or a translation tool in isolation. The goal is to get a piece of foreign-language source media to a point where you can hear the translated dialogue spoken back — and then refine it until it sounds right.

The full loop:

1. **Load** a local video or audio file
2. **Transcribe** — generate a timed transcript using local AI or a cloud API
3. **Translate** — adapt the transcript into a target language
4. **Dub** — generate a spoken TTS audio track, one segment at a time
5. **Preview** — play source video alongside dubbed segments; toggle between original and dub audio
6. **Refine** — regenerate individual segments, adjust text, re-run TTS on demand
7. **Export** — save captions as `.srt`
8. **Persist** — sessions save automatically; reopen and continue later

---

## Feature Overview

### Pipeline

- Segment-based workflow: each transcript line is an independent unit that can be individually translated, re-dubbed, or replaced
- Full pipeline runs in order: transcription → translation → TTS generation
- Individual segments can be regenerated at any stage without re-running everything
- Stage gating: downstream stages only enable when upstream results are present and artifacts are on disk

### Compute Selection

Every inference stage exposes a `CPU / GPU / Cloud` selector — no hidden routing. If the selected compute path is unavailable, the stage blocks with a clear remediation message; there is no silent fallback.

- **CPU** — local subprocess; works on any Windows machine; no GPU required
- **GPU** — routes through the managed local venv host (default) or a Docker host backend; NVIDIA GPU with CUDA required
- **Cloud** — calls a remote API; requires the corresponding API key in Settings

The GPU path defaults to a **managed local venv host** that the app can bootstrap for you. Docker is only required if you deliberately switch the advanced GPU backend to `Docker GPU host`.

### Playback and Preview

- Embedded video playback powered by **libmpv** with GPU-accelerated rendering
- Source scrubbing and segment-aware navigation
- Toggle between source audio and dubbed segment audio in real time
- Subtitle overlay with bilingual display (source and translated text)
- Auto-hiding controls bar and fullscreen mode
- Hover-reveal volume slider

### Session Management

- Sessions auto-save to `%LOCALAPPDATA%\BabelPlayer\state\`
- Recent sessions list with one-click restore
- Artifacts (transcripts, translations, TTS audio) are cached per-session under `%LOCALAPPDATA%\BabelPlayer\sessions\{SessionId}\`
- On restore, missing artifacts are detected and the stage downgrades gracefully — no stale state

### Settings and Credentials

- Per-stage provider, model, and voice selection persisted across launches
- In-app API key manager with live validation
- Bootstrap diagnostics surface missing dependencies and configuration gaps at startup
- Hardware-aware compute type policy (selects `float16` / `int8` / `float8` based on GPU generation)

### Export

- SRT caption export — prefers translated text, falls back to source text

---

## Provider Support

### Transcription (Speech to Text)

| Provider | Compute | Notes |
|---|---|---|
| Faster-Whisper | CPU / GPU | Local subprocess or containerized GPU host; models: `tiny`, `base`, `small`, `medium`, `large-v3` |
| OpenAI Whisper API | Cloud | Requires OpenAI API key |
| Google Cloud STT | Cloud | Requires Google Cloud credentials |
| Google Gemini | Cloud | `gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`; supports transcription-only or transcription + translation in one pass |

### Translation

| Provider | Compute | Notes |
|---|---|---|
| CTranslate2 | CPU / GPU | Local; lightweight; containerized GPU variant available |
| NLLB-200 | CPU / GPU | Local subprocess; models: `distilled-600M`, `distilled-1.3B`, `1.3B`; containerized GPU variant uses larger models |
| DeepL API | Cloud | Requires DeepL API key |
| OpenAI API | Cloud | Requires OpenAI API key |
| Google Gemini | Cloud | `gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`; requires Gemini API key |
| Google Translate (free) | Cloud | Unreliable; rate-limited web scraper; use for quick informal tests only |

### Text to Speech

| Provider | Compute | Notes |
|---|---|---|
| Piper | CPU | Local neural TTS; fully offline; lower voice quality |
| Edge TTS | Cloud | Free; no API key; Microsoft voices |
| ElevenLabs | Cloud | Requires ElevenLabs API key; high quality |
| Google Cloud TTS | Cloud | Requires Google Cloud credentials |
| OpenAI TTS | Cloud | Requires OpenAI API key |
| XTTS v2 | GPU | Containerized; voice cloning via reference audio; auto-extracts reference from source video |
| Qwen3-TTS | GPU | Containerized; alternative GPU TTS path |

### Diarization

| Provider | Compute | Notes |
|---|---|---|
| Pyannote | CPU | Local subprocess; requires HuggingFace token and model gate acceptance; not in default requirements install |

---

## Compute Modes

| Mode | Transcription | Translation | TTS |
|---|---|---|---|
| **CPU** | Faster-Whisper (subprocess) | CTranslate2 / NLLB-200 (subprocess) | Piper (subprocess) |
| **GPU** | Faster-Whisper (containerized host) | NLLB-200 1.3B or CTranslate2 (containerized host) | XTTS v2 or Qwen3-TTS (containerized host) |
| **Cloud** | OpenAI / Google / Gemini | DeepL / OpenAI / Gemini / Google Translate | Edge TTS / ElevenLabs / Google Cloud / OpenAI |

The GPU path bootstraps a managed Python venv host that runs the inference server. The app detects whether you have a CUDA-capable NVIDIA GPU and selects the appropriate compute dtype automatically (`float16` for older CUDA GPUs, `float8` for Blackwell+, `int8` for CPU-only).

---

## Requirements

| Scenario | Requirements |
|---|---|
| Any mode | Windows 10 or 11 x64 |
| CPU local path | `ffmpeg.exe` (bundled in release); Python 3.10+ is managed automatically by the app if `uv.exe` is present |
| GPU managed venv path | NVIDIA GPU with CUDA support |
| GPU Docker backend | Docker Desktop with a Linux engine; NVIDIA container toolkit for GPU pass-through |
| Cloud providers | The relevant API key entered in Settings |
| Source build | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

---

## Installation

### Portable release (recommended)

1. Download `Babel-Player-<version>-win-x64-portable.zip` from [GitHub Releases](https://github.com/mta1124-1629472/Babel-Player/releases/latest).
2. Extract to a folder of your choice, e.g. `C:\Apps\BabelPlayer`.
3. Run `BabelPlayer.exe`.

The release bundle is self-contained and includes:

- `BabelPlayer.exe` and all managed .NET dependencies (including the .NET runtime)
- `ffmpeg.exe`
- `libmpv-2.dll`
- `docker-compose.yml` (for the optional Docker GPU backend)
- Inference host assets under `inference/`
- `uv.exe` for managed Python venv bootstrapping

No installer, no registry entries. To uninstall: delete the folder and optionally clear `%LOCALAPPDATA%\BabelPlayer\`.

---

## First Run

1. Launch `BabelPlayer.exe`.
2. Click **Open** and load a local video or audio file.
3. In the pipeline pane, pick `CPU`, `GPU`, or `Cloud` for each stage.
4. Select a provider and model or voice for that stage.
5. If using cloud providers, go to **Settings → API Keys** and enter your credentials.
6. Click **Transcribe**. Review the timed segments.
7. Click **Translate**. Review the translated text.
8. Click **Generate TTS**. Wait for all segments to render.
9. Use the playback controls to preview. Toggle **Dub mode** to hear the dubbed audio.
10. Regenerate individual segments as needed from the segment list.
11. Export captions with **Export Captions** when done.

Sessions save automatically. Your session will appear in the recent sessions list next time you open the app.

---

## Source Build

```powershell
git clone https://github.com/mta1124-1629472/Babel-Player.git
cd Babel-Player
dotnet build
dotnet run --project BabelPlayer.csproj
```

Run the full verification suite:

```powershell
dotnet test
python scripts/check-architecture.py
python -m py_compile inference/main.py
```

The architecture linter (`scripts/check-architecture.py`) enforces structural rules: provider string constants, ViewModel pipeline call discipline, coordinator line limits, and `PLACEHOLDER` requirements on unimplemented stubs.

---

## Dependencies

### Runtime (bundled in release)

| Dependency | Purpose |
|---|---|
| [Avalonia 12.0.0-rc1](https://avaloniaui.net/) | Desktop UI framework |
| [libmpv](https://mpv.io/) | Native media playback (GPU-accelerated video rendering) |
| [CommunityToolkit.MVVM 8.2.1](https://github.com/CommunityToolkit/dotnet) | MVVM observables and commands |
| [ffmpeg](https://ffmpeg.org/) | Media ingest, audio extraction, and segment mixing |

### Python inference host (`inference/requirements.txt`)

| Package | Purpose |
|---|---|
| `fastapi` / `uvicorn` | Inference HTTP server |
| `faster-whisper` | Local speech recognition |
| `ctranslate2` / `sentencepiece` | Local translation (CTranslate2 / NLLB) |
| `tts` (Coqui) | XTTS v2 voice synthesis |
| `qwen-tts` | Qwen3-TTS voice synthesis |
| `torch` / `transformers` / `accelerate` | ML runtime |
| `soundfile` / `numpy` | Audio I/O |

> Pyannote diarization is **not** in the default requirements; it requires a separate HuggingFace token and model gate acceptance.

### Cloud APIs (optional, key required)

- [OpenAI](https://platform.openai.com/) — Whisper transcription, GPT translation, TTS
- [ElevenLabs](https://elevenlabs.io/) — High-quality TTS
- [Google Cloud](https://cloud.google.com/) — Speech-to-Text, Cloud TTS
- [Google Gemini](https://ai.google.dev/) — Transcription and translation
- [DeepL](https://www.deepl.com/pro-api) — Translation

---

## Current Limitations

- **Windows only.** Linux and macOS are not supported. The architecture is designed for future portability but no cross-platform work has been done yet.
- **No video export.** The VideoExportPlanner backend is implemented and tested, but the feature is not yet surfaced in the app's UI. SRT caption export works.
- **GPU TTS hardware verified as partial.** XTTS v2 and Qwen3-TTS are wired end-to-end; real-hardware smoke tests on a physical NVIDIA GPU are still pending (milestone 12 gate).
- **GPU diarization not available.** Pyannote runs CPU-only. There is no GPU diarization path.
- **Google Translate (free) is unreliable.** It uses a web scraper and is rate-limited. Use a real API provider for production work.
- **No real-time or streaming.** All stages process the full session; segment-level regeneration is available after the initial pass.
- **Session restore does not auto-re-run.** If artifacts are missing on restore, the pipeline resets to the last verified stage; you re-run manually.
- **Blackwell GPU (`float8`) dtype is wired but unverified on real Blackwell hardware.**

---

## Roadmap

### In progress — Milestone 12: Runtime Optimization and Hardware Routing

- [ ] GPU validation on real NVIDIA hardware (RTX path for FasterWhisper and XTTS confirmed end-to-end)
- [ ] NVDEC / D3D11VA hardware decode path in libmpv transport verified
- [ ] Live container health status visible in Settings UI
- [ ] WSL-hosted Python inference path tested and documented
- [ ] Runtime routing diagnostics surfaced in UI when a selected path is degraded
- [ ] Hardware/diagnostics panel accessible to the user
- [ ] Benchmark suite in `benchmarks/` used for regression tracking

### Planned — Milestone 13: Release Hardening

- Packaged Windows build (NUKE publish pipeline)
- Clean-machine validation (full workflow without dev-environment assumptions)
- Crash and support log artifacts usable by a non-developer
- Video export UI wired through (mux dubbed audio into source container)
- Final SRT export, session restore, and API key setup verified on packaged build

### Under consideration (post 1.0)

- macOS and Linux support
- Multi-speaker workflow surfaced in UI (backend already implemented)
- Timeline editing: adjust segment timing visually
- Batch processing: multiple files in sequence
- Streaming / real-time preview for long-form content

---

## Project Layout

```
Babel-Player/
├── Models/                  # Domain records and enums (session state, segments, providers, compute profiles)
├── Services/                # Workflow coordinator, providers, persistence, transport, host management
│   └── Registries/          # Per-stage provider registries with compute-aware filtering
├── ViewModels/              # MVVM layer
├── Views/                   # Avalonia XAML UI
├── BabelPlayer.Tests/       # xUnit integration tests
├── inference/               # Python inference server (FastAPI + Faster-Whisper + CTranslate2 + XTTS + Qwen)
├── scripts/                 # Architecture linter and tooling
├── benchmarks/              # Stage benchmark run artifacts
├── tools/                   # Bundled tooling (uv, ffmpeg placed at publish time)
├── native/win-x64/          # libmpv-2.dll
├── docs/
│   ├── architecture.md      # Structural boundaries and state ownership
│   ├── smoke/               # Milestone verification notes
│   └── containers.md        # Docker/WSL deployment notes
└── test-assets/             # Sample media for tests
```

Key files:

| File | Role |
|---|---|
| [Services/SessionWorkflowCoordinator.cs](Services/SessionWorkflowCoordinator.cs) | Single owner of all workflow and session state |
| [ViewModels/EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs) | Playback, preview, segment selection, dub mode |
| [Models/ProviderNames.cs](Models/ProviderNames.cs) | All provider identifier constants |
| [Models/ComputeProfile.cs](Models/ComputeProfile.cs) | CPU / GPU / Cloud enum |
| [Services/InferenceRuntimeCatalog.cs](Services/InferenceRuntimeCatalog.cs) | Compute profile → provider routing |
| [inference/main.py](inference/main.py) | Python inference server (transcription, translation, TTS) |
| [App.axaml.cs](App.axaml.cs) | Startup / composition root |

---

## Contributing

Read these first:

- [AGENTS.md](AGENTS.md) — operating rules and non-negotiables
- [PLAN.md](PLAN.md) — milestone order and gates
- [CONTRIBUTING.md](CONTRIBUTING.md) — contributor workflow and scope discipline
- [docs/architecture.md](docs/architecture.md) — structural map and ownership rules

Minimum verification before opening a PR:

```powershell
dotnet build
dotnet test
python scripts/check-architecture.py
```

The project is in active milestone hardening. Contributions that preserve the working dubbing loop and keep readiness behavior truthful are welcome. Speculative features, silent fallbacks, and scope expansions outside the current milestone will be declined.


```
source media → timed transcript → translated dialogue → spoken dubbed output → in-context preview
```

> Babel Player is built and maintained by a solo developer.
> If you find it useful, consider sponsoring:
>
> [![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/R5R01WOOYW)

![Babel Player preview](Assets/preview.png)

---

## Table of Contents

- [What It Does](#what-it-does)
- [Feature Overview](#feature-overview)
- [Provider Support](#provider-support)
- [Compute Modes](#compute-modes)
- [Requirements](#requirements)
- [Installation](#installation)
- [First Run](#first-run)
- [Source Build](#source-build)
- [Dependencies](#dependencies)
- [Current Limitations](#current-limitations)
- [Roadmap](#roadmap)
- [Project Layout](#project-layout)
- [Contributing](#contributing)

---

## What It Does

Babel Player is a dubbing workstation, not a subtitle editor or a translation tool in isolation. The goal is to get a piece of foreign-language source media to a point where you can hear the translated dialogue spoken back — and then refine it until it sounds right.

The full loop:

1. **Load** a local video or audio file
2. **Transcribe** — generate a timed transcript using local AI or a cloud API
3. **Translate** — adapt the transcript into a target language
4. **Dub** — generate a spoken TTS audio track, one segment at a time
5. **Preview** — play source video alongside dubbed segments; toggle between original and dub
6. **Refine** — regenerate individual segments, adjust text, re-run TTS on demand
7. **Export** — save captions as `.srt`
8. **Persist** — sessions save automatically; reopen and continue later

---

## Feature Overview

### Pipeline

- Segment-based workflow: each transcript line is an independent unit that can be individually transcribed, translated, re-dubbed, or discarded
- The full pipeline runs in order: transcription → translation → TTS generation
- Individual segments can be regenerated at any point without re-running the entire pipeline
- Stage gating: downstream stages only enable when upstream results are present

### Playback and Preview

- Embedded video playback powered by **libmpv** with GPU-accelerated rendering
- Source scrubbing and segment-aware navigation
- Toggle between source audio and dubbed segment audio in real time
- Subtitle overlay with bilingual display (source and translated text)
- Auto-hiding controls bar and fullscreen mode
- Volume control with hover-reveal slider

### Session Management

- Sessions persist to `%LOCALAPPDATA%\BabelPlayer\state\`
- Recent sessions list with one-click restore
- Artifacts (transcripts, translations, TTS audio files) are cached per-session under `%LOCALAPPDATA%\BabelPlayer\sessions\{SessionId}\`
- On restore, missing artifacts are detected and the pipeline stage downgrades gracefully — no silent stale state

### Settings and Credentials

- Per-stage provider and model selection persisted across launches
- In-app API key manager with live validation
- Bootstrap diagnostics surface missing dependencies and configuration gaps at startup
- Settings window with runtime/model/voice pickers

### Compute Selection

Each inference stage exposes a `CPU / GPU / Cloud` selector. The app does not silently fall back — if a selected compute path is unavailable, the stage blocks with a clear remediation message.

### Export

- SRT caption export (prefers translated text; falls back to source text)

---

## Provider Support

### Transcription (Speech to Text)

| Provider | Runtime | Notes |
|---|---|---|
| Faster-Whisper | Local (CPU / GPU) | Models: `tiny`, `base`, `small`, `medium`, `large-v3` |
| OpenAI Whisper API | Cloud | Requires OpenAI API key |
| Google Cloud STT | Cloud | Requires Google Cloud credentials |
| Google Gemini | Cloud | `gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`; requires Gemini API key |

### Translation

| Provider | Runtime | Notes |
|---|---|---|
| NLLB-200 | Local (CPU) | Models: `distilled-600M`, `distilled-1.3B`, `1.3B` |
| DeepL API | Cloud | Requires DeepL API key |
| OpenAI API | Cloud | Requires OpenAI API key |
| Google Gemini | Cloud | `gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`; requires Gemini API key |
| Google Translate (free) | Cloud | Unreliable; rate-limited; use for quick tests only |

### Text to Speech (Dubbing)

| Provider | Runtime | Notes |
|---|---|---|
| Edge TTS | Cloud (free) | No API key required; Microsoft voices |
| Piper | Local (CPU) | Offline; fast; lower voice quality |
| ElevenLabs | Cloud | Requires ElevenLabs API key; high quality |
| Google Cloud TTS | Cloud | Requires Google Cloud credentials |
| OpenAI TTS | Cloud | Requires OpenAI API key |
| XTTS (containerized) | Containerized GPU | Requires Docker with NVIDIA container support |

### Diarization (Speaker Detection)

| Provider | Runtime | Notes |
|---|---|---|
| Pyannote | Local (CPU) | Requires HuggingFace token and model gate acceptance |

---

## Compute Modes

Every inference stage has a `Compute` selector:

- **CPU** — runs locally via Python subprocess on your machine's CPU
- **GPU** — routes to the managed local GPU host (default) or the Docker GPU backend
- **Cloud** — calls a remote API; requires the corresponding API key in Settings

The GPU path uses a **managed local host** by default. The app can bootstrap this host automatically. Docker is only required if you explicitly switch the advanced GPU backend to `Docker GPU host`.

The app never silently falls back between compute modes. If GPU is selected and unavailable, the stage stays blocked until the problem is resolved.

---

## Requirements

| Scenario | Requirements |
|---|---|
| Any mode | Windows 10 or 11 x64 |
| CPU local path | `ffmpeg.exe` (bundled in release); Python 3.10+ if no managed venv runtime is detected |
| GPU managed local path | NVIDIA GPU with CUDA support |
| GPU Docker backend | Docker Desktop with a Linux engine; NVIDIA container toolkit for GPU pass-through |
| Cloud providers | The relevant API key entered in Settings |
| Source build | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

### Python dependencies (local inference)

The managed venv host installs these automatically on first use:

- `faster-whisper` — local transcription
- `ctranslate2` — NLLB translation backend
- `sentencepiece` — NLLB tokenization
- `piper-tts` — local TTS
- `pyannote.audio` — speaker diarization
- `edge-tts` — cloud TTS via Edge
- Standard transport packages: `torch`, `torchaudio`, `numpy`, `requests`

For containerized inference, the image is built from `inference/Dockerfile` and installs `inference/requirements.txt`.

---

## Installation

### Portable release (recommended)

1. Download `Babel-Player-<version>-win-x64-portable.zip` from [GitHub Releases](https://github.com/mta1124-1629472/Babel-Player/releases/latest).
2. Extract to a folder of your choice, e.g. `C:\Apps\BabelPlayer`.
3. Run `BabelPlayer.exe`.

The release bundle is self-contained and includes:

- `BabelPlayer.exe` and all managed .NET dependencies
- Bundled .NET runtime (no separate .NET install required)
- `ffmpeg.exe`
- `libmpv-2.dll`
- Inference host assets under `inference/`
- `uv.exe` for managed Python venv bootstrapping (when included)

No installer is required. No registry entries are written. To uninstall, delete the folder and optionally clear `%LOCALAPPDATA%\BabelPlayer\`.

---

## First Run

1. Launch `BabelPlayer.exe`.
2. Click **Open** and load a local video or audio file.
3. In the pipeline pane, pick `CPU`, `GPU`, or `Cloud` for each stage.
4. Select a provider and model/voice for each stage.
5. If using cloud providers, go to **Settings → API Keys** and enter your credentials.
6. Click **Transcribe**. Review the timed segments.
7. Click **Translate**. Review the translated text.
8. Click **Generate TTS**. Wait for all segments to render.
9. Use the playback controls to preview. Toggle **Dub mode** to hear the dubbed audio.
10. Regenerate individual segments as needed from the segment list.
11. Export captions with **Export SRT** when done.

Sessions save automatically. Next time you open the app, your session will appear in the recent sessions list.

---

## Source Build

```powershell
git clone https://github.com/mta1124-1629472/Babel-Player.git
cd Babel-Player
dotnet build
dotnet run --project BabelPlayer.csproj
```

Run the full verification suite:

```powershell
dotnet test
python scripts/check-architecture.py
python -m py_compile inference/main.py
```

The architecture linter (`scripts/check-architecture.py`) enforces structural rules: provider string constants, ViewModel pipeline call discipline, coordinator line limits, and `PLACEHOLDER` requirements on unimplemented stubs.

---

## Dependencies

### Runtime (bundled in release)

| Dependency | Purpose |
|---|---|
| [Avalonia 12.0 RC1](https://avaloniaui.net/) | Cross-platform UI framework |
| [libmpv](https://mpv.io/) | Native media playback (GPU-accelerated video) |
| [CommunityToolkit.MVVM 8.2.1](https://github.com/CommunityToolkit/dotnet) | MVVM observables and commands |
| [ffmpeg](https://ffmpeg.org/) | Media ingest and audio extraction |

### Inference (Python, installed on demand)

| Dependency | Purpose |
|---|---|
| [Faster-Whisper](https://github.com/guillaumekervizic/faster-whisper) | Local speech recognition |
| [CTranslate2](https://github.com/OpenNMT/CTranslate2) | Local NLLB translation |
| [Piper](https://github.com/rhasspy/piper) | Local neural TTS |
| [Pyannote.audio](https://github.com/pyannote/pyannote-audio) | Speaker diarization |
| [edge-tts](https://github.com/rany2/edge-tts) | Microsoft Edge cloud TTS |

### Cloud APIs (optional, key required)

- [OpenAI API](https://platform.openai.com/) — Whisper transcription, GPT translation, TTS
- [ElevenLabs](https://elevenlabs.io/) — High-quality voice cloning TTS
- [Google Cloud](https://cloud.google.com/) — Speech-to-Text, Cloud TTS
- [Google Gemini](https://ai.google.dev/) — Transcription and translation via Gemini models
- [DeepL API](https://www.deepl.com/pro-api) — High-quality translation

---

## Current Limitations

- **Windows only.** Linux and macOS are not supported in 1.0. The architecture is designed for future portability, but no cross-platform work has been done yet.
- **No final video export.** The muxed output (video + dubbed audio track, optionally with burned captions) is structurally planned but not exposed in the app yet.
- **Single speaker per TTS generation pass.** Multi-speaker routing (automatic diarization → per-speaker voice assignment) is implemented in the backend but not the primary workflow. Manual speaker diarization via Pyannote is available.
- **Google Translate (free) is unreliable.** It uses a web scraper approach and is rate-limited. Use it only for quick informal tests; prefer DeepL, OpenAI, or Gemini for real work.
- **XTTS containerized path requires Docker and NVIDIA.** If you do not have a compatible setup, use a cloud TTS provider or Piper for local.
- **No real-time or streaming mode.** All stages process the full session before output is available. Segment-level regeneration is available after the initial pass.
- **Session restore downgrades gracefully but does not auto-re-run.** If artifacts are missing on restore, the pipeline resets to the last verified stage — it does not re-run automatically.

---

## Roadmap

### In progress — Runtime Optimization and Hardware Routing (Milestone 12)

- GPU acceleration validation for Faster-Whisper (CUDA path confirmation on real hardware)
- NVDEC / D3D11VA hardware decode path in libmpv
- Live container status visible in Settings
- WSL-hosted Python inference path tested and documented
- Runtime routing diagnostics surfaced in UI when a selected path is degraded
- Hardware snapshot panel accessible from settings

### Planned — Release Hardening (Milestone 13)

- Packaged Windows installer (NUKE publish pipeline)
- Clean-machine validation: full workflow without dev-environment assumptions
- Crash and support log artifacts usable by a non-developer
- Final video export: mux dubbed audio into the original container
- SRT burn-in option on export

### Under consideration (post 1.0)

- macOS and Linux desktop support (Avalonia supports both; requires native lib validation)
- Multi-speaker dubbing workflow surfaced in UI
- Timeline editing: adjust segment timing and duration visually
- Batch processing: transcribe and translate multiple files in sequence
- Additional TTS providers: StyleTTS2, Kokoro, F5-TTS
- Streaming / real-time preview for long-form content

---

## Project Layout

```
Babel-Player/
├── Models/                  # Domain records and enums (session state, segments, providers)
├── Services/                # Workflow coordinator, providers, persistence, transport, host management
├── ViewModels/              # MVVM layer; EmbeddedPlaybackViewModel is the largest (~600 lines)
├── Views/                   # Avalonia XAML UI
├── BabelPlayer.Tests/       # xUnit integration tests (~22 tests across all layers)
├── inference/               # Python inference host (Dockerfile + scripts)
├── scripts/                 # Architecture linter and tooling
├── docs/
│   ├── architecture.md      # Structural boundaries and state ownership map
│   ├── smoke/               # Milestone verification notes (required per milestone)
│   └── containers.md        # Docker/WSL deployment notes
├── benchmarks/              # Stage benchmark run artifacts
├── tools/                   # Bundled tooling (uv, etc.)
├── native/win-x64/          # Bundled libmpv-2.dll
└── test-assets/             # Sample media for tests
```

Key files:

| File | Role |
|---|---|
| [Services/SessionWorkflowCoordinator.cs](Services/SessionWorkflowCoordinator.cs) | Single owner of all workflow and session state |
| [ViewModels/EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs) | Playback, preview, segment selection, dub mode |
| [Models/ProviderNames.cs](Models/ProviderNames.cs) | All provider identifier constants (no string literals elsewhere) |
| [Models/SessionWorkflowStage.cs](Models/SessionWorkflowStage.cs) | Pipeline stage enum |
| [App.axaml.cs](App.axaml.cs) | Startup, composition root, DI wiring |

---

## Contributing

Read these first:

- [AGENTS.md](AGENTS.md) — operating rules and non-negotiables
- [PLAN.md](PLAN.md) — milestone order and gates
- [CONTRIBUTING.md](CONTRIBUTING.md) — contributor workflow and scope discipline
- [docs/architecture.md](docs/architecture.md) — structural map and ownership rules

Minimum verification before opening a PR:

```powershell
dotnet build
dotnet test
python scripts/check-architecture.py
```

The project is in active milestone hardening. Contributions that preserve the working dubbing loop and keep readiness behavior truthful are welcome. Speculative features, silent fallbacks, and scope expansions without explicit justification will be declined.
