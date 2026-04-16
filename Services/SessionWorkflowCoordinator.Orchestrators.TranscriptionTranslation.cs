using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal sealed class TranscriptionOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        internal TranscriptionOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        internal async Task ExecuteAsync(
            IProgress<double>? progress,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_c.CurrentSession.IngestedMediaPath))
                throw new InvalidOperationException("No media loaded. Please load media first.");

            if (!File.Exists(_c.CurrentSession.IngestedMediaPath))
                throw new FileNotFoundException($"Ingested media file not found: {_c.CurrentSession.IngestedMediaPath}");

            await _c.EnsureTranscriptionProviderReadyAsync(progress, stageContext, cancellationToken);

            ReportStage(
                stageContext,
                $"Starting transcription with {_c.CurrentSettings.TranscriptionProvider} / {_c.CurrentSettings.TranscriptionModel}. Audio will be segmented and the spoken language will be detected before translation.",
                progress01: 0,
                isIndeterminate: true);

            var sessionDir = _c.GetSessionDirectory();
            var transcriptDir = Path.Combine(sessionDir, "transcripts");
            Directory.CreateDirectory(transcriptDir);

            var fileName = Path.GetFileNameWithoutExtension(_c.CurrentSession.IngestedMediaPath);
            var transcriptPath = Path.Combine(transcriptDir, $"{fileName}.json");

            var cpuThreads = _c.CurrentSettings.TranscriptionCpuThreads > 0
                ? _c.CurrentSettings.TranscriptionCpuThreads.ToString()
                : "auto";
            var cpuWorkers = Math.Max(1, _c.CurrentSettings.TranscriptionNumWorkers);
            var routeSummary =
                $"provider={_c.CurrentSettings.TranscriptionProvider}, model={_c.CurrentSettings.TranscriptionModel}, " +
                $"cpu_compute={_c.CurrentSettings.TranscriptionCpuComputeType}, cpu_threads={cpuThreads}, cpu_workers={cpuWorkers}";
            var hwSummary =
                $"avx2={(_c.HardwareSnapshot.HasAvx2 ? "yes" : "no")}, " +
                $"avx512={(_c.HardwareSnapshot.HasAvx512F ? "yes" : "no")}, " +
                $"cuda={(_c.HardwareSnapshot.HasCuda ? "yes" : "no")}";

            _c.Log.Info($"Starting transcription: {_c.CurrentSession.IngestedMediaPath} " +
                      $"[{_c.CurrentSettings.TranscriptionProvider}/{_c.CurrentSettings.TranscriptionModel}] " +
                      $"route=({routeSummary}) hw=({hwSummary})");

            var transcriptionService = _c._transcriptionService ??= _c.CreateTranscriptionService();
            var result = await _c._inferenceEngine.TranscribeAsync(
                transcriptionService,
                new TranscriptionRequest(
                    _c.CurrentSession.IngestedMediaPath,
                    transcriptPath,
                    _c.CurrentSettings.TranscriptionModel,
                    SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(_c.CurrentSettings.TranscriptionLanguageHint),
                    _c.CurrentSettings.TranscriptionCpuComputeType,
                    _c.CurrentSettings.TranscriptionCpuThreads,
                    _c.CurrentSettings.TranscriptionNumWorkers),
                cancellationToken);

            if (!result.Success)
            {
                var errorMsg = result.ErrorMessage ?? "Unknown transcription error";
                var ex = new InvalidOperationException($"Transcription failed: {errorMsg}");
                _c.Log.Error(ex.Message, ex);
                throw ex;
            }

            _c.CommitTranscriptionSessionState(result, transcriptPath);

            ReportStage(
                stageContext,
                $"Transcription complete. {result.Segments.Count} segments were detected in {result.Language}.",
                progress01: 1,
                isIndeterminate: false);
        }
    }

    internal sealed class TranslationOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        internal TranslationOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        internal async Task ExecuteAsync(
            IProgress<double>? progress,
            string? targetLanguage,
            string? sourceLanguage,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_c.CurrentSession.TranscriptPath))
                throw new InvalidOperationException("No transcript available. Please transcribe media first.");

            if (!File.Exists(_c.CurrentSession.TranscriptPath))
                throw new FileNotFoundException($"Transcript file not found: {_c.CurrentSession.TranscriptPath}");

            var rawLang = targetLanguage ?? _c.CurrentSettings.TargetLanguage;
            var lang = NormalizePipelineLanguage(rawLang, _c.CurrentSettings.TargetLanguage);
            var rawSrc = sourceLanguage ?? _c.CurrentSession.SourceLanguage ?? "auto";
            var src = string.Equals(rawSrc, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto"
                : NormalizePipelineLanguage(rawSrc, rawSrc);

            ReportStage(
                stageContext,
                $"Checking translation runtime, provider readiness, language routing, and model availability for {_c.CurrentSettings.TranslationProvider} / {_c.CurrentSettings.TranslationModel}…",
                progress01: 0,
                isIndeterminate: true);

            var downloadProgress = CreateStageDownloadProgress(
                stageContext,
                progress,
                $"Preparing translation model '{_c.CurrentSettings.TranslationModel}'");
            await _c.EnsureTranslationExecutionReadyAsync(downloadProgress, cancellationToken);

            _c._translationService ??= _c.CreateTranslationService();

            ReportStage(
                stageContext,
                $"Running translation from {src} to {lang} with {_c.CurrentSettings.TranslationProvider} / {_c.CurrentSettings.TranslationModel}. Segment text will be rewritten into the target language for dubbing.",
                progress01: 0,
                isIndeterminate: true);

            var sessionDir = _c.GetSessionDirectory();
            var translationDir = Path.Combine(sessionDir, "translations");
            Directory.CreateDirectory(translationDir);

            var fileName = Path.GetFileNameWithoutExtension(_c.CurrentSession.TranscriptPath);
            var translationPath = Path.Combine(translationDir, $"{fileName}_{lang}.json");

            _c.Log.Info($"Starting translation: {_c.CurrentSession.TranscriptPath} ({src} -> {lang})");

            var result = await _c._inferenceEngine.TranslateAsync(
                _c._translationService,
                new TranslationRequest(
                    _c.CurrentSession.TranscriptPath,
                    translationPath,
                    src,
                    lang,
                    _c.CurrentSettings.TranslationModel),
                cancellationToken);

            if (!result.Success)
            {
                var errorMsg = result.ErrorMessage ?? "Unknown translation error";
                var ex = new InvalidOperationException($"Translation failed: {errorMsg}");
                _c.Log.Error(ex.Message, ex);
                throw ex;
            }

            _c.CommitTranslationSessionState(result, translationPath, src, lang);

            ReportStage(
                stageContext,
                $"Translation complete. {result.Segments.Count} segments were translated from {src} to {lang}.",
                progress01: 1,
                isIndeterminate: false);
        }
    }
}
