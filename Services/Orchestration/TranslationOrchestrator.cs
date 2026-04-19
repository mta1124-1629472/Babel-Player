using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Planning;

namespace Babel.Player.Services.Orchestration;

internal sealed class TranslationOrchestrator
{
    private readonly ISessionStateAccessor _session;
    private readonly IStageExecutionPlanner _planner;
    private readonly IProviderLifecycleManager _providers;
    private readonly ISessionCommitter _committer;
    private readonly IInferenceExecutionEngine _inferenceEngine;
    private readonly AppLog _log;

    internal TranslationOrchestrator(
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
        string? targetLanguage,
        string? sourceLanguage,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var stagePlan = _planner.ResolveAndApplyExecutionPlan(InferenceStage.Translation);
        var stageTimer = Stopwatch.StartNew();
        var stageSucceeded = false;

        try
        {
            if (string.IsNullOrEmpty(_session.CurrentSession.TranscriptPath))
                throw new InvalidOperationException("No transcript available. Please transcribe media first.");

            if (!File.Exists(_session.CurrentSession.TranscriptPath))
                throw new FileNotFoundException(
                    $"Transcript file not found: {_session.CurrentSession.TranscriptPath}");

            var rawTargetLanguage = targetLanguage ?? _session.CurrentSettings.TargetLanguage;
            var normalizedTargetLanguage = PipelineStageReporter.NormalizePipelineLanguage(
                rawTargetLanguage,
                _session.CurrentSettings.TargetLanguage);
            var rawSourceLanguage = sourceLanguage ?? _session.CurrentSession.SourceLanguage ?? "auto";
            var normalizedSourceLanguage = string.Equals(rawSourceLanguage, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : PipelineStageReporter.NormalizePipelineLanguage(rawSourceLanguage, rawSourceLanguage);

            var readinessMessage = BuildInfo.IsDevBuild
                ? $"Checking translation runtime, provider readiness, language routing, and model availability for {_session.CurrentSettings.TranslationProvider} / {_session.CurrentSettings.TranslationModel}…"
                : "Checking translation runtime and model availability…";
            PipelineStageReporter.ReportStage(
                stageContext,
                readinessMessage,
                progress01: 0,
                isIndeterminate: true);

            var downloadProgress = PipelineStageReporter.CreateStageDownloadProgress(
                stageContext,
                progress,
                $"Preparing translation model '{_session.CurrentSettings.TranslationModel}'");
            var snapshot = await _providers.PrepareTranslationExecutionSnapshotAsync(
                    stagePlan,
                    _session.CurrentSession.TranscriptPath!,
                    normalizedSourceLanguage,
                    normalizedTargetLanguage,
                    downloadProgress,
                    allowPendingTranscriptArtifact: false,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var translationProviderLease = snapshot.ProviderLease;

            var runMessage = BuildInfo.IsDevBuild
                ? $"Running translation from {normalizedSourceLanguage} to {normalizedTargetLanguage} with {snapshot.Plan.ProviderId} / {snapshot.Model}. Segment text will be rewritten into the target language for dubbing."
                : $"Running translation into {normalizedTargetLanguage}.";
            PipelineStageReporter.ReportStage(
                stageContext,
                runMessage,
                progress01: 0,
                isIndeterminate: true);

            _log.Debug(
                $"Starting translation run={snapshot.RunId}: {_session.CurrentSession.TranscriptPath} " +
                $"({normalizedSourceLanguage} -> {normalizedTargetLanguage})");

            var result = await _inferenceEngine.TranslateAsync(
                    snapshot.Provider,
                    new TranslationRequest(
                        snapshot.TranscriptPath,
                        snapshot.WorkingTranslationPath,
                        normalizedSourceLanguage,
                        normalizedTargetLanguage,
                        snapshot.Model),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                var errorMsg = result.ErrorMessage ?? "Unknown translation error";
                var ex = new InvalidOperationException($"Translation failed: {errorMsg}");
                _log.Error(ex.Message, ex);
                throw new PipelineProviderException(
                    $"Translation provider '{snapshot.Plan.ProviderId}' failed during translation stage: {errorMsg}",
                    ex);
            }

            await _committer.CommitTranslationSessionStateAsync(
                snapshot,
                result).ConfigureAwait(false);
            stageSucceeded = true;

            var completionMessage = BuildInfo.IsDevBuild
                ? $"Translation complete. {result.Segments.Count} segments were translated from {normalizedSourceLanguage} to {normalizedTargetLanguage}."
                : $"Translation complete. {result.Segments.Count} segments are ready for dubbing.";
            PipelineStageReporter.ReportStage(
                stageContext,
                completionMessage,
                progress01: 1,
                isIndeterminate: false);
        }
        finally
        {
            _log.Debug(
                $"Stage telemetry stage=translation success={(stageSucceeded ? "true" : "false")} " +
                $"provider={stagePlan.ProviderId} runtime={stagePlan.Runtime} role={stagePlan.Role} " +
                $"elapsed_ms={stageTimer.ElapsedMilliseconds}");
        }
    }
}
