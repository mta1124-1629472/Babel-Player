# Layered Architecture

The repository follows a strict dependency flow to prevent UI logic from coupling to application logic.

## Guidelines

- WinUI → App → Core is the only allowed dependency direction.
- WinUI must never reference Core directly.
- Core must not reference App or WinUI.
- App orchestrates workflows and coordinates domain services.
