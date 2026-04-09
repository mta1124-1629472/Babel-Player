# Babel Player — Gemini Context

## Commands

```bash
dotnet clean babel-player.sln
dotnet build babel-player.sln             # Full build (includes restore)
dotnet build babel-player.sln --no-restore # Fast build (skip restore)
dotnet test babel-player.sln             # Run all tests
dotnet run --project BabelPlayer.csproj   # Launch the app
python3 scripts/check-architecture.py    # Architecture linter
```

## Troubleshooting Build Issues

If the build fails with a "process cannot access the file" error (typically locked by `clrdbg.exe` or `.NET Host`), run the following command to force-clear the locks:

```powershell
taskkill /F /IM clrdbg.exe /IM dotnet.exe
```

## Essential Reading

Before making non-trivial changes, read:
- `AGENTS.md` — operating rules, scope discipline, non-negotiables
- `PLAN.md` — milestone order and gates (the plan wins if anything conflicts)
- `docs/architecture.md` — structural boundaries and state ownership

## Project Overview

Babel Player is a cross-platform desktop dubbing/localization app:
- **C# / .NET 10.0** + **Avalonia 12.0 RC1** (Fluent theme)
- **libmpv** for media playback (P/Invoke)
- **Python subprocesses** for AI inference (Faster-Whisper, Piper, etc.)
- **Gemini Native Support**: Integrated transcription and translation via Google Gemini AI.

Core workflow chain: `source media → ingest → transcribe → translate → TTS → preview → persist`

## Current Milestone

**Milestones 1–11** are complete. **Milestone 12 — Runtime Optimization and Hardware Routing** is in progress.

## Gemini Capabilities in Babel Player

| Feature | Implementation | Notes |
|---------|----------------|-------|
| **Transcription** | `GeminiTranscriptionProvider` | Supports `TranscribeOnly` or `TranscribeAndTranslate` (one-pass). |
| **Translation** | `GeminiTranslationProvider` | Multi-segment or single-segment translation. |
| **API Client** | `GeminiApiClient` | Handles audio/text requests to Google Gemini. |

### Pro-Tip: One-Pass Dubbing
`GeminiTranscriptionProvider` can perform transcription and translation in a single pass (`GeminiTranscribeMode.TranscribeAndTranslate`). This bypasses the dedicated translation stage in the coordinator.

## Python/C# Serialization Contract

**Critical:** Field names crossing the Python/C# boundary are explicit serialization contracts.
- Python emits `snake_case/camelCase`
- C# deserializes with `PropertyNameCaseInsensitive = true` or `[JsonPropertyName]`
- **Segment ID Contract**: `segment_{start}` (e.g., `segment_0.0`, `segment_3.68`). Changing this breaks TTS segment lookup.

## Directory Structure

- `Models/`: Domain data (Snapshots, Segments, PlaybackState)
- `Services/`: Core logic, `SessionWorkflowCoordinator` (state owner)
- `Views/` & `ViewModels/`: Avalonia MVVM UI
- `inference/`: Python inference scripts
- `scripts/check-architecture.py`: Architecture linter (RUN THIS REGULARLY)

## Gotchas

- **State Ownership**: `SessionWorkflowCoordinator` is the sole owner of session/workflow state. Do not scatter state in UI.
- **Provider Registry**: All providers must be registered in `TranscriptionRegistry`, `TranslationRegistry`, or `TtsRegistry`.
- **Hardware Logic**: Use `HardwareSnapshot` for capability detection; do not hardcode GPU assumptions.
- **Fake Readiness**: Forbidden. Use explicit placeholders or disabled states for unimplemented paths.
- **No Premature Abstraction**: Don't build "plugin systems" for things that don't exist yet.

## Testing

```bash
dotnet test                            # All tests
dotnet test --filter "ClassName=SessionWorkflowTests"
```
Tests involving libmpv or GPU resources are often grouped in the `"Media transport"` collection and run non-parallel.
