# Babel Player Architecture

## Overview

Babel Player is a MediaSession-centered media and language-processing application.

The architecture is organized around:

- Shell layer
- Application/domain layer
- Playback backend layer
- Presentation/rendering layer
- Provider/runtime layer

## Source of truth

`MediaSession` is the authoritative timed state for:

- source media
- playback timeline
- active stream selections
- transcript segments
- translation segments
- subtitle presentation state
- language analysis state
- future audio augmentation state

`MediaSessionCoordinator` is the only mutation boundary for timed session state.

## Layers

### Shell
Responsibilities:
- WinUI visual tree
- event forwarding
- layout
- presenter attachment

Must not own business truth.

### App / Domain
Responsibilities:
- MediaSession
- queue / history
- workflow orchestration
- projections

Must remain platform-neutral.

### Playback backend
Responsibilities:
- load / play / pause / seek
- track enumeration
- normalized clock reporting

Current implementation:
- `MpvPlaybackBackend`

### Presentation / Rendering
Responsibilities:
- video presentation
- subtitle presentation
- stage attachment

Current implementations:
- `MpvVideoPresenter`
- `DetachedWindowSubtitlePresenter`

Future direction:
- custom renderer backend
- in-renderer subtitle composition

## Long-term rendering direction

The architecture should support:

- D3D11 backend first on Windows
- possible future Vulkan / Metal-style backends
- portable renderer contracts
- no graphics-native types in App-layer contracts

## Anti-goals

Do not:
- reintroduce parallel timed state
- let shell code own subtitle or playback truth
- let presenter implementations become workflow coordinators
- leak WinUI / HWND / D3D types into app contracts