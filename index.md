---
layout: default
title: Babel Player
description: Local-first AI dubbing for Windows — transcribe, translate, assign voices, and preview dubs on your own hardware. Optional cloud APIs when you bring your own keys.
body_class: bp-home
---

<div class="lead-block" markdown="0">
<p class="lead">Ship a full dubbing loop without uploading your masters: load media, run timed ASR, translate, synthesize speech per segment, and preview in the player — with CPU, GPU, or explicit cloud routes you control.</p>
</div>

<p class="badge-row">
  <a href="https://github.com/sponsors/mta-babel"><img src="https://img.shields.io/github/sponsors/mta-babel?label=Sponsor&logo=GitHub" alt="Sponsor on GitHub"></a>
  <a href="https://github.com/Babelworks/Babel-Player/releases/latest"><img src="https://img.shields.io/github/v/release/Babelworks/Babel-Player" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/platform-Windows%20x64%20%7C%20ARM64-blue" alt="Windows x64 and ARM64">
  <a href="https://github.com/Babelworks/Babel-Player/blob/main/LICENSE"><img src="https://img.shields.io/github/license/Babelworks/Babel-Player" alt="License"></a>
</p>

<figure class="hero-shot">
  <a href="https://github.com/Babelworks/Babel-Player/releases/latest">
    <img
      src="https://raw.githubusercontent.com/Babelworks/Babel-Player/main/Assets/preview.png"
      alt="Babel Player main window showing the dubbing pipeline and preview"
      width="1152"
      height="648"
      loading="eager"
      decoding="async">
  </a>
  <figcaption>Screenshot from the latest public build — <a href="https://github.com/Babelworks/Babel-Player/releases/latest">grab the installer or portable zip</a>.</figcaption>
</figure>

## The workflow

Babel Player is a **dubbing workstation**, not a one-off subtitle utility. Everything is **segment-based**: each line of dialogue can be translated, re-voiced, or regenerated on its own without redoing the whole file.

<ol class="pipeline-steps">
  <li><strong>Open</strong> local video or audio</li>
  <li><strong>Transcribe</strong> with Faster-Whisper (CPU/GPU) or cloud STT</li>
  <li><strong>Diarize &amp; assign</strong> speakers and voices</li>
  <li><strong>Translate</strong> with local NLLB / CTranslate2 or cloud providers</li>
  <li><strong>Dub</strong> per segment with Piper, Edge, XTTS, Qwen3-TTS, ElevenLabs, and more</li>
  <li><strong>Preview</strong> in embedded playback (libmpv) — swap source vs. dubbed audio</li>
  <li><strong>Refine</strong> any line, then <strong>export</strong> captions to <code>.srt</code></li>
</ol>

## Interface gallery

<figure class="hero-shot">
  <img
    src="https://raw.githubusercontent.com/Babelworks/Babel-Player/main/Assets/Pipeline%201.png"
    alt="Pipeline view: source media through transcription, vocal separation, and diarization"
    width="1152"
    loading="lazy"
    decoding="async">
  <figcaption>Transcription through diarization — each stage shows explicit CPU / GPU / Cloud routing and readiness.</figcaption>
</figure>

<figure class="hero-shot">
  <img
    src="https://raw.githubusercontent.com/Babelworks/Babel-Player/main/Assets/Pipeline%202.png"
    alt="Pipeline view: translation, text-to-speech, and export"
    width="1152"
    loading="lazy"
    decoding="async">
  <figcaption>Translation, per-segment speech synthesis, and caption export — downstream stages stay gated until upstream artifacts exist.</figcaption>
</figure>

<figure class="hero-shot">
  <img
    src="https://raw.githubusercontent.com/Babelworks/Babel-Player/main/Assets/wizard.png"
    alt="Speaker reference wizard for multi-speaker routing and voice cloning"
    width="1152"
    loading="lazy"
    decoding="async">
  <figcaption>Speaker reference wizard — capture reference audio so diarization, voice assignment, and cloning stay consistent per speaker.</figcaption>
</figure>

Sessions **auto-save** under `%LOCALAPPDATA%\BabelPlayer\` so you can close the app and pick up later.

## Why teams and hobbyists use it

<div class="feature-grid" markdown="0">
  <article class="feature-card">
    <h3 class="feature-card__title">Honest compute routing</h3>
    <p>Every stage exposes <strong>CPU</strong>, <strong>GPU</strong>, or <strong>Cloud</strong>. If a path is not available, the UI blocks with a clear fix — no silent fallback to the wrong device.</p>
  </article>
  <article class="feature-card">
    <h3 class="feature-card__title">Managed Python stack</h3>
    <p>Bundled <code>uv.exe</code> bootstraps local inference hosts automatically. You do not need a separate Python install for GPU or CPU pipelines.</p>
  </article>
  <article class="feature-card">
    <h3 class="feature-card__title">Polished preview</h3>
    <p>GPU-accelerated playback, bilingual subtitle overlay, transport controls, and optional <strong>RTX Video</strong> features on supported NVIDIA hardware when gpu-next is enabled.</p>
  </article>
  <article class="feature-card">
    <h3 class="feature-card__title">Multi-speaker aware</h3>
    <p>NeMo or WeSpeaker diarization, per-speaker voices, cloning-friendly providers (e.g. XTTS v2, Qwen3-TTS), and sensible fallbacks for unassigned speakers.</p>
  </article>
  <article class="feature-card">
    <h3 class="feature-card__title">BYOK cloud</h3>
    <p>Optional OpenAI, Google, DeepL, ElevenLabs, and others only run with <strong>your</strong> API keys — stored locally with Windows DPAPI. Traffic goes straight to the provider.</p>
  </article>
  <article class="feature-card">
    <h3 class="feature-card__title">Curated language batch</h3>
    <p>The embedded local dub path targets <strong>16</strong> output languages end-to-end (translation UI + offline Piper where voices exist). Transcription auto-detect still leverages Whisper’s broader coverage.</p>
  </article>
</div>

## Providers at a glance

| Stage | Local / managed | Cloud (BYOK) |
| --- | --- | --- |
| **Transcription** | Faster-Whisper (CPU/GPU) | Gemini, Google STT, OpenAI Whisper API |
| **Translation** | NLLB-200 (GPU), CTranslate2 (CPU) | DeepL, Gemini, OpenAI, … |
| **TTS** | Piper (CPU), Qwen3-TTS / XTTS (GPU) | Edge TTS (no key), ElevenLabs, Google Cloud TTS, OpenAI TTS |
| **Diarization** | NeMo (GPU), WeSpeaker (CPU) | — |

See the [full provider tables and language notes](https://github.com/Babelworks/Babel-Player#provider-support) in the repository README.

## Requirements

| | |
| --- | --- |
| **OS** | Windows 10 or 11 (**x64** and **ARM64**) |
| **GPU (optional)** | NVIDIA CUDA for local GPU stages; RTX-class recommended for heavier models |
| **VRAM** | ~6&nbsp;GB minimum for many GPU paths; 8&nbsp;GB+ for higher-quality cloning workloads |
| **Release builds** | Self-contained — no separate .NET runtime install |
| **Source builds** | [.NET 10 SDK](https://dotnet.microsoft.com/) |

First GPU or CPU inference pulls a managed runtime download (on the order of hundreds of MB to a few GB, depending on path). Artifacts cache under `%LOCALAPPDATA%\BabelPlayer\runtime\`.

## Privacy & data

Processing stays on your machine unless you **choose** a cloud stage and supply keys. There are no accounts, no mandatory uploads, and no marketing trackers on this site. Details: [Privacy policy]({{ site.baseurl }}/privacy/).

## Get Babel Player

<div class="cta-panel" markdown="0">
  <p class="cta-panel__title">Ready to try it?</p>
  <p class="cta-panel__text">Installers and portable zips are published with every GitHub release.</p>
  <p class="cta-panel__actions">
    <a class="btn btn--primary" href="https://github.com/Babelworks/Babel-Player/releases/latest">Download latest release</a>
    <a class="btn btn--ghost" href="https://github.com/Babelworks/Babel-Player#installation">Installation notes</a>
  </p>
</div>

### Elsewhere

- [Source repository](https://github.com/Babelworks/Babel-Player)
- [CI status](https://github.com/Babelworks/Babel-Player/actions/workflows/ci.yml)
- [Support development on Ko-fi](https://ko-fi.com/babel_player)
