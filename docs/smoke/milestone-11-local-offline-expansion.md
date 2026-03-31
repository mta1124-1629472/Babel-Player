# Milestone 11: Local / Offline Expansion — Smoke Note

## Metadata

| Field | Value |
|---|---|
| Milestone | 11 |
| Name | Local / Offline Expansion |
| Date | 2026-03-31 |
| Status | **partial** |

---

## Gate Summary

| Gate item | Status |
|---|---|
| The main workflow can run locally on at least one supported machine configuration | ⚠️ Partial — transcription is local; translation and TTS require internet |
| Local capability is truthful and verified | ✅ — what is local is clearly local; what is not is gated and explicit |
| Unsupported local paths remain clearly unsupported | ✅ — `ProviderCapability` throws explicit errors for unimplemented providers |

---

## What Is Fully Local Today

### Transcription — `faster-whisper` ✅

- Whisper model runs entirely on local CPU (or GPU if available) via the `faster-whisper` Python library.
- No internet access required after the model is downloaded.
- Model weights are downloaded by faster-whisper on first use and cached in the Hugging Face cache directory (`~/.cache/huggingface/`). Subsequent runs are fully air-gapped.
- All five model sizes (tiny, base, small, medium, large-v3) are supported and now selectable from the config panel; the selected model is passed to the Python subprocess.
- ffmpeg is required locally for audio extraction from video files; this is probed at startup and surfaced explicitly if absent.

**Verified:** transcription runs without any network calls after model download. `BootstrapDiagnostics` surfaces a clear warning if Python or ffmpeg is absent.

---

## What Requires Internet Today

### Translation — `google-translate-free` ⚠️

- Uses the `googletrans` Python library, which calls Google Translate's public web API.
- **Not offline.** Fails silently or raises an exception if the machine has no internet access.
- No API key is required (unofficial free-tier endpoint), but the endpoint is not guaranteed stable — it is a reverse-engineered path and has historically broken across library versions.
- A fully local translation path (e.g., Argos Translate, LibreTranslate, or a local LLM) is not yet implemented. The `ProviderCapability` gate correctly rejects all other listed translation providers (`deepl`, `openai`) with an explicit "not implemented" message.

**Status:** internet-dependent. A local translation path is the primary remaining gap for this milestone.

### TTS — `edge-tts` ⚠️

- Uses Microsoft's Edge neural TTS service via the `edge-tts` Python library.
- **Not offline.** Requires a live connection to Microsoft's Azure TTS endpoint.
- No API key required (uses an unofficial endpoint scraped from Edge browser), but it is subject to availability and rate limits.
- Fully local TTS alternatives (Piper, Coqui TTS, Kokoro) are not yet implemented. The `ProviderCapability` gate rejects `elevenlabs`, `openai-tts`, and `google-cloud-tts` with explicit "not implemented" messages.

**Status:** internet-dependent. A local TTS path is the second remaining gap for this milestone.

---

## What the Dispatch Layer Does for Milestone 11

The backend provider dispatch (see `milestone-07-provider-dispatch.md`) provides the architecture needed to add local providers without restructuring:

- `ProviderCapability.ValidateTranscription/Translation/Tts()` is the insertion point for new providers.
- Adding a local translation or TTS provider means: adding it to the supported set, adding a corresponding service class, and dispatching from the coordinator.
- The Python subprocess boundary (`TranscriptionService`, `TranslationService`, `TtsService`) is the right seam — local providers can slot in behind it without changing any upstream code.
- The `AppSettings` fields (`TranslationProvider`, `TtsProvider`) and UI dropdowns are already in place; no new settings infrastructure is needed.

---

## Truthfulness Statement

The app makes no false claims about offline capability:

- Running the pipeline with default settings on a machine with no internet will:
  - ✅ Transcribe successfully (faster-whisper is local)
  - ❌ Fail translation with a Python subprocess error (googletrans network call fails)
  - ❌ Fail TTS with a Python subprocess error (edge-tts network call fails)
- These failures surface as `StatusText` error messages in the UI — not silent degradation.
- The diagnostics warning bar only flags Python and ffmpeg absence; it does not claim internet connectivity readiness because the app does not probe network access at startup.

---

## Remaining Work to Complete This Milestone

| Gap | Suggested path | Notes |
|---|---|---|
| Local translation | Argos Translate (Python, MIT) or LibreTranslate (self-hosted) | Argos is fully offline post-install; quality is lower than cloud but acceptable for dubbing scaffolding |
| Local TTS | Piper TTS (Python, MIT) or Kokoro (Python) | Piper is fast on CPU, supports many voices, fully offline |
| Model download / setup UX | Explicit download step with progress, not silent on first run | AGENTS.md: no fake readiness — model download must be visible |
| `BootstrapDiagnostics` expansion | Add Python library checks (`faster_whisper`, `googletrans`, `edge_tts`) | Currently only probes Python binary and ffmpeg binary |

---

## Evidence

- `Services/TranscriptionService.cs` — local faster-whisper subprocess, model passed from settings
- `Services/ProviderCapability.cs` — dispatch gate; unsupported providers throw `PipelineProviderException`
- `Services/BootstrapDiagnostics.cs` — Python + ffmpeg probe, exposed as coordinator observable property
- `Models/ProviderOptions.cs` — full provider/model option lists; non-implemented providers are listed in UI and rejected at runtime

### Build and test status

```
dotnet build BabelPlayer.csproj   → 0 errors, 0 warnings
dotnet test                       → 83 passed, 0 failed
```

---

## Deferred Items

- Local translation provider (Argos Translate or equivalent)
- Local TTS provider (Piper or equivalent)
- Python library presence check in `BootstrapDiagnostics`
- Model download progress UX (explicit, not silent)
- WSL-hosted inference path (evaluate in M12)
- Containerised / NVIDIA-managed serving path (evaluate in M12)
