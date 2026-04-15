# Milestone 06 TTS Dubbing - Smoke Note

## Metadata
- Milestone: `06`
- Name: `TTS Dubbing v1`
- Date: `2026-03-28`
- Status: `complete`

## Gate Summary
- [x] Accept translated English text segments
- [x] Run one narrow TTS path (edge-tts)
- [x] Generate spoken audio output
- [x] Persist TTS artifacts in session
- [x] Prove session can reopen and reuse TTS audio after restart
- [x] Surface missing TTS as truthful degraded state

## What Was Verified
- `GenerateTtsAsync()` runs edge-tts synthesis
- TTS audio file generated with non-zero size
- TTS persisted to `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/tts/{filename}_{voice}.mp3`
- On reopen, coordinator verifies TTS artifact exists
- Missing TTS surfaces truthful degraded state with logging
- Tests prove full load → transcribe → translate → TTS → persist → reopen → reuse loop

## Evidence

### Commands Run
```bash
pip install edge-tts
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 18
Passed: 18

New tests (Milestone 6):
- GenerateTts_ProducesAudio
- GenerateTts_ThenReopen_ReusesAudio
- ReopenWithMissingTts_SurfacesDegradedState

Existing tests still pass (15):
- MediaTransportTests (6)
- SessionWorkflowTests (12)
```

### TTS Output
- Voice: en-US-AriaNeural
- Format: MP3
- Input: Translated English text from Milestone 5
- Output file persisted to session artifacts directory

### Artifacts / Paths
- TTS location: `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/tts/{filename}_{voice}.mp3`
- Uses `LocalApplicationData` (not roaming)

## Notes
- Uses `edge-tts` (Microsoft Edge text-to-speech)
- Voice: `en-US-AriaNeural` (fixed for this narrow slice)
- Input: Translated segments from Milestone 5 (English text)
- Combines all translated segments into single TTS output
- No voice cloning, no multiple providers, no cloud settings

## Conclusion
Milestone 06 complete. First TTS dubbing slice works:
- load media
- transcribe (faster-whisper)
- translate (googletrans es→en)
- generate TTS (edge-tts en-US-AriaNeural)
- persist audio artifact
- reopen and reuse TTS
- missing TTS surfaces as truthful degraded state

## What Remains for Future Work
- In-context preview with playback (Milestone 7)
- Segment-aware TTS (per-segment audio)
- Multiple voice options
- Dub timing sophistication
