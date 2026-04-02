---
milestone: 12
title: Containerized Inference Readiness Honesty and External Service Posture
status: partial
date: 2026-04-02
---

## Metadata
- Scope: external/local containerized inference contract, live readiness checks, startup regression smoke
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] Containerized providers now report not-ready when the configured service is down.
- [x] Provider readiness consumes live stage capabilities, not just liveness.
- [x] The supported container posture is now external/local inference service only.
- [x] The repo `docker-compose.yml` is explicitly dev-only.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Manual verification against a live local inference container stack.

## What Was Verified
1. `dotnet build BabelPlayer.csproj /p:UseSharedCompilation=false` completed successfully.
2. `dotnet test BabelPlayer.Tests\\BabelPlayer.Tests.csproj` passed with 198 tests.
3. `python scripts/check-architecture.py` passed all 10 checks.
4. Manual app startup smoke succeeded: `dotnet run --project BabelPlayer.csproj /p:UseSharedCompilation=false` launched and remained running for 8 seconds without startup failure.
5. App log recorded normal bootstrap completion with `Bootstrap: inference mode = SubprocessCpu (Local subprocess (CPU))`, confirming the non-container path did not regress.

## What Was Not Verified
- Live readiness and capability transition against a running local inference container.
- End-to-end source media -> transcript -> translation -> TTS through the external/local containerized service.

## Evidence
- Shared readiness gate: `Services/ContainerizedProviderReadiness.cs`
- Startup wiring and env override: `App.axaml.cs`, `Services/Settings/AppSettings.cs`
- Provider readiness updates: `Services/ContainerizedTranscriptionProvider.cs`, `Services/ContainerizedTranslationProvider.cs`, `Services/ContainerizedTtsProvider.cs`
- Service contract: `Services/ContainerizedInferenceClient.cs`, `inference/main.py`
- Dev container assets: `docker-compose.yml`, `inference/Dockerfile`, `inference/requirements.txt`
- Test coverage: `BabelPlayer.Tests/RegistryTests.cs`
- Startup log excerpt:
  - `App startup: session coordinator ready.`
  - `Bootstrap: all dependencies available.`
  - `Bootstrap: inference mode = SubprocessCpu (Local subprocess (CPU))`

## Notes
The desktop app no longer claims to manage Docker lifecycle. Container support is now an honest external/local service contract instead of a repo-relative orchestration path.

## Conclusion
Status: `partial`.

The contract and docs now match: the app probes an external/local inference service and refuses unavailable or incapable stages. Live local container verification still needs a manual pass.

## Deferred Items
- Run a manual smoke against an actual local inference container and update this note to `complete` if stage readiness and end-to-end containerized inference are verified.
