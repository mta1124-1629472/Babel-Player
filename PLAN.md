# Babel Player From-Scratch Plan (Windows-first, libmpv early, TTS-centered)

## Summary

Build the app as a strict sequence of vertical slices, but organize those slices around the real product chain:

`foundation -> headless media transport -> media ingest/artifacts -> transcription -> translation/adaptation -> TTS dubbing -> dub session workflow -> embedded playback/preview -> subtitle/transcript inspection -> settings/bootstrap -> local/offline expansion -> runtime optimization -> release hardening`

The product center is not “a player with AI attached.”
The product center is:

`source media -> timed transcript -> translated/adapted dialogue -> spoken dubbed output -> in-context preview and refinement`

Playback matters, but as a supporting inspection and QA surface rather than the main identity.

---

## Working Rules

- Work one milestone at a time. No downstream scope starts until the current milestone has build, tests, and a written smoke result.
- Keep behavior truthful. Missing work uses explicit placeholders. No fake readiness, no silent fallback, no pretending a local/runtime path works when it does not.
- The first goal is a real end-to-end workflow, not a pretty shell.
- Do not add optional features before the current milestone is genuinely usable.
- Do not generalize early for multiple providers, runtimes, or model families unless the current milestone is blocked without it.
- Preserve old working code and experiments. Archive instead of deleting.
- Treat architecture as a servant of the main loop, not as a parallel product.
- Local and optimized inference paths should preserve a clear boundary between the desktop app and Python-backed inference services so they can later run natively, in WSL, or in containerized environments without changing the core product workflow.
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

## Milestones

### 1. Foundation
Set up the repo, app skeleton, logging, persistence basics, test project, and a single coordinator that owns session state and workflow progression.

The aim here is not feature depth. It is enforcing the rules of the rebuild:
one source of truth,
honest states,
clear boundaries,
testability,
and no fake scaffolding.

Gate:
- App boots
- Test project runs
- Logging works
- Basic persistence works
- Session ownership is explicit and not split across random surfaces

---

### 2. Headless Media Transport Proof
Retire libmpv risk early in headless mode.

This milestone exists because media transport has already been a recurring failure point. The goal is not to make playback the product center. The goal is to eliminate a known technical trap before more workflow depends on it.

Scope:
- Load local media
- Play/pause
- Seek
- Current time and duration
- Ended/completed events
- Repeated load/unload cycles
- Clean teardown

Gate:
- Repeated headless playback cycles complete without hangs
- Teardown is stable
- No ghost state survives reload cycles
- A smoke note confirms repeated success rather than one lucky run

---

### 3. Media Ingest and Artifact Pipeline
Build the substrate that later stages consume.

Given a source media file, the app should be able to:
- Extract relevant audio artifacts
- Persist them
- Represent timed segments/artifacts in a stable way
- Reopen and reuse those artifacts across sessions

This is the pipeline foundation for transcription and TTS, not a user-facing polish phase.

Gate:
- A source file can be ingested into stable reusable artifacts
- Artifacts survive restart
- Timed chunks or intermediate media assets are represented consistently enough to feed downstream milestones

---

### 4. Transcription v1
Implement the first real AI slice: generate a timed source-language transcript.

Keep this narrow. The point is not configurability. The point is to produce trustworthy timed text the rest of the pipeline can build on.

Scope:
- Transcript generation for one source file
- Timed segment output
- Honest handling of failures
- Persistent transcript artifacts

Gate:
- A sample file can be transcribed end to end
- Output segments are timestamped and stable enough for downstream use
- Failures surface honestly rather than silently degrading

---

### 5. Translation and Dialogue Adaptation v1
Translate the transcript into target-language dialogue that is usable for speech generation.

This is not just subtitle translation. The output needs to behave like spoken lines. The system should start distinguishing between:
- literal semantic transfer
- dub-ready line adaptation

Scope:
- Translate transcript segments
- Store adapted dialogue alongside source transcript
- Preserve timing relationships closely enough for later TTS work
- Keep the workflow narrow and truthful

Gate:
- A sample file can move from transcript to target-language dialogue segments
- The resulting lines are clearly usable for spoken output
- The system stores source and target text as linked artifacts

---

### 6. TTS Dubbing Vertical Slice
Build the feature that actually defines the product.

Given translated/adapted dialogue segments, the app should generate spoken output and let the user preview it. Keep this milestone intentionally narrow: one working path, one segment flow, one stable loop.

Scope:
- Generate spoken output from target-language dialogue
- Support segment-based generation
- Preview generated output
- Regenerate on demand
- Persist generated artifacts

Gate:
- A sample file can produce previewable dubbed output for at least a meaningful set of segments
- Regeneration is stable
- Output artifacts persist and can be replayed without rerunning the full upstream pipeline

---

### 7. Dub Session Workflow
Turn the raw TTS capability into a usable operator workflow.

This milestone is where the app starts feeling like a dubbing tool rather than a collection of services.

Scope:
- Segment list or queue
- Re-run individual segments
- Compare alternative outputs
- Keep selected outputs
- Maintain session continuity across restarts
- Track which segments are pending, generated, accepted, or need revision

Gate:
- A user can take one source file through transcript, translation, generation, and selective refinement inside one persistent session
- The workflow supports iteration without rerunning everything from scratch

---

### 8. Embedded Playback and In-Context Preview
Add embedded media playback as a refinement and inspection tool.

Playback is important, but it arrives here to support the dub workflow rather than to define the app’s identity. The purpose is contextual QA:
scrub source media,
preview generated speech in context,
check timing and flow.

Scope:
- Embedded playback
- Scrubbing and preview in context
- Segment-aware navigation
- Basic playback controls sufficient for inspection

Gate:
- Generated dialogue can be previewed in media context reliably
- Playback integration does not destabilize earlier milestones
- A user can move between source context and generated output without losing workflow state

---

### 9. Subtitle and Transcript Inspection
Add visual inspection surfaces that help refine the dub workflow.

This is where transcript panes, subtitle overlays, bilingual comparison, and similar surfaces earn their keep.

Scope:
- Source transcript view
- Target dialogue view
- Bilingual comparison
- Subtitle-style inspection surfaces where useful
- Segment-focused QA views

Gate:
- The user can inspect source text, target text, and generated speech together
- These surfaces improve refinement instead of just inflating the shell

---

### 10. Settings and Bootstrap
Add persisted preferences, recent sessions, credentials/setup basics, and recoverable startup states.

This milestone should support the proven workflow, not sprawl into a large configuration universe.

Gate:
- Restart persistence works
- Recent sessions reopen correctly
- Missing configuration is explicit and recoverable
- Setup states do not pretend readiness

---

### 11. Local / Offline Expansion
Once the core workflow is real, add local paths for transcription, translation, and TTS.

This milestone is where local execution starts to matter. It should come only after the cloud-backed or simplest main workflow has proven the actual product loop.

Local inference should preserve a clear boundary between the desktop app and Python-backed inference services. Native local execution may be the first proving path, but WSL-hosted, containerized, and NVIDIA-managed serving paths should remain future-compatible deployment options rather than early prerequisites.


Gate:
- The main workflow can run locally on at least one supported machine configuration
- Local capability is truthful and verified
- Unsupported local paths remain clearly unsupported


---

### 12. Runtime Optimization and Hardware Routing
Only now add richer runtime selection, hardware readiness checks, and optimized execution paths.

This is a scaling and reliability milestone, not a foundation milestone.

WSL, containers, and NVIDIA-managed serving paths should be evaluated here as deployment and reproducibility tools once the first real local workflow exists, not as foundation work before the product loop is proven.

Gate:
- Runtime routing is truthful
- Diagnostics are useful
- Optimized paths pass smoke tests on real hardware
- The app never claims a target is ready unless it has been actually verified

---

### 13. Release Hardening
Package the app, harden startup/recovery, improve crash logging and support artifacts, and validate the workflow on clean machines.

Gate:
- Packaged builds complete the core workflow on a clean machine
- Crash/support logs are usable
- Startup and shutdown are dependable
- The app is shippable without relying on dev-machine assumptions

---

## What This Plan Is Protecting Against

This plan is specifically designed to prevent the common failure mode where each rebuild becomes cleaner architecturally but less complete as a product.

It protects against:
- rebuilding the shell before proving the main workflow
- over-investing in playback identity before the dubbed-voice loop is real
- adding runtime/provider complexity before the product exists
- polishing the interface while the core chain is still broken
- deleting earlier working knowledge instead of preserving it

---

## Milestone Philosophy

Implementation order and product importance are not the same thing.

Transcription and translation come before TTS because they are dependencies.
TTS remains the hero feature because it is the first place the pipeline becomes compelling to a user.

That means the app should be built so that:
- upstream stages exist to serve dubbed output
- playback exists to inspect and refine dubbed output
- optional features do not outrank the end-to-end workflow

---

## Definition of Success

The rebuild is succeeding if, as early as possible, a user can:

1. Load a source file
2. Generate a timed transcript
3. Produce translated/adapted dialogue
4. Generate spoken dubbed output
5. Preview and refine that output in context
6. Reopen the same session and continue without losing work

Everything else is secondary until that loop is real.