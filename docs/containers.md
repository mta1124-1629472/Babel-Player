# Containers, WSL, and GPU Hosting

This document explains the current inference-host posture.

## Current Position

- The primary public runtime model is the desktop app plus a managed local GPU host.
- Public compute profiles are `CPU`, `GPU`, and `Cloud`.
- Docker is still supported, but as an advanced optional GPU backend.
- WSL is a viable hosting option for experimentation and future work, not the default product foundation.

## What Is True in the Current Repo

- The managed local GPU host is the default GPU backend.
- The managed host serves the same HTTP contract the Docker backend uses.
- GPU-stage support currently includes transcription, translation, Qwen3-TTS, and NeMo diarization.
- CPU-stage local support remains available separately for Faster Whisper, CTranslate2, Piper, and WeSpeaker.
- The desktop app does not containerize the shell itself.

## Why the Boundary Matters

The app should keep a clear boundary between the desktop shell and Python-backed inference so the host can change without rewriting the workflow.

That means:

- explicit HTTP or process contracts
- runtime assets separated from normal app source
- no hidden assumption that Docker is always present
- no hidden assumption that Windows-native local execution is the only path forever

## Docker Posture

Docker is appropriate when you want:

- a reproducible alternate GPU environment
- stronger dependency isolation
- a loopback-hosted local service with the same contract as the managed host

Docker is not the default user story.

Current behavior:

- the desktop app can target a Docker-hosted GPU service URL
- local autostart is only meaningful for loopback Docker-host scenarios
- readiness is based on service health and capabilities, not just a configured URL

## WSL Posture

WSL remains useful when Linux-first tooling or GPU validation is easier there.

It should stay behind the same inference boundary as every other host. Do not bake WSL-specific assumptions into the desktop workflow unless the code and product direction explicitly require it.

## NVIDIA-Managed or Other External Serving

Treat vendor-managed serving as optional future deployment work, not as the default architecture center of gravity.

The product should stay honest:

- local managed GPU host first
- Docker optional
- external serving only when it reduces real operational pain

## Contributor Guidance

When touching runtime code:

- preserve the explicit host boundary
- avoid hard-coding one backend as the only valid future
- keep the public UX framed around `CPU`, `GPU`, and `Cloud`
- document backend-specific assumptions where they are introduced
