# QWEN.md — Babel Player Context

## Project Overview

**Babel Player** is a Windows desktop dubbing workstation built with C# / .NET 10 and Avalonia 12. It transforms source media through a pipeline:

```text
source media → timed transcript → translated dialogue → spoken dubbed output → in-context preview and refinement
```

Users load a video or audio file, generate timed transcripts (local AI or cloud API), translate the dialogue into a target language, produce spoken TTS audio per segment, and preview the dubbed result alongside the source video — then refine individual segments on demand.

**Key characteristics:**
- Windows-only desktop app (WinExe), no Linux/macOS support yet
- Avalonia 12 with Fluent theme, libmpv for embedded video playback
- Python inference subprocess (managed via bundled `uv.exe` — no manual Python install needed)
- GPU (CUDA) and CPU compute paths with CPU/GPU/Cloud selectors per stage
- Session-based workflow with auto-save/restore from `%LOCALAPPDATA%\BabelPlayer\state\`
- comprehensive xUnit integration test suite

## Tech Stack

| Layer | Technology |
|---|---|
| **UI Framework** | Avalonia 12.0 (Fluent theme, Inter font) |
| **MVVM** | CommunityToolkit.Mvvm 8.4.2 |
| **Runtime** | .NET 10.0, C# 12+, nullable enabled |
| **Native media** | libmpv-2.dll (P/Invoke, GPU-accelerated video) |
| **Media processing** | ffmpeg.exe (bundled) |
| **Python management** | uv.exe (bundled, auto-bootstraps venv) |
| **Testing** | xUnit, coverlet |
| **Architecture linter** | `scripts/check-architecture.py` |

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
├── docs/                      # Architecture, smoke notes, benchmarks
├── native/win-x64/            # libmpv-2.dll
├── tools/win-x64/             # ffmpeg.exe, uv.exe
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
| `Models/ProviderNames.cs` | All provider identifier constants (`ProviderNames.*`, `CredentialKeys.*`) |
| `Models/ComputeProfile.cs` | CPU / GPU / Cloud enum |
| `Services/InferenceRuntimeCatalog.cs` | Compute profile → provider routing and normalization |
| `Services/MediaTransportManager.cs` | Owns `LibMpvHeadlessTransport` and `LibMpvEmbeddedTransport` lifecycle |
| `inference/main.py` | Python inference HTTP server (transcription, translation, TTS, diarization) |
| `scripts/check-architecture.py` | Architecture linter (enforces structural rules) |
| `PLAN.md` | 13-milestone roadmap with gates |
| `AGENTS.md` | Operating rules and non-negotiables |

## Building and Running

### Build

```powershell
dotnet build babel-player.sln          # Full build (includes restore)
dotnet build babel-player.sln --no-restore  # Fast build (skip restore)
dotnet run -c Dev                      # Dev build (no optimizations, full debug)
dotnet run --project BabelPlayer.csproj # Launch the app
```

### Test

```powershell
dotnet test babel-player.sln                          # All tests
dotnet test babel-player.sln --filter "ClassName~SessionWorkflowCoordinatorUnitTests"  # Single test class
dotnet test babel-player.sln --filter "ClassName~MethodName"                            # Single test method
dotnet test babel-player.sln -v n                     # Verbose output
```

**Test categories:** `Integration`, `RequiresPython`, `RequiresFfmpeg`, `RequiresExternalTranslation`

### Lint / Verify

```powershell
python3 scripts/check-architecture.py    # Architecture linter (required after structural changes)
python -m py_compile inference/main.py   # Verify Python inference code
```

### Full verification sequence

```powershell
dotnet build babel-player.sln
dotnet test babel-player.sln
python3 scripts/check-architecture.py
python -m py_compile inference/main.py
```

### Troubleshooting

- **Locked file error:** `taskkill /F /IM clrdbg.exe /IM dotnet.exe`
- **Always run the full diagnostic sequence** before reporting issues

## Architecture Rules (Non-Negotiable)

### Provider identifiers are constants
All provider strings live in `Models/ProviderNames.cs`. No inline literals in production code.

### Stage progression runs through coordinator
ViewModels must NOT call `TranscribeMediaAsync`, `TranslateTranscriptAsync`, or `GenerateTtsAsync` directly. Pipeline actions must route through `SessionWorkflowCoordinator` entry points: `AdvancePipelineAsync` for normal progression, `ContinuePipelineAsync` to resume after `Diarized`, and `RunTtsOnlyAsync` when already `Translated`.

### Python/C# field names are explicit contracts
Python emits snake_case or camelCase. C# must match with hardcoded strings or `[JsonPropertyName]` attributes. Never rely on implicit .NET casing. Segment IDs follow `segment_{start}` format (e.g., `segment_0.0`).

### Service interfaces are uniform
Every AI inference service implements a provider interface with uniform method signatures. No provider-specific parameters. Configuration injected at construction.

### Transport lifecycle managed by MediaTransportManager
`LibMpvHeadlessTransport` and `LibMpvEmbeddedTransport` are created/owned/disposed by `MediaTransportManager`. Use `GetOrCreate*` accessors.

### Architecture linter rules
1. `BabelPlayer.csproj` must exist with `OutputType=WinExe`
2. Test project references main project
3. `NotImplementedException` must include `PLACEHOLDER` message
4. Silent event stubs have `PLACEHOLDER` comments
5. No magic provider strings outside `ProviderNames.cs`
6. ViewModels do not call pipeline methods directly
7. `SessionWorkflowCoordinator.cs` must be under 1300 lines

## Code Style

| Convention | Rule |
|---|---|
| **Formatting** | K&R braces, 4-space indent, no trailing whitespace |
| **Naming** | PascalCase for classes/methods/properties; `_camelCase` for private fields; `I` prefix for interfaces |
| **Types** | Prefer `record` for immutable DTOs; use `required` for required properties |
| **Imports** | No unused imports; group: System, third-party, project |
| **Errors** | Throw specific exceptions; `PipelineProviderException` for provider failures with context |
| **Constants** | PascalCase (`ProviderNames.FasterWhisper`) |

## Milestone Status

| Milestone | Status | Summary |
|---|---|---|
| 1–9 | ✅ Complete | Foundation through subtitle/inspection |
| 10 | ✅ Complete | Settings and bootstrap |
| 11 | ✅ Substantially complete | Local/offline expansion |
| **12** | **⏳ In progress** | Runtime optimization, hardware routing, container health |
| 13 | 🔲 Future | Release hardening, clean-machine validation |

## Provider Support Summary

| Stage | Local (CPU) | Local (GPU) | Cloud |
|---|---|---|---|
| **Transcription** | Faster-Whisper | Faster-Whisper | OpenAI Whisper, Google STT, Gemini |
| **Translation** | CTranslate2, NLLB-200 | CTranslate2, NLLB-200 | DeepL, OpenAI, Gemini, Google Translate |
| **TTS** | Piper | Qwen3-TTS, XTTS v2 | Edge TTS, ElevenLabs, Google Cloud, OpenAI |
| **Diarization** | Manual, NemoLocal (`NemoDiarizationAlias`), WeSpeakerLocal (`WeSpeakerDiarizationAlias`) | Manual, NemoLocal, WeSpeakerLocal | — |

## Development Conventions

- **Work one milestone at a time** — no downstream scope starts until current milestone is verified
- **Truthful behavior only** — no fake buttons, silent fallbacks, or disguised incomplete implementation
- **Git history is the archive** — delete dead code; use branches for abandoned experiments
- **Smoke notes required** — store in `docs/smoke/milestone-XX-label.md` with status: `complete`, `partial`, or `failed`
- **Refactors only when they unblock current milestone or reduce real complexity** — not for aesthetic purity
- **UI work should serve the core workflow** — not prestige polish before the loop is real
- **Do not push to main without explicit instruction**

## Session State Persistence

- **Session state:** `%LOCALAPPDATA%\BabelPlayer\state\current-session.json`
- **Session artifacts:** `%LOCALAPPDATA%\BabelPlayer\sessions\{SessionId}\`
- On restore, coordinator validates artifacts exist and downgrades stage if missing
- Corruption recovery: bad JSON moved to `.corrupt`

## Python Inference Environment

- Managed automatically via bundled `uv.exe` — no manual Python install required
- First-use downloads cached in `%LOCALAPPDATA%\BabelPlayer\runtime\`
- GPU path: ~5 GB (torch+CUDA, models); CPU path: ~800 MB
- Inference server runs as HTTP subprocess (FastAPI + uvicorn)
