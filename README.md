# Babel Deck

[![Sponsor](https://img.shields.io/github/sponsors/mta1124-1629472?label=Sponsor&logo=GitHub)](https://github.com/sponsors/mta1124-1629472)
[![CI](https://github.com/mta1124-1629472/Babel-Deck/actions/workflows/ci.yml/badge.svg)](https://github.com/mta1124-1629472/Babel-Player/actions/workflows/ci.yml)
[![GitHub Release](https://img.shields.io/github/v/release/mta1124-1629472/Babel-Deck)](https://github.com/mta1124-1629472/Babel-Player/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#run)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)

Babel Deck is an Avalonia/.NET desktop media player for language learning. It can load local media, generate captions, translate subtitles, and synthesize dubbed speech through cloud or local inference paths depending on what is installed on the machine.

The core product chain is:

`source media -> timed transcript -> translated/adapted dialogue -> spoken dubbed output -> in-context preview and refinement`


## Current posture

The project is in an early build phase.

The main goal right now is to prove the core workflow in the right order, with truthful states and tight scope control. The repo should prefer narrow working slices over broad partial systems.

## Repo guides

Start here before making changes:

- `PLAN.md` — milestone order, product sequencing, and gates
- `docs/architecture.md` — current structural map of the system and major boundaries
- `AGENTS.md` — rules for AI contributors and anti-drift guardrails
- `CONTRIBUTING.md` — contributor workflow, verification expectations, and scope discipline

## Working principle

A feature is not done because it compiles or because a UI surface exists.

A feature is done when the current milestone gate has been actually demonstrated with build, tests, and a short smoke result.

Python-backed inference is expected to stay deployable through multiple hosting modes over time, including native local execution, WSL, and more isolated/containerized paths, but those are not early prerequisites for proving the core workflow.

## Current priority

Protect the center of mass:

- transcription must produce trustworthy timed text
- translation/adaptation must produce speakable dialogue
- TTS must turn that dialogue into compelling spoken output
- playback and inspection exist to support preview and refinement

Anything that pulls the repo away from that chain needs a strong reason to exist.
