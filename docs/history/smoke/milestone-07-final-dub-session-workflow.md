# Milestone 7 Final: Dub Session Workflow - Smoke Note

## Metadata
- Milestone: `07`
- Name: `Dub Session Workflow`
- Date: `2026-03-29`
- Status: `complete`

## Gate Summary
- [x] A user can take one source file through transcript, translation, generation, and selective refinement inside one persistent session
- [x] The workflow supports iteration without rerunning everything from scratch
- [x] Sequential dubbed segment playback in session order
- [x] Coordinator playback state is explicit and observable (`Idle`, `PlayingSingleSegment`, `PlayingSequence`)
- [x] Stop/cancel works for single-segment and sequence playback
- [x] Missing segment TTS artifacts in sequence are skipped truthfully (no exception)
- [x] Reopen preserves TTS artifact paths and PlayAll works on reopened session

## What Was Verified
- `PlayAllDubbedSegmentsAsync()` plays segments with `HasTtsAudio=true` in StartSeconds order
- Segments with missing files on disk are skipped without throwing
- `PlaybackState` transitions: Idle → PlayingSingleSegment → Idle (via stop or natural end), Idle → PlayingSequence → Idle
- `StopPlayback()` cancels both single-segment and sequence playback and always returns `PlaybackState.Idle`
- Starting `PlayAllDubbedSegmentsAsync()` while single-segment is active correctly transitions to `PlayingSequence`
- After session reopen, `TtsSegmentAudioPaths` is restored and `PlayAllDubbedSegmentsAsync()` uses persisted paths
- All 41 tests pass

## What Was Not Verified
- Sequential playback with real audio output (libmpv `Ended` event is never raised by `LibMpvHeadlessTransport` — `HasEnded` polling is used instead, untested with real long-form audio)
- Manual end-to-end smoke path through the full UI (UI surface for this workflow is deferred to Milestone 8+)

## Evidence

### Commands Run
```bash
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```
Total tests: 41
Passed: 41
Duration: 302.3s

Tests covering this milestone:
- PlayAllDubbedSegments_PlaysInOrder
- StopPlayback_CancelsSequence
- PlaySegmentThenPlayAll_TransitionsStateCleanly
- PlaybackState_ReflectsIdleSingleSequenceStopped
- MissingSegmentTts_InSequence_IsHandledTruthfully
- Reopen_PlayAllDubbedSegments_UsesPersistedArtifacts
```

### Implementation Details
- `Models/PlaybackState.cs`: `Idle`, `PlayingSingleSegment`, `PlayingSequence`
- `SessionWorkflowCoordinator`:
  - `[ObservableProperty] PlaybackState _playbackState` — observable to UI/ViewModel
  - `private CancellationTokenSource? _sequenceCts` — owned by coordinator, cancelled by `StopPlayback()` or new playback start
  - `PlayAllDubbedSegmentsAsync()` — filters `HasTtsAudio=true`, plays in `StartSeconds` order, polls `player.HasEnded`
  - `StopPlayback()` — cancels CTS + pauses player + clears `ActiveTtsSegmentId` + sets `Idle`
  - `PlaySegmentTtsAsync()` — cancels any running sequence CTS, sets `PlayingSingleSegment`

## Notes
- `LibMpvHeadlessTransport.Ended` event is declared with `#pragma warning disable CS0067` and never raised. Sequential playback auto-advance uses `player.HasEnded` polling. For tests, `FakeSegmentPlayer(simulateInstantEnd: true)` fires `Ended` from `Play()` and sets `HasEnded = true`.
- `StopPlayback()` replaces `StopSegmentTts()` as the unified stop surface. Old call sites updated.
- `PlaybackState` only resets to `Idle` in the sequence's `finally` block if still `PlayingSequence` — prevents race if a new single-segment play has superseded the sequence.
- Sequence playback includes timeout protection: each segment has a max wait based on `player.Duration + 10s` (minimum 15s) to prevent infinite polling if `HasEnded` is never set.

## Conclusion
Milestone 7 gate is satisfied. A session can proceed from load → transcript → translate → generate → per-segment refinement → sequential playback → stop, with all state persisted across reopen. The coordinator is the sole state owner. The interaction surface (PlaySegmentTtsAsync, PlayAllDubbedSegmentsAsync, StopPlayback, PlaybackState, ActiveTtsSegmentId) is ready for UI wiring in Milestone 8.

## Deferred Items
- UI surface for segment list, playback controls (Milestone 8)
- `SegmentStatus` enum for accepted/needs-revision/pending states
- Compare alternative TTS outputs
- Real audio auto-advance (requires `LibMpvHeadlessTransport` to raise `Ended` event on EOF)
- Full end-to-end manual smoke path through UI
