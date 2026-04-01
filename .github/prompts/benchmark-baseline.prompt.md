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

## Token Validation Reference

Use this table as the single lookup for validating every token before constructing any ID.
Hard-fail on the first violation; emit the listed error message and stop.

| Token | Kind | Allowed values / pattern | Error message |
|---|---|---|---|
| `source` | enum | `local`, `public` | `Invalid source: must be 'local' or 'public'` |
| `content` | enum | `dialogue`, `mixed`, `narration` | `Invalid content: must be 'dialogue', 'mixed', or 'narration'` |
| `langpair` | pattern | Exactly two ISO 639 codes (2–3 lowercase alpha chars each) separated by `-`, e.g. `es-en`, `zho-en` | `Invalid langpair: must be two lowercase ISO 639 codes (2–3 alpha chars) separated by '-', e.g. 'es-en'` |
| `duration_bucket` | enum | `s`, `m`, `l`, `xl` | `Invalid duration_bucket: must be 's', 'm', 'l', or 'xl'` |
| `stage` | enum | `transcription`, `translation`, `tts` | `Invalid stage: must be 'transcription', 'translation', or 'tts'` |
| `provider` | pattern | Lowercase alphanumeric, hyphens between words, no leading/trailing hyphens; e.g. `fasterwhisper`, `edge-tts` | `Invalid provider: must match pattern '^[a-z0-9]+(-[a-z0-9]+)*$'` |
| `model` | pattern | Lowercase alphanumeric, hyphens between words, no leading/trailing hyphens; e.g. `base`, `large-v3` | `Invalid model: must match pattern '^[a-z0-9]+(-[a-z0-9]+)*$'` |
| `hw_profile` | pattern | `<precision>_<N>c<N>t_<N>g`, e.g. `fp16_8c16t_32g` | `Invalid hw_profile: must match pattern '<precision>_<cores>c<threads>t_<ram_gb>g'` |
| `semver` | pattern | `v<major>.<minor>.<patch>`, e.g. `v1.0.0` | `Invalid version: must follow semver format 'v<major>.<minor>.<patch>'` |
| `yyyyMMdd-HHmmss` | pattern | UTC timestamp, e.g. `20240401-143022` | `Invalid timestamp: must be UTC formatted as 'yyyyMMdd-HHmmss'` |
| `run_count` | pattern | Positive integer | `Invalid run_count: must be a positive integer` |

For **enum** tokens: reject any value not in the listed set.
For **pattern** tokens: reject any value that does not match the described format.

## Baseline Rules
- Minimum default run count: 5 per matrix variant unless user overrides.
- Warm-up: exactly 1 warm-up run (excluded from aggregate stats); `r<run_count>` refers to measured runs only.
- All benchmark plans must include cold and warm cache modes unless explicitly excluded.
- Reject plans that do not use the exact naming formats above.
- Hard-fail on unknown or malformed tokens using the Token Validation Reference above; do not proceed with partial normalization.

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
