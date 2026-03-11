# Babel Player Architecture Audit

Last updated: 2026-03-11

## Executive Summary

- The current branch is no longer organized around a WinUI shell directly controlling `mpv`; it is organized around immutable `MediaSessionSnapshot` state plus a write boundary in `MediaSessionCoordinator`. Evidence: `src/BabelPlayer.App/MediaSessionModels.cs :: MediaSessionSnapshot`; `src/BabelPlayer.App/MediaSessionStore.cs :: InMemoryMediaSessionStore.Snapshot`, `InMemoryMediaSessionStore.Update`; `src/BabelPlayer.App/MediaSessionCoordinator.cs :: MediaSessionCoordinator.ApplyClock`, `ApplyPlaybackState`, `ApplyTracks`, `SetTranscriptSegments`, `ReplaceTranslationSegments`, `UpdatePresentation`.
- `mpv` and detached subtitle/fullscreen overlays are now infrastructure adapters behind explicit seams. Evidence: `src/BabelPlayer.App/PlaybackContracts.cs :: IPlaybackClock`, `IPlaybackBackend`; `src/BabelPlayer.WinUI/PresentationContracts.cs :: IVideoPresenter`, `ISubtitlePresenter`; `src/BabelPlayer.App/MpvPlaybackBackend.cs :: MpvPlaybackBackend.InitializeAsync`; `src/BabelPlayer.WinUI/MpvVideoPresenter.cs :: MpvVideoPresenter.Initialize`; `src/BabelPlayer.WinUI/DetachedWindowSubtitlePresenter.cs :: DetachedWindowSubtitlePresenter.Present`.
- The shell is substantially thinner than the historical version, but `MainWindow` is still the largest integration hotspot because it builds the visual tree, wires events, applies projections, and still owns a large amount of UI glue. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: MainWindow.MainWindow`, `BuildShell`, `ApplyShellProjection`, `PlayerHost_MediaOpened`, `PlayerHost_MediaEnded`, `UpdateSubtitleVisibility`, `MainWindow_Closed`.

## Layer Breakdown

### 1. Process Entry And Runtime Composition

- `App.OnLaunched()` is the process entry point and delegates real composition to `MainWindow(new ShellCompositionRoot())`. Evidence: `src/BabelPlayer.WinUI/App.xaml.cs :: App.OnLaunched`.
- `ShellCompositionRoot.Create()` is the real dependency graph builder. It manually wires the shell, session coordinator, provider registries, subtitle services, playback backend, presenters, projections, and stage coordinator. Evidence: `src/BabelPlayer.WinUI/ShellCompositionRoot.cs :: ShellCompositionRoot.Create`.
- The composition root already reflects the future direction: app-layer session and workflow services are composed separately from WinUI presenters and mpv adapters. Evidence: `src/BabelPlayer.WinUI/ShellCompositionRoot.cs :: ShellCompositionRoot.Create`; constructed classes include `MediaSessionCoordinator`, `SubtitleApplicationService`, `SubtitleWorkflowController`, `MpvPlaybackBackend`, `PlaybackBackendCoordinator`, `MpvVideoPresenter`, `DetachedWindowSubtitlePresenter`, `ShellProjectionService`, `ShellController`, `StageCoordinator`.

### 2. Shell / Presentation Layer

- `MainWindow` is the WinUI shell surface. It owns control construction, event hookup, and application of immutable projections to UI controls. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: MainWindow.MainWindow`, `BuildShell`, `ApplyShellProjection`.
- `PlaybackHostAdapter` is the view-side bridge for video presentation and host-originated events. It exposes a WinUI `View`, initializes the presenter, and converts backend state into immutable `PlaybackStateSnapshot` payloads for shell events. Evidence: `src/BabelPlayer.WinUI/PlaybackHostAdapter.cs :: PlaybackHostAdapter.Initialize`, `BuildSnapshot`; events `MediaOpened`, `MediaEnded`, `PlaybackStateChanged`.
- `StageCoordinator` owns fullscreen overlay lifecycle, subtitle presenter placement, stage-bound synchronization, modal suppression, and overlay auto-hide behavior. It does not own subtitle business rules or playback policy. Evidence: `src/BabelPlayer.WinUI/StageCoordinator.cs :: StageCoordinator.HandleWindowModeChanged`, `HandleStageLayoutChanged`, `PresentSubtitles`, `SuppressModalUi`, `RefreshSubtitlePresentation`.
- `ShellProjectionService` is the shell’s read-model projector. It consumes `IMediaSessionStore` and emits immutable transport, track, and subtitle projections. Evidence: `src/BabelPlayer.App/ShellProjectionService.cs :: ShellProjectionService.HandleSnapshotChanged`, `BuildTransportProjection`, `BuildTrackProjection`, `BuildSubtitleProjection`.

### 3. Application Orchestration Layer

- `ShellController` is the top-level app orchestrator for playlist loading, media-open/media-ended behavior, resume policy, transport commands, and the caption startup gate. Evidence: `src/BabelPlayer.App/ShellController.cs :: ShellController.EnqueueFiles`, `EnqueueFolder`, `EnqueueDroppedItems`, `LoadPlaylistItemAsync`, `HandleMediaOpenedAsync`, `HandleMediaEnded`, `PlayAsync`, `SeekAsync`, `EvaluateCaptionStartupGateAsync`.
- `SubtitleApplicationService` is the main subtitle/AI workflow orchestrator. It owns model selection, translation enablement, auto-translate policy, subtitle import/generation/translation, cached generated subtitles, and runtime/credential readiness orchestration. Evidence: `src/BabelPlayer.App/SubtitleApplicationService.cs :: SubtitleApplicationService.InitializeAsync`, `SelectTranscriptionModelAsync`, `SelectTranslationModelAsync`, `LoadMediaSubtitlesAsync`, `HandleMediaSessionSnapshotChanged`; `src/BabelPlayer.App/SubtitleApplicationService.Workflow.cs :: LoadPersistedSelections`, `EnsureTranslationProviderReadyAsync`, `ReprocessCurrentSubtitlesForTranslationSettingsAsync`, `StartAutomaticCaptionGenerationAsync`.
- `SubtitleWorkflowController` is now a compatibility facade, not the source of truth. It delegates commands to `SubtitleApplicationService`, exposes projection-based snapshots, and uses `SubtitlePresentationProjector` for renderer-neutral subtitle presentation. Evidence: `src/BabelPlayer.App/SubtitleWorkflowController.cs :: SubtitleWorkflowController.InitializeAsync`, `SelectTranscriptionModelAsync`, `GetOverlayPresentation`, `GetEffectiveRenderMode`.

### 4. Session / Domain State Layer

- `MediaSessionSnapshot` is the central timed domain model. It is deliberately structured into source, timeline, streams, transcript, translation, subtitle presentation, language analysis, and augmentation lanes instead of one flat object. Evidence: `src/BabelPlayer.App/MediaSessionModels.cs :: MediaSessionSnapshot`, `MediaSourceState`, `MediaTimelineState`, `MediaStreamState`, `TranscriptLane`, `TranslationLane`, `SubtitlePresentationState`, `LanguageAnalysisState`, `AudioAugmentationLane`.
- The shell boundary is immutable. `InMemoryMediaSessionStore.Snapshot` returns a cloned snapshot, and `Update()` clones both the incoming and outgoing state before publishing `SnapshotChanged`. Evidence: `src/BabelPlayer.App/MediaSessionStore.cs :: InMemoryMediaSessionStore.Snapshot`, `InMemoryMediaSessionStore.Update`; `src/BabelPlayer.App/MediaSessionModels.cs :: MediaSessionSnapshotCloner.Clone`.
- `MediaSessionCoordinator` is the only mutation boundary for timed state. It applies playback backend state, clocks, tracks, transcript segments, translation segments, language analysis, and computed subtitle presentation. Evidence: `src/BabelPlayer.App/MediaSessionCoordinator.cs :: MediaSessionCoordinator.ApplyPlaybackState`, `ApplyClock`, `ApplyTracks`, `SetTranscriptSegments`, `UpsertTranscriptSegment`, `ReplaceTranslationSegments`, `UpsertTranslationSegment`, `SetLanguageAnalysis`, `UpdatePresentation`.
- Subtitle presentation state in the app layer is renderer-neutral. It stores active segment IDs and plain text/status, not screen coordinates, window handles, or presentational geometry. Evidence: `src/BabelPlayer.App/MediaSessionModels.cs :: SubtitlePresentationState`.
- Transcript and translation segments already carry stable identities, provenance, and revision data that can support later alignment, replacement, hover lookup, and dubbed-audio mapping. Evidence: `src/BabelPlayer.App/MediaSessionModels.cs :: TranscriptSegmentId`, `TranslationSegmentId`, `SegmentProvenance`, `SegmentRevision`, `TranscriptSegment`, `TranslationSegment`, `SegmentIdentity.CreateTranscriptId`, `SegmentIdentity.CreateTranslationId`.

### 5. Playback / Rendering Adapters

- `IPlaybackClock` is intentionally minimal and normalized: `Position`, `Duration`, `Rate`, `IsPaused`, `IsSeekable`, and `SampledAtUtc`. Evidence: `src/BabelPlayer.App/PlaybackContracts.cs :: ClockSnapshot`, `IPlaybackClock`.
- `IPlaybackBackend` is the app-facing playback seam. It owns load/play/pause/seek/track/delay/rate/volume commands, media lifecycle events, and track/state publication. Evidence: `src/BabelPlayer.App/PlaybackContracts.cs :: IPlaybackBackend`.
- `MpvPlaybackBackend` is the current backend adapter. It wraps `MpvPlaybackEngine`, normalizes engine state into `ClockSnapshot` and `PlaybackBackendState`, and raises backend-neutral events. Evidence: `src/BabelPlayer.App/MpvPlaybackBackend.cs :: MpvPlaybackBackend.InitializeAsync`, `HandleEngineStateChanged`.
- `PlaybackBackendCoordinator` is the only path that writes backend timing/state into `MediaSession`. Evidence: `src/BabelPlayer.App/PlaybackBackendCoordinator.cs :: PlaybackBackendCoordinator.HandlePlaybackStateChanged`, `HandleTracksChanged`, `HandleClockChanged`.
- `MpvVideoPresenter` is a narrow presentation adapter over `MpvHostControl`. Evidence: `src/BabelPlayer.WinUI/MpvVideoPresenter.cs :: MpvVideoPresenter.Initialize`, `RequestBoundsSync`, `SuppressPresentation`, `GetStageBounds`.
- `MpvHostControl` is transitional infrastructure that still contains a broader legacy transport surface than the rest of the architecture now exposes, but its active role is native child window hosting, backend initialization, host bounds sync, and input/shortcut/fullscreen event bridging. Evidence: `src/BabelPlayer.WinUI/MpvHostControl.cs :: MpvHostControl.Initialize`, `EnsureInitialized`, `QueueHostBoundsSync`, `GetStageBounds`, `HostWindowProc`.

### 6. AI / Provider Layer

- `ISubtitleSourceResolver`, `ICaptionGenerator`, `ISubtitleTranslator`, `IAiCredentialCoordinator`, and `IRuntimeProvisioner` are the workflow-side capability seams. Evidence: `src/BabelPlayer.App/SubtitleApplicationServices.cs :: ISubtitleSourceResolver`, `ICaptionGenerator`, `ISubtitleTranslator`, `IAiCredentialCoordinator`, `IRuntimeProvisioner`.
- Caption generation is provider-registry based. `DefaultCaptionGenerator.GenerateCaptionsAsync()` resolves providers from `TranscriptionProviderRegistry` and tries them in sequence. Evidence: `src/BabelPlayer.App/SubtitleApplicationServices.cs :: DefaultCaptionGenerator.GenerateCaptionsAsync`; `src/BabelPlayer.App/ProviderAvailabilityService.cs :: TranscriptionProviderRegistry.ResolveProviders`.
- Translation is provider-registry based. `ProviderBackedSubtitleTranslator.TranslateBatchAsync()` selects an adapter through `TranslationProviderRegistry`. Evidence: `src/BabelPlayer.App/SubtitleApplicationServices.cs :: ProviderBackedSubtitleTranslator.TranslateBatchAsync`; `src/BabelPlayer.App/ProviderAvailabilityService.cs :: TranslationProviderRegistry.TryGetProvider`.
- Provider composition is explicit and centralized. `ProviderAvailabilityCompositionFactory.Create()` wires local Whisper, Windows Speech fallback, OpenAI transcription, OpenAI/Google/DeepL/Microsoft translation, local llama translation, and the llama.cpp runtime resolver. Evidence: `src/BabelPlayer.App/ProviderAvailabilityService.cs :: ProviderAvailabilityCompositionFactory.Create`.
- Per-provider adapters already exist as isolated files, which makes the adapter seam real rather than theoretical. Evidence: `src/BabelPlayer.App/LocalTranscriptionProviderAdapter.cs :: WhisperLocalTranscriptionProvider.TranscribeAsync`, `WindowsSpeechFallbackTranscriptionProvider.TranscribeAsync`; `src/BabelPlayer.App/OpenAiTranslationProviderAdapter.cs :: OpenAiTranslationProviderAdapter.TranslateBatchAsync`; sibling files `GoogleTranslationProviderAdapter.cs`, `DeepLTranslationProviderAdapter.cs`, `MicrosoftTranslationProviderAdapter.cs`, `LocalLlamaTranslationProviderAdapter.cs`, `OpenAiTranscriptionProviderAdapter.cs`.

### 7. Runtime Bootstrap And Persistence

- Runtime acquisition is handled by explicit installers, not embedded binaries. Evidence: `src/BabelPlayer.App/RuntimeBootstrapService.cs :: RuntimeBootstrapService.EnsureMpvAsync`, `EnsureFfmpegAsync`, `EnsureLlamaCppAsync`; `src/BabelPlayer.App/MpvRuntimeInstaller.cs :: MpvRuntimeInstaller.InstallAsync`; `src/BabelPlayer.App/FfmpegRuntimeInstaller.cs :: FfmpegRuntimeInstaller.InstallAsync`; `src/BabelPlayer.App/LlamaCppRuntimeInstaller.cs :: LlamaCppRuntimeInstaller.InstallAsync`.
- Resume persistence is now app-layer coordination rather than window-owned timer logic. `ResumeTrackingCoordinator` subscribes to the normalized playback clock and persists on a five-second cadence through `ResumePlaybackService`. Evidence: `src/BabelPlayer.App/ResumeTrackingCoordinator.cs :: ResumeTrackingCoordinator.HandleClockChanged`, `Flush`, `CurrentSnapshot`; `src/BabelPlayer.App/ResumePlaybackService.cs :: ResumePlaybackService.BuildEntry`, `FindEntry`, `Update`, `RemoveCompletedEntry`.

## Dependency Structure

- `App` depends on `MainWindow`, and `MainWindow` depends on `IShellCompositionRoot`. Evidence: `src/BabelPlayer.WinUI/App.xaml.cs :: App.OnLaunched`; `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: MainWindow.MainWindow`.
- `ShellCompositionRoot` depends on both app-layer services and WinUI infrastructure. Evidence: `src/BabelPlayer.WinUI/ShellCompositionRoot.cs :: ShellCompositionRoot.Create`.
- `ShellController` depends on `PlaylistController`, `PlaybackSessionController`, `IPlaybackBackend`, `SubtitleWorkflowController`, `LibraryBrowserService`, and `ResumePlaybackService`. Evidence: `src/BabelPlayer.App/ShellController.cs :: ShellController.ShellController`.
- `SubtitleWorkflowController` depends on `SubtitleApplicationService`, `SubtitleWorkflowProjectionAdapter`, and `SubtitlePresentationProjector`. Evidence: `src/BabelPlayer.App/SubtitleWorkflowController.cs :: SubtitleWorkflowController.SubtitleWorkflowController`.
- `SubtitleWorkflowProjectionAdapter` depends on `ISubtitleWorkflowStateStore` and `IMediaSessionStore`. Evidence: `src/BabelPlayer.App/SubtitleWorkflowProjectionAdapter.cs :: SubtitleWorkflowProjectionAdapter.BuildSnapshot`.
- `SubtitleApplicationService` depends on source resolver, caption generator, translator, credential/runtime coordinators, `MediaSessionCoordinator`, workflow state store, and provider availability. Evidence: `src/BabelPlayer.App/SubtitleApplicationService.cs :: SubtitleApplicationService.SubtitleApplicationService`.
- `PlaybackBackendCoordinator` depends on `IPlaybackBackend` and `MediaSessionCoordinator` and is the bridge from backend events to session state. Evidence: `src/BabelPlayer.App/PlaybackBackendCoordinator.cs :: PlaybackBackendCoordinator.PlaybackBackendCoordinator`.
- `PlaybackHostAdapter` depends on `IPlaybackBackend` and `IVideoPresenter`; `StageCoordinator` depends on `IWindowModeService`, `IVideoPresenter`, and `ISubtitlePresenter`. Evidence: `src/BabelPlayer.WinUI/PlaybackHostAdapter.cs :: PlaybackHostAdapter.PlaybackHostAdapter`; `src/BabelPlayer.WinUI/StageCoordinator.cs :: StageCoordinator.StageCoordinator`.

## Entry Points

### Process Entry

- `App.OnLaunched()` creates and activates the shell. Evidence: `src/BabelPlayer.WinUI/App.xaml.cs :: App.OnLaunched`.
- `MainWindow.MainWindow()` creates the visual tree, asks the composition root for dependencies, initializes the playback host, and subscribes to workflow and projection events. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: MainWindow.MainWindow`.

### User / Runtime Entry Paths

- Open-file command enters through `MainWindow.OpenFile_Click`, then hands work to `ShellController` and `SubtitleWorkflowController`. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: OpenFile_Click`; `src/BabelPlayer.App/ShellController.cs :: EnqueueFiles`, `LoadPlaylistItemAsync`; `src/BabelPlayer.App/SubtitleWorkflowController.cs :: LoadMediaSubtitlesAsync`.
- Media-open events enter through `PlaybackHostAdapter.MediaOpened`, then `MainWindow.PlayerHost_MediaOpened`, then `ShellController.HandleMediaOpenedAsync`. Evidence: `src/BabelPlayer.WinUI/PlaybackHostAdapter.cs :: MediaOpened`; `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: PlayerHost_MediaOpened`; `src/BabelPlayer.App/ShellController.cs :: HandleMediaOpenedAsync`.
- Media-ended events enter through `PlaybackHostAdapter.MediaEnded`, then `MainWindow.PlayerHost_MediaEnded`, then `ShellController.HandleMediaEnded`. Evidence: `src/BabelPlayer.WinUI/PlaybackHostAdapter.cs :: MediaEnded`; `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: PlayerHost_MediaEnded`; `src/BabelPlayer.App/ShellController.cs :: HandleMediaEnded`.
- Subtitle workflow changes enter through `SubtitleWorkflowController` command methods and propagate through `SubtitleApplicationService`. Evidence: `src/BabelPlayer.App/SubtitleWorkflowController.cs :: SelectTranscriptionModelAsync`, `SelectTranslationModelAsync`, `SetTranslationEnabledAsync`, `ImportExternalSubtitlesAsync`; `src/BabelPlayer.App/SubtitleApplicationService.cs :: SelectTranscriptionModelAsync`, `SelectTranslationModelAsync`, `SetTranslationEnabledAsync`, `ImportExternalSubtitlesAsync`.

## Media Pipeline

1. `ShellController.LoadPlaylistItemAsync()` loads media into the backend, applies playback defaults, resets resume tracking, and asks the subtitle workflow to load subtitles. Evidence: `src/BabelPlayer.App/ShellController.cs :: LoadPlaylistItemAsync`.
2. `MpvPlaybackBackend.LoadAsync()` forwards to `MpvPlaybackEngine.LoadAsync()`. Evidence: `src/BabelPlayer.App/MpvPlaybackBackend.cs :: LoadAsync`; `src/BabelPlayer.App/MpvPlaybackEngine.cs :: LoadAsync`.
3. `MpvPlaybackEngine.InitializeAsync()` ensures the mpv runtime, launches `mpv.exe`, opens the named pipe, and subscribes to property changes. Evidence: `src/BabelPlayer.App/MpvPlaybackEngine.cs :: InitializeAsync`, `ObservePropertyAsync`, `ReaderLoopAsync`.
4. Backend events flow through `PlaybackBackendCoordinator` into `MediaSessionCoordinator`, which updates source, timeline, streams, and active subtitle presentation. Evidence: `src/BabelPlayer.App/PlaybackBackendCoordinator.cs :: HandlePlaybackStateChanged`, `HandleTracksChanged`, `HandleClockChanged`; `src/BabelPlayer.App/MediaSessionCoordinator.cs :: ApplyPlaybackState`, `ApplyClock`, `ApplyTracks`, `UpdatePresentation`.
5. The shell reads immutable projections from `ShellProjectionService` and updates transport/track/subtitle UI from those projections. Evidence: `src/BabelPlayer.App/ShellProjectionService.cs :: HandleSnapshotChanged`; `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: ApplyShellProjection`.

## AI / Subtitle Pipeline

1. Media subtitle loading begins in `SubtitleApplicationService.LoadMediaSubtitlesAsync()`, which resets workflow state, opens the session, clears transcript state, and chooses sidecar, cached generated, or generated-caption paths. Evidence: `src/BabelPlayer.App/SubtitleApplicationService.cs :: LoadMediaSubtitlesAsync`.
2. External or embedded subtitle import flows through `ISubtitleSourceResolver` and then back into `LoadSubtitleCuesAsync()` to write transcript lanes. Evidence: `src/BabelPlayer.App/SubtitleApplicationServices.cs :: DefaultSubtitleSourceResolver.LoadExternalSubtitleCuesAsync`, `ExtractEmbeddedSubtitleCuesAsync`; `src/BabelPlayer.App/SubtitleApplicationService.Workflow.cs :: LoadSubtitleCuesAsync`.
3. Generated captions flow through `DefaultCaptionGenerator.GenerateCaptionsAsync()`, provider registries, and provider adapters, then are converted into `TranscriptSegment` records. Evidence: `src/BabelPlayer.App/SubtitleApplicationServices.cs :: DefaultCaptionGenerator.GenerateCaptionsAsync`; `src/BabelPlayer.App/SubtitleApplicationService.Utilities.cs :: BuildTranscriptSegments`, `BuildTranscriptSegment`.
4. Transcript and translation lanes are written through `MediaSessionCoordinator`, not by presenter code. Evidence: `src/BabelPlayer.App/MediaSessionCoordinator.cs :: SetTranscriptSegments`, `UpsertTranscriptSegment`, `ReplaceTranslationSegments`, `UpsertTranslationSegment`.
5. Translation triggering is session-driven. `SubtitleApplicationService.HandleMediaSessionSnapshotChanged()` watches the active transcript segment in session state and queues translation only when translation is enabled and the matching translated segment does not yet exist. Evidence: `src/BabelPlayer.App/SubtitleApplicationService.cs :: HandleMediaSessionSnapshotChanged`; `src/BabelPlayer.App/SubtitleApplicationService.Utilities.cs :: HasTranslatedSegment`, `GetActiveTranscriptSegment`, `CreateTranslationSegment`.
6. `SubtitleWorkflowProjectionAdapter` and `SubtitlePresentationProjector` convert workflow state plus media session state into UI-friendly workflow snapshots and renderer-neutral subtitle presentation models. Evidence: `src/BabelPlayer.App/SubtitleWorkflowProjectionAdapter.cs :: BuildSnapshot`; `src/BabelPlayer.App/SubtitlePresentationProjector.cs :: Build`, `GetEffectiveRenderMode`.

## Rendering Pipeline

### Video Path

1. `MainWindow` hosts `PlaybackHostAdapter.View`. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: MainWindow.MainWindow`, `BuildPlayerPane`; `src/BabelPlayer.WinUI/PlaybackHostAdapter.cs :: View`.
2. `PlaybackHostAdapter.Initialize()` calls `IVideoPresenter.Initialize()`. Evidence: `src/BabelPlayer.WinUI/PlaybackHostAdapter.cs :: Initialize`.
3. `MpvVideoPresenter.Initialize()` delegates to `MpvHostControl.Initialize()`. Evidence: `src/BabelPlayer.WinUI/MpvVideoPresenter.cs :: Initialize`.
4. `MpvHostControl.EnsureInitialized()` creates a native child host window, initializes the backend with the host handle, and keeps host bounds synchronized with the WinUI layout. Evidence: `src/BabelPlayer.WinUI/MpvHostControl.cs :: EnsureInitialized`, `QueueHostBoundsSync`, `UpdateHostBounds`.
5. `MpvPlaybackEngine.InitializeAsync()` launches mpv with `--wid=<hostHandle>` and feeds its playback state back through IPC. Evidence: `src/BabelPlayer.App/MpvPlaybackEngine.cs :: InitializeAsync`.

### Subtitle Path

1. `MainWindow.UpdateSubtitleVisibility()` builds a `SubtitlePresentationModel` from current shell/workflow state and delegates actual presentation to `StageCoordinator`. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: UpdateSubtitleVisibility`.
2. `StageCoordinator.PresentSubtitles()` stores the current presentation model and style, then `RefreshSubtitlePresentation()` calculates stage bounds from the video presenter and calls the subtitle presenter. Evidence: `src/BabelPlayer.WinUI/StageCoordinator.cs :: PresentSubtitles`, `RefreshSubtitlePresentation`.
3. `DetachedWindowSubtitlePresenter.Present()` turns the renderer-neutral model into overlay window content and positions the detached subtitle window. Evidence: `src/BabelPlayer.WinUI/DetachedWindowSubtitlePresenter.cs :: Present`.
4. `SubtitleOverlayWindow.ShowOverlay()` and `PositionOverlay()` handle the concrete detached-window composition. Evidence: `src/BabelPlayer.WinUI/SubtitleOverlayWindow.cs :: ShowOverlay`, `PositionOverlay`.

## Native Interop Points

- Native child video host creation and message pumping live in `MpvHostControl`. Evidence: `src/BabelPlayer.WinUI/MpvHostControl.cs :: EnsureInitialized`, `DestroyHostWindow`, `HostWindowProc`; Win32 calls include `CreateWindowEx`, `SetWindowLongPtr`, `DestroyWindow`, `ShowWindow`, `MoveWindow`, `CallWindowProc`, `DefWindowProc`, `GetKeyState`, `GetDoubleClickTime`, `ClientToScreen`.
- Detached subtitle and fullscreen overlay windows both use owner-window parenting and manual z-order/show positioning. Evidence: `src/BabelPlayer.WinUI/SubtitleOverlayWindow.cs :: EnsureWindow`, `ShowOverlay`, `PositionOverlay`; `src/BabelPlayer.WinUI/FullscreenOverlayWindow.cs :: EnsureWindow`, `ShowOverlay`, `PositionOverlay`; Win32 calls include `SetWindowLongPtr`, `ShowWindow`, `SetWindowPos`.
- Window mode changes are performed through `AppWindow` presenters rather than custom Win32 fullscreen logic. Evidence: `src/BabelPlayer.WinUI/WinUIWindowModeService.cs :: SetModeAsync`, `EnterFullscreenAsync`, `ExitFullscreenAsync`.
- mpv integration is process + named-pipe IPC, not an in-process decoder. Evidence: `src/BabelPlayer.App/MpvPlaybackEngine.cs :: InitializeAsync`, `ReaderLoopAsync`, `WriteMessageAsync`; .NET interop types include `Process`, `NamedPipeClientStream`, `StreamReader`, `StreamWriter`.
- Runtime bootstrap downloads binaries at runtime and unpacks them into app data. Evidence: `src/BabelPlayer.App/MpvRuntimeInstaller.cs :: InstallAsync`; `src/BabelPlayer.App/FfmpegRuntimeInstaller.cs :: InstallAsync`; `src/BabelPlayer.App/LlamaCppRuntimeInstaller.cs :: InstallAsync`.

## Likely Refactor Targets

### 1. `MainWindow`

- Why: It still combines code-built visual tree construction, a large amount of event wiring, settings/UI synchronization, shell event forwarding, overlay control handling, and several UI-specific behavior branches. Evidence: `src/BabelPlayer.WinUI/MainWindow.xaml.cs :: MainWindow.MainWindow`, `BuildShell`, `ApplyShellProjection`, `PlayerHost_MediaOpened`, `PlayerHost_MediaEnded`, `UpdateSubtitleVisibility`, `TryApplyStandardAutoFit`, `MainWindow_Closed`.

### 2. `ShellCompositionRoot`

- Why: It is the correct composition boundary, but it is now a long manual object-graph factory with substantial knowledge of provider, runtime, workflow, playback, and shell wiring. Evidence: `src/BabelPlayer.WinUI/ShellCompositionRoot.cs :: ShellCompositionRoot.Create`.

### 3. `SubtitleApplicationService`

- Why: The controller split is done, but the application service is still large and spread across multiple partials for policy, cache management, translation orchestration, caption generation, status publishing, and runtime coordination. Evidence: `src/BabelPlayer.App/SubtitleApplicationService.cs :: SubtitleApplicationService.LoadMediaSubtitlesAsync`, `HandleMediaSessionSnapshotChanged`; `src/BabelPlayer.App/SubtitleApplicationService.Workflow.cs :: ReprocessCurrentSubtitlesForTranslationSettingsAsync`, `ReprocessCurrentSubtitlesForTranscriptionModelAsync`, `StartAutomaticCaptionGenerationAsync`; `src/BabelPlayer.App/SubtitleApplicationService.Helpers.cs :: TranslateAllCuesAsync`, `TranslateCueAsync`, `WarmupSelectedLocalTranslationRuntimeAsync`.

### 4. `MpvHostControl`

- Why: It is functioning as infrastructure, but it still exposes a broad legacy transport/control surface (`Source`, `Position`, `Volume`, `Play`, `Pause`, `SeekBy`, `SetPlaybackRate`, `SetAspectRatio`, `SetHardwareDecodingMode`, `Screenshot`) that no longer matches the narrowed presenter architecture. Evidence: `src/BabelPlayer.WinUI/MpvHostControl.cs :: Source`, `Position`, `Volume`, `Play`, `Pause`, `SeekBy`, `SetPlaybackRate`, `SetAspectRatio`, `SetHardwareDecodingMode`, `Screenshot`.

### 5. `MpvPlaybackEngine`

- Why: It is the most infrastructure-heavy class in the repo. It owns process lifecycle, named-pipe IPC, mpv command serialization, property observation, track parsing, and snapshot normalization. Any backend swap or deeper renderer migration will continue to isolate here first. Evidence: `src/BabelPlayer.App/MpvPlaybackEngine.cs :: InitializeAsync`, `SendCommandAsync`, `WriteMessageAsync`, `ReaderLoopAsync`, `HandlePropertyChange`, `ParseTracks`, `UpdateTrackSelection`.

### 6. `ProviderAvailabilityService`

- Why: Provider availability, provider composition, registry definitions, and runtime path resolution still live in one file. The seam is correct, but composition, registry declarations, and availability service logic are tightly co-located. Evidence: `src/BabelPlayer.App/ProviderAvailabilityService.cs :: ProviderAvailabilityCompositionFactory.Create`, `TranscriptionProviderRegistry.ResolveProviders`, `TranslationProviderRegistry.TryGetProvider`, `ProviderAvailabilityService.ResolvePersistedTranscriptionModelKey`, `ResolvePersistedTranslationModelKey`, `ResolveLlamaCppServerPath`.

## Future-Renderer Readiness

- Placeholder renderer-neutral contracts already exist in code, which means future renderer work has named seams to target without leaking graphics API concerns into app state. Evidence: `src/BabelPlayer.App/FutureRendererContracts.cs :: IMediaDecodeBackend`, `IVideoPresentationPipeline`, `ISubtitleCompositor`, `IAudioPipeline`.
- The current subtitle presentation model is already clean enough for in-renderer subtitle composition later because it only carries text visibility and line selection, not detached-window coordinates. Evidence: `src/BabelPlayer.App/MediaSessionModels.cs :: SubtitlePresentationModel`, `SubtitlePresentationState`; `src/BabelPlayer.App/SubtitlePresentationProjector.cs :: Build`.
- The future renderer is not implemented. Today’s rendering path still terminates in `MpvPlaybackEngine` plus detached overlay windows. Evidence: `src/BabelPlayer.App/MpvPlaybackEngine.cs :: InitializeAsync`; `src/BabelPlayer.WinUI/DetachedWindowSubtitlePresenter.cs :: Present`; `src/BabelPlayer.WinUI/SubtitleOverlayWindow.cs :: ShowOverlay`.

## Overall Assessment

- The architecture has crossed the important threshold from “WinUI player shell with subtitle features” to “MediaSession-centered application with replaceable playback/presentation adapters.” Evidence: `src/BabelPlayer.App/MediaSessionModels.cs :: MediaSessionSnapshot`; `src/BabelPlayer.App/PlaybackContracts.cs :: IPlaybackClock`, `IPlaybackBackend`; `src/BabelPlayer.WinUI/PresentationContracts.cs :: IVideoPresenter`, `ISubtitlePresenter`.
- The highest remaining architectural risk is not missing seams; it is integration complexity concentrated in a few large boundary classes. The primary candidates are `MainWindow`, `ShellCompositionRoot`, `SubtitleApplicationService`, `MpvHostControl`, `MpvPlaybackEngine`, and `ProviderAvailabilityService`. Evidence: cited above in “Likely Refactor Targets.”
