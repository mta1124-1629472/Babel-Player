# Containers, WSL, and NVIDIA-Managed Serving Posture

## Purpose

This document explains how the app should think about Python inference hosting, WSL, containers, and NVIDIA-managed serving paths.

It exists to prevent two opposite mistakes:

- treating deployment isolation as mandatory foundation work before the product loop is proven
- hard-coding the app into one fragile local environment with no path to cleaner hosting later

This is a deployment-posture document, not a mandate to build container infrastructure early.

---

## Current Position

The app should remain compatible with Python-backed inference that can be hosted in more than one way over time.

Expected hosting modes include:

- native local execution
- managed local GPU host execution
- Docker-hosted local GPU execution
- WSL-hosted execution
- other NVIDIA-managed serving paths where they actually fit the model stack

The desktop app should not assume one of these modes too early.

The first requirement is still a real working product slice.
That means proving a narrow transcription, translation/adaptation, and TTS workflow before investing heavily in deployment complexity.

---

## Why This Matters

The Python and AI ecosystem often wants different dependency combinations across transcription, translation, and TTS workloads.

Over time, the project may need:

- reproducible Python environments
- clearer GPU/runtime assumptions
- isolation between incompatible model stacks
- a cleaner path for local/offline support
- a way to support stronger deployment consistency across machines

Containers and WSL can help with those problems.

They do not, by themselves, prove the product.

---

## Architecture Consequence

The app should preserve a clear boundary between the desktop shell and Python-backed inference.

That means:

- explicit request/response contracts across the app and inference boundary
- Python inference hosted as a separate process or service rather than hidden inside the shell for convenience
- model downloads and runtime assets kept separate from normal application source code
- runtime assumptions documented as they are discovered

This boundary is what makes native, WSL, containerized, or NVIDIA-managed hosting possible later without rewriting the core workflow.

---

## WSL Posture

WSL is a valid hosting option, especially when Linux-first inference tooling is more stable or easier to reproduce there than on native Windows.

But WSL should be treated as a hosting mode, not as the product foundation.

Risks to keep in mind:

- Windows/WSL file-path friction
- temp artifact movement across boundaries
- media extraction and preview asset handling
- service orchestration complexity
- GPU/runtime debugging spread across more layers

If WSL is used, it should remain behind the same explicit service boundary as any other inference host.

Current status: WSL GPU-backed PyTorch smoke testing has been verified on the development machine, so WSL remains a proven optional hosting path for future Python-backed inference work.


---

## Container Posture

Containers are useful when the project starts needing stronger reproducibility, cleaner dependency isolation, or support for multiple Python-backed stacks that should not share one environment.

Containers are not the first thing to optimize.

They become important when:

- the first real local workflow already exists
- environment drift starts wasting time
- multiple inference stacks want incompatible dependencies
- collaboration or deployment repeatability becomes a real problem

Until then, the codebase should stay container-compatible without requiring containers.

Current supported implementation:

- the public app UI now exposes `CPU / GPU / Cloud`, not `Local / Containerized / Cloud`
- the default public `GPU` path is the managed local GPU host
- Docker is an advanced GPU backend, not the primary public-facing runtime choice
- the Docker path still uses an external/local HTTP inference host consumed over the same health/capabilities contract
- for loopback service URLs such as `http://localhost:8000`, the app can optionally run `docker compose up -d inference`
- local Docker autostart happens only when the advanced GPU backend is set to Docker and either a GPU stage is selected or `Always start local GPU host at app start` is enabled
- the desktop app does not try to launch remote hosts, WSL, or non-loopback endpoints for you
- `INFERENCE_SERVICE_URL` overrides the saved Docker-host service URL at startup when present
- readiness is based on `GET /health/live` plus `GET /capabilities`, not URL presence alone
- the bundled `docker-compose.yml` is a same-machine helper for the advanced Docker backend; it is not a remote deployment or orchestration story for the desktop app

## Managed Local GPU Posture

The managed local GPU host is now the default low-friction GPU path.

That means:

- `GPU` is a compute-profile choice, not a promise that Docker is involved
- the app is responsible for bootstrapping and starting the managed host
- the host still speaks the same explicit HTTP contract as the Docker backend
- host launch-time compute policy is chosen on the C# side and passed to Python as `--compute-type`

Phase-1 managed host scope is intentionally narrow:

- GPU transcription
- GPU translation
- no public GPU TTS yet
- no GPU diarization yet

---

## NVIDIA-Managed Serving Posture

NVIDIA-managed serving paths can be useful where the relevant model family actually fits that ecosystem well.

They should be treated as optional deployment modes, not as default assumptions for the project.

This is especially important because voice-cloning and dubbing-related TTS stacks can move quickly and may not align cleanly with a standardized NVIDIA-managed path.

That means:

- do not design the product around NVIDIA-managed serving first
- evaluate those paths after the first real local workflow exists
- use them where they reduce real operational pain, not where they merely sound cleaner in advance

---

## What Contributors Should Do Now

For current milestones:

- keep Python inference behind an explicit boundary
- pin dependencies in real environment files
- document runtime assumptions when they are introduced or changed
- avoid baking WSL-only assumptions into the desktop app unless the milestone explicitly needs them
- avoid mixing unrelated model stacks into one giant environment without a strong reason

This keeps the repo compatible with native, WSL, and containerized hosting later without forcing premature infra work now.

---

## What This Document Does Not Mean

This document does not mean:

- containers should be built now
- WSL is mandatory
- NVIDIA-managed serving is the default inference path
- deployment concerns should outrank milestone progress

The project should still prefer narrow working slices over deployment sophistication.

---

## Trigger Points For Revisit

This posture should be revisited when one or more of these become true:

- the first real local inference workflow is proven
- environment drift is causing repeated breakage
- multiple Python stacks need meaningful isolation
- reproducible collaborator setup becomes important
- GPU/runtime verification needs stronger consistency across machines

At that point, containerization or NVIDIA-managed serving can move from optional future-compatible path to active implementation work.
