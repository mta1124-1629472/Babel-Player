---
name: Performance + Memory Audit and Fix Sweep
description: "Run a deep Babel Player performance/memory hotspot audit, then dispatch subagents to implement high-impact fixes with validation."
argument-hint: "Provide workflow to profile, constraints, target latency/memory goals, and any known regression symptoms."
agent: "agent"
---
Run a full-codebase audit focused on performance, memory usage, inefficiencies, and bottlenecks, then implement fixes via subagents.

## Inputs to gather first
- Workflow(s) to optimize (startup, media load, transcribe, translate, TTS, playback, restore)
- Environment constraints (Windows/Linux/macOS, CPU/GPU/NPU, RAM)
- Guardrails (no behavior changes, no API breaks, no milestone scope expansion unless approved)
- Target outcomes (latency, throughput, memory ceiling, UI responsiveness)

## Mandatory execution flow
1. **Baseline and discovery**
   - Establish baseline timing/memory observations for the requested workflow(s).
   - Identify hotspots using code evidence (allocation-heavy paths, repeated I/O, blocking UI-thread work, avoidable subprocess churn, redundant serialization, cache misuse, duplicated transforms).
   - Classify each hotspot by severity and expected win.

2. **Delegate analysis and fixes to subagents**
   - Use specialized subagents where applicable:
     - `.NET Avalonia Performance Sweep` for UI/rendering/allocation/runtime bottlenecks
     - `Error Fixer and Debug Zapper` for compile/runtime issues encountered while optimizing
     - `build-check` and `test-runner` after edits
   - Provide each subagent with explicit constraints: preserve behavior, minimal-risk diffs, measurable impact.

3. **Implement in small validated batches**
   - Apply highest-impact, lowest-risk fixes first.
   - After each batch: build + relevant tests, and summarize deltas.
   - If an optimization is uncertain or risky, propose it separately instead of applying blindly.

4. **Verification and regression defense**
   - Re-check baseline scenarios and report before/after deltas.
   - Confirm no hidden regressions (correctness, workflow stage progression, provider routing truthfulness, session restore integrity).

## Hard requirements
- Do not claim performance wins without concrete evidence from this session.
- Keep changes aligned with repository architecture rules and milestone discipline.
- Do not introduce fake readiness, silent fallback, or speculative abstractions.
- Prefer surgical edits over broad refactors unless explicitly requested.

## Required output format
Return exactly these sections:

### A) Audit Scope and Environment
- Scenarios audited
- Machine/runtime context
- Constraints applied

### B) Hotspot Findings (Ranked)
For each finding include:
- Location (file/symbol)
- Symptom
- Likely root cause
- Severity (high/medium/low)
- Proposed fix
- Confidence level

### C) Subagent Delegation Log
- Which subagent was used
- Why it was used
- What changes it produced

### D) Implemented Fixes
- File-by-file summary of actual edits
- Behavior-risk notes

### E) Validation Results
- Build status
- Test status
- Any remaining warnings/errors

### F) Performance/Memory Delta
- Before vs after observations from this run
- If exact metrics unavailable, clearly label qualitative improvements and missing instrumentation

### G) Follow-up Backlog
- Not-yet-implemented opportunities, ordered by impact/risk
