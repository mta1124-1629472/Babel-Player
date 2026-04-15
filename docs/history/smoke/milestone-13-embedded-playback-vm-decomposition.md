# Metadata
- milestone: 13
- label: embedded-playback-vm-decomposition
- date: 2026-04-13
- status: partial

# Gate Summary
- [x] `dotnet build Babel-Player.sln`
- [x] `dotnet test Babel-Player.sln`
- [x] `python scripts/check-architecture.py`
- [x] `python -m py_compile inference/main.py`
- [ ] Manual playback-shell smoke across load, refresh, playback, subtitles, dub mode, fullscreen, speaker routing, and caption export

# What Was Verified
- `ViewModels/EmbeddedPlaybackViewModel.cs` is now a composition-focused parent at 163 lines, well under the 800-line target.
- `EmbeddedPlaybackPreviewViewModel` now owns segment state, source playback controls, subtitle state, dub-mode ducking/sync, fullscreen/control-hide behavior, and segment refresh logic.
- `EmbeddedPlaybackPipelineViewModel` now owns pipeline progress, run/cancel/clear commands, and the re-diarize command state.
- `EmbeddedPlaybackSpeakerRoutingViewModel` now owns diarization settings, speaker selection, per-speaker assignment details, and reference-audio actions.
- `MainWindow.axaml`, `MainWindow.axaml.cs`, and `SegmentInspectionViewModel` now bind and react through `Playback.Preview`, `Playback.Pipeline`, and `Playback.SpeakerRouting` instead of broad parent forwarding aliases.
- Playback composition, progress, diarization, inspection, speech-rate, and segment-refresh regression tests were updated and passed against the decomposed surface.

# What Was Not Verified
- Manual desktop interaction after the refactor, including media load, play/pause/seek/skip, subtitle persistence after reload, dub-mode sync/ducking, fullscreen auto-hide, speaker assignment/reference clip flow, and caption export.
- Visual confirmation that the updated nested bindings still match the intended Avalonia layout during live use.

# Evidence
- `dotnet build Babel-Player.sln` succeeded on 2026-04-13 with 0 errors and 1 warning (`CA2024` in `Services/PythonJsonWorkerPool.cs`, pre-existing to this refactor).
- `dotnet test Babel-Player.sln` passed: 898 passed, 0 failed, 0 skipped.
- Architecture linter passed all checks, including the coordinator line-count threshold.
- `python -m py_compile inference/main.py` completed successfully.

# Notes
- This smoke note is `partial` because no interactive Avalonia session was run from this environment after the binding and code-behind changes.
- The decomposition intentionally removed the parent-level playback forwarding surface; callers now target the dedicated child viewmodels directly.

# Conclusion
- Tier 1 / 6.1b is implemented and verified at build, test, and architecture levels with no observed regression in automated coverage.
- Historical note: this originally pointed to Parakeet as the next implementation action; Parakeet ASR was completed on April 15, 2026.

# Deferred Items
- Run the manual desktop smoke path for preview playback, subtitle reload, dub-mode behavior, fullscreen control-hide, speaker routing, and caption export.
- (Completed later) Parakeet transcription endpoint, provider, and GPU-profile UI exposure landed on April 15, 2026.
