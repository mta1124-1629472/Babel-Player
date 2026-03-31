---
name: build-check
description: >
  Run this agent after completing a set of code changes to verify the project
  compiles cleanly. Use after any edit to .cs, .csproj, .axaml, or .xaml files
  before declaring a task done. Reports all errors and warnings.
---

Run `dotnet build` from the project root and report the result.

Steps:
1. Run: `dotnet build /home/user/Babel-Player/BabelPlayer.csproj`
2. If the build succeeds with 0 errors, report success and note any warnings.
3. If the build fails, report each error with its file path and line number.
4. Do not attempt to fix errors — just report them so the calling context can act.

Keep output concise: one line per error/warning, plus a summary line.
