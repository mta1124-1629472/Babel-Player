using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Planning;
using Babel.Player.Services.Transcription;

namespace Babel.Player.Services.Orchestration;

internal sealed class TranscriptionOrchestrator
{
    private readonly ISessionStateAccessor _session;
    private readonly IStageExecutionPlanner _planner;
    private readonly IProviderLifecycleManager _providers;
    private readonly ISessionCommitter _committer;
    private readonly IInferenceExecutionEngine _inferenceEngine;
    private readonly AppLog _log;

    internal TranscriptionOrchestrator(
        ISessionStateAccessor session,
        IStageExecutionPlanner planner,
        IProviderLifecycleManager providers,
        ISessionCommitter committer,
        IInferenceExecutionEngine inferenceEngine,
        AppLog log)
    {
        _session = session;
        _planner = planner;
        _providers = providers;
        _committer = committer;
        _inferenceEngine = inferenceEngine;
        _log = log;
    }

    internal async Task ExecuteAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var stagePlan = _planner.ResolveAndApplyExecutionPlan(InferenceStage.Transcription);
        var stageTimer = Stopwatch.StartNew();
        var stageSucceeded = false;

        try
        {
            if (string.IsNullOrEmpty(_session.CurrentSession.IngestedMediaPath))
                throw new InvalidOperationException("No media loaded. Please load media first.");

            if (!File.Exists(_session.CurrentSession.IngestedMediaPath))
            {
                throw new FileNotFoundException(
                    $"Ingested media file not found: {_session.CurrentSession.IngestedMediaPath}");
            }

            var transcriptionSourcePath = _session.CurrentSession.IngestedMediaPath;
            if (_session.CurrentSettings.VocalSeparationEnabled)
            {
                transcriptionSourcePath = await _providers.SeparateVocalsAsync(
                        progress,
                        stageContext,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await _providers.EnsureTranscriptionProviderReadyAsync(
                    progress,
                    stageContext,
                    cancellationToken)
                .ConfigureAwait(false);

            var stageStartMessage = BuildInfo.IsDevBuild
                ? $"Starting transcription with {_session.CurrentSettings.TranscriptionProvider} / {_session.CurrentSettings.TranscriptionModel}. Audio will be segmented and the spoken language will be detected before translation."
                : "Starting transcription. Detecting speech segments and spoken language.";
            PipelineStageReporter.ReportStage(
                stageContext,
                stageStartMessage,
                progress01: 0,
                isIndeterminate: true);

            var sessionDir = _session.GetSessionDirectory();
            var transcriptDir = Path.Combine(sessionDir, "transcripts");
            Directory.CreateDirectory(transcriptDir);

            var transcriptStem = SessionWorkflowCoordinator.ResolveTranscriptArtifactStem(
                _session.CurrentSession.IngestedMediaPath,
                transcriptionSourcePath);
            var transcriptPath = Path.Combine(transcriptDir, $"{transcriptStem}.json");

            var transcriptionService = _providers.TranscriptionService ??= _providers.CreateTranscriptionService();
            var request = CpuTranscriptionRuntimePolicy.BuildTranscriptionRequest(
                _session.CurrentSettings,
                _session.HardwareSnapshot,
                transcriptionSourcePath,
                transcriptPath,
                _session.CurrentSettings.TranscriptionModel,
                SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(
                    _session.CurrentSettings.TranscriptionLanguageHint),
                _log,
                out var cpuParams);

            var cpuThreads = request.CpuThreads > 0 ? request.CpuThreads.ToString() : "auto";
            var routeSummary =
                $"provider={_session.CurrentSettings.TranscriptionProvider}, model={_session.CurrentSettings.TranscriptionModel}, " +
                $"cpu_compute={request.CpuComputeType}, cpu_threads={cpuThreads}, cpu_workers={request.NumWorkers}";
            var hwSummary =
                $"avx2={(_session.HardwareSnapshot.HasAvx2 ? "yes" : "no")}, " +
                $"avx512={(_session.HardwareSnapshot.HasAvx512F ? "yes" : "no")}, " +
                $"cuda={(_session.HardwareSnapshot.HasCuda ? "yes" : "no")}, " +
                $"phys_cores={(_session.HardwareSnapshot.CpuPhysicalCores?.ToString() ?? "?")}";

            if (cpuParams.ResolutionNotes.Count > 0)
                _log.Debug($"CPU transcription policy: {string.Join("; ", cpuParams.ResolutionNotes)}");

            _log.Debug(
                $"Starting transcription: {transcriptionSourcePath} " +
                $"[{_session.CurrentSettings.TranscriptionProvider}/{_session.CurrentSettings.TranscriptionModel}] " +
                $"route=({routeSummary}) hw=({hwSummary})");

            var result = await _inferenceEngine.TranscribeAsync(
                    transcriptionService,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success
                && string.Equals(
                    _session.CurrentSettings.TranscriptionProvider,
                    ProviderNames.FasterWhisper,
                    StringComparison.Ordinal)
                && CpuTranscriptionRuntimePolicy.IsRecoverableCpuTranscriptionFailure(result.ErrorMessage))
            {
                var safeRequest = CpuTranscriptionRuntimePolicy.WithSafeCpuFallback(request);
                PipelineStageReporter.ReportStage(
                    stageContext,
                    "Transcription failed with the current CPU settings. Retrying once with a safe CPU profile (int8, single worker).",
                    progress01: 0,
                    isIndeterminate: true);
                _log.Warning(
                    $"Transcription retry after recoverable error: {result.ErrorMessage}. " +
                    $"Retrying with cpu_compute={safeRequest.CpuComputeType}, workers={safeRequest.NumWorkers}.");
                result = await _inferenceEngine.TranscribeAsync(
                        transcriptionService,
                        safeRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!result.Success)
            {
                var errorMsg = result.ErrorMessage ?? "Unknown transcription error";
                var ex = new InvalidOperationException($"Transcription failed: {errorMsg}");
                _log.Error(ex.Message, ex);
                throw new PipelineProviderException(
                    $"Transcription provider '{_session.CurrentSettings.TranscriptionProvider}' failed during transcription stage: {errorMsg}",
                    ex);
            }

            await _committer.CommitTranscriptionSessionStateAsync(result, transcriptPath).ConfigureAwait(false);
            stageSucceeded = true;

            var completionMessage = BuildInfo.IsDevBuild
                ? $"Transcription complete. {result.Segments.Count} segments were detected in {result.Language}."
                : $"Transcription complete. {result.Segments.Count} segments detected.";
            PipelineStageReporter.ReportStage(
                stageContext,
                completionMessage,
                progress01: 1,
                isIndeterminate: false);
        }
        finally
        {
            _log.Debug(
                $"Stage telemetry stage=transcription success={(stageSucceeded ? "true" : "false")} " +
                $"provider={stagePlan.ProviderId} runtime={stagePlan.Runtime} role={stagePlan.Role} " +
                $"elapsed_ms={stageTimer.ElapsedMilliseconds}");
        }
    }
}
