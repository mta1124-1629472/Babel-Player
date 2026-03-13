# Shortcut System Ownership

Shortcut semantics are controlled by the App layer.

## Guidelines

- WinUI captures keyboard events only.
- VirtualKey input may be translated to gesture tokens.
- Gesture parsing and normalization belong in the App layer.
- Command execution must occur through IShortcutCommandExecutor.
