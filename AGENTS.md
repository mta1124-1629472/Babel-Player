# AGENTS.md

## Mission

Build the best tool for taking source media through transcript,
translated dialogue, and spoken dubbed output — with in-context preview and
refinement as the quality layer.

`source media -> timed transcript -> translated/adapted dialogue -> spoken dubbed output -> in-context preview and refinement`

The repo should not drift back into a shell-first or architecture-first project,
but it is also no longer a fragile prototype. The core loop is real and working.
Engineering investment is now appropriate.

---

## Project State

The core workflow is complete and proven across milestones 1–11:
- Foundation, media transport, ingest pipeline, transcription, translation,
  TTS generation, dub session workflow, embedded playback, subtitle inspection,
  settings/bootstrap, and local/offline providers are all in place.
- The product loop (load → transcribe → translate → dub → preview → persist) works.
- The Python/C# service boundary is established.
- Provider selection, hardware detection, and bootstrap diagnostics exist.

The project is now in a phase where:
- Structural improvements are warranted, not deferred.
- Backend preparation for upcoming milestones (12: runtime optimisation and
  hardware routing, 13: release hardening) is encouraged when grounded in
  near-term reality.
- Bigger scope thinking is appropriate — with product focus maintained.

---

## Proactive Operating State

- **Proactivity:** `~/proactivity/` (via `proactivity` skill)

Before any non-trivial task:
- Read `~/proactivity/memory.md` and `~/proactivity/session-state.md`
- Recover from local state before asking the user to repeat recent work
- Leave one clear next move in state before the final response when work is ongoing

Proactivity boundaries:
- Refactor tasks → SUGGEST mode unless the user has explicitly asked for them
- Git commit/push → ASK always
- Scope expansion into a future milestone → flag and discuss, do not silently implement

---

## What Matters Most

The product is succeeding when a user can:

1. Load source media
2. Generate a timed transcript
3. Produce translated/adapted dialogue
4. Generate spoken dubbed output
5. Preview and refine that output in context
6. Reopen the session and continue without losing work

Everything else is in service of that loop, not a parallel identity.

---

## Engineering Principles

### 1. Milestone gates still govern feature scope

Do not build features from a future milestone without a deliberate decision to
do so. Backend preparation that clearly serves the next milestone is allowed;
building speculative features two milestones out is not.

A milestone is complete when:
- the build passes
- relevant tests pass
- a manual smoke note exists
- the gate in `PLAN.md` is actually satisfied

### 2. Scope means features, not structure

Adding a user-visible feature outside the current milestone requires discussion.
Choosing how to structure existing code does not. Interfaces, factories, service
classes, and similar patterns applied to real current implementations are
engineering decisions, not scope expansion.

Do not conflate "widening scope" with "improving structure."

### 3. Abstract over real cases, not imagined ones

The right test for whether an abstraction earns its place:

> If you removed all but one concrete implementation, would the abstraction
> still justify its existence?

**Appropriate:** An interface over 2 existing provider implementations.
A factory that selects between them. A base class that eliminates duplicated
subprocess boilerplate.

**Premature:** A plugin loader for providers not yet written. A capability
registry with dynamic discovery. A routing matrix across deployment targets
not yet proven.

The rule is not "avoid abstraction." The rule is "don't build scaffolding for
things that don't exist yet."

### 4. Do not fake readiness

Never leave behind code that implies a feature is working when it is not.

Use explicit placeholders, disabled states, or honest errors. Do not:
- silently fall back to another provider
- claim a runtime is ready without verification
- scaffold UI that reads as implemented when it is not
- mark a milestone complete while gate items are unverified

### 5. Preserve the product center of mass

Playback, settings, and infrastructure exist to support the dubbing workflow.
The moment the repo starts feeling more like a media player than a dubbing tool,
something has drifted.

### 6. Keep one owner for session/workflow state

`SessionWorkflowCoordinator` owns workflow state and stage progression. Do not
scatter product state across views, helper classes, or services. Services
produce results; the coordinator decides what those results mean for the session.

### 7. Python/C# boundary field names are explicit contracts

Field names crossing the Python/C# boundary are serialization contracts, not
implementation details. Do not rely on implicit .NET casing.

When Python emits `snake_case` or `camelCase`, C# must match deliberately with
hardcoded strings or `[JsonPropertyName]` attributes. Any change to a
cross-language field name must be updated on both sides together.

### 8. Inference hosting discipline

The boundary between the desktop app and Python-backed inference services must
stay clean. WSL, containers, and NVIDIA-managed serving paths are valid future
deployment strategies, not current prerequisites. Do not hard-code assumptions
that tie the app to one hosting model.

### 9. Fix structure while the code is open

The right moment to clean up a structural problem is while you are already
working in that area. Do not block feature delivery for cleanup, but do not
defer obvious structural problems with "I'll fix it later" when fixing them
now costs almost nothing.

The rule is about timing, not avoidance.

### 10. Service interfaces are uniform; configuration lives in constructors

Every AI inference service implements a provider interface
(`ITranscriptionService`, `ITranslationService`, `ITtsService`). Method
signatures are uniform across providers — no provider-specific parameters on
interface methods. Provider configuration (model name, voice, API key, model
directory) is injected at construction time.

All five Python-backed services extend `PythonSubprocessServiceBase`. Do not
duplicate Python discovery, temp-file management, or process-spawn boilerplate
— use `RunPythonScriptAsync` and `ThrowIfFailed`.

### 11. Provider identifiers are constants, not string literals

All provider and credential key strings live in `Models/ProviderNames.cs`
(`ProviderNames.*` and `CredentialKeys.*`). Use these constants everywhere in
production code. String literals matching a known provider ID outside of
`ProviderNames.cs` are a linter violation (`check-architecture.py` check 8).

### 12. Stage progression runs through AdvancePipelineAsync

ViewModels must not call `TranscribeMediaAsync`, `TranslateTranscriptAsync`,
or `GenerateTtsAsync` directly. All pipeline advancement goes through
`SessionWorkflowCoordinator.AdvancePipelineAsync`. ViewModels may read
`.CurrentSession.Stage` for UI state display; they must not decide which stage
to execute. The linter enforces this (`check-architecture.py` check 9).

### 13. Transport lifecycle is managed by MediaTransportManager

`LibMpvHeadlessTransport` (TTS segment audio) and `LibMpvEmbeddedTransport`
(video to native HWND) are created, owned, and disposed by
`MediaTransportManager`. Do not create or dispose transport instances directly
in the coordinator or ViewModel. Use `IMediaTransportManager.GetOrCreate*`
accessors.

### 14. Git history is the archive

Do not accumulate dead code, commented-out blocks, or unused files in active
source just because they once worked. Delete them. Git history preserves them.

For large abandoned experiments (whole feature branches, multi-file approaches),
preserve as a named branch before removing from main.

---

## Architecture Linter

`python3 scripts/check-architecture.py` enforces:

1. `BabelPlayer.csproj` exists
2. Test project references main project
3. Test project has a test framework
4. Main project is an Avalonia app
5. `OutputType=WinExe`
6. `NotImplementedException` carries a `PLACEHOLDER` message
7. Silent event stubs have `PLACEHOLDER` comments
8. No magic provider string literals in production code (outside `ProviderNames.cs`)
9. ViewModels do not call raw pipeline execution methods directly
10. `SessionWorkflowCoordinator.cs` stays under 1300 lines

Run this after any structural change. CI should fail on any violation.

---

## Provider and Inference Architecture

The project now has multiple providers per pipeline stage. These are the
structural conventions to follow as more are added:

**Each provider gets its own service class or clearly bounded methods.**
`TranslationService` owns `google-translate-free`. If a new translation provider
is substantial, it gets its own file. This keeps each file focused and testable.

**`ProviderCapability` is the single validation gate.**
All unsupported providers throw `PipelineProviderException` with a useful message.
No silent fallback. No guessing.

**`ProviderOptions` is the single source of provider/model lists.**
The UI, ViewModel, and coordinator all read from here. Do not duplicate lists.

**`SessionWorkflowCoordinator` owns routing.**
Provider-specific branching happens here, not inside services. Services do not
know what provider they are — they execute a specific inference path when called.

**Interfaces are appropriate once you have 2+ real implementations.**
If multiple translation service classes exist with the same call contract, extract
an interface. This is structural hygiene, not premature abstraction.

**AppSettings owns provider configuration.**
Provider selection, model choice, voice name, model directory paths — all
persisted here. Services receive configuration as constructor arguments or
method parameters, not by reading settings directly.

---

## Verification Expectations

Before calling work complete:
- run the build using `dotnet build babel-player.sln` for full consistency
- run relevant tests using `dotnet test babel-player.sln`
- add or update tests when the change is testable
- perform the milestone's manual smoke path
- record a smoke note in `docs/smoke/` using the conventions below

### Troubleshooting
If the build fails with a "process cannot access the file" error (locked by `clrdbg.exe` or `.NET Host`), run the following to clear locks:
`taskkill /F /IM clrdbg.exe /IM dotnet.exe`

Do not claim completion based on static inspection alone.

---

```md id="ag-smoke-template-rules"
### Smoke note conventions

All milestone smoke notes live in `docs/smoke/`.

#### File naming
- `milestone-01-foundation.md`
- `milestone-11-local-offline-expansion.md`
- lowercase, hyphen-separated, two-digit milestone number, short label
- no `_SMOKE_NOTE`, no `_COMPLETE`

#### Status values
- `complete` — all gate items verified
- `partial` — some gate items unverified, explicitly listed
- `failed` — gate not met

Do not use: "mostly done", "substantially complete", "appears working".

#### Truthfulness rules
- If any gate item is unverified, status cannot be `complete`
- Inferred behavior belongs under `What Was Not Verified` or `Deferred Items`
- Do not mark complete while listing unresolved items

#### Required sections
Metadata · Gate Summary · What Was Verified · What Was Not Verified ·
Evidence · Notes · Conclusion · Deferred Items
```

---

## Encouraged Work

These are explicitly good uses of time at this stage:

- Structural improvements that reduce duplication or improve navigability
  across existing working code
- Adding interfaces or base classes once 2+ concrete implementations exist
- Backend preparation for Milestone 12 (hardware routing, runtime selection)
  when it can be done without touching the active workflow
- Expanding provider support with truthful capability gating
- Improving error messages, diagnostics, and user-facing honesty
- Test coverage improvements for existing behaviour
- Clarifying the Python/C# service boundary for future hosting flexibility
- Maintaining `.github/agents/`, `.github/instructions/`, and `.github/prompts/`
  (AI agents, high-level instructions, and reusable prompt templates used by
  our automation and Copilot tooling) so that AI routing and prompt flows
  — which agent handles which task, with what context — stay aligned with
  the current architecture

---

## Still Prohibited

- Fake readiness in any form
- Scattering workflow state outside the coordinator
- Silent provider fallback
- Implicit Python/C# field name assumptions
- Building deployment infrastructure (WSL, container, NVIDIA serving) before
  a local inference path is proven on real hardware
- Speculative abstractions for providers, runtimes, or features not yet planned
- Marking milestone gates complete without evidence
