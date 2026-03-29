---
auto_execution_mode: 2
description: Run a milestone smoke test and record a compliant smoke note
---
You are performing a milestone smoke test and recording a compliant smoke note per AGENTS.md.

## Steps
1. **Build the project**
   - Run the appropriate build command:
     - `dotnet build` (C#/.NET)
     - `npm run build` or `pnpm build` (Node/TS)
     - `cargo build` (Rust)
     - `make` or `cmake --build .` (C/C++)
   - Verify the build succeeds with no critical errors.

2. **Run relevant tests**
   - `dotnet test` or `npm test` or `cargo test` as appropriate
   - Ensure all required tests pass for the current milestone.

3. **Perform the manual smoke path**
   - Follow the milestone’s manual verification steps in PLAN.md
   - Record actual behavior vs expected

4. **Create/update the smoke note**
   - Path: `docs/smoke/milestone-XX-<label>.md` (use the exact naming pattern from AGENTS.md)
   - Use only these status values: `complete`, `partial`, `failed`
   - Include all required sections:
     - Metadata
     - Gate Summary
     - What Was Verified
     - What Was Not Verified
     - Evidence
     - Notes
     - Conclusion
     - Deferred Items

5. **Update session state**
   - If milestone is complete: clear session-state.md and mark the gate closed
   - If partial/failed: update session-state.md with blockers and next moves

## Commands (copy/paste as needed)
```bash
# Build
dotnet build
# or
npm run build

# Tests
dotnet test
# or
npm test

# Smoke note location
ls docs/smoke/
```

## Important rules
- Never mark a milestone `complete` if any gate item is unverified
- Do not create smoke notes outside `docs/smoke/`
- Use the exact naming pattern: `milestone-XX-<label>.md`
- Be honest about what was not verified
