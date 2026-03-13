# App Workflow Ownership

All application workflows must live in the App layer.

## Guidelines

- Queue management belongs in App.
- Playback orchestration belongs in App.
- Subtitle workflows belong in App.
- Credential readiness logic belongs in App.
- Shortcut normalization and routing belong in App.
- WinUI only forwards commands and renders state.
