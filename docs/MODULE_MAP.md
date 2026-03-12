# Babel Player Module Map

## Repository layout

### `src/BabelPlayer.WinUI`
Shell and presentation layer.

Owns:
- Main window
- WinUI controls
- stage coordination
- presenter adapters
- window mode behavior

Examples:
- `MainWindow.xaml.cs`
- `StageCoordinator.cs`
- `MpvVideoPresenter.cs`
- `DetachedWindowSubtitlePresenter.cs`

### `src/BabelPlayer.App`
Application/domain orchestration.

Owns:
- `MediaSession`
- `MediaSessionCoordinator`
- queue/history state
- subtitle application workflows
- shell projections
- playback backend abstraction

Examples:
- `MediaSessionModels.cs`
- `MediaSessionCoordinator.cs`
- `ShellController.cs`
- `SubtitleApplicationService.cs`

### `src/BabelPlayer.Core`
Reusable lower-level services and provider-facing abstractions where applicable.

### `tests/`
Behavioral and seam-validation tests.

## Ownership guidance

### Add code here when...
- UI-only or presenter code → `BabelPlayer.WinUI`
- App state / workflow / projections → `BabelPlayer.App`
- Provider/runtime adapters → app/provider area or infrastructure area
- Pure reusable library logic → `BabelPlayer.Core`

## Current hotspots

### `MainWindow.xaml.cs`
Still a shell hotspot. Avoid adding new business logic here unless it is purely view wiring.

### `SubtitleApplicationService`
Large orchestration surface. Prefer splitting new responsibilities into narrower services rather than growing it.

### `MpvHostControl`
Transitional infrastructure. Avoid adding new app/business logic here.

## Transitional infrastructure

These are temporary or likely to be replaced:
- `MpvPlaybackBackend`
- `MpvVideoPresenter`
- `DetachedWindowSubtitlePresenter`
- `MpvHostControl`

New work should not deepen coupling to these more than necessary.