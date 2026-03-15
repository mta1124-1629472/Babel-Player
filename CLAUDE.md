# CLAUDE.md

This file provides guidance for AI assistants (Claude and others) working with the Babel-Player codebase.

---

## Essential Commands

```bash
# Build
dotnet build src/BabelPlayer.Avalonia/BabelPlayer.Avalonia.csproj

# Run all tests
dotnet test tests/BabelPlayer.App.Tests/BabelPlayer.App.Tests.csproj

# Run a single test
dotnet test --filter "FullyQualifiedTestName~<TestClass>.<TestMethod>"

# Full NUKE build
./build.sh         # Linux/macOS
./build.ps1        # Windows (PowerShell)

# NUKE targets (can be combined)
./build.ps1 Compile Test
./build.ps1 ValidateNativeAssets Compile Test --configuration Release

# Run the app
powershell -ExecutionPolicy Bypass -File ./scripts/run.ps1
# NOTE: run-winui.ps1 is disabled by design and hard-fails
```

---

## Repository Structure

```
Babel-Player/
├── src/
│   ├── BabelPlayer.Avalonia/        # Desktop shell (Avalonia UI, entry point)
│   ├── BabelPlayer.App/             # Application / domain logic layer
│   ├── BabelPlayer.Core/            # Platform-agnostic utilities and interfaces
│   ├── BabelPlayer.Infrastructure/  # Provider adapters and runtime installers
│   └── BabelPlayer.Playback.Mpv/    # libmpv P/Invoke playback backend
├── tests/
│   └── BabelPlayer.App.Tests/       # xunit tests (unit + seam)
├── build/                           # NUKE build project
├── docs/                            # Architecture and developer docs
├── scripts/                         # Build and runtime launcher scripts
├── installer/                       # Inno Setup Windows installer config
├── linux/                           # Linux packaging
├── native/                          # libmpv DLLs (LFS-tracked)
├── eval/                            # LLM evaluation infrastructure
├── AGENTS.md                        # Quick AI agent reference (also read this)
├── ARCHITECTURE_AUDIT.md            # Deep architecture documentation
└── ARCHITECTURE_MAP.json            # Machine-readable architecture map
```

---

## Architecture Overview

The app follows a strict **layered architecture**. Each layer has a defined role:

### Layers (top → bottom)

| Layer | Project | Role |
|-------|---------|------|
| Shell | `BabelPlayer.Avalonia` | View-only: captures events, renders snapshots |
| App | `BabelPlayer.App` | Domain logic, orchestration, state management |
| Infrastructure | `BabelPlayer.Infrastructure` | Provider adapters, runtime installers |
| Playback | `BabelPlayer.Playback.Mpv` | libmpv P/Invoke backend |
| Core | `BabelPlayer.Core` | Interfaces, subtitle parsing, utilities |

### State Flow (critical to understand)

```
User Event → Shell → App interface call → App workflow
                                              ↓
                             MediaSessionCoordinator (single write point)
                                              ↓
                             MediaSessionSnapshot (immutable read model)
                                              ↓
                             Shell observes → Presenter renders
```

- **`MediaSessionSnapshot`** is the single authoritative timed state (in `BabelPlayer.App/MediaSessionModels.cs`)
- **`MediaSessionCoordinator`** is the **only** class that may mutate session state
- Shell never reads mutable state directly; it only observes projected snapshots

### Key App Layer Services

| Class | Responsibility |
|-------|----------------|
| `ShellController` | Playlist/file loading, media lifecycle |
| `SubtitleApplicationService` | Subtitle generation, translation, import |
| `PlaybackBackendCoordinator` | Backend state → session state bridge |
| `PlaybackSessionController` | Transport command execution |
| `PlaylistController` | Queue management |
| `PlaybackQueueController` | Auto-advance, reordering |
| `ShortcutProfileService` | Configurable keyboard shortcuts |
| `ResumeTrackingCoordinator` | Resume state persistence |

### Shell-Facing Interfaces (Avalonia may only depend on these)

- `IShellLibraryService`
- `IQueueProjectionReader` / `IQueueCommands`
- `IShellPlaybackCommands`
- `IShellPreferenceCommands`
- `ICredentialSetupService`
- `IShortcutProfileService` / `IShortcutCommandExecutor`
- `ISubtitleWorkflowShellService`

---

## Critical Architecture Rules

1. **Never duplicate timed state in UI controls.** `MediaSession` is the ONLY authoritative timed state.
2. **All timed state changes MUST flow through `MediaSessionCoordinator`.** Direct modification causes sync bugs.
3. **Shell is strictly view-only.** Zero business logic in shell window files.
4. **Presenters (`IVideoPresenter`, `ISubtitlePresenter`) are stateless adapters.** They only render what the App layer provides.
5. **Never export `AppWindow`/`HWND`/`DX11Device` from App-layer contracts.** This breaks cross-platform portability.
6. **Platform-native code (mpv, Win32, DirectX) must stay in Infrastructure/Playback layers.**
7. **Cross-layer communication requires immutable projections.** Shell never writes directly to App state.
8. **Large `MainWindow.xaml.cs` files indicate missing controllers.** Extract logic to App layer instead.

Read `docs/SHELL_BOUNDARY_GUARDRAILS.md` before touching any `MainWindow*.cs` or shell/App boundaries.

---

## Tech Stack

- **Language:** C# with nullable reference types enabled
- **.NET:** 9.0 LTS
- **UI Framework:** Avalonia 11.3.12 (cross-platform)
- **Build System:** NUKE 10.1.0 + dotnet CLI
- **Tests:** xunit 2.9.3
- **Playback:** libmpv via P/Invoke (`BabelPlayer.Playback.Mpv`)
- **Local Transcription:** Whisper.NET + ONNX Runtime + DirectML (GPU acceleration on Windows)
- **Cloud Transcription:** OpenAI Whisper-1
- **Local Translation:** HY-MT models via llama.cpp/llama-server
- **Cloud Translation:** OpenAI GPT-4o, Google Translate, DeepL, Microsoft Translator
- **Audio Extraction:** FFmpeg (downloaded at runtime)
- **Credential Storage:** Windows DPAPI / Linux AES-256-GCM
- **Archive Extraction:** SharpCompress

---

## Code Style Conventions

- **Private fields:** `_camelCase` (underscore prefix required)
- **Classes/properties/methods:** `PascalCase`
- **Local variables/parameters:** `camelCase`
- **Suffixes:** `Provider`, `Service`, `Controller`, `Coordinator`, `Factory`, `Manager`
- **No field/property qualification** (no `this.`, no namespace qualifiers unless needed)
- **Expression-bodied members** preferred for simple properties and methods
- **Async/await required** throughout; never block the UI thread
- **Cross-thread updates:** Use `DispatcherQueue` for shell observations
- **Error handling:** Log with `IBabelLogger`/`ILogger<T>`, never swallow exceptions silently
- **Snapshot-driven UI** preferred over control-local state

---

## Testing

### Setup
- Framework: xunit 2.9.3
- Location: `tests/BabelPlayer.App.Tests/`
- Test file naming: `*Tests.cs`
- Target framework: `net9.0-windows10.0.22621.0` (Windows environment required for integration tests)

### Test Types
- **Unit tests** – Isolated component behavior (queue, preferences, projections)
- **Seam tests** – Validate presenter/backend contracts WITHOUT full shell
- **Architecture compliance tests** – Verify layering rules

### Key Test Files
| File | Coverage |
|------|----------|
| `AppLayerTests.cs` | Queue, preferences, projections |
| `CaptionGenerationOrchestratorTests.cs` | Caption workflow |
| `MediaSessionSeamTests.cs` | Playback/session coordinator |
| `PlaybackSessionControllerTests.cs` | Transport control |
| `UxProjection/UxSessionProjectorTests.cs` | UX projection logic |

### Testing Rules
- Seam tests validate `IVideoPresenter`/`ISubtitlePresenter` contracts directly
- `MediaSessionCoordinator` tests must cover all state transitions
- Test `MediaSession` projections in isolation
- Integration tests require Windows + mpv runtime + transcription services
- Mock-free approach: use testable service composition

---

## Build Targets (NUKE)

| Target | Description |
|--------|-------------|
| `Compile` | Build the solution |
| `Test` | Run xunit tests → `artifacts/test-results/` |
| `Clean` | Delete bin/obj/artifacts |
| `Restore` | NuGet restore |
| `ValidateNativeAssets` | Verify libmpv DLLs exist |
| `PublishWinX64` / `PublishWinArm64` | Self-contained Windows publish |
| `PublishLinuxX64` / `PublishLinuxArm64` | Self-contained Linux publish |
| `PackagePortableWinX64` | ZIP distribution |
| `PackagePortableLinuxX64` | tar.gz distribution |
| `BuildInstallerWinX64` | Inno Setup EXE installer |

---

## Platform Support

| Platform | Status |
|----------|--------|
| Windows x64 | Primary supported target |
| Windows ARM64 | Supported |
| Linux x64 | Supported (cross-platform compat work ongoing) |
| Linux ARM64 | Supported |

**Cross-platform notes:**
- Active shell runtime is **Avalonia** — WinUI runtime path is retired
- Credential storage is platform-abstracted: DPAPI on Windows, AES-256-GCM on Linux
- ASR and settings backends are injected via interfaces for platform isolation
- libmpv DLLs are fetched at first run via `MpvRuntimeInstaller`

---

## Commit Conventions

Follow the existing convention from git history:

```
feat: short description
fix: short description
refactor: short description
chore: short description
```

- Keep subject line concise (imperative mood)
- Focus on the "why" in the body when needed

---

## Common Gotchas

- **`run-winui.ps1` hard-fails by design** — WinUI runtime is retired; use Avalonia
- **libmpv must be present** at startup — `ValidateNativeAssets` build target verifies this
- **`CredentialFacade` is removed** — use `ICredentialStore` / `SecureCredentialStore`
- **Never reference concrete workflow services from shell** — see forbidden list in `AGENTS.md`
- **Presenter owning workflow state** breaks statelessness and testability
- **Multiple parallel authoritative timed state models** cause sync bugs — always route through `MediaSessionCoordinator`

---

## Key Documentation Files

| File | Purpose |
|------|---------|
| `AGENTS.md` | Quick-reference for AI agents (read alongside this file) |
| `ARCHITECTURE_AUDIT.md` | Deep dive into current architecture |
| `ARCHITECTURE_MAP.json` | Machine-readable architecture map |
| `docs/SHELL_BOUNDARY_GUARDRAILS.md` | Shell/App boundary rules (required reading before touching shell) |
| `docs/ARCHITECTURE.md` | Additional architecture docs |
| `docs/INSTALL.md` | Installation guide |
