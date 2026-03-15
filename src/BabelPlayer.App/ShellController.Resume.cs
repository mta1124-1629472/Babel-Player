namespace BabelPlayer.App;

/// <summary>
/// Resume decision state machine: presents a pending resume prompt to the
/// UI and applies the user's decision (Resume / StartOver / Dismiss).
/// </summary>
public sealed partial class ShellController
{
    public async Task<ShellResumeDecisionResult> ApplyResumeDecisionAsync(
        ShellResumeDecision decision,
        CancellationToken cancellationToken = default)
    {
        var pending = _pendingResumeDecision;
        if (pending is null)
            return new ShellResumeDecisionResult
            {
                DecisionApplied = false,
                StatusMessage   = "No pending resume decision."
            };

        var current = _playbackQueueController.NowPlayingItem;
        if (!string.IsNullOrWhiteSpace(current?.Path) &&
            !string.Equals(current.Path, pending.Path, StringComparison.OrdinalIgnoreCase))
        {
            ClearPendingResumeDecision();
            return new ShellResumeDecisionResult
            {
                DecisionApplied = false,
                StatusMessage   = "Resume decision expired due to media change."
            };
        }

        var displayName = current?.DisplayName ?? System.IO.Path.GetFileName(pending.Path);

        switch (decision)
        {
            case ShellResumeDecision.Resume:
                await _playbackBackend.SeekAsync(pending.ResumePosition, cancellationToken);
                await _playbackBackend.PlayAsync(cancellationToken);
                ClearPendingResumeDecision();
                return new ShellResumeDecisionResult
                {
                    DecisionApplied = true,
                    StatusMessage   = $"Resumed {displayName} at {pending.ResumePosition:mm\\:ss}."
                };

            case ShellResumeDecision.StartOver:
                _resumePlaybackService.RemoveCompletedEntry(pending.Path);
                await _playbackBackend.SeekAsync(TimeSpan.Zero, cancellationToken);
                await _playbackBackend.PlayAsync(cancellationToken);
                ClearPendingResumeDecision();
                return new ShellResumeDecisionResult
                {
                    DecisionApplied = true,
                    StatusMessage   = $"Starting {displayName} from the beginning."
                };

            default: // Dismiss
                ClearPendingResumeDecision();
                return new ShellResumeDecisionResult
                {
                    DecisionApplied = true,
                    StatusMessage   = $"Resume skipped for {displayName}."
                };
        }
    }
}
