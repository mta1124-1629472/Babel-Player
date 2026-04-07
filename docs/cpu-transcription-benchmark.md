# CPU Transcription Benchmark Runbook

## Purpose

This runbook defines a repeatable CPU-only transcription benchmark for Babel Player after the addition of CPU tuning controls (`compute type`, `threads`, `workers`).

Scope for this document:

- stage: `transcription`
- providers: `faster-whisper`, `containerized-service`
- goal: compare CPU tuning variants without changing transcript correctness expectations
- status: backed by `scripts/run_cpu_transcription_benchmark.py`

## Executable Runner

Primary runner:

- `scripts/run_cpu_transcription_benchmark.py`

Dry-run example:

- `python scripts/run_cpu_transcription_benchmark.py --source test-assets/video/sample.mp4 --provider faster-whisper --model base --dataset-source local --dataset-content dialogue --source-lang es --target-lang en --duration-bucket s --variant cpu-auto --dry-run`

Measured-run example:

- `python scripts/run_cpu_transcription_benchmark.py --source test-assets/video/sample.mp4 --provider faster-whisper --model base --dataset-source local --dataset-content dialogue --source-lang es --target-lang en --duration-bucket s --variant cpu-auto --variant cpu-8t --cache-mode cold --cache-mode warm`

Current runner behavior:

- generates naming-compliant dataset, matrix, and run batch IDs
- captures an environment snapshot automatically
- writes structured JSON output under `docs/benchmarks/`
- supports executable local CPU benchmarking for `faster-whisper`
- supports executable warm-path benchmarking for `containerized-service`
- rejects containerized cold-cache execution until service restart automation exists

## Environment Snapshot

- MachineId: `win11-ryzen5700x3d-rtx5070`
- OS name and version: `Microsoft Windows 11 Pro 10.0.26200`
- CPU model: `AMD Ryzen 7 5700X3D 8-Core Processor`
- Physical cores / logical threads: `8 / 16`
- RAM total: `31.9 GB`
- GPU model / VRAM: `NVIDIA GeForce RTX 5070 / 4.0 GB (WMI-reported)`
- Disk path used for benchmark artifacts: `D:\Dev\Babel-Player` on `SSD Pool`
- .NET SDK/runtime version: `10.0.201`
- Python version: `3.10.11`
- Provider runtime versions:
  - `faster-whisper 1.2.1`
  - `googletrans 4.0.2`
  - `edge-tts 7.2.8`
  - `torch 2.11.0+cu128`

Hardware profile token for this machine:

- `int8_8c16t_32g`

Hardware Bottleneck Risk: `medium`

Reason: this benchmark intentionally forces CPU-oriented transcription tuning, so throughput is expected to be CPU-bound while the discrete GPU contributes little or nothing on the measured path.

## Dataset IDs

Primary smoke dataset:

- Dataset ID: `bp.dataset.local.dialogue.es-en.s.v1.0.0`
- Source file: `test-assets/video/sample.mp4`
- Intended use: quick correctness + latency smoke benchmark after code changes

Recommended follow-up dataset for more stable CPU comparisons:

- Dataset ID: `bp.dataset.local.dialogue.es-en.m.v1.0.0`
- Source file: user-supplied local dialogue clip of 1–5 minutes
- Intended use: meaningful CPU tuning comparison once smoke run passes

## Matrix IDs

Local provider baselines:

- `bp.matrix.transcription.faster-whisper.tiny.int8_8c16t_32g.v1.0.0`
- `bp.matrix.transcription.faster-whisper.base.int8_8c16t_32g.v1.0.0`
- `bp.matrix.transcription.faster-whisper.small.int8_8c16t_32g.v1.0.0`

Containerized provider baselines:

- `bp.matrix.transcription.containerized-service.tiny.int8_8c16t_32g.v1.0.0`
- `bp.matrix.transcription.containerized-service.base.int8_8c16t_32g.v1.0.0`
- `bp.matrix.transcription.containerized-service.small.int8_8c16t_32g.v1.0.0`

Note: matrix IDs do not encode `threads` or `workers`. Record those values in the run metadata for every batch.

## Variant Matrix

Use these tuning variants under each matrix ID:

| Variant Label | Compute Type | Threads | Workers | Notes |
|---|---|---:|---:|---|
| `cpu-auto` | `int8` | `0` | `1` | Current conservative baseline |
| `cpu-4t` | `int8` | `4` | `1` | Lower-thread stability comparison |
| `cpu-8t` | `int8` | `8` | `1` | Matches physical core count |
| `cpu-16t` | `int8` | `16` | `1` | Matches logical thread count |
| `cpu-8t-2w` | `int8` | `8` | `2` | Oversubscription check |

Optional exploratory variants after baseline only:

| Variant Label | Compute Type | Threads | Workers | Notes |
|---|---|---:|---:|---|
| `cpu-auto-int8f16` | `int8_float16` | `0` | `1` | Use only if runtime/backend supports it cleanly |
| `cpu-auto-fp32` | `float32` | `0` | `1` | Accuracy/reference check, not expected winner |

## Measurement Rules

- Warm-up runs: exactly `1` per variant, excluded from aggregate results
- Measured runs per variant: `5`
- Cache modes required:
  - `cold`
  - `warm`
- Cold and warm results must be reported separately
- Do not compare results across different hardware profiles as a single conclusion

## Benchmark Procedure

### 1. Preflight

- Build the app successfully
- Confirm the left-panel `ACTIVE CONFIG` block reflects the intended provider/model settings
- Confirm logs include the transcription route summary (`provider`, `model`, `cpu_compute`, `cpu_threads`, `cpu_workers`, `avx2`, `avx512`, `cuda`)

### 2. Configure the run

For each variant:

- Open Settings → General → Advanced: CPU Transcription
- Set `CPU Compute Type`
- Set `CPU Threads`
- Set `Workers`
- Save settings
- Select the intended transcription provider and model in the pipeline panel

### 3. Warm-up run

- Run the pipeline once for transcription only
- Exclude this run from aggregate numbers
- Confirm no correctness regressions (run completes, transcript file created, language reported sensibly)

### 4. Cold-cache measured runs

For each measured cold run:

- Restart the app before the run
- Re-open the same media
- Verify active config values again
- Start timing at the moment `Run Pipeline` is triggered
- Stop timing when the status reaches the completed transcription state or when the coordinator logs transcription completion
- Record elapsed wall time

### 5. Warm-cache measured runs

For each measured warm run:

- Keep the app process alive
- Re-run with the same provider/model/CPU tuning settings
- Record elapsed wall time in the same way

### 6. Evidence capture

Use these artifacts for every run:

- app status text in the UI
- `%LOCALAPPDATA%\BabelPlayer\logs\babel-player.log`
- transcript artifact path under the session directory

At minimum, capture:

- provider
- model
- compute type
- threads
- workers
- cold/warm mode
- elapsed wall time
- segment count
- reported source language

## Run Batch ID Format

Use:

- `bp.run.<yyyyMMddTHHmmssZ>.<dataset_id>.<matrix_id>.r<run_count>`

Example:

- `bp.run.20260402T210000Z.bp.dataset.local.dialogue.es-en.s.v1.0.0.bp.matrix.transcription.faster-whisper.base.int8_8c16t_32g.v1.0.0.r5`

## Results Table Template

| Run Batch ID | Variant | Cache Mode | p50 (s) | p95 (s) | Mean (s) | Segment Count | Language | Notes |
|---|---|---|---:|---:|---:|---:|---|---|
| `...` | `cpu-auto` | `cold` |  |  |  |  |  |  |
| `...` | `cpu-auto` | `warm` |  |  |  |  |  |  |

## Acceptance Gates

Baseline acceptance:

- build passes
- relevant tests pass
- 1 warm-up + 5 measured runs captured for each tested variant
- no transcription failures
- no hidden fallback ambiguity in logs

Performance acceptance:

- no variant is considered a winner unless it improves measured wall time in both cold and warm modes, or improves one mode without regressing the other by more than `5%`
- if accuracy/correctness differs, performance result is invalid regardless of latency improvement

Correctness guardrails:

- transcript file is produced successfully
- segment count stays in the expected band for the same dataset
- detected language remains stable unless the source materially changes

## Interpretation Notes

- Prefer `threads = physical cores` before trying logical-thread saturation
- Treat `workers > 1` as exploratory until proven beneficial on this exact workload
- If `int8_float16` or `float32` underperform or increase instability, keep `int8` as the production default
- If the containerized path differs materially from local provider results, report it as an execution-path difference rather than a pure CPU-tuning conclusion

## Follow-up Automation Backlog

- Capture per-stage timestamps automatically from coordinator logs
- Emit structured benchmark JSON for CI or support review
- Add a benchmark summary doc or artifact folder for archived runs