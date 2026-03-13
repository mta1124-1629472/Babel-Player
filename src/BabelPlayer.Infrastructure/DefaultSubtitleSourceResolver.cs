using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.Infrastructure;

public sealed class DefaultSubtitleSourceResolver : ISubtitleSourceResolver
{
    private readonly IRuntimeBootstrapService _runtimeBootstrapService;
    private readonly IBabelLogger _logger;

    public DefaultSubtitleSourceResolver(
        IRuntimeBootstrapService runtimeBootstrapService,
        IBabelLogFactory? logFactory = null)
    {
        _runtimeBootstrapService = runtimeBootstrapService;
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger("subtitles.source");
    }

    public Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        _logger.LogInfo("Loading external subtitles.", BabelLogContext.Create(("path", path)));
        return SubtitleImportService.LoadExternalSubtitleCuesAsync(
            path,
            _runtimeBootstrapService,
            onRuntimeProgress,
            onStatus,
            cancellationToken);
    }

    public Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken)
    {
        _logger.LogInfo("Extracting embedded subtitle track.", BabelLogContext.Create(("videoPath", videoPath), ("trackId", track.Id), ("codec", track.Codec)));
        return SubtitleImportService.ExtractEmbeddedSubtitleCuesAsync(
            videoPath,
            track,
            _runtimeBootstrapService,
            onRuntimeProgress,
            onStatus,
            cancellationToken);
    }
}
