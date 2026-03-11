# Babel Player Architecture Audit

## Scope

This audit covers the current solution structure, runtime flow, UI/rendering model, media and AI pipelines, native interop, and likely refactor targets in `Babel-Player`.

## Executive Summary

Babel Player is organized as a three-layer desktop application:

1. `BabelPlayer.WinUI` is the WinUI 3 shell and native windowing layer.
2. `BabelPlayer.App` is the orchestration layer for playback, subtitles, settings, credentials, and runtime bootstrapping.
3. `BabelPlayer.Core` contains reusable engines and provider-facing services for transcription, translation, language detection, and hardware probing.

Evidence:

- Solution references show `WinUI -> App -> Core`: `src/BabelPlayer.WinUI/BabelPlayer.WinUI.csproj`, `src/BabelPlayer.App/BabelPlayer.App.csproj`
- Test references target `App` and `Core`, not the shell: `tests/BabelPlayer.App.Tests/BabelPlayer.App.Tests.csproj`

The architecture is functional, but it is not strongly layered at runtime. The main shell directly constructs many collaborators, the player host directly creates its playback engine, and subtitle workflow logic centralizes orchestration, credentials, runtime installation, translation, and caption generation in one controller.

Evidence:

- Shell composition happens in `MainWindow.MainWindow()`: `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `MainWindow`
- Player host hard-codes the engine in `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`
- Subtitle workflow is concentrated in `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`

## Solution Breakdown

### 1. WinUI Shell Layer

Primary responsibility: application startup, windowing, layout, command handling, host control integration, overlay windows, and user interaction.

Key components:

- `src/BabelPlayer.WinUI/App.xaml.cs`, class `App`, method `OnLaunched`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, methods `MainWindow`, `BuildShell`
- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`, methods `Initialize`, `EnsureInitialized`, `UpdateHostBounds`
- `src/BabelPlayer.WinUI/SubtitleOverlayWindow.cs`, class `SubtitleOverlayWindow`, methods `ShowOverlay`, `PositionOverlay`
- `src/BabelPlayer.WinUI/FullscreenOverlayWindow.cs`, class `FullscreenOverlayWindow`, methods `ShowOverlay`, `PositionOverlay`
- `src/BabelPlayer.WinUI/WinUIWindowModeService.cs`, class `WinUIWindowModeService`, methods `EnsureInitialStandardBounds`, `SetModeAsync`

Claim:

- The shell is code-built rather than XAML-composed for its main UI surface.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml`, class `MainWindow`, only declares a root `<Grid />`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `BuildShell`, builds the command bar, panes, player surface, and transport UI in C#

### 2. Application Orchestration Layer

Primary responsibility: session state, playlist control, subtitle workflow, settings, credentials, runtime setup, and view-model state.

Key components:

- `src/BabelPlayer.App/ViewModels/MainShellViewModel.cs`, class `MainShellViewModel`
- `src/BabelPlayer.App/PlaylistController.cs`, class `PlaylistController`
- `src/BabelPlayer.App/PlaybackSessionController.cs`, class `PlaybackSessionController`
- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`
- `src/BabelPlayer.App/SettingsFacade.cs`, class `SettingsFacade`
- `src/BabelPlayer.App/CredentialFacade.cs`, class `CredentialFacade`
- `src/BabelPlayer.App/AppStateStore.cs`, class `AppStateStore`
- `src/BabelPlayer.App/SecureSettingsStore.cs`, class `SecureSettingsStore`
- `src/BabelPlayer.App/MpvPlaybackEngine.cs`, class `MpvPlaybackEngine`
- `src/BabelPlayer.App/MpvRuntimeInstaller.cs`, class `MpvRuntimeInstaller`
- `src/BabelPlayer.App/LlamaCppRuntimeInstaller.cs`, class `LlamaCppRuntimeInstaller`

Claim:

- The view-model root aggregates browser, playlist, transport, and subtitle overlay state for the shell.

Evidence:

- `src/BabelPlayer.App/ViewModels/MainShellViewModel.cs`, class `MainShellViewModel`, properties `Browser`, `Playlist`, `Transport`, `SubtitleOverlay`

### 3. Core Services Layer

Primary responsibility: reusable AI/media-adjacent services and platform detection.

Key components:

- `src/BabelPlayer.Core/AsrService.cs`, class `AsrService`
- `src/BabelPlayer.Core/MtService.cs`, class `MtService`
- `src/BabelPlayer.Core/LanguageDetector.cs`, class `LanguageDetector`
- `src/BabelPlayer.Core/HardwareDetector.cs`, class `HardwareDetector`
- `src/BabelPlayer.Core/IPlaybackEngine.cs`, interface `IPlaybackEngine`

Claim:

- The Core layer contains the provider-facing translation and transcription implementations, while the App layer decides when and how to use them.

Evidence:

- `src/BabelPlayer.Core/AsrService.cs`, class `AsrService`, methods `TranscribeVideoAsync`, `TranscribeWithCloudAsync`
- `src/BabelPlayer.Core/MtService.cs`, class `MtService`, methods `TranslateAsync`, `TranslateBatchAsync`
- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`, methods `StartAutomaticCaptionGenerationAsync`, `TranslateCueAsync`, `TranslateCueBatchAsync`

## Entry Points

### Process Entry

The application starts in WinUI and immediately activates `MainWindow`.

Evidence:

- `src/BabelPlayer.WinUI/App.xaml.cs`, class `App`, method `OnLaunched`

### UI Composition Entry

`MainWindow.MainWindow()` is the runtime composition root. It constructs services, wires events, initializes the player host, binds playlist state, and starts asynchronous shell initialization.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `MainWindow`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `BuildShell`

### Media Ingress Entry Points

Media can enter the system through explicit open actions, folder queueing, and drag-drop.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `OpenFile_Click`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `OpenFolder_Click`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `RootGrid_Drop`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `QueueSpecificFolderAsync`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `LoadPlaylistItemAsync`

## Dependency Structure

The intended reference graph is clean, but runtime ownership is concentrated in the shell.

Reference graph:

- `BabelPlayer.WinUI` references `BabelPlayer.App` and `BabelPlayer.Core`
- `BabelPlayer.App` references `BabelPlayer.Core`
- Tests reference `BabelPlayer.App` and `BabelPlayer.Core`

Evidence:

- `src/BabelPlayer.WinUI/BabelPlayer.WinUI.csproj`
- `src/BabelPlayer.App/BabelPlayer.App.csproj`
- `tests/BabelPlayer.App.Tests/BabelPlayer.App.Tests.csproj`

Runtime graph:

- `MainWindow` directly creates playback, window-mode, file-picker, credential-dialog, runtime-bootstrap, and subtitle workflow services.
- `MpvHostControl` directly creates `MpvPlaybackEngine`.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `MainWindow`
- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`, field initialization for `_engine`

## Media Pipeline

### 1. Queue and Session Selection

User actions populate the playlist and select an item for loading.

Evidence:

- `src/BabelPlayer.App/PlaylistController.cs`, class `PlaylistController`
- `src/BabelPlayer.App/PlaybackSessionController.cs`, class `PlaybackSessionController`, method `StartWith`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `LoadPlaylistItemAsync`

### 2. Playback Host Initialization

The WinUI host creates a child native window, embeds mpv into it, and keeps the host bounds synchronized with the XAML layout.

Evidence:

- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`, methods `EnsureInitialized`, `UpdateHostBounds`, `HostWindowProc`
- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`, native imports `CreateWindowEx`, `SetWindowLongPtr`, `DefWindowProc`, `DestroyWindow`

### 3. mpv Runtime and IPC

Playback uses an external `mpv.exe` process controlled through named-pipe JSON IPC, not an in-proc media engine.

Evidence:

- `src/BabelPlayer.App/MpvPlaybackEngine.cs`, class `MpvPlaybackEngine`, methods `InitializeAsync`, `LoadAsync`, `ReaderLoopAsync`, `HandlePropertyChange`
- `src/BabelPlayer.App/MpvRuntimeInstaller.cs`, class `MpvRuntimeInstaller`, method `InstallAsync`

### 4. Subtitle Source Resolution

Subtitle loading prefers sidecar files, then generated subtitle cache, then caption generation, while embedded subtitle imports are handled separately.

Evidence:

- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`, methods `LoadMediaSubtitlesAsync`, `TryLoadCachedGeneratedSubtitles`, `StartAutomaticCaptionGenerationAsync`
- `src/BabelPlayer.App/SubtitleImportService.cs`, class `SubtitleImportService`, methods `LoadExternalSubtitleCuesAsync`, `ExtractEmbeddedSubtitleCuesAsync`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, method `LoadPlaylistItemAsync`

## AI Pipeline

### 1. Automatic Caption Generation

Caption generation is orchestrated by `SubtitleWorkflowController`, which chooses between local and cloud ASR based on model selection and available credentials.

Evidence:

- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`, methods `StartAutomaticCaptionGenerationAsync`, `DefaultTranscribeVideoAsync`
- `src/BabelPlayer.Core/AsrService.cs`, class `AsrService`, methods `TranscribeVideoAsync`, `TranscribeLocallyAsync`, `TranscribeWithCloudAsync`

### 2. Local ASR Path

The local path extracts audio, chunks it, downloads or loads Whisper models, and can fall back to Windows speech APIs.

Evidence:

- `src/BabelPlayer.Core/AsrService.cs`, class `AsrService`, methods `ExtractWaveAudio`, `SplitWaveFile`, `GetWhisperFactoryAsync`, `CopyModelStreamWithProgressAsync`, `TranscribeWithWindowsSpeech`

### 3. Translation Path

Translation is provider-pluggable. The subtitle workflow configures the translation mode, and `MtService` dispatches to local llama.cpp or cloud providers such as OpenAI, Google, DeepL, and Microsoft.

Evidence:

- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`, methods `ConfigureTranslator`, `TranslateAllCuesAsync`, `TranslateCueAsync`, `TranslateCueBatchAsync`
- `src/BabelPlayer.Core/MtService.cs`, class `MtService`, methods `ConfigureCloud`, `ConfigureLocal`, `TranslateBatchWithProviderAsync`, `TranslateWithLlamaServerAsync`
- `src/BabelPlayer.Core/MtService.cs`, class `MtService`, provider methods for OpenAI, Google, DeepL, and Microsoft

### 4. Credential and Runtime Bootstrap

The AI pipeline is coupled to credential prompts and local runtime installation from within the subtitle workflow controller.

Evidence:

- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`, methods `EnsureOpenAiApiKeyAsync`, `EnsureTranslationProviderCredentialsAsync`, `EnsureLlamaCppRuntimeAsync`, `InstallLlamaCppRuntimeAsync`
- `src/BabelPlayer.App/LlamaCppRuntimeInstaller.cs`, class `LlamaCppRuntimeInstaller`

## Rendering Pipeline

### 1. Main Stage Rendering

The visible application shell is rendered by WinUI controls assembled in `MainWindow.BuildShell()`, with the player stage built in `BuildPlayerPane()`.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, methods `BuildShell`, `BuildPlayerPane`

### 2. Video Rendering

Video rendering itself is delegated to mpv inside a hosted native child window rather than to a WinUI media element.

Evidence:

- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`, methods `EnsureInitialized`, `UpdateHostBounds`
- `src/BabelPlayer.App/MpvPlaybackEngine.cs`, class `MpvPlaybackEngine`, method `InitializeAsync`

### 3. Subtitle Overlay Rendering

Subtitle presentation is projected into a separate overlay window, which is positioned relative to the player stage and updated independently of the main XAML tree.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, methods `UpdateSubtitleOverlay`, `UpdateSubtitleOverlayWindow`, `GetPlayerStageScreenBounds`
- `src/BabelPlayer.WinUI/SubtitleOverlayWindow.cs`, class `SubtitleOverlayWindow`, methods `ApplyStyle`, `ShowOverlay`, `PositionOverlay`, `EnsureWindow`

### 4. Fullscreen Control Overlay

Fullscreen controls are managed through another detached window, with visibility driven by input activity and timers.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, methods `EnsureFullscreenOverlayWindow`, `ShowFullscreenOverlay`, `HideFullscreenOverlay`, `FullscreenControlsTimer_Tick`
- `src/BabelPlayer.WinUI/FullscreenOverlayWindow.cs`, class `FullscreenOverlayWindow`, methods `ShowOverlay`, `PositionOverlay`, `EnsureWindow`

## Native Interop Points

### Win32 Windowing

The player host and overlay coordination depend on explicit Win32 APIs.

Evidence:

- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`, native imports and subclassing methods
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, methods `GetPlayerStageScreenBounds`, `IsAppStillForeground`

### WinUI/AppWindow Interop

Window-mode changes use `AppWindow` and presenter switching rather than remaining entirely inside XAML state.

Evidence:

- `src/BabelPlayer.WinUI/WinUIWindowModeService.cs`, class `WinUIWindowModeService`, methods `EnsureInitialStandardBounds`, `SetModeAsync`

### File Picker Interop

File picker services attach WinRT pickers to the native window handle.

Evidence:

- `src/BabelPlayer.WinUI/WinUIFilePickerService.cs`, class `WinUIFilePickerService`

### Platform Probing

Hardware capability detection uses system-level probes including WMI and native library loading.

Evidence:

- `src/BabelPlayer.Core/HardwareDetector.cs`, class `HardwareDetector`

## State and Persistence

Settings, resume state, credentials, and selected AI/runtime preferences are persisted in separate stores.

Evidence:

- `src/BabelPlayer.App/AppStateStore.cs`, class `AppStateStore`, methods for settings and resume persistence
- `src/BabelPlayer.App/SecureSettingsStore.cs`, class `SecureSettingsStore`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`, methods `SaveResumePosition`, `TryApplyResumePosition`
- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`, method `LoadPersistedSelections`

## Test Coverage Signals

Automated tests focus on the App/Core layers and on workflow behavior rather than UI shell rendering or native interop.

Evidence:

- `tests/BabelPlayer.App.Tests/AppLayerTests.cs`
- No dedicated WinUI UI test project is referenced from `BabelPlayer.sln`

## Likely Refactor Targets

### 1. `MainWindow`

Why it stands out:

- It acts as composition root, event hub, command handler, playback coordinator, overlay coordinator, drag-drop handler, resume manager, and window-mode controller.
- The file size indicates high responsibility concentration.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`

### 2. `SubtitleWorkflowController`

Why it stands out:

- It owns subtitle source selection, ASR orchestration, translation orchestration, provider credential UX, runtime installation, overlay snapshots, and persisted selection handling.

Evidence:

- `src/BabelPlayer.App/SubtitleWorkflowController.cs`, class `SubtitleWorkflowController`

### 3. `MtService`

Why it stands out:

- Provider implementations, local server lifecycle, prompt construction, and provider dispatch all live in one class.

Evidence:

- `src/BabelPlayer.Core/MtService.cs`, class `MtService`

### 4. Playback Abstraction Boundary

Why it stands out:

- The codebase defines playback abstractions, but the shell still depends on a concrete WinUI host and that host still depends on a concrete mpv engine.

Evidence:

- `src/BabelPlayer.Core/IPlaybackEngine.cs`, interface `IPlaybackEngine`
- `src/BabelPlayer.App/Interfaces.cs`, interface `IPlaybackHost`
- `src/BabelPlayer.WinUI/MpvHostControl.cs`, class `MpvHostControl`
- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`

### 5. Overlay and Window-Mode Coordination

Why it stands out:

- Responsibility is split across the main window, a window-mode service, and two detached overlay windows, which increases state-sync risk during fullscreen, PiP, and focus transitions.

Evidence:

- `src/BabelPlayer.WinUI/MainWindow.xaml.cs`, class `MainWindow`
- `src/BabelPlayer.WinUI/WinUIWindowModeService.cs`, class `WinUIWindowModeService`
- `src/BabelPlayer.WinUI/SubtitleOverlayWindow.cs`, class `SubtitleOverlayWindow`
- `src/BabelPlayer.WinUI/FullscreenOverlayWindow.cs`, class `FullscreenOverlayWindow`

## Architectural Characteristics

The current design is pragmatic and desktop-oriented:

- mpv is treated as an external rendering engine controlled over IPC.
- AI features are first-class application features, not add-ons.
- Overlay rendering intentionally leaves the main XAML visual tree for tighter control over subtitle and fullscreen presentation.
- Runtime installation is part of the app experience for both playback and local AI paths.

Those choices are coherent for a Windows-first media app, but they also explain the current coupling: shell logic, runtime bootstrap, and workflow orchestration are all tightly connected because the application optimizes for end-to-end control instead of strict separation.
