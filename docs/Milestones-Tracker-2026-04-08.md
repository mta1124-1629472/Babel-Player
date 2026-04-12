# Babel-Player — Engineering Milestone Tracker

**Consolidated Implementation Plan: Codebase Health · Inference Performance · NeMo Provider Integrations**

Category: Engineering Report | Author: Michael | Date: April 7, 2026 (revised April 8, 2026) | Status: Active

---

## Project Context

Babel-Player is a local-first multilingual video dubbing application. Stack: C# / .NET 10 / Avalonia 12.0.0 / CommunityToolkit.Mvvm. The pipeline runs three inference stages — Transcription, Translation, and TTS — plus a Diarization stage for speaker identification, with provider registries per stage supporting CPU, GPU, and Cloud compute profiles. All local GPU/CPU inference runs through a single FastAPI server at `inference/main.py` inside a managed Python 3.12 `.venv`, with zero data leaving the machine on local profiles.

### Architecture Notes (verified April 8, 2026)

- **No Docker containers for inference.** All local providers (faster-whisper, NLLB/CTranslate2, Qwen TTS, NeMo diarization) run as endpoints in a single FastAPI process inside a managed `.venv`. The old milestone tracker incorrectly described these as separate Docker containers.
- **`nemo-toolkit[asr]==2.7.2`** is already installed in the GPU `.venv` and verified working with `torch 2.8.0` and Python 3.12.
- **Multi-speaker voice cloning is fully implemented.** `SessionWorkflowCoordinator.TtsReference.cs` extracts per-speaker reference clips via `/speakers/extract-reference`. `QwenContainerTtsProvider` resolves `reference_id` per segment. The diarization → speaker extraction → per-speaker TTS chain is complete and tested.
- **XTTS v2 is being removed** in a parallel cleanup effort. All XTTS references are legacy.
- **Edge TTS and Piper are the only subprocess-based providers.** Everything else runs in the persistent FastAPI server.

---

## Codebase Audit Findings (April 2026)

Following a comprehensive audit of the codebase, the following issues and opportunities have been identified across the system:

### 1. Code Gaps (missing implementations, incomplete features, unhandled edge cases)
- **Missing Speaker Labels in UI:** The segment list row template in `Views/MainWindow.axaml` does not render the `SpeakerId`. The data is present in the ViewModel, but multi-speaker dubbing users cannot visually map speakers to segments without clicking each one.
  - *Fix:* Add a `TextBlock` or colored badge bound to `SpeakerId` in the segment row template of `MainWindow.axaml` (Phase 3.9).
- **Diarization UI Controls Incomplete:** The UI still relies on a binary CheckBox for auto-speaker detection instead of a ComboBox for provider selection, and lacks a standalone "Re-diarize" command.
  - *Fix:* Replace CheckBox with ComboBox bound to `DiarizationProviderOptions` and add a `RunDiarizationOnlyCommand` to the Diarization panel (Phase 3.7 & 3.8).
- **Parakeet ASR Integration Missing:** The Parakeet endpoint and C# provider (Phase 4) have not been implemented, leaving faster-whisper as the sole local ASR option.
  - *Fix:* Implement `POST /transcribe/parakeet` in `inference/main.py` and the corresponding `ParakeetTranscriptionProvider` in C#.

### 2. Improper Code (anti-patterns, misuse of APIs, incorrect logic)
- **`async void` Anti-Pattern in Background Probes:** `Services/ContainerizedServiceProbe.cs` uses an `async void ObserveCompletionWithFaultHandling` method for fire-and-forget background task monitoring. While it catches exceptions, `async void` is an anti-pattern outside of event handlers and complicates lifecycle management.
  - *Fix:* Refactor to use the `FireAndForgetAsync` extension method introduced in Phase 1.2, or return a `Task` that the caller gracefully ignores using the helper.

### 3. Missing Opportunities (unused language/framework features, better abstractions)
- **Streaming Downloads for Cloud APIs:** ~~While `ContainerizedInferenceClient` correctly uses `ReadAsStreamAsync` (Phase 1.5), the cloud API clients (`Services/OpenAiApiClient.cs` and `Services/ElevenLabsApiClient.cs`) still use `ReadAsByteArrayAsync`. This buffers entire audio responses in memory before writing to disk.~~
  - ~~*Fix:* Update both cloud API clients to use `ReadAsStreamAsync` piped to `File.Create` via `CopyToAsync`.~~
  - *Status:* **Resolved.** `OpenAiApiClient.DownloadSpeechAsync` and `ElevenLabsApiClient.DownloadSpeechAsync` both use `HttpCompletionOption.ResponseHeadersRead`, `ReadAsStreamAsync`, and `CopyToAsync` to stream audio to disk without buffering the full response in memory.

### 4. Code Smells (dead code, duplication, excessive complexity, poor naming)
- **God Object ViewModel:** `ViewModels/EmbeddedPlaybackViewModel.cs` is ~1200 lines long and handles playback controls, pipeline execution, provider selection, diarization, dub mode, and subtitle management.
  - *Fix:* Decompose into focused sub-ViewModels (`PipelineConfigViewModel`, `MultiSpeakerViewModel`, etc.) composed within the main ViewModel (Phase 6.1).
- **Constructor Over-Injection:** `Services/SessionWorkflowCoordinator.cs` has a constructor with 14+ parameters, degrading readability and testability.
  - *Fix:* Introduce an options record or a builder/factory pattern for coordinator construction (Phase 6.4).
- **Forced Process Termination:** `App.axaml.cs` uses `Environment.Exit` on shutdown as a workaround for long-lived background threads (mpv event loop, bootstrap warmup) keeping the CLR alive.
  - *Fix:* Track background operations with `CancellationTokenSource` instances owned by the coordinator and cancel them cleanly in `Dispose()` (Phase 6.5).

### 5. Performance Bottlenecks (hot paths, unnecessary allocations, blocking calls)
- **Sequential Pipeline Blocking:** `AdvancePipelineAsync` executes strictly sequentially (Transcribe all -> Translate all -> TTS all). Each stage waits for the previous to fully complete, leaving inference hardware idle.
  - *Fix:* Implement a streaming pipeline using `System.Threading.Channels.Channel<T>` for inter-stage overlap (Phase 6.2).
- **N+1 Subprocess Overhead:** `EdgeTtsProvider` and `PiperTtsProvider` spawn a new Python interpreter process per segment, incurring massive startup overhead for long videos.
  - *Fix:* Implement a persistent Python worker pool communicating over stdin/stdout JSON-RPC (Phase 5.2).

### 6. Optimizations (caching opportunities, lazy loading, batch operations)
- **Server-Side Batching for GPU TTS:** Each Qwen TTS segment sends an individual HTTP request, resulting in poor GPU utilization due to sequential processing overhead.
  - *Fix:* Add a batch endpoint (`POST /tts/qwen/batch`) to group segments into batches (e.g., 4-8), drastically improving GPU throughput (Phase 6.3).

### 7. Refactors Needed (tight coupling, poor separation of concerns)
- **Hardcoded Placeholder Exceptions:** Multiple providers and tests throw `NotImplementedException` with the string "PLACEHOLDER". While this satisfies the architectural linter, the underlying architecture for combined generation remains tightly coupled to the coordinator rather than cleanly abstracted.
  - *Fix:* Abstract audio aggregation to a dedicated pipeline stage and remove `NotImplementedException` stubs from provider implementations.

---

## Phase 1: Foundation Stabilization ✅

Low-risk fixes that eliminate silent bugs and reduce risk before larger changes. Estimated effort: 3–5 days. **Status: Complete.**

### 1.0 — Upgrade to Avalonia 12.0.0 Stable
- **Status:** **Resolved.** `BabelPlayer.csproj` targets `Avalonia 12.0.0`.

### 1.1 — Fix \_mediaSnapshotCache Thread Safety
- **Status:** **Resolved.** `_mediaSnapshotCache` is now a `ConcurrentDictionary`.

### 1.2 — Create Fire-and-Forget Async Helper
- **Status:** **Resolved.** Implemented in `Services/TaskExtensions.cs`.

### 1.3 — Extract Duplicated Startup Code
- **Status:** **Resolved.** Refactored in `App.axaml.cs`.

### 1.4 — Fix Hardcoded Language Fallback
- **Status:** **Resolved.** Explicit checks added to `SessionWorkflowCoordinator.Pipeline.cs`.

### 1.5 — Stream TTS Audio Downloads
- **Status:** **Resolved.** Implemented for `ContainerizedInferenceClient.cs`, `OpenAiApiClient.cs`, and `ElevenLabsApiClient.cs` using `ReadAsStreamAsync` + `CopyToAsync`.

### 1.6 — Reuse HttpClient for Cloud Providers
- **Status:** **Resolved.** `HttpClient` is now reused in cloud provider instances.

---

## Phase 2: TTS Performance Quick Wins ✅

Direct inference time reductions with minimal architectural change. Estimated effort: 3–5 days. **Status: Complete. Merged to main.**

### 2.1 — Eliminate Double TTS Synthesis
- **Status:** **Resolved.** Sequential synthesis bottleneck removed; purely I/O concatenation remains.

### 2.2 — Provider-Aware Parallelism Cap
- **Status:** **Resolved.** `MaxConcurrency` property implemented and utilized by the coordinator.

---

## Phase 3: Diarization Provider Overhaul (Still Open)

Replace pyannote with NeMo ClusteringDiarizer (GPU) and WeSpeaker (CPU fallback). Eliminate HuggingFace token friction. Fix two UI gaps found during wiring audit. 

### 3.1 — Python: Replace pyannote `/diarize` with NeMo ClusteringDiarizer
- **Status:** **Still Open.** 

### 3.2 — Python: Add WeSpeaker `/diarize/wespeaker` Endpoint
- **Status:** **Still Open.**

### 3.3 — Python: Update `/capabilities`
- **Status:** **Still Open.**

### 3.4 — C#: Add NeMo and WeSpeaker Diarization Providers
- **Status:** **Still Open.**

### 3.5 — Remove pyannote + HuggingFace Token Cleanup
- **Status:** **Still Open.**

### 3.6 — UI Wiring Audit → DONE
- **Status:** **Resolved.**

### 3.7 — UI: Replace Diarization CheckBox with Provider ComboBox
- **Status:** **Still Open** (Flagged in April 2026 Audit).

### 3.8 — UI: Add Standalone "Re-diarize" Command
- **Status:** **Still Open** (Flagged in April 2026 Audit).

### 3.9 — UI: Show SpeakerId in Segment Row Template
- **Status:** **Still Open** (Flagged in April 2026 Audit).

---

## Phase 4: ASR Provider Expansion (NeMo Parakeet) (Still Open)

Add Parakeet-TDT-0.6B-v3 as a high-performance ASR option for European languages. Estimated effort: 1.5–2 weeks.

- **Status:** **Still Open** (Flagged in April 2026 Audit).

---

## Phase 5: Subprocess Provider Polish (Still Open / Backlog)

Process lifecycle improvements for the only two subprocess-based providers. Estimated effort: 3–5 days.

### 5.1 — Batch Python Scripts for Edge TTS and Piper
- **Status:** **Still Open.**

### 5.2 — Persistent Python Worker Pool
- **Status:** **Still Open.**

---

## Phase 6: Long-Term Architectural Refactors (Still Open)

Major structural changes that benefit from all prior stabilization. Estimated effort: 4–8 weeks total, can be staggered.

### 6.1 — Decompose EmbeddedPlaybackViewModel
- **Status:** **Still Open** (Flagged in April 2026 Audit).

### 6.2 — Streaming Pipeline with Inter-Stage Overlap
- **Status:** **Still Open** (Flagged in April 2026 Audit).

### 6.3 — Server-Side Batching for Qwen TTS
- **Status:** **Still Open** (Flagged in April 2026 Audit).

### 6.4 — Constructor Overload Cleanup
- **Status:** **Still Open** (Flagged in April 2026 Audit).

### 6.5 — Clean Shutdown (Replace Environment.Exit)
- **Status:** **Still Open** (Flagged in April 2026 Audit).

---

## Appendix A: Priority Impact Matrix (REVISED)

| Optimization | TTS Speedup | Pipeline Speedup | Effort | Risk | Phase |
|---|---|---|---|---|---|
| Fix thread safety | — | — | Trivial | None | 1 ✅ |
| Fire-and-forget helper | — | — | Low | Low | 1 ✅ |
| Extract startup code | — | — | Low | None | 1 ✅ |
| Fix "es" fallback | — | — | Trivial | None | 1 ✅ |
| Stream downloads | ~5% | ~2% | Trivial | None | 1 ✅ |
| Reuse HttpClient | ~10% (cloud) | ~4% | Trivial | None | 1 ✅ |
| Eliminate double synthesis | ~50% | ~20% | Low | Low | 2 ✅ |
| Provider-aware parallelism | ~30% (cloud) | ~10% | Very low | Low | 2 ✅ |
| ~~Diarization registry fix~~ | — | — | ~~Low~~ | ~~Low~~ | ~~3~~ (already resolved) |
| NeMo diarization endpoint | — | — | Low-med | Low | 3 |
| WeSpeaker endpoint | — | — | Low | Low | 3 |
| NeMo + WeSpeaker C# providers | — | — | Low-med | Low | 3 |
| pyannote + HF token removal | — | — | Low | Low | 3 |
| UI wiring audit | — | — | Low-med | Low | 3 |
| Parakeet endpoint | ~10x ASR | ~15% | Low-med | Medium | 4 |
| Parakeet C# provider | — | — | Low | Low | 4 |
| Batch Python scripts | ~20% (Edge/Piper) | ~8% | Medium | Low | 5 (backlog) |
| Persistent Python pool | ~25% (Edge/Piper) | ~10% | Med-high | Medium | 5 (backlog) |
| Decompose ViewModel | — | — | Medium | Low | 6 |
| Streaming pipeline | — | ~30-50% | High | Medium | 6 |
| Server-side batching | ~40-60% (GPU) | ~15% | High | Medium | 6 |
| Constructor cleanup | — | — | Low | Low | 6 |
| Clean shutdown | — | — | Medium | Low | 6 |

---

## Appendix B: Local vs Cloud Provider Map (REVISED — verified April 8, 2026)

| Stage | Provider | Compute Profile | Deployment | Network | Token Required |
|---|---|---|---|---|---|
| ASR | faster-whisper | CPU / GPU | `.venv` FastAPI | localhost only | No |
| ASR | Parakeet-TDT (Phase 4) | GPU | `.venv` FastAPI | localhost only | No |
| Translation | NLLB / CTranslate2 | CPU / GPU | `.venv` FastAPI | localhost only | No |
| TTS | Qwen TTS | GPU | `.venv` FastAPI | localhost only | No |
| TTS | Piper | CPU | Subprocess | None | No |
| TTS | ~~XTTS v2~~ | ~~GPU~~ | ~~.venv FastAPI~~ | ~~localhost only~~ | ~~No~~ (removing) |
| TTS | Edge TTS | Cloud | Subprocess | Microsoft servers | No |
| TTS | OpenAI TTS | Cloud | HTTP client | OpenAI API | API key |
| TTS | ElevenLabs | Cloud | HTTP client | ElevenLabs API | API key |
| Diarization | ~~pyannote~~ | ~~CPU / GPU~~ | ~~Subprocess~~ | ~~None~~ | ~~HF token~~ (removing) |
| Diarization | NeMo ClusteringDiarizer (Phase 3) | GPU | `.venv` FastAPI | localhost only | No |
| Diarization | WeSpeaker (Phase 3) | CPU | `.venv` FastAPI | localhost only | No |

**8 of 10 active providers** run in a single persistent FastAPI process. Only Edge TTS and Piper use subprocesses.