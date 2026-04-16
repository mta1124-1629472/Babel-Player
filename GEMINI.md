# Babel Player — Gemini Context

Babel Player is a high-performance Windows desktop workstation for segment-based AI video dubbing. It automates the pipeline from source media to a dubbed video with a translated and spoken transcript.

## Project Overview

- **Core Workflow**: `Source Media → Ingest → Transcribe → Translate → TTS (Dub) → Preview → Export`.
- **Primary Tech Stack**:
  - **Frontend**: C# / .NET 10.0 + Avalonia 12.0 RC1 (Fluent Theme).
  - **Media**: libmpv (P/Invoke) for hardware-accelerated playback; ffmpeg for processing.
  - **AI Inference**: Python subprocesses (managed via `uv`) or Docker containers.
- **Key AI Providers**:
  - **Transcription**: Faster-Whisper (local), OpenAI Whisper, Google STT, Gemini.
  - **Translation**: CTranslate2 (local), NLLB-200 (local), DeepL, OpenAI, Gemini.
  - **TTS**: Piper (local), Edge TTS, ElevenLabs, Qwen3-TTS (GPU), XTTS v2 (GPU).
- **Speaker Diarization**: NeMo ClusteringDiarizer (GPU), WeSpeaker (CPU).

## Building and Running

### Prerequisites
- Windows 10/11 (x64 or ARM64).
- NVIDIA GPU (RTX 30-series+ recommended) for GPU-accelerated paths.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

### Essential Commands
```powershell
# Fetch native binaries (libmpv, uv.exe) - Required after initial clone
pwsh ./scripts/fetch-win-native-deps.ps1

# Build & Run
dotnet build Babel-Player.sln             # Full build
dotnet run --project BabelPlayer.csproj   # Launch application
dotnet run -c Dev                         # Dev build (no optimizations)

# Testing
dotnet test Babel-Player.sln             # Run all tests
dotnet test --filter "ClassName=SessionWorkflowTests"

# Architecture & Linting
python scripts/check-architecture.py    # Architecture linter (RUN REGULARLY)
python -m py_compile inference/main.py    # Verify Python syntax
```

## Development Conventions

- **State Ownership**: `SessionWorkflowCoordinator` is the **sole owner** of session and workflow state. Never scatter state across Views or ViewModels.
- **MVVM Pattern**: Strict separation between `Views` (XAML) and `ViewModels` (CommunityToolkit.MVVM). `ViewModels` must not call inference services directly; all pipeline advancement goes through the coordinator.
- **Serialization Contract**: Field names crossing the Python/C# boundary are **explicit contracts**. C# must use `[JsonPropertyName]` or match the Python snake_case/camelCase exactly.
- **Segment IDs**: Derived from start time (`segment_{start}`, e.g., `segment_0.0`). Changing this breaks TTS lookup.
- **Hardware-Aware Routing**: Use `HardwareSnapshot` for capability detection. Avoid silent fallbacks; surface missing dependencies or hardware gaps via readiness checks.
- **Naming**: Product name is `Babel Player`. Namespaces/Assemblies use `BabelPlayer`.

## Project Structure

- `Models/`: Domain records and enums (Snapshots, Segments, PlaybackState).
- `Services/`: Core logic, coordinator, and AI/media boundaries.
- `ViewModels/`: MVVM layer (Coordinators for UI).
- `Views/`: Avalonia XAML files.
- `inference/`: Python inference server (FastAPI + AI Models).
- `scripts/`: Development tools and architecture linter.
- `BabelPlayer.Tests/`: xUnit integration and unit tests.

## Operating Rules for AI Agents

- **Read `AGENTS.md`**: Contains non-negotiable operating rules and learned preferences.
- **Scope Discipline**: Do not perform unrelated refactors or "cleanup". Stick to the current milestone (see `docs/PLAN.md`).
- **Truthful Readiness**: Never implement silent fallbacks or pretend-complete UI. If a path is unimplemented, use explicit placeholders or disabled states.
- **Verification**: All changes must pass the architecture linter (`scripts/check-architecture.py`) and existing tests.
- **Logs**: Diagnostics are written to `%LOCALAPPDATA%\BabelPlayer\logs\babel-player.log`.
- **Persistence**: Sessions auto-save to `%LOCALAPPDATA%\BabelPlayer\state\`.

## Troubleshooting
- **Locked Files**: If the build fails due to file locks, run:
  `taskkill /F /IM clrdbg.exe /IM dotnet.exe`
- **Workload Resolver**: `Directory.Build.props` disables the workload resolver to avoid SDK upgrade artifacts.
