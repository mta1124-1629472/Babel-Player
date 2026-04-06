# Benchmark Architecture

This document describes the measurement infrastructure added in the
`feat/benchmark-instrumentation` PR and the correct order of assembly for
reaching statistically valid benchmark scores.

---

## Component Map

```
BenchmarkRunHarness          ← orchestrates N warmup + M measured runs
    │
    ├─► provider.TranscribeAsync / TranslateAsync / TtsAsync
    │       │
    │       └─► PythonSubprocessServiceBase.RunPythonScriptAsync
    │                │
    │                └─► ScriptResult.ElapsedMs  ← Stopwatch around WaitForExitAsync
    │
    ├─► WerComputer.ComputeWer / ComputeCer      ← pure C# edit-distance, no jiwer
    │
    └─► BenchmarkResultWriter.WriteAsync         ← emits canonical JSON result file
```

---

## Files Added / Modified

| File | Change | What it enables |
|------|--------|-----------------|
| `Services/PythonSubprocessServiceBase.cs` | `ScriptResult` gains `ElapsedMs`; `Stopwatch` wraps `WaitForExitAsync` | Wall-clock timing for every subprocess call |
| `Services/BenchmarkResultWriter.cs` | New service | Writing valid `results[]` JSON files |
| `Services/BenchmarkRunHarness.cs` | New service | Warmup + measured N-run loop driver |
| `Services/WerComputer.cs` | New utility | WER / CER without external Python deps |
| `test-assets/datasets/.../manifest.json` | New dataset stub | Ground-truth dataset scaffold |
| `docs/benchmark-architecture.md` | This file | Architecture reference |

---

## What Is Still Missing (Follow-up Work)

### VRAM / RAM Sampling

The benchmark schema expects `peak_vram_mb` and `peak_ram_mb`. These fields
are plumbed through `BenchmarkRunEntry` and `BenchmarkResultEntry` already,
but the values are always `-1` until the Python scripts themselves sample
memory via `pynvml` or `nvidia-smi` before and after inference and return
the readings in their stdout JSON.

**Next step**: add a `_sample_vram()` helper inside the faster-whisper Python
script and include `peak_vram_mb` / `peak_ram_mb` in the JSON output blob that
C# reads back from `ScriptResult.Stdout`.

### Real Audio Clips

`test-assets/datasets/bp.dataset.local.dialogue.es-en.s.v1.0.0/` contains
placeholder stub WAV files. Quality scores (WER, CER) remain `-1` until
real recordings are substituted.

### Provider Integration

`FasterWhisperTranscriptionProvider.TranscribeAsync` now returns `ElapsedMs`
via `ScriptResult` but does not yet call `BenchmarkRunHarness` or
`BenchmarkResultWriter` — that wiring belongs in a dedicated benchmark
orchestrator or test fixture that composes the three new services together.

---

## Milestone 12 Prerequisite Status

| Prerequisite | Status |
|---|---|
| Wall-clock timing in `RunPythonScriptAsync` | ✅ Done |
| `BenchmarkResultWriter` service | ✅ Done |
| N-run warmup / measured loop driver | ✅ Done |
| WER / CER computation | ✅ Done (pure C#) |
| Ground-truth dataset scaffold | ✅ Stub committed |
| VRAM / RAM sampling | ⬜ Follow-up (Python side) |
| Real audio clips | ⬜ Follow-up (replace stubs) |
| Provider ↔ harness wiring | ⬜ Follow-up |
