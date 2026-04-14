# Babel-Player — Engineering Plan

**Last updated:** April 13, 2026 — code-verified audit plus constructor cleanup completion  
**Status:** Phases 1–3, 5, and Phase 6 item 6.1b/6.4 complete. Phase 4 and Phase 6 (6.2a) remain.

---

## Project Context

Babel-Player is a local-first multilingual video dubbing application. Stack: C# / .NET 10 / Avalonia 12.0.0 / CommunityToolkit.Mvvm. The pipeline runs three inference stages — Transcription, Translation, TTS — plus Diarization for speaker identification. Provider registries per stage support CPU, GPU, and Cloud compute profiles. All local inference runs through a single FastAPI server at `inference/main.py` inside a managed Python 3.12 `.venv`. Zero data leaves the machine on local profiles.

### Architecture notes (verified April 13, 2026)

- Default local inference does not require Docker. All local providers (faster-whisper, NLLB/CTranslate2, Qwen TTS, NeMo diarization) run as endpoints in a single FastAPI process inside a managed `.venv`. Docker is an advanced optional backend; not required for CPU or managed-GPU paths.
- `nemo-toolkit[asr]==2.7.2` is installed in the GPU `.venv`, verified working with `torch 2.8.0` and Python 3.12.
- Multi-speaker voice cloning is fully implemented. `SessionWorkflowCoordinator.TtsReference.cs` extracts per-speaker reference clips via `/speakers/extract-reference`. The diarization → speaker extraction → per-speaker TTS chain is complete and tested.
- Edge TTS and Piper are the only subprocess-based providers. Everything else runs in the persistent FastAPI server.
- XTTS v2 removed. All XTTS references are legacy.

### Provider map (verified April 13, 2026)

| Stage | Provider | Profile | Deployment | Token |
|---|---|---|---|---|
| ASR | faster-whisper | CPU / GPU | `.venv` FastAPI | No |
| ASR | Parakeet-TDT *(Phase 4, open)* | GPU | `.venv` FastAPI | No |
| Translation | NLLB / CTranslate2 | CPU / GPU | `.venv` FastAPI | No |
| TTS | Qwen TTS | GPU | `.venv` FastAPI | No |
| TTS | Piper | CPU | Subprocess | No |
| TTS | Edge TTS | Cloud | Subprocess | No |
| TTS | OpenAI TTS | Cloud | HTTP client | API key |
| TTS | ElevenLabs | Cloud | HTTP client | API key |
| Diarization | NeMo ClusteringDiarizer | GPU | `.venv` FastAPI | No |
| Diarization | WeSpeaker (CPU) | CPU | `.venv` FastAPI | No |

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

### Phase 6 (partial) — Architectural Refactors ✅ (items below)

| Item | Resolution |
|---|---|
| 6.1b — VM decomposition | `EmbeddedPlaybackViewModel.cs` reduced to 163 lines; preview, pipeline, and speaker-routing child VMs now own their binding surfaces |
| 6.3 — Qwen TTS server-side batching | `/tts/qwen/batch` endpoint live |
| 6.4 — Constructor cleanup | `CoordinatorCoreServices` added; coordinator constructors reduced to ≤ 5 parameters; all call sites updated |
| 6.5 — Clean shutdown | `Environment.Exit` removed; Part 4 staged |

---

## Remaining Work

Execution order is fixed by dependency: Parakeet lands before streaming so the next capability can be added without reopening the completed playback VM split.

```
Tier 2 (4.x Parakeet)      → 1.5–2 weeks — do next
    ↓
Tier 3 (6.2 streaming)     → 2–3 weeks   — highest impact, highest risk, last
```

---

### Tier 2 — Parakeet ASR Provider (4.1–4.3)

**Zero Parakeet references exist anywhere in the codebase.**

#### 4.1 — Python: `POST /transcribe/parakeet`
- **File:** `inference/main.py`
- **What:** Full endpoint using NeMo Parakeet-TDT-0.6B-v3. Accepts audio file, returns timed segments matching the existing segment schema.
- **Dependency:** `nemo-toolkit[asr]` already installed in GPU `.venv`. Weights downloaded on first use.
- **Acceptance:** Endpoint returns JSON matching the schema consumed by the C# coordinator.

#### 4.2 — C#: `ParakeetTranscriptionProvider`
- **Files:** `Services/ParakeetTranscriptionProvider.cs`, `Models/ProviderNames.cs`, `Services/Registries/TranscriptionRegistry.cs`
- **What:** New provider implementing `ITranscriptionProvider`. Add `ProviderNames.Parakeet` constant. Register in `TranscriptionRegistry`.
- **Acceptance:** Parakeet selectable in transcription provider ComboBox, produces correct timed segments.

#### 4.3 — UI wiring
- **What:** Surface Parakeet in the transcription provider ComboBox when GPU profile is active.
- **Acceptance:** Parakeet visible and selectable when GPU compute enabled.

---

### Tier 3 — Channel Streaming Pipeline (6.2a)

**`Services/SessionWorkflowCoordinator.Pipeline.cs`**

Current state: fully sequential async/await. Zero `System.Threading.Channels` usage. The Part 4 "staged, partial" tracker note was not reflected in actual code as of April 13, 2026.

What remains:
- Replace `Transcribe all → Translate all → TTS all` with `Channel<TranscriptSegment>` → `Channel<TranslatedSegment>` → `Channel<TtsResult>` pipeline
- Each stage reads from upstream channel and writes to downstream, enabling overlap
- Potentially extract to new `Services/PipelineChannel*.cs` types

Acceptance: Transcription segments begin flowing to translation before full transcription completes; translation segments begin flowing to TTS before full translation completes. Measurable reduction in end-to-end wall time on the test video.

---

## Impact Reference

| Item | Pipeline Impact | Effort | Risk |
|---|---|---|---|
| 6.1b — VM decomposition | — | 2–3 days | Low |
| 4.1–4.3 — Parakeet ASR | ~10x ASR speed (EU langs) | 1.5–2 weeks | Medium |
| 6.2a — Channel streaming | ~30–50% end-to-end | 2–3 weeks | Medium |
