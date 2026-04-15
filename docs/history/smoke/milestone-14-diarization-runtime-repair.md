---
milestone: 14
title: Diarization Runtime Repair
status: partial
date: 2026-04-14
---

## Metadata

- Scope: managed CPU WeSpeaker bootstrap/readiness repair, managed CPU dependency validation, NeMo Torch 2.8 meta-tensor compatibility, live CPU/GPU diarization execution
- Status: `partial`
- Operator: Codex

## Gate Summary

- [x] Managed CPU runtime uses dedicated `cpu-requirements.txt` and `cpu-constraints.txt`.
- [x] Managed CPU bootstrap validates `torch`, `torchaudio`, and `wespeaker` imports before marking the runtime ready.
- [x] Local WeSpeaker diarization bootstraps before the coordinator applies the readiness gate.
- [x] WeSpeaker subprocess stdout is JSON-clean for C# parsing.
- [x] NeMo diarization config matches the installed `nemo-toolkit 2.7.2` schema.
- [x] Managed GPU host handles NeMo Torch 2.8 meta-tensor restore during diarizer construction.
- [x] `dotnet build Babel-Player.sln` passed.
- [x] `dotnet test Babel-Player.sln` passed.
- [x] `python scripts/check-architecture.py` passed.
- [x] `python -m py_compile inference/main.py` passed.
- [x] Live CPU diarization succeeded via the compiled `WeSpeakerCpuDiarizationProvider`.
- [x] Live GPU diarization succeeded via the managed GPU `/diarize` endpoint.
- [x] Full app-shell/manual UX smoke of diarization-triggered transcript/translation artifact updates.

## What Was Verified

1. `dotnet build Babel-Player.sln` passed.
2. `dotnet test Babel-Player.sln` passed with `910/910` tests.
3. `python scripts/check-architecture.py` passed all checks.
4. `python -m py_compile inference/main.py` passed.
5. Managed CPU import validation succeeded in the rebuilt runtime:
   - `torch=2.8.0`
   - `torchaudio=2.8.0`
   - `wespeaker=0.0.0`
6. Live CPU diarization succeeded through the compiled provider path using the managed CPU runtime and sample WAV:
   - `ready=True`
   - `success=True`
   - `speakers=1`
7. Managed GPU `/capabilities` reported diarization ready with provider detail `NeMo ClusteringDiarizer construction ready`.
8. Live GPU `/diarize` succeeded against the sample WAV and returned normalized speaker segments.
9. Regression coverage now includes:
   - CPU marker hash changes when either CPU manifest changes
   - persisted CPU validation failures surfacing through readiness
   - local coordinator diarization bootstrapping before readiness gating
   - WeSpeaker script output remaining parseable after the subsegment patch
   - NeMo config-contract assertions for nested `speaker_embeddings.parameters.*`
10. Existing coordinator coverage for transcript/translation speaker-ID merge remained green:
   - `SessionWorkflowCoordinatorUnitTests.RunDiarizationAsync_TranslatedSession_UpdatesTranscriptAndTranslationSpeakerIds`
11. Manual confirmation was provided on 2026-04-15 for app-shell diarization UX and transcript/translation artifact update behavior.

## What Was Not Verified

- Performance characterization of CPU vs GPU diarization after the repair.

## Evidence

- CPU runtime/bootstrap:
  - `Services/ManagedCpuRuntimeManager.cs`
  - `inference/cpu-requirements.txt`
  - `inference/cpu-constraints.txt`
- CPU provider/readiness:
  - `Services/WeSpeakerCpuDiarizationProvider.cs`
  - `Services/SessionWorkflowCoordinator.Playback.cs`
- GPU host compatibility:
  - `inference/main.py`
- Regression coverage:
  - `BabelPlayer.Tests/PythonSubprocessServiceBaseTests.cs`
  - `BabelPlayer.Tests/WeSpeakerCpuDiarizationProviderTests.cs`
  - `BabelPlayer.Tests/SessionWorkflowCoordinatorUnitTests.cs`
  - `BabelPlayer.Tests/InferenceRequirementsTests.cs`
- Live runtime checks:
  - managed CPU venv: `%LOCALAPPDATA%\\BabelPlayer\\runtime\\managed-cpu\\.venv`
  - managed GPU host on `http://127.0.0.1:18083`
  - sample audio: `%TEMP%\\babel-diarization-sample.wav`

## Notes

The CPU failure resolved in three layers:

1. readiness now reflects persisted bootstrap/validation state instead of a fresh in-memory default,
2. the CPU runtime is pinned and validated independently from the GPU host dependencies,
3. the embedded WeSpeaker script now suppresses library stdout chatter so C# receives only JSON.

The GPU failure resolved in two layers:

1. the NeMo diarization config now matches the installed schema,
2. the host applies a NeMo restore compatibility fallback for Torch 2.8 meta-tensor model construction.

This smoke pass used a temporary external console runner only to exercise the compiled public/internal C# provider path against the managed CPU runtime. No product code was added for that helper.

## Conclusion

Status: `partial`.

Both live diarization engines now execute successfully in their intended runtimes, the repo verification suite is green, and manual app-shell artifact-update UX has been confirmed. Remaining confidence work is performance characterization and optional diagnostics refinement.

## Deferred Items

- Capture before/after timing for CPU WeSpeaker and GPU NeMo on a representative multi-speaker file.
- Decide whether WeSpeaker diagnostic stderr should be surfaced in success-path logs for operator visibility.
