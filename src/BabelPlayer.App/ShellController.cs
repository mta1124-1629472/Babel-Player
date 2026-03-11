using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed class ShellController
{
    private readonly PlaylistController _playlistController;
    private readonly PlaybackSessionController _playbackSessionController;
    private readonly IPlaybackBackend _playbackBackend;
    private readonly SubtitleWorkflowController _subtitleWorkflowController;

    public ShellController(
        PlaylistController playlistController,
        PlaybackSessionController playbackSessionController,
        IPlaybackBackend playbackBackend,
        SubtitleWorkflowController subtitleWorkflowController)
    {
        _playlistController = playlistController;
        _playbackSessionController = playbackSessionController;
        _playbackBackend = playbackBackend;
        _subtitleWorkflowController = subtitleWorkflowController;
    }

    public async Task<bool> LoadPlaylistItemAsync(
        PlaylistItem? item,
        HardwareDecodingMode hardwareDecodingMode,
        double playbackRate,
        string aspectRatio,
        double audioDelaySeconds,
        double subtitleDelaySeconds,
        double volume,
        CancellationToken cancellationToken)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            return false;
        }

        _playbackSessionController.StartWith(item);

        await _playbackBackend.LoadAsync(item.Path, cancellationToken);
        await _playbackBackend.SetHardwareDecodingModeAsync(hardwareDecodingMode, cancellationToken);
        await _playbackBackend.SetPlaybackRateAsync(playbackRate, cancellationToken);
        await _playbackBackend.SetAspectRatioAsync(aspectRatio, cancellationToken);
        await _playbackBackend.SetAudioDelayAsync(audioDelaySeconds, cancellationToken);
        await _playbackBackend.SetSubtitleDelayAsync(subtitleDelaySeconds, cancellationToken);
        await _playbackBackend.SetZoomAsync(0, cancellationToken);
        await _playbackBackend.SetPanAsync(0, 0, cancellationToken);
        await _playbackBackend.SetVolumeAsync(volume, cancellationToken);
        await _subtitleWorkflowController.LoadMediaSubtitlesAsync(item.Path, cancellationToken);
        return true;
    }

    public PlaylistItem? AdvanceAfterMediaEnded()
    {
        return _playlistController.AdvanceAfterMediaEnded();
    }
}
