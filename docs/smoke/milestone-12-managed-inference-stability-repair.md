---
milestone: 12
title: Managed Inference Stability Repair Pass
status: partial
date: 2026-04-10
---

## Metadata

- Scope: managed GPU host lifecycle hardening, cached/serialized provider health, truthful probe/readiness reporting, Qwen concurrency reduction, WeSpeaker migration to managed CPU runtime
- Status: `partial`
- Operator: Codex

## Gate Summary


- [x] Qwen and NeMo heavy readiness work is serialized and cached on the managed GPU host.
- [x] `/capabilities` no longer performs heavy live warmup/import work inline for Qwen and NeMo.
- [x] Managed GPU host restart logic defers when local requests are active or the host reports busy.
- [x] Probe/readiness surfaces live-but-failed provider state instead of downgrading to generic `starting` when stale live state exists.
- [x] Qwen segment execution is serialized to a safe single-flight level.
- [x] WeSpeaker is moved off the managed GPU host path and into the managed CPU runtime.
- [x] GPU diarization capabilities now advertise NeMo only.
- [x] Build passes.
- [x] Automated tests pass.
- [ ] Manual app-shell smoke of Qwen synthesis, NeMo diarization, and WeSpeaker CPU fallback.

## What Was Verified


1. `python -m py_compile inference/main.py` passed.
2. `python3 scripts/check-architecture.py` passed all checks.
3. `dotnet build Babel-Player.sln` passed.
4. `dotnet test Babel-Player.sln --no-build` passed with `854` tests.
5. Focused regression coverage now includes:
   - stale probe semantics returning last-known available state during refresh
   - managed host restart deferral while request leases are active
   - truthful health parsing with `CapabilitiesError` and busy counters
   - containerized diarization client rejecting WeSpeaker on the GPU host path
   - WeSpeaker registry/runtime ownership moving to local CPU runtime
   - WeSpeaker CPU subprocess execution and speaker-label normalization
   - requirements ownership split between CPU and GPU manifests
6. The managed GPU Python host now exposes busy counters and reason text from `/health/live`.
7. The managed GPU Python host now reports cached provider-health state for Qwen and NeMo instead of running deep imports inline on `/capabilities`.

## What Was Not Verified


- Real app-shell startup with Qwen selected after this repair pass.
- End-to-end Qwen segment synthesis under the repaired host lifecycle.
- End-to-end NeMo diarization against a real media file on the rebuilt managed GPU runtime.
- End-to-end WeSpeaker diarization using the managed CPU runtime from the app shell.
- Real hardware validation that host restarts no longer interrupt in-flight Qwen work.

## Evidence


- Host orchestration and health caching:
  - `inference/main.py`
- Host lifecycle protection:
  - `Services/ManagedVenvHostManager.cs`
  - `Services/ContainerizedRequestLeaseTracker.cs`
  - `Services/ContainerizedInferenceClient.cs`
- Probe/readiness truthfulness:
  - `Services/ContainerizedServiceProbe.cs`
  - `Services/ContainerizedProviderReadiness.cs`
- Runtime ownership split:
  - `Services/Registries/DiarizationRegistry.cs`
  - `Services/WeSpeakerCpuDiarizationProvider.cs`
  - `Services/InferenceRuntimeCatalog.cs`
  - `inference/requirements.txt`
  - `inference/gpu-requirements.txt`
- Regression coverage:
  - `BabelPlayer.Tests/ContainerizedServiceProbeTests.cs`
  - `BabelPlayer.Tests/ManagedVenvHostManagerTests.cs`
  - `BabelPlayer.Tests/ContainerizedProvidersTests.cs`
  - `BabelPlayer.Tests/WeSpeakerCpuDiarizationProviderTests.cs`
  - `BabelPlayer.Tests/RegistryTests.cs`

## Notes


This pass intentionally fixes the managed inference stack as a coordinated system. WeSpeaker is no longer treated as a GPU-hosted diarization engine. It now belongs to the managed CPU runtime, which removes the misleading GPU warmup/readiness messaging from its execution path.

The automated verification in this environment is strong enough to confirm the code paths, host-health semantics, and runtime ownership split. It does not substitute for a real app-shell smoke with actual provider startup and media execution.

## Conclusion


Status: `partial`.

The managed inference architecture is repaired in code and regression-tested, but manual end-to-end validation for Qwen, NeMo, and WeSpeaker is still outstanding before this area should be considered fully complete.

## Deferred Items


- Run a manual app-shell smoke with Qwen selected and confirm no restart interrupts active synthesis.
- Run NeMo diarization from the shell and verify honest status reporting on both success and failure.
- Run WeSpeaker CPU fallback from the shell and verify readiness and execution no longer mention GPU warmup.
- Tune Qwen concurrency upward only after the single-flight path proves stable on real hardware.