> **Retired April 13, 2026.** Consolidated into `docs/Engineering-Plan.md`. This file is kept for history only.

# Babel-Player — Remaining Implementation Plan

**Derived from:** Milestones-Tracker-2026-04-08.md (April 8, 2026)  
**Updated:** April 15, 2026 — historical addendum: Parakeet work completed  
**Scope:** What is still unimplemented, ordered by priority and dependency

---

## What Has Been Completed Since the Tracker

| Item | Phase | Completed In |
|---|---|---|
| Clean shutdown (no `Environment.Exit`) | 6.5 | Part 4 (staged) |
| Qwen TTS batch endpoint (`/tts/qwen/batch`) | 6.3 | Part 4 (staged) |
| ViewModel decomposition (pipeline + speaker routing sub-VMs) | 6.1 | Part 4 (staged, partial — see 6.1b) |
| Persistent Python worker pool for Edge TTS / Piper | 5.2 | Part 4 (staged) |
| Streaming pipeline / inter-stage overlap | 6.2 | Part 4 (staged, partial) |
| Diarization UI (ComboBox, Re-diarize, SpeakerId badges) | 3.7–3.9 | Already resolved (pre-tracker) |
| Diarization NeMo + WeSpeaker C# providers + registry | 3.4 | Already resolved (pre-tracker) |
| **3.1 — NeMo-only `/diarize`** | 3 | **Verified April 13, 2026** |
| **3.5 — pyannote + HF token removal** | 3 | **Verified April 13, 2026** |
| **WeSpeaker GPU deprecation** | 3 | **Verified + file deleted April 13, 2026** |
| **4.1–4.3 — Parakeet ASR provider** | 4 | **Implemented April 15, 2026 (endpoint + provider + UI exposure)** |

---

## Remaining Work

> **Historical context note (April 15, 2026):** Tier 1 (6.4 constructor cleanup) and Tier 2 (6.1b ViewModel decomposition) were completed after this plan snapshot. Keep this file for chronology; use `docs/Engineering-Plan.md` for current status.

### Tier 1 — Constructor Cleanup (Phase 6.4, ~2–3 days)

#### 6.4 — Introduce Options Record for Coordinator
- **Current state:** `SessionWorkflowCoordinator.cs` primary constructor has **18 parameters** (8 required, 10 optional). No `CoordinatorOptions` record exists.
- **What remains:** Create a `CoordinatorOptions` record that bundles related services. Reduce constructor to ≤ 5 parameters. Update composition root in `App.axaml.cs`.
- **Files:** `Services/SessionWorkflowCoordinator.cs` (lines ~112–160), `App.axaml.cs`
- **Acceptance:** Constructor has ≤ 5 parameters. All 22 tests pass. No behavior change.
- **Do this first:** Both Parakeet and streaming will want to extend the coordinator. Cleaning the constructor now avoids doing it twice.

---

### Tier 2 — ViewModel Decomposition (Phase 6.1b, ~2–3 days)

#### 6.1b — Finish EmbeddedPlaybackViewModel Decomposition
- **Current state:** `EmbeddedPlaybackViewModel.cs` is **2,473 lines**. `EmbeddedPlaybackPipelineViewModel.cs` and `EmbeddedPlaybackSpeakerRoutingViewModel.cs` were created in Part 4 but the main file was not reduced — logic was added alongside the split files, not moved out of them.
- **What remains:** Actually move playback control, subtitle management, and dub mode logic out of the main VM into the existing sub-VMs or new focused VMs. Target: main VM under 800 lines.
- **Files:** `ViewModels/EmbeddedPlaybackViewModel.cs`, `ViewModels/EmbeddedPlaybackPipelineViewModel.cs`, `ViewModels/EmbeddedPlaybackSpeakerRoutingViewModel.cs`
- **Acceptance:** Main VM under 800 lines. All tests pass. No behavior change.
- **Do this before streaming:** Streaming will add coordinator-to-VM surface area. A 2,473-line VM makes that work dangerous.

---

### Tier 4 — Pipeline Streaming (Phase 6.2, ~2–3 weeks)

#### 6.2a — `Channel<T>`-Based Inter-Stage Overlap
- **Current state:** `SessionWorkflowCoordinator.Pipeline.cs` is fully sequential async/await. Zero `System.Threading.Channels` usage. The Part 4 "staged, partial" note in the tracker was not reflected in the actual code.
- **What remains:** Replace the `Transcribe all → Translate all → TTS all` sequential model with a `Channel<TranscriptSegment>` → `Channel<TranslatedSegment>` → `Channel<TtsResult>` pipeline. Each stage reads from the upstream channel and writes to the downstream, enabling overlap.
- **Files:** `Services/SessionWorkflowCoordinator.Pipeline.cs`, potentially new `Services/PipelineChannel*.cs` types
- **Acceptance:** Transcription segments begin flowing to translation before full transcription completes; translation segments flow to TTS before full translation completes. Measurable reduction in end-to-end wall time on a test video.

---

### Tier 5 — Open Questions / Low-Priority

#### Parakeet for Non-European Languages
- **Scope:** Parakeet-TDT-0.6B-v3 is strong on European languages. Decide whether Babel Player needs coverage for other language families (e.g., WhisperX for multilingual, or language-specific models).
- **Status:** Deferred until Phase 4 ships.

---

## Priority Matrix (Remaining Items Only)

| Item | Phase | Pipeline Impact | Effort | Risk | Dependency |
|---|---|---|---|---|---|
| 6.4 — Constructor cleanup | 6.4 | — | 2–3 days | Low | None |
| 6.1b — Finish VM decomposition | 6.1 | — | 2–3 days | Low | None |
| 6.2a — Channel streaming pipeline | 6.2 | ~30–50% end-to-end | 2–3 weeks | Medium | 6.4, 6.1b done |

---

## Execution Order

```
Tier 1 (constructor cleanup)   → 2–3 days, unblocks everything touching coordinator
    ↓
Tier 2 (VM decomposition)      → 2–3 days, reduces risk before streaming adds surface area
    ↓
Tier 4 (Channel streaming)     → 2–3 weeks, highest pipeline impact, last because highest risk
```

Parakeet (former Tier 3) is complete; streaming remains the major open item.

---

## Items Explicitly Closed

| Item | Phase | Resolution | Verified |
|---|---|---|---|
| 3.1 — NeMo-only `/diarize` | 3 | `_run_nemo_diarization()` → `ClusteringDiarizer`; zero pyannote imports | April 13, 2026 |
| 3.5 — pyannote + HF token removal | 3 | Zero pyannote/HF_TOKEN refs in `requirements.txt` and `main.py` | April 13, 2026 |
| WeSpeaker GPU deprecation | 3 | `WeSpeakerContainerizedDiarizationProvider.cs` deleted; only CPU variant registered | April 13, 2026 |
| 3.6 — UI wiring audit | 3 | Already done before tracker | Pre-tracker |
| 3.7 — Diarization ComboBox | 3 | ComboBox exists in `MainWindow.axaml` | Pre-tracker |
| 3.8 — Re-diarize command | 3 | `RunDiarizationOnlyCommand` exists | Pre-tracker |
| 3.9 — SpeakerId in segment row | 3 | Colored badge with `SpeakerIdToShortLabelConverter` | Pre-tracker |
| 3.4 — NeMo + WeSpeaker C# providers | 3 | Both registered in `DiarizationRegistry` | Pre-tracker |
| 6.5 — Clean shutdown | 6.5 | Part 4 (staged) | Pre-tracker |
| 6.3 — Qwen TTS batching | 6.3 | `/tts/qwen/batch` endpoint live | Pre-tracker |
| 5.2 — Python worker pool | 5.2 | `PythonJsonWorkerPool.cs` with Edge TTS + Piper workers | Pre-tracker |
| Phase 1 (Foundation) | 1 | All items resolved (1.0–1.6) | Pre-tracker |
| Phase 2 (TTS Quick Wins) | 2 | All items resolved (2.1–2.2) | Pre-tracker |
