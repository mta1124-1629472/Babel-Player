# Project Architecture Rules (Non-Obvious Only)

- MediaSession is the ONLY authoritative timed state - never duplicate timeline/playback position in UI controls
- ALL timed state changes MUST flow through MediaSessionCoordinator - direct modification causes sync bugs
- Shell/WinUI is strictly view-only - zero business logic allowed in MainWindow*.cs files
- Presenters (IVideoPresenter, ISubtitlePresenter) are stateless adapters - they render only what App layer provides
- WinUI can ONLY depend on specific approved App interfaces (IShellPreferencesService, IShellLibraryService, etc.)
- Forbidden WinUI dependencies: CredentialFacade, ShortcutService, SettingsFacade, LibraryBrowserService, SubtitleWorkflowController, ProviderAvailabilityService, DefaultRuntimeProvisioner, DefaultAiCredentialCoordinator
- State flow: MediaSession → App projections (immutable snapshots) → Shell observes → Presenter renders
- Platform-native code (mpv, Win32, DirectX) must be isolated in Infrastructure/Playback layers
- Cross-layer communication requires immutable projections - Shell never writes directly to App state