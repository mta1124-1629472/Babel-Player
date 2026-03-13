# Composition Root Authority

Service construction must occur only in the composition root.

## Guidelines

- ShellCompositionRoot is responsible for creating application services.
- WinUI must never instantiate App services directly.
- Dependency wiring must occur only in the composition root.
- Only interfaces may be returned to WinUI.
