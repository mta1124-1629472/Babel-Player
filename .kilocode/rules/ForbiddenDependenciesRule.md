# Forbidden WinUI Dependencies

Certain internal types must never appear in WinUI code.

## Guidelines

The following classes must not be referenced in WinUI:

- CredentialFacade
- ShortcutService
- SettingsFacade
- LibraryBrowserService
- SubtitleWorkflowController
- ProviderAvailabilityService
- DefaultRuntimeProvisioner
- DefaultAiCredentialCoordinator

If functionality is required, expose it through a shell interface.
