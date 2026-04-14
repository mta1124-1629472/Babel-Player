# Milestone 03 Media Ingest - Smoke Note

## Metadata
- Milestone: `03`
- Name: `Media Ingest`
- Date: `2026-03-28`
- Status: `complete`

## Gate Summary
- [x] Accept one local media file
- [x] Persist one reusable session-owned media artifact
- [x] Persist artifact metadata in session snapshot
- [x] Prove session can reopen and reuse artifact after restart

## What Was Verified
- `LoadMedia()` copies source media to session-owned directory
- Snapshot stores `SourceMediaPath` and `IngestedMediaPath`
- On reopen, coordinator verifies artifact exists
- Missing artifact surfaces truthful degraded state with logging
- Tests prove the full load → copy → persist → reopen → reuse loop

## Evidence

### Commands Run
```text
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 9
Passed: 9

New tests:
- LoadMedia_ThenReopen_ReusesArtifact
- ReopenWithMissingArtifact_SurfacesDegradedState

Existing tests still pass (7):
- MediaTransportTests (6 tests)
- UnitTest1 (1 test)
```

### Artifacts / Paths
- Artifact location: `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/media/{filename}`
- Example: `C:\Users\...\AppData\Local\BabelPlayer\sessions\{guid}\media\sample.mp4`

## Notes
- Uses `LocalApplicationData` for session-owned artifacts (not roaming)
- SessionWorkflowStage now has `MediaLoaded = 1`
- Snapshot model includes: SourceMediaPath, IngestedMediaPath, MediaLoadedAtUtc
- Missing artifact-on-reopen is logged and surfaced as degraded state

## Conclusion
Milestone 03 first slice is complete. The narrow media ingest path works:
- load media
- copy it to a session-owned location
- persist metadata
- reopen and verify artifact presence
- surface missing artifact as a truthful degraded state.

## What Remains for Next Sub-slice
- Extract real audio artifact from source media (not just media file copy)
- Downstream milestones: transcription, translation, TTS
