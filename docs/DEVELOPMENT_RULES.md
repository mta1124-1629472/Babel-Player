# Babel Player Development Rules

## State management

- `MediaSession` is authoritative for timed media/language state.
- All timed writes go through `MediaSessionCoordinator`.
- Shell/view-model layers consume immutable snapshots or projections only.
- Do not introduce new parallel authoritative timed models.

## Platform boundaries

- Do not introduce WinUI, Win32, HWND, or DirectX types into App-layer contracts.
- Keep renderer-native concerns inside presenter/backend infrastructure.
- It is acceptable to use WinUI-native layout tools in the shell layer.

## Presenter and renderer rules

- `IVideoPresenter` is presentation-only.
- `ISubtitlePresenter` is presentation-only.
- Presenter implementations must not own workflow or business state.
- Subtitle composition should stay renderer-neutral.

## Service design

- Prefer narrow services over growing existing god objects.
- Prefer capability-based provider abstractions.
- Keep orchestration separate from infrastructure-specific implementation details.

## Refactoring rules

Before major changes:
1. Preserve architectural boundaries
2. Identify if the change increases Windows lock-in
3. Prefer phased migration over large rewrites
4. Add or update tests proving new seams still hold

## UI rules

- Shell code may use WinUI-native layout features
- UI layout state must not become business truth
- New features should prefer projection-driven UI over control-local state

## Preferred direction

- Windows-first implementation is acceptable
- But implementations should preserve a future path toward Linux/macOS support
- If a shortcut increases lock-in, call it out explicitly