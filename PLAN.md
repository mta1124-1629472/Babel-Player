# Babel Player — Build Plan (Windows-first, libmpv early, TTS-centered)

## Summary

Build the app as a strict sequence of vertical slices organized around the real product chain:

`foundation → headless media transport → media ingest/artifacts → transcription → translation/adaptation → TTS dubbing → dub session workflow → embedded playback/preview → subtitle/transcript inspection → settings/bootstrap → local/offline expansion → runtime optimization → release hardening`

The product center is not "a player with AI attached."
The product center is:

`source media → timed transcript → translated/adapted dialogue → spoken dubbed output → in-context preview and refinement`

Playback matters as a supporting inspection and QA surface, not as the main identity.

---

## Working Rules

- Work one milestone at a time. No downstream scope starts until the current milestone has build, tests, and a written smoke result.
- Keep behavior truthful. Missing work uses explicit placeholders. No fake readiness, no silent fallback, no pretending a local/runtime path works when it does not.
- The first goal is a real end-to-end workflow, not a pretty shell.
- Do not add optional features before the current milestone is genuinely usable.
- Preserve old working code and experiments. Archive instead of deleting.
- Treat architecture as a servant of the main loop, not as a parallel product.
- Keep the desktop app and Python-backed inference separated by an explicit service/process boundary so local, WSL-hosted, containerized, or NVIDIA-managed deployment paths remain possible later without changing the core workflow.

---

## Product Priority

### Primary differentiator
Translated dialogue spoken back in a compelling voice-driven form.

### Required dependency chain
1. Transcription
2. Translation and dialogue adaptation
3. TTS / dubbed speech generation
4. In-context preview, inspection, and refinement

### Supporting systems
- Media transport
- Artifact extraction and caching
- Playback and scrubbing
- Subtitle and transcript inspection
- Settings, setup, and runtime diagnostics

---

## Provider Inventory (as of current build)

### Transcription
| Provider | Runtime | Status |
|---|---|---|
| FasterWhisper | Local / Containerized | ✅ Implemented |
| OpenAI Whisper API | Cloud | ✅ Implemented |
| Google STT | Cloud | ✅ Implemented |
| Google Gemini (`gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`) | Cloud | ✅ Implemented |

### Translation
| Provider | Runtime | Status |
|---|---|---|
| Google Translate (Free / web scraper) | Cloud | ✅ Implemented (unreliable) |
| NLLB-200 | Local | ✅ Implemented |
| DeepL API | Cloud | ✅ Implemented |
| OpenAI API | Cloud | ✅ Implemented |
| Google Gemini (`gemini-2.0-flash`, `gemini-2.5-flash-preview-04-17`) | Cloud | ✅ Implemented |

### TTS
| Provider | Runtime | Status |
|---|---|---|
| Edge TTS | Cloud | ✅ Implemented |
| Piper | Local | ✅ Implemented |
| ElevenLabs | Cloud | ✅ Implemented |
| Google Cloud TTS | Cloud | ✅ Implemented |
| OpenAI TTS | Cloud | ✅ Implemented |
| Qwen3-TTS | Local (GPU) | ✅ Implemented (voice cloning, RTX validated) |
| XTTS v2 | Local (GPU) | ✅ Implemented (voice cloning, containerized) |

### Diarization
| Provider | Runtime | Status |
|---|---|---|
| Pyannote (local) | Local | ✅ Implemented |

---

## Milestones

### 1. Foundation ✅ COMPLETE
Repo, app skeleton, logging, persistence basics, test project, session coordinator owning session state and workflow progression.

Gate passed:
- App boots
- Test project runs (`BabelPlayer.Tests/`)
- `AppLog` structured logging works
- `SessionSnapshotStore` / `PerSessionSnapshotStore` persistence works
- `SessionWorkflowCoordinator` owns session state, not split across views

---

### 2. Headless Media Transport Proof ✅ COMPLETE
Retired libmpv risk early in headless mode.

Gate passed:
- `LibMpvHeadlessTransport` handles repeated load/unload cycles cleanly
- `LibMpvEmbeddedTransport` stable for embedded playback
- `IMediaTransport` / `IMediaTransportManager` interfaces defined
- Teardown is stable; no ghost state survives reload cycles

---

### 3. Media Ingest and Artifact Pipeline ✅ COMPLETE
Substrate for downstream stages.

Gate passed:
- Source media loads into stable reusable artifacts
- `SessionArtifactReader` reads artifacts across restarts
- `SessionSnapshotSemantics` represents timed segments consistently
- Artifacts survive restart and feed downstream pipeline stages

---

### 4. Transcription v1 ✅ COMPLETE
Real AI slice: timed source-language transcript generation.

Gate passed:
- `FasterWhisperTranscriptionProvider` produces timestamped segments
- `OpenAiWhisperTranscriptionProvider`, `GoogleSttTranscriptionProvider`, `GeminiTranscriptionProvider` all wired and selectable
- `TranscriptionRegistry` with `IsImplemented` flags, `ProviderDescriptor`, readiness checks
- `InferenceRuntimeCatalog` normalizes provider/runtime round-trips for all transcription providers including Gemini
- Failures surface via `PipelineProviderException`, not silently

---

### 5. Translation and Dialogue Adaptation v1 ✅ COMPLETE
Transcript → target-language dialogue usable for speech generation.

Gate passed:
- `NllbTranslationProvider`, `GoogleTranslationProvider`, `DeepLTranslationProvider`, `OpenAiTranslationProvider`, `GeminiTranslationProvider` all wired and selectable
- `TranslationRegistry` with readiness checks and runtime normalization
- `InferenceRuntimeCatalog` handles all translation provider IDs including Gemini
- Source and target text stored as linked artifacts in session snapshots
- Timing relationships preserved through segment structure

---

### 6. TTS Dubbing Vertical Slice ✅ COMPLETE
The feature that defines the product.

Gate passed:
- `EdgeTtsProvider`, `PiperTtsProvider`, `ElevenLabsTtsProvider`, `GoogleCloudTtsProvider`, `OpenAiTtsProvider`, `XttsContainerTtsProvider` all wired
- `TtsRegistry` with readiness checks and runtime normalization
- Segment-based generation, persist generated artifacts
- Regeneration on demand
- `ITtsProvider` interface consistent across all providers

---

### 7. Dub Session Workflow ✅ COMPLETE
Usable operator workflow over raw TTS capability.

Gate passed:
- `SessionWorkflowCoordinator` orchestrates transcript → translation → TTS as a managed pipeline
- `SessionWorkflowCoordinator.cs` (56 KB), `.Playback.cs`, `.Containerized.cs` partial classes covering all workflow branches
- Session continuity across restarts via snapshot store
- Segment state tracking (pending / generated / accepted) in coordinator
- `SessionSwitchService` and `RecentSessionsStore` support multi-session workflows

---

### 8. Embedded Playback and In-Context Preview ✅ COMPLETE
Playback as a refinement and inspection tool.

Gate passed:
- `LibMpvEmbeddedTransport` stable for embedded rendering
- Source scrubbing, segment-aware navigation, basic transport controls
- Volume control with hover-reveal vertical slider (3-second retraction timer)
- Subtitle overlay, auto-hide controls bar
- Fullscreen mode via `IsFullscreen` → `WindowState.FullScreen`
- Playback integration does not destabilize earlier milestones

---

### 9. Subtitle and Transcript Inspection ✅ COMPLETE
Visual inspection surfaces for dub workflow refinement.

Gate passed:
- Source transcript view and target dialogue view in `MainWindow`
- `SegmentList` with `ScrollIntoView` on `SelectedSegment`
- Bilingual segment comparison visible in UI
- SRT caption export via `SrtGenerator`
- Speaker diarization panel with reference audio support per speaker (`PyannoteDiarizationProvider`)

---

### 10. Settings and Bootstrap ✅ COMPLETE
Persisted preferences, credentials, recent sessions, recoverable startup.

Gate passed:
- `AppSettings` with full provider/runtime/model/language preference persistence
- `ApiKeyStore` (credential management) with `CredentialKeys` constants for all providers
- `ApiKeysDialog` + `ApiKeyValidationService` for in-app key setup
- `SettingsWindow` / `SettingsViewModel` for runtime/provider/model selection
- `BootstrapDiagnostics` for readable startup diagnostics
- `RecentSessionsStore` — recent sessions reopen correctly
- Missing configuration is explicit and recoverable (readiness checks surface API key absence)
- `InferenceRuntimeCatalog.NormalizeSettings` round-trips provider selection without silent corruption

---

### 11. Local / Offline Expansion ✅ SUBSTANTIALLY COMPLETE
Local paths for transcription, translation, TTS, and diarization are real.

Current state:
- **Transcription (local):** `FasterWhisperTranscriptionProvider` — calls Python subprocess, models: `tiny`, `base`, `small`, `medium`, `large-v3`
- **Translation (local):** `NllbTranslationProvider` — calls Python subprocess, models: `nllb-200-distilled-600M`, `nllb-200-distilled-1.3B`, `nllb-200-1.3B`
- **TTS (local):** `PiperTtsProvider` — calls Python subprocess
- **Diarization (local):** `PyannoteDiarizationProvider` — calls Python subprocess, requires HuggingFace token and model gate acceptance
- **Containerized runtime:** `ContainerizedInferenceClient` / `ContainerizedInferenceManager` with `ContainerizedServiceProbe` for health checks; containerized transcription, translation, and TTS providers all wired
- `PythonSubprocessServiceBase` shared subprocess lifecycle management
- `ModelDownloader` for local model acquisition
- `HardwareSnapshot` and `HardwareEncoderHelper` for machine capability reads

Remaining gap:
- Smoke-tested end-to-end run on a clean machine without dev-environment assumptions has not been formally documented
- WSL and NVIDIA-managed container deployment paths are designed for but not yet validated

Gate: local capability is real and selectable. Unsupported paths remain clearly unsupported via readiness checks.

---

### 12. Runtime Optimization and Hardware Routing ⏳ IN PROGRESS
Richer runtime selection, hardware readiness checks, and optimized execution paths.

This is a scaling and reliability milestone, not a foundation milestone. The local and cloud paths both work; this milestone is about making them trustworthy across machine configurations.

Scope:
- ✅ GPU acceleration validation for FasterWhisper (CUDA, NVIDIA runtime detection) — RTX path validated on real hardware
- ✅ Compute profiles (CPU/GPU/Cloud) fully implemented with hardware-aware type selection (`float8` for Blackwell, `float16` for older GPUs)
- ✅ Multi-speaker diarization fully implemented and surfaced in UI (Pyannote with speaker boundary detection)
- ✅ GPU TTS validation (XTTS v2, Qwen3-TTS) — wired end-to-end, RTX path validated; Blackwell validation pending
- ⏳ Container health probe integration in settings UI (show live container status) — partially complete
- 🔲 NVDEC / D3D11VA hardware decode path verification in libmpv transport — not yet verified on real hardware
- 🔲 WSL-hosted Python inference path tested and documented — designed but not yet validated
- ⏳ Runtime routing diagnostics — surface in UI when a selected runtime is degraded — diagnostics layer implemented, UI integration in progress
- ✅ `HardwareSnapshot` implemented and available in diagnostics
- ✅ UI elegance cleanup and controls refinement — section headers clarified, contrast improved, transport controls unified
- 🔲 Benchmark suite in `benchmarks/` for regression tracking — basic structure present, comprehensive suite not yet built

Completed work:
- Word-level timestamps in transcription output
- Speaker-boundary splitting in diarization
- Per-speaker voice assignment with fallback routing
- Voice cloning support for Qwen3-TTS and XTTS v2

Gate:
- Runtime routing is truthful and visible in the UI (partial — diagnostics exist, UI integration in progress)
- Diagnostics are useful, not just logged (✅)
- Optimized paths pass smoke tests on real hardware (✅ RTX; Blackwell pending)
- App never claims a target is ready unless it has been actually verified (✅)

---

### 13. Release Hardening 🔲 FUTURE
Package the app, harden startup/recovery, improve crash logging and support artifacts, validate on clean machines.

Scope:
- ✅ Portable and installer builds available from releases
- ✅ SRT export, session restore, and API key setup working on packaged build
- ✅ README updated with current architecture and features
- 🔲 Packaged Windows build — further hardening and clean-machine validation
- 🔲 Clean-machine validation: core workflow completes without dev assumptions
- 🔲 Crash/support log artifacts usable by a non-developer
- 🔲 Startup and shutdown hardening across unexpected exits
- 🔲 CONTRIBUTING guide updated for packaged workflow

Gate:
- Packaged builds complete the core workflow on a clean machine
- Crash/support logs are usable
- The app is shippable without relying on dev-machine assumptions

---

## What This Plan Is Protecting Against

This plan is specifically designed to prevent the common failure mode where each rebuild becomes cleaner architecturally but less complete as a product.

It protects against:
- Rebuilding the shell before proving the main workflow
- Over-investing in playback identity before the dubbed-voice loop is real
- Adding runtime/provider complexity before the product exists
- Polishing the interface while the core chain is still broken
- Deleting earlier working knowledge instead of preserving it

---

## Milestone Philosophy

Implementation order and product importance are not the same thing.

Transcription and translation come before TTS because they are dependencies.
TTS remains the hero feature because it is the first place the pipeline becomes compelling to a user.

The app should be built so that:
- Upstream stages exist to serve dubbed output
- Playback exists to inspect and refine dubbed output
- Optional features do not outrank the end-to-end workflow

---

## Definition of Success

The rebuild is succeeding if, as early as possible, a user can:

1. Load a source file
2. Generate a timed transcript (any of: FasterWhisper, OpenAI, Google STT, Gemini)
3. Produce translated/adapted dialogue (any of: NLLB-200, DeepL, OpenAI, Gemini, Google Translate)
4. Generate spoken dubbed output (any of: Edge TTS, Piper, ElevenLabs, Google Cloud TTS, OpenAI TTS, XTTS)
5. Preview and refine that output in context with the source video
6. Reopen the same session and continue without losing work

Everything else is secondary until that loop is real and verified on real hardware.
