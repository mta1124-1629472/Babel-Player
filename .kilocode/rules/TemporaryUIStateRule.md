# Temporary UI State

Temporary UI state is allowed only when it affects visual interaction.

## Guidelines

Allowed UI state:

- pointer capture
- drag/drop hover state
- animation state
- local slider drag values

Not allowed in UI:

- remembered workflow modes
- policy flags affecting commands
- subtitle fallback rules
- provider readiness decisions

If removing a field changes application behavior, it belongs in App.
