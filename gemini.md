# Babel Player — Gemini Context

Babel Player is a Windows desktop dubbing workstation that automates the process of turning source media into a dubbed video with a translated and spoken transcript.

## Project Overview

- **Core Workflow**: `Source Media → Ingest → Transcribe → Translate → TTS (Dub) → Preview → Export`
- **Primary Tech Stack**: 
  - **Frontend**: C# / .NET 10.0 + Avalonia 12.0 RC1 (Fluent Theme)
  - **Media**: libmpv (P/Invoke) for hardware-accelerated playback; ffmpeg for processing.
  - **AI Inference**: Python subprocesses (managed via `uv`) or Docker containers.
- **Key AI Providers**: 
  - **Transcription**: Faster-Whisper (local), OpenAI Whisper, Google STT, Gemini.
  - **Translation**: CTranslate2 (local), NLLB-200 (local), DeepL, OpenAI, Gemini.
  - **TTS**: Piper (local), Edge TTS, ElevenLabs, Qwen3-TTS (GPU), XTTS v2 (GPU).

## Essential Commands

```bash
# Build & Run
dotnet build Babel-Player.sln             # Full build
dotnet run --project BabelPlayer.csproj   # Launch application
dotnet run -c Dev                         # Dev build (no optimizations)

# Testing
dotnet test Babel-Player.sln             # Run all tests
dotnet test --filter "ClassName=SessionWorkflowTests"

# Architecture & Linting
python3 scripts/check-architecture.py    # Architecture linter (RUN REGULARLY)
python -m py_compile inference/main.py    # Verify Python syntax
```

## Architectural Principles

- **State Ownership**: `SessionWorkflowCoordinator` is the **sole owner** of session and workflow state. Never scatter state across Views or ViewModels.
- **Pipeline Advancement**: ViewModels must not call inference services directly. All advancement must go through the coordinator (e.g., `AdvancePipelineAsync`).
- **Provider Registry**: All new providers must be registered in their respective registries (`TranscriptionRegistry`, `TranslationRegistry`, `TtsRegistry`).
- **Hardware-Aware Routing**: Use `HardwareSnapshot` for capability detection. Do not hardcode GPU assumptions.
- **Truthful Behavior**: Avoid silent fallbacks. If a path is unimplemented or a key is missing, surface it clearly via readiness checks.

## Development Conventions

- **C# Style**: PascalCase for public members, `_camelCase` for private fields. Use `record` for immutable data and `required` for mandatory properties.
- **MVVM**: Strict separation between `Views` (XAML) and `ViewModels` (CommunityToolkit.MVVM).
- **Serialization Contract**: Field names crossing the Python/C# boundary are **explicit contracts**. C# must use `[JsonPropertyName]` or match the Python case exactly.
- **Segment IDs**: Derived from start time (`segment_{start}`, e.g., `segment_0.0`). Changing this breaks TTS lookup.
- **Linter Rules**: `scripts/check-architecture.py` enforces coordinator line limits (<1300), provider constant usage, and `PLACEHOLDER` requirements for stubs.

## Project Structure

- `Models/`: Domain records and enums (Snapshots, Segments, PlaybackState).
- `Services/`: Core logic and AI/media boundaries.
- `ViewModels/` & `Views/`: Avalonia MVVM UI.
- `inference/`: Python inference server (FastAPI + AI Models).
- `scripts/`: Development tools and architecture linter.
- `BabelPlayer.Tests/`: xUnit integration and unit tests.

## Current Milestone

**Milestone 12 — Runtime Optimization and Hardware Routing** is in progress. 
Current focus: GPU acceleration validation, compute profiles (CPU/GPU/Cloud), and multi-speaker diarization refinement.

## troubleshooting

- **Locked Files**: If the build fails due to file locks, run:
  `taskkill /F /IM clrdbg.exe /IM dotnet.exe`
- **Logs**: Located at `%LOCALAPPDATA%\BabelPlayer\logs\babel-player.log`.
- **State**: Persistent sessions are in `%LOCALAPPDATA%\BabelPlayer\state\`.
