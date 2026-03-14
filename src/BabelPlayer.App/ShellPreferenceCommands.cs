namespace BabelPlayer.App;

public interface IShellPreferenceCommands
{
    ShellPreferencesSnapshot ApplyLayoutChange(ShellLayoutPreferencesChange change);

    Task<ShellPreferencesSnapshot> ApplyAudioStateAsync(ShellAudioStateChange change, CancellationToken cancellationToken = default);

    Task<ShellPreferencesSnapshot> ApplyPlaybackDefaultsAsync(ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default);

    ShellPreferencesSnapshot ApplySubtitlePresentationChange(ShellSubtitlePresentationChange change);

    ShellPreferencesSnapshot ApplyShortcutProfileChange(ShellShortcutProfile profile);

    ShellResumePreferenceResult ApplyResumeEnabledChange(bool enabled);

    ShellPreferencesSnapshot ApplyAutoPlayNextInQueueChange(bool enabled);
}

public sealed record ShellResumePreferenceResult(
    ShellPreferencesSnapshot UpdatedPreferences,
    bool ResumeHistoryCleared);

public sealed class ShellPreferenceCommands : IShellPreferenceCommands
{
    private readonly IShellPreferencesService _shellPreferencesService;
    private readonly IShellPlaybackCommands _shellPlaybackCommands;
    private readonly IShortcutProfileService _shortcutProfileService;

    public ShellPreferenceCommands(
        IShellPreferencesService shellPreferencesService,
        IShellPlaybackCommands shellPlaybackCommands,
        IShortcutProfileService shortcutProfileService)
    {
        _shellPreferencesService = shellPreferencesService;
        _shellPlaybackCommands = shellPlaybackCommands;
        _shortcutProfileService = shortcutProfileService;
    }

    public ShellPreferencesSnapshot ApplyLayoutChange(ShellLayoutPreferencesChange change)
        => _shellPreferencesService.ApplyLayoutChange(change);

    public async Task<ShellPreferencesSnapshot> ApplyAudioStateAsync(ShellAudioStateChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = _shellPreferencesService.ApplyAudioStateChange(change);
        await _shellPlaybackCommands.ApplyAudioPreferencesAsync(snapshot.VolumeLevel, snapshot.IsMuted, cancellationToken);
        return snapshot;
    }

    public async Task<ShellPreferencesSnapshot> ApplyPlaybackDefaultsAsync(ShellPlaybackDefaultsChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = _shellPreferencesService.ApplyPlaybackDefaultsChange(change);
        await _shellPlaybackCommands.ApplyPlaybackDefaultsAsync(change, cancellationToken);
        return snapshot;
    }

    public ShellPreferencesSnapshot ApplySubtitlePresentationChange(ShellSubtitlePresentationChange change)
        => _shellPreferencesService.ApplySubtitlePresentationChange(change);

    public ShellPreferencesSnapshot ApplyShortcutProfileChange(ShellShortcutProfile profile)
    {
        _shortcutProfileService.ApplyShortcutProfileChange(profile);
        return _shellPreferencesService.Current;
    }

    public ShellResumePreferenceResult ApplyResumeEnabledChange(bool enabled)
    {
        var snapshot = _shellPreferencesService.ApplyResumeEnabledChange(new ShellResumeEnabledChange(enabled));
        _shellPlaybackCommands.SetResumeTrackingEnabled(enabled);
        var resumeHistoryCleared = false;
        if (!enabled)
        {
            _shellPlaybackCommands.ClearResumeHistory();
            resumeHistoryCleared = true;
        }

        return new ShellResumePreferenceResult(snapshot, resumeHistoryCleared);
    }

    public ShellPreferencesSnapshot ApplyAutoPlayNextInQueueChange(bool enabled)
    {
        return _shellPreferencesService.ApplyAutoPlayNextInQueueChange(new ShellAutoPlayNextInQueueChange(enabled));
    }
}
