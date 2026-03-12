# Workspace Instructions Enhancement Summary

## What Was Updated

Your Babel-Player workspace had good foundational instruction files, but they needed consolidation, clarity, and actionable guidance. Here's what was enhanced:

### 1. **.github/copilot-instructions.md** (Day-to-day Developer Guide)
- **Added**: Real build/test commands with exact paths (`dotnet build BabelPlayer.sln`, `scripts/run.ps1`)
- **Added**: Architecture "at a glance" section with layers and invariants
- **Added**: Concrete coding patterns (state/projections, UI threading, error handling, naming)
- **Added**: Hot spots with antipatterns (MainWindow, SubtitleApplicationService, Presenters, WinUI leaks)
- **Added**: Testing strategy breakdown (unit, seam, integration)
- **Added**: Common issues troubleshooting table
- **Streamlined**: Removed generic WinUI advice; focused on Babel-Player specifics

### 2. **AGENTS.md** (Architecture Codex & Constraints)
- **Added**: Quick reference table of core invariants with exact file locations
- **Enhanced**: Each priority explains the "what," "why," "where," and "antipattern"
- **Added**: Architectural boundaries diagram showing data flow
- **Added**: Refactoring checklist with actionable boxes
- **Added**: Testing strategy section (unit, seam, integration)
- **Added**: Common pitfalls table with fixes
- **Added**: Example prompts showing how to invoke guidance
- **Cross-referenced**: Clear links to supporting docs (ARCHITECTURE.md, MODULE_MAP.md, DEVELOPMENT_RULES.md)

### 3. Supporting Docs (Verified, Not Modified)
- **docs/ARCHITECTURE.md**: Canonical state design
- **docs/MODULE_MAP.md**: Module ownership and hotspots
- **docs/DEVELOPMENT_RULES.md**: Detailed operational constraints

## Key Changes in Tone & Structure

| Aspect | Before | After |
|--------|--------|-------|
| **Scope clarity** | Mixed UI/WinUI generics with arch code | Frontend (copilot-instructions.md) + Architecture (AGENTS.md) split |
| **Actionability** | Conceptual guidelines | Concrete file paths, commands, and patterns |
| **Catchability** | Scattered across 3 files | Quick reference tables + detailed sections |
| **Antipatterns** | Mentioned briefly | Explicit tables with "why it's bad" → "how to fix" |
| **Testing** | Not well-covered | Full strategy section with test types |
| **Links** | Some broken or vague | Cross-referenced to canonical docs |

## How to Use These Now

1. **For daily development**: Open `.github/copilot-instructions.md`
   - Build/test commands
   - Architecture overview
   - Hot spots to avoid
   - Common issues

2. **For architectural decisions**: Open `AGENTS.md`
   - Core invariants (before any PR)
   - Why each invariant matters
   - Refactoring checklist
   - Common pitfalls

3. **For deep design**: Link to detailed docs
   - `docs/ARCHITECTURE.md` (state model)
   - `docs/MODULE_MAP.md` (who owns what)
   - `docs/DEVELOPMENT_RULES.md` (platform rules)

## Suggested Next Steps

These improvements are ready to use now. Consider:

1. **Create an `.agent.md`** for specialized workflows
   - Example: `/.agent.refactor-mainwindow.md` for extracting business logic from MainWindow
   - Example: `/tests/.agent.test-patterns.md` for test authoring guidance

2. **Create a `.hook.md`** or `.skill.md** to capture common requests
   - Example: Seam test generation (test a presenter contract without shell)
   - Example: Projection pattern extraction (state snapshot → UI binding)

3. **Iterate based on pain points** from your team's questions
   - What do people frequently ask about?
   - What gotchas do they hit?
   - Document those patterns

## Files Affected

- ✅ `.github/copilot-instructions.md` – Enhanced (4.7 KB → full guidance)
- ✅ `AGENTS.md` – Enhanced (original ~2 KB → 10 KB with tables, examples, links)
- ✅ Verified: `docs/ARCHITECTURE.md`, `docs/MODULE_MAP.md`, `docs/DEVELOPMENT_RULES.md`

All changes preserve the existing architecture philosophy while making it dramatically more actionable and AI-friendly.
