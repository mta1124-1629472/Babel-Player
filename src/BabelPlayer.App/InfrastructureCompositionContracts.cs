using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface IProviderCompositionFactory
{
    ProviderAvailabilityComposition Create(
        CredentialFacade credentialFacade,
        Func<string, string?> environmentVariableReader,
        IBabelLogFactory? logFactory = null);
}

public interface IAiCredentialCoordinatorFactory
{
    IAiCredentialCoordinator Create(
        CredentialFacade credentialFacade,
        ICredentialDialogService? credentialDialogService,
        Func<string, string?>? environmentVariableReader = null);
}

public interface ITranscriptionEngineFactory
{
    ITranscriptionEngine Create(TranscriptionRequest request, ProviderAvailabilityContext context);
}

public interface ITranscriptionEngine
{
    Task<IReadOnlyList<SubtitleCue>> TranscribeWithWhisperAsync(
        string videoPath,
        CaptionGenerationOptions options,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubtitleCue>> TranscribeWithWindowsSpeechAsync(
        string videoPath,
        string? languageHint,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SubtitleCue>> TranscribeWithOpenAiAsync(
        string videoPath,
        CaptionGenerationOptions options,
        CancellationToken cancellationToken);
}

public interface ITranslationEngineFactory
{
    ITranslationEngine Create(IBabelLogFactory? logFactory = null);
}

public interface ITranslationEngine
{
    event Action<LocalTranslationRuntimeStatus>? LocalRuntimeStatusChanged;

    void ConfigureCloud(CloudTranslationOptions? cloud);

    void ConfigureLocal(LocalTranslationOptions? local);

    Task WarmupLocalRuntimeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken);
}
