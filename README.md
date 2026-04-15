# Babel Player

[![Sponsor](https://img.shields.io/github/sponsors/mta-babel?label=Sponsor&logo=GitHub)](https://github.com/sponsors/mta-babel)
[![CI](https://github.com/Babelworks/Babel-Player/actions/workflows/ci.yml/badge.svg)](https://github.com/Babelworks/Babel-Player/actions/workflows/ci.yml)
[![GitHub Release](https://img.shields.io/github/v/release/Babelworks/Babel-Player)](https://github.com/Babelworks/Babel-Player/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/Babelworks/Babel-Player)](LICENSE)


**Babel Player is a A high-performance .NET 10 and Avalonia 12 workstation for segment-based AI video dubbing. Local RTX-accelerated transcription, translation, and voice cloning with cloud API support. Built natively for Windows x64 and ARM.**

```
The Pipeline: Load Media → Timed Transcript → Voice Assignment → Translated Dialogue → Voiced Dubbing → In-Context Preview
```

![Babel Player preview](Assets/preview.png)

Babel Player is built and maintained by a solo developer.
If you find it useful, consider sponsoring:

[![ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/babel_player)


---

## Table of Contents

- [What It Does](#what-it-does)
- [Features](#features)
- [Provider Support](#provider-support)
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

Babel Player is a dubbing workstation, not a subtitle editor or a translation tool in isolation. The goal is to get a piece of foreign-language source media to a point where you can hear the translated dialogue spoken back, then refine it until it sounds right.


The full loop:

1. **Load** a local video or audio file
2. **Transcribe** — generate a timed transcript using local AI or a cloud API
3. **Diarize & Assign**: Identify unique speakers and assign specific voices to individual segments.
4. **Translate** — adapt the transcript into a target language
5. **Dub** — generate a spoken TTS audio track, one segment at a time
6. **Multi-speaker routing** (optional) — assign different voices to different speakers via diarization
7. **Preview** — play source video alongside dubbed segments; toggle between original and dub audio
8. **Refine** — regenerate individual segments, adjust text, re-run TTS on demand
9. **Export** — save captions as `.srt`
10. **Persist** — sessions save automatically; reopen and continue later

---

## Features

### Pipeline

- Segment-based workflow: each transcript line is an independent unit that can be individually translated, re-dubbed, or replaced
- Full pipeline runs in order: transcription → translation → TTS generation
- Individual segments can be regenerated at any stage without re-running everything
- Stage gating: downstream stages only enable when upstream results are present and artifacts are on disk
- Word-level timestamps from transcription enable fine-grained segment editing

### Compute Selection

Each inference stage exposes a CPU / GPU / Cloud selector with no hidden routing. If the selected compute path is unavailable, the stage blocks with a clear remediation message. There is no silent fallback.

- **CPU** — local Python subprocess; works on any Windows machine; no GPU required
- **GPU** — routes through a managed local Python venv host (default); NVIDIA GPU with CUDA required. 
- **Cloud** — calls a remote API; requires the corresponding API key in Settings

The GPU path bootstraps a managed local venv automatically using the bundled `uv.exe`. No manual Python installation is required.

### Playback and Preview

- Embedded video playback powered by **libmpv** with GPU-accelerated rendering
- Source scrubbing and segment-aware navigation
- Toggle between source audio and dubbed segment audio in real time
- Subtitle overlay with bilingual display (source and translated text)
- Auto-hiding controls bar and fullscreen mode
- Hover-reveal volume slider
- Refined transport controls with unified pill styling and visual grouping

### Multi-Speaker Routing

- Automatic speaker diarization via Pyannote (requires HuggingFace token)
- Per-speaker voice assignment — assign different TTS voices to different speakers
- Voice cloning for compatible providers (XTTS v2, Qwen3-TTS) — reference audio uploaded per speaker
- Speaker boundary detection — automatically splits segments at detected speaker changes
- Fallback voice configuration — intelligently routes unassigned speakers

### Session Management

- Sessions auto-save to `%LOCALAPPDATA%\BabelPlayer\state\`
- Recent sessions list with one-click restore
- Artifacts (transcripts, translations, TTS audio) are cached per-session under `%LOCALAPPDATA%\BabelPlayer\sessions\{SessionId}\`
- Artifact validation on restore — gracefully downgrades to last verified stage if files are missing
- Each inference stage exposes a CPU / GPU / Cloud selector with no hidden routing
- If the selected compute path is unavailable, the stage blocks with a clear remediation message

### Settings and Credentials

- Per-stage provider, model, and voice selection persisted across launches
- In-app API key manager with live validation
- Bootstrap diagnostics surface missing dependencies and configuration gaps at startup
- Hardware-aware compute type policy (selects `float16` / `int8` for older GPUs, `float8` for Blackwell)
- Container health status visible in Settings UI 

### Export

- SRT caption export — prefers translated text, falls back to source text
- Automatic speaker labels in exported captions (when diarization is enabled)

---

## Provider Support

### Transcription

| Provider | Runtime | Notes |
|---|---|---|
| [Faster-Whisper](https://github.com/SYSTRAN/faster-whisper) | GPU | Models: `tiny`, `base`, `small`, `medium`, `large-v3`; word-level timestamps |
| [Faster-Whisper](https://github.com/SYSTRAN/faster-whisper) | CPU | Same models as GPU; slower inference |
| [Google Gemini](https://ai.google.dev/) | Cloud | `gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`; requires API key |
| [Google Speech-to-Text](https://cloud.google.com/speech-to-text/docs) | Cloud | Requires API key |
| [OpenAI Whisper API](https://platform.openai.com/docs/guides/speech-to-text) | Cloud | Requires API key |

### Translation

| Provider | Runtime | Notes |
|---|---|---|
| [NLLB-200](https://huggingface.co/facebook/nllb-200-distilled-600M) | GPU | Models: `distilled-1.3B`, `1.3B`; 200+ languages |
| [CTranslate2](https://github.com/OpenNMT/CTranslate2) | CPU | int8-quantized; model: `distilled-600M`; fast |
| [DeepL](https://www.deepl.com/docs-api) | Cloud | Higher quality for European languages; requires API key |
| [Google Gemini](https://ai.google.dev/) | Cloud | `gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`; requires API key |
| [OpenAI](https://platform.openai.com/docs/guides/text-generation) | Cloud | Requires API key |

### Text to Speech

| Provider | Runtime | Notes |
|---|---|---|
| [Qwen3-TTS](https://huggingface.co/Qwen/Qwen3-TTS) | GPU | Voice cloning; auto-extracts reference audio from source video |
| [Piper](https://github.com/rhasspy/piper) | CPU | Fully offline; fast; lower voice quality |
| [Edge TTS](https://github.com/rany2/edge-tts) | Cloud | **No API key required**; Microsoft Azure voices |
| [ElevenLabs](https://elevenlabs.io/docs) | Cloud | Highest voice quality; requires API key |
| [Google Cloud TTS](https://cloud.google.com/text-to-speech/docs) | Cloud | Requires API key |
| [OpenAI TTS](https://platform.openai.com/docs/guides/text-to-speech) | Cloud | Requires API key |

### Diarization (Speaker Detection)

| Provider | Runtime |
|---|---|
| [NeMo](https://github.com/NVIDIA/NeMo) | GPU |
| [WeSpeaker](https://github.com/wenet-e2e/wespeaker) | CPU |

---
## Requirements

| Scenario | Requirements |
| :--- | :--- |
| **OS Architecture** | Windows 10 or 11 (**x64** and **ARM64** natively supported) |
| **GPU Acceleration** | NVIDIA GPU with CUDA support (RTX 30-series or newer recommended) |
| **VRAM** | 8GB+ for high-quality local cloning; 6GB minimum for base pipelines |

---
## First-run setup

Babel Player bundles `uv.exe` and manages all Python runtimes automatically — no manual Python installation required.

| Path | One-time download | Triggered by |
|------|-------------------|--------------|
| GPU inference | ~5 GB (torch+CUDA, faster-whisper, TTS models) | First GPU transcription/TTS use |
| CPU inference | ~800 MB (torch CPU, faster-whisper) | First CPU transcription use |
| Diarization | ~500 MB (NeMo or WeSpeaker) | First speaker detection use |

Downloads are cached in `%LOCALAPPDATA%\BabelPlayer\runtime\`. The CPU runtime bootstraps automatically in the background on first launch; progress is shown live in the status bar during install.

---

## Installation

### Portable

1. Download `Babel-Player-<version>-win-x64-portable.zip` from [GitHub Releases](https://github.com/Babelworks/Babel-Player/releases/latest).
2. Extract to a folder of your choice, e.g. `C:\Apps\BabelPlayer`.
3. Run `BabelPlayer.exe`.

The release bundle is self-contained and includes:

- `BabelPlayer.exe` and all .NET dependencies (runtime included — no separate .NET install required)
- `ffmpeg.exe`
- `libmpv-2.dll`
- `uv.exe` for managed Python venv bootstrapping
- Inference host assets under `inference/`

No registry entries are created. To uninstall: delete the folder and optionally clear `%LOCALAPPDATA%\BabelPlayer\`.

### Installer

An Inno Setup installer (`Babel-Player-<version>-win-x64-setup.exe`) is also available on the releases page. It installs to `%LOCALAPPDATA%\Programs\BabelPlayer` (no admin required), adds a Start Menu entry, and registers a clean uninstaller.

---

## First Run

1. Launch `BabelPlayer.exe`.
2. Click **Open** and load a local video or audio file.
3. In the pipeline pane, pick `CPU`, `GPU`, or `Cloud` for each stage.
4. Select a provider and model or voice for that stage.
5. If using cloud providers, go to **Settings → API Keys** and enter your credentials.
6. If using diarization (multi-speaker), toggle "Enable multi-speaker routing" and set your HuggingFace token.
7. Click **Run Pipeline**. The pipeline will:
   - Transcribe the source audio
   - Translate the transcript
   - Generate TTS audio for each segment
   - (If diarization enabled) detect speaker changes and assign voices per speaker
8. Review the timed segments in the right panel.
9. Use the playback controls to preview. Toggle **Dub mode** to hear the dubbed audio.
10. Regenerate individual segments as needed from the segment list.
11. Export captions with **Export** when done.

Sessions save automatically. Your session will appear in the recent sessions list next time you open the app.

---

## Source Build

1. **Clone the repository**
   ```bash
   git clone [https://github.com/Babelworks/Babel-Player.git](https://github.com/Babelworks/Babel-Player.git)
   cd Babel-Player
2. Fetch Native Binaries (Required for libmpv-2.dll and uv.exe)
   Note: These binaries are excluded from Git to keep the repository lean.
   pwsh ./scripts/fetch-win-native-deps.ps1

3. Build and Run   
   dotnet build Babel-Player.sln
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
| [**Avalonia 12.0.1**](https://avaloniaui.net/) | Desktop UI framework with Fluent theming |
| [**libmpv**](https://mpv.io/) | Native media playback (GPU-accelerated video rendering, NVDEC support) |
| [**ffmpeg**](https://ffmpeg.org/) | Media ingest, audio extraction, segment mixing, and format conversion |
| [**uv**](https://github.com/astral-sh/uv) | Python environment and package management |

### Python inference host (installed on first use via `uv`)

| Package | Purpose |
|---|---|
| `faster-whisper` | Local speech recognition with word-level timestamps |
| `ctranslate2` / `sentencepiece` | Local translation (CTranslate2 models) |
| `nllb-200` | NLLB-200 translation models (200+ languages) |
| `piper-tts` | Local neural TTS (CPU-based) |
| `qwen-tts` | Qwen3-TTS with voice cloning support |
| `tts` (Coqui) | XTTS v2 voice synthesis with cloning |
| `pyannote.audio` | Speaker diarization (speaker detection and segmentation) |
| `torch` / `transformers` / `accelerate` | ML runtime with CUDA/CPU backends |
| `fastapi` / `uvicorn` | Inference HTTP server (containerized backend) |
| `soundfile` / `numpy` | Audio I/O and processing |


### Cloud APIs (optional, key required)

- [OpenAI](https://platform.openai.com/) — Whisper transcription, GPT translation, TTS
- [ElevenLabs](https://elevenlabs.io/) — High-quality TTS (no voice cloning yet; future work)
- [Google Cloud](https://cloud.google.com/) — Speech-to-Text, Cloud TTS
- [Google Gemini](https://ai.google.dev/) — Transcription and translation
- [DeepL](https://www.deepl.com/pro-api) — Translation

---

## Current Limitations

- **Windows only.** Linux and macOS are not supported. The architecture is designed for future portability but no cross-platform work has been done yet.
- **No video export.** SRT caption export works. The muxed video output (dubbed audio mixed into the source container) is planned for a future release.
- **GPU TTS validation in progress.** Qwen3-TTS are fully wired end-to-end; NVIDIA RTX path validated on real hardware. Blackwell (`float8`) dtype is wired but validation pending on real Blackwell hardware.
- **No real-time or streaming.** All stages process the full session; segment-level regeneration is available after the initial pass.

---

## Roadmap

### In progress (Milestone 12)

- **Container health status** — live container status visible in Settings UI

### Planned (Milestone 13 and beyond)

- **Video export UI** — mux dubbed audio into the source container
- **Clean-machine validation** — full workflow without dev-environment assumptions
- **Improved crash/support logs** — artifacts usable by non-developers
- **Multi-file batch processing** — queue multiple media files in sequence
- **Additional TTS providers** — StyleTTS2, Kokoro, F5-TTS
- **Timeline editing** — adjust segment timing visually
- **Streaming support** — real-time preview for long-form content

### Under consideration

- macOS and Linux support
- Collaborative workflow integration

---

## Project Layout

```
Babel-Player/
├── Models/                  # Domain records and enums (session state, segments, providers, compute profiles)
├── Services/                # Workflow coordinator, providers, persistence, transport, host management
│   └── Registries/          # Per-stage provider registries with compute-aware filtering
├── ViewModels/              # MVVM layer with observables and commands
├── Views/                   # Avalonia XAML UI with refined styling
├── BabelPlayer.Tests/       # xUnit integration tests (~650 tests)
├── inference/               # Python inference server (FastAPI + Faster-Whisper + TTS + diarization)
├── scripts/                 # Architecture linter and development tooling
├── docs/
│   ├── architecture.md      # Structural map and ownership rules
│   ├── PLAN.md              # Milestone plans (index)
│   ├── context/             # Extra agent context (Gemini, Qwen, …)
│   └── history/
│       ├── smoke/           # Milestone smoke / gate evidence
│       └── benchmarks/    # Transcription benchmark runs + leaderboard
├── native/win-x64/          # libmpv-2.dll (fetched; see README setup)
├── installer/               # Inno Setup script
├── AGENTS.md                # Operating rules (read before non-trivial changes)
├── CLAUDE.md                # Claude / Cursor-oriented project context
├── CONTRIBUTING.md
├── README.md
├── LICENSE
└── BabelPlayer.csproj
```

Key files:

| File | Role |
|---|---|
| [Services/SessionWorkflowCoordinator.cs](Services/SessionWorkflowCoordinator.cs) | Single owner of all workflow and session state |
| [ViewModels/EmbeddedPlaybackViewModel.cs](ViewModels/EmbeddedPlaybackViewModel.cs) | Playback, preview, segment selection, dub mode, multi-speaker routing |
| [Models/ProviderNames.cs](Models/ProviderNames.cs) | All provider identifier constants |
| [Models/ComputeProfile.cs](Models/ComputeProfile.cs) | CPU / GPU / Cloud enum with hardware-aware selection |
| [Services/InferenceRuntimeCatalog.cs](Services/InferenceRuntimeCatalog.cs) | Compute profile → provider routing and normalization |
| [inference/main.py](inference/main.py) | Python inference server (transcription, translation, TTS, diarization) |
| [App.axaml.cs](App.axaml.cs) | Startup / composition root with diagnostics bootstrapping |

---

## Contributing

Read these first:

- [AGENTS.md](AGENTS.md) — operating rules and non-negotiables
- [CLAUDE.md](CLAUDE.md) — context and instructions for Claude
- [docs/context/GEMINI.md](docs/context/GEMINI.md) — context for Gemini-oriented assistants
- [docs/context/QWEN.md](docs/context/QWEN.md) — context for Qwen Coder–style setups
- [docs/PLAN.md](docs/PLAN.md) — milestone order and gates
- [CONTRIBUTING.md](CONTRIBUTING.md) — contributor workflow and scope discipline
- [docs/architecture.md](docs/architecture.md) — structural map and ownership rules
- [docs/privacy-policy.md](docs/privacy-policy.md) — privacy policy (published copy also on GitHub Pages)
- **Marketing site (GitHub Pages)** — Jekyll source on branch [`site`](https://github.com/Babelworks/Babel-Player/tree/site); push to that branch to deploy (workflow lives in `.github/workflows/` on `main`).

Minimum verification before opening a PR:

```powershell
dotnet build babel-player.sln
dotnet test babel-player.sln
python scripts/check-architecture.py
```

The project is in active milestone hardening. Contributions that preserve the working dubbing loop and keep readiness behavior truthful are welcome. Speculative features, silent fallbacks, and scope expansions outside the current milestone will be declined.
