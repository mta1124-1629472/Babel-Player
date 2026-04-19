# Testing Requirements

## Purpose

`BabelPlayer.Tests` is the maintained day-to-day suite. It exists to catch high-signal regressions quickly enough that contributors and agents can run it during normal iteration.

The supported routine command is:

```powershell
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
```

Runtime budget:

- local target: under 10 minutes
- CI target: under 5 minutes

If the compiled suite cannot meet that bar, the test surface is too large or too flaky and must be reduced.

## What Belongs in `BabelPlayer.Tests`

Allowed:

- pure unit tests
- small seam tests using fakes and stubs
- export, planner, serialization, and normalization tests
- small smoke tests tagged with `[Trait("Category", "Smoke")]`

Required qualities:

- deterministic
- no hangs
- no hidden retries or long polling
- meaningful failure messages
- minimal fixture and setup cost

## What Does Not Belong in the Maintained Suite

Do not add these to compiled `BabelPlayer.Tests`:

- real Python, runtime, container, or libmpv tests
- real network tests
- manual, benchmark, or performance tests
- broad workflow orchestration suites
- tests that require `RequiresPython`, `RequiresFfmpeg`, or `RequiresExternalTranslation`
- tests that need `Thread.Sleep`
- tests that need `Task.Delay` above 100 ms

If a test falls into one of those categories, either:

1. rewrite it into a small deterministic seam test, or
2. move it under `BabelPlayer.Tests/Quarantined/`

Do not keep a flaky or slow test in the maintained suite just to preserve nominal coverage.

## Smoke Tests

`Smoke` is the only PR-gated test category.

Use `[Trait("Category", "Smoke")]` only for a very small set of tests that verify the seams most likely to regress during normal work. Smoke tests must remain fast, deterministic, and free of runtime-heavy dependencies.

Supported smoke command:

```powershell
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "Category=Smoke"
```

## Quarantine Policy

`BabelPlayer.Tests/Quarantined/` stores legacy tests that are intentionally excluded from compile.

Some tests remain in the compiled project but are tagged `[Trait("Category", "Quarantined")]` so routine filters (for example Codex “Test (Excluding Quarantined)”) skip them. Use the **directory** for legacy suites you do not want built at all; use the **trait** when a test should stay compiled but excluded from default agent/CI subsets.

Rules:

- keep them on disk only if they provide future rewrite value
- do not move them back into the compiled suite without rewriting them to satisfy this document
- prefer deleting dead or misleading tests over carrying them indefinitely

## Required Verification

Before opening a PR:

```powershell
dotnet build Babel-Player.sln -c Release
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
python scripts/check-architecture.py
```

Also run the smoke subset when the change touches smoke-covered seams:

```powershell
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release --filter "Category=Smoke"
```

## Agent and Contributor Rules

Before writing or modifying tests:

1. read this document
2. decide whether the behavior belongs in the maintained suite or quarantine
3. prefer a smaller seam test over an end-to-end orchestration test
4. prefer deleting or quarantining a flaky test over “fixing” it with longer waits
