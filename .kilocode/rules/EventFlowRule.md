# Application Event Flow

User interactions follow a strict flow.

## Guidelines

UI Event → Shell Interface → App Workflow → State Update → Snapshot Event → UI Render

WinUI must not implement application state machines.
