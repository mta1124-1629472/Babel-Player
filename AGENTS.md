\# Babel Player – Codex Development Guidelines



These rules apply when modifying this repository.



\## Architectural priorities



Babel Player is being structured so it can eventually support Linux and macOS in addition to Windows.



When proposing or implementing changes:



\- Keep application/domain models platform neutral.

\- Avoid introducing WinUI, Win32, or DirectX types into App-layer contracts.

\- Treat mpv and detached overlay windows as transitional infrastructure adapters.



\## Core state model



\- `MediaSession` is the authoritative timed state for playback, transcripts, translations, and subtitle presentation.

\- All mutations must go through `MediaSessionCoordinator`.

\- Shell/UI layers must consume immutable projections or snapshots.



Do not introduce parallel authoritative state.



\## Layer responsibilities



Shell (WinUI)

\- UI layout

\- input handling

\- visual state

\- presenters



App layer

\- MediaSession

\- workflow orchestration

\- queue/history logic

\- transcript and translation lanes



Infrastructure

\- playback backends

\- renderer implementations

\- provider adapters

\- platform integration



Keep these boundaries intact.



\## Rendering direction



The renderer architecture should support:



\- D3D11 backend on Windows initially

\- potential future Vulkan / Metal backends

\- in-renderer subtitle composition

\- future audio augmentation / dubbing pipelines



Avoid exposing graphics-native objects outside renderer infrastructure.



Examples to avoid leaking into App-layer contracts:



\- ID3D11Texture

\- swap chains

\- HWND

\- WinUI types



\## Subtitle architecture



Subtitle logic should be separated into:



\- subtitle source resolution

\- caption generation

\- translation

\- presentation projection

\- rendering/presentation



Subtitle presentation models must remain renderer-neutral.



\## Code changes



Before major refactors:



1\. Identify current ownership boundaries.

2\. Identify platform-specific leakage.

3\. Ensure the change does not increase Windows lock-in unnecessarily.



Prefer incremental migrations with clear exit criteria.

