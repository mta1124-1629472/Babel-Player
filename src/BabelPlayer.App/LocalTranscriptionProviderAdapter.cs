using BabelPlayer.Core;

namespace BabelPlayer.App;

internal sealed class WhisperLocalTranscriptionProvider : ITranscriptionProvider
{
    public string Id => "local-whisper";

    public TranscriptionProvider Provider => TranscriptionProvider.Local;

    public bool CanHandle(TranscriptionModelSelection selection) => selection.Provider == TranscriptionProvider.Local;

    public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var service = ProviderAvailabilityUtilities.BuildAsrService(request, context);
        return service.TranscribeVideoWithWhisperAsync(
            request.VideoPath,
            request.Options.LocalModelType ?? Whisper.net.Ggml.GgmlType.BaseEn,
            request.Options.LanguageHint,
            cancellationToken);
    }
}

internal sealed class WindowsSpeechFallbackTranscriptionProvider : ITranscriptionProvider
{
    public string Id => "windows-speech";

    public TranscriptionProvider Provider => TranscriptionProvider.Local;

    public bool CanHandle(TranscriptionModelSelection selection) => selection.Provider == TranscriptionProvider.Local;

    public bool IsAvailable(TranscriptionModelSelection selection, ProviderAvailabilityContext context) => true;

    public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
        TranscriptionRequest request,
        ProviderAvailabilityContext context,
        CancellationToken cancellationToken)
    {
        var service = ProviderAvailabilityUtilities.BuildAsrService(request, context);
        return service.TranscribeVideoWithWindowsSpeechAsync(
            request.VideoPath,
            request.Options.LanguageHint,
            cancellationToken);
    }
}
