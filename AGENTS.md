# AGENTS.md

## Mission

Build Babel Player as a disciplined sequence of vertical slices centered on the real product chain:

`source media -> timed transcript -> translated/adapted dialogue -> spoken dubbed output -> in-context preview and refinement`

The repo is not allowed to drift back into a shell-first or architecture-first project.

The current phase is defined by `PLAN.md`. If this file conflicts with a proposed change, the plan wins.

---

## Proactive Operating State

- **Proactivity:** `~/proactivity/` (via `proactivity` skill) - proactive operating state, action boundaries, active task recovery, and follow-through rules

Before any non-trivial task:
- Read `~/proactivity/memory.md`
- Read `~/proactivity/session-state.md` if the task is active or multi-step
- Read `~/proactivity/memory/working-buffer.md` if context is long, fragile, or likely to drift
- Recover from local state before asking the user to repeat recent work
- Check for obvious blockers, next steps, or useful suggestions not yet asked for
- Leave one clear next move in state before the final response when work is ongoing

Write proactive state as follows:
- Durable preference or boundary -> append to `~/proactivity/memory.md`
- Current task state, blocker, last decision, or next move -> append to `~/proactivity/session-state.md`
- Volatile breadcrumbs or recovery hints -> append to `~/proactivity/memory/working-buffer.md`
- Repeat proactive win worth reusing -> append to `~/proactivity/patterns.md`
- Proactive action taken or suggested -> append to `~/proactivity/log.md`
- Recurring follow-up worth re-checking -> append to `~/proactivity/heartbeat.md`

Proactivity boundaries for this repo:
- Refactor tasks -> SUGGEST mode (propose the change, wait for confirmation before writing)
- Spec review -> DO mode (analyze and write the review file automatically)
- Git commit/push -> ASK always
- Scope expansion of any kind -> ASK always

---

## What Matters Most

The product is succeeding when a user can:

1. Load source media
2. Generate a timed transcript
3. Produce translated/adapted dialogue
4. Generate spoken dubbed output
5. Preview and refine that output in context
6. Reopen the session and continue without losing work

Everything else is secondary until that loop is real.

---

## Non-Negotiable Rules

### 1. Work one milestone at a time
Do not start downstream scope before the current milestone is complete.

A milestone is not complete because code exists.
It is complete when:
- the build passes
- relevant tests pass
- a manual smoke note exists
- the milestone gate in `PLAN.md` is actually satisfied

### 2. Do not widen scope
Do not add optional features, nice-to-have polish, alternate providers, runtime matrices, UI prestige work, or speculative extensibility unless the current milestone explicitly requires them.

### 3. Do not fake readiness
Never leave behind code that implies a feature is working when it is not.

Use explicit placeholders, disabled states, or honest errors.
Do not:
- silently fall back
- pretend a local path is active
- claim a runtime is ready without verification
- scaffold UI that reads as implemented when it is not

### 4. Preserve the product center of mass
Do not let the repo quietly revert into a generic media player project.

Playback exists to support dubbing workflow and in-context inspection.
Transcription, translation/adaptation, and TTS are the main product chain.

### 5. Retire known technical risks early, but do not let them become the product
If a recurring infrastructure failure point blocks the plan, it is valid to tackle it early.
That does not justify expanding unrelated scope around it.

Example:
headless media transport stability may be addressed early as risk retirement.
That does not justify rebuilding the whole playback shell before the dub loop is real.

### 6. Prefer ugly truth over elegant incompleteness
A working narrow slice is better than a cleaner design that proves less of the product.
Do not trade working behavior for prettier abstractions unless the current milestone demands it.

### 7. Do not delete working history
Never remove old working code, branches, or experiments without preserving them somewhere recoverable.
Archive instead of erasing.

### 8. Keep missing work visible
If something is not implemented, make that obvious in code and UI.
Use names and comments that tell the truth.

### 9. Avoid premature architecture
Do not introduce:
- provider matrices
- execution-target routing systems
- setup hubs
- plugin architectures
- backend factories for hypothetical future paths
- generalized workflow engines

unless the current milestone requires them to deliver a real gate.

### 10. Inference hosting discipline
Do not treat WSL, containers, or NVIDIA-managed serving as mandatory foundation work before the first real local inference slice is proven.
Keep Python inference behind a clear boundary so hosting can evolve later.
Do not hard-code WSL-only assumptions or introduce deployment complexity that outruns the current milestone.

### 11. Keep one owner for session/workflow state
Do not scatter product state across views, random services, and helper classes.
A clear coordinator or equivalent owner should drive workflow progression.

### 12. Treat Python/C# boundary field names as explicit serialization contracts
Python/C# boundary field names are an explicit serialization contract — not an implementation detail.

Do not rely on implicit .NET casing assumptions (PascalCase by default) when crossing the Python/C# boundary.
When Python emits snake_case or camelCase, C# consumers must match that contract deliberately with hardcoded property names.
Any change to cross-language JSON/artifact field names must be updated on both producer (Python) and consumer (C#) sides together.

This applies to:
- JSON files written by Python scripts and consumed by C#
- Dynamic JSON parsing via `GetProperty()` with string literals
- DTOs and typed deserialization
- Dictionary keys crossing the boundary

---

## Allowed Work

Allowed work is work that directly helps the current milestone pass its gate.

Examples:
- implementing the missing core behavior for the current slice
- fixing build/test failures
- adding tests needed to prove the milestone
- adding narrowly scoped models/types needed by the slice
- improving logging/diagnostics that unblock debugging
- simplifying existing code when it reduces friction without widening scope

---

## Forbidden Work Unless Explicitly Required

Do not do these on your own initiative:

- broad refactors outside the current milestone
- replacing major subsystems because a new stack seems cleaner
- adding multiple model/provider choices early
- building large settings surfaces before the workflow exists
- polishing visual design before the core loop is usable
- introducing elaborate abstractions "for later"
- building fake facades that mimic unfinished features
- migrating stable code just to make the architecture look purer
- changing naming, structure, or patterns repo-wide without direct milestone need

---

## How To Make Changes

### When touching code
- keep changes narrow
- preserve existing working behavior
- prefer direct fixes over framework theater
- leave comments only where they clarify non-obvious behavior or constraints
- do not move code across layers unless the current milestone truly needs it

### When adding a new type or service
Ask:
- does this directly serve the current milestone?
- is it the smallest honest shape that works?
- does it model something real in the product, or just future possibility?

If it mostly serves future possibility, do not add it yet.

### When blocked
If the current plan is blocked by a real technical issue:
- fix the blocker
- document why it blocked the milestone
- do not use the blocker as an excuse to expand the project sideways

---

## Verification Expectations

Before calling work complete:
- run the build
- run relevant tests
- add or update tests when the change is testable
- perform the milestone's manual smoke path
- record a short smoke note naming the exact gate that was verified

Do not claim completion based on static inspection alone if the milestone is behavior-driven.

---

```md id="ag-smoke-template-rules"
### Smoke note conventions

All milestone smoke notes must use the repo smoke-note template and must live under `docs/smoke/`.

#### File location
Store smoke notes only in:

- `docs/smoke/`

Do not create root-level milestone evidence files unless explicitly asked.

#### File naming
Use this exact naming pattern:

- `milestone-01-foundation.md`
- `milestone-02-headless-libmpv.md`
- `milestone-03-media-ingest.md`

Rules:
- lowercase only
- hyphen-separated
- two-digit milestone number
- short stable milestone label
- no `_SMOKE_NOTE`
- no `_COMPLETE`

#### File purpose
A smoke note is the authoritative milestone evidence file.

Do not create a second completion-summary file unless it adds genuinely different information.
In normal use, the smoke note is also the completion record.

#### Status values
Allowed smoke note status values are only:
- `complete`
- `partial`
- `failed`

Do not use vague status labels like:
- `mostly done`
- `substantially complete`
- `good enough`
- `appears working`

#### Truthfulness rules
- If any gate item is unverified, status cannot be `complete`.
- If a behavior is inferred rather than demonstrated, it belongs under `What Was Not Verified` or `Deferred Items`.
- If a milestone is partial, say `partial` plainly.
- Do not mark a milestone complete while listing unresolved gate items.

#### Required sections
Every smoke note must contain:
- Metadata
- Gate Summary
- What Was Verified
- What Was Not Verified
- Evidence
- Notes
- Conclusion
- Deferred Items

Do not replace these with looser summary prose.
```

---

## Preferred Biases

When there is a tradeoff, generally prefer:

- working slice over broad foundation
- narrow real behavior over broad fake readiness
- direct implementation over speculative abstraction
- persistent artifacts over recomputation
- recoverability over elegance
- clear failure states over silent fallback

---

## If Unsure

If a proposed change feels smart but not necessary for the current milestone, do not do it.

If a change improves architecture but delays the main product loop, do not do it.

If a change makes the shell nicer but does not strengthen transcript -> translation/adaptation -> TTS -> preview, it is probably not the priority.

When in doubt, serve the milestone gate and protect the product center of mass.
