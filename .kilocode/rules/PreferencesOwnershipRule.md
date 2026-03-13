# Preferences Ownership

Preferences are controlled by the App layer.

## Guidelines

- WinUI must not mutate settings directly.
- Preferences are updated through IShellPreferencesService intent methods.
- SnapshotChanged events propagate preference updates to the UI.
