---
name: Hardware Discovery & Inference Routing Strategy
description: "Define how the app should detect available hardware/drivers at runtime and choose the safest, fastest inference path with explicit fallback behavior."
argument-hint: "Provide OS targets, supported providers, model constraints, startup and runtime requirements, and SLO goals."
agent: "Hardware Acceleration Architect"
---
Use this prompt to design runtime hardware discovery and provider-routing logic for the application.

## Paste this template
"Design a hardware discovery and inference routing strategy for Babel Player.

Targets:
- OS/platforms: <windows/linux/macos>
- App architecture: <in-process/subprocess/service/container>
- Stages to route: <transcription/translation/tts>
- Candidate providers: <cpu/cuda/directml/openvino/npu>
- SLOs: <latency/throughput/reliability>

Runtime detection requirements:
- Detect CPU features: <avx2/avx512/sse/neon>
- Detect GPU(s): model, VRAM, driver version, runtime compatibility
- Detect NPU(s): model capabilities, runtime and provider support
- Detect provider runtime availability (DLLs/shared libs, package/runtime versions)
- Detect container passthrough/device visibility if containerized

Required output:
1) startup probe sequence (ordered)
2) capability scoring model (weights for performance, compatibility, reliability)
3) routing policy per stage (CPU/GPU/NPU default + fallback ladder)
4) pseudocode or C# sketch for provider selection
5) user-visible diagnostics messages and telemetry fields
6) kill-switches and feature flags for safe rollback
7) validation plan with concrete benchmark and correctness checks

Constraints:
- No silent fallback: always log reason and selected route.
- Favor production safety: choose stable path when confidence is low.
- Include explicit behavior for missing/outdated drivers.
- Include behavior for partial compatibility (some ops unsupported).

## Fast variants
- "Design startup probes and routing for Windows desktop with NVIDIA GPU and optional NPU."
- "Design routing policy for containerized Linux service with Intel OpenVINO and CPU fallback."
- "Design stage-by-stage routing where TTS can use CPU while transcription uses GPU/NPU."
