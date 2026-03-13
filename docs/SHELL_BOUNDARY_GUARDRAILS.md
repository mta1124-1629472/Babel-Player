# Shell Boundary Guardrails

This document defines the non-negotiable rules for keeping WinUI shell code thin in Babel-Player.

## Purpose

The shell exists to:
- capture UI events
- forward intents into App-layer interfaces
- render immutable snapshots and projections
- coordinate presentation-only concerns such as layout, focus, and control state

The shell must not:
- decide application workflow
- persist preferences directly
- own business state
- reference concrete App workflow services when a shell interface exists

## Required Flow

All shell features should follow this flow:

`UI event -> shell interface call -> App workflow/policy -> immutable result or snapshot -> shell render`

If a MainWindow method decides what should happen next in the application, it belongs in `BabelPlayer.App`, not `BabelPlayer.WinUI`.

## MainWindow Rules

Files under `src/BabelPlayer.WinUI/MainWindow*.cs` may contain only:
- event handlers
- `DispatcherQueue` marshaling
- calls to shell-facing App interfaces
- snapshot application
- presenter updates
- visual/layout helpers

Files under `src/BabelPlayer.WinUI/MainWindow*.cs` must not contain:
- direct calls to preference mutation methods on `IShellPreferencesService`
- references to concrete workflow services such as `LibraryBrowserService`, `SubtitleWorkflowController`, `SettingsFacade`, `CredentialFacade`, or `ShortcutService`
- static model/catalog lookups that encode workflow decisions in WinUI
- shell-owned fields that mirror authoritative workflow state

## Preference Ownership

Preference writes must stay behind App-owned seams.

Use:
- `IShellPreferenceCommands` for layout, playback default, audio, subtitle presentation, shortcut profile, and resume preference mutations

Do not use from MainWindow partials:
- `IShellPreferencesService.ApplyLayoutChange(...)`
- `IShellPreferencesService.ApplyPlaybackDefaultsChange(...)`
- `IShellPreferencesService.ApplyAudioStateChange(...)`
- `IShellPreferencesService.ApplySubtitlePresentationChange(...)`
- `IShellPreferencesService.ApplyResumeEnabledChange(...)`

Reading the current snapshot through `IShellPreferencesService.Current` is allowed.

## Concrete Type Boundary

WinUI should depend on shell-facing interfaces and immutable projections.

Preferred shell-facing seams include:
- `IShellLibraryService`
- `IQueueProjectionReader`
- `IQueueCommands`
- `IShellPlaybackCommands`
- `IShellPreferenceCommands`
- `ICredentialSetupService`
- `IShortcutProfileService`
- `IShortcutCommandExecutor`
- `ISubtitleWorkflowShellService`

Concrete implementations should be created only in the composition root.

## Review Checklist

When a PR touches `MainWindow*.cs`, check all of the following:
- Does WinUI only call shell interfaces?
- Did any new preference mutation appear outside `IShellPreferenceCommands`?
- Did any new workflow state field appear in MainWindow?
- Did any new branch or switch in WinUI decide application behavior instead of presentation?
- Was a source-scan or seam test updated when a new boundary rule was introduced?

## Required Reading

Read this document before:
- refactoring `MainWindow.xaml.cs` or any `MainWindow*.cs` partial
- adding new shell commands or settings behavior
- writing tests that enforce shell/App boundaries

Related documents:
- `AGENTS.md`
- `.github/copilot-instructions.md`
- `docs/ARCHITECTURE.md`
- `docs/DEVELOPMENT_RULES.md`
- `docs/MODULE_MAP.md`