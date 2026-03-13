namespace BabelPlayer.App;

public interface IShortcutCommandExecutor
{
    Task<ShortcutCommandExecutionResult> ExecuteAsync(string commandId, CancellationToken cancellationToken = default);
}

public enum ShortcutShellAction
{
    None,
    ToggleFullscreen,
    TogglePictureInPicture,
    ToggleSubtitleVisibility
}

public sealed record ShortcutCommandExecutionResult
{
    public ShortcutShellAction ShellAction { get; init; }
    public ShellPlaylistItem? ItemToLoad { get; init; }
    public string? StatusMessage { get; init; }
    public bool IsError { get; init; }
    public bool RequiresOverlayInteraction { get; init; }
    public ShellPreferencesSnapshot? UpdatedPreferences { get; init; }
}

public sealed class ShortcutCommandExecutor : IShortcutCommandExecutor
{
    private readonly IQueueCommands _queueCommands;
    private readonly IShellPlaybackCommands _shellPlaybackCommands;
    private readonly IShellPreferencesService _shellPreferencesService;
    private readonly IShellPreferenceCommands _shellPreferenceCommands;
    private readonly ISubtitleWorkflowShellService _subtitleWorkflowService;

    public ShortcutCommandExecutor(
        IQueueCommands queueCommands,
        IShellPlaybackCommands shellPlaybackCommands,
        IShellPreferencesService shellPreferencesService,
        IShellPreferenceCommands shellPreferenceCommands,
        ISubtitleWorkflowShellService subtitleWorkflowService)
    {
        _queueCommands = queueCommands;
        _shellPlaybackCommands = shellPlaybackCommands;
        _shellPreferencesService = shellPreferencesService;
        _shellPreferenceCommands = shellPreferenceCommands;
        _subtitleWorkflowService = subtitleWorkflowService;
    }

    public async Task<ShortcutCommandExecutionResult> ExecuteAsync(string commandId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        switch (commandId)
        {
            case "play_pause":
                if (_shellPlaybackCommands.CurrentPlaybackSnapshot.IsPaused)
                {
                    await _shellPlaybackCommands.PlayAsync(cancellationToken);
                    return new ShortcutCommandExecutionResult { StatusMessage = "Playback resumed." };
                }

                await _shellPlaybackCommands.PauseAsync(cancellationToken);
                return new ShortcutCommandExecutionResult { StatusMessage = "Playback paused." };

            case "seek_back_small":
                await _shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(-5), cancellationToken);
                return new ShortcutCommandExecutionResult { RequiresOverlayInteraction = true };

            case "seek_forward_small":
                await _shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(5), cancellationToken);
                return new ShortcutCommandExecutionResult { RequiresOverlayInteraction = true };

            case "seek_back_large":
                await _shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(-15), cancellationToken);
                return new ShortcutCommandExecutionResult { RequiresOverlayInteraction = true };

            case "seek_forward_large":
                await _shellPlaybackCommands.SeekRelativeAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return new ShortcutCommandExecutionResult { RequiresOverlayInteraction = true };

            case "previous_frame":
                await _shellPlaybackCommands.StepFrameAsync(forward: false, cancellationToken);
                return new ShortcutCommandExecutionResult
                {
                    RequiresOverlayInteraction = true,
                    StatusMessage = "Stepped to previous frame."
                };

            case "next_frame":
                await _shellPlaybackCommands.StepFrameAsync(forward: true, cancellationToken);
                return new ShortcutCommandExecutionResult
                {
                    RequiresOverlayInteraction = true,
                    StatusMessage = "Stepped to next frame."
                };

            case "fullscreen":
                return new ShortcutCommandExecutionResult { ShellAction = ShortcutShellAction.ToggleFullscreen };

            case "pip":
                return new ShortcutCommandExecutionResult { ShellAction = ShortcutShellAction.TogglePictureInPicture };

            case "mute":
                {
                    var current = _shellPreferencesService.Current;
                    var muted = !current.IsMuted;
                    var updatedPreferences = await _shellPreferenceCommands.ApplyAudioStateAsync(
                        new ShellAudioStateChange(current.VolumeLevel, muted),
                        cancellationToken);
                    return new ShortcutCommandExecutionResult
                    {
                        UpdatedPreferences = updatedPreferences,
                        StatusMessage = muted ? "Audio muted." : "Audio unmuted."
                    };
                }

            case "subtitle_delay_back":
                return await AdjustSubtitleDelayAsync(-0.05, cancellationToken);

            case "subtitle_delay_forward":
                return await AdjustSubtitleDelayAsync(0.05, cancellationToken);

            case "audio_delay_back":
                return await AdjustAudioDelayAsync(-0.05, cancellationToken);

            case "audio_delay_forward":
                return await AdjustAudioDelayAsync(0.05, cancellationToken);

            case "speed_up":
                return await SetPlaybackRateAsync(_shellPreferencesService.Current.PlaybackRate + 0.25, cancellationToken);

            case "speed_down":
                return await SetPlaybackRateAsync(_shellPreferencesService.Current.PlaybackRate - 0.25, cancellationToken);

            case "speed_reset":
                return await SetPlaybackRateAsync(1.0, cancellationToken);

            case "next_item":
                return new ShortcutCommandExecutionResult
                {
                    ItemToLoad = _queueCommands.MoveNext()
                };

            case "previous_item":
                return new ShortcutCommandExecutionResult
                {
                    ItemToLoad = _queueCommands.MovePrevious()
                };

            case "subtitle_toggle":
                return new ShortcutCommandExecutionResult { ShellAction = ShortcutShellAction.ToggleSubtitleVisibility };

            case "translation_toggle":
                {
                    var enabled = !_subtitleWorkflowService.Current.IsTranslationEnabled;
                    await _subtitleWorkflowService.SetTranslationEnabledAsync(enabled, cancellationToken: cancellationToken);
                    return new ShortcutCommandExecutionResult
                    {
                        StatusMessage = enabled ? "Translation enabled." : "Translation disabled."
                    };
                }

            default:
                throw new InvalidOperationException($"Unsupported shortcut command '{commandId}'.");
        }
    }

    private async Task<ShortcutCommandExecutionResult> SetPlaybackRateAsync(double playbackRate, CancellationToken cancellationToken)
    {
        var current = _shellPreferencesService.Current;
        var clampedRate = Math.Clamp(playbackRate, 0.25, 2.0);
        var updatedPreferences = await _shellPreferenceCommands.ApplyPlaybackDefaultsAsync(new ShellPlaybackDefaultsChange(
            current.HardwareDecodingMode,
            clampedRate,
            current.AudioDelaySeconds,
            current.SubtitleDelaySeconds,
            current.AspectRatio),
            cancellationToken);
        return new ShortcutCommandExecutionResult
        {
            UpdatedPreferences = updatedPreferences,
            StatusMessage = $"Playback speed: {clampedRate:0.00}x."
        };
    }

    private async Task<ShortcutCommandExecutionResult> AdjustSubtitleDelayAsync(double delta, CancellationToken cancellationToken)
    {
        var current = _shellPreferencesService.Current;
        var updatedDelay = current.SubtitleDelaySeconds + delta;
        var updatedPreferences = await _shellPreferenceCommands.ApplyPlaybackDefaultsAsync(new ShellPlaybackDefaultsChange(
            current.HardwareDecodingMode,
            current.PlaybackRate,
            current.AudioDelaySeconds,
            updatedDelay,
            current.AspectRatio),
            cancellationToken);
        return new ShortcutCommandExecutionResult
        {
            UpdatedPreferences = updatedPreferences,
            StatusMessage = $"Subtitle delay: {updatedDelay:+0.00;-0.00;0.00}s"
        };
    }

    private async Task<ShortcutCommandExecutionResult> AdjustAudioDelayAsync(double delta, CancellationToken cancellationToken)
    {
        var current = _shellPreferencesService.Current;
        var updatedDelay = current.AudioDelaySeconds + delta;
        var updatedPreferences = await _shellPreferenceCommands.ApplyPlaybackDefaultsAsync(new ShellPlaybackDefaultsChange(
            current.HardwareDecodingMode,
            current.PlaybackRate,
            updatedDelay,
            current.SubtitleDelaySeconds,
            current.AspectRatio),
            cancellationToken);
        return new ShortcutCommandExecutionResult
        {
            UpdatedPreferences = updatedPreferences,
            StatusMessage = $"Audio delay: {updatedDelay:+0.00;-0.00;0.00}s"
        };
    }
}
