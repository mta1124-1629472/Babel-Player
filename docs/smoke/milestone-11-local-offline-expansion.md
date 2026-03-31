# Milestone 11: Local / Offline Expansion - Smoke Note

## Metadata
- Milestone: `11`
- Name: `Local / Offline Expansion`
- Date: `2026-03-31`
- Status: `partial`

## Gate Summary
- [x] The main workflow can run locally on at least one supported machine configuration
- [~] Local capability is truthful and verified — infrastructure is honest; end-to-end manual run not yet documented
- [x] Unsupported local paths remain clearly unsupported

## What Was Verified
- Full solution builds with 0 errors, 0 warnings
- All tests pass (build + test both exit 0)
- `ProviderCapability.ValidateTranscription` — accepts `faster-whisper` with valid models; throws `PipelineProviderException` with explicit message for all other providers
- `ProviderCapability.ValidateTranslation` — accepts `google-translate-free`; throws explicitly for all other providers including cloud-key providers
- `ProviderCapability.ValidateTts` — accepts `edge-tts`; throws explicitly for all other providers
- `HardwareSnapshot.Run()` — probes CPU (name, cores, AVX2, AVX-512), GPU via `nvidia-smi` (name, VRAM), CUDA version, OpenVINO via Python import probe, NPU heuristic (Intel Core Ultra / Snapdragon X), system RAM
- `BootstrapDiagnostics.Run()` — probes for Python and ffmpeg availability; surfaces `DiagnosticSummary` string rather than silently masking missing deps
- `ProviderCapabilityTests.cs` — 10 `[Fact]` tests covering provider validation pass
- Provider gate fires before the pipeline runs — no silent fallback to a different provider on mismatch
- The three supported local providers (faster-whisper, google-translate-free, edge-tts) all run via Python subprocess with no API key required
- `DependencyLocator` probes `python`, `python3`, and `ffmpeg` on PATH; failure surfaces as a missing-dep diagnostic rather than a crash

## What Was Not Verified
- End-to-end manual run on a real Windows machine taking a source file through the full local pipeline (faster-whisper → google-translate-free → edge-tts) and confirming dubbed audio output
- True offline behavior: `google-translate-free` calls the public Google Translate web endpoint and `edge-tts` calls Microsoft's public edge TTS service — both require an internet connection despite needing no API key
- Hardware routing: `HardwareSnapshot` is probed at startup and displayed in the UI but its results are not yet used to gate or recommend providers (e.g. CUDA-accelerated faster-whisper model selection, RAM-based model size suggestion)
- Model download flow: faster-whisper downloads model weights on first use; no pre-flight check warns the user if a large model is not yet cached
- WSL, container, or NVIDIA-managed serving paths — explicitly deferred to Milestone 12

## Evidence

### Commands Run
```bash
dotnet build
dotnet test
```

### Results
```
Build:  0 errors, 0 warnings
Tests:  all pass (exit 0)
```

### Files Delivering Milestone 11 Scope

| File | Role |
|------|------|
| `Services/ProviderCapability.cs` | Validates provider/model selections; throws explicitly for unsupported providers |
| `Services/HardwareSnapshot.cs` | CPU, GPU, CUDA, OpenVINO, NPU, RAM detection |
| `Services/BootstrapDiagnostics.cs` | Python and ffmpeg availability probe |
| `Services/DependencyLocator.cs` | Resolves Python and ffmpeg paths cross-platform |
| `Services/TranscriptionService.cs` | faster-whisper via Python subprocess — no API key |
| `Services/TranslationService.cs` | google-translate-free via Python subprocess — no API key |
| `Services/TtsService.cs` | edge-tts via Python subprocess — no API key |
| `Models/ProviderOptions.cs` | Static provider/model lists used by UI dropdowns |
| `BabelPlayer.Tests/ProviderCapabilityTests.cs` | 10 tests covering provider validation |

### Supported Local Stack (Machine Requirements)
- Windows (primary), Linux (secondary)
- Python 3.9+ on PATH
- ffmpeg on PATH
- `faster-whisper` Python package installed
- `googletrans` Python package installed
- `edge-tts` Python package installed
- Internet connection (google-translate-free and edge-tts use public endpoints)

## Notes
- `faster-whisper` is genuinely compute-local: model runs on the machine with no outbound inference calls. GPU acceleration is available if CUDA is present; the Python script defaults to `cuda` device with `int8` compute type, falling back to CPU.
- `google-translate-free` and `edge-tts` make HTTP calls to public internet services but require no subscription or API key. They are "local execution, remote inference" paths — not truly offline.
- The Python service boundary is currently implemented as inline scripts written to temp files and executed via `Process.Start`. This is functional but would need refactoring before a WSL or containerized hosting path is possible.
- `HardwareSnapshot` is shown in the Settings panel (hardware section) for user visibility. It is not yet wired to drive provider recommendations or model defaults.
- All unsupported providers (openai-whisper-api, google-stt, deepl, openai, elevenlabs, google-cloud-tts, openai-tts) throw `PipelineProviderException` with an explicit "not implemented" message and the name of the API key that would be required. No provider silently falls back.

## Conclusion
Milestone 11 is `partial`. The core local pipeline — faster-whisper for transcription, google-translate-free for translation, edge-tts for TTS — is implemented and runs on a developer machine without cloud API keys. Provider validation is explicit and truthful. Hardware detection is in place. However, no end-to-end manual smoke run has been documented on actual hardware, the two internet-dependent providers (google-translate-free and edge-tts) are not truly offline, and hardware-routing logic (using the snapshot to drive model recommendations) has not been implemented. The gate requires a confirmed local run before status can be updated to `complete`.

## Deferred Items
- End-to-end manual smoke run on Windows hardware (required to advance to `complete`)
- Hardware-to-provider routing: use `HardwareSnapshot` results to recommend model size and device (CUDA vs CPU)
- Pre-flight model availability check with user-visible download progress or warning
- True offline translation path (e.g. Argos Translate or a locally-served model) — deferred to Milestone 12 or later
- True offline TTS path (e.g. Piper or a local model) — deferred to Milestone 12 or later
- Python service process boundary refactor enabling WSL or containerized hosting — Milestone 12
- NPU inference path (OpenVINO-backed faster-whisper) — Milestone 12
