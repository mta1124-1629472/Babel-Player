# Milestone 7.2 Single Segment Translation Regeneration - Smoke Note

## Metadata
- Milestone: `07.2`
- Name: `Single Segment Translation Regeneration`
- Date: `2026-03-29`
- Status: `complete`

## Gate Summary
- [x] Regenerate translation for a single segment by stable segment ID
- [x] Leave all other segments unchanged
- [x] Persist the updated translation artifact
- [x] Reopen session and preserve mixed state correctly

## What Was Verified
- `RegenerateSegmentTranslationAsync(segmentId)` re-translates only one segment
- Translation segment ID based on start time (e.g., "segment_0.0")
- Uses segment's `text` field as source for re-translation
- Only the selected segment's `translatedText` is updated
- Other segments remain unchanged
- Translation persists to existing artifact file
- Reopen preserves updated segment
- Invalid segment ID throws `InvalidOperationException`

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
- RegenerateSegmentTranslation_UpdatesSingleSegment
- RegenerateSegmentTranslation_DoesNotModifyOtherSegments
- Reopen_PreservesUpdatedTranslationSegment
- RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment  [new — see bug fix below]
- RegenerateSegmentTranslation_ThrowsOnFailedTranslation       [new]
- TranslateTranscript_PersistsSourceLanguageToSnapshot         [new]
- TranslateTranscript_ThenReopen_PreservesSourceLanguage       [new]
```

### Implementation Details
- Uses segment IDs from translation JSON (e.g., "segment_0.0", "segment_3.68")
- Reads source text from segment's `text` field (NOT translatedText)
- Re-translates via googletrans
- Updates only the selected segment in the JSON file
- Other segments preserved unchanged
- Segment ID format is culture-invariant (uses `FormattableString.Invariant`)

## Bug Fixed Post-Original Gate

### Critical: segmentId was never passed to Python

The original 7.2 implementation had a bug where `TranslateSingleSegmentAsync` never
passed the `segmentId` argument to the Python script. The script received an empty
string for `sys.argv[5]`, causing the guard condition `if 'segments' in data and seg_id:`
to evaluate as falsy. The segment was translated but **no segment in the file was ever
updated** — the file was read and re-written unchanged.

The original tests passed because they only asserted `translatedText != null`, which was
already true before the call.

**Fixes applied (2026-03-29):**
- `TranslationService.TranslateSingleSegmentAsync`: added `segmentId` parameter; fixed
  `Arguments` string to pass it as `sys.argv[5]`
- `SessionWorkflowCoordinator.RegenerateSegmentTranslationAsync`: passes `segmentId` to
  service; checks `result.Success` (was unchecked); uses `CurrentSession.SourceLanguage`
  instead of hardcoded `"es"`
- `WorkflowSessionSnapshot`: added `SourceLanguage` field, persisted during
  `TranslateTranscriptAsync`
- `SessionWorkflowCoordinator.GenerateTtsAsync`: removed dead first `CurrentSession`
  assignment (the second write was overwriting the first with slightly different data)
- `SessionWorkflowCoordinator.SegmentId(double)`: extracted shared static helper using
  `FormattableString.Invariant` — matches Python's `f"segment_{start}"` format and is
  culture-safe

**New regression test:** `RegenerateSegmentTranslation_ActuallyWritesNewTextToSegment`
corrupts a segment's `translatedText` to a sentinel value before calling regen, then
asserts the sentinel is gone afterward. This test would have caught the original bug.

## Notes
- googletrans is deterministic — same input produces same output
- `RegenerateSegmentTranslation_UpdatesSingleSegment` still only asserts the text is
  non-null (not changed); the sentinel test is the definitive coverage for the update path
- No TTS modifications
- No UI work

## Conclusion
Milestone 7.2 complete. Single segment translation regeneration works:
- regenerate translation for one segment by ID
- segment text is actually written to the file (previously broken — fixed)
- other segments unchanged
- persisted to translation artifact
- reopen preserves updated segment
- source language is persisted to snapshot and used on regen

## What Remains
- Full dub session workflow (Milestone 7)
- In-context preview
- Multiple voice options
