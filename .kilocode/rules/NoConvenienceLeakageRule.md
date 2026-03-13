# No Convenience Leakage

Logic must not be placed in a layer simply because it is easy to access there.

## Guidelines

- Workflow state must not be stored in UI components.
- Command routing must not occur inside event handlers.
- Domain data normalization must occur in the App layer.
- Responsibility determines ownership, not convenience.
