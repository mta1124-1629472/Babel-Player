# Milestone 7.4 Segment-Level TTS Playback - Smoke Note

## Metadata
- Milestone: `07.4`
- Name: `Segment-Level TTS Playback`
- Date: `2026-03-29`
- Status: `complete`

## Gate Summary
- [x] `PlaySegmentTtsAsync(segmentId)` loads and plays the correct per-segment audio file
- [x] `StopSegmentTts()` pauses playback and clears `ActiveTtsSegmentId`
- [x] `ActiveTtsSegmentId` is set on play, cleared on stop, natural end, and error
- [x] `FileNotFoundException` thrown when audio artifact is missing
- [x] `InvalidOperationException` thrown when segment has no TTS path in dictionary
- [x] Playback path survives session reopen (persisted `TtsSegmentAudioPaths`)
- [x] All 31 existing tests still pass

## What Was Verified
- `PlaySegmentTtsAsync` validates path in `TtsSegmentAudioPaths`, checks file exists, then calls `Load` + `Play` on an `IMediaTransport`
- `ActiveTtsSegmentId` is set before `Play()` and cleared by `StopSegmentTts()` (explicit) and via `Ended`/`ErrorOccurred` event handlers (natural end or transport error)
- `LibMpvHeadlessTransport` gains `bool suppressAudio = true` parameter; existing headless tests unaffected; production playback passes `suppressAudio: false`
- `IMediaTransport` injected via coordinator constructor for test isolation — `FakeSegmentPlayer` bypasses libmpv entirely
- Coordinator implements `IDisposable`; disposes only the player it created (not injected ones)
- `Play()` offloaded via `Task.Run` because `LibMpvHeadlessTransport.Play()` contains a blocking polling loop (`Thread.Sleep(50)`, up to 20 iterations)

## Evidence

### Commands Run
```bash
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 35
Passed: 35

Tests covering this milestone:
- PlaySegmentTtsAsync_LoadsCorrectFile
- PlaySegmentTtsAsync_MissingArtifact_Throws
- StopSegmentTts_ClearsActiveSegment
- PlaySegmentTtsAsync_AfterReopen_UsesPersistedPath
```

### Implementation Details
- `Services/LibMpvHeadlessTransport.cs`: `bool suppressAudio = true` constructor parameter controls `ao=null`
- `Services/SessionWorkflowCoordinator.cs`:
  - `IMediaTransport? segmentPlayer = null` constructor parameter (test injection point)
  - `GetOrCreateSegmentPlayer()`: lazy creation of `LibMpvHeadlessTransport(suppressAudio: false)`; subscribes `Ended` and `ErrorOccurred` to clear `ActiveTtsSegmentId`
  - `[ObservableProperty] string? _activeTtsSegmentId`
  - `PlaySegmentTtsAsync(string segmentId)`: validates dict + file, calls `Load` + `Task.Run(Play)`
  - `StopSegmentTts()`: calls `Pause()`, clears `ActiveTtsSegmentId` in `finally`
  - `IDisposable.Dispose()`: disposes created player only

## Notes
- `StopSegmentTts()` is pause + state-clear. `IMediaTransport` has no `Stop()` method; a full unload is deferred to a future milestone if needed.
- `ActiveTtsSegmentId` cleared by three paths: explicit `StopSegmentTts()`, `player.Ended` event, `player.ErrorOccurred` event — state is always truthful.
- No UI wiring in this milestone. `PlaySegmentTtsAsync` and `StopSegmentTts` are coordinator methods ready for the UI surface (deferred to Milestone 7.5 or later).

## What Remains (Milestone 7 gate items not yet complete)
- UI surface exposing the segment list and playback controls to the user
- `SegmentStatus` enum or equivalent for accepted / needs-revision / pending states
- Compare alternative outputs (not implemented)
- Full end-to-end user journey: load → transcript → translate → generate → selectively refine → preview → save, all through UI

## Conclusion
Milestone 7.4 complete. Per-segment TTS playback is coordinator-driven and fully tested. `PlaySegmentTtsAsync` and `StopSegmentTts` are wired to `IMediaTransport`, `ActiveTtsSegmentId` is observable and always current, and the implementation survives session reopen.
