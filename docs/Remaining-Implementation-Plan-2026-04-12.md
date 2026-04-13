# Babel-Player — Remaining Implementation Plan

**Derived from:** Milestones-Tracker-2026-04-08.md (April 8, 2026)  
**Updated:** April 12, 2026 — after Part 4 (shutdown / Qwen batch / VM decomposition)  
**Scope:** What is still unimplemented, ordered by priority and dependency

---

## What Has Been Completed Since the Tracker

| Item | Phase | Completed In |
|---|---|---|
| Clean shutdown (no `Environment.Exit`) | 6.5 | Part 4 (staged) |
| Qwen TTS batch endpoint (`/tts/qwen/batch`) | 6.3 | Part 4 (staged) |
| ViewModel decomposition (pipeline + speaker routing sub-VMs) | 6.1 | Part 4 (staged) |
| Persistent Python worker pool for Edge TTS / Piper | 5.2 | Part 4 (staged) |
| Streaming pipeline / inter-stage overlap | 6.2 | Part 4 (staged, partial) |
| Diarization UI (ComboBox, Re-diarize, SpeakerId badges) | 3.7–3.9 | Already resolved (pre-tracker) |
| Diarization NeMo + WeSpeaker C# providers + registry | 3.4 | Already resolved (pre-tracker) |

---

## Remaining Work

### Tier 1 — Diarization Cleanup (Phase 3, ~1 week)

#### 3.1 — Python: Replace pyannote `/diarize` with NeMo ClusteringDiarizer
- **Current state:** `/diarize` endpoint exists in `inference/main.py` and calls `_run_nemo_diarization`. NeMo ClusteringDiarizer is already the live implementation.
- **What remains:** Verify no pyannote code path exists anywhere. Confirm the endpoint is not a thin wrapper around pyannote. If pyannote is fully gone, **close this item**.
- **Files:** `inference/main.py` (line ~1534), `Services/NemoContainerizedDiarizationProvider.cs`
- **Acceptance:** `/diarize` route uses only NeMo; zero `pyannote` imports in `inference/`.

#### 3.5 — Remove pyannote + HuggingFace Token Cleanup
- **Current state:** HF token references and pyannote dependencies may still exist in `requirements.txt`, `inference/main.py`, or settings UI.
- **What remains:** Audit and remove all pyannote/HF token artifacts from the codebase.
- **Files:** `inference/requirements*.txt`, `inference/main.py`, settings/credential stores
- **Acceptance:** `grep -ri "pyannote" inference/` returns zero matches. HF token no longer required for diarization.

#### WeSpeaker GPU endpoint deprecation
- **Current state:** `/diarize/wespeaker` returns HTTP 410 (Gone) — correctly deprecated.
- **What remains:** Confirm `WeSpeakerContainerizedDiarizationProvider.cs` (the GPU/container variant) is no longer registered or referenced. If the CPU variant (`WeSpeakerCpuDiarizationProvider`) is the sole active provider, the containerized one can be deleted.
- **Files:** `Services/WeSpeakerContainerizedDiarizationProvider.cs`, `Services/Registries/DiarizationRegistry.cs`
- **Acceptance:** Only `WeSpeakerCpuDiarizationProvider` registered; containerized variant deleted or confirmed dead.

---

### Tier 2 — Parakeet ASR Provider (Phase 4, ~1.5–2 weeks)

#### 4.1 — Python: Implement `POST /transcribe/parakeet` in `inference/main.py`
- **Current state:** Zero Parakeet references exist in the codebase.
- **What remains:** Full endpoint implementation using NeMo Parakeet-TDT-0.6B-v3 model. Accepts audio file, returns timed segments with timestamps.
- **Files:** `inference/main.py`
- **Dependencies:** `nemo-toolkit[asr]` already installed in GPU `.venv`. Model weights downloaded on first use.
- **Acceptance:** `POST /transcribe/parakeet` returns JSON matching the segment schema used by C# coordinator.

#### 4.2 — C#: Add `ParakeetTranscriptionProvider`
- **What remains:** New provider implementing `ITranscriptionProvider`. Registered in `TranscriptionRegistry` under `ProviderNames.Parakeet`.
- **Files:** `Services/ParakeetTranscriptionProvider.cs`, `Models/ProviderNames.cs`, `Services/Registries/TranscriptionRegistry.cs`
- **Acceptance:** Parakeet selectable in Transcription provider ComboBox, produces correct timed segments.

#### 4.3 — UI: Wire Parakeet as a transcription provider option
- **What remains:** Ensure the transcription provider ComboBox includes Parakeet when GPU profile is active.
- **Acceptance:** Parakeet visible and selectable in UI when GPU compute enabled.

---

### Tier 3 — Pipeline Streaming (Phase 6.2, ~2–3 weeks)

#### 6.2a — `Channel<T>`-Based Inter-Stage Overlap
- **Current state:** `SessionWorkflowCoordinator.Pipeline.cs` uses conventional `async`/`await` with `IProgress<double>`. No `System.Threading.Channels` usage. The staged Part 4 changes improved parallelism but did not introduce channel-based streaming.
- **What remains:** Replace the fully-sequential `Transcribe all → Translate all → TTS all` model with a `Channel<TranscriptSegment>` → `Channel<TranslatedSegment>` → `Channel<TtsResult>` pipeline. Each stage reads from the upstream channel and produces to the downstream channel, enabling overlap.
- **Files:** `Services/SessionWorkflowCoordinator.Pipeline.cs`, potentially new `Services/PipelineChannel*.cs` types
- **Acceptance:** Transcription segments begin flowing to translation before full transcription completes; translation segments begin flowing to TTS before full translation completes. Measurable reduction in end-to-end pipeline time.

---

### Tier 4 — Constructor Over-Injection (Phase 6.4, ~2–3 days)

#### 6.4 — Introduce Options Record / Builder for Coordinator
- **Current state:** Primary constructor has **18 parameters**. A legacy 10-parameter overload exists for backward compatibility.
- **What remains:** Create a `CoordinatorOptions` record (or `SessionWorkflowCoordinatorBuilder`) that bundles related services. Reduce constructor to ≤ 5 parameters.
- **Files:** `Services/SessionWorkflowCoordinator.cs` (~112–160), `App.axaml.cs` (composition root)
- **Acceptance:** Constructor has ≤ 5 parameters. All tests pass. No behavior change.

---

### Tier 5 — Open Questions / Low-Priority

#### 6.1b — Further ViewModel Decomposition
- **Current state:** `EmbeddedPlaybackViewModel` was split into `EmbeddedPlaybackPipelineViewModel` and `EmbeddedPlaybackSpeakerRoutingViewModel`. The main VM still handles playback controls, dub mode, and subtitle management.
- **What remains:** Assess whether `EmbeddedPlaybackViewModel` is now at an acceptable size after Part 4 decomposition. If still too large (> 800 lines), consider extracting `PlaybackControlsViewModel` and `SubtitleManagementViewModel`.
- **Acceptance:** Main VM under 800 lines, or team consensus that current decomposition is sufficient.

#### Parakeet for Non-European Languages
- **Scope:** Parakeet-TDT-0.6B-v3 excels at European languages. Determine whether Babel Player needs to support additional ASR models for other language families (e.g., WhisperX for multilingual, or language-specific models).
- **Status:** Deferred to future milestone after Phase 4 ships.

---

## Priority Matrix (Remaining Items Only)

| Item | Phase | Pipeline Impact | Effort | Risk | Dependency |
|---|---|---|---|---|---|
| 3.1 — Confirm NeMo-only `/diarize` | 3 | — | Trivial | None | None |
| 3.5 — Remove pyannote + HF token | 3 | — | Low | Low | 3.1 |
| WeSpeaker GPU deprecation cleanup | 3 | — | Low | Low | 3.5 |
| 4.1–4.3 — Parakeet ASR | 4 | ~10x ASR speed (EU langs) | 1.5–2 weeks | Medium | nemo-toolkit in .venv |
| 6.2a — Channel streaming pipeline | 6.2 | ~30–50% end-to-end | 2–3 weeks | Medium | None (standalone) |
| 6.4 — Constructor cleanup | 6.4 | — | 2–3 days | Low | None |
| 6.1b — Further VM decomposition | 6.1 | — | 1–2 days | Low | Part 4 decomposition |

---

## Execution Order

```
Tier 1 (pyannote removal)     → 2–3 days, no dependencies, cleans dead code
    ↓
Tier 2 (Parakeet ASR)         → 1.5–2 weeks, new capability, parallel with Tier 3
    ↓
Tier 3 (Channel streaming)    → 2–3 weeks, highest pipeline impact
    ↓
Tier 4 (Constructor cleanup)  → 2–3 days, quality-of-life refactor
    ↓
Tier 5 (VM decomposition Q)   → 1–2 days, assess after Part 4
```

Tiers 2 and 3 can proceed in parallel if resourced allows — they touch different layers (inference server vs. coordinator orchestration).

---

## Items Explicitly Closed (Verified April 12, 2026)

| Item | Phase | Resolution |
|---|---|---|
| 3.6 — UI wiring audit | 3 | Already done before tracker |
| 3.7 — Diarization ComboBox | 3 | ComboBox exists in `MainWindow.axaml` |
| 3.8 — Re-diarize command | 3 | `RunDiarizationOnlyCommand` exists |
| 3.9 — SpeakerId in segment row | 3 | Colored badge with `SpeakerIdToShortLabelConverter` |
| 3.4 — NeMo + WeSpeaker C# providers | 3 | Both registered in `DiarizationRegistry` |
| 6.5 — Clean shutdown | 6.5 | Part 4 (staged) |
| 6.3 — Qwen TTS batching | 6.3 | `/tts/qwen/batch` endpoint live |
| 6.1 (partial) — VM decomposition | 6.1 | Pipeline + speaker routing sub-VMs extracted |
| 5.2 — Python worker pool | 5.2 | `PythonJsonWorkerPool.cs` with Edge TTS + Piper workers |
| Phase 1 (Foundation) | 1 | All items resolved (1.0–1.6) |
| Phase 2 (TTS Quick Wins) | 2 | All items resolved (2.1–2.2) |
