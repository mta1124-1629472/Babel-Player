using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface ISubtitleSourceResolver
{
    Task<IReadOnlyList<SubtitleCue>> LoadExternalSubtitleCuesAsync(
        string path,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrackInfo track,
        Action<RuntimeInstallProgress>? onRuntimeProgress,
        Action<string>? onStatus,
        CancellationToken cancellationToken);
}

public interface ICaptionGenerator
{
    Task<IReadOnlyList<SubtitleCue>> GenerateCaptionsAsync(
        string videoPath,
        TranscriptionModelSelection selection,
        string? languageHint,
        Action<TranscriptChunk>? onFinal,
        Action<ModelTransferProgress>? onProgress,
        CancellationToken cancellationToken);
}

public interface ISubtitleTranslator
{
    event Action<LocalTranslationRuntimeStatus>? RuntimeStatusChanged;

    Task WarmupAsync(TranslationModelSelection selection, CancellationToken cancellationToken);

    Task<string> TranslateAsync(
        TranslationModelSelection selection,
        string text,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> TranslateBatchAsync(
        TranslationModelSelection selection,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}

public interface IAiCredentialCoordinator
{
    Task<bool> EnsureOpenAiApiKeyAsync(CancellationToken cancellationToken);

    Task<bool> EnsureTranslationProviderCredentialsAsync(TranslationProvider provider, CancellationToken cancellationToken);
}

public interface IRuntimeProvisioner
{
    Task<string> EnsureLlamaCppAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);

    Task<string> EnsureFfmpegAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);

    Task<bool> EnsureLlamaCppRuntimeReadyAsync(Action<RuntimeInstallProgress>? onProgress, CancellationToken cancellationToken);
}
