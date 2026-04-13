## Metadata
- Status: `partial`
- Date: `2026-04-11`
- Scope: `Diarized workflow pause/resume`, `managed GPU runtime honesty`, `status-bar warm-up surfacing`, `requirements split`

## Gate Summary
- `dotnet build Babel-Player.sln`: passed
- `dotnet test Babel-Player.sln`: passed (`877` passed, `0` failed)
- `python3 scripts/check-architecture.py`: passed
- `python -m py_compile inference/main.py`: passed

## What Was Verified
- `SessionWorkflowStage.Diarized` persists correctly, round-trips through stores, serializes as strings, and still reads legacy numeric enum values.
- Snapshot semantics now preserve legacy/single-speaker translated sessions while still supporting reset-to-`Diarized` when diarization markers exist.
- Pipeline progress covers:
  - straight-through single-speaker runs,
  - pause-at-`Diarized` multi-speaker runs,
  - `ContinuePipelineAsync(...)`,
  - `RunTtsOnlyAsync(...)`.
- Managed/containerized readiness tests cover:
  - honest unavailable/failed/cached wording,
  - provider-specific warm-up wording,
  - Qwen concurrency metrics passthrough,
  - stale-probe handling,
  - managed-host restart deferral behavior under reported work/warm-up.
- Python inference contract checks now cover the NeMo diarizer config shape, including top-level speaker-embedding multiscale fields and `diarizer.vad.parameters.*`.
- Requirements coverage now asserts:
  - CPU runtime keeps WeSpeaker/S3PRL,
  - CPU runtime does not carry `qwen-tts`,
  - GPU runtime carries `qwen-tts` and `nemo-toolkit[asr]`,
  - GPU runtime does not carry WeSpeaker/S3PRL.

## What Was Not Verified
- Manual UI confirmation that switching transcription, translation, or TTS to GPU immediately updates the main status bar with bootstrap/warm-up/ready/failure state.
- Manual end-to-end multi-speaker session flow through:
  - transcribe,
  - diarize/pause,
  - assign per-speaker voices,
  - continue,
  - dub preview.
- Real managed-GPU runtime behavior on an actual CUDA machine during host restart deferral and provider warm-up.

## Evidence
- Automated test run:
  - `dotnet test Babel-Player.sln`
  - Result: `877` passed, `0` failed
- Architecture check:
  - `python3 scripts/check-architecture.py`
  - Result: all checks passed
- Python verification:
  - `python -m py_compile inference/main.py`
  - Result: passed

## Notes
- This smoke note is `partial` because the required GPU/status-bar/manual session checks were not executed from this environment.
- Automated coverage was expanded to exercise the new persisted stage, legacy enum compatibility path, pause/resume flow, TTS-only reruns, and managed-host/provider-readiness messaging.

## Conclusion
- The implementation is ready from an automated verification standpoint.
- Manual confirmation is still required for the interactive GPU warm-up UX and the user-driven speaker-mapping continuation flow.

## Deferred Items
- Run the manual GPU runtime selection smoke path on a machine with the managed GPU runtime available.
- Run a real multi-speaker media sample through the new `Diarized` pause and continuation UX.
