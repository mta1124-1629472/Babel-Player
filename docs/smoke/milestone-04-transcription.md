# Milestone 04 Transcription v1 - Smoke Note

## Metadata
- Milestone: `04`
- Name: `Transcription v1`
- Date: `2026-03-28`
- Status: `complete`

## Gate Summary
- [x] Accept one local media file (mp4/wav/mp3)
- [x] Run one narrow transcription path (faster-whisper base model)
- [x] Produce timed transcript segments
- [x] Persist transcript artifacts in session
- [x] Prove session can reopen and reuse transcript after restart
- [x] Surface missing transcript as truthful degraded state

## What Was Verified
- `LoadMedia()` copies source to session-owned directory
- `TranscribeMediaAsync()` runs faster-whisper transcription
- Transcript segments are timestamped (start/end seconds, text)
- Transcript persisted to `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/transcripts/{filename}.json`
- On reopen, coordinator verifies transcript artifact exists
- Missing transcript surfaces truthful degraded state with logging
- Tests prove full load → transcribe → persist → reopen → reuse loop

## Evidence

### Commands Run
```text
pip install faster-whisper
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 12
Passed: 12

New tests (Milestone 4):
- TranscribeMedia_ProducesTimedSegments
- TranscribeMedia_ThenReopen_ReusesTranscript
- ReopenWithMissingTranscript_SurfacesDegradedState

Existing tests still pass (9):
- MediaTransportTests (6)
- SessionWorkflowTests (3)
```

### Transcription Output Example
```json
{
  "language": "es",
  "language_probability": 0.99,
  "segments": [
    { "start": 0.0, "end": 3.68, "text": "Hoy vamos a hacer un ejercicio..." },
    ...
  ]
}
```

### Artifacts / Paths
- Transcript location: `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/transcripts/{filename}.json`
- Uses `LocalApplicationData` (not roaming)

## Notes
- Uses `faster-whisper` with `base` model (CPU, int8)
- Supports formats: wav, mp3, flac, ogg, mp4, avi, mkv, mov
- Auto-extracts audio from video formats before transcription
- Python process spawned for transcription (isolated in TranscriptionService)

## Conclusion
Milestone 04 complete. First real AI slice works:
- load media
- transcribe with faster-whisper
- persist timed transcript
- reopen and reuse transcript
- missing transcript surfaces as truthful degraded state

## What Remains for Future Work
- Translation/adaptation (Milestone 5)
- TTS/dubbing (Milestone 6)
- In-context preview
- Multiple model/provider options (not needed yet)
