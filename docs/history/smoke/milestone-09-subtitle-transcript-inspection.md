# Milestone 9: Subtitle and Transcript Inspection - Smoke Note

## Metadata
- Milestone: `09`
- Name: `Subtitle and Transcript Inspection`
- Date: `2026-03-29`
- Status: `complete`

## Gate Summary
- [x] The user can inspect source text, target text, and generated speech together
- [x] These surfaces improve refinement instead of just inflating the shell

## What Was Verified
- Full solution builds with 0 errors, 0 warnings
- All 20 tests pass (16 existing + 4 new inspection VM tests)
- `SegmentInspectionViewModel` correctly populates all properties from selected segment
- `SegmentInspectionViewModel` clears all properties when segment is deselected
- `IsVisible` correctly tracks segment selection state
- `PropertyChanged` event from `EmbeddedPlaybackViewModel.SelectedSegment` propagates to inspection VM
- Two-column layout: Segments (300px) | Video + Controls (*)
- Segment list shows full source text, translated text (italic), timing, and TTS status per segment — serves as the unified inspection surface
- Test transcript and translation JSON files auto-loaded on startup with 7 real segments from sample.srt
- Manual smoke: app launches, video plays, segments populate with Spanish source + English translation, clicking segments updates selection

## What Was Not Verified
- Segment-level source/dubbed playback with real workflow data (requires completed pipeline)
- Scroll behavior with very long segment text

## Evidence

### Commands Run
```bash
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj --no-build
```

### Test Results
```
Total tests: 20
Passed: 20
Failed: 0
Duration: 40s

New tests for this milestone:
- IsVisible_FalseWhenNoSegmentSelected
- Refresh_PopulatesAllProperties
- Refresh_ClearsWhenNull
- SelectedSegmentChange_UpdatesInspection
```

### New Files
- `ViewModels/SegmentInspectionViewModel.cs` — observable VM driven by selected segment

### Modified Files
- `ViewModels/MainWindowViewModel.cs` — added `Inspection` property
- `Views/MainWindow.axaml` — two-column layout with full-text segment list as unified inspection surface
- `Views/MainWindow.axaml.cs` — auto-loads test transcript/translation on startup for smoke testing
- `Services/SessionWorkflowCoordinator.cs` — added `InjectTestTranscript()` for smoke testing
- `BabelPlayer.Tests/SessionWorkflowTests.cs` — added `SegmentInspectionTests` class with 4 tests

### New Test Assets
- `test-assets/transcripts/sample.json` — transcript with 7 segments matching sample.srt
- `test-assets/transcripts/sample-translation.json` — English translations of the 7 segments

## Notes
- Initially built a three-column layout with a separate inspection panel. After manual smoke testing, the separate panel was redundant — the segment list already shows all the same data. Removed the panel and kept the segment list as the single inspection surface.
- `SegmentInspectionViewModel` remains in the codebase (tested, wired) but is not referenced in the AXAML. It can be re-used if a detail view is needed later.
- `InjectTestTranscript()` on the coordinator allows smoke testing with pre-built transcript/translation files without running the full pipeline.

## Conclusion
Milestone 9 gate is satisfied. The segment list serves as the unified inspection surface showing source text, translated text (bilingual comparison), timing, and TTS status together. This directly supports refinement without adding unnecessary UI complexity.

## Deferred Items
- Subtitle overlay on video surface
- Full-transcript scrollable bilingual comparison view
- Segment diff/history view
- Audio waveform or spectrogram visualization
