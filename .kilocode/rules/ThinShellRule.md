# Thin Shell UI

WinUI must remain a thin presentation layer. It captures user input and renders state projections but does not implement application workflows.

## Guidelines

- WinUI handles layout, rendering, and UI events only.
- WinUI may translate keyboard and mouse input to application commands.
- WinUI must not implement workflow logic.
- If a method decides what should happen next in the application, it belongs in the App layer.
