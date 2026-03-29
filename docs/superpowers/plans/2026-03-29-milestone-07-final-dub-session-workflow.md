# Milestone 7 Final: Sequential Dub Playback + State/Cancellation + Minimal Interaction Surface

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete Milestone 7 by adding sequential dubbed-segment playback, explicit coordinator playback state, and stop/cancel — with all behavior verified by tests and a smoke note.

**Architecture:** A new `PlaybackState` enum (Idle/PlayingSingleSegment/PlayingSequence) is added as an observable coordinator property. Sequential playback is driven by `PlayAllDubbedSegmentsAsync()` using a `CancellationTokenSource` for clean stop/cancel. State is owned exclusively in `SessionWorkflowCoordinator` per architecture rules.

**Tech Stack:** C# 10 / .NET 10, CommunityToolkit.Mvvm `[ObservableProperty]`, xUnit, `CancellationTokenSource`, `IMediaTransport` (existing), `FakeSegmentPlayer` (test double, extended)

---

## Readiness Check

Confirmed before writing this plan:
- `PlaySegmentTtsAsync(segmentId)` and `StopSegmentTts()` exist and are tested (35/35 passing)
- `ActiveTtsSegmentId` is `[ObservableProperty]` on the coordinator
- `FakeSegmentPlayer` is in `SessionWorkflowTests.cs` (internal class)
- `TtsSegmentAudioPaths` persists across reopen (tested)
- `GetSegmentWorkflowListAsync()` returns `List<WorkflowSegmentState>` ordered by transcript read order
- `IMediaTransport` has `HasEnded`, `Ended` event, `Load(string)`, `Play()`, `Pause()`
- `LibMpvHeadlessTransport.Ended` is never raised (declared with `#pragma warning disable CS0067`)
- Sequence waits for `HasEnded` polling (not `Ended` event) to work correctly with real libmpv

## Known Limitation

`LibMpvHeadlessTransport.Ended` is declared but never raised. Sequential playback with real audio uses `HasEnded` polling (`while (!player.HasEnded)`) for segment completion detection. `FakeSegmentPlayer` simulates this by setting `HasEnded = true` from `Play()`.

---

## File Map

| File | Change |
|------|--------|
| `Models/PlaybackState.cs` | CREATE — `PlaybackState` enum |
| `Services/SessionWorkflowCoordinator.cs` | MODIFY — add `PlaybackState` property, `_sequenceCts`, `PlayAllDubbedSegmentsAsync()`, rename `StopSegmentTts→StopPlayback`, update `PlaySegmentTtsAsync`, `Dispose` |
| `BabelDeck.Tests/SessionWorkflowTests.cs` | MODIFY — extend `FakeSegmentPlayer`, rename stop test, add 6 tests |
| `docs/smoke/milestone-07-final-dub-session-workflow.md` | CREATE — smoke note |

---

## Task 1: Add `PlaybackState` enum

**Files:**
- Create: `Models/PlaybackState.cs`

- [ ] **Step 1: Write the file**

```csharp
namespace Babel.Deck.Models;

public enum PlaybackState
{
    Idle = 0,
    PlayingSingleSegment = 1,
    PlayingSequence = 2,
}
```

- [ ] **Step 2: Build to confirm no errors**

```
dotnet build Babel-Deck.sln
```
Expected: Build succeeded, 0 errors.

---

## Task 2: Extend `FakeSegmentPlayer` + rename stop test (RED)

Write the test infrastructure and the 6 failing tests before touching production code.

**Files:**
- Modify: `BabelDeck.Tests/SessionWorkflowTests.cs`

- [ ] **Step 1: Replace `FakeSegmentPlayer` with the extended version**

Find the existing `FakeSegmentPlayer` at the bottom of `SessionWorkflowTests.cs` and replace it:

```csharp
internal sealed class FakeSegmentPlayer : IMediaTransport
{
    private readonly bool _simulateInstantEnd;
    private bool _hasEnded;

    public string? LastLoadedPath { get; private set; }
    public List<string> LoadedPaths { get; } = new();
    public bool PlayCalled { get; private set; }
    public bool PauseCalled { get; private set; }

    public FakeSegmentPlayer(bool simulateInstantEnd = true)
    {
        _simulateInstantEnd = simulateInstantEnd;
    }

    public void Load(string filePath)
    {
        LastLoadedPath = filePath;
        LoadedPaths.Add(filePath);
        _hasEnded = false;
        PlayCalled = false;
    }

    public void Play()
    {
        PlayCalled = true;
        if (_simulateInstantEnd)
        {
            _hasEnded = true;
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Pause() { PauseCalled = true; PlayCalled = false; }
    public void Seek(long positionMs) { }
    public long CurrentTime => 0;
    public long Duration => 5000;
    public bool HasEnded => _hasEnded;
#pragma warning disable CS0067
    public event EventHandler? Ended;
    public event EventHandler<Exception>? ErrorOccurred;
#pragma warning restore CS0067
    public void Dispose() { }
}
```

- [ ] **Step 2: Rename `StopSegmentTts_ClearsActiveSegment` → `StopPlayback_ClearsActiveSegment` and update the call**

Find the test `StopSegmentTts_ClearsActiveSegment` in `SessionWorkflowTests.cs`. Rename it and change the call from `coordinator.StopSegmentTts()` to `coordinator.StopPlayback()`.

- [ ] **Step 3: Add 6 failing tests**

Add the following tests to `SessionWorkflowTests` class (before the closing `}`):

```csharp
// --- Milestone 7 Final: Sequential Playback + State/Cancellation ---

[Fact]
public async Task PlayAllDubbedSegments_PlaysInOrder()
{
    var stateFilePath = Path.Combine(_testStateDir, "session_playall_order.json");
    _lastStateFilePath = stateFilePath;
    var log = new AppLog(GetTestLogPath());
    var store = new SessionSnapshotStore(stateFilePath, log);
    var fake = new FakeSegmentPlayer(simulateInstantEnd: true);
    var coordinator = new SessionWorkflowCoordinator(store, log, fake);
    coordinator.Initialize();

    coordinator.LoadMedia(_testMediaPath);
    await coordinator.TranscribeMediaAsync();
    await coordinator.TranslateTranscriptAsync("en", "es");
    await coordinator.GenerateTtsAsync();

    var segments = await coordinator.GetSegmentWorkflowListAsync();
    Assert.True(segments.Count >= 2, "Need at least 2 segments");

    // Regen TTS for first 2 segments (they will have HasTtsAudio = true)
    await coordinator.RegenerateSegmentTtsAsync(segments[0].SegmentId);
    await coordinator.RegenerateSegmentTtsAsync(segments[1].SegmentId);

    var expectedFirst = coordinator.CurrentSession.TtsSegmentAudioPaths![segments[0].SegmentId];
    var expectedSecond = coordinator.CurrentSession.TtsSegmentAudioPaths![segments[1].SegmentId];

    await coordinator.PlayAllDubbedSegmentsAsync();

    // Verify played in order by checking LoadedPaths
    Assert.True(fake.LoadedPaths.Count >= 2, "Expected at least 2 segments played");
    Assert.Equal(expectedFirst, fake.LoadedPaths[0]);
    Assert.Equal(expectedSecond, fake.LoadedPaths[1]);
    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);
}

[Fact]
public async Task StopPlayback_CancelsSequence()
{
    var stateFilePath = Path.Combine(_testStateDir, "session_stop_sequence.json");
    _lastStateFilePath = stateFilePath;
    var log = new AppLog(GetTestLogPath());
    var store = new SessionSnapshotStore(stateFilePath, log);
    // simulateInstantEnd: false — sequence will hang at HasEnded polling until cancelled
    var fake = new FakeSegmentPlayer(simulateInstantEnd: false);
    var coordinator = new SessionWorkflowCoordinator(store, log, fake);
    coordinator.Initialize();

    coordinator.LoadMedia(_testMediaPath);
    await coordinator.TranscribeMediaAsync();
    await coordinator.TranslateTranscriptAsync("en", "es");
    await coordinator.GenerateTtsAsync();

    var segments = await coordinator.GetSegmentWorkflowListAsync();
    await coordinator.RegenerateSegmentTtsAsync(segments[0].SegmentId);

    // Start sequence (do NOT await — it will hang polling HasEnded)
    var sequenceTask = coordinator.PlayAllDubbedSegmentsAsync();
    Assert.Equal(PlaybackState.PlayingSequence, coordinator.PlaybackState);

    // Cancel it
    coordinator.StopPlayback();
    await sequenceTask;

    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);
    Assert.Null(coordinator.ActiveTtsSegmentId);
}

[Fact]
public async Task PlaySegmentThenPlayAll_TransitionsStateCleanly()
{
    var stateFilePath = Path.Combine(_testStateDir, "session_single_then_all.json");
    _lastStateFilePath = stateFilePath;
    var log = new AppLog(GetTestLogPath());
    var store = new SessionSnapshotStore(stateFilePath, log);
    var fake = new FakeSegmentPlayer(simulateInstantEnd: false);
    var coordinator = new SessionWorkflowCoordinator(store, log, fake);
    coordinator.Initialize();

    coordinator.LoadMedia(_testMediaPath);
    await coordinator.TranscribeMediaAsync();
    await coordinator.TranslateTranscriptAsync("en", "es");
    await coordinator.GenerateTtsAsync();

    var segments = await coordinator.GetSegmentWorkflowListAsync();
    await coordinator.RegenerateSegmentTtsAsync(segments[0].SegmentId);

    // Play single segment (fake doesn't fire Ended, so PlaybackState stays PlayingSingleSegment)
    await coordinator.PlaySegmentTtsAsync(segments[0].SegmentId);
    Assert.Equal(PlaybackState.PlayingSingleSegment, coordinator.PlaybackState);

    // Start PlayAll — should cancel single, transition to PlayingSequence
    var sequenceTask = coordinator.PlayAllDubbedSegmentsAsync();
    Assert.Equal(PlaybackState.PlayingSequence, coordinator.PlaybackState);

    // Stop cleanly
    coordinator.StopPlayback();
    await sequenceTask;
    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);
}

[Fact]
public async Task PlaybackState_ReflectsIdleSingleSequenceStopped()
{
    var stateFilePath = Path.Combine(_testStateDir, "session_state_lifecycle.json");
    _lastStateFilePath = stateFilePath;
    var log = new AppLog(GetTestLogPath());
    var store = new SessionSnapshotStore(stateFilePath, log);
    var fake = new FakeSegmentPlayer(simulateInstantEnd: false);
    var coordinator = new SessionWorkflowCoordinator(store, log, fake);
    coordinator.Initialize();

    coordinator.LoadMedia(_testMediaPath);
    await coordinator.TranscribeMediaAsync();
    await coordinator.TranslateTranscriptAsync("en", "es");
    await coordinator.GenerateTtsAsync();

    var segments = await coordinator.GetSegmentWorkflowListAsync();
    await coordinator.RegenerateSegmentTtsAsync(segments[0].SegmentId);

    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);

    await coordinator.PlaySegmentTtsAsync(segments[0].SegmentId);
    Assert.Equal(PlaybackState.PlayingSingleSegment, coordinator.PlaybackState);

    coordinator.StopPlayback();
    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);

    var sequenceTask = coordinator.PlayAllDubbedSegmentsAsync();
    Assert.Equal(PlaybackState.PlayingSequence, coordinator.PlaybackState);

    coordinator.StopPlayback();
    await sequenceTask;
    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);
}

[Fact]
public async Task MissingSegmentTts_InSequence_IsHandledTruthfully()
{
    var stateFilePath = Path.Combine(_testStateDir, "session_missing_in_sequence.json");
    _lastStateFilePath = stateFilePath;
    var log = new AppLog(GetTestLogPath());
    var store = new SessionSnapshotStore(stateFilePath, log);
    var fake = new FakeSegmentPlayer(simulateInstantEnd: true);
    var coordinator = new SessionWorkflowCoordinator(store, log, fake);
    coordinator.Initialize();

    coordinator.LoadMedia(_testMediaPath);
    await coordinator.TranscribeMediaAsync();
    await coordinator.TranslateTranscriptAsync("en", "es");
    await coordinator.GenerateTtsAsync();

    var segments = await coordinator.GetSegmentWorkflowListAsync();
    Assert.True(segments.Count >= 2, "Need at least 2 segments");

    await coordinator.RegenerateSegmentTtsAsync(segments[0].SegmentId);
    await coordinator.RegenerateSegmentTtsAsync(segments[1].SegmentId);

    // Delete the first segment's audio file to simulate a missing artifact
    var missingPath = coordinator.CurrentSession.TtsSegmentAudioPaths![segments[0].SegmentId];
    File.Delete(missingPath);

    var expectedPath = coordinator.CurrentSession.TtsSegmentAudioPaths![segments[1].SegmentId];

    // Should not throw — missing segments are skipped truthfully
    await coordinator.PlayAllDubbedSegmentsAsync();

    // Only the second segment should have been loaded
    Assert.DoesNotContain(missingPath, fake.LoadedPaths);
    Assert.Contains(expectedPath, fake.LoadedPaths);
    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);
}

[Fact]
public async Task Reopen_PlayAllDubbedSegments_UsesPersistedArtifacts()
{
    var stateFilePath = Path.Combine(_testStateDir, "session_reopen_playall.json");
    _lastStateFilePath = stateFilePath;
    var log = new AppLog(GetTestLogPath());
    var store = new SessionSnapshotStore(stateFilePath, log);

    var coordinator = new SessionWorkflowCoordinator(store, log);
    coordinator.Initialize();

    coordinator.LoadMedia(_testMediaPath);
    await coordinator.TranscribeMediaAsync();
    await coordinator.TranslateTranscriptAsync("en", "es");
    await coordinator.GenerateTtsAsync();

    var segments = await coordinator.GetSegmentWorkflowListAsync();
    await coordinator.RegenerateSegmentTtsAsync(segments[0].SegmentId);

    var expectedPath = coordinator.CurrentSession.TtsSegmentAudioPaths![segments[0].SegmentId];

    // Reopen with a fresh coordinator and fake player
    var fake2 = new FakeSegmentPlayer(simulateInstantEnd: true);
    coordinator = new SessionWorkflowCoordinator(store, log, fake2);
    coordinator.Initialize();

    await coordinator.PlayAllDubbedSegmentsAsync();

    Assert.Contains(expectedPath, fake2.LoadedPaths);
    Assert.Equal(PlaybackState.Idle, coordinator.PlaybackState);
}
```

- [ ] **Step 4: Build and confirm it fails for the right reasons**

```
dotnet build Babel-Deck.sln
```
Expected: Build FAILED with errors like:
- `'SessionWorkflowCoordinator' does not contain a definition for 'StopPlayback'`
- `'SessionWorkflowCoordinator' does not contain a definition for 'PlayAllDubbedSegmentsAsync'`
- `'SessionWorkflowCoordinator' does not contain a definition for 'PlaybackState'`
- `'PlaybackState' could not be found`

---

## Task 3: Implement `PlaybackState` property, `_sequenceCts`, updated handlers (GREEN)

**Files:**
- Modify: `Services/SessionWorkflowCoordinator.cs`

- [ ] **Step 1: Add using for Models namespace (already imported via file-level), add fields and property**

In `SessionWorkflowCoordinator.cs`, after the existing `private readonly EventHandler<Exception>? _segmentErrorHandler;` line, add:

```csharp
private CancellationTokenSource? _sequenceCts;

[ObservableProperty]
private PlaybackState _playbackState;
```

At the top of the file, add the using if not present:
```csharp
using Babel.Deck.Models;
```

(It's already in the namespace, so the enum is accessible without extra using.)

- [ ] **Step 2: Update `_segmentEndedHandler` and `_segmentErrorHandler` in the constructor**

In the constructor, replace:
```csharp
_segmentEndedHandler = (_, _) => ActiveTtsSegmentId = null;
_segmentErrorHandler = (_, ex) => ActiveTtsSegmentId = null;
```
With:
```csharp
_segmentEndedHandler = (_, _) =>
{
    ActiveTtsSegmentId = null;
    if (PlaybackState == PlaybackState.PlayingSingleSegment)
        PlaybackState = PlaybackState.Idle;
};
_segmentErrorHandler = (_, _) =>
{
    ActiveTtsSegmentId = null;
    if (PlaybackState == PlaybackState.PlayingSingleSegment)
        PlaybackState = PlaybackState.Idle;
};
```

- [ ] **Step 3: Update `Dispose()` to cancel and dispose `_sequenceCts`**

In `Dispose()`, before the event unsubscription block, add:
```csharp
_sequenceCts?.Cancel();
_sequenceCts?.Dispose();
_sequenceCts = null;
```

- [ ] **Step 4: Build and confirm still fails (GREEN step incomplete)**

```
dotnet build Babel-Deck.sln
```
Expected: Still fails — `StopPlayback`, `PlayAllDubbedSegmentsAsync` not yet added.

---

## Task 4: Add `PlayAllDubbedSegmentsAsync`, rename `StopSegmentTts` → `StopPlayback`, update `PlaySegmentTtsAsync` (GREEN)

**Files:**
- Modify: `Services/SessionWorkflowCoordinator.cs`

- [ ] **Step 1: Rename `StopSegmentTts()` to `StopPlayback()` and expand it**

Replace the entire `StopSegmentTts()` method:
```csharp
public void StopSegmentTts()
{
    try
    {
        _segmentPlayer?.Pause();
    }
    finally
    {
        ActiveTtsSegmentId = null;
    }
}
```
With:
```csharp
public void StopPlayback()
{
    _sequenceCts?.Cancel();
    try
    {
        _segmentPlayer?.Pause();
    }
    finally
    {
        ActiveTtsSegmentId = null;
        PlaybackState = PlaybackState.Idle;
    }
}
```

- [ ] **Step 2: Update `PlaySegmentTtsAsync` to cancel sequence and set state**

Replace the internal stop call in `PlaySegmentTtsAsync`. The method currently has:
```csharp
StopSegmentTts();

var player = GetOrCreateSegmentPlayer();
player.Load(audioPath);
ActiveTtsSegmentId = segmentId;
await Task.Run(() => player.Play());
```
Replace with:
```csharp
_sequenceCts?.Cancel();
_segmentPlayer?.Pause();
ActiveTtsSegmentId = null;

PlaybackState = PlaybackState.PlayingSingleSegment;

var player = GetOrCreateSegmentPlayer();
player.Load(audioPath);
ActiveTtsSegmentId = segmentId;
await Task.Run(() => player.Play());
```

- [ ] **Step 3: Add `PlayAllDubbedSegmentsAsync()`**

Add this method after `PlaySegmentTtsAsync`:

```csharp
public async Task PlayAllDubbedSegmentsAsync()
{
    // Cancel any running single-segment or sequence playback
    _sequenceCts?.Cancel();
    _sequenceCts = new CancellationTokenSource();
    var token = _sequenceCts.Token;

    PlaybackState = PlaybackState.PlayingSequence;

    try
    {
        var segments = await GetSegmentWorkflowListAsync();
        var dubbed = segments
            .Where(s => s.HasTtsAudio)
            .OrderBy(s => s.StartSeconds)
            .ToList();

        var player = GetOrCreateSegmentPlayer();

        foreach (var segment in dubbed)
        {
            token.ThrowIfCancellationRequested();

            var paths = CurrentSession.TtsSegmentAudioPaths;
            if (paths is null || !paths.TryGetValue(segment.SegmentId, out var audioPath))
                continue; // HasTtsAudio=true but no path — skip truthfully

            if (!File.Exists(audioPath))
            {
                _log.Warning($"Segment TTS artifact missing during sequence: {audioPath}");
                continue; // File gone from disk — skip truthfully
            }

            player.Load(audioPath);
            ActiveTtsSegmentId = segment.SegmentId;
            await Task.Run(() => player.Play(), token);

            // Wait for this segment to end or for cancellation
            while (!player.HasEnded)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(50, token);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Cancelled by StopPlayback() or a new playback starting — exit cleanly
    }
    finally
    {
        ActiveTtsSegmentId = null;
        // Only reset to Idle if we're still in PlayingSequence (may have been superseded by single-segment)
        if (PlaybackState == PlaybackState.PlayingSequence)
            PlaybackState = PlaybackState.Idle;
    }
}
```

- [ ] **Step 4: Build and verify it succeeds**

```
dotnet build Babel-Deck.sln
```
Expected: Build succeeded, 0 errors.

---

## Task 5: Verify GREEN — all 41 tests pass

- [ ] **Step 1: Run all tests**

```
dotnet test BabelDeck.Tests/BabelDeck.Tests.csproj
```
Expected output:
```
Total tests: 41
Passed: 41
```

If any test fails, diagnose before proceeding. Do not move to Task 6 until all pass.

---

## Task 6: Write smoke note

**Files:**
- Create: `docs/smoke/milestone-07-final-dub-session-workflow.md`

- [ ] **Step 1: Create smoke note with all required sections**

```markdown
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
dotnet build Babel-Deck.sln
dotnet test BabelDeck.Tests/BabelDeck.Tests.csproj
```

### Test Results
```
Total tests: 41
Passed: 41

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

## Conclusion
Milestone 7 gate is satisfied. A session can proceed from load → transcript → translate → generate → per-segment refinement → sequential playback → stop, with all state persisted across reopen. The coordinator is the sole state owner. The interaction surface (PlaySegmentTtsAsync, PlayAllDubbedSegmentsAsync, StopPlayback, PlaybackState, ActiveTtsSegmentId) is ready for UI wiring in Milestone 8.

## Deferred Items
- UI surface for segment list, playback controls (Milestone 8)
- `SegmentStatus` enum for accepted/needs-revision/pending states
- Compare alternative TTS outputs
- Real audio auto-advance (requires `LibMpvHeadlessTransport` to raise `Ended` event on EOF)
- Full end-to-end manual smoke path through UI
```

- [ ] **Step 2: Update proactivity session state**

Write to `~/proactivity/session-state.md`:
```
# Session State
- Current objective: Milestone 7 complete
- Last confirmed decision: All 7 sub-milestones done, 41/41 passing, smoke note written
- Blocker or open question: —
- Next useful move: Milestone 8 — Embedded Playback and In-Context Preview (PLAN.md §8)
```

---

## Self-Review Against Spec

| Spec requirement | Task covering it |
|-----------------|-----------------|
| Coordinator plays segments sequentially | Task 4 — `PlayAllDubbedSegmentsAsync()` |
| Skip segments without TTS artifact truthfully | Task 4 — `continue` when `!File.Exists` |
| Stop sequence when requested | Task 4 — `StopPlayback()` cancels CTS |
| Explicit playback state (idle/single/sequence) | Tasks 1+3+4 — `PlaybackState` enum + property |
| Stop/cancel interrupts single-segment | Task 4 — `StopPlayback()` pauses player |
| Stop/cancel interrupts sequence | Task 4 — CTS cancellation |
| New playback doesn't leave old state dangling | Task 4 — `PlaySegmentTtsAsync` cancels CTS; sequence finally guards PlaybackState |
| State transitions explicit and testable | Task 2 — 6 tests verify all transitions |
| `PlayAllDubbedSegments_PlaysInOrder` | Task 2 |
| `StopPlayback_CancelsSequence` | Task 2 |
| `PlaySegmentThenPlayAll_TransitionsStateCleanly` | Task 2 |
| `PlaybackState_ReflectsIdleSingleSequenceStopped` | Task 2 |
| `MissingSegmentTts_InSequence_IsHandledTruthfully` | Task 2 |
| `Reopen_PlayAllDubbedSegments_UsesPersistedArtifacts` | Task 2 |
| Smoke note at `docs/smoke/milestone-07-final-dub-session-workflow.md` | Task 6 |
| No embedded video UI, no timeline scrubbing, no advanced sync | No UI files touched |
