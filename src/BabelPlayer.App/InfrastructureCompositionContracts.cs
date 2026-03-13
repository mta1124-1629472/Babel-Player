using BabelPlayer.Core;

namespace BabelPlayer.App;

public interface IProviderCompositionFactory
{
    ProviderAvailabilityComposition Create(
        CredentialFacade credentialFacade,
        Func<string, string?> environmentVariableReader,
        IBabelLogFactory? logFactory = null);
}

public sealed record SubtitleWorkflowInfrastructureRequest(
    CredentialFacade CredentialFacade,
    ICredentialDialogService? CredentialDialogService,
    IFilePickerService? FilePickerService,
    Func<string, string?> EnvironmentVariableReader,
    IBabelLogFactory? LogFactory = null);

public sealed record SubtitleWorkflowInfrastructure(
    IRuntimeBootstrapService RuntimeBootstrapService,
    IProviderAvailabilityService ProviderAvailabilityService,
    ISubtitleSourceResolver SubtitleSourceResolver,
    ICaptionGenerator CaptionGenerator,
    ISubtitleTranslator SubtitleTranslator,
    IAiCredentialCoordinator AiCredentialCoordinator,
    IRuntimeProvisioner RuntimeProvisioner);

public interface ISubtitleWorkflowInfrastructureFactory
{
    SubtitleWorkflowInfrastructure Create(SubtitleWorkflowInfrastructureRequest request);
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
