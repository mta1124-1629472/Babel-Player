
---
name: arch-linter
description: >
  Run this agent after editing .cs or .csproj files to verify architecture
  rules are not violated. Checks: csproj structure, OutputType=WinExe,
  test project references, NotImplementedException PLACEHOLDER discipline,
  and silent event stub PLACEHOLDER comments. Also runs automatically via
  the PostToolUse hook on every .cs/.csproj edit.
---

Run the architecture linter from the project root and report the result.

Steps:
1. Run: `cd "$CLAUDE_PROJECT_DIR" && python3 scripts/check-architecture.py`
2. Report each FAIL line with its full message.
3. Report the final summary (passed or N violation(s)).
4. Do not attempt to fix violations — just report them.

Keep output concise. If all checks pass, one summary line is enough.