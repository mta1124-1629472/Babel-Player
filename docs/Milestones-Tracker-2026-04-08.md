I'll extract the full tracker, then rewrite Phases 3–5 grounded in what we've verified.



Here's the full updated tracker. Phases 1–2 and Phase 6 are unchanged (status notes added). Phases 3–5, Appendix A, and Appendix B are rewritten from verified architecture.



\---



\# Babel-Player — Engineering Milestone Tracker



\*\*Consolidated Implementation Plan: Codebase Health · Inference Performance · NeMo Provider Integrations\*\*



Category: Engineering Report | Author: Michael | Date: April 7, 2026 (revised April 8, 2026) | Status: Active



\---



\## Project Context



Babel-Player is a local-first multilingual video dubbing application. Stack: C# / .NET 10 / Avalonia 12.0.0 / CommunityToolkit.Mvvm. The pipeline runs three inference stages — Transcription, Translation, and TTS — plus a Diarization stage for speaker identification, with provider registries per stage supporting CPU, GPU, and Cloud compute profiles. All local GPU/CPU inference runs through a single FastAPI server at `inference/main.py` inside a managed Python 3.12 `.venv`, with zero data leaving the machine on local profiles.



\### Architecture Notes (verified April 8, 2026)



\- \*\*No Docker containers for inference.\*\* All local providers (faster-whisper, NLLB/CTranslate2, Qwen TTS, NeMo diarization) run as endpoints in a single FastAPI process inside a managed `.venv`. The old milestone tracker incorrectly described these as separate Docker containers.

\- \*\*`nemo-toolkit\[asr]==2.7.2`\*\* is already installed in the GPU `.venv` and verified working with `torch 2.8.0` and Python 3.12.

\- \*\*Multi-speaker voice cloning is fully implemented.\*\* `SessionWorkflowCoordinator.TtsReference.cs` extracts per-speaker reference clips via `/speakers/extract-reference`. `QwenContainerTtsProvider` resolves `reference\_id` per segment. The diarization → speaker extraction → per-speaker TTS chain is complete and tested.

\- \*\*XTTS v2 is being removed\*\* in a parallel cleanup effort. All XTTS references are legacy.

\- \*\*Edge TTS and Piper are the only subprocess-based providers.\*\* Everything else runs in the persistent FastAPI server.



\---



\## Phase 1: Foundation Stabilization ✅



Low-risk fixes that eliminate silent bugs and reduce risk before larger changes. Estimated effort: 3–5 days. \*\*Status: Complete.\*\*



\### 1.0 — Upgrade to Avalonia 12.0.0 Stable



\*\*DO FIRST\*\* — Establishes a stable API surface before all other work.



\- \*\*Problem:\*\* The project targets Avalonia 12.0.0-rc1. Avalonia 12.0.0 stable was released on April 7, 2026 with fixes since rc1: CompositionAnimation fixes, accessibility backend fixes (Linux AT-SPI, macOS), TextPresenter measurement with trailing whitespace, WindowState now a direct property with reliable values, and focus/access key fixes. The AvaloniaUI.DiagnosticsSupport hang noted in the codebase may be resolved.

\- \*\*Fix:\*\* Bump all Avalonia package references from 12.0.0-rc1 to 12.0.0 in BabelPlayer.csproj. Re-enable AvaloniaUI.DiagnosticsSupport and test. Review the breaking changes doc (WindowState is now a direct property — check any code that observes WindowState changes).

\- \*\*Risk:\*\* Low. rc1 to stable is a patch-level change. The breaking changes between rc1 and rc2 (WindowState direct property) should be verified.

\- \*\*Files:\*\* BabelPlayer.csproj, any code referencing WindowState



\### 1.1 — Fix \\\_mediaSnapshotCache Thread Safety



\- \*\*Problem:\*\* `\_mediaSnapshotCache` is a plain `Dictionary<string, WorkflowSessionSnapshot>` accessed from multiple threads (UI thread via Initialize/LoadMedia, background Task.Run via ApplyBootstrapWarmupData, CacheMediaSnapshot). Concurrent modification will corrupt state or throw.

\- \*\*Fix:\*\* Replace with `ConcurrentDictionary<string, WorkflowSessionSnapshot>`. The project already uses ConcurrentDictionary for TTS segment paths, so the pattern is established.

\- \*\*Risk:\*\* None. Drop-in replacement.

\- \*\*Files:\*\* SessionWorkflowCoordinator.cs



\### 1.2 — Create Fire-and-Forget Async Helper



\- \*\*Problem:\*\* Multiple `\_ = SomeAsyncMethod()` call sites (RefreshSegmentsAsync, SeekAndPlayAsync, PlayTtsForSegmentAsync, RefreshProviderReadinessStatusesAsync). These rely on the global OnUnobservedTaskException handler, which marks them observed and shows a crash dialog — but the call site has zero awareness of failure. No status update, no retry, no user-facing message.

\- \*\*Fix:\*\* Create a `FireAndForget(Task task, string context)` helper that logs exceptions via AppLog and optionally updates StatusText. Replace all `\_ =` fire-and-forget patterns with this helper.

\- \*\*Risk:\*\* Low. Improves observability of all subsequent work.

\- \*\*Files:\*\* New utility class + all call sites in EmbeddedPlaybackViewModel.cs, SessionWorkflowCoordinator.\\\*.cs



\### 1.3 — Extract Duplicated Startup Code



\- \*\*Problem:\*\* The coordinator construction block in App.axaml.cs (\~40 lines) is copy-pasted into the catch handler. Both paths construct the full coordinator with all dependencies. If a probe, registry, or manager constructor changes, both paths must be updated.

\- \*\*Fix:\*\* Extract a `CreateCoordinator()` factory method. Call it in the try block; call it again in catch with a flag to skip the component that failed.

\- \*\*Risk:\*\* None.

\- \*\*Files:\*\* App.axaml.cs



\### 1.4 — Fix Hardcoded Language Fallback



\- \*\*Problem:\*\* In RegenerateSegmentTranslationAsync: `var sourceLanguage = CurrentSession.SourceLanguage ?? "es";` — Spanish as a default source language is arbitrary. If a session has no source language recorded, this silently picks Spanish.

\- \*\*Fix:\*\* Throw or block with a clear message if SourceLanguage is null at this point. The transcription step should always set it. A null here indicates a state machine violation.

\- \*\*Risk:\*\* None. Converts a silent bug into an explicit error.

\- \*\*Files:\*\* SessionWorkflowCoordinator.Pipeline.cs



\### 1.5 — Stream TTS Audio Downloads



\- \*\*Problem:\*\* DownloadTtsAudioAsync in ContainerizedInferenceClient buffers entire response in memory via ReadAsByteArrayAsync before writing to disk. For large segments or combined audio, this creates unnecessary memory pressure.

\- \*\*Fix:\*\* Replace with ReadAsStreamAsync piped to File.Create via CopyToAsync. Two-line change.

\- \*\*Risk:\*\* None.

\- \*\*Files:\*\* ContainerizedInferenceClient.cs



\### 1.6 — Reuse HttpClient for Cloud Providers



\- \*\*Problem:\*\* OpenAiTtsProvider and ElevenLabsTtsProvider call `using var client = \_clientFactory();` inside every GenerateSegmentTtsAsync call. The `using` disposes the client after each segment, paying TCP connection setup + TLS handshake per segment.

\- \*\*Fix:\*\* Create the API client once in the provider constructor (or lazily on first use) and reuse it across all segment calls within a session. HttpClient is designed to be long-lived.

\- \*\*Risk:\*\* Very low. Standard HttpClient usage pattern.

\- \*\*Files:\*\* OpenAiTtsProvider.cs, ElevenLabsTtsProvider.cs



\---



\## Phase 2: TTS Performance Quick Wins ✅



Direct inference time reductions with minimal architectural change. Estimated effort: 3–5 days. \*\*Status: Complete. Merged to main.\*\*



\### 2.1 — Eliminate Double TTS Synthesis



\*\*HIGHEST PRIORITY — Single biggest performance win available.\*\*



\- \*\*Problem:\*\* GenerateTtsAsync in SessionWorkflowCoordinator.Pipeline.cs synthesized every segment twice. Pass 1 called `\_ttsService.GenerateTtsAsync()` to produce the combined audio file — for XTTS and Qwen this iterated through every segment in a sequential foreach, synthesized each one, downloaded it, then ffmpeg-concatenated results. Pass 2 called `Parallel.ForEachAsync` over the same segments to produce per-segment clips. That was 2x the inference cost.

\- \*\*Fix:\*\* Flipped the order. Generate per-segment clips first (already parallelized), then produce the combined audio by concatenating the segment files via `AudioConcatUtility.CombineAudioSegmentsAsync`. The combined pass is now pure I/O — zero additional inference.

\- \*\*Impact:\*\* \~50% TTS wall-clock time reduction. Eliminated the sequential foreach bottleneck entirely.

\- \*\*Files:\*\* SessionWorkflowCoordinator.Pipeline.cs



\### 2.2 — Provider-Aware Parallelism Cap



\- \*\*Problem:\*\* Per-segment `Parallel.ForEachAsync` was capped at `Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2))`. Reasonable for local single-GPU inference, but left throughput on the table for cloud providers (network-bound, not compute-bound — could safely run 8–16 concurrent requests).

\- \*\*Fix:\*\* Added a `MaxConcurrency` property to `ITtsProvider`. Cloud providers override to return higher values. The coordinator uses `Math.Max(1, Math.Min(\_ttsService.MaxConcurrency, candidateSegments.Count))` instead of the hardcoded cap.

\- \*\*Impact:\*\* \~30% TTS speedup for cloud providers (OpenAI, ElevenLabs). No change for local providers.

\- \*\*Files:\*\* ITtsProvider.cs, OpenAiTtsProvider.cs, ElevenLabsTtsProvider.cs, SessionWorkflowCoordinator.Pipeline.cs



\---



Good data — two real gaps, five confirmed chains, and a UI control that needs replacing. Let me fold all of this into the Phase 3 section.



Here's the revised Phase 3 with the audit findings folded in as concrete work items instead of hypothetical checks:



\---



\## Phase 3: Diarization Provider Overhaul (REVISED — post-audit)



Replace pyannote with NeMo ClusteringDiarizer (GPU) and WeSpeaker (CPU fallback). Eliminate HuggingFace token friction. Fix two UI gaps found during wiring audit. 



\*\*UI wiring audit results (verified April 8, 2026):\*\*



| # | Chain | Status | Action |

|---|---|---|---|

| 1 | Diarization provider selector | ✅ Wired | Replace CheckBox with ComboBox (see 3.7) |

| 2 | Min/Max Speakers | ✅ Wired | No changes needed |

| 3 | "Run Diarization" trigger | ⚠️ Pipeline-only | Add standalone re-diarize command (see 3.8) |

| 4 | Speaker label in segment list | ❌ Gap | Add SpeakerId to segment row template (see 3.9) |

| 5 | Speaker reference extraction | ✅ Automatic | No changes needed |

| 6 | Per-speaker TTS voice assignment | ✅ End-to-end | No changes needed |

| 7 | Provider readiness indicators | ✅ Wired | No changes needed — `RefreshAutoSpeakerDetectionStatus()` already calls `DiarizationRegistry.CheckReadiness()` |



\### 3.1 — Python: Replace pyannote `/diarize` with NeMo ClusteringDiarizer



\- \*\*Problem:\*\* Two separate pyannote code paths exist: (1) `PyannoteDiarizationProvider.cs` spawns inline Python via `PythonSubprocessServiceBase`, (2) `main.py` has a `/diarize` POST endpoint that calls pyannote. Both require a HuggingFace token.

\- \*\*Fix:\*\* Remove all pyannote code from `main.py`. Replace the `/diarize` endpoint with NeMo `ClusteringDiarizer`:

&#x20; - VAD model: `vad\_multilingual\_marblenet`

&#x20; - Speaker embedding model: `titanet\_large`

&#x20; - Clustering: multi-scale with tunable `scale\_dict`

&#x20; - NeMo diarization is file-based: write a manifest JSON → call `diarizer.diarize()` → parse output RTTM → convert to `DiarizationResponse` segments

&#x20; - Wrap `diarize()` with `await asyncio.to\_thread(...)` — it's synchronous and GPU-heavy

&#x20; - Use `tempfile.TemporaryDirectory()` for manifest and RTTM I/O

&#x20; - Remove `hf\_token` from Form parameters entirely — NeMo models download from NGC without authentication

&#x20; - Preserve the existing response contract exactly (`DiarizationResponse` with `segments: list\[DiarizationSegment]` and `speaker\_count: int`)

&#x20; - Models download from NGC on first call (\~500MB total). Log a startup message about this.

\- \*\*Do NOT touch:\*\* `/speakers/extract-reference` endpoint (engine-agnostic), `/tts/\*` endpoints, `/transcribe`, `/translate`, `qwen\_reference\_registry`

\- \*\*Risk:\*\* Low-medium. NeMo is already installed and verified.

\- \*\*Files:\*\* `inference/main.py`



\### 3.2 — Python: Add WeSpeaker `/diarize/wespeaker` Endpoint



\- \*\*Purpose:\*\* Lightweight CPU fallback diarization. Lower accuracy (DER \~12–16%) but zero GPU requirement.

\- \*\*Implementation:\*\*

&#x20; - Add `wespeaker` to `inference/gpu-requirements.txt` (it supports CPU-only execution)

&#x20; - New endpoint: `POST /diarize/wespeaker` accepting audio file upload

&#x20; - Parse WeSpeaker output into the same `DiarizationResponse` contract

&#x20; - Force CPU execution: `model.to("cpu")` regardless of GPU availability

\- \*\*License:\*\* Apache 2.0. Fully AGPL-compatible.

\- \*\*Files:\*\* `inference/main.py`, `inference/gpu-requirements.txt`



\### 3.3 — Python: Update `/capabilities`



```python

"diarization\_engines": \["nemo", "wespeaker"],

"hf\_token\_required": false,

"diarization\_default": "nemo"

```



\### 3.4 — C#: Add NeMo and WeSpeaker Diarization Providers



\- \*\*NemoContainerizedDiarizationProvider (new file):\*\* Follow the same pattern as `QwenContainerTtsProvider`. Takes `ContainerizedInferenceClient` in constructor. Implements `IDiarizationProvider`. POSTs audio to `/diarize`. No API key required.

\- \*\*WeSpeakerContainerizedDiarizationProvider (new file):\*\* Same pattern, hits `/diarize/wespeaker`. Forces CPU compute profile.

\- \*\*Add `DiarizeAsync` to `ContainerizedInferenceClient`\*\* if it doesn't exist: multipart POST with `audio` (file), `min\_speakers` (optional), `max\_speakers` (optional).

\- \*\*Update `DiarizationRegistry`:\*\* Remove `PyannoteLocal` descriptor, add `NemoLocal` and `WeSpeakerLocal`. Update `CreateProvider()` switch. Registry constructor accepts `ContainerizedInferenceClient` (follow TtsRegistry pattern).

\- \*\*Update `InferenceRuntimeCatalog`:\*\* NeMo → `ComputeProfile.ContainerGpu`, WeSpeaker → CPU profile.

\- \*\*Update `ContainerizedProviderReadiness`:\*\* Add diarization capability check against `/capabilities`.

\- \*\*Update `AppSettings`:\*\* Default `DiarizationProvider` from `PyannoteLocal` to `NemoLocal`.

\- \*\*Speaker ID normalization:\*\* Both providers must produce `"spk\_NN"` format matching what `PyannoteDiarizationProvider.NormaliseSpeakerId` produced, so downstream multi-speaker voice cloning works unchanged.

\- \*\*Files:\*\* New provider classes, DiarizationRegistry.cs, InferenceRuntimeCatalog.cs, ContainerizedProviderReadiness.cs, ContainerizedInferenceClient.cs, AppSettings.cs



\### 3.5 — Remove pyannote + HuggingFace Token Cleanup



\*\*Delete:\*\*

\- `Services/PyannoteDiarizationProvider.cs` — entire file

\- All pyannote imports and handler code in `inference/main.py`

\- `pyannote.audio` from requirements files (if present)



\*\*Remove HuggingFace token from diarization path (search all):\*\*

\- `DiarizationHuggingFaceToken` in AppSettings

\- `CredentialKeys.HuggingFace` — if only used by diarization, remove entirely

\- `hf\_token` / `HF\_TOKEN` references in diarization code

\- HuggingFace token hint in `MainWindow.axaml` (DIARIZATION / SPEAKERS panel)

\- HuggingFace entry in `ApiKeysDialog` — if only used by diarization, remove

\- `DiarizationRequest.HuggingFaceToken` property



\*\*Update tests:\*\*

\- Remove/update all tests referencing Pyannote, pyannote, HuggingFace, hf\_token

\- Add unit tests for new providers following `ContainerizedProvidersTests.cs` patterns



\### 3.6 — \~\~UI Wiring Audit\~\~ → DONE (results above)



All seven chains were traced on April 8, 2026. Five are fully wired and require no changes. Three items below (3.7–3.9) address the gaps found.



\### 3.7 — UI: Replace Diarization CheckBox with Provider ComboBox



\- \*\*Problem:\*\* The current UI exposes diarization as a single `IsAutoSpeakerDetectionEnabled` CheckBox (line 293 in `MainWindow.axaml`). `OnIsAutoSpeakerDetectionEnabledChanged()` hardcodes `DiarizationProvider = ProviderNames.PyannoteLocal` when checked. The ViewModel has `DiarizationProviderOptions` list and a `\_diarizationProvider` field, but there is \*\*no ComboBox in the XAML\*\* — the CheckBox is the sole control. With two providers (NeMo + WeSpeaker), a binary toggle no longer works.

\- \*\*Fix:\*\*

&#x20; 1. Replace the CheckBox with a ComboBox bound to `DiarizationProviderOptions` (already populated by the ViewModel from `DiarizationRegistry.GetAvailableProviders()`), with an additional "Off" / empty-string item at the top.

&#x20; 2. Selected item writes to `DiarizationProvider` — the existing `OnDiarizationProviderChanged()` handler already writes to `CurrentSettings.DiarizationProvider` and calls `NotifySettingsModified()`.

&#x20; 3. Remove `IsAutoSpeakerDetectionEnabled` property and its change handler — the ComboBox selection replaces it. "Off" = no provider = no diarization.

&#x20; 4. Update `RefreshAutoSpeakerDetectionStatus()` — it currently checks `IsAutoSpeakerDetectionEnabled`. Change it to check `!string.IsNullOrEmpty(DiarizationProvider)`.

&#x20; 5. The readiness indicator (`AutoSpeakerDetectionStatus`, lines 329–333) already calls `DiarizationRegistry.CheckReadiness()` — this chain will work automatically once the registry returns NeMo/WeSpeaker providers.

\- \*\*XAML location:\*\* Left panel, DIARIZATION / SPEAKERS section, \~line 293

\- \*\*Files:\*\* `MainWindow.axaml`, `EmbeddedPlaybackViewModel.cs`



\### 3.8 — UI: Add Standalone "Re-diarize" Command



\- \*\*Problem:\*\* Diarization can only run as part of the full pipeline via ▶ Run Pipeline → `TranscribeMediaAsync` → `RunDiarizationAsync`. There is no way to re-run diarization (e.g., after changing Min/Max Speakers) without re-running transcription. For a 10-minute video, transcription takes 30–120 seconds. Re-running it just to tweak speaker count is wasteful.

\- \*\*Fix:\*\*

&#x20; 1. Add a `RunDiarizationOnlyCommand` (or "Re-diarize" button) in the DIARIZATION / SPEAKERS panel that calls `RunDiarizationAsync` directly on the coordinator.

&#x20; 2. Guard: only enabled when `CurrentSession.WorkflowStage >= SessionWorkflowStage.Transcribed` (transcription has already run) AND a diarization provider is selected.

&#x20; 3. On completion, re-run `EnsureMultiSpeakerReferenceClipsAsync` to re-extract speaker reference clips for the new speaker assignment.

&#x20; 4. Clear any previously generated TTS audio (speaker assignments changed → TTS segments are stale). Set `CurrentSession.WorkflowStage` back to `Translated` to force TTS re-generation on next pipeline run.

\- \*\*Impact:\*\* Lets users iterate on speaker count and diarization parameters without re-transcribing. Critical UX improvement for multi-speaker workflows.

\- \*\*Files:\*\* `EmbeddedPlaybackViewModel.cs`, `MainWindow.axaml`, potentially `SessionWorkflowCoordinator.Playback.cs`



\### 3.9 — UI: Show SpeakerId in Segment Row Template



\- \*\*Problem:\*\* `WorkflowSegmentState.SpeakerId` is correctly populated after diarization, but the segment list row template (XAML lines 883–925) only renders time range, source text, translated text, and the green/orange status dot. A user with multi-speaker dubbing cannot see which speaker maps to which row without clicking through segments one at a time in the left panel.

\- \*\*Fix:\*\*

&#x20; 1. Add a small `TextBlock` or colored badge showing `SpeakerId` (e.g., "S0", "S1") in the segment row template, left of the time range.

&#x20; 2. Only visible when `MultiSpeakerEnabled` is true (use `IsVisible` binding).

&#x20; 3. Optional: assign a stable color per speaker ID (e.g., `spk\_0` = blue, `spk\_1` = green, `spk\_2` = orange) via a value converter. Keep it to 6–8 distinct colors with a fallback gray.

\- \*\*Impact:\*\* Speaker assignment becomes scannable at a glance instead of requiring per-segment inspection.

\- \*\*Files:\*\* `MainWindow.axaml` (segment row template, \~lines 883–925), possibly a new `SpeakerIdToColorConverter`



\### Verification



1\. `dotnet build BabelPlayer.sln` — zero errors

2\. `dotnet test BabelPlayer.sln` — no new failures beyond 5 pre-existing

3\. `DiarizationRegistry.GetAvailableProviders()` returns NeMo and WeSpeaker (no Pyannote)

4\. Zero references to `pyannote`, `PyannoteLocal`, or `DiarizationHuggingFaceToken` in codebase

5\. `/capabilities` reports `"diarization\_engines": \["nemo", "wespeaker"]` and `"hf\_token\_required": false`

6\. Diarization ComboBox shows "Off", "NeMo (Local GPU)", "WeSpeaker (Local CPU)" — selecting each writes to `CurrentSettings.DiarizationProvider`

7\. "Re-diarize" button is disabled before transcription runs, enabled after, and correctly re-runs diarization without re-transcribing

8\. Segment rows show speaker badges (S0, S1, etc.) when multi-speaker mode is active, hidden when not



\### Commit messages



```

3.1: Add NeMo ClusteringDiarizer endpoint — replace pyannote GPU diarization

3.2: Add WeSpeaker endpoint — CPU fallback diarization

3.3: C# NeMo + WeSpeaker providers, registry, readiness checks

3.4: Remove pyannote and HuggingFace token from diarization path

3.5: Replace diarization CheckBox with provider ComboBox

3.6: Add standalone re-diarize command

3.7: Show speaker ID badges in segment row template

```

\---



\## Phase 4: ASR Provider Expansion (NeMo Parakeet) (REVISED)



Add Parakeet-TDT-0.6B-v3 as a high-performance ASR option for European languages. Estimated effort: 1.5–2 weeks.



\*\*Key architectural correction:\*\* No Docker container to build. Parakeet runs as a new endpoint in the existing FastAPI server at `inference/main.py`. `nemo-toolkit\[asr]` is already installed in the `.venv`.



\### 4.1 — Python: Add Parakeet `/transcribe/parakeet` Endpoint



\- \*\*Implementation:\*\*

&#x20; - Add endpoint to `inference/main.py`: `POST /transcribe/parakeet` accepting audio file upload

&#x20; - Load `parakeet-tdt-0.6b-v3` via `nemo.collections.asr.models.ASRModel.from\_pretrained("nvidia/parakeet-tdt-0.6b-v3")`

&#x20; - Model downloads from NGC on first call (\~1.2GB). No authentication required.

&#x20; - Return JSON segments with timestamps in the same contract as the existing `/transcribe` endpoint (faster-whisper)

&#x20; - Wrap inference with `await asyncio.to\_thread(...)` — it's synchronous and GPU-heavy

\- \*\*Update `/capabilities`:\*\* Add `"transcription\_engines": \["faster-whisper", "parakeet"]`

\- \*\*License:\*\* CC-BY-4.0. Compatible with AGPL-3.0.

\- \*\*Files:\*\* `inference/main.py`



\### 4.2 — C#: Add ParakeetTranscriptionProvider



\- \*\*Implementation:\*\* Follow `ContainerizedTranscriptionProvider` pattern. POSTs audio to `/transcribe/parakeet` via `ContainerizedInferenceClient`.

\- \*\*Wire into `InferenceRuntimeCatalog`\*\* with `ComputeProfile.ContainerGpu`.

\- \*\*Key advantage:\*\* Roughly 10x faster than faster-whisper with lower WER (6.05% vs 7.44% on standard benchmarks).

\- \*\*Files:\*\* New provider class, InferenceRuntimeCatalog, TranscriptionRegistry



\### 4.3 — Language Routing Logic



\- \*\*Problem:\*\* Parakeet supports 25 European languages. faster-whisper supports 100+. Parakeet should be used when it can, with faster-whisper as the universal fallback.

\- \*\*Option A (simpler, ship first):\*\* Expose both providers in the UI dropdown. Let the user pick based on content language.

\- \*\*Option B (follow-up):\*\* Auto-detect source language from a short audio sample (first 30 seconds). If detected language is in Parakeet's supported set → route to Parakeet. Otherwise → faster-whisper.

\- \*\*Recommendation:\*\* Ship Option A first. Add Option B once both providers are stable.



\### 4.4 — Integration Testing



\- Test matrix: Verify transcription quality and segment format compatibility across the language boundary (Parakeet's 25 EU languages vs faster-whisper's full set).

\- Confirm downstream pipeline stages (translation, TTS) work identically regardless of which ASR provider produced the segments.

\- Regression test: Ensure the session snapshot model (`SessionWorkflowStage.Transcribed`) works correctly with both providers.



\---



\## Phase 5: Subprocess Provider Polish (REVISED — demoted to backlog)



Process lifecycle improvements for the only two subprocess-based providers. Estimated effort: 3–5 days.



\*\*Context:\*\* Eight of ten providers already run in the persistent FastAPI process. Only Edge TTS and Piper still spawn a Python process per segment. These are CPU-only, no-GPU fallback providers used primarily by the budget-hardware audience. The optimization is real (\~20% speedup for these providers) but non-blocking and lower priority than Phases 3–4.



\### 5.1 — Batch Python Scripts for Edge TTS and Piper



\- \*\*Problem:\*\* `EdgeTtsProvider` and `PiperTtsProvider` each spawn a new Python process per segment. For a 200-segment video, that is 200 Python interpreter startups with \~0.5–1.5 seconds of overhead per segment from process creation and import statements.

\- \*\*Fix (Edge TTS):\*\* Write a batch-mode Python script that reads a JSON array of `{text, outputPath, voice}` items from stdin and processes them all in one interpreter session.

\- \*\*Fix (Piper):\*\* Piper's CLI supports streaming from stdin line-by-line. Spawn once, pipe segments sequentially. Split output by segment boundaries.

\- \*\*Impact:\*\* \~20% TTS speedup for Edge TTS and Piper providers specifically.

\- \*\*Files:\*\* New Python batch scripts, EdgeTtsProvider.cs, PiperTtsProvider.cs



\### 5.2 — Persistent Python Worker Pool



\- \*\*Problem:\*\* Even with batching, the subprocess-per-session model still pays Python startup cost per session.

\- \*\*Fix:\*\* Replace `PythonSubprocessServiceBase` with a `PythonWorkerPool` — a pool of long-lived Python worker processes that accept work over stdin/stdout JSON-RPC. Workers stay warm, imports paid once, pool size controls concurrency.

\- \*\*Impact:\*\* Eliminates \~1s Python startup + \~0.3s import overhead per invocation. Eliminates temp script file I/O.

\- \*\*Prerequisite:\*\* Phase 5.1 (batch scripts validate the multi-segment-per-process pattern first).

\- \*\*Files:\*\* PythonSubprocessServiceBase.cs (replaced), new PythonWorkerPool class



\---



\## Phase 6: Long-Term Architectural Refactors



Major structural changes that benefit from all prior stabilization. Estimated effort: 4–8 weeks total, can be staggered. \*\*Unchanged from original tracker.\*\*



\### 6.1 — Decompose EmbeddedPlaybackViewModel



\- \*\*Problem:\*\* The ViewModel handles playback controls, pipeline execution, provider/model/voice selection for all three stages, multi-speaker routing, dub mode, subtitle management, provider readiness polling, and segment inspection. 1200+ lines with \~50 observable properties. Every new feature touches this file.

\- \*\*Fix:\*\* Extract focused sub-ViewModels: PipelineConfigViewModel, DubModeViewModel, MultiSpeakerViewModel, SubtitleViewModel. Compose them in EmbeddedPlaybackViewModel. The coordinator already has this decomposition pattern (seven partial classes by concern) — the ViewModel layer should mirror it.

\- \*\*Impact:\*\* Reduces merge conflicts, improves testability, makes the ViewModel layer match the coordinator's clean separation.

\- \*\*Files:\*\* EmbeddedPlaybackViewModel.cs (split), new sub-ViewModel classes



\### 6.2 — Streaming Pipeline with Inter-Stage Overlap



\- \*\*Problem:\*\* AdvancePipelineAsync is strictly sequential: Transcribe (all segments) → Translate (all segments) → TTS (all segments). Each stage waits for the previous one to fully complete.

\- \*\*Fix:\*\* Use `System.Threading.Channels.Channel<T>` as segment queues between stages. Each stage consumes from its input channel and produces to its output channel, creating a streaming producer-consumer chain.

\- \*\*Prerequisite:\*\* Transcription endpoint would need to support streaming responses (SSE or chunked JSON) to get full benefit. Translation via CTranslate2/NLLB can process segments individually — maps cleanly.

\- \*\*Consideration:\*\* The session snapshot model assumes stage-level completion (`SessionWorkflowStage.Transcribed`, `.Translated`, `.TtsGenerated`). A streaming pipeline needs a more granular progress model.

\- \*\*Impact:\*\* For a 10-minute video with \~200 segments, total pipeline time reduction of 30–50% by overlapping the long tails of each stage.

\- \*\*Files:\*\* SessionWorkflowCoordinator.Pipeline.cs, all provider interfaces, session state model



\### 6.3 — Server-Side Batching for Qwen TTS



\- \*\*Problem:\*\* Each segment sends an individual HTTP request to the FastAPI server. The server processes one request at a time. GPU is idle between requests waiting for HTTP overhead and file I/O.

\- \*\*Fix:\*\* Add a batch endpoint (`POST /tts/qwen/batch`) that accepts an array of segments and returns audio paths. The server batches text inputs into a single forward pass. On the C# side, add a `GenerateBatchSegmentTtsAsync` method to `ITtsProvider`, group segments into batches of 4–8 (tunable by VRAM).

\- \*\*Impact:\*\* GPU utilization from \~40% to 80%+. Biggest win for Qwen on GPU.

\- \*\*Files:\*\* `inference/main.py`, QwenContainerTtsProvider.cs, ITtsProvider.cs



\### 6.4 — Constructor Overload Cleanup



\- \*\*Problem:\*\* SessionWorkflowCoordinator has two constructors — a convenience wrapper delegating to a primary that takes 14+ parameters. Readability and testability degrade as service count grows.

\- \*\*Fix:\*\* Introduce a builder/factory pattern or a lightweight DI container (even `Microsoft.Extensions.DependencyInjection`). Replace the convenience overload with named arguments or an options record.

\- \*\*Files:\*\* SessionWorkflowCoordinator.cs, App.axaml.cs



\### 6.5 — Clean Shutdown (Replace Environment.Exit)



\- \*\*Problem:\*\* `Environment.Exit(e.ApplicationExitCode)` is used because background threads (mpv event loop, debounce continuations, bootstrap warmup) keep the CLR alive. This is a symptom — `Dispose()` doesn't fully clean up all managed threads.

\- \*\*Fix:\*\* Track all long-lived background operations with `CancellationTokenSource` instances owned by the coordinator. Cancel them in `Dispose()`. The mpv event loop likely needs a Quit command before disposal.

\- \*\*Files:\*\* App.axaml.cs, SessionWorkflowCoordinator.cs, mpv integration layer



\---



\## Watch List — No Action, Monitor for Changes



Technologies to revisit when licensing or access conditions change.



\### Canary-1B-v2 (ASR + Translation in One Pass)



\- \*\*What:\*\* NVIDIA's Canary model performs ASR and translation in a single forward pass, collapsing two pipeline stages into one. Architecturally the biggest possible pipeline optimization.

\- \*\*Blocker:\*\* CC-BY-NC-4.0 license — non-commercial only. Incompatible with AGPL distribution to commercial users.

\- \*\*Watch for:\*\* NVIDIA relicensed Parakeet from restrictive (v2) to CC-BY-4.0 (v3). If Canary follows the same path, immediately prioritize integration.



\### Magpie TTS (Zero-Shot Voice Cloning)



\- \*\*What:\*\* NVIDIA's Magpie TTS family includes zero-shot voice cloning models (Zeroshot, Flow).

\- \*\*Blockers:\*\* English-only for cloning (+ European Spanish on Zeroshot). Gated access — "Apply for Access" on NIM, 2–3 day HuggingFace approval with non-commercial checkbox. Trades XTTS's 17-language zero-shot cloning for a narrower, gated, non-cloning alternative.

\- \*\*Watch for:\*\* Multilingual voice cloning support + ungated permissive licensing.



\### DiariZen



\- \*\*What:\*\* Open-source diarization with 30–50% DER reduction vs pyannote across benchmarks.

\- \*\*Blockers:\*\* (1) Uses a pyannote-audio git submodule fork whose token bypass status is unverified — the fork's README still references HuggingFace access tokens. (2) Best model (wavlm-large-s80-md) uses CC-BY-NC-4.0 weights. The MIT-licensed base model has notably lower accuracy.

\- \*\*Watch for:\*\* Confirmation that the fork eliminates the token requirement + a permissive license on the large model.



\---



\## Appendix A: Priority Impact Matrix (REVISED)



| Optimization | TTS Speedup | Pipeline Speedup | Effort | Risk | Phase |

|---|---|---|---|---|---|

| Fix thread safety | — | — | Trivial | None | 1 ✅ |

| Fire-and-forget helper | — | — | Low | Low | 1 ✅ |

| Extract startup code | — | — | Low | None | 1 ✅ |

| Fix "es" fallback | — | — | Trivial | None | 1 ✅ |

| Stream downloads | \~5% | \~2% | Trivial | None | 1 ✅ |

| Reuse HttpClient | \~10% (cloud) | \~4% | Trivial | None | 1 ✅ |

| Eliminate double synthesis | \~50% | \~20% | Low | Low | 2 ✅ |

| Provider-aware parallelism | \~30% (cloud) | \~10% | Very low | Low | 2 ✅ |

| \~\~Diarization registry fix\~\~ | — | — | \~\~Low\~\~ | \~\~Low\~\~ | \~\~3\~\~ SKIP |

| NeMo diarization endpoint | — | — | Low-med | Low | 3 |

| WeSpeaker endpoint | — | — | Low | Low | 3 |

| NeMo + WeSpeaker C# providers | — | — | Low-med | Low | 3 |

| pyannote + HF token removal | — | — | Low | Low | 3 |

| UI wiring audit | — | — | Low-med | Low | 3 |

| Parakeet endpoint | \~10x ASR | \~15% | Low-med | Medium | 4 |

| Parakeet C# provider | — | — | Low | Low | 4 |

| Batch Python scripts | \~20% (Edge/Piper) | \~8% | Medium | Low | 5 (backlog) |

| Persistent Python pool | \~25% (Edge/Piper) | \~10% | Med-high | Medium | 5 (backlog) |

| Decompose ViewModel | — | — | Medium | Low | 6 |

| Streaming pipeline | — | \~30-50% | High | Medium | 6 |

| Server-side batching | \~40-60% (GPU) | \~15% | High | Medium | 6 |

| Constructor cleanup | — | — | Low | Low | 6 |

| Clean shutdown | — | — | Medium | Low | 6 |



\---



\## Appendix B: Local vs Cloud Provider Map (REVISED — verified April 8, 2026)



| Stage | Provider | Compute Profile | Deployment | Network | Token Required |

|---|---|---|---|---|---|

| ASR | faster-whisper | CPU / GPU | `.venv` FastAPI | localhost only | No |

| ASR | Parakeet-TDT (Phase 4) | GPU | `.venv` FastAPI | localhost only | No |

| Translation | NLLB / CTranslate2 | CPU / GPU | `.venv` FastAPI | localhost only | No |

| TTS | Qwen TTS | GPU | `.venv` FastAPI | localhost only | No |

| TTS | Piper | CPU | Subprocess | None | No |

| TTS | \~\~XTTS v2\~\~ | \~\~GPU\~\~ | \~\~.venv FastAPI\~\~ | \~\~localhost only\~\~ | \~\~No\~\~ (removing) |

| TTS | Edge TTS | Cloud | Subprocess | Microsoft servers | No |

| TTS | OpenAI TTS | Cloud | HTTP client | OpenAI API | API key |

| TTS | ElevenLabs | Cloud | HTTP client | ElevenLabs API | API key |

| Diarization | \~\~pyannote\~\~ | \~\~CPU / GPU\~\~ | \~\~Subprocess\~\~ | \~\~None\~\~ | \~\~HF token\~\~ (removing) |

| Diarization | NeMo ClusteringDiarizer (Phase 3) | GPU | `.venv` FastAPI | localhost only | No |

| Diarization | WeSpeaker (Phase 3) | CPU | `.venv` FastAPI | localhost only | No |



\*\*8 of 10 active providers\*\* run in a single persistent FastAPI process. Only Edge TTS and Piper use subprocesses.

