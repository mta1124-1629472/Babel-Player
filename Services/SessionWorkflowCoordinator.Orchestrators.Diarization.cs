using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal sealed class DiarizationStageOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        /// <summary>
/// Initializes a new instance of the DiarizationStageOrchestrator and associates it with the enclosing SessionWorkflowCoordinator.
/// </summary>
/// <param name="coordinator">The parent SessionWorkflowCoordinator instance used to access session state and orchestration helpers.</param>
internal DiarizationStageOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        /// <summary>
        /// Orchestrates the diarization stage: validates inputs, reports start/completion, and runs speaker mapping before translation and dubbing.
        /// </summary>
        /// <remarks>
        /// Entry state: expects a current session with an ingested media path and a transcript path present.
        /// Exit state on success: the session is advanced to <see cref="SessionWorkflowStage.Diarized"/> (set by the underlying diarization operation).
        /// The method delegates work to the diarization implementation which persists the resulting session state.
        /// </remarks>
        /// <param name="stageContext">Optional pipeline stage context used for progress and status reporting.</param>
        /// <param name="cancellationToken">Propagates cancellation to the diarization operation; the method may throw <see cref="OperationCanceledException"/> if canceled.</param>
        /// <exception cref="InvalidOperationException">Thrown if the current session lacks an ingested media path or a transcript path required for speaker mapping.</exception>
        internal async Task ExecuteAsync(PipelineStageContext? stageContext, CancellationToken cancellationToken)
        {
            var stagePlan = _c.ResolveAndApplyExecutionPlan(Planning.InferenceStage.Diarization);
            var stageTimer = Stopwatch.StartNew();
            var stageSucceeded = false;
            try
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
                    statusMessage: "Speaker analysis complete.");

                ReportStage(
                    stageContext,
                    $"Speaker mapping complete. Identified {outcome.SpeakerCount} speakers across {outcome.SegmentCount} segments.",
                    progress01: 1,
                    isIndeterminate: false);
                stageSucceeded = true;
            }
            finally
            {
                _c.Log.Info(
                    $"Stage telemetry stage=diarization success={(stageSucceeded ? "true" : "false")} " +
                    $"provider={stagePlan.ProviderId} runtime={stagePlan.Runtime} role={stagePlan.Role} " +
                    $"elapsed_ms={stageTimer.ElapsedMilliseconds}");
            }
        }
    }
}
