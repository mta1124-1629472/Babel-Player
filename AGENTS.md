# Babel Player – Codex Development Guidelines

These instructions apply to this repository and guide all architectural decisions.

## Quick Reference: Core Invariants

| Invariant | Implication | Key File |
|-----------|-------------|----------|
| `MediaSession` = only timed state | No parallel authoritative timed models | `src/BabelPlayer.App/MediaSessionModels.cs` |
| All timed writes through `MediaSessionCoordinator` | Single mutation boundary for timeline/selections | `src/BabelPlayer.App/MediaSessionCoordinator.cs` |
| Shell/WinUI is view-only | No business logic in `MainWindow.xaml.cs` | Extract logic to controllers in App layer |
| No WinUI/Win32/DirectX leaks from App | Platform-agnostic contracts at boundaries | Use `IPlaybackBackend`, `IVideoPresenter`, etc. |
| Presenters are stateless | State flows: App → Shell projections → Presenter | Never own state in presenter implementations |
| Narrow interfaces & adapters | Enable cross-platform porting (Linux/macOS future) | Use immutable projections at layer boundaries |

## Primary Priorities

### 1. `MediaSession` is the Single Source of Truth

- **What it owns**: timeline position, active stream selections, transcript/translation segments, subtitle presentation state, language processing state.
- **Why**: Eliminates race conditions; enables clean shell/app separation.
- **Location**: `src/BabelPlayer.App/MediaSessionModels.cs`.
- **Antipattern**: Storing playback position in both `MediaSession` and a WinUI control.

### 2. `MediaSessionCoordinator` is the Timed Mutation Boundary

- **What it does**: Every timed state change flows through this coordinator.
- **Why**: Single point of control + testability; enforces immutability contracts.
- **Location**: `src/BabelPlayer.App/MediaSessionCoordinator.cs`.
- **Antipattern**: Directly modifying `MediaSession.TimelinePosition` without coordinator.

### 3. Platform-Native Code is Isolated

- **App layer**: Remains platform-agnostic (no `AppWindow`, `HWND`, `DX11Device`, WinUI types).
- **Infrastructure**: mpv, Win32 interop, DirectX, WinUI presenters live here.
- **How**: Adapter pattern (`IPlaybackBackend` → `MpvPlaybackBackend`).
- **Antipattern**: Exporting Windows-specific types in App-layer public contracts.

### 4. Shell Layer is View-Only

- **Responsibilities**: WinUI visual tree, layout, event wiring, presenter attachment.
- **What it cannot do**: Business logic, state ownership (except UI-only state like "which pane is visible").
- **Controllers**: Business logic lives in App layer (`ShellController`, `PlaybackBackendCoordinator`, `SubtitleWorkflowController`).
- **Why**: Enables testing without shell; keeps shell logic minimal.
- **Antipattern**: Large switch statements in `MainWindow.xaml.cs` → extract to controllers.

### 5. Presenters are Stateless Adapters

- **`IVideoPresenter`**: Accepts display dimensions, position, tracks; renders video. No state ownership.
- **`ISubtitlePresenter`**: Accepts subtitle text, styling, position; renders UI overlay. No workflow state.
- **How state flows**: `MediaSession` → App projections (e.g., `SubtitlePresentationProjector`) → Shell observes and calls presenter.
- **Antipattern**: Presenter deciding which translation model to use; instead, App layer decides and passes text to presenter.

### 6. Seams are Preserved for Cross-Platform Support

- **Narrow interfaces**: Each component exposes only what's needed (capability-based, not god objects).
- **Immutable projections**: Shell consumes immutable views of App state, never writes directly.
- **Adapters + abstraction**: mpv, transcription providers, translation providers are swappable.
- **Why**: Unblocks Linux/macOS porting without App-layer rewrites.
- **Antipattern**: Tight coupling (e.g., `MainWindow` directly calling `mpv` APIs).

## Architectural Boundaries

### Layers

- **Shell** (`BabelPlayer.WinUI`): WinUI visual tree, XAML, layout, presenter attachment, window mode logic, event wiring.
- **App Domain** (`BabelPlayer.App`): `MediaSession` state management, orchestrators (`MediaSessionCoordinator`, `ShellController`, `SubtitleWorkflowController`), queue/history, shell projections.
- **Core** (`BabelPlayer.Core`): Reusable, platform-agnostic services and utilities.

### Data Flow at Boundaries

```
┌─────────────────────────────────────────────────────────┐
│ App Domain Layer                                        │
│ ┌──────────────────┐        ┌────────────────────────┐ │
│ │  MediaSession    │───────→│ Shell Projections      │ │
│ │  (authoritative) │        │ (immutable snapshots)  │ │
│ └──────────────────┘        └────────────────────────┘ │
│         ↑                            │                  │
│         │ via Coordinator            ↓                  │
│         └────────────────────────────┐                  │
└─────────────────────────────────────────────────────────┘
                                       │
┌─────────────────────────────────────────────────────────┐
│ Shell Layer (WinUI)                                     │
│ ┌──────────────────┐        ┌────────────────────────┐ │
│ │ Shell Controls   │───────→│ Presenters             │ │
│ │ (observe)        │        │ (render only)          │ │
│ └──────────────────┘        └────────────────────────┘ │
└─────────────────────────────────────────────────────────┘
```

## Implementation Style

- **Prefer phased migrations** with explicit exit criteria over large rewrites.
- **Prefer narrow interfaces** over god objects (e.g., many focused services vs. one `SubtitleService`).
- **Prefer immutable projections** at boundaries (shell consumes snapshots, never writes).
- **Prefer adapters** for infrastructure (playback, transcription, translation backends).

## Refactoring Checklist

Before proposing any architectural change:

1. **Read the docs first**:
   - `docs/ARCHITECTURE.md` (canonical state model).
   - `docs/MODULE_MAP.md` (module ownership and hotspots).
   - `docs/DEVELOPMENT_RULES.md` (detailed constraints).

2. **Architectural fit**:
   - [ ] Does this increase Windows lock-in? If yes, justify explicitly in PR.
   - [ ] Do you introduce new timed state? If yes, route all writes through `MediaSessionCoordinator`.
   - [ ] Do you cross layers (e.g., App → Shell → Presenter)? If yes, use immutable projections.

3. **Testing**:
   - [ ] Add unit tests proving the new seams still hold.
   - [ ] If adding a new presenter, validate its contract with a seam test (no shell).
   - [ ] If modifying `MediaSessionCoordinator`, add tests covering new state transitions.

## Testing Strategy

- **Unit tests** (`BabelPlayer.App.Tests`): Test orchestrators (`MediaSessionCoordinator`, `ShellController`), state projections, and service logic in isolation.
- **Seam tests**: Validate presenter/backend contracts without a full shell (e.g., test `IVideoPresenter` implementation directly).
- **Integration tests**: Test mpv runtime, transcription services, and shell attachment (Windows environment required).

## Common Pitfalls and How to Avoid Them

| Pitfall | Why It's Bad | How to Fix |
|---------|-------------|-----------|
| Storing playback state in both `MediaSession` and a WinUI control | Race conditions; out-of-sync UI | Single source of truth: `MediaSession` only |
| Business logic in `MainWindow.xaml.cs` | Hard to test, shell becomes god object | Extract to controllers in App layer |
| Presenter owning workflow state (e.g., translation choices) | Breaks statelessness; couples presenter to domain | Presenter receives pre-decided state from App → shell projection |
| Exporting `AppWindow`, `HWND` from App-layer public contracts | Locks-in to Windows; breaks portability | Use platform-agnostic interfaces (`ISurfaceProvider`, etc.) |
| Multiple parallel authoritative timed state models | Sync bugs, data loss, inconsistency | All timed writes through `MediaSessionCoordinator` only |
| Tight coupling in presenter implementations (e.g., `MainWindow` directly calling mpv) | Hard to swap implementations; breaks testability | Use adapter pattern and inject via constructor |

## Specialized Agents

Three focused agents exist in `.github/agents/`. Use them automatically when the task matches — do not attempt the work inline.

| Agent | File | Invoke when... |
|-------|------|----------------|
| `refactor-shell` | `.github/agents/refactor-shell.md` | Moving or extracting logic out of `MainWindow.xaml.cs` or any WinUI shell file into App-layer controllers or projections |
| `seam-test` | `.github/agents/seam-test.md` | Writing tests for presenter contracts, backend→session seams, projection correctness, or orchestrator behavior without a live shell |
| `new-provider` | `.github/agents/new-provider.md` | Adding a new transcription or translation provider adapter (any new AI service, cloud API, or local model) |

**Trigger words that should invoke these agents:**

- "move logic out of MainWindow", "extract to controller", "refactor shell" → `refactor-shell`
- "write a test", "add a seam test", "add unit test", "test this in isolation" → `seam-test`
- "add a provider", "integrate `[service]`", "new transcription model", "new translation adapter" → `new-provider`

## Example Prompts

- "Create a new narrow service for resumePlayback logic; test it in isolation without the shell."
- "Refactor this MainWindow snippet X to use a shell projection instead of direct state mutation."
- "What's the minimal interface needed for a custom video presenter?"
- "Validate the new subtitle presenter contract with a seam test."

## Documentation References

- [.github/copilot-instructions.md](.github/copilot-instructions.md) — Day-to-day workspace setup and patterns.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — Authoritative state definition and layer design.
- [docs/MODULE_MAP.md](docs/MODULE_MAP.md) — Module ownership, hotspots, and refactoring boundaries.
- [docs/DEVELOPMENT_RULES.md](docs/DEVELOPMENT_RULES.md) — Detailed operational rules and constraints (state, platforms, presenters, services, UI).
