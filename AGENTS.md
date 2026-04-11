# AGENTS.md

> **Read this file at the start of every session.** This file contains canonical
> guidelines for agentic coding agents operating in this repository.

## Mission

Build the best tool for taking source media through transcript → translated
dialogue → spoken dubbed output → in-context preview and refinement.

---

## Build, Test, and Lint Commands

```bash
# Build solution (required before tests)
dotnet build babel-player.sln

# Run all tests
dotnet test babel-player.sln

# Run a single test class
dotnet test babel-player.sln --filter "FullyQualifiedName~SessionWorkflowCoordinatorUnitTests"

# Run a single test method
dotnet test babel-player.sln --filter "FullyQualifiedName~SessionWorkflowCoordinatorUnitTests.ShouldAdvanceToTranslationStage"

# Run tests with verbose output
dotnet test babel-player.sln -v n

# Run architecture linter (required after structural changes)
python3 scripts/check-architecture.py

# Verify Python inference code
python -m py_compile inference/main.py

# Dev build (disables optimisations, full debug info)
dotnet run -c Dev
```

### Test Categories

Tests are filtered by category. Core tests run in CI; full suite requires local environment:
- `Integration` — end-to-end workflows
- `RequiresPython` — Python subprocess calls
- `RequiresFfmpeg` — audio/video processing
- `RequiresExternalTranslation` — cloud API calls

---

## Git Hooks

The repository includes optional git hooks in `.git-hooks/` that automate verification:

| Hook | When | What it runs |
|---|---|---|
| `pre-commit` | Before every commit | Architecture linter, Python syntax, C# build (if .cs files changed) |
| `commit-msg` | Before every commit | Message format rules (length, no WIP, no "Merge ", PLACEHOLDER warnings) |
| `pre-push` | Before every push | Full verification: `dotnet build`, `dotnet test` (core only), architecture linter, Python syntax |
| `post-merge` | After pull/merge | Reminds you to `dotnet restore` if dependencies changed |

**Activate:**
```bash
git config core.hooksPath .git-hooks
```

**Bypass a hook** (when you know what you're doing):
```bash
git commit --no-verify    # skip pre-commit + commit-msg
git push --no-verify      # skip pre-push
```

CI runs the same checks independently — hooks are a developer convenience, not a gate.
See `.git-hooks/README.md` for full documentation.

---

## Code Style Guidelines

### General
- **Target:** .NET 10 / C# 12+ with Avalonia 12
- **Nullable:** Enabled. **ImplicitUsings:** Not enabled project-wide; keep or add required manual `using` directives for framework and other types.

### Formatting
- Standard .NET conventions (K&R braces, 4-space indent)
- No trailing whitespace, no extra blank lines at end of file

### Naming
- **Classes/Methods/Properties:** PascalCase
- **Private fields:** `_camelCase`
- **Interfaces:** `I` prefix
- **Constants:** PascalCase (`ProviderNames.FasterWhisper`)
- **Avoid:** Abbreviations beyond `Http` (use `HttpClient` for client types)

### Types
- Prefer `record` for immutable DTOs
- Use `required` keyword for required properties
- Avoid `object`; use concrete types or generics

### Imports
- No unused imports (linter catches this)
- Group: System, third-party, project

### Error Handling
- Throw specific exceptions (`ArgumentNullException`, `InvalidOperationException`)
- `PipelineProviderException` for provider failures with context
- `NotImplementedException` must include `PLACEHOLDER` message

---

## Architecture Linter

`python3 scripts/check-architecture.py` enforces:
1. `BabelPlayer.csproj` exists
2. Test project references main project
3. Test project has a test framework
4. Main project is Avalonia app
5. `OutputType=WinExe`
6. `NotImplementedException` has `PLACEHOLDER` message
7. Silent event stubs have `PLACEHOLDER` comments
8. No magic provider strings outside `ProviderNames.cs`
9. ViewModels do not call pipeline methods directly
10. `SessionWorkflowCoordinator.cs` under 1300 lines

---

## Core Engineering Rules

### Provider identifiers are constants
All provider strings in `Models/ProviderNames.cs` (`ProviderNames.*` and
`CredentialKeys.*`). No inline literals in production code.

### Stage progression runs through coordinator
ViewModels must NOT call `TranscribeMediaAsync`, `TranslateTranscriptAsync`,
or `GenerateTtsAsync` directly. All pipeline advancement must go through
`SessionWorkflowCoordinator` entry points such as
`AdvancePipelineAsync`, `ContinuePipelineAsync`, or `RunTtsOnlyAsync`.

### Python/C# field names are explicit contracts
Python emits snake_case or camelCase. C# must match with hardcoded strings or
`[JsonPropertyName]` attributes. Never rely on implicit .NET casing.

### Service interfaces are uniform
Every AI inference service implements a provider interface.
Method signatures uniform across providers — no provider-specific parameters.
Configuration injected at construction time.

### Transport lifecycle managed by MediaTransportManager
`LibMpvHeadlessTransport` and `LibMpvEmbeddedTransport` created/owned/disposed
by `MediaTransportManager`. Use `GetOrCreate*` accessors.

### Git history is the archive
Delete dead code. Use `git branch` for abandoned experiments, not comments.

---

## Verification Expectations

Before calling work complete:
- Run `dotnet build babel-player.sln` for full consistency
- Run relevant tests with `dotnet test babel-player.sln`
- Add/update tests when change is testable
- Perform milestone's manual smoke path
- Record smoke note in `docs/smoke/` (filename: `milestone-XX-label.md`)

### Troubleshooting
Locked file error: `taskkill /F /IM clrdbg.exe /IM dotnet.exe`

When reporting build/test instability, run the standard diagnostic sequence:
1. `dotnet build babel-player.sln`
2. `dotnet test babel-player.sln`
3. `python3 scripts/check-architecture.py`
4. `python -m py_compile inference/main.py`

---

## Proactivity

Before any non-trivial task:
1. Read proactivity memory files if they exist
2. Leave one clear next move in state when work is ongoing

Boundaries:
- Refactor tasks → SUGGEST mode (unless explicitly asked)
- Git commit/push → ASK always
- Scope expansion into future milestone → flag and discuss

---

## Smoke Note Conventions

- **Location:** `docs/smoke/`
- **Naming:** `milestone-01-foundation.md`, lowercase, hyphen-separated
- **Status:** `complete`, `partial`, or `failed` (no "mostly done", "substantially complete")
- **Required sections:** Metadata, Gate Summary, What Was Verified, What Was Not Verified, Evidence, Notes, Conclusion, Deferred Items