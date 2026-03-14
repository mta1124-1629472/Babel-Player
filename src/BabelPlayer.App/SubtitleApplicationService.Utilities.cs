using BabelPlayer.Core;
using System.Text;

namespace BabelPlayer.App;

public sealed partial class SubtitleApplicationService
{
    private sealed record TranslationRunContext(
        long RunId,
        string MediaPath,
        int TranscriptRevision,
        string ModelKey,
        string TargetLanguage);

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

    private TranslationRunContext BeginTranslationRun(MediaSessionSnapshot snapshot)
    {
        var mediaPath = ResolveRunMediaPath(snapshot);
        var revision = UpdateTranscriptRevision(snapshot);
        var modelKey = _workflowStateStore.Snapshot.SelectedTranslationModelKey ?? string.Empty;
        var run = new TranslationRunContext(
            Interlocked.Increment(ref _translationRunIdCounter),
            mediaPath,
            revision,
            modelKey,
            _translationTargetLanguage);
        lock (_translationSync)
        {
            _activeTranslationRun = run;
        }

        return run;
    }

    private void EndTranslationRun(TranslationRunContext run)
    {
        lock (_translationSync)
        {
            if (_activeTranslationRun?.RunId == run.RunId)
            {
                _activeTranslationRun = null;
            }
        }
    }

    private bool HasActiveTranslationRun()
    {
        lock (_translationSync)
        {
            return _activeTranslationRun is not null;
        }
    }

    private bool IsRunActive(TranslationRunContext run)
    {
        lock (_translationSync)
        {
            return _activeTranslationRun?.RunId == run.RunId;
        }
    }

    private void InvalidateTranslationRun()
    {
        lock (_translationSync)
        {
            _activeTranslationRun = null;
        }
    }

    private bool TryUpsertTranslationSegment(TranscriptSegment transcriptSegment, string translatedText, TranslationRunContext run)
    {
        if (!IsRunActive(run))
        {
            return false;
        }

        _mediaSessionCoordinator.UpsertTranslationSegment(CreateTranslationSegment(transcriptSegment, translatedText));
        return true;
    }

    private string ResolveRunMediaPath(MediaSessionSnapshot snapshot)
    {
        return _workflowStateStore.Snapshot.CurrentVideoPath
            ?? snapshot.Source.Path
            ?? string.Empty;
    }

    private int UpdateTranscriptRevision(MediaSessionSnapshot snapshot)
    {
        var mediaPath = ResolveRunMediaPath(snapshot);
        var fingerprint = BuildTranscriptFingerprint(snapshot.Transcript.Segments);

        lock (_translationSync)
        {
            if (!string.Equals(_translationRevisionMediaPath, mediaPath, StringComparison.OrdinalIgnoreCase))
            {
                _translationRevisionMediaPath = mediaPath;
                _translationRevisionFingerprint = fingerprint;
                _translationTranscriptRevision = 1;
                return _translationTranscriptRevision;
            }

            if (string.Equals(_translationRevisionFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return _translationTranscriptRevision;
            }

            _translationRevisionFingerprint = fingerprint;
            _translationTranscriptRevision++;
            return _translationTranscriptRevision;
        }
    }

    private static string BuildTranscriptFingerprint(IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0)
        {
            return "empty";
        }

        var builder = new StringBuilder(segments.Count * 64);
        foreach (var segment in segments.OrderBy(item => item.Start).ThenBy(item => item.End))
        {
            builder
                .Append(segment.Id.Value)
                .Append('|')
                .Append(segment.Start.Ticks)
                .Append('|')
                .Append(segment.End.Ticks)
                .Append('|')
                .Append(segment.Language)
                .Append('|')
                .Append(segment.Text)
                .Append('|')
                .Append(segment.Revision.Value)
                .Append(';');
        }

        return builder.ToString();
    }
}
