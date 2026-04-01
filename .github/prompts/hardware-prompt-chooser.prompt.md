---
name: Hardware Prompt Chooser
description: "Choose the right hardware acceleration prompt for discovery, triage, compatibility, readiness, reporting, and release planning."
argument-hint: "Describe your scenario in one sentence and include environment basics (OS/hardware/runtime)."
agent: "Hardware Acceleration Architect"
---
Use this quick index to pick the best prompt for your task.

## Choose by goal
- **I need startup detection + route selection strategy**
  - Use: `hardware-discovery-routing-strategy.prompt.md`
- **I need pre-deployment readiness checks**
  - Use: `hardware-readiness-preflight.prompt.md`
- **Acceleration is failing or regressing in prod/CI**
  - Use: `hardware-incident-triage.prompt.md`
- **I suspect hidden fallback/operator mismatch**
  - Use: `operator-compatibility-audit.prompt.md`
- **I need machine-readable JSON diagnostics for CI/support**
  - Use: `driver-runtime-compatibility-report.prompt.md`
- **I need an environment/provider capability table for planning**
  - Use: `provider-capability-matrix-generator.prompt.md`
- **I need broad examples and starter prompts**
  - Use: `hardware-acceleration-examples.prompt.md`

## Recommended incident sequence
1) `driver-runtime-compatibility-report.prompt.md`
2) `hardware-incident-triage.prompt.md`
3) `operator-compatibility-audit.prompt.md`
4) `hardware-discovery-routing-strategy.prompt.md`
5) `provider-capability-matrix-generator.prompt.md`

## One-line starter
"Help me choose the right hardware prompt for <scenario>; environment is <OS>, <GPU/NPU>, <runtime/provider stack>."
