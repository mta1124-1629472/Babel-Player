---
name: Hardware Acceleration Rollout Plan
description: "Create a comprehensive staged plan to add CUDA, OpenVINO, DirectML, AVX2, and AVX-512 acceleration to inference pipelines where each path fits best."
argument-hint: "Provide OS targets, hardware inventory, model/runtime stack, pipeline stages, SLOs, and deployment constraints."
agent: "Hardware Acceleration Architect"
---
Create a comprehensive, production-safe rollout plan for adding hardware acceleration to inference pipelines.

## Paste this template
"Build a staged hardware acceleration plan for our inference pipelines.

Environment:
- OS/platforms: <windows/linux/macos>
- Deployment model: <in-process/subprocess/service/container>
- Hardware inventory: <cpu models, gpu models, npu models>
- Driver/runtime versions (if known): <...>
- .NET/runtime stack: <...>
- Inference frameworks/providers: <onnx/pytorch/tensorflow/python subprocess/...>

Pipeline scope:
- Stages: <transcription/translation/tts/other>
- Current path per stage: <cpu/gpu/provider>
- Models per stage: <...>
- Batch/latency profile per stage: <...>

Targets and constraints:
- SLOs: <p95 latency/throughput/reliability>
- Quality constraints: <accuracy/tolerance thresholds>
- Security/compliance constraints: <...>
- Licensing constraints: <...>
- Timeline/resource limits: <...>

Acceleration candidates to evaluate:
- CUDA (NVIDIA)
- OpenVINO (Intel)
- DirectML (Windows cross-vendor)
- AVX2 (CPU SIMD)
- AVX-512 (CPU SIMD)

Required output sections:
1) Executive recommendation (default path by environment)
2) Stage-by-stage routing matrix (CPU/GPU/NPU/provider + fallback ladder)
3) Capability & compatibility findings (drivers, runtimes, operators, precision)
4) Phased implementation plan (milestones, effort, dependencies, risks)
5) Benchmark and correctness validation plan (baseline + pass/fail gates)
6) Observability plan (telemetry, alerts, fallback-rate monitoring)
7) Rollout plan (canary, kill-switches, rollback criteria)
8) CI/CD integration plan (perf gates + compatibility checks)
9) Security/licensing notes and decision log
10) Open assumptions + unknowns that need verification

Plan quality rules:
- Choose acceleration per stage based on measured fit, not blanket preference.
- Explicitly state when AVX2/AVX-512 is preferable to accelerator offload.
- Include conservative defaults when evidence is incomplete.
- Never rely on silent fallback; require explicit route logging.
- Prioritize non-breaking adoption with CPU fallback always available.
- Distinguish fast mitigation actions vs durable architectural actions.

Output style:
- Use concise tables for routing and risk.
- Use numbered milestones with estimated effort (`S/M/L`) and risk (`low/medium/high`).
- End with a "First 2-week execution slice" containing concrete next actions."

## Fast variants
- "Plan CUDA-first acceleration on Windows with DirectML + AVX2 fallback."
- "Plan OpenVINO + AVX2/AVX-512 on Intel Linux hosts with conservative rollout."
- "Plan cross-platform strategy: CUDA on NVIDIA, DirectML on Windows non-NVIDIA, CPU SIMD fallback."
