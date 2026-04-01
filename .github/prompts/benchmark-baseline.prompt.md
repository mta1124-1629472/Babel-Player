---
name: Benchmark Baseline Matrix
description: "Create a Babel Player benchmark baseline plan with enforced dataset and matrix naming conventions for Transcription, Translation, and TTS."
argument-hint: "Provide stage(s), dataset details, variable matrix, run count, and constraints."
agent: "Babel Player Benchmark Runner"
---
Build a benchmark baseline plan for Babel Player and enforce the naming conventions below.

## Required Naming Conventions
Use these exact IDs and formats.

1. Dataset ID
- Format: `bp.dataset.<source>.<content>.<langpair>.<duration_bucket>.v<semver>`
- Allowed values:
  - source: `local` or `public`
  - content: `dialogue`, `mixed`, or `narration`
  - langpair: `<src>-<tgt>` lower-case ISO-like codes, example `es-en`
  - duration_bucket: `s`, `m`, `l`, or `xl`
- Example: `bp.dataset.local.dialogue.es-en.m.v1.0.0`

2. Matrix ID
- Format: `bp.matrix.<stage>.<provider>.<model>.<hw_profile>.v<semver>`
- stage must be exactly one of:
  - `transcription`
  - `translation`
  - `tts`
- `hw_profile` format: `<precision>_<cores>c<threads>t_<ram_gb>g`
  - Allowed `<precision>` values: `fp32`, `fp16`, `fp8`, `fp4`, `int8`
- Example: `bp.matrix.transcription.fasterwhisper.base.fp16_8c16t_32g.v1.0.0`

3. Run Batch ID
- Format: `bp.run.<yyyyMMdd-HHmmss>.<dataset_id>.<matrix_id>.r<run_count>`

## Baseline Rules
- Minimum default run count: 5 per matrix variant unless user overrides.
- Warm-up: exactly 1 warm-up run, excluded from aggregate stats.
- All benchmark plans must include cold and warm cache modes unless explicitly excluded.
- Reject plans that do not use the exact naming formats above.
- Hard-fail on unknown tokens outside allowed value sets; do not proceed with partial normalization.

## Output Format
Return exactly these sections:
- Section A: Normalized Inputs
- Section B: Dataset IDs
- Section C: Matrix IDs
- Section D: Run Batch IDs
- Section E: Benchmark Command Plan
- Section F: Validation Errors (if any)

Validation behavior:
- If any input cannot be normalized into the required naming format, stop and list all errors in Section F.
- Do not continue with execution steps until validation errors are resolved.
