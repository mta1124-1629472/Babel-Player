# Babel Player — Claude Context

> For full project context, see [docs/AI-CONTEXT.md](docs/AI-CONTEXT.md).
> For operating rules and preferences, see [AGENTS.md](AGENTS.md).

## Running Commands as an Agent

The Linux sandbox does not have `dotnet` installed. To run builds, tests, or other
Windows-native commands, use `mcp__Desktop_Commander__start_process` (preferred) or
`mcp__Windows-MCP__PowerShell`. Always use the full path to the solution/project:

```
dotnet build "D:\Dev\Babel-Player\Babel-Player.sln"
```

Use `mcp__Desktop_Commander__read_process_output` to read results after the process starts.

## Active Skills

| Skill | Purpose |
|-------|---------|
| `proactivity-proactive-agent` | State lives in `~/proactivity/`. Read `memory.md` + `session-state.md` before non-trivial tasks. Leave a next move in state after meaningful work. |
| `self-improving-proactive-agent` | State lives in `~/self-improving/`. Log corrections and self-reflections. Promote patterns after 3x use. |

## Essential Reading

- [AGENTS.md](AGENTS.md) — operating rules, learned preferences, workspace facts
- [docs/AI-CONTEXT.md](docs/AI-CONTEXT.md) — full project context (tech stack, architecture, commands, conventions)
- [docs/PLAN.md](docs/PLAN.md) — milestone order and gates
- [docs/architecture.md](docs/architecture.md) — structural boundaries and philosophical intent
