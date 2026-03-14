# AGENTS.md

This file provides guidance to agents when working with code in this repository.

## Essential Commands
- Build (default): `dotnet build src/BabelPlayer.Avalonia/BabelPlayer.Avalonia.csproj`
- Run: `powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1`
- Test (unit): `dotnet test tests/BabelPlayer.App.Tests/BabelPlayer.App.Tests.csproj`
- Test (single test): `dotnet test --filter "FullyQualifiedTestName~<TestClass>.<TestMethod>"`
- Run Avalonia directly: `.\scripts\run-avalonia.ps1`
- Legacy compatibility wrapper: `.\scripts\run-winui.ps1`

## Critical Architecture Patterns (Non-Obvious)
- MediaSession is the ONLY authoritative timed state - never duplicate timeline/playback position in UI controls
- ALL timed state changes MUST flow through MediaSessionCoordinator - direct modification causes sync bugs
- Shell/WinUI is strictly view-only - zero business logic allowed in MainWindow*.cs files
- Presenters (IVideoPresenter, ISubtitlePresenter) are stateless adapters - they render only what App layer provides
- WinUI can ONLY depend on specific approved App interfaces (IShellPreferencesService, IShellLibraryService, etc.)
- Forbidden WinUI dependencies: CredentialFacade, ShortcutService, SettingsFacade, LibraryBrowserService, SubtitleWorkflowController, ProviderAvailabilityService, DefaultRuntimeProvisioner, DefaultAiCredentialCoordinator
- State flow: MediaSession → App projections (immutable snapshots) → Shell observes → Presenter renders
- Platform-native code (mpv, Win32, DirectX) must be isolated in Infrastructure/Playback layers
- Cross-layer communication requires immutable projections - Shell never writes directly to App state

## Code Style (Project-Specific)
- Private fields: _camelCase (not just camelCase)
- UI threading: ALWAYS use async/await; NEVER block UI thread
- Cross-thread updates: Use DispatcherQueue for shell observations
- Error handling: Log exceptions with context via IBabelLogger/ILogger<T> - never swallow silently
- Snapshot-driven UI preferred over control-local state
- Naming: Classes/properties/methods=PascalCase, locals/fields=camelCase, private fields=_camelCase

## Testing Specifics
- Seam tests validate presenter/backend contracts WITHOUT full shell (e.g., test IVideoPresenter directly)
- MediaSessionCoordinator tests must cover state transitions
- Test MediaSession projections in isolation
- Integration tests require Windows environment (mpv runtime, transcription services)
- Test files must be in tests/BabelPlayer.App.Tests/ following *Tests.cs naming

## Key Gotchas
- Exporting AppWindow/HWND/DX11Device from App-layer contracts breaks cross-platform portability
- Large MainWindow.xaml.cs files indicate missing controllers - extract logic to App layer
- Presenter owning workflow state (e.g., translation choices) breaks statelessness
- Multiple parallel authoritative timed state models cause sync bugs - route all writes through MediaSessionCoordinator
- Tight coupling in presenter implementations (e.g., MainWindow calling mpv directly) breaks testability

## Required References
- Shell/App boundary changes in MainWindow files must follow docs/SHELL_BOUNDARY_GUARDRAILS.md