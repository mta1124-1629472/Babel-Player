using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal sealed class DiarizationStageOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        internal DiarizationStageOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        internal async Task ExecuteAsync(PipelineStageContext? stageContext, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_c.CurrentSession.IngestedMediaPath))
                throw new InvalidOperationException("No ingested media is available for speaker mapping.");
            if (string.IsNullOrWhiteSpace(_c.CurrentSession.TranscriptPath))
                throw new InvalidOperationException("No transcript is available for speaker mapping.");

            ReportStage(
                stageContext,
                $"Running {_c.CurrentSettings.DiarizationProvider} diarization to identify speakers before translation and dubbing…",
                progress01: 0,
                isIndeterminate: true);

            var outcome = await _c.ExecuteDiarizationAsync(
                _c.CurrentSession.IngestedMediaPath,
                _c.CurrentSession.TranscriptPath,
                cancellationToken,
                resultingStage: SessionWorkflowStage.Diarized,
                statusMessage: "Speaker mapping complete. Continuing translation and dubbing.");

            ReportStage(
                stageContext,
                $"Speaker mapping complete. Identified {outcome.SpeakerCount} speakers across {outcome.SegmentCount} segments.",
                progress01: 1,
                isIndeterminate: false);
        }
    }
}
