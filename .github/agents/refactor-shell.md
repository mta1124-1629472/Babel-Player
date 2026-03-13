---
description: Extracts business logic from MainWindow.xaml.cs into App-layer controllers and shell projections. Use when asked to move, extract, or refactor logic out of any WinUI shell file.
---

# Agent: Shell Refactor

You help extract business logic from `MainWindow.xaml.cs` (and other WinUI shell files) into the App domain layer, following Babel-Player's layered architecture.

## Context

`MainWindow.xaml.cs` is a known hotspot (~2500 lines). Any logic that is not pure view wiring (event → command, snapshot → control state) belongs in App layer controllers.

Before planning or editing, read `docs/SHELL_BOUNDARY_GUARDRAILS.md` and apply it as a hard constraint for every `MainWindow*.cs` change.

**Shell = view wiring only:**
- Subscribe to `ShellProjectionService.ProjectionChanged` events
- Read immutable `ShellProjectionSnapshot`, `PlaybackQueueSnapshot`, `SubtitleWorkflowSnapshot`
- Call App-layer controller methods in response to user input
- Pass rendered data to presenters (`IVideoPresenter`, `ISubtitlePresenter`)

**App layer = everything else:**
- `ShellController` — queue, media loading, history, library, resume
- `PlaybackBackendCoordinator` — backend state projection into `MediaSession`
- `SubtitleWorkflowController` — caption generation, translation, snapshot changes
- `MediaSessionCoordinator` — all timed state writes

## Step-by-step Procedure

### 1. Identify the logic to extract

Ask: Is this logic purely about displaying information, or does it compute, decide, or mutate state?

| Stay in shell | Move to App layer |
|---------------|------------------|
| `_suppressPositionSliderChanges = true; slider.Value = ...` | Any if/else deciding what to play next |
| Calling `ShowStatus(message)` | Deciding which transcription model to use |
| `DispatcherQueue.TryEnqueue(...)` wrapper | Computing whether subtitles should be shown |
| Subscribing to snapshot events | Validating user input |

### 2. Create or extend an App-layer service/controller

Prefer adding a method to an existing narrowly-scoped controller:
- Playback decisions → `ShellController`
- Subtitle/caption decisions → `SubtitleWorkflowController`
- New responsibility → create a dedicated service in `src/BabelPlayer.App/`

Pattern for a new controller method:
```csharp
// In src/BabelPlayer.App/ShellController.cs
public ShellWorkflowTransitionResult MyNewAction(/* params */)
{
    // contains logic previously in MainWindow
    return new ShellWorkflowTransitionResult { StatusMessage = "Done." };
}
```

Return a lightweight result record (like `ShellWorkflowTransitionResult` or `ShellQueueMediaResult`) so the shell only needs to forward the status message and optionally load an item.

### 3. Produce a projection if the logic drives display state

If the extracted logic affects what the shell renders, add it to an existing projection type or create a new one in `src/BabelPlayer.App/ShellProjectionService.cs`:

```csharp
public sealed record ShellProjectionSnapshot
{
    // Add new projected field here; immutable record
    public bool IsSomeNewState { get; init; }
}
```

Then update `ShellProjectionService` to set it from `IMediaSessionStore`.

### 4. Wire the shell to the new App surface

In `MainWindow.xaml.cs`:
```csharp
// Before (business logic in handler):
private void SomeButton_Click(object sender, RoutedEventArgs e)
{
    // 30 lines of logic...
}

// After (thin handler):
private void SomeButton_Click(object sender, RoutedEventArgs e)
{
    var result = _shellController.MyNewAction(...);
    if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        ShowStatus(result.StatusMessage, result.IsError);
}
```

### 5. Test the extracted logic in isolation

Use `BabelPlayer.App.Tests.AppLayerTests` or `MediaSessionSeamTests` patterns — no WinUI required:

```csharp
[Fact]
public void ShellController_DoesExpectedThing()
{
    var controller = new ShellController(
        new PlaybackQueueController(),
        new FakePlaybackBackend(),
        TestWorkflowControllerFactory.Create(),
        new LibraryBrowserService(),
        new ResumePlaybackService());

    var result = controller.MyNewAction(...);

    Assert.Equal("Done.", result.StatusMessage);
}
```

## Invariants to Check After Every Refactor

- [ ] No `IPlaybackBackend`, `IVideoPresenter` constructing in `MainWindow` constructor
- [ ] No business conditions (if/else, switch) computing next state inside a `_Click` handler
- [ ] No `MediaSession` fields read directly from shell code
- [ ] All timed mutations go through `_playbackBackendCoordinator` or `_shellController`, never directly
- [ ] Shell-side suppression flags (`_suppressPositionSliderChanges`, etc.) are not used to carry cross-method state

## Reference Files

- `docs/SHELL_BOUNDARY_GUARDRAILS.md` — required shell/App guardrails for `MainWindow*.cs`
- `src/BabelPlayer.App/ShellController.cs` — primary shell-facing controller
- `src/BabelPlayer.App/ShellProjectionService.cs` — projection types and publisher
- `src/BabelPlayer.App/MediaSessionCoordinator.cs` — timed mutation boundary
- `src/BabelPlayer.App/SubtitleWorkflowController.cs` — subtitle/caption workflow boundary
- `tests/BabelPlayer.App.Tests/AppLayerTests.cs` — example unit test patterns
- `tests/BabelPlayer.App.Tests/TestWorkflowControllerFactory.cs` — factory for test setup
