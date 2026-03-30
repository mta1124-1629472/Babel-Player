# Milestone 05 Translation v1 - Smoke Note

## Metadata
- Milestone: `05`
- Name: `Translation v1`
- Date: `2026-03-28`
- Status: `complete`

## Gate Summary
- [x] Accept persisted timed transcript segments
- [x] Run one narrow translation path (googletrans Spanish→English)
- [x] Produce translated/adapted dialogue per segment
- [x] Persist translated artifacts in session
- [x] Prove session can reopen and reuse translation after restart
- [x] Surface missing translation as truthful degraded state

## What Was Verified
- `TranslateTranscriptAsync()` runs translation via googletrans
- Translation preserves segment timing (start/end seconds)
- Translated text is added to each segment alongside source text
- Translation persisted to `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/translations/{filename}_{target}.json`
- On reopen, coordinator verifies translation artifact exists
- Missing translation surfaces truthful degraded state with logging
- Tests prove full load → transcribe → translate → persist → reopen → reuse loop

## Evidence

### Commands Run
```bash
pip install torch transformers googletrans sentencepiece
dotnet build Babel-Player.sln
dotnet test BabelPlayer.Tests/BabelPlayer.Tests.csproj
```

### Test Results
```text
Total tests: 15
Passed: 15

New tests (Milestone 5):
- TranslateTranscript_ProducesTranslatedSegments
- TranslateTranscript_ThenReopen_ReusesTranslation
- ReopenWithMissingTranslation_SurfacesDegradedState

Existing tests still pass (12):
- MediaTransportTests (6)
- SessionWorkflowTests (9)
```

### Translation Output Example
```json
{
  "sourceLanguage": "es",
  "targetLanguage": "en",
  "segments": [
    { "start": 0.0, "end": 3.68, "text": "Hoy vamos a hacer un ejercicio...", "translatedText": "Today we are going to do an audio comprehension exercise" },
    ...
  ]
}
```

### Artifacts / Paths
- Translation location: `{LocalApplicationData}/BabelPlayer/sessions/{SessionId}/translations/{filename}_{target}.json`
- Uses `LocalApplicationData` (not roaming)

## Notes
- Uses `googletrans` for translation (cloud-based)
- Source language: Spanish (es) - from sample.mp4 content
- Target language: English (en) - fixed for this narrow slice
- No local model used due to memory constraints (MarianMT failed)
- Translation preserves source timing from transcript

## Conclusion
Milestone 05 complete. First translation slice works:
- load media
- transcribe (faster-whisper)
- translate (googletrans es→en)
- persist translated segments
- reopen and reuse translation
- missing translation surfaces as truthful degraded state

## What Remains for Future Work
- TTS/dubbing (Milestone 6)
- In-context preview
- Multiple language targets (not fixed to es→en)
- Local translation model (when memory available)
