using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow
{
    private async void PreviousTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaybackItemAsync(_queueCommands.MovePrevious());
    }

    private async void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        await LoadPlaybackItemAsync(_queueCommands.MoveNext());
    }

    private void SeekBack_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(-10)));
    }

    private void SeekForward_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(10)));
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        if (ViewModel.Transport.IsPaused)
        {
            FireAndForget(_shellPlaybackCommands.PlayAsync());
            ShowStatus("Playback resumed.");
            return;
        }

        FireAndForget(_shellPlaybackCommands.PauseAsync());
        ShowStatus("Playback paused.");
    }

    private void PreviousFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.StepFrameAsync(forward: false));
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to previous frame.");
    }

    private void NextFrame_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.StepFrameAsync(forward: true));
        ViewModel.Transport.IsPaused = true;
        ShowStatus("Stepped to next frame.");
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPositionSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        FireAndForget(_shellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(e.NewValue)));
        if (_isPositionScrubbing)
        {
            UpdateScrubTimeLabels(e.NewValue, PositionSlider.Maximum);
        }
    }

    private void FullscreenPositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (FullscreenPositionSlider is null || _suppressFullscreenSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.5)
        {
            return;
        }

        RegisterFullscreenOverlayInteraction();
        FireAndForget(_shellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(e.NewValue)));
        UpdateScrubTimeLabels(e.NewValue, FullscreenPositionSlider.Maximum);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolumeSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.001)
        {
            return;
        }

        var normalizedVolume = Math.Clamp(e.NewValue / 100d, 0, 1);
        ViewModel.Transport.Volume = normalizedVolume;
        FireAndForget(ApplyAudioStatePreferenceAsync(normalizedVolume, MuteToggleButton?.IsChecked == true));
        if (!_isInitializingShellState)
        {
            UpdateMuteButtonVisual();
        }
    }

    private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var isMuted = MuteToggleButton.IsChecked == true;
        ViewModel.Transport.IsMuted = isMuted;
        var normalizedVolume = Math.Clamp((VolumeSlider?.Value ?? (ViewModel.Transport.Volume * 100d)) / 100d, 0, 1);
        FireAndForget(ApplyAudioStatePreferenceAsync(normalizedVolume, isMuted));
        UpdateMuteButtonVisual();
    }

    private void PlaybackRateFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: double rate })
        {
            return;
        }

        SetPlaybackRate(rate);
    }

    private void PlaybackSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressPlaybackSpeedSliderChanges || Math.Abs(e.NewValue - e.OldValue) < 0.01)
        {
            return;
        }

        SetPlaybackRate(e.NewValue);
    }

    private void SetPlaybackRate(double speed, bool persistSettings = true, bool showStatus = true)
    {
        var clamped = Math.Clamp(speed, 0.25, 2.0);
        var change = new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            clamped,
            ViewModel.Settings.AudioDelaySeconds,
            ViewModel.Settings.SubtitleDelaySeconds,
            ViewModel.Settings.AspectRatio);
        ViewModel.Transport.PlaybackRate = clamped;
        FireAndForget(persistSettings && !_isInitializingShellState
            ? ApplyPlaybackDefaultsPreferenceAsync(change)
            : _shellPlaybackCommands.ApplyPlaybackDefaultsAsync(change));
        UpdatePlaybackRateFlyoutChecks();
        SyncPlaybackSpeedSlider(clamped);

        if (showStatus)
        {
            ShowStatus($"Playback speed: {clamped:0.00}x");
        }
    }

    private void PlayerHost_MediaOpened(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            _logger.LogInfo(
                "Player host media opened.",
                BabelLogContext.Create(
                    ("path", snapshot.Path),
                    ("duration", snapshot.Duration),
                    ("videoWidth", snapshot.VideoWidth),
                    ("videoHeight", snapshot.VideoHeight),
                    ("displayWidth", snapshot.VideoDisplayWidth),
                    ("displayHeight", snapshot.VideoDisplayHeight)));
            PlayerHost.RequestHostBoundsSync();
            UpdateWindowHeader();
            var result = await _shellPlaybackCommands.HandleMediaOpenedAsync(
                snapshot,
                ViewModel.Settings);
            ShowStatus(result.StatusMessage);
        });
    }

    private void PlayerHost_MediaEnded(PlaybackStateSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            _logger.LogInfo("Player host media ended.", BabelLogContext.Create(("path", snapshot.Path)));
            var result = _shellPlaybackCommands.HandleMediaEnded(
                ViewModel.Settings.ResumeEnabled,
                ViewModel.Settings.AutoPlayNextInQueue);
            if (result.NextItem is null)
            {
                ShowStatus(result.StatusMessage);
                return;
            }

            await LoadPlaybackItemAsync(result.NextItem, ShellMediaOpenTrigger.Autoplay);
        });
    }

    private void PlayerHost_MediaFailed(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _logger.LogError("Player host media failed.", null, BabelLogContext.Create(("message", message), ("path", _shellPlaybackCommands.CurrentPlaybackSnapshot.Path)));
            ShowStatus(message, true);
        });
    }

    private void PlayerHost_RuntimeInstallProgress(RuntimeInstallProgress progress)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus($"Runtime setup: {progress.Stage}."));
    }

    private void PlayerHost_InputActivity()
    {
        void showOverlay()
        {
            ShowFullscreenOverlay();
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            showOverlay();
            return;
        }

        DispatcherQueue.TryEnqueue(showOverlay);
    }

    private void PlayerHost_FullscreenExitRequested()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
            {
                await ExitFullscreenAsync();
            }
        });
    }

    private void ShellProjectionService_ProjectionChanged(ShellProjectionSnapshot projection)
    {
        DispatcherQueue.TryEnqueue(() => ApplyShellProjection(projection));
    }

    private void ApplyShellProjection(ShellProjectionSnapshot projection)
    {
        _currentShellProjection = projection;
        ViewModel.Transport.PositionSeconds = projection.Transport.PositionSeconds;
        ViewModel.Transport.DurationSeconds = projection.Transport.DurationSeconds;
        ViewModel.Transport.CurrentTimeText = projection.Transport.CurrentTimeText;
        ViewModel.Transport.DurationText = projection.Transport.DurationText;
        ViewModel.Transport.IsPaused = projection.Transport.IsPaused;
        ViewModel.Transport.IsMuted = projection.Transport.IsMuted;
        ViewModel.Transport.Volume = projection.Transport.Volume;
        ViewModel.Transport.PlaybackRate = projection.Transport.PlaybackRate;
        ViewModel.ActiveHardwareDecoder = projection.Transport.ActiveHardwareDecoder;
        HardwareDecoderTextBlock.Text = ViewModel.ActiveHardwareDecoder;

        ViewModel.SubtitleOverlay.StatusText = projection.Subtitle.StatusText;
        ViewModel.SubtitleOverlay.SubtitleSource = projection.Subtitle.Source;
        ViewModel.SubtitleOverlay.IsCaptionGenerationInProgress = projection.Subtitle.IsCaptionGenerationInProgress;
        ViewModel.SubtitleOverlay.IsTranslationEnabled = projection.Subtitle.IsTranslationEnabled;
        ViewModel.SubtitleOverlay.IsAutoTranslateEnabled = projection.Subtitle.IsAutoTranslateEnabled;

        UpdatePositionSurfaces(
            TimeSpan.FromSeconds(projection.Transport.PositionSeconds),
            TimeSpan.FromSeconds(projection.Transport.DurationSeconds));

        UpdateTrackProjection(projection.SelectedTracks.Tracks);
        UpdateSubtitleVisibility();
        UpdatePlayPauseButtonVisual();
        UpdateOverlayControlState();
        CurrentTimeTextBlock.Text = ViewModel.Transport.CurrentTimeText;
        DurationTextBlock.Text = ViewModel.Transport.DurationText;
        _suppressVolumeSliderChanges = true;
        VolumeSlider.Value = ViewModel.Transport.Volume * 100d;
        _suppressVolumeSliderChanges = false;
        MuteToggleButton.IsChecked = ViewModel.Transport.IsMuted;
        UpdateMuteButtonVisual();
        UpdatePlaybackDiagnostics(projection.Transport);
        UpdatePortraitVideoLanguageToolsState();
        TryApplyStandardAutoFit();
    }

    private void UpdateTrackProjection(IReadOnlyList<MediaTrackInfo> tracks)
    {
        if (HasEquivalentTrackProjection(_currentTracks, tracks))
        {
            return;
        }

        _currentTracks.Clear();
        _currentTracks.AddRange(tracks.Select(track => new MediaTrackInfo
        {
            Id = track.Id,
            FfIndex = track.FfIndex,
            Kind = track.Kind,
            Title = track.Title,
            Language = track.Language,
            Codec = track.Codec,
            IsEmbedded = track.IsEmbedded,
            IsSelected = track.IsSelected,
            IsTextBased = track.IsTextBased
        }));
        RebuildAudioTrackFlyout();
        RebuildEmbeddedSubtitleTrackFlyout();
    }

    private static bool HasEquivalentTrackProjection(IReadOnlyList<MediaTrackInfo> current, IReadOnlyList<MediaTrackInfo> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = next[index];
            if (left.Id != right.Id
                || left.Kind != right.Kind
                || left.IsSelected != right.IsSelected
                || !string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                || !string.Equals(left.Language, right.Language, StringComparison.Ordinal)
                || !string.Equals(left.Codec, right.Codec, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateAspectRatioFlyoutChecks()
    {
        UpdateAspectRatioFlyoutChecks(AspectRatioFlyoutSubItem);
    }

    private void UpdateAspectRatioFlyoutChecks(MenuFlyoutSubItem? aspectRatioFlyoutSubItem)
    {
        ApplyToggleFlyoutChecks(
            aspectRatioFlyoutSubItem,
            item => string.Equals(item.Tag as string, ViewModel.Settings.AspectRatio, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdatePlaybackRateFlyoutChecks()
    {
        UpdatePlaybackRateFlyoutChecks(PlaybackRateFlyoutSubItem);
    }

    private void UpdatePlaybackRateFlyoutChecks(MenuFlyoutSubItem? playbackRateFlyoutSubItem)
    {
        ApplyToggleFlyoutChecks(
            playbackRateFlyoutSubItem,
            item => item.Tag is double rate && Math.Abs(rate - ViewModel.Transport.PlaybackRate) < 0.001);

        if (playbackRateFlyoutSubItem is not null)
        {
            playbackRateFlyoutSubItem.Text = $"Playback Rate ({ViewModel.Transport.PlaybackRate:0.00}x)";
        }
    }

    private void UpdateHardwareDecodingFlyoutChecks()
    {
        UpdateHardwareDecodingFlyoutChecks(HardwareDecodingFlyoutSubItem);
    }

    private void UpdateHardwareDecodingFlyoutChecks(MenuFlyoutSubItem? hardwareDecodingFlyoutSubItem)
    {
        ApplyToggleFlyoutChecks(
            hardwareDecodingFlyoutSubItem,
            item => item.Tag is HardwareDecodingMode mode && mode == ViewModel.Settings.HardwareDecodingMode);

        if (hardwareDecodingFlyoutSubItem is not null)
        {
            hardwareDecodingFlyoutSubItem.Text = $"Hardware Decode ({FormatHardwareDecodingLabel(ViewModel.Settings.HardwareDecodingMode)})";
        }
    }

    private static void ApplyToggleFlyoutChecks(MenuFlyoutSubItem? flyoutSubItem, Func<ToggleMenuFlyoutItem, bool> checkSelector)
    {
        if (flyoutSubItem is null)
        {
            return;
        }

        foreach (var item in flyoutSubItem.Items.OfType<ToggleMenuFlyoutItem>())
        {
            item.IsChecked = checkSelector(item);
        }
    }

    private void UpdateDelayFlyoutLabels()
    {
        UpdateDelayFlyoutLabels(SubtitleDelayFlyoutSubItem, AudioDelayFlyoutSubItem);
    }

    private void UpdateDelayFlyoutLabels(MenuFlyoutSubItem? subtitleDelayFlyoutSubItem, MenuFlyoutSubItem? audioDelayFlyoutSubItem)
    {
        if (subtitleDelayFlyoutSubItem is not null)
        {
            subtitleDelayFlyoutSubItem.Text = $"Subtitle Delay ({ViewModel.Settings.SubtitleDelaySeconds:+0.00;-0.00;0.00}s)";
        }

        if (audioDelayFlyoutSubItem is not null)
        {
            audioDelayFlyoutSubItem.Text = $"Audio Delay ({ViewModel.Settings.AudioDelaySeconds:+0.00;-0.00;0.00}s)";
        }
    }

    private static string FormatHardwareDecodingLabel(HardwareDecodingMode mode)
    {
        return mode switch
        {
            HardwareDecodingMode.AutoSafe => "Auto Safe",
            HardwareDecodingMode.D3D11 => "D3D11",
            HardwareDecodingMode.Nvdec => "NVDEC",
            HardwareDecodingMode.Software => "Software",
            _ => mode.ToString()
        };
    }

    private void AspectRatioFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string aspectRatio })
        {
            return;
        }

        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            ViewModel.Settings.AudioDelaySeconds,
            ViewModel.Settings.SubtitleDelaySeconds,
            aspectRatio)));
        UpdateAspectRatioFlyoutChecks();
        ShowStatus($"Aspect ratio: {(aspectRatio == "-1" ? "fill" : aspectRatio)}.");
    }

    private void HardwareDecodingFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: HardwareDecodingMode mode })
        {
            return;
        }

        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            mode,
            ViewModel.Transport.PlaybackRate,
            ViewModel.Settings.AudioDelaySeconds,
            ViewModel.Settings.SubtitleDelaySeconds,
            ViewModel.Settings.AspectRatio)));
        UpdateHardwareDecodingFlyoutChecks();
        ShowStatus($"Hardware decode: {FormatHardwareDecodingLabel(mode)}.");
    }

    private void AdjustSubtitleDelay(double delta)
    {
        var updatedDelay = ViewModel.Settings.SubtitleDelaySeconds + delta;
        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            ViewModel.Settings.AudioDelaySeconds,
            updatedDelay,
            ViewModel.Settings.AspectRatio)));
        UpdateDelayFlyoutLabels();
        SyncSubtitleDelayValueText();
        ShowStatus($"Subtitle delay: {updatedDelay:+0.00;-0.00;0.00}s");
    }

    private void ResetSubtitleDelay()
    {
        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            ViewModel.Settings.AudioDelaySeconds,
            0,
            ViewModel.Settings.AspectRatio)));
        UpdateDelayFlyoutLabels();
        SyncSubtitleDelayValueText();
        ShowStatus("Subtitle delay reset.");
    }

    private void AdjustAudioDelay(double delta)
    {
        var updatedDelay = ViewModel.Settings.AudioDelaySeconds + delta;
        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            updatedDelay,
            ViewModel.Settings.SubtitleDelaySeconds,
            ViewModel.Settings.AspectRatio)));
        UpdateDelayFlyoutLabels();
        ShowStatus($"Audio delay: {updatedDelay:+0.00;-0.00;0.00}s");
    }

    private void ResetAudioDelay()
    {
        FireAndForget(ApplyPlaybackDefaultsPreferenceAsync(new ShellPlaybackDefaultsChange(
            ViewModel.Settings.HardwareDecodingMode,
            ViewModel.Transport.PlaybackRate,
            0,
            ViewModel.Settings.SubtitleDelaySeconds,
            ViewModel.Settings.AspectRatio)));
        UpdateDelayFlyoutLabels();
        ShowStatus("Audio delay reset.");
    }

    private void SubtitleDelayBack_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(-0.05);

    private void SubtitleDelayForward_Click(object sender, RoutedEventArgs e) => AdjustSubtitleDelay(0.05);

    private void ResetSubtitleDelay_Click(object sender, RoutedEventArgs e) => ResetSubtitleDelay();

    private void SyncSubtitleDelayValueText()
    {
        if (SubtitleDelayValueText is not null)
        {
            SubtitleDelayValueText.Text = $"{ViewModel.Settings.SubtitleDelaySeconds:+0.00;-0.00;0.00}s";
        }
    }

    private void SyncPlaybackSpeedSlider(double speed)
    {
        if (PlaybackSpeedSlider is null)
        {
            return;
        }

        _suppressPlaybackSpeedSliderChanges = true;
        PlaybackSpeedSlider.Value = speed;
        _suppressPlaybackSpeedSliderChanges = false;
        if (PlaybackSpeedValueText is not null)
        {
            PlaybackSpeedValueText.Text = $"{speed:0.00}x";
        }
    }

    private void AudioDelayBack_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(-0.05);

    private void AudioDelayForward_Click(object sender, RoutedEventArgs e) => AdjustAudioDelay(0.05);

    private void ResetAudioDelay_Click(object sender, RoutedEventArgs e) => ResetAudioDelay();

    private void ResumePlaybackToggleItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item)
        {
            return;
        }

        var result = _shellPreferenceCommands.ApplyResumeEnabledChange(item.IsChecked);
        ApplyPreferencesSnapshot(result.UpdatedPreferences);
        if (ResumePlaybackToggleItem is not null && !ReferenceEquals(item, ResumePlaybackToggleItem))
        {
            ResumePlaybackToggleItem.IsChecked = item.IsChecked;
        }

        ShowStatus(item.IsChecked ? "Resume playback enabled." : "Resume playback disabled.");
    }

    private async Task LoadPlaybackItemAsync(PlaylistItem? item, ShellMediaOpenTrigger openTrigger = ShellMediaOpenTrigger.Manual)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return;
        }

        _isLanguageToolsAutoCollapseOverridden = false;
        _pendingAutoFitPath = item.Path;
        _lastAutoFitSignature = null;
        try
        {
            var loaded = await _shellPlaybackCommands.LoadPlaybackItemAsync(
                item,
                BuildShellLoadOptions(openTrigger),
                CancellationToken.None);
            if (!loaded)
            {
                return;
            }

            PlayerHost.RequestHostBoundsSync();
            UpdateWindowHeader();
        }
        catch (Exception ex)
        {
            _logger.LogError("Window-level media load failed.", ex, BabelLogContext.Create(("path", item.Path), ("displayName", item.DisplayName)));
            ShowStatus(ex.Message, true);
        }
    }

    private ShellLoadMediaOptions BuildShellLoadOptions(ShellMediaOpenTrigger openTrigger)
    {
        var volume = Math.Clamp((VolumeSlider?.Value ?? (ViewModel.Transport.Volume * 100d)) / 100d, 0, 1);
        return new ShellLoadMediaOptions
        {
            HardwareDecodingMode = ViewModel.Settings.HardwareDecodingMode,
            PlaybackRate = ViewModel.Transport.PlaybackRate,
            AspectRatio = ViewModel.Settings.AspectRatio,
            AudioDelaySeconds = ViewModel.Settings.AudioDelaySeconds,
            SubtitleDelaySeconds = ViewModel.Settings.SubtitleDelaySeconds,
            Volume = volume,
            IsMuted = MuteToggleButton?.IsChecked == true,
            ResumeEnabled = ViewModel.Settings.ResumeEnabled,
            OpenTrigger = openTrigger,
            PreviousPlaybackState = _shellPlaybackCommands.CurrentPlaybackSnapshot
        };
    }

    private void AttachScrubberHandlers(Slider slider)
    {
        slider.PointerPressed += Scrubber_PointerPressed;
        slider.PointerReleased += Scrubber_PointerReleased;
        slider.PointerCaptureLost += Scrubber_PointerCaptureLost;
    }

    private void Scrubber_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _isPositionScrubbing = true;
        _activeScrubber = sender as Slider;
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            EnsureStageOverlayControls();
            _stageCoordinator.HandleScrubbingChanged(true);
            ShowFullscreenOverlay();
        }
    }

    private void Scrubber_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        EndScrubbing(sender);
    }

    private void Scrubber_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        EndScrubbing(sender);
    }

    private void EndScrubbing(object sender)
    {
        if (sender is Slider slider)
        {
            FireAndForget(_shellPlaybackCommands.SeekAsync(TimeSpan.FromSeconds(slider.Value)));
        }

        _isPositionScrubbing = false;
        _activeScrubber = null;
        if (_windowModeService.CurrentMode == PlaybackWindowMode.Fullscreen)
        {
            _stageCoordinator.HandleScrubbingChanged(false);
            ShowFullscreenOverlay();
        }
    }

    private void UpdatePositionSurfaces(TimeSpan position, TimeSpan duration)
    {
        var maximumSeconds = Math.Max(duration.TotalSeconds, 1);
        if (!_isPositionScrubbing)
        {
            _suppressPositionSliderChanges = true;
            PositionSlider.Maximum = maximumSeconds;
            PositionSlider.Value = Math.Min(position.TotalSeconds, maximumSeconds);
            _suppressPositionSliderChanges = false;

            if (FullscreenPositionSlider is not null)
            {
                _suppressFullscreenSliderChanges = true;
                FullscreenPositionSlider.Maximum = maximumSeconds;
                FullscreenPositionSlider.Value = Math.Min(position.TotalSeconds, maximumSeconds);
                _suppressFullscreenSliderChanges = false;
            }
        }

        UpdateScrubTimeLabels(_isPositionScrubbing ? _activeScrubber?.Value ?? position.TotalSeconds : position.TotalSeconds, maximumSeconds);
    }

    private void UpdateScrubTimeLabels(double currentSeconds, double totalSeconds)
    {
        if (FullscreenCurrentTimeTextBlock is null || FullscreenDurationTextBlock is null)
        {
            return;
        }

        var current = TimeSpan.FromSeconds(Math.Max(currentSeconds, 0));
        var total = TimeSpan.FromSeconds(Math.Max(totalSeconds, 0));
        FullscreenCurrentTimeTextBlock.Text = FormatPlaybackClock(current);
        FullscreenDurationTextBlock.Text = total > TimeSpan.Zero ? FormatPlaybackClock(total) : "00:00";
    }

    private static string FormatPlaybackClock(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"mm\:ss");
    }
}
