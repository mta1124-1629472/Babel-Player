# Review Guide

This repo treats review as a correctness and scope-control step, not a formality.

## Review priorities

Review in this order:

1. correctness and regression risk
2. milestone or blocker scope discipline
3. verification quality
4. maintainability of the changed path
5. polish only if it materially affects the workflow

The main user outcome is still:

`load media -> get transcript -> get translated/adapted dialogue -> generate spoken output -> preview/refine in context -> save and resume later`

If a change does not strengthen that path, it needs a strong reason to exist.

## What a good review checks

A good review checks that the PR is:

- tightly scoped
- aligned to one milestone or one blocker
- honest about what it completes and what it does not
- accompanied by build and test results
- accompanied by smoke results when relevant

Reviewers should push back on:

- unrelated refactors mixed into milestone work
- speculative cleanup that is not needed to remove the blocker
- fake scaffolding
- claims of completion without verified behavior

## Findings

When leaving findings:

- verify against the current head, not an outdated patch chunk
- prefer concrete, actionable findings over broad style commentary
- lead with the highest-severity issues first
- include file and line references where possible
- distinguish user-facing regressions from internal cleanup suggestions

If there are no real findings, say so plainly.

## Verification expectations

Before approval, expect the author to provide the relevant verification.

Baseline verification:

```powershell
dotnet build Babel-Player.sln -c Release
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj -c Release
python scripts/check-architecture.py
```

Also expect:

- smoke coverage evidence when the change touches smoke-covered seams
- targeted manual notes when the change affects UI behavior, workflow transitions, or media preview

For tests in `BabelPlayer.Tests`, hold the line on repo policy:

- deterministic
- no hidden retries or long polling
- no `Thread.Sleep`
- no `Task.Delay` above 100 ms
- no real runtime, network, container, or libmpv dependencies

## Review etiquette

Prefer one primary automated reviewer at a time.

Use extra automated reviewers only when:

- the first reviewer found a real blocker that needs a second opinion
- a domain-specific check is needed
- a human explicitly asked for a second tool or model

Do not summon multiple bots by default for the same patch just to collect overlapping comments.

## Local automation

Repo-local Gemini review is manual by design.

Use an explicit request such as:

```text
@gemini-cli /review
```

This avoids redundant automatic review noise on every PR while keeping the workflow available when it is actually needed.
