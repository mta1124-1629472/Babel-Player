# Babel Deck — Claude Context

## Commands

```bash
dotnet build                             # Build the project
dotnet test                              # Run all tests
dotnet run --project BabelDeck.csproj    # Launch the app
```

## Essential Reading

Before making non-trivial changes, read:
- `AGENTS.md` — operating rules, scope discipline, non-negotiables
- `PLAN.md` — milestone order and gates (the plan wins if anything conflicts)
- `docs/architecture.md` — structural boundaries and state ownership

## Current Milestone

Milestone 7 — Dub Session Workflow. Check `docs/smoke/` for completed gate evidence.

## Naming Conventions

| Context | Form |
|---------|------|
| Product / branding | `Babel Deck` |
| Repository | `Babel-Deck` |
| .NET namespaces / assembly IDs | `BabelDeck` or `Babel.Deck` |
| Filenames / folders | match local convention already in use |

## Key Files

| File | Purpose |
|------|---------|
| `Services/SessionWorkflowCoordinator.cs` | Single owner of workflow state — all stage progression runs through here |
| `PLAN.md` | Milestone gates — current milestone is the only allowed scope |
| `docs/smoke/` | Milestone smoke notes (required evidence for gate completion) |

## Active Skills

Both skills are active for this session and should be used throughout:

| Skill | Purpose |
|-------|---------|
| `proactivity-proactive-agent` | State lives in `~/proactivity/`. Read `memory.md` + `session-state.md` before non-trivial tasks. Leave a next move in state after meaningful work. |
| `self-improving-proactive-agent` | State lives in `~/self-improving/`. Log corrections and self-reflections. Promote patterns after 3x use. |

## Gotchas

- **State ownership:** Do not scatter session/workflow state across views or helper classes. `SessionWorkflowCoordinator` is the explicit owner.
- **Smoke notes:** Live in `docs/smoke/milestone-NN-label.md`. Status must be `complete`, `partial`, or `failed` — nothing vague.
- **Scope discipline:** AGENTS.md rules are non-negotiable. Refactors, abstractions, and scope expansion require explicit justification against the current milestone.
- **Fake readiness is forbidden:** Use explicit placeholders or disabled states — never silent fallback or pretend-complete UI.
- **Python/C# JSON contracts:** Field names crossing the Python/C# boundary are explicit serialization contracts. Do not rely on implicit .NET casing. Python emits camelCase/snake_case, C# must match with hardcoded strings or explicit `[JsonPropertyName]` attributes.
