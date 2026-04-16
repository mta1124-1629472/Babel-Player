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

        /// <summary>
/// Initializes a TranscriptionOrchestrator bound to the given session workflow coordinator.
/// </summary>
/// <param name="coordinator">The parent <see cref="SessionWorkflowCoordinator"/> used to access session state, settings, services, and helper operations.</param>
internal TranscriptionOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        /// <summary>
        /// Orchestrates the transcription pipeline stage for the current session: validates preconditions, ensures the transcription provider is ready, runs transcription, commits session state, and reports progress.
        /// </summary>
        /// <remarks>
        /// Entry state: a session with a non-empty IngestedMediaPath is required; the method verifies the file exists before proceeding. On success the session's transcription state is persisted via CommitTranscriptionSessionState and the stage is reported as complete. The method creates a "transcripts" directory under the session directory and writes the transcript as a JSON file named after the input media (without extension). The operation honors the provided <paramref name="cancellationToken"/>; when canceled the method may throw <see cref="OperationCanceledException"/> and will not commit a completed transcription state.
        /// Guard conditions: throws <see cref="InvalidOperationException"/> if no media is loaded; throws <see cref="FileNotFoundException"/> if the ingested media file cannot be found. The method also ensures transcription provider readiness before executing the transcription request and will surface provider errors as an <see cref="InvalidOperationException"/> if the transcription reports failure.
        /// </remarks>
        /// <param name="progress">Optional progress reporter receiving values in [0,1] to reflect stage progress.</param>
        /// <param name="stageContext">Optional pipeline stage context used for stage reporting and download progress tracking.</param>
        /// <param name="cancellationToken">Token used to cancel provider readiness checks and the transcription operation.</param>
        /// <exception cref="InvalidOperationException">Thrown when no media is loaded or when transcription fails with an error message from the provider.</exception>
        /// <exception cref="FileNotFoundException">Thrown when the ingested media file specified by the session does not exist.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the provided <paramref name="cancellationToken"/> is canceled during preparation or transcription.</exception>
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

        /// <summary>
/// Creates a TranslationOrchestrator associated with the given SessionWorkflowCoordinator.
/// </summary>
/// <param name="coordinator">The parent SessionWorkflowCoordinator used to access session state, configuration, and services.</param>
internal TranslationOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        /// <summary>
        /// Orchestrates translation of the current session's transcript into a target language and persists the translated transcript.
        /// </summary>
        /// <remarks>
        /// Entry state: expects <see cref="_c.CurrentSession.TranscriptPath"/> to be set and the transcript file to exist.
        /// On success: commits translation state via <c>CommitTranslationSessionState</c> and writes a translation JSON file under the session's <c>translations</c> directory.
        /// This method ensures the translation provider/model and runtime are ready before execution; readiness checks and any required model downloads are awaited. If readiness or execution fails, the corresponding exception is propagated and no translation state is committed. Progress and stage reporting are emitted via the provided <paramref name="progress"/> and <paramref name="stageContext"/>.
        /// Cancellation: honoring <paramref name="cancellationToken"/> will abort waiting and in-flight inference; if cancelled before commit, the session's persisted translation state will not be updated.
        /// </remarks>
        /// <param name="progress">Optional progress reporter used for stage-level progress updates.</param>
        /// <param name="targetLanguage">Optional target language code; if null, the coordinator's configured target language is used. The value is normalized before use.</param>
        /// <param name="sourceLanguage">Optional source language hint; if null, the session's source language is used or "auto" if unavailable. The special value "auto" is preserved and routed as automatic language detection.</param>
        /// <param name="stageContext">Optional pipeline stage context used for stage reporting and download-scoped progress creation.</param>
        /// <param name="cancellationToken">Token to cancel readiness checks and translation execution; cancelling will abort the operation and prevent session state commit if not already completed.</param>
        /// <exception cref="InvalidOperationException">Thrown when no transcript path is set or when translation execution fails.</exception>
        /// <exception cref="System.IO.FileNotFoundException">Thrown when the transcript file does not exist at the configured transcript path.</exception>
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
