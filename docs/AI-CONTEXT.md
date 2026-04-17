# Babel Player — AI Context

> Single source of truth for project context shared across all AI assistants and contributors.
> Agent-specific files (CLAUDE.md, GEMINI.md, etc.) contain only tool-specific instructions and point here.
> Last updated: 2026-04-17

## Project Overview

**Babel Player** is a Windows desktop dubbing workstation built with C# / .NET 10 and Avalonia 12. It transforms source media through a pipeline:

```text
source media → timed transcript → translated dialogue → spoken dubbed output → in-context preview and refinement
```

Users load a video or audio file, generate timed transcripts (local AI or cloud API), translate the dialogue into a target language, produce spoken TTS audio per segment, and preview the dubbed result alongside the source video — then refine individual segments on demand.

**Key characteristics:**
- Windows-only desktop app (WinExe); no Linux/macOS support yet
- Avalonia 12 with Fluent theme, libmpv for embedded video playback
- Python inference subprocess (managed via bundled `uv.exe` — no manual Python install needed)
- GPU (CUDA) and CPU compute paths with explicit CPU / GPU / Cloud selectors per stage
- Session-based workflow with auto-save/restore from `%LOCALAPPDATA%\BabelPlayer\state\`
- Comprehensive xUnit integration test suite

## Tech Stack

| Layer | Technology |
|---|---|
| **UI framework** | Avalonia 12.0 (Fluent theme, Inter font) |
| **MVVM** | CommunityToolkit.Mvvm 8.4.2 |
| **Runtime** | .NET 10.0, C# 12+, nullable enabled |
| **Native media** | libmpv-2.dll (P/Invoke, GPU-accelerated video) |
| **Media processing** | ffmpeg.exe (bundled) |
| **Python management** | uv.exe (bundled, auto-bootstraps venv) |
| **Testing** | xUnit, coverlet |
| **Architecture linter** | `scripts/check-architecture.py` |

## Core Pipeline Stages

1. **Ingest** — load local media; extract or persist reusable media artifacts
2. **Transcribe** — generate a timed transcript (local AI or cloud API)
3. **Diarize** (optional) — identify unique speakers and split transcript segments at speaker boundaries
4. **Translate** — adapt the transcript into a target language
5. **TTS** — generate spoken dubbed audio per segment
6. **Preview / Refine** — play source video alongside dubbed segments; regenerate individual segments on demand
7. **Export / Persist** — SRT caption export; session auto-save for later resume

### Provider Support Summary

| Stage | Local (CPU) | Local (GPU) | Cloud |
|---|---|---|---|
| **Transcription** | Faster-Whisper | Faster-Whisper | OpenAI Whisper, Google STT, Gemini |
| **Translation** | CTranslate2, NLLB-200 | CTranslate2, NLLB-200 | DeepL, OpenAI, Gemini, Google Translate |
| **TTS** | Piper | Qwen3-TTS, XTTS v2 | Edge TTS, ElevenLabs, Google Cloud, OpenAI |
| **Diarization** | Manual, WeSpeakerLocal (`WeSpeakerDiarizationAlias`) | Manual, NemoLocal (`NemoDiarizationAlias`), WeSpeakerLocal | — |

## Compute Selection Model

Each inference stage exposes a CPU / GPU / Cloud selector with **no hidden routing**. If the selected compute path is unavailable, the stage blocks with a clear remediation message. **There is no silent fallback.**

- **CPU** — local Python subprocess; works on any Windows machine; no GPU required
- **GPU** — routes through a managed local Python venv host (default); NVIDIA GPU with CUDA required
- **Cloud** — calls a remote API; requires the corresponding API key in Settings

The GPU path bootstraps a managed local venv automatically using the bundled `uv.exe`. No manual Python installation is required.

## Directory Structure

```text
Babel-Player/
├── Models/                    # Domain records, enums, compute profiles
│   └── Artifacts/             # Session artifact types
├── Services/                  # All services and providers
│   ├── Registries/            # Per-stage provider registries
│   ├── Settings/              # App settings, API key store, bootstrap
│   └── Credentials/           # Credential management
├── ViewModels/                # MVVM layer (observables, commands)
├── Views/                     # Avalonia XAML UI
├── BabelPlayer.Tests/         # xUnit integration tests
├── inference/                 # Python inference server (FastAPI)
├── scripts/                   # Architecture linter, dev tooling
├── docs/                      # architecture.md; AI-CONTEXT.md; history/smoke; history/benchmarks
├── native/win-x64/            # libmpv-2.dll
├── tools/win-x64/             # ffmpeg.exe, ffprobe.exe, uv.exe
├── installer/                 # Inno Setup installer scripts
├── Program.cs                 # Entry point (+ --benchmark CLI path)
├── App.axaml(.cs)             # Composition root, startup
└── BabelPlayer.csproj         # net10.0, WinExe, Avalonia packages
```

## Key Files

| File | Purpose |
|---|---|
| `Services/SessionWorkflowCoordinator.cs` | **Single owner** of all workflow/session state; all pipeline advancement through coordinator entry points (`AdvancePipelineAsync`, `ContinuePipelineAsync`, `RunTtsOnlyAsync`) |
| `ViewModels/EmbeddedPlaybackViewModel.cs` | Playback, preview, segment selection, dub mode, multi-speaker routing |
| `Models/WorkflowSessionSnapshot.cs` | Complete session state record persisted to disk |
| `Models/SessionWorkflowStage.cs` | Enum: `Foundation → MediaLoaded → Transcribed → Translated → TtsGenerated` |
| `Models/PlaybackState.cs` | Enum: `Idle`, `PlayingSingleSegment`, `PlayingSequence` |
| `Models/WorkflowSegmentState.cs` | Record: segment ID, timing, source/translated text, TTS status |
| `Models/ProviderNames.cs` | All provider identifier constants (`ProviderNames.*`, `CredentialKeys.*`) |
| `Models/ComputeProfile.cs` | CPU / GPU / Cloud enum |
| `Services/IMediaTransport.cs` | Abstraction for load/play/pause/seek + subtitle + events |
| `Services/InferenceRuntimeCatalog.cs` | Compute profile → provider routing and normalization |
| `Services/MediaTransportManager.cs` | Owns `LibMpvHeadlessTransport` and `LibMpvEmbeddedTransport` lifecycle |
| `inference/main.py` | Python inference HTTP server (transcription, translation, TTS, diarization) |
| `scripts/check-architecture.py` | Architecture linter (enforces structural rules) |
| `docs/PLAN.md` | Milestone gates — current milestone is the only allowed scope |
| `docs/history/smoke/` | Required gate evidence for each milestone |
| `AGENTS.md` | Non-negotiable operating rules |

## Services Reference

| Service | Responsibility |
|---------|----------------|
| `SessionWorkflowCoordinator` | State owner; orchestrates transcription, translation, TTS; manages multi-file caching; segment playback via two `IMediaTransport` instances |
| `SessionSnapshotStore` | JSON persistence to `%LOCALAPPDATA%\BabelPlayer\state\`; corruption recovery (moves bad JSON to `.corrupt`) |
| `AppLog` | Thread-safe file logging (`%LOCALAPPDATA%\BabelPlayer\logs\babel-player.log`) |
| `LibMpvHeadlessTransport` | Headless libmpv (`vo=null`, `ao=null`); used for TTS segment audio playback |
| `LibMpvEmbeddedTransport` | GPU-accelerated libmpv (`vo=gpu`, `wid`); renders video to native HWND |
| `TranscriptionService` | Subprocess → Faster-Whisper; auto-detects source language; returns timed segments |
| `TranslationService` | Subprocess → NLLB/CTranslate2; supports full-transcript and single-segment regeneration |
| `TtsService` | Subprocess → edge-tts / Piper / GPU providers; generates audio per segment; supports single-segment regeneration |
| `SrtGenerator` | Static utility; converts segment list to SRT; prefers translated text, falls back to source |

## Build & Test Commands

### Build

```powershell
dotnet clean Babel-Player.sln
dotnet build Babel-Player.sln                # Full build (includes restore)
dotnet build Babel-Player.sln --no-restore   # Fast build (skip restore)
dotnet run -c Dev                            # Dev build (no optimizations, full debug)
dotnet run --project BabelPlayer.csproj      # Launch the app
```

### Test

```powershell
dotnet test Babel-Player.sln                                                            # All tests
dotnet test Babel-Player.sln --filter "ClassName~SessionWorkflowCoordinatorUnitTests"   # Single test class
dotnet test Babel-Player.sln --filter "ClassName~MethodName"                            # Single test method
dotnet test Babel-Player.sln -v n                                                       # Verbose output
```

**Test categories:** `Integration`, `RequiresPython`, `RequiresFfmpeg`, `RequiresExternalTranslation`.

### Lint / Verify

```powershell
python3 scripts/check-architecture.py    # Architecture linter (required after structural changes)
python -m py_compile inference/main.py   # Verify Python inference code
```

### Full verification sequence

```powershell
dotnet build Babel-Player.sln
dotnet test Babel-Player.sln
python3 scripts/check-architecture.py
python -m py_compile inference/main.py
```

## Architecture Principles

Babel Player is built as a sequence of vertical slices around the core product chain. The architecture should remain subordinate to that chain — not the other way around.

### State Ownership
- The **shell displays** state; the **coordinator owns** workflow state; **services produce** results; **storage preserves** artifacts and session data.
- `SessionWorkflowCoordinator` is the **sole owner** of session and workflow state. Never scatter state across Views or ViewModels.
- Prefer a small number of explicit state owners over a proliferation of convenience caches and duplicated view-local models.

### MVVM
- Strict separation between `Views` (XAML) and `ViewModels` (CommunityToolkit.Mvvm).
- ViewModels must **not** call inference services directly (`TranscribeMediaAsync`, `TranslateTranscriptAsync`, `GenerateTtsAsync`). Pipeline actions route through `SessionWorkflowCoordinator` entry points: `AdvancePipelineAsync` for normal progression, `ContinuePipelineAsync` to resume after `Diarized`, and `RunTtsOnlyAsync` when already `Translated`.

### Truthful Readiness
- No fake buttons, silent fallbacks, or UI that looks functional but is not.
- If a path is unimplemented, use explicit placeholders or disabled states.
- Surface missing dependencies, missing keys, and hardware gaps via readiness checks.
- Hardware-aware routing uses `HardwareSnapshot`; do not hardcode GPU assumptions.

### Scope Discipline
- Work one milestone at a time — no downstream scope starts until the current milestone is verified (see `docs/PLAN.md`).
- No speculative extension points for future providers, runtimes, or workflows.
- A narrower real feature is preferred over a broader partial one.

### Vertical Slices
- Each stage of the pipeline should be proven end-to-end before adjacent stages expand.
- Refactors are justified when they **unblock** the current milestone, **reduce real complexity** in code being actively changed, or **remove a proven source of instability** — not for aesthetic purity or future-proofing alone.

## Naming Conventions

| Context | Form |
|---------|------|
| Product / branding | `Babel Player` |
| Repository | `Babel-Player` |
| .NET namespaces / assembly IDs | `BabelPlayer` or `Babel.Player` |
| Filenames / folders | Match the local convention already in use |
| **UI verbosity** | **Avoid over-explaining.** Prefer concise status (e.g., "Ready") over internal technical details. |

## Code Style

| Convention | Rule |
|---|---|
| **Formatting** | K&R braces, 4-space indent, no trailing whitespace |
| **Naming** | PascalCase for classes/methods/properties; `_camelCase` for private fields; `I` prefix for interfaces |
| **Types** | Prefer `record` for immutable DTOs; use `required` for required properties |
| **Imports** | No unused imports; group: System, third-party, project |
| **Errors** | Throw specific exceptions; `PipelineProviderException` for provider failures with context |
| **Constants** | PascalCase (`ProviderNames.FasterWhisper`) |

## Python/C# Serialization Contract

**Critical:** Field names crossing the Python/C# boundary are explicit serialization contracts. Do **not** rely on implicit .NET casing.

- Python emits snake_case/camelCase field names.
- C# deserializes with `PropertyNameCaseInsensitive = true` **or** explicit `[JsonPropertyName]`.
- Segment IDs are derived from transcript start time: `segment_{start}` (e.g., `segment_0.0`, `segment_3.68`) — must match Python output exactly, as they key the TTS segment dictionary.
- When changing field names in Python scripts or C# result records, **update both sides deliberately** in the same change.
- Python writes fields like `translatedText`, `sourceLanguage`, `segments`; C# reads via `GetProperty("translatedText")` or typed DTOs with matching names.

## Artifact Storage

- Session state: `%LOCALAPPDATA%\BabelPlayer\state\current-session.json`
- Session artifacts: `%LOCALAPPDATA%\BabelPlayer\sessions\{SessionId}\`
  - Transcripts, translations, TTS audio in session-specific subdirectories
- On restore, the coordinator validates that artifacts exist and **downgrades stage** if any are missing.
- Corruption recovery: bad JSON is moved to `.corrupt` and the session is reinitialized.
- Logs: `%LOCALAPPDATA%\BabelPlayer\logs\babel-player.log`.
- Runtime cache (managed Python venvs, models): `%LOCALAPPDATA%\BabelPlayer\runtime\`.

## Language Support

**Dub targets** are a **curated set of 16** local output languages — each one is wired end-to-end (translation + UI catalogs + offline Piper voices where available) so the pipeline stays predictable and testable.

Local translation targets (NLLB + CTranslate2):
Arabic (`ar`), German (`de`), English (`en`), Spanish (`es`), French (`fr`), Hindi (`hi`), Italian (`it`), Japanese (`ja`), Korean (`ko`), Dutch (`nl`), Polish (`pl`), Portuguese (`pt`), Russian (`ru`), Swedish (`sv`), Turkish (`tr`), Chinese — Simplified (`zh`).

**Transcription** uses Faster-Whisper. The default is **Auto-detect**, which leverages Whisper across many spoken languages. The spoken-language hint menu (Auto-detect plus 16 ISO codes) is a curated shortcut list, not a cap on recognition.

**Piper TTS** (offline, as of April 2026) ships **14** of those **16** targets in the in-app voice catalog; **Japanese** and **Korean** use Edge TTS, Qwen, or another provider until Piper publishes matching voices. Cloud translation APIs may extend destinations beyond the embedded batch.

## Vocal Separation

- **Capability vs persisted flag:** `VocalSeparationEnabled` is stored in app settings. The pipeline only runs separation when the inference host reports vocal separation **ready**; otherwise the run fails with an explicit error.
- The UI disables the toggle when the host is not ready; the coordinator and settings UI also **coerce the flag off** once the container probe has a definitive "not ready" snapshot, so hand-edited `app-settings.json` or stale state does not stay "enabled" when it cannot work.
- **Transcript naming:** Transcript JSON under `transcripts/` is named from the **ingested media file stem** (the file the user loaded), not from post-separation stem paths such as `vocals.wav`, so exported artifacts stay aligned with the source video/audio name.

## Testing

- Integration tests in `BabelPlayer.Tests/` (xUnit, coverlet).
- Shared fixture via `SessionWorkflowTemplateFixture` (temp dirs, reusable templates).
- xUnit collection `"Media transport"` runs non-parallel (hardware resource).
- Test assets: `test-assets/video/sample.mp4` (small Spanish TTS video).
- Categories: `Integration`, `RequiresPython`, `RequiresFfmpeg`, `RequiresExternalTranslation`.

## Architecture Linter Rules

`scripts/check-architecture.py` enforces structural discipline. Run it after any structural change:

1. `BabelPlayer.csproj` exists with `OutputType=WinExe`.
2. Test project references the main project.
3. `NotImplementedException` must include a `PLACEHOLDER` message.
4. Silent event stubs have `PLACEHOLDER` comments.
5. No magic provider strings outside `Models/ProviderNames.cs`.
6. ViewModels do not call pipeline methods directly.
7. `SessionWorkflowCoordinator.cs` must be under 1300 lines.
8. Every AI inference service implements a provider interface with uniform method signatures (no provider-specific parameters; configuration injected at construction).
9. `LibMpvHeadlessTransport` and `LibMpvEmbeddedTransport` are created/owned/disposed by `MediaTransportManager` via `GetOrCreate*` accessors.

## Current Milestone Status

| Milestone | Status | Summary |
|---|---|---|
| 1–9 | Complete | Foundation through subtitle/inspection |
| 10 | Complete | Settings and bootstrap |
| 11 | Substantially complete | Local/offline expansion |
| **12** | **In progress** | Runtime optimization, hardware routing, compute profiles (CPU/GPU/Cloud), managed local GPU host; real NVIDIA validation and live container smoke tests still pending |
| 13 | Future | Release hardening, clean-machine validation |

See `docs/history/smoke/` for gate evidence on each milestone.

## Troubleshooting

### Standard diagnostic sequence (`/troubleshoot`)

When build/test instability is reported, run and record output from each step:

1. `dotnet build`
2. `dotnet test`
3. `python scripts/check-architecture.py`
4. `python -m py_compile inference/main.py`

Treat this output as required evidence in bug reports and fix PRs: failing step, first concrete error, impacted files/symbols.

### Workload resolver error

`Workload set version X has missing manifests` is a known artifact of SDK upgrades. `Directory.Build.props` sets `MSBuildEnableWorkloadResolver=false` to suppress it permanently — no action needed.

### Locked file error

If the build fails with `process cannot access the file` (locked by `clrdbg.exe` or `.NET Host`):

```powershell
taskkill /F /IM clrdbg.exe /IM dotnet.exe
```

### Python inference environment

- Managed automatically via bundled `uv.exe` — no manual Python install required.
- First-use downloads cached in `%LOCALAPPDATA%\BabelPlayer\runtime\`.
- GPU path: ~5 GB (torch+CUDA, models); CPU path: ~800 MB.
- Inference server runs as an HTTP subprocess (FastAPI + uvicorn).
