---
name: Hardware Acceleration Examples
description: "Prompt pack for AI inference acceleration in Babel Player: CUDA, DirectML, OpenVINO, NPU offload, SIMD, and container deployment trade-offs."
argument-hint: "Pick one scenario and provide environment details (OS, hardware, runtime, framework, goals)."
agent: "Hardware Acceleration Architect"
---
Use these ready-to-run prompt templates. Replace placeholders.

> For go-live readiness and blocker discovery before deployment, use `hardware-readiness-preflight.prompt.md`.
> For hidden fallback and provider/runtime operator mismatch analysis, use `operator-compatibility-audit.prompt.md`.
> For startup hardware detection and stage-wise provider selection policy, use `hardware-discovery-routing-strategy.prompt.md`.
> For release planning artifacts across environments/providers, use `provider-capability-matrix-generator.prompt.md`.
> For CI/support machine-readable diagnostics output, use `driver-runtime-compatibility-report.prompt.md`.

## Environment capture + recommendation
"Assess my environment and recommend the best acceleration path for Babel Player.\nOS: <windows/linux + version>\nGPU: <vendor/model>\nNPU: <vendor/model or none>\nCPU: <model>\n.NET: <version>\nInference runtime: <onnx/tensorflow/pytorch/python subprocess>\nGoal: <latency/throughput target>\nConstraints: <licensing/security/ops>"

## NPU offload decision
"Determine whether NPU acceleration should be used for our pipeline stages (transcription, translation, TTS).\nProvide a stage-by-stage CPU vs GPU vs NPU recommendation, fallback plan, and risk notes."

## CUDA integration plan
"Create a low-risk migration plan to add CUDA acceleration for transcription in our existing C# + Python subprocess architecture. Include phased rollout, observability, rollback, and benchmark checkpoints."

## DirectML cross-vendor path
"Design a Windows-first DirectML inference path in C# that gracefully falls back to CPU when unsupported drivers/hardware are detected. Include runtime detection code and logging strategy."

## OpenVINO on Intel
"Provide exact steps to enable OpenVINO acceleration on Intel hardware for inference services, including package/runtime installation, model compatibility checks, and validation commands."

## CPU SIMD optimization
"Optimize this C# inference pre/post-processing path with .NET intrinsics. Show AVX2 fast-path + safe fallback, include microbenchmark setup, and explain when to avoid over-optimization."

## Containerized deployment decision
"Should we ship acceleration via native installer or self-hosted Docker image? Compare startup latency, driver friction, security, CI/CD, and support burden for enterprise desktops. Recommend a default."

## Monitoring + reliability
"Create a production monitoring checklist for hardware-accelerated inference: driver/runtime checks, GPU/NPU availability, fallback alerts, performance SLOs, and failure triage playbook."

## CI benchmark gate
"Define CI performance gates for inference acceleration changes. Include baseline strategy, noise control, pass/fail thresholds, and regression handling policy."
