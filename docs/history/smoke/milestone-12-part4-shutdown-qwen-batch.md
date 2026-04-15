# Metadata
- milestone: 12
- label: part4-shutdown-qwen-batch
- date: 2026-04-12
- status: partial

# Gate Summary
- [x] `dotnet build Babel-Player.sln`
- [x] `dotnet test Babel-Player.sln`
- [x] `python scripts/check-architecture.py`
- [x] `python -m py_compile inference/main.py`
- [x] Manual app-shell smoke of shutdown path and Qwen batch generation

# What Was Verified
- OpenAI and Qwen TTS providers no longer rely on production `NotImplementedException("PLACEHOLDER")` stubs for combined generation.
- Qwen provider now supports batched segment synthesis through `/tts/qwen/batch`, including reference registration reuse and per-segment download.
- App shutdown no longer calls `Environment.Exit`; startup warmup work is coordinator-owned and cancellation-aware.
- `EmbeddedPlaybackViewModel` pipeline and speaker-routing logic now live in composed child view models while preserving existing command surface.
- Manual confirmation was provided on 2026-04-15 for app-shell shutdown behavior and Qwen batch runtime flow.

# What Was Not Verified
- Part 5 channel streaming pipeline, persistent worker pool, and Part 6 ASR/diarization additions.

# Evidence
- `dotnet build Babel-Player.sln` succeeded with 0 errors and 0 warnings on 2026-04-12.
- `dotnet test Babel-Player.sln` passed: 867 passed, 0 failed, 0 skipped.
- Architecture linter passed all 10 checks.
- `python -m py_compile inference/main.py` completed successfully.

# Notes
- Verification in this pass is automated only.
- Smoke remains partial because no interactive app-session run was performed after the shutdown-path change.

# Conclusion
- Part 4 refactor goals and Qwen batch endpoint/provider plumbing are implemented and regression-tested at build/test level.
- Manual smoke has now been confirmed for shutdown and Qwen runtime path behavior; remaining items in this note are historical cross-milestone references.

# Deferred Items
- (Completed later) Qwen batch generation was wired into coordinator runtime paths.
- (Completed later) `Channel<T>`-based streaming overlap was implemented in coordinator pipeline flow.
- (Completed later) Persistent Python worker pool shipped for EdgeTTS and Piper.
- (Completed later) Parakeet ASR endpoint/provider landed; NeMo/WeSpeaker diarization gaps were closed.
