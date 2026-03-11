using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class OpenAiTranscriptionProviderAdapter : ITranscriptionProvider
{
    public string Id => "openai-transcription";

    public TranscriptionProvider Provider => TranscriptionProvider.Cloud;

    public bool CanHandle(TranscriptionModelSelection selection) => selection.Provider == TranscriptionProvider.Cloud;

    public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context)
        => ProviderAvailabilityUtilities.HasOpenAiApiKey(context);

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var service = ProviderAvailabilityUtilities.BuildAsrService(request);
        var apiKey = context.EnvironmentVariableReader("OPENAI_API_KEY") ?? context.CredentialFacade.GetOpenAiApiKey();
        var options = new CaptionGenerationOptions
        {
            Mode = request.Options.Mode,
            LanguageHint = request.Options.LanguageHint,
            OpenAiApiKey = apiKey,
            LocalModelType = request.Options.LocalModelType,
            CloudModel = request.Options.CloudModel
        };
        return service.TranscribeVideoWithOpenAiAsync(request.VideoPath, options, cancellationToken);
    }
}
