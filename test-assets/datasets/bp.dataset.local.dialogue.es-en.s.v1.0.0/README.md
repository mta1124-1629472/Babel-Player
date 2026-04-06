# Dataset: bp.dataset.local.dialogue.es-en.s.v1.0.0

Short Spanish-English bilingual dialogue clips used for local benchmark runs.

## Format

- **Audio**: 16 kHz, mono, PCM-16 WAV
- **Reference transcripts**: verbatim, in `manifest.json`
- **Language**: Spanish (`es`) source, English (`en`) reference translation

## Clips

| ID | Duration | Reference |
|----|----------|-----------|
| clip_001 | 5.2 s | "Hola, ¿cómo estás?" |
| clip_002 | 8.7 s | "El tiempo está muy bueno hoy." |
| clip_003 | 12.1 s | "Me gustaría una taza de café, por favor." |

## Status

> ⚠️ **Placeholder audio files** — the `.wav` files in this directory are silent stubs.
> Replace them with real recordings before running WER/CER quality benchmarks.
> Speed metrics (latency, RTF) can still be collected against any non-empty audio file.

## Replacing stubs

1. Record or source 16 kHz mono WAV clips for each `clip_00N.wav`.
2. Update `duration_seconds` in `manifest.json` to match the real clip length.
3. Verify `reference_transcript` accuracy against the actual recording.
4. Re-run benchmarks with `BenchmarkRunHarness` to populate `results[]`.
