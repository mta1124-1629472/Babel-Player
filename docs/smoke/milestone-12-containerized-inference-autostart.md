---
milestone: 12
title: Containerized Inference Autostart and Readiness Honesty
status: partial
date: 2026-04-02
---

## Metadata
- Scope: local containerized inference startup wiring, live readiness checks, startup regression smoke
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] Containerized providers now report not-ready when the configured service is down.
- [x] Local `localhost` containerized configurations can attempt `docker compose up -d inference` during startup.
- [x] Manager only auto-starts local loopback service URLs; remote URLs are probe-only.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Manual verification of Docker-backed autostart against a live inference container stack.

## What Was Verified
1. `dotnet build BabelPlayer.csproj /p:UseSharedCompilation=false` completed successfully.
2. `dotnet test BabelPlayer.Tests\\BabelPlayer.Tests.csproj` passed with 198 tests.
3. `python scripts/check-architecture.py` passed all 10 checks.
4. Manual app startup smoke succeeded: `dotnet run --project BabelPlayer.csproj /p:UseSharedCompilation=false` launched and remained running for 8 seconds without startup failure.
5. App log recorded normal bootstrap completion with `Bootstrap: inference mode = SubprocessCpu (Local subprocess (CPU))`, confirming the new startup wiring did not regress the default non-container path.

## What Was Not Verified
- Live Docker autostart of `docker compose up -d inference` from the desktop app.
- Health transition from unavailable to healthy with a running local inference container.
- Exit-time `docker compose stop inference` behavior for a manager-started container.

## Evidence
- New service manager: `Services/ContainerizedInferenceManager.cs`
- Shared readiness gate: `Services/ContainerizedProviderReadiness.cs`
- Startup wiring: `App.axaml.cs`
- Provider readiness updates: `Services/ContainerizedTranscriptionProvider.cs`, `Services/ContainerizedTranslationProvider.cs`, `Services/ContainerizedTtsProvider.cs`
- Test coverage: `BabelPlayer.Tests/ContainerizedInferenceManagerTests.cs`, `BabelPlayer.Tests/RegistryTests.cs`
- Startup log excerpt:
  - `App startup: session coordinator ready.`
  - `Bootstrap: all dependencies available.`
  - `Bootstrap: inference mode = SubprocessCpu (Local subprocess (CPU))`

## Notes
The implementation deliberately avoids auto-starting remote container URLs. Only loopback service URLs are eligible for local Docker orchestration.

## Conclusion
Status: `partial`.

The code path is built, tested, and non-container startup was manually smoke-checked. Live Docker autostart still needs a manual verification pass on a machine with the intended container stack running.

## Deferred Items
- Run a manual smoke against an actual local Docker inference service and update this note to `complete` if the autostart and shutdown behavior are verified end-to-end.
