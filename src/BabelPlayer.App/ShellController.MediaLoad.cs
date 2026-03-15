using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Media load lifecycle: <see cref="LoadPlaybackItemAsync"/>,
/// <see cref="HandleMediaOpenedAsync"/>, and <see cref="HandleMediaEnded"/>.
/// Owns pending-context tracking and applies playback defaults on open.
/// </summary>
public sealed partial class ShellController
{
    public async Task<bool> LoadPlaybackItemAsync(
        ShellPlaylistItem? item,
        ShellLoadMediaOptions options,
        CancellationToken cancellationToken)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            _logger.LogWarning("Load playback item skipped — item missing.", null,
                BabelLogContext.Create(("path", item?.Path)));
            return false;
        }

        var opId = $"media-{Guid.NewGuid():N}";
        _logger.LogInfo("Media load starting.",
            BabelLogContext.Create(
                ("operationId",  opId),
                ("path",         item.Path),
                ("displayName",  item.DisplayName)));

        ResetCaptionStartupGate();
        _resumeTrackingCoordinator.SetEnabled(options.ResumeEnabled);
        if (options.ResumeEnabled)
            _resumeTrackingCoordinator.Flush();
        _resumeTrackingCoordinator.ResetForMedia(item.Path);
        ClearPendingResumeDecision();
        SetPendingMediaOpenContext(item.Path, options.ResumeEnabled, options.OpenTrigger);

        try
        {
            await _playbackBackend.LoadAsync(item.Path, cancellationToken);
            await _playbackBackend.SetHardwareDecodingModeAsync(options.HardwareDecodingMode.ToCore(), cancellationToken);
            await _playbackBackend.SetPlaybackRateAsync(options.PlaybackRate, cancellationToken);
            await _playbackBackend.SetAspectRatioAsync(options.AspectRatio, cancellationToken);
            await _playbackBackend.SetAudioDelayAsync(options.AudioDelaySeconds, cancellationToken);
            await _playbackBackend.SetSubtitleDelayAsync(options.SubtitleDelaySeconds, cancellationToken);
            await _playbackBackend.SetZoomAsync(0, cancellationToken);
            await _playbackBackend.SetPanAsync(0, 0, cancellationToken);
            await _playbackBackend.SetVolumeAsync(options.Volume, cancellationToken);
            await _playbackBackend.SetMuteAsync(options.IsMuted, cancellationToken);
            await _subtitleWorkflowController.LoadMediaSubtitlesAsync(item.Path, cancellationToken);

            _logger.LogInfo("Media load completed.",
                BabelLogContext.Create(("operationId", opId), ("path", item.Path)));
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ClearPendingMediaOpenContext(item.Path);
            _logger.LogError("Media load failed.", ex,
                BabelLogContext.Create(
                    ("operationId", opId),
                    ("path",        item.Path),
                    ("displayName", item.DisplayName)));
            throw;
        }
    }

    public async Task<ShellPlaybackOpenResult> HandleMediaOpenedAsync(
        ShellPlaybackStateSnapshot snapshot,
        ShellPreferencesSnapshot preferences,
        CancellationToken cancellationToken = default)
    {
        var current       = _playbackQueueController.NowPlayingItem;
        var pendingCtx    = ConsumePendingMediaOpenContext(snapshot.Path);
        var trigger       = pendingCtx?.OpenTrigger ?? ShellMediaOpenTrigger.Manual;
        var resumeEnabled = pendingCtx?.ResumeEnabled ?? preferences.ResumeEnabled;

        var result = new ShellPlaybackOpenResult
        {
            OpenTrigger   = trigger,
            StatusMessage = BuildOpenedStatusMessage(current, trigger)
        };

        await ApplyPlaybackDefaultsAsync(new ShellPlaybackDefaultsChange(
            preferences.HardwareDecodingMode,
            preferences.PlaybackRate,
            preferences.AudioDelaySeconds,
            preferences.SubtitleDelaySeconds,
            preferences.AspectRatio), cancellationToken);

        _resumeTrackingCoordinator.SetEnabled(resumeEnabled);
        _resumeTrackingCoordinator.ResetForMedia(snapshot.Path);

        if (!resumeEnabled)
        {
            ClearPendingResumeDecision();
            return result;
        }

        var entry = _resumePlaybackService.FindEntry(snapshot.Path, snapshot.Duration);
        if (entry is null)
        {
            ClearPendingResumeDecision();
            return result;
        }

        var pos = TimeSpan.FromSeconds(
            Math.Clamp(entry.PositionSeconds, 0, snapshot.Duration.TotalSeconds));

        if (trigger != ShellMediaOpenTrigger.Autoplay)
        {
            SetPendingResumeDecision(entry.Path, pos);
            _logger.LogInfo("Resume decision pending.",
                BabelLogContext.Create(("path", snapshot.Path), ("pos", pos)));
            return result with
            {
                ResumePosition        = pos,
                ResumeDecisionPending = true,
                StatusMessage         = BuildResumePromptStatusMessage(current, pos)
            };
        }

        ClearPendingResumeDecision();
        await _playbackBackend.SeekAsync(pos, cancellationToken);
        _logger.LogInfo("Resume position applied.",
            BabelLogContext.Create(("path", snapshot.Path), ("pos", pos)));
        return result with
        {
            ResumePosition = pos,
            StatusMessage  = BuildResumedStatusMessage(current, trigger, pos)
        };
    }

    public ShellMediaEndedResult HandleMediaEnded(
        bool resumeEnabled,
        bool autoPlayNextInQueue = true)
    {
        ClearPendingResumeDecision();
        _resumeTrackingCoordinator.SetEnabled(resumeEnabled);
        if (resumeEnabled)
            _resumeTrackingCoordinator.Flush(forceRemoveCompleted: true);

        ResetCaptionStartupGate();

        var next = autoPlayNextInQueue
            ? _playbackQueueController.AdvanceAfterMediaEnded()
            : null;

        _logger.LogInfo("Handled media end.",
            BabelLogContext.Create(
                ("nextPath",            next?.Path),
                ("resumeEnabled",       resumeEnabled),
                ("autoPlayNextInQueue", autoPlayNextInQueue)));

        return new ShellMediaEndedResult
        {
            NextItem      = next?.ToShell(),
            StatusMessage = next is null
                ? autoPlayNextInQueue
                    ? "Playback ended."
                    : "Playback ended. Up Next is ready when you are."
                : $"Now playing {next.DisplayName}."
        };
    }

    public void SetResumeTrackingEnabled(bool enabled) =>
        _resumeTrackingCoordinator.SetEnabled(enabled);

    public void ClearResumeHistory() =>
        _resumePlaybackService.Clear();

    public void FlushResumeTracking(bool forceRemoveCompleted = false) =>
        _resumeTrackingCoordinator.Flush(forceRemoveCompleted);

    private static string BuildOpenedStatusMessage(Core.PlaylistItem? item, ShellMediaOpenTrigger trigger) =>
        item is null ? "Media opened."
            : trigger == ShellMediaOpenTrigger.Autoplay
                ? $"Autoplaying {item.DisplayName}."
                : $"Now playing {item.DisplayName}.";

    private static string BuildResumedStatusMessage(
        Core.PlaylistItem? item, ShellMediaOpenTrigger trigger, TimeSpan pos) =>
        item is null
            ? trigger == ShellMediaOpenTrigger.Autoplay
                ? $"Autoplay resumed at {pos:mm\\:ss}."
                : $"Resumed at {pos:mm\\:ss}."
            : trigger == ShellMediaOpenTrigger.Autoplay
                ? $"Autoplay resumed {item.DisplayName} at {pos:mm\\:ss}."
                : $"Resumed {item.DisplayName} at {pos:mm\\:ss}.";

    private static string BuildResumePromptStatusMessage(Core.PlaylistItem? item, TimeSpan pos) =>
        item is null
            ? $"Resume available at {pos:mm\\:ss}."
            : $"Resume available for {item.DisplayName} at {pos:mm\\:ss}.";
}
