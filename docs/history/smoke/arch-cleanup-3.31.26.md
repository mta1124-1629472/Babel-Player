# Smoke Note — Milestone arch-cleanup: Architecture Cleanup

## Metadata

- **Date:** 2026-03-31
- **Status:** complete
- **Branch:** architecture-cleanup
- **Build:** dotnet build → 0 errors, 0 warnings
- **Tests:** dotnet test → 92 passed, 0 failed

## Gate Summary

This milestone addressed structural debt accumulated across prior milestones: duplicated subprocess boilerplate, scattered provider/credential string literals, pipeline advancement logic split across ViewModel and Coordinator, ad-hoc transport field management, and absent CancellationToken threading in AI service interfaces. All identified gate items were resolved. The audit also rejected over-engineered recommendations (4-way coordinator split, factory pattern) that were not warranted by the actual problems. The architecture linter was extended to enforce the new constraints with 3 additional checks (10 total), and AGENTS.md was updated to codify the resulting principles.

## What Was Verified

- `Models/ProviderNames.cs` introduced with `ProviderNames.*` and `CredentialKeys.*` constants; all inline magic strings removed from `ProviderCapability`, `ProviderReadinessResolver`, `ProviderOptions`, `EmbeddedPlaybackViewModel`, `ModelsTabViewModel`, `SessionWorkflowCoordinator`, `AppSettings`, and `ApiKeyStore`
- `PipelineInvalidation` enum added; `CheckSettingsInvalidation()` and `AdvancePipelineAsync()` extracted from `EmbeddedPlaybackViewModel` into `SessionWorkflowCoordinator`; ViewModel delegates pipeline advancement rather than gating stages itself
- `IMediaTransportManager` / `MediaTransportManager` extracted; coordinator delegates all transport lifecycle to it; 4 duplicated transport fields removed from coordinator
- `PythonSubprocessServiceBase` abstract class created with `RunPythonScriptAsync()` and `ThrowIfFailed()`; `TranscriptionService`, `TranslationService`, `TtsService`, `NllbTranslationService`, and `PiperTtsService` all inherit from it; approximately 400 lines of duplicated subprocess boilerplate removed; `FindPythonPath()` static methods removed from `TranslationService` and `TtsService`, replaced by `DependencyLocator.FindPython()` in the base class
- `CancellationToken` threaded into `ITranslationService.TranslateAsync`, `TranslateSingleSegmentAsync`, `ITtsService.GenerateTtsAsync`, `GenerateSegmentTtsAsync`, all 4 implementations, and coordinator call sites including the per-segment TTS loop and `File.ReadAllTextAsync`
- `AppLog` startup log rotation implemented: archives log files exceeding 10 MB with a timestamp suffix; retains last 4 archives plus the current log (5 total); oldest archives pruned on a best-effort basis
- File handle audit across `Services/`, `Models/`, `ViewModels/` confirmed all `FileStream`/`StreamReader`/`StreamWriter` usages are guarded with `using`
- Architecture linter (`scripts/check-architecture.py`) extended with 3 new checks: (8) magic provider string literals outside `ProviderNames.cs`, (9) ViewModel calls to raw pipeline execution methods, (10) `SessionWorkflowCoordinator` line count exceeding 1300; test files excluded from check 8; stage checks narrowed to execution calls only
- AGENTS.md updated with principles 11–14 (service interface uniformity, provider identifier constants, `AdvancePipelineAsync` ownership, `MediaTransportManager` transport lifecycle) and an Architecture Linter section listing all 10 checks
- Pre-existing csproj bug fixed: `BabelPlayer/` subdirectory SDK wildcard was causing duplicate assembly attribute errors; `Compile`/`EmbeddedResource`/`None Remove="BabelPlayer\**"` exclusions added
- `dotnet build`: 0 errors, 0 warnings
- `dotnet test`: 92/92 pass
- `python3 scripts/check-architecture.py`: all 10 checks pass
- PR #62: https://github.com/mta1124-1629472/Babel-Player/pull/62

## What Was Not Verified

- No manual smoke path through the full dubbing workflow (this milestone is a structural refactor with no user-facing changes)
- Python inference services not exercised end-to-end in this session (covered by the 92 integration tests)
- Log rotation not triggered in practice — requires a 10 MB log file at startup, which was not present during this session

## Evidence

**Build**

```
dotnet build → 0 errors, 0 warnings
```

**Tests**

```
dotnet test → 92 passed, 0 failed
```

**Architecture linter**

```
python3 scripts/check-architecture.py → all 10 checks pass
```

**Pull request:** https://github.com/mta1124-1629472/Babel-Player/pull/62

Phases completed in sequence:

- Phase 1: provider/credential constant centralization
- Phase 2A: pipeline advancement ownership moved to coordinator
- Phase 2B: transport lifecycle extracted to `MediaTransportManager`
- Phase 2C: service interfaces already extracted — skipped
- Phase 2D: `PythonSubprocessServiceBase` with 5 inheriting services
- Phase 3A: `CancellationToken` threaded through AI service interfaces and coordinator
- Phase 3B: `AppLog` startup rotation
- Phase 3C: file handle audit — no issues found
- Phase 4: linter extended (3 new checks), `AGENTS.md` updated with principles 11–14

## Notes

The original audit surfaced both real structural debt and over-engineered recommendations. The 4-way coordinator split and factory pattern were explicitly rejected as premature; only the concrete, demonstrable problems were addressed. The linter extensions ensure the new constraints are enforced going forward rather than relying on convention alone. The csproj duplicate-attribute bug was pre-existing and unrelated to the milestone work but was fixed in the same branch as it was blocking clean builds.

## Conclusion

Gate met: all structural cleanup phases completed, build is clean, all 92 tests pass, and the architecture linter passes all 10 checks on the updated codebase.

## Deferred Items

- `CancellationToken` not yet added to `RegenerateSegmentTtsAsync`, `RegenerateSegmentTranslationAsync`, or `GetSegmentWorkflowListAsync` (lower urgency; these are single-segment operations not part of long-running batch paths)
- Log rotation tested at startup only; in-process rotation for sessions that accumulate more than 10 MB without a restart is not implemented
