using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>Platform-neutral rectangle (pixel units) returned by display-bounds queries.</summary>
public readonly record struct DisplayBounds(int X, int Y, int Width, int Height);

public interface IPlaybackHost
{
    event Action<PlaybackStateSnapshot>? StateChanged;
    event Action<IReadOnlyList<MediaTrackInfo>>? TracksChanged;
    event Action? MediaOpened;
    event Action? MediaEnded;
    event Action<string>? MediaFailed;
    event Action<RuntimeInstallProgress>? RuntimeInstallProgress;

    PlaybackStateSnapshot Snapshot { get; }
    IReadOnlyList<MediaTrackInfo> CurrentTracks { get; }
    HardwareDecodingMode HardwareDecodingMode { get; set; }

    Task InitializeAsync(nint hostHandle, CancellationToken cancellationToken);
    Task LoadAsync(string path, CancellationToken cancellationToken);
    Task PlayAsync(CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
    Task SeekRelativeAsync(TimeSpan delta, CancellationToken cancellationToken);
    Task SetPlaybackRateAsync(double speed, CancellationToken cancellationToken);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken);
    Task SetMuteAsync(bool muted, CancellationToken cancellationToken);
    Task StepFrameAsync(bool forward, CancellationToken cancellationToken);
    Task SetAudioTrackAsync(int? trackId, CancellationToken cancellationToken);
    Task SetSubtitleTrackAsync(int? trackId, CancellationToken cancellationToken);
    Task SetAudioDelayAsync(double seconds, CancellationToken cancellationToken);
    Task SetSubtitleDelayAsync(double seconds, CancellationToken cancellationToken);
    Task SetAspectRatioAsync(string aspectRatio, CancellationToken cancellationToken);
}

public interface IWindowModeService
{
    PlaybackWindowMode CurrentMode { get; }
    DisplayBounds GetCurrentDisplayBounds(bool workArea = false);
    Task SetModeAsync(PlaybackWindowMode mode, CancellationToken cancellationToken = default);
}

public interface IFilePickerService
{
    Task<IReadOnlyList<string>> PickMediaFilesAsync(CancellationToken cancellationToken = default);
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
    Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default);
    Task<string?> PickExecutableAsync(string title, string filterDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default);
    Task<string?> PickSaveFileAsync(string suggestedName, string fileTypeDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default);
}

public interface ICredentialDialogService
{
    Task<string?> PromptForApiKeyAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default);
    Task<(string ApiKey, string Region)?> PromptForApiKeyWithRegionAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default);
    Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<ShortcutProfile?> EditShortcutsAsync(ShortcutProfile currentProfile, CancellationToken cancellationToken = default);
}

public interface IRuntimeBootstrapService
{
    Task<string> EnsureMpvAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);
    Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);
    Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);
}
