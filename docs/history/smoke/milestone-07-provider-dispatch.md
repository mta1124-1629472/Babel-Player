# Smoke Note — Provider/Model Dispatch Backend

## Metadata

| Field | Value |
|---|---|
| Date | 2026-03-30 |
| Branch | `feature/provider-dispatch-backend` |
| Milestone | 7 (Dub Session Workflow) — backend routing slice |
| Status | **complete** |

---

## Gate Summary

The left config panel already exposed provider/model dropdowns backed by persisted AppSettings, but the pipeline backend ignored those settings entirely. This slice closes that gap with honest, minimal routing:

- Provider/model selections are now **validated before each stage runs**
- Unsupported providers **fail explicitly** with a human-readable error — no silent fallback
- The `faster-whisper` model selection (tiny/base/small/medium/large-v3) is now **actually honoured** in the Python subprocess
- All currently working default providers continue to work on the exact same code paths

---

## What Was Verified

### Build and tests

- `dotnet build` — 0 errors, 0 warnings
- `dotnet test` — all existing tests pass; all 13 new `ProviderCapabilityTests` pass

### Provider gate behaviour

| Scenario | Expected result | Verified |
|---|---|---|
| Default settings (faster-whisper / google-translate-free / edge-tts) | Pipeline runs identically to before | ✓ (tests pass, code path unchanged) |
| `faster-whisper` + model `base` | Validation passes, service receives `model="base"` | ✓ |
| `faster-whisper` + model `small` | Validation passes, Python script uses `model_name = 'small'` | ✓ (log message confirms) |
| `faster-whisper` + model `large-v3` | Validation passes | ✓ |
| `faster-whisper` + model `large` (invalid) | `PipelineProviderException` with message listing valid models | ✓ (unit test) |
| `openai-whisper-api` (not implemented) | `PipelineProviderException` — "not implemented, only faster-whisper supported" | ✓ (unit test) |
| `google-stt` (not implemented) | `PipelineProviderException` | ✓ (unit test) |
| `deepl` translation (not implemented) | `PipelineProviderException` | ✓ (unit test) |
| `openai` translation (not implemented) | `PipelineProviderException` | ✓ (unit test) |
| `elevenlabs` TTS (not implemented) | `PipelineProviderException` | ✓ (unit test) |
| `openai-tts` (not implemented) | `PipelineProviderException` | ✓ (unit test) |
| `google-cloud-tts` (not implemented) | `PipelineProviderException` | ✓ (unit test) |

### Exception propagation

`PipelineProviderException` extends `InvalidOperationException`, so it is caught by the existing `catch` blocks in `EmbeddedPlaybackViewModel.RunPipelineAsync()` and surfaces as `StatusText` — the same way any other pipeline failure does. No UI changes were needed.

---

## What Was Not Verified

- **Live execution with non-default models** (e.g. `small`, `large-v3`) against real audio — those require Python + faster_whisper installed, which is outside the scope of this routing slice. The model injection mechanism is correct; runtime behaviour depends on the model files being present on disk.
- **API key–gated providers** — the error message for providers that require a key mentions the credential key name. Actual key presence is checked when `ApiKeyStore` is non-null; the gate is wired but the providers themselves are not implemented.

---

## Evidence

- `BabelPlayer.Tests/ProviderCapabilityTests.cs` — 13 focused unit tests, all passing
- `Services/ProviderCapability.cs` — validation logic, plain static methods
- `Services/PipelineProviderException.cs` — explicit exception type
- `Services/TranscriptionService.cs` — `TranscribeAsync()` now accepts `model` param, injected into Python script
- `Services/SessionWorkflowCoordinator.cs` — three stage methods now call `ProviderCapability.Validate*` before dispatching

---

## What Is Real vs. Placeholder

| Provider | Stage | Status |
|---|---|---|
| `faster-whisper` | Transcription | **Real** — model selection honoured (tiny/base/small/medium/large-v3) |
| `openai-whisper-api` | Transcription | **Placeholder** — listed in UI, throws `PipelineProviderException` if selected |
| `google-stt` | Transcription | **Placeholder** — same |
| `google-translate-free` | Translation | **Real** — provider gate passes, service runs as before |
| `deepl` | Translation | **Placeholder** — throws `PipelineProviderException` |
| `openai` | Translation | **Placeholder** — throws `PipelineProviderException` |
| `edge-tts` | TTS | **Real** — voice/model selection unchanged, provider gate passes |
| `elevenlabs` | TTS | **Placeholder** — throws `PipelineProviderException` |
| `openai-tts` | TTS | **Placeholder** — throws `PipelineProviderException` |
| `google-cloud-tts` | TTS | **Placeholder** — throws `PipelineProviderException` |

---

## Deferred Items

- Implementing `openai-whisper-api` (cloud transcription) — Milestone 11 (Local/Offline Expansion) or later
- Implementing `deepl`, `openai` translation — Milestone 11 or later
- Implementing `elevenlabs`, `openai-tts`, `google-cloud-tts` — Milestone 11 or later
- Hardware-based model routing (e.g. auto-select `large-v3` on GPU) — Milestone 12 (Runtime Optimization)
