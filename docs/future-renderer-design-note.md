# Future Renderer Design Note

## Goal

Keep the WinUI 3 shell stable while allowing Babel Player to migrate from the current mpv-backed presentation path to a future in-process renderer with tighter subtitle sync and room for translated or dubbed audio.

## Stable App-Layer Contracts

The following contracts are intended to survive backend and renderer replacement:

- `MediaSessionSnapshot`
- `MediaSessionCoordinator`
- `IMediaSessionStore`
- `IPlaybackClock`
- `IPlaybackBackend`
- `SubtitlePresentationModel`
- `SubtitlePresentationProjector`

These contracts remain renderer-neutral. They must not depend on Direct3D, swapchains, textures, Win32 handles, or any other graphics-specific types.

## Current Path

Today the active playback/presentation stack is:

- `MpvPlaybackBackend`
- `MpvVideoPresenter`
- `DetachedWindowSubtitlePresenter`
- `PlaybackHostAdapter`

This is infrastructure, not the long-term architecture.

## Future Custom Renderer Path

The intended replacement path is:

1. Add `IMediaDecodeBackend`
   - Own demux/decode and stream selection.
   - Publish normalized timing through `IPlaybackClock`.
   - Feed decoded audio/video data into renderer-owned pipelines.

2. Add `IVideoPresentationPipeline`
   - Own in-process frame presentation.
   - Remain behind `IVideoPresenter` at the shell boundary.
   - Replace `MpvVideoPresenter` without changing `MainWindow`, `StageCoordinator`, or `MediaSessionCoordinator`.

3. Add `ISubtitleCompositor`
   - Consume `SubtitlePresentationModel`.
   - Compose subtitles in-process over the rendered video surface.
   - Replace `DetachedWindowSubtitlePresenter` without changing subtitle workflows.

4. Add `IAudioPipeline`
   - Consume source audio selection plus `AudioAugmentationLane`.
   - Support future dubbed or synthesized playback aligned to `MediaSession`.

## Subtitle Composition Migration

Current detached overlays are a compatibility layer only.

- Subtitle workflow logic writes transcript and translation lanes into `MediaSession`.
- `SubtitlePresentationProjector` produces presenter-ready subtitle state.
- `StageCoordinator` treats subtitle presentation as infrastructure.

When in-renderer composition is added:

- `DetachedWindowSubtitlePresenter` can be removed.
- `ISubtitleCompositor` can consume the same `SubtitlePresentationModel`.
- Subtitle timing stays driven by `IPlaybackClock` and `MediaSessionCoordinator`.

## Dubbed Audio Migration

`AudioAugmentationLane` is intentionally present before dubbed playback exists.

This allows a future dubbed pipeline to:

- attach synthesized segments to stable transcript identities
- align translated speech with the shared playback clock
- switch between original and augmented audio without changing shell contracts

## Rules for Future Work

- Do not add renderer-native types to `BabelPlayer.App`.
- Keep `IVideoPresenter` focused on stage attachment and presentation lifecycle only.
- Keep `IPlaybackClock` minimal and normalized.
- Any future renderer must publish timing into `MediaSessionCoordinator` the same way the current backend does.
