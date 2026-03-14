# Copilot Workspace Instructions for Babel-Player

## Overview

Babel-Player is a desktop media player for local video with embedded subtitle generation and translation workflows. The supported runtime shell is `src/BabelPlayer.Avalonia`. The codebase is organized around a **MediaSession-centered architecture** that keeps timed state authoritative, separates platform concerns, and preserves a future path to cross-platform support.

**See also**: `AGENTS.md` for architectural constraints, `docs/DEVELOPMENT_RULES.md` for operational rules, and `docs/SHELL_BOUNDARY_GUARDRAILS.md` for required `MainWindow*.cs` shell/App boundary rules.

## Quick Start

### Build
```powershell
dotnet build src/BabelPlayer.Avalonia/BabelPlayer.Avalonia.csproj
```

### Run
```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```
Or in Visual Studio: set `BabelPlayer.Avalonia` as startup project and press F5.

### Test
```powershell
dotnet test tests/BabelPlayer.App.Tests/BabelPlayer.App.Tests.csproj
```
Integration tests require Windows environment. See `tests/` for xUnit/MSTest patterns.

## Architecture at a Glance

### Layers
- **Shell** (`BabelPlayer.Avalonia`, active runtime): visual tree, layout, presenter attachment, event wiring—view logic only.
- **App Domain** (`BabelPlayer.App`): `MediaSession` state, `MediaSessionCoordinator` mutations, queue/history, subtitle workflows, shell projections.
- **Core** (`BabelPlayer.Core`): Reusable services and cross-layer abstractions.
- **Tests** (`tests/`): Behavioral and seam-validation tests.

### Critical Invariants
- **`MediaSession` is authoritative** for timed media state (timeline, stream selections, transcript, translation, presentation state).
- **`MediaSessionCoordinator` is the mutation boundary** for all timed writes.
- **No parallel timed state models**—all updates flow through the coordinator.
- **No Windows-specific types** (`WinUI`, `Win32`, `HWND`, `DirectX`) leak into App-layer contracts.
- **Platform-neutral contracts** where practical; platform-native code stays in presenters/backends.

## Coding Patterns

### State and Projections
- App layer produces **immutable snapshots** (`MediaSession`) or **projections** (filtered/computed views).
- Shell consumes snapshots/projections; never writes.
- Timed writes (playback position, track selection, etc.) route through `MediaSessionCoordinator`.

### UI & Threading
- Always use `async/await`; never block the UI thread.
- Use `DispatcherQueue` for cross-thread updates (shell observations).
- Prefer snapshot-driven UI over control-local state.

### Error Handling
- Use try-catch with logging via `IBabelLogger` or `ILogger<T>`.
- Log exceptions with context; don't swallow silently.

### Naming
- Classes/properties/method parameters: **PascalCase**.
- Local variables/fields: **camelCase**.
- Private fields: `_camelCase`.

## Hot Spots & Key Boundaries

### ⚠️ MainWindow.xaml.cs (2500+ lines)
Shell hotspot. Business logic here is a code smell.
**Better**: Extract to a controller, expose immutable snapshots or events, consume in shell.

### ⚠️ SubtitleApplicationService
Large orchestration surface. Avoid growing it further.
**Better**: Split new workflows into narrow, single-purpose services; compose them.

### ⚠️ Presenter ownership
Presenters (`IVideoPresenter`, `ISubtitlePresenter`) are **presentation-only**; they must not own workflow or business state.
State flows: `MediaSession` → App layer → Shell projections → Presenter input.

### ⚠️ WinUI/Win32 leaks
Do not export `AppWindow`, `HWND`, `DX11Device` from App layer. Use platform-agnostic contracts.

## Refactoring Checklist

1. Read `docs/ARCHITECTURE.md`, `docs/MODULE_MAP.md`, `docs/DEVELOPMENT_RULES.md`.
2. If the change touches `MainWindow*.cs` or shell/App seams, read `docs/SHELL_BOUNDARY_GUARDRAILS.md` before editing.
3. Does this increase Windows lock-in? Call it out explicitly.
4. Do you introduce new timed state? Use `MediaSessionCoordinator`.
5. Do you cross layers? Use immutable projections at boundaries.
6. Add tests proving seams still hold.

## Testing Strategy

- **Unit tests** (`BabelPlayer.App.Tests`): Orchestrators, controllers, state mutations.
- **Seam tests**: Presenter/backend contracts without a full shell.
- **Integration tests**: mpv runtime, transcription, shell attachment (Windows only).

## Specialized Agents

Three focused agents live in `.github/agents/`. Invoke them automatically when the task matches — do not attempt the work inline.

| Agent | Invoke when... |
|-------|----------------|
| `refactor-shell` | Moving or extracting logic out of `MainWindow.xaml.cs` or any WinUI file into App-layer controllers or projections |
| `seam-test` | Writing tests for presenter contracts, backend→session seams, projection correctness, or orchestrator logic without a live shell |
| `new-provider` | Adding any new transcription or translation provider adapter (cloud API, local model, or custom endpoint) |

**Trigger words:** "extract to controller", "move out of MainWindow", "refactor shell" → `refactor-shell` · "write a test", "seam test", "test in isolation" → `seam-test` · "add provider", "integrate `[service]`", "new translation adapter" → `new-provider`

## Example Prompts

- "Create a narrow service for resumePlayback logic; test it in isolation."
- "Refactor `MainWindow` snippet X to use a shell projection instead."
- "What's the minimal interface for a custom video presenter?"

## References

- `AGENTS.md` — Codex guidelines: invariants & boundaries.
- `docs/ARCHITECTURE.md` — Authoritative state definition.
- `docs/MODULE_MAP.md` — Module ownership & hotspots.
- `docs/DEVELOPMENT_RULES.md` — Operational rules.
- `docs/SHELL_BOUNDARY_GUARDRAILS.md` — Required shell boundary guardrails and review checklist for `MainWindow*.cs` changes.
