using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Direct playback commands: play, pause, seek, volume, track selection,
/// A/V sync delays, aspect ratio, hardware decoding.
/// All methods are thin pass-throughs to <see cref="IPlaybackBackend"/>.
/// </summary>
public sealed partial class ShellController
{
    public Task PlayAsync(CancellationToken cancellationToken = default)
        => _playbackBackend.PlayAsync(cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default)
        => _playbackBackend.PauseAsync(cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
        => _playbackBackend.SeekAsync(position, cancellationToken);

    public Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken = default)
        => _playbackBackend.SeekRelativeAsync(delta, cancellationToken);

    public Task StepFrameAsync(bool forward, CancellationToken cancellationToken = default)
        => _playbackBackend.StepFrameAsync(forward, cancellationToken);

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
        => _playbackBackend.SetVolumeAsync(volume, cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default)
        => _playbackBackend.SetMuteAsync(muted, cancellationToken);

    public async Task ApplyAudioPreferencesAsync(
        double volume, bool muted, CancellationToken cancellationToken = default)
    {
        await _playbackBackend.SetVolumeAsync(volume, cancellationToken);
        await _playbackBackend.SetMuteAsync(muted, cancellationToken);
    }

    public Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken = default)
        => _playbackBackend.SetPlaybackRateAsync(speed, cancellationToken);

    public Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAudioTrackAsync(trackId, cancellationToken);

    public Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken = default)
        => _playbackBackend.SetSubtitleTrackAsync(trackId, cancellationToken);

    public Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAudioDelayAsync(seconds, cancellationToken);

    public Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken = default)
        => _playbackBackend.SetSubtitleDelayAsync(seconds, cancellationToken);

    public Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken = default)
        => _playbackBackend.SetAspectRatioAsync(aspectRatio, cancellationToken);

    public Task SetHardwareDecodingModeAsync(
        ShellHardwareDecodingMode mode, CancellationToken cancellationToken = default)
        => _playbackBackend.SetHardwareDecodingModeAsync(mode.ToCore(), cancellationToken);

    public async Task ApplyPlaybackDefaultsAsync(
        ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _playbackBackend.SetHardwareDecodingModeAsync(change.HardwareDecodingMode.ToCore(), cancellationToken);
        await _playbackBackend.SetPlaybackRateAsync(change.PlaybackRate, cancellationToken);
        await _playbackBackend.SetAspectRatioAsync(change.AspectRatio, cancellationToken);
        await _playbackBackend.SetAudioDelayAsync(change.AudioDelaySeconds, cancellationToken);
        await _playbackBackend.SetSubtitleDelayAsync(change.SubtitleDelaySeconds, cancellationToken);
    }

    public async Task<ShellSubtitleTrackSelectionResult> SelectEmbeddedSubtitleTrackAsync(
        string? currentPath,
        SubtitlePipelineSource currentSubtitleSource,
        ShellMediaTrack? track,
        CancellationToken cancellationToken = default)
    {
        if (track is null)
        {
            await _playbackBackend.SetSubtitleTrackAsync(null, cancellationToken);
            if (currentSubtitleSource == SubtitlePipelineSource.EmbeddedTrack &&
                !string.IsNullOrWhiteSpace(currentPath))
                await _subtitleWorkflowController.LoadMediaSubtitlesAsync(currentPath, cancellationToken);

            _logger.LogInfo("Embedded subtitle track disabled.",
                BabelLogContext.Create(("path", currentPath), ("subtitleSource", currentSubtitleSource.ToString())));
            return new ShellSubtitleTrackSelectionResult
            {
                TrackSelectionChanged = true,
                StatusMessage = "Embedded subtitle track disabled."
            };
        }

        if (track.IsTextBased)
        {
            if (string.IsNullOrWhiteSpace(currentPath))
                return new ShellSubtitleTrackSelectionResult
                {
                    StatusMessage = "Open a video first.",
                    IsError = true
                };

            await _playbackBackend.SetSubtitleTrackAsync(null, cancellationToken);
            var loadResult = await _subtitleWorkflowController
                .ImportEmbeddedSubtitleTrackAsync(currentPath, track.ToCore(), cancellationToken);
            var imported = loadResult.CueCount > 0;

            _logger.LogInfo("Embedded text subtitle track imported.",
                BabelLogContext.Create(
                    ("path",     currentPath),
                    ("trackId",  track.Id),
                    ("cueCount", loadResult.CueCount)));

            return new ShellSubtitleTrackSelectionResult
            {
                TrackSelectionChanged = true,
                StatusMessage = imported
                    ? $"Imported embedded subtitle track {track.Id}."
                    : "Embedded subtitle import failed.",
                IsError = !imported
            };
        }

        await _playbackBackend.SetSubtitleTrackAsync(track.Id, cancellationToken);
        _logger.LogInfo("Image subtitle track selected.",
            BabelLogContext.Create(("path", currentPath), ("trackId", track.Id)));
        return new ShellSubtitleTrackSelectionResult
        {
            SelectedSubtitleTrackId = track.Id,
            TrackSelectionChanged   = true,
            StatusMessage = "Selected image-based embedded subtitle track for direct playback."
        };
    }

    private async Task<ShellWorkflowTransitionResult> PauseForWorkflowTransitionAsync(
        string statusMessage,
        CancellationToken cancellationToken,
        bool? startupGateBlocking = null)
    {
        await _playbackBackend.PauseAsync(cancellationToken);
        await WaitForPauseStateAsync(true, cancellationToken);
        return new ShellWorkflowTransitionResult
        {
            StatusMessage       = statusMessage,
            StartupGateBlocking = startupGateBlocking
        };
    }

    private async Task WaitForPauseStateAsync(bool paused, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_playbackBackend.Clock.Current.IsPaused == paused) return;
            await Task.Delay(50, cancellationToken);
        }
    }
}
