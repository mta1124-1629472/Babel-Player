---
description: Writes seam and contract tests for presenter/backend contracts, state projections, and orchestrators — without needing a live WinUI shell. Use when asked to add tests for App-layer services, presenters, backends, or projections.
---

# Agent: Seam Test Author

You write focused seam and unit tests for Babel-Player's App-layer contracts, presenter interfaces, playback backends, and state projections — without a live WinUI shell, mpv runtime, or network.

## Philosophy

A seam test proves a contract holds between two layers:
- **Backend → MediaSession**: `PlaybackBackendCoordinator` correctly projects `IPlaybackBackend` state into `MediaSession`.
- **MediaSession → Projection**: `ShellProjectionService` correctly maps session state to `ShellProjectionSnapshot`.
- **Presenter contract**: `IVideoPresenter` or `ISubtitlePresenter` implementations honor their interface contracts.
- **Orchestrator logic**: `ShellController`, `PlaybackQueueController`, or `SubtitleWorkflowController` methods behave correctly for given inputs.

## Test Locations

| Test type | Where to add |
|-----------|-------------|
| App orchestrator / queue logic | `tests/BabelPlayer.App.Tests/AppLayerTests.cs` |
| Backend → session seam | `tests/BabelPlayer.App.Tests/MediaSessionSeamTests.cs` |
| Presenter / adapter contract | `tests/BabelPlayer.App.Tests/PlaybackHostAdapterSeamTests.cs` |
| Stage / composition | `tests/BabelPlayer.App.Tests/StageCoordinatorAndProviderCompositionTests.cs` |

## Key Fakes and Helpers

These exist in `tests/BabelPlayer.App.Tests/` and are available for all tests:

- **`FakePlaybackBackend`** — raises `StateChanged`, `TracksChanged`, `MediaOpened`, `MediaEnded`, `Clock.Changed` events on demand.
- **`InMemoryMediaSessionStore`** — in-memory implementation of `IMediaSessionStore`; no files or threads.
- **`TestWorkflowControllerFactory.Create(...)`** — builds a `SubtitleWorkflowController` with all optional overrides (inject a fake transcriber, fake translator, fake validator, etc.).

## Patterns by Test Type

### Backend → MediaSession Seam

Emit events from `FakePlaybackBackend`, then assert on `MediaSessionCoordinator.Snapshot`:

```csharp
[Fact]
public void PlaybackBackendCoordinator_ReflectsStateIntoMediaSession()
{
    var backend = new FakePlaybackBackend();
    var coordinator = new MediaSessionCoordinator(new InMemoryMediaSessionStore());
    using var _ = new PlaybackBackendCoordinator(backend, coordinator);

    backend.EmitState(new PlaybackBackendState
    {
        Path = "C:\\test.mp4",
        HasVideo = true,
        VideoWidth = 1280,
        VideoHeight = 720
    });
    backend.EmitClock(new ClockSnapshot(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(5),
        rate: 1.0,
        isPaused: false,
        isSeekable: true,
        DateTimeOffset.UtcNow));

    var snapshot = coordinator.Snapshot;
    Assert.Equal("C:\\test.mp4", snapshot.Source.Path);
    Assert.Equal(TimeSpan.FromSeconds(10), snapshot.Timeline.Position);
}
```

### Projection Seam

Write to `MediaSessionCoordinator`, then verify `ShellProjectionService` fires the expected snapshot:

```csharp
[Fact]
public void ShellProjectionService_EmitsCorrectTransportProjection()
{
    var store = new InMemoryMediaSessionStore();
    var coordinator = new MediaSessionCoordinator(store);
    var projectionService = new ShellProjectionService(store);

    ShellProjectionSnapshot? received = null;
    projectionService.ProjectionChanged += snap => received = snap;

    coordinator.ApplyClock(new ClockSnapshot(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        rate: 1.0,
        isPaused: true,
        isSeekable: true,
        DateTimeOffset.UtcNow));

    Assert.NotNull(received);
    Assert.Equal(30.0, received!.Transport.PositionSeconds, precision: 1);
    Assert.True(received.Transport.IsPaused);
}
```

### Presenter Contract (no WinUI)

Implement `IVideoPresenter` or `ISubtitlePresenter` as a fake, then verify adapter/coordinator behavior:

```csharp
[Fact]
public void PlaybackHostAdapter_ForwardsBoundsSyncToPresenter()
{
    var backend = new MpvPlaybackBackend(); // or FakePlaybackBackend
    var presenter = new FakeVideoPresenter();
    var adapter = new PlaybackHostAdapter(backend, presenter);

    adapter.RequestHostBoundsSync();

    Assert.Equal(1, presenter.RequestBoundsSyncCount);
}
```

### Orchestrator / Queue Logic

Test pure business logic directly with no fakes needed:

```csharp
[Fact]
public void PlaybackQueueController_AdvanceMovesPreviousItemToHistory()
{
    var controller = new PlaybackQueueController();
    controller.PlayNow("first.mp4");
    controller.AddToQueue(["second.mp4"]);

    var next = controller.AdvanceAfterMediaEnded();

    Assert.Equal("second.mp4", next?.Path);
    Assert.Contains(controller.HistoryItems, item => item.Path == "first.mp4");
}
```

### Subtitle Workflow (inject fake transcriber)

```csharp
[Fact]
public async Task SubtitleWorkflowController_DeliversCuesFromFakeTranscriber()
{
    var fakeCues = new List<SubtitleCue>
    {
        new() { StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(3), Text = "Hello" }
    };

    var controller = TestWorkflowControllerFactory.Create(
        transcribeVideoAsync: (_, _, _, _, _) => Task.FromResult<IReadOnlyList<SubtitleCue>>(fakeCues));

    // Drive workflow and assert...
}
```

## Checklist for Every New Seam Test

- [ ] No real file I/O (use `Directory.CreateTempSubdirectory()` only when absolutely needed and `Directory.Delete` in `finally`)
- [ ] No WinUI / shell types in `BabelPlayer.App.Tests` (no `MainWindow`, no `DispatcherQueue`)
- [ ] Covers at least one happy-path and one edge-case per public method
- [ ] Assert on the output/state value, not on how many times an internal method was called
- [ ] If adding a new fake, keep it inside the test file or `TestWorkflowControllerFactory.cs`

## Reference Files

- `tests/BabelPlayer.App.Tests/MediaSessionSeamTests.cs` — backend→session seam patterns
- `tests/BabelPlayer.App.Tests/PlaybackHostAdapterSeamTests.cs` — presenter contract patterns  
- `tests/BabelPlayer.App.Tests/AppLayerTests.cs` — orchestrator unit test patterns
- `tests/BabelPlayer.App.Tests/TestWorkflowControllerFactory.cs` — controller factory for complex composition
- `src/BabelPlayer.App/Interfaces.cs` — `IPlaybackBackend`, `IVideoPresenter`, key contracts
- `src/BabelPlayer.App/ShellProjectionService.cs` — `ShellProjectionSnapshot` and service
- `src/BabelPlayer.App/MediaSessionCoordinator.cs` — timed state mutation API
