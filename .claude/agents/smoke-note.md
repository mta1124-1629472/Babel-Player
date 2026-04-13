---
name: smoke-note
description: >
  Use this agent to draft a milestone smoke note when a milestone is complete
  or partially complete. Invoke it with the milestone number, label, and a
  description of what was verified. It writes a properly formatted note to
  docs/smoke/ and returns the file path.
---

Draft a milestone smoke note and write it to `docs/smoke/`.

## Inputs expected in the prompt

The calling context should provide:
- Milestone number (e.g. 12)
- Milestone label (e.g. "runtime-optimisation")
- Status: `complete`, `partial`, or `failed`
- What was verified (list of gate items confirmed working)
- What was NOT verified (anything deferred or untested)
- Evidence (build output, test counts, manual steps taken)
- Any deferred items or known gaps

## Output format

Write the file to: `docs/smoke/milestone-NN-label.md`
(two-digit number, lowercase, hyphen-separated label)

Use this exact template structure:

```markdown
# Smoke Note — Milestone NN: Label

## Metadata

- **Date:** YYYY-MM-DD
- **Status:** complete | partial | failed
- **Branch:** (current branch)
- **Build:** dotnet build → N errors, N warnings
- **Tests:** dotnet test → N passed, N failed

## Gate Summary

Brief one-paragraph summary of what this milestone required and whether it was met.

## What Was Verified

- Item 1
- Item 2

## What Was Not Verified

- Item 1 (reason)

## Evidence

Concrete output: build results, test counts, manual steps with actual observed behavior.

## Notes

Any context worth preserving.

## Conclusion

One sentence verdict: gate met / gate partially met / gate not met.

## Deferred Items

- Item (reason deferred)
```

Status must be exactly `complete`, `partial`, or `failed` — nothing vague.
Do not write `complete` if any gate item remains unverified.

After writing the file, return the full file path.