using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Caption startup gate: auto-pauses playback while the first batch of
/// generated captions is being produced, then resumes once cues arrive.
/// Also handles the pause/resume cycle triggered by transcription model changes.
/// </summary>
public sealed partial class ShellController
{
    private bool    _autoResumePlaybackAfterCaptionReady;
    private string? _autoResumePlaybackPath;
    private TimeSpan _autoResumePlaybackPosition = TimeSpan.Zero;
    private bool    _autoResumePlaybackFromBeginning = true;

    public Task<ShellWorkflowTransitionResult> PrepareForTranscriptionRefreshAsync(
        SubtitleWorkflowSnapshot snapshot,
        ShellPlaybackStateSnapshot playbackState,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.SubtitleSource != SubtitlePipelineSource.Generated ||
            string.IsNullOrWhiteSpace(playbackState.Path))
            return Task.FromResult(new ShellWorkflowTransitionResult());

        _autoResumePlaybackAfterCaptionReady = true;
        _autoResumePlaybackPath              = playbackState.Path;
        _autoResumePlaybackPosition          = playbackState.Position;
        _autoResumePlaybackFromBeginning      = false;

        return PauseForWorkflowTransitionAsync(
            "Refreshing captions for the selected transcription model.",
            cancellationToken);
    }

    public async Task<ShellWorkflowTransitionResult> EvaluateCaptionStartupGateAsync(
        SubtitleWorkflowSnapshot snapshot,
        ShellPlaybackStateSnapshot playbackState,
        CancellationToken cancellationToken = default)
    {
        var path = playbackState.Path;

        if (string.IsNullOrWhiteSpace(path) ||
            !string.Equals(snapshot.CurrentVideoPath, path, StringComparison.OrdinalIgnoreCase))
        {
            ResetCaptionStartupGate();
            return new ShellWorkflowTransitionResult { StartupGateBlocking = false };
        }

        var shouldPause = snapshot.SubtitleSource == SubtitlePipelineSource.Generated
            && snapshot.IsCaptionGenerationInProgress
            && snapshot.Cues.Count == 0
            && playbackState.Position <= TimeSpan.FromSeconds(2);

        if (shouldPause && !_autoResumePlaybackAfterCaptionReady)
        {
            _autoResumePlaybackAfterCaptionReady = true;
            _autoResumePlaybackPath              = path;
            _autoResumePlaybackPosition          = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning      = true;

            return await PauseForWorkflowTransitionAsync(
                "Generating initial captions before playback starts.",
                cancellationToken,
                startupGateBlocking: true);
        }

        if (_autoResumePlaybackAfterCaptionReady
            && string.Equals(_autoResumePlaybackPath, path, StringComparison.OrdinalIgnoreCase)
            && snapshot.Cues.Count > 0)
        {
            _autoResumePlaybackAfterCaptionReady = false;
            _autoResumePlaybackPath              = null;
            var resumePos = _autoResumePlaybackFromBeginning
                ? TimeSpan.Zero
                : _autoResumePlaybackPosition;
            _autoResumePlaybackPosition     = TimeSpan.Zero;
            _autoResumePlaybackFromBeginning = true;

            await _playbackBackend.SeekAsync(resumePos, cancellationToken);
            await _playbackBackend.PlayAsync(cancellationToken);

            return new ShellWorkflowTransitionResult
            {
                StatusMessage       = "Captions ready. Playing with generated subtitles.",
                StartupGateBlocking = false
            };
        }

        if (!snapshot.IsCaptionGenerationInProgress)
        {
            ResetCaptionStartupGate();
            return new ShellWorkflowTransitionResult { StartupGateBlocking = false };
        }

        return new ShellWorkflowTransitionResult();
    }

    internal void ResetCaptionStartupGate()
    {
        _autoResumePlaybackAfterCaptionReady = false;
        _autoResumePlaybackPath              = null;
        _autoResumePlaybackPosition          = TimeSpan.Zero;
        _autoResumePlaybackFromBeginning      = true;
    }
}
