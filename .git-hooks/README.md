# Git Hooks for Babel-Player

## Overview

This directory contains git hooks that enforce project conventions and run verification checks.

**Activate with:**
```bash
git config core.hooksPath .git-hooks
```

**Deactivate (revert to default):**
```bash
git config --unset core.hooksPath
```

## Hooks

### `pre-commit` (fast, runs on every commit)

| Check | Purpose |
|---|---|
| Architecture linter | Structural rules, PLACEHOLDER discipline, provider constants, ViewModel isolation |
| Python syntax | `inference/main.py` compiles cleanly |
| C# build (conditional) | Only if `.cs`/`.csproj`/`.sln` files are staged |

Bypass: `git commit --no-verify`

### `commit-msg`

| Check | Purpose |
|---|---|
| Non-empty message | Reject empty commit messages |
| Subject ≤ 72 chars | Keep commit subjects readable |
| No "Merge " prefix | Encourage rebasing over merge commits |
| No "WIP" | Use draft PRs instead |
| PLACEHOLDER warning | Flag commits that mention placeholders |

Bypass: `git commit --no-verify`

### `pre-push` (full verification)

| Check | Purpose |
|---|---|
| `dotnet build` (Release) | Ensures solution compiles cleanly |
| `dotnet test` (core only) | Runs tests excluding Integration/RequiresPython/RequiresFfmpeg/RequiresExternalTranslation |
| Architecture linter | Full structural validation |
| Python syntax | `inference/main.py` compiles |

Bypass: `git push --no-verify`

### `post-merge`

| Check | Purpose |
|---|---|
| Dependency change detection | Reminds you to run `dotnet restore` after pulling dependency changes |

Cannot be bypassed (informational only).

## Notes

- Hooks use `bash` — works in Git Bash, WSL, GitHub Actions, and most Unix-like environments
- On Windows CMD/PowerShell, ensure Git Bash is available (`bash --version`)
- Hooks are **not** committed to the repository by default (`.git-hooks/` is typically gitignored)
- CI runs the same checks independently — hooks are a developer convenience, not a gate
