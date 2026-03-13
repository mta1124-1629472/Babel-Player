# Shell Interface Boundary

WinUI communicates with the App layer exclusively through shell interfaces.

## Guidelines

- WinUI must depend only on approved App interfaces.
- Implementation classes must never be referenced by WinUI.
- Shell interfaces provide the only entry points into application workflows.

Approved interfaces:

- IShellPreferencesService
- IShellLibraryService
- IQueueProjectionReader
- IQueueCommands
- IShellPlaybackCommands
- ICredentialSetupService
- IShortcutProfileService
- IShortcutCommandExecutor
- ISubtitleWorkflowShellService
