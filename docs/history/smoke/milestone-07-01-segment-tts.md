# Milestone 7.1 Single Segment TTS Regeneration - Smoke Note

## Metadata
- Milestone: `07.1`
- Name: `Single Segment TTS Regeneration`
- Date: `2026-03-28`
- Status: `complete`

## Gate Summary
- [x] Regenerate TTS for a single segment without touching others
- [x] Persist updated audio artifact
- [x] Prove reopen preserves the change

## What Was Verified
- `RegenerateSegmentTtsAsync(segmentId)` generates TTS for a single segment
- Segment ID based on start time (e.g., "segment_0.0", "segment_3.68")
- Per-segment audio saved to `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/tts/segments/{segmentId}.mp3`
- Segment audio path persisted in snapshot's `TtsSegmentAudioPaths` dictionary
- On reopen, segment audio path is restored from snapshot
- Other segments are NOT affected by regeneration

## Evidence

### Commands Run
```bash
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 20
Passed: 20

New tests (Milestone 7.1):
- RegenerateSegmentTts_ProducesSingleSegmentAudio
- RegenerateSegmentTts_ThenReopen_PreservesChange

Existing tests still pass (18):
- MediaTransportTests (6)
- SessionWorkflowTests (14)
```

### Segment ID Format
- Translation segments now include `id` field
- Format: `segment_{start_time}` (e.g., "segment_0.0", "segment_3.68")
- Stable across sessions

### Artifacts / Paths
- Segment TTS: `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/tts/segments/{segmentId}.mp3`

## Notes
- Uses segment start time as stable ID
- Per-segment audio tracked in snapshot's `TtsSegmentAudioPaths` dictionary
- Does NOT re-run full TTS pipeline
- Only regenerates the specified segment

## Conclusion
Milestone 7.1 complete. Single segment TTS regeneration works:
- generate full TTS
- regenerate single segment by ID
- segment audio persisted separately
- reopen preserves regenerated segment
- other segments untouched

## What Remains
- Full dub session workflow (Milestone 7)
- In-context preview
- Multiple voice options
