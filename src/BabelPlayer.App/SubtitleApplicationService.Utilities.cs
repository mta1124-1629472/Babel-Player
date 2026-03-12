using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed partial class SubtitleApplicationService
{
    private bool TryLoadCachedGeneratedSubtitles(string videoPath, string transcriptionModelKey)
    {
        if (!_generatedSubtitleCache.TryGet(videoPath, transcriptionModelKey, out var cachedCues))
        {
            return false;
        }

        var currentSourceLanguage = SubtitleCueSessionMapper.ApplySourceLanguageToCues(cachedCues, DefaultSourceLanguage);
        UpdateWorkflowState(state => state with
        {
            CurrentVideoPath = videoPath,
            OverlayStatus = null
        });
        _mediaSessionCoordinator.SetTranscriptSegments(
            SubtitleCueSessionMapper.BuildTranscriptSegments(
                cachedCues,
                SubtitlePipelineSource.Generated,
                videoPath,
                transcriptionModelKey,
                SubtitleWorkflowCatalog.GetTranscriptionModel(transcriptionModelKey).DisplayName,
                DefaultSourceLanguage),
            SubtitlePipelineSource.Generated,
            currentSourceLanguage);
        _mediaSessionCoordinator.SetCaptionGenerationState(false);
        return true;
    }

    private void CacheGeneratedSubtitles(string videoPath, string transcriptionModelKey, IReadOnlyList<SubtitleCue> cues)
    {
        _generatedSubtitleCache.Store(videoPath, transcriptionModelKey, cues);
    }

    private TranslationSegment CreateTranslationSegment(TranscriptSegment transcriptSegment, string translatedText)
    {
        return SubtitleCueSessionMapper.CreateTranslationSegment(
            transcriptSegment,
            translatedText,
            _mediaSessionCoordinator.Snapshot.Transcript.Source,
            SubtitleWorkflowCatalog.GetTranslationModel(_workflowStateStore.Snapshot.SelectedTranslationModelKey).DisplayName,
            _workflowStateStore.Snapshot.SelectedTranslationModelKey,
            _translationTargetLanguage);
    }
}
