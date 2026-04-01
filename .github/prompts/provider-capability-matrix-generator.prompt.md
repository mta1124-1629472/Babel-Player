---
name: Provider Capability Matrix Generator
description: "Generate a normalized provider capability matrix for Babel Player stages across CPU, CUDA, DirectML, OpenVINO, and NPU paths."
argument-hint: "Provide stages, target hardware profiles, providers/runtimes, precision modes, and acceptance thresholds."
agent: "Hardware Acceleration Architect" # Defined in .github/agents/hardware-acceleration-architect.agent.md
---
Build a provider capability matrix artifact the team can use for planning and release gating.

## Paste this template
"Create a provider capability matrix for Babel Player.

Scope:
- Stages: <transcription/translation/tts/all>
- Environment set: <windows-nvidia, windows-directml, linux-intel, etc.>
- Hardware profiles: <cpu/gpu/npu models>
- Providers/runtimes: <cuda/directml/openvino/cpu/...>
- Precision targets: <fp32/fp16/int8/...>

Required output table columns:
1) Stage
2) Provider/runtime
3) Hardware target
4) Precision
5) Functional status (supported/partial/unsupported)
6) Performance status (meets target / below target)
7) Correctness status (pass/fail/tolerance)
8) Known blockers (ops, drivers, memory, dependencies)
9) Mitigation path
10) Recommended default route
11) Risk level
12) Validation evidence needed

Rules:
- Mark as `partial` whenever unsupported operators or fallback segments exist.
- Do not mark `supported` unless both correctness and performance checks are satisfied.
- Include explicit CPU fallback recommendation for every unsupported or partial row.
- Highlight NPU viability separately for low-power and small-batch scenarios.

Also return:
- a short rollout recommendation by environment
- top 3 highest-risk gaps
- next benchmark tasks to close those gaps"

## Fast variants
- "Generate a release-readiness capability matrix for Windows + NVIDIA + DirectML fallback."
- "Generate a cross-platform matrix for Linux Intel/OpenVINO and Windows CUDA/DirectML."
- "Generate a matrix focused on NPU viability and where CPU should remain default."
