---
milestone: 12
title: Containerized Inference Autostart and External Service Posture
status: partial
date: 2026-04-02
---

## Metadata
- Scope: external/local containerized inference contract, loopback autostart, live readiness checks, startup regression smoke
- Status: `partial`
- Operator: Codex

## Gate Summary
- [x] Containerized providers now report not-ready when the configured service is down.
- [x] Provider readiness consumes live stage capabilities, not just liveness.
- [x] The supported container posture is now external/local inference service only.
- [x] The app can attempt local container autostart for loopback service URLs.
- [x] The bundled `docker-compose.yml` is treated as a same-machine local inference helper, not a remote deployment story.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Manual verification against a live local inference container stack.

## What Was Verified
1. `dotnet build BabelPlayer.csproj -c Release /p:UseSharedCompilation=false` completed successfully.
2. `dotnet test BabelPlayer.Tests\\BabelPlayer.Tests.csproj -c Release /p:UseSharedCompilation=false` passed with 337 tests.
3. `python scripts/check-architecture.py` passed all 10 checks.
4. Manual app startup smoke succeeded: `dotnet run --no-build -c Release --project BabelPlayer.csproj` launched and remained running until the CLI timeout without startup failure.
5. With a temporary settings override enabling `AlwaysRunContainerAtAppStart=true` and `ContainerizedServiceUrl=http://localhost:8000`, app startup logged a local container autostart attempt instead of silently requiring a pre-started service.
6. The autostart path remained non-fatal when Docker or the local inference service was unavailable; startup completed and the app stayed responsive.

## What Was Not Verified
- Live readiness and capability transition against a running local inference container.
- End-to-end source media -> transcript -> translation -> TTS through the external/local containerized service.
- Successful `docker compose up -d inference` on a machine with a healthy local Docker engine.

## Evidence
- Shared readiness gate: `Services/ContainerizedProviderReadiness.cs`
- Startup wiring and env override: `App.axaml.cs`, `Services/Settings/AppSettings.cs`
- Local autostart manager: `Services/ContainerizedInferenceManager.cs`, `Services/SessionWorkflowCoordinator.Containerized.cs`
- Provider readiness updates: `Services/ContainerizedTranscriptionProvider.cs`, `Services/ContainerizedTranslationProvider.cs`, `Services/ContainerizedTtsProvider.cs`
- Service contract: `Services/ContainerizedInferenceClient.cs`, `inference/main.py`
- Dev container assets: `docker-compose.yml`, `inference/Dockerfile`, `inference/requirements.txt`
- Test coverage: `BabelPlayer.Tests/RegistryTests.cs`, `BabelPlayer.Tests/AppSettingsTests.cs`, `BabelPlayer.Tests/ContainerizedInferenceManagerTests.cs`, `BabelPlayer.Tests/SessionWorkflowCoordinatorUnitTests.cs`
- Startup log excerpt:
  - `App startup: session coordinator ready.`
  - `Container autostart requested: trigger=AppStartup, url=http://localhost:8000, compose=D:\Dev\Babel-Player\bin\Release\net10.0\docker-compose.yml`
  - `Container autostart failed: unable to get image 'net100-inference' ... check if the daemon is running`

## Notes
The desktop app now has a narrow, honest Docker lifecycle hook for local loopback services only. It can request `docker compose up -d inference` for the bundled local stack, but it still treats remote services, WSL, and non-loopback endpoints as external dependencies.

## Conclusion
Status: `partial`.

The contract and docs now match: the app probes an external/local inference service, can attempt loopback autostart for the bundled local stack, and refuses unavailable or incapable stages. Live local container verification still needs a manual pass.

## Deferred Items
- Run a manual smoke against an actual local inference container and update this note to `complete` if autostart, stage readiness, and end-to-end containerized inference are verified.
