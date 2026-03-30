# Milestone 7.3 Segment Workflow List - Smoke Note

## Metadata
- Milestone: `07.3`
- Name: `Segment Workflow List`
- Date: `2026-03-29`
- Status: `complete`

## Gate Summary
- [x] `GetSegmentWorkflowListAsync()` builds a per-segment view from transcript, translation, and TTS artifacts
- [x] Each segment reports its own translation and TTS state accurately
- [x] Empty/failed translations do not appear as successfully translated
- [x] Segment list survives session reopen and reconstructs correctly
- [x] Regenerated segment TTS is distinguished from untouched segments

## What Was Verified
- `GetSegmentWorkflowListAsync()` returns one `WorkflowSegmentState` per transcript segment
- Each entry carries `SegmentId`, `StartSeconds`, `EndSeconds`, `SourceText`, `HasTranslation`, `TranslatedText`, `HasTtsAudio`
- `HasTranslation=true` only for segments with a non-empty `translatedText` (segments where googletrans silently failed with `''` are correctly marked as untranslated)
- `HasTtsAudio=true` only for segments explicitly regenerated via `RegenerateSegmentTtsAsync` — bulk TTS does not populate per-segment audio
- After session reopen, segment IDs, translation state, and per-segment TTS state match pre-reopen values
- Regenerating TTS for one segment sets `HasTtsAudio=true` for that segment only; adjacent segments remain `false`

## Evidence

### Commands Run
```bash
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 31
Passed: 31

Tests covering this milestone:
- BuildSegmentWorkflowList_ReflectsArtifactState
- Reopen_ReconstructsMixedSegmentState
- RegeneratedAndUntouchedSegments_RemainDistinct
- GenerateTts_SetsSegmentTrackingStructures (verifies TtsSegmentAudioPaths is empty after bulk TTS)
```

### Implementation Details
- `WorkflowSegmentState` is a sealed record in `Models/WorkflowSegmentState.cs`
- `GetSegmentWorkflowListAsync()` reads transcript JSON for segment geometry, then joins translation and TTS artifact state by segment ID
- Segment IDs are derived via `SessionWorkflowCoordinator.SegmentId(double start)` using `FormattableString.Invariant` — matches Python's `f"segment_{start}"` format and is culture-safe
- Method is `async Task<List<WorkflowSegmentState>>` — reads both files with `File.ReadAllTextAsync` to avoid blocking the UI thread

## Notes
- `HasTtsAudio` reflects per-segment audio only, not the presence of the bulk TTS file. This is intentional: the flag is used to track which segments have been individually refined.
- `GetSegmentWorkflowListAsync()` is not yet wired to any view or viewmodel. The data model is ready; the UI surface is deferred to a later sub-milestone of Milestone 7.

## What Remains (Milestone 7 gate items not yet complete)
- UI surface exposing the segment list to the user
- `SegmentStatus` enum or equivalent for accepted / needs-revision / pending states (current model uses booleans only)
- Compare alternative outputs (not implemented; no placeholder)
- Full end-to-end user journey: load → transcript → translate → generate → selectively refine → save, all through UI

## Conclusion
Milestone 7.3 complete. The per-segment workflow data model and query method exist and are verified. Selective per-segment state (translation present, TTS regenerated) is tracked correctly and survives reopen. The milestone 7 gate cannot be closed until the UI surface and segment status model are in place.
