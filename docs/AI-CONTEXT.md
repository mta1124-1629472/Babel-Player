# Babel Player — AI Context

> **Purpose:** Single-file project context for any AI assistant working on this codebase.
> Consolidates information from README.md, AGENTS.md, CLAUDE.md, GEMINI.md, CONTRIBUTING.md, and docs/architecture.md.
> Last updated: 2026-04-17.

---

## What Is Babel Player

Babel Player is a high-performance Windows desktop workstation for **segment-based AI video dubbing**. It automates the pipeline from source media to dubbed output with translated, spoken dialogue. Built on **.NET 10** and **Avalonia 12**, it uses local RTX-accelerated inference and optional cloud APIs.

The product is built by a solo developer and follows a "vertical-slice" philosophy — functional end-to-end dubbing over abstract framework complexity.

**Core pipeline:**
```
Load Media → Timed Transcript → Voice Assignment → Translated Dialogue → Voiced Dubbing → In-Context Preview
```

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Desktop UI | C# / .NET 10.0 + Avalonia 12.0 (Fluent theme) |
| Media playback | libmpv (P/Invoke, GPU-accelerated, NVDEC, RTX VSR/HDR) |
| Media processing | ffmpeg (audio extraction, segment mixing, format conversion) |
| Python env management | uv (bundled `uv.exe`) |
| AI inference (local) | Python subprocesses or managed venv (FastAPI server) |
| AI inference (cloud) | HTTP clients for OpenAI, ElevenLabs, DeepL, Google, Gemini |
| MVVM toolkit | CommunityToolkit.MVVM 8.2.1 |
| Testing | xUnit 2.9.3 + coverlet |

---

## Pipeline Stages

Each stage is **gated** — downstream stages only enable when upstream artifacts exist on disk.

| Stage | What It Does | Compute Options | Key Providers |
|-------|-------------|----------------|---------------|
| **Transcribe** | Timed transcript with word-level timestamps | CPU / GPU / Cloud | Faster-Whisper, Gemini, OpenAI Whisper, Google STT |
| **Diarize** | Identify speakers, assign voices | CPU / GPU | WeSpeaker (CPU), NeMo (GPU/container) |
| **Translate** | Adapt transcript to target language | CPU / GPU / Cloud | NLLB-200, CTranslate2, DeepL, Gemini, OpenAI |
| **Dub (TTS)** | Generate spoken audio per segment | CPU / GPU / Cloud | Piper, Qwen3-TTS, Edge TTS, ElevenLabs, OpenAI TTS, Google TTS |
| **Preview** | In-context playback, toggle source/dub audio | — | libmpv embedded transport |

Compute selection is explicit (CPU / GPU / Cloud selector per stage). No silent fallbacks — if a path is unavailable, the stage blocks with a remediation message.

---

## Project Structure

```
Babel-Player/
├── Models/                  # Domain records and enums (session state, segments, providers, compute profiles)
├── Services/                # Workflow coordinator, providers, persistence, transport, host management
│   └── Registries/          # Per-stage provider registries with compute-aware filtering
├── ViewModels/              # MVVM layer with observables and commands
├── Views/                   # Avalonia XAML UI
├── BabelPlayer.Tests/       # xUnit test project
├── inference/               # Python inference server (FastAPI + Faster-Whisper + TTS + diarization)
├── scripts/                 # Architecture linter and dev tooling
├── docs/
│   ├── architecture.md      # Structural map and ownership rules
│   ├── PLAN.md              # Milestone plans (index)
│   ├── history/smoke/       # Milestone completion evidence
│   └── context/             # Per-agent context files (Gemini, Qwen)
├── native/win-x64/          # libmpv-2.dll (fetched via script)
├── installer/               # Inno Setup script
├── AGENTS.md                # Operating rules (read before non-trivial changes)
├── CLAUDE.md                # Claude-oriented project context
├── GEMINI.md                # Gemini-oriented project context
├── CONTRIBUTING.md          # Contributor workflow and scope discipline
└── BabelPlayer.csproj       # net10.0, WinExe, RootNamespace=Babel.Player
```

---

## Key Files

| File | Role |
|------|------|
| `Services/SessionWorkflowCoordinator.cs` | **Single owner of all workflow and session state** — all stage progression runs through here. Split into partials: `.Pipeline.cs`, `.Playback.cs`, `.Settings.cs`, `.Export.cs`, `.Progress.cs`, `.Shutdown.cs`, etc. |
| `ViewModels/EmbeddedPlaybackViewModel.cs` | Largest VM; manages video playback UI, segment selection, dub mode, subtitle toggle |
| `ViewModels/MainWindowViewModel.cs` | Top-level VM; composes pipeline, preview, and settings VMs |
| `Models/WorkflowSessionSnapshot.cs` | Complete session state record persisted to disk |
| `Models/SessionWorkflowStage.cs` | Enum: `Foundation → MediaLoaded → Transcribed → Translated → TtsGenerated` |
| `Models/ProviderNames.cs` | All provider identifier constants (string keys) |
| `Models/ComputeProfile.cs` | CPU / GPU / Cloud enum with hardware-aware selection |
| `Services/InferenceRuntimeCatalog.cs` | Compute profile → provider routing and normalization |
| `Services/MediaTransportManager.cs` | Manages libmpv instances (embedded + headless) |
| `Services/ManagedVenvHostManager.cs` | Bootstraps and manages the local GPU Python venv via uv.exe |
| `Services/ManagedCpuRuntimeManager.cs` | Manages the CPU Python runtime |
| `Services/HardwareSnapshot.cs` | GPU/hardware capability detection |
| `inference/main.py` | Python inference server (transcription, translation, TTS, diarization) |
| `scripts/check-architecture.py` | Architecture linter enforcing structural rules |
| `App.axaml.cs` | Startup / composition root with diagnostics bootstrapping |

---

## Architecture Principles

1. **Single state owner.** `SessionWorkflowCoordinator` owns all workflow/session state. Never scatter state across Views or ViewModels.
2. **Strict MVVM.** Views (XAML) and ViewModels are separated. ViewModels must not call inference services directly — all pipeline advancement goes through the coordinator.
3. **Truthful readiness.** Never implement silent fallbacks or pretend-complete UI. If a path is unimplemented, use explicit placeholders or disabled states.
4. **Vertical slices over abstractions.** No provider matrices, factory systems, plugin architectures, or runtime selection systems until milestones earn them.
5. **Persistent artifacts.** Generated outputs (transcripts, translations, TTS audio) are first-class and cached per-session. Users should not recompute everything on reopen.
6. **Narrow service seams.** AI/inference services sit behind explicit contracts. The desktop app and inference runtime are separated by process/HTTP boundaries.

---

## Serialization Contracts (Python ↔ C#)

**Critical:** Field names crossing the Python/C# boundary are explicit serialization contracts.

- Python emits snake_case or camelCase field names (e.g., `translatedText`, `sourceLanguage`, `segments`)
- C# reads via `GetProperty("translatedText")` or typed DTOs with `[JsonPropertyName]`
- Segment IDs: `segment_{start}` format (e.g., `segment_0.0`, `segment_3.68`) — must match Python output exactly
- Changes to cross-language JSON field names must be updated on **both sides** simultaneously

---

## Artifact Storage

| What | Where |
|------|-------|
| Session state | `%LOCALAPPDATA%/BabelPlayer/state/current-session.json` |
| Session artifacts | `%LOCALAPPDATA%/BabelPlayer/sessions/{SessionId}/` |
| Logs | `%LOCALAPPDATA%/BabelPlayer/logs/babel-player.log` |
| Python runtimes | `%LOCALAPPDATA%/BabelPlayer/runtime/` |
| Native tools | `tools/<rid>/` (ffmpeg.exe, ffprobe.exe) |

On restore, the coordinator validates artifacts exist and **downgrades stage** if files are missing.

---

## Build & Test Commands

```powershell
# Fetch native binaries (required after clone)
pwsh ./scripts/fetch-win-native-deps.ps1

# Build
dotnet build Babel-Player.sln

# Run
dotnet run --project BabelPlayer.csproj

# Test
dotnet test Babel-Player.sln

# Architecture linter (run before every PR)
python scripts/check-architecture.py

# Verify Python syntax
python -m py_compile inference/main.py
```

---

## Naming Conventions

| Context | Form |
|---------|------|
| Product / branding | `Babel Player` (space, no dash) |
| Repository | `Babel-Player` |
| .NET namespaces / assembly IDs | `BabelPlayer` or `Babel.Player` |
| Dev builds | Append `[DEV]` |
| Filenames / folders | Match local convention already in use |

---

## Language Support

- **16 curated dub target languages:** ar, de, en, es, fr, hi, it, ja, ko, nl, pl, pt, ru, sv, tr, zh
- **Transcription source:** Auto-detect (full Whisper breadth) or hint from the 16-language list
- **Piper TTS:** 14 of 16 targets; ja and ko use Edge TTS or Qwen instead
- Cloud providers may extend language reach beyond the local 16

Use canonical lowercase ISO 639-1 codes in persisted settings and pipeline artifacts. Human-readable labels only in UI catalogs.

---

## Current Milestone Status

Milestones 1–9 are complete. Milestones 10–11 are partially complete (settings/bootstrap and local-offline expansion). Milestone 12 (Runtime Optimization and Hardware Routing) is in progress.

See `docs/PLAN.md` for milestone gates and `docs/history/smoke/` for completion evidence.

---

## Non-Negotiable Rules (from AGENTS.md)

- Prefer iterative runtime debugging: reproduce issue, then continue from fresh logs
- Prefer canonical language codes in persisted settings (lowercase ISO 639-1)
- Custom window chrome matching Windows 11 expectations
- Piper and Edge TTS voice assignment through the Speaker Reference Wizard
- For commit/push with explicit file lists, treat that list as authoritative
- Concise, plain UI wording; avoid em dashes in user-visible copy
- Hardware-informed defaults over fixed ultra-conservative baselines
- Avoid UI verbosity: prefer concise status messages ("Ready") over internal technical explanations
- Multi-speaker detection is WeSpeaker-only with diarization off by default
- NeMo background health is off unless `BABEL_ENABLE_NEMO_BACKGROUND_HEALTH` is set
- Transcript JSON is named from the ingested source media stem, not from post-separation stems
- `VocalSeparationEnabled` is coerced off when the container reports audio separator not ready

---

## Scope Discipline

- Work one milestone at a time. Do not start downstream features early.
- No speculative extension points for future providers, runtimes, or workflows.
- Refactors only when they unblock the current milestone, reduce real complexity, or remove proven instability.
- No fake buttons, silent fallbacks, or "coming soon" behavior disguised as completed implementation.
- A narrower real feature is preferred over a broader partial one.

---

## Troubleshooting

- **Workload resolver error:** `Directory.Build.props` sets `MSBuildEnableWorkloadResolver=false` — no action needed.
- **Locked file error:** `taskkill /F /IM clrdbg.exe /IM dotnet.exe`
- **Standard diagnostic script:** build → test → architecture linter → Python compile check (see Build & Test Commands above).
