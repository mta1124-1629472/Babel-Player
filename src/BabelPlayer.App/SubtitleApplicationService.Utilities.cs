using BabelPlayer.Core;

namespace BabelPlayer.App;

public sealed partial class SubtitleApplicationService
{
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
