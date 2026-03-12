# Babel Player – Codex Development Guidelines

These instructions apply to this repository.

## Primary priorities

- Keep `MediaSession` as the authoritative timed state.
- Route timed mutations through `MediaSessionCoordinator`.
- Treat mpv and detached overlay windows as transitional infrastructure adapters.
- Keep renderer-native concerns out of App-layer contracts.
- Preserve a future path toward Linux and macOS support.

## Architectural boundaries

- Shell/UI framework concerns belong in the shell layer only.
- App/domain contracts must remain platform-neutral where practical.
- Playback, rendering, subtitle composition, and provider/runtime logic should remain separate concerns.

## Before major refactors

1. Read `docs/ARCHITECTURE.md`
2. Read `docs/MODULE_MAP.md`
3. Follow `docs/DEVELOPMENT_RULES.md`

## Important invariants

- Do not introduce parallel authoritative timed state.
- Do not leak WinUI, Win32, or DirectX types into App-layer contracts.
- Do not make shell code the source of truth for business logic.
- Do not let subtitle presenter implementations own workflow state.

## Implementation style

- Prefer phased migrations with explicit exit criteria.
- Prefer narrow interfaces and adapter boundaries.
- Prefer immutable projections at shell boundaries.

## Documentation references

- Architecture overview: `docs/ARCHITECTURE.md`
- Module/file ownership map: `docs/MODULE_MAP.md`
- Development conventions and anti-patterns: `docs/DEVELOPMENT_RULES.md`