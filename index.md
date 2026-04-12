---
layout: default
title: Babel Player — Local-First Dubbing Workstation
---

# Babel Player

[![Sponsor](https://img.shields.io/github/sponsors/mta-babel?label=Sponsor&logo=GitHub)](https://github.com/sponsors/mta-babel)
[![GitHub Release](https://img.shields.io/github/v/release/Babelworks/Babel-Player)](https://github.com/Babelworks/Babel-Player/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)
[![License](https://img.shields.io/github/license/Babelworks/Babel-Player)](https://github.com/Babelworks/Babel-Player/blob/main/LICENSE)

**Babel Player is a Windows desktop dubbing workstation.** Load source media, generate a timed transcript, translate the dialogue, produce a spoken dub, and preview the result in context — entirely on your own hardware.

```
source media → timed transcript → translated dialogue → spoken dubbed output → in-context preview
```

---

## What It Does

Babel Player is a dubbing workstation, not a subtitle editor or translation tool in isolation. The goal is to get a piece of foreign-language source media to a point where you can hear the translated dialogue spoken back, then refine it until it sounds right.

- **Transcription** — Powered by Faster-Whisper (local CPU/GPU) or cloud providers
- **Translation** — CTranslate2, NLLB-200, DeepL, OpenAI, or Google
- **Text-to-Speech** — Piper (local), Edge TTS, ElevenLabs, XTTS v2, and more
- **In-Context Preview** — Watch the original video synced with dubbed audio

## Local-First by Design

All processing runs on your device. No accounts, no cloud uploads, no telemetry beyond standard .NET crash reporting.  
See the [Privacy Policy]({{ site.baseurl }}/privacy/) for full details.

---

## Download

[**Download the latest release →**](https://github.com/Babelworks/Babel-Player/releases/latest)

## Requirements

- **OS:** Windows 10/11 x64
- **Release builds:** Self-contained — runtime included, no separate .NET install required
- **Source builds:** .NET 10 SDK

---

## Links

- [Source code on GitHub](https://github.com/Babelworks/Babel-Player)
- [Privacy Policy]({{ site.baseurl }}/privacy/)
- [Contributing guide](https://github.com/Babelworks/Babel-Player/blob/main/CONTRIBUTING.md)
- [Support development on Ko-fi](https://ko-fi.com/babel_player)
