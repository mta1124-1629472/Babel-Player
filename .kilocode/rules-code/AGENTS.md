# Project Coding Rules (Non-Obvious Only)

- Use immutable records (e.g., ShellLoadMediaOptions, ShellQueueMediaResult) for data transfer between shell and App layers; never mutate these records after creation.
- All timed state mutations must go through MediaSessionCoordinator; direct modification of MediaSession properties causes synchronization bugs.
- Shell layer must not instantiate App services directly; dependencies are provided via constructor injection (see ShellController constructor).
- Presenters (IVideoPresenter, ISubtitlePresenter) are stateless adapters; they must not own any workflow state or make decisions about content (e.g., translation model selection).
- WinUI must not reference forbidden types: CredentialFacade, ShortcutService, SettingsFacade, LibraryBrowserService, SubtitleWorkflowController, ProviderAvailabilityService, DefaultRuntimeProvisioner, DefaultAiCredentialCoordinator; use only approved shell interfaces.
- UI threading: always use async/await; never block the UI thread with synchronous calls or long-running work.
- Cross-thread updates to UI elements must use DispatcherQueue (observed in shell observations).
- Error handling: log exceptions with context via IBabelLogger/ILogger<T>; never swallow exceptions silently.
- Private fields must use _camelCase naming (not just camelCase).
- Snapshot-driven UI is preferred over control-local state; shell should observe immutable projections from App layer.
- Queue management and playback orchestration belong exclusively to the App layer; shell only forwards commands and renders state.
- Subtitle workflow state (e.g., translation enable/disable) must be managed in App layer; shell must not store subtitle policy state.
- Preferences are controlled by App layer; shell must not mutate settings directly; use IShellPreferencesService intent methods.
- Shortcut semantics are controlled by App layer; shell only captures keyboard events and forwards them via IShortcutCommandExecutor.
- Library browsing is projection-based; shell may construct TreeView nodes but must not originate queue mutations or playback actions.