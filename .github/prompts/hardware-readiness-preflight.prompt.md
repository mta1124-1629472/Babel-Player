---
name: Hardware Readiness Preflight
description: "Pre-deployment readiness checklist for hardware acceleration in Babel Player across CUDA, DirectML, OpenVINO, NPU, and CPU fallback paths."
argument-hint: "Provide OS, target hardware, runtime and provider stack, deployment mode, and rollout constraints."
agent: "Hardware Acceleration Architect"
---
Run this preflight before enabling or shipping any hardware-accelerated inference path.

## Paste this template
"Perform a hardware acceleration readiness preflight for Babel Player.

Environment:
- OS/version: <...>
- CPU: <...>
- GPU: <...>
- NPU: <... or none>
- RAM/VRAM: <...>
- .NET runtime/SDK: <...>
- Python/runtime stack: <...>
- Inference framework/provider: <onnx/cuda/directml/openvino/python subprocess/...>
- Deployment mode: <native/docker/service>

Goals & constraints:
- Primary objective: <latency/throughput/cost/power>
- SLO/SLA targets: <...>
- Security/compliance limits: <...>
- Licensing constraints: <...>

Required output:
1) readiness scorecard (green/yellow/red) by stage: transcription, translation, tts
2) blocker list with exact remediation steps
3) acceleration routing recommendation (CPU vs GPU vs NPU) with rationale
4) fallback design (degrade-to-cpu behavior + triggers)
5) validation plan (correctness + benchmark) with exact commands
6) rollout plan (canary, observability, rollback)
7) monitoring checks (driver/runtime drift, provider mismatch, fallback rate)
8) MCP/skills plan: which authoritative docs/workflows to consult and why"

## Acceptance checks to require
- Driver/runtime compatibility confirmed for selected provider path
- Device visibility checks pass on target host(s)
- Model/operator compatibility verified for precision/runtime
- Explicit CPU fallback path tested
- Baseline and accelerated performance measured with repeatable methodology
- Output correctness delta within acceptable tolerance
- Rollback procedure tested

## Fast variants
- "Preflight this for Windows + NVIDIA + CUDA with Docker deployment."
- "Preflight this for Intel iGPU/NPU + OpenVINO on Linux native deployment."
- "Preflight this for cross-vendor Windows desktop using DirectML + CPU fallback."
