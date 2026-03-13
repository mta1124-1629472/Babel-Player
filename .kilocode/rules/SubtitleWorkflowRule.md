# Subtitle Workflow Boundary

Subtitle state and behavior must be controlled by the App layer.

## Guidelines

- WinUI reads subtitle state through SubtitleWorkflowSnapshot.
- WinUI may render subtitle information and trigger subtitle commands.
- WinUI must not store subtitle policy state.
- Render-mode decisions and translation behavior belong in ISubtitleWorkflowShellService.
