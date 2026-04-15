# Babel-Player — Engineering Plan

**Last updated:** April 15, 2026 — Parakeet + streaming status refresh  
**Status:** Phases 1–6 implemented in code; remaining work is verification/benchmark follow-up.

---

## Project Context

Babel-Player is a local-first multilingual video dubbing application. Stack: C# / .NET 10 / Avalonia 12.0.0 / CommunityToolkit.Mvvm. The pipeline runs three inference stages — Transcription, Translation, TTS — plus Diarization for speaker identification. Provider registries per stage support CPU, GPU, and Cloud compute profiles. All local inference runs through a single FastAPI server at `inference/main.py` inside a managed Python 3.12 `.venv`. Zero data leaves the machine on local profiles.

### Architecture notes (verified April 13, 2026)

- Default local inference does not require Docker. All local providers (faster-whisper, NLLB/CTranslate2, Qwen TTS, NeMo diarization) run as endpoints in a single FastAPI process inside a managed `.venv`. Docker is an advanced optional backend; not required for CPU or managed-GPU paths.
- `nemo-toolkit[asr]==2.7.2` is installed in the GPU `.venv`, verified working with `torch 2.8.0` and Python 3.12.
- Multi-speaker voice cloning is fully implemented. `SessionWorkflowCoordinator.TtsReference.cs` extracts per-speaker reference clips via `/speakers/extract-reference`. The diarization → speaker extraction → per-speaker TTS chain is complete and tested.
- Edge TTS, Piper, and WeSpeaker are subprocess-based providers. Everything else runs in the persistent FastAPI server.
- Managed CPU diarization bootstrap now uses dedicated `inference/cpu-requirements.txt` and `inference/cpu-constraints.txt` manifests plus a persisted import-validation record before WeSpeaker is marked ready.
- XTTS v2 removed. All XTTS references are legacy.

### Provider map (verified April 13, 2026)

| Stage | Provider | Profile | Deployment | Token |
|---|---|---|---|---|
| ASR | faster-whisper | CPU / GPU | `.venv` FastAPI | No |
| ASR | Parakeet-TDT | GPU | `.venv` FastAPI | No |
| Translation | NLLB / CTranslate2 | CPU / GPU | `.venv` FastAPI | No |
| TTS | Qwen TTS | GPU | `.venv` FastAPI | No |
| TTS | Piper | CPU | Subprocess | No |
| TTS | Edge TTS | Cloud | Subprocess | No |
| TTS | OpenAI TTS | Cloud | HTTP client | API key |
| TTS | ElevenLabs | Cloud | HTTP client | API key |
| Diarization | NeMo ClusteringDiarizer | GPU | `.venv` FastAPI | No |
| Diarization | WeSpeaker (CPU) | CPU | Managed CPU `.venv` subprocess | No |

---

## Completed Work

### Phase 1 — Foundation Stabilization ✅

| Item | Resolution |
|---|---|
| 1.0 — Avalonia 12.0.0 upgrade | `BabelPlayer.csproj` targets `Avalonia 12.0.0` |
| 1.1 — `_mediaSnapshotCache` thread safety | `ConcurrentDictionary` |
| 1.2 — Fire-and-forget async helper | `Services/TaskExtensions.cs` |
| 1.3 — Duplicated startup code | Refactored in `App.axaml.cs` |
| 1.4 — Hardcoded language fallback | Explicit checks in `SessionWorkflowCoordinator.Pipeline.cs` |
| 1.5 — Stream TTS audio downloads | `ReadAsStreamAsync` + `CopyToAsync` in all three HTTP clients |
| 1.6 — Reuse `HttpClient` | Reused in all cloud provider instances |

### Phase 2 — TTS Performance Quick Wins ✅

| Item | Resolution |
|---|---|
| 2.1 — Eliminate double TTS synthesis | Sequential synthesis bottleneck removed |
| 2.2 — Provider-aware parallelism cap | `MaxConcurrency` property implemented and used by coordinator |

### Phase 3 — Diarization Provider Overhaul ✅

| Item | Resolution | Verified |
|---|---|---|
| 3.1 — NeMo-only `/diarize` | `_run_nemo_diarization()` → `ClusteringDiarizer`; zero pyannote imports | April 13, 2026 |
| 3.2 — WeSpeaker `/diarize/wespeaker` | Endpoint returns HTTP 410 Gone; deprecated in favour of managed CPU runtime | April 13, 2026 |
| 3.3 — Update `/capabilities` | Reflects current provider set | April 13, 2026 |
| 3.4 — NeMo + WeSpeaker C# providers | Both registered in `DiarizationRegistry`; GPU containerized variant deleted | April 13, 2026 |
| 3.5 — pyannote + HF token removal | Zero pyannote/HF_TOKEN refs in `requirements.txt` and `main.py` | April 13, 2026 |
| 3.6 — UI wiring audit | Complete | Pre-tracker |
| 3.7 — Diarization provider ComboBox | ComboBox in `MainWindow.axaml` | Pre-tracker |
| 3.8 — Re-diarize command | `RunDiarizationOnlyCommand` exists | Pre-tracker |
| 3.9 — SpeakerId in segment row | Colored badge with `SpeakerIdToShortLabelConverter` | Pre-tracker |

### Phase 5 — Subprocess Provider Polish ✅

| Item | Resolution |
|---|---|
| 5.1 — Batch Python scripts | Superseded by 5.2; worker pool JSON-RPC achieves the same goal |
| 5.2 — Persistent Python worker pool | `PythonJsonWorkerPool.cs` with Edge TTS + Piper workers; stderr reading hardened April 13, 2026 |

### Phase 4 — ASR Provider Expansion (NeMo Parakeet) ✅

| Item | Resolution | Verified |
|---|---|---|
| 4.1 — Python `/transcribe/parakeet` endpoint | Endpoint implemented in `inference/main.py` using NeMo Parakeet-TDT-0.6B-v3 with timed segment shaping | April 15, 2026 |
| 4.2 — C# Parakeet provider | `ParakeetTranscriptionProvider` implemented and routed through `ContainerizedInferenceClient.TranscribeParakeetAsync` | April 15, 2026 |
| 4.3 — UI wiring | Parakeet exposed in GPU transcription provider options via `TranscriptionRegistry` | April 15, 2026 |

### Phase 6 (partial) — Architectural Refactors ✅ (items below)

| Item | Resolution |
|---|---|
| 6.1b — VM decomposition | `EmbeddedPlaybackViewModel.cs` reduced to 163 lines; preview, pipeline, and speaker-routing child VMs now own their binding surfaces |
| 6.2a — Channel streaming pipeline | `SessionWorkflowCoordinator.Pipeline.cs` now uses channel-based overlap (`TranscriptChannelItem` → `TranslationChannelItem` → `TtsChannelItem`) for streaming execution paths |
| 6.3 — Qwen TTS server-side batching | `/tts/qwen/batch` endpoint live |
| 6.4 — Constructor cleanup | `CoordinatorCoreServices` added; coordinator constructors reduced to ≤ 5 parameters; all call sites updated |
| 6.5 — Clean shutdown | `Environment.Exit` removed; Part 4 staged |

---

## Remaining Work

Implementation milestones are complete; remaining follow-up is verification evidence:

- Run and record an end-to-end benchmark showing overlap improvements from the channel pipeline path.
- Run manual app-shell smoke to validate streaming stage messaging/progress UX during real sessions.
- Keep historical smoke notes synchronized so retired docs do not imply open implementation gaps.

---

## Impact Reference

| Item | Pipeline Impact | Effort | Risk |
|---|---|---|---|
| 6.1b — VM decomposition | — | 2–3 days | Low |
| 6.2a — Channel streaming | ~30–50% end-to-end | Implemented (verification follow-up) | Medium |
