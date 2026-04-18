using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Pipeline;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    public Task SeparateVocalsAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        SeparateVocalsAsync(progress, stageContext: null, cancellationToken);

    internal async Task<string> SeparateVocalsAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No ingested media is available for vocal separation.");
        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media file not found: {CurrentSession.IngestedMediaPath}");

        if (!string.IsNullOrWhiteSpace(CurrentSession.VocalsAudioPath)
            && !string.IsNullOrWhiteSpace(CurrentSession.AmbianceAudioPath)
            && File.Exists(CurrentSession.VocalsAudioPath)
            && File.Exists(CurrentSession.AmbianceAudioPath))
        {
            return CurrentSession.VocalsAudioPath;
        }

        ReportStage(
            stageContext,
            "Checking vocal-separation readiness on the local GPU host…",
            progress01: 0,
            isIndeterminate: true);

        await EnsureContainerizedExecutionRuntimeStartedAsync(
            InferenceRuntime.Containerized,
            "Vocal separation",
            cancellationToken).ConfigureAwait(false);

        var readiness = _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckVocalSeparationForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken).ConfigureAwait(false)
            : ContainerizedProviderReadiness.CheckVocalSeparation(CurrentSettings, serviceProbe: _containerizedProbe);

        if (!readiness.IsReady)
            throw new PipelineProviderException(readiness.BlockingReason ?? "Vocal separation is not ready.");

        var stemsDir = Path.Combine(GetSessionDirectory(), "stems");
        Directory.CreateDirectory(stemsDir);

        ReportStage(
            stageContext,
            "Separating vocals from backing track before transcription…",
            progress01: 0.05,
            isIndeterminate: true);

        _vocalSeparationProvider ??= CreateVocalSeparationProvider();
        var result = await _inferenceEngine.SeparateVocalsAsync(
            _vocalSeparationProvider,
            new VocalSeparationRequest(CurrentSession.IngestedMediaPath, stemsDir),
            cancellationToken).ConfigureAwait(false);

        if (!result.Success
            || string.IsNullOrWhiteSpace(result.VocalsAudioPath)
            || string.IsNullOrWhiteSpace(result.AmbianceAudioPath))
        {
            throw new InvalidOperationException(
                $"Vocal separation failed: {result.ErrorMessage ?? "Unknown vocal separation error"}");
        }

        if (!File.Exists(result.VocalsAudioPath))
            throw new InvalidOperationException($"Vocal separation completed but vocals artifact was not found: {result.VocalsAudioPath}");

        if (!File.Exists(result.AmbianceAudioPath))
            throw new InvalidOperationException($"Vocal separation completed but ambiance artifact was not found: {result.AmbianceAudioPath}");

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                VocalsAudioPath = result.VocalsAudioPath,
                AmbianceAudioPath = result.AmbianceAudioPath,
                StatusMessage = "Audio prepared for transcription.",
            };
        }
        await SaveCurrentSessionAsync().ConfigureAwait(false);

        ReportStage(
            stageContext,
            "Vocal separation complete. Transcription will use the isolated vocals stem.",
            progress01: 0.1,
            isIndeterminate: false);

        return result.VocalsAudioPath;
    }

    /// <summary>
        /// Transcribes the current session's ingested media and advances the session to the Transcribed pipeline stage.
        /// </summary>
        /// <param name="progress">Optional progress reporter receiving a fraction (0.0–1.0) indicating transcription progress.</param>
        /// <param name="cancellationToken">Token to monitor for cancellation. If cancelled, the operation will observe the token and may throw <see cref="OperationCanceledException"/>.</param>
        /// <returns>A task that completes when transcription (and any associated commit of transcription state) finishes successfully.</returns>
        public Task TranscribeMediaAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        TranscribeMediaAsync(progress, stageContext: null, cancellationToken);

    /// <summary>
    /// Runs the transcription pipeline for the current session.
    /// Expects the session to have media loaded on entry and, on success, updates the session to the Transcribed stage and persists the session state.
    /// </summary>
    /// <param name="progress">Optional progress reporter for overall pipeline progress updates.</param>
    /// <param name="stageContext">Optional stage context that constrains or targets the pipeline stage; if provided, the pipeline will use it to mark or report stage-specific progress.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation; when canceled, the method will honor the request and propagate <see cref="OperationCanceledException"/>.</param>
    /// <summary>
            /// Executes the transcription pipeline for the current session, advancing the session to the Transcribed stage on success.
            /// </summary>
            /// <param name="progress">Optional progress reporter receiving values from 0.0 to 1.0 for the transcription operation.</param>
            /// <param name="stageContext">Optional stage-scoped context used to aggregate stage progress and messages; pass null to use a fresh context.</param>
            /// <param name="cancellationToken">Token to observe for cancellation; the operation will honor cancellation and may throw <see cref="OperationCanceledException"/>.</param>
            /// <returns>Completion of the transcription operation; on success the session is advanced to the Transcribed stage and session state is persisted.</returns>
            /// <exception cref="PipelineProviderException">Thrown when the configured transcription provider or runtime is not ready for execution and the blocking reason prevents continuation.</exception>
    internal Task TranscribeMediaAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _transcriptionOrchestrator.ExecuteAsync(
            progress,
            stageContext is { } context ? context.ToShared() : null,
            cancellationToken);

    /// <summary>
        /// Advances the current session by translating the existing transcript into the specified target language.
        /// </summary>
        /// <remarks>
        /// Entry state: requires a session with a completed transcript (stage at or after <c>Transcribed</c>).
        /// On success: updates the session to the <c>Translated</c> stage and persists session metadata.
        /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when canceled.
        /// </remarks>
        /// <param name="progress">Optional progress reporter for overall stage progress (0.0–1.0).</param>
        /// <param name="targetLanguage">Optional BCP-47 language code to translate into; when null, the session or settings default is used.</param>
        /// <param name="sourceLanguage">Optional source language hint; when null, the session or settings default is used.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>A task that completes when the translation stage finishes and session state has been persisted.</returns>
        public Task TranslateTranscriptAsync(
        IProgress<double>? progress = null,
        string? targetLanguage = null,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        TranslateTranscriptAsync(progress, targetLanguage, sourceLanguage, stageContext: null, cancellationToken);

    /// <summary>
        /// Translates the current transcript into the specified target language and advances the session to the Translated stage.
        /// </summary>
        /// <param name="progress">Optional progress reporter for stage progress updates.</param>
        /// <param name="targetLanguage">Target language code for translation (e.g., "en", "fr").</param>
        /// <param name="sourceLanguage">Optional source language hint; if null the system will use the session's detected language.</param>
        /// <param name="stageContext">Optional stage context controlling persistence/visibility of intermediate stage updates.</param>
        /// <param name="cancellationToken">Token to observe for cancellation; operation honors cancellation and will throw <see cref="OperationCanceledException"/> when requested.</param>
        /// <returns>A task that completes when translation finishes. On success the session's stage is set to Translated and translation metadata are persisted.</returns>
    internal Task TranslateTranscriptAsync(
        IProgress<double>? progress,
        string? targetLanguage,
        string? sourceLanguage,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _translationOrchestrator.ExecuteAsync(
            progress,
            targetLanguage,
            sourceLanguage,
            stageContext is { } context ? context.ToShared() : null,
            cancellationToken);

    /// <summary>
        /// Runs the TTS generation pipeline for the current session using the provided voice and progress reporter.
        /// </summary>
        /// <param name="progress">Optional progress reporter that receives stage progress values (0.0–1.0).</param>
        /// <param name="voice">Optional voice identifier to use for TTS; if null the session/default voice is used.</param>
        /// <param name="cancellationToken">Token to observe for cancelling the operation.</param>
        /// <remarks>
        /// Entry state: requires the session to be at or beyond the Translated stage (a translation must exist).  
        /// Exit state: on success the session will be advanced to the TtsGenerated stage and TTS-related metadata will be persisted.  
        /// Persistence: this operation updates and saves the current session state on successful completion.  
        /// Cancellation: honoring <paramref name="cancellationToken"/> will cancel the operation; if cancelled the session may be left unchanged or partially updated depending on progress.
        /// </remarks>
        /// <summary>
        /// Generate text-to-speech audio for the current session's translation.
        /// </summary>
        /// <param name="progress">Optional progress reporter receiving values in the range 0.0–1.0 for generation progress.</param>
        /// <param name="voice">Optional TTS voice identifier to use for generation; when null the session/default voice is used.</param>
        /// <param name="cancellationToken">Token to cancel the generation operation; cancellation causes the task to end by throwing <see cref="OperationCanceledException"/>.</param>
        /// <returns>A task that completes when TTS generation has finished. On success the session is advanced to the TtsGenerated stage and the session state is persisted.</returns>
        public Task GenerateTtsAsync(
        IProgress<double>? progress = null,
        string? voice = null,
        CancellationToken cancellationToken = default) =>
        GenerateTtsAsync(progress, voice, stageContext: null, cancellationToken);

    /// <summary>
    /// Executes the TTS generation pipeline for the current session using the specified voice.
    /// </summary>
    /// <remarks>
    /// Entry state: requires the session to contain a completed translation (session stage at or after Translated).
    /// Exit state on success: updates the session to indicate TTS has been generated (TtsGenerated) and persists session metadata.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when cancelled.
    /// Provider readiness: verifies the configured TTS runtime/voice is available; a readiness failure may result in a <see cref="PipelineProviderException"/> or an <see cref="InvalidOperationException"/> if required model download fails.
    /// </remarks>
    /// <param name="progress">Optional progress reporter for overall pipeline progress (0.0–1.0).</param>
    /// <param name="voice">Optional voice identifier to use for generation; if null the pipeline resolves a default voice.</param>
    /// <param name="stageContext">Optional stage context that targets a specific stage marker or controls stage persistence behavior; when provided the pipeline uses it to scope progress and completion reporting.</param>
    /// <param name="cancellationToken">Cancellation token to abort pipeline execution.</param>
    /// <summary>
        /// Executes the text-to-speech generation stage for the current session, producing TTS segment files and final dub assets.
        /// </summary>
        /// <remarks>
        /// Expects the session to already have translation output available (session stage &gt;= Translated). On success the session will be advanced to the TtsGenerated stage and the session state will be persisted by the TTS pipeline orchestrator.
        /// </remarks>
        /// <param name="progress">Optional progress reporter for overall TTS generation progress (0.0–1.0).</param>
        /// <param name="voice">Optional voice identifier to use for generation; when null the orchestrator selects a default voice.</param>
        /// <param name="stageContext">Optional shared stage context used to report progress/messages across pipeline stages.</param>
        /// <param name="cancellationToken">Cancellation token to observe; operation honors cancellation and may throw <see cref="OperationCanceledException"/>.</param>
        /// <returns>A task that completes when TTS generation and session persistence are finished.</returns>
        /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider or runtime is not ready and cannot proceed.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via the provided <paramref name="cancellationToken"/>.</exception>
    internal Task GenerateTtsAsync(
        IProgress<double>? progress,
        string? voice,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _ttsPipelineOrchestrator.ExecuteAsync(progress, voice, stageContext, cancellationToken);

    /// <summary>
    /// Verifies the transcription runtime/provider is ready for execution and ensures any required transcription model is available.
    /// </summary>
    /// <param name="progress">Optional progress reporter for overall readiness; may be used to report download progress.</param>
    /// <param name="stageContext">Optional pipeline stage context used for reporting stage messages.</param>
    /// <param name="cancellationToken">Token to cancel readiness checks and any model download operations.</param>
    /// <remarks>
    /// Entry state: no specific session stage required; caller should have a configured <c>CurrentSettings.TranscriptionProvider</c> and <c>CurrentSettings.TranscriptionModel</c>.
    /// Exit state: a transcription service instance is created and assigned to <c>_transcriptionService</c> if readiness checks and any necessary model download succeed.
    /// This method does not persist session state.
    /// Cancellation: the operation observes <paramref name="cancellationToken"/> and will abort ongoing checks or downloads when cancelled.
    /// Guard behavior:
    /// - If the provider/runtime is blocked from executing and a model download is not allowed, a <see cref="PipelineProviderException"/> is thrown with the blocking reason.
    /// - If a required model download fails, an <see cref="InvalidOperationException"/> is thrown.
    /// </remarks>
    /// <exception cref="PipelineProviderException">Thrown when the provider/runtime is not ready and model download is not permitted; the exception message contains the blocking reason.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a required model download fails.</exception>
    private async Task EnsureTranscriptionProviderReadyAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        ReportStage(
            stageContext,
            $"Checking transcription runtime, provider readiness, and model availability for {CurrentSettings.TranscriptionProvider} / {CurrentSettings.TranscriptionModel}…",
            progress01: 0,
            isIndeterminate: true);

        await EnsureContainerizedExecutionRuntimeStartedAsync(CurrentSettings.TranscriptionRuntime, cancellationToken);

        var readiness = CurrentSettings.TranscriptionRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTranscriptionForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TranscriptionRegistry.CheckReadiness(
                CurrentSettings.TranscriptionProvider,
                CurrentSettings.TranscriptionModel,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TranscriptionProfile);

        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (readiness.RequiresModelDownload)
        {
            var downloadProgress = CreateStageDownloadProgress(
                stageContext,
                progress,
                $"Preparing transcription model '{CurrentSettings.TranscriptionModel}'");
            if (!await TranscriptionRegistry.EnsureModelAsync(
                    CurrentSettings.TranscriptionProvider,
                    CurrentSettings.TranscriptionModel,
                    CurrentSettings,
                    downloadProgress,
                    cancellationToken,
                    CurrentSettings.TranscriptionProfile,
                    KeyStore))
            {
                throw new InvalidOperationException($"Failed to download model '{CurrentSettings.TranscriptionModel}'.");
            }
        }

        _transcriptionService ??= CreateTranscriptionService();
    }

    private void CommitTranscriptionSessionState(TranscriptionResult result, string transcriptPath)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        // When vocal separation is disabled, clear any stale stem paths from a previous run that
        // had separation enabled. When separation is enabled, SeparateVocalsAsync already wrote
        // fresh stem paths into CurrentSession before this method is called, so we preserve them.
        var vocalsPath = CurrentSettings.VocalSeparationEnabled ? CurrentSession.VocalsAudioPath : null;
        var ambiancePath = CurrentSettings.VocalSeparationEnabled ? CurrentSession.AmbianceAudioPath : null;
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Transcribed,
                TranscriptPath = transcriptPath,
                SourceLanguage = result.Language,
                VocalsAudioPath = vocalsPath,
                AmbianceAudioPath = ambiancePath,
                TranscribedAtUtc = nowUtc,
                TranscriptionRuntime = CurrentSettings.TranscriptionRuntime,
                TranscriptionProvider = CurrentSettings.TranscriptionProvider,
                TranscriptionModel = CurrentSettings.TranscriptionModel,
                TranscriptionLanguageHint = SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(
                    CurrentSettings.TranscriptionLanguageHint),
                StatusMessage = ShouldRunDiarization()
                    ? $"Transcribed {result.Segments.Count} segments ({result.Language}). Speaker mapping is available before translation."
                    : $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
            };
        }

        _log.Info($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        SaveCurrentSession();
    }

    private void CommitTranslationSessionState(
        TranslationResult result,
        string translationPath,
        string sourceLanguage,
        string targetLanguage)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.Translated,
                TranslationPath = translationPath,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                TranslatedAtUtc = nowUtc,
                TranslationRuntime = CurrentSettings.TranslationRuntime,
                TranslationProvider = CurrentSettings.TranslationProvider,
                TranslationModel = CurrentSettings.TranslationModel,
                StatusMessage = "Translation complete. Ready for dubbing.",
            };
        }

        _log.Info($"Translation complete: {result.Segments.Count} segments, {sourceLanguage} -> {targetLanguage}");
        SaveCurrentSession();
    }

    /// <summary>
    /// Checks TTS runtime and provider readiness, then downloads the voice model if needed.
    /// Mirrors the guard pattern used by TranscribeMediaAsync and TranslateTranscriptAsync.
    /// <summary>
    /// Ensures the configured TTS runtime, provider, and voice assets are available and ready for generation.
    /// </summary>
    /// <param name="voice">The desired TTS voice identifier to validate or prepare.</param>
    /// <param name="progress">Optional progress reporter for stage-level progress or model download progress.</param>
    /// <param name="stageContext">Optional pipeline stage context used for stage-scoped progress and messages.</param>
    /// <param name="cancellationToken">Cancellation token to observe for long-running operations.</param>
    /// <remarks>
    /// Entry state: expects a configured TTS runtime/provider/voice in <see cref="CurrentSettings"/>; no particular pipeline stage is required.
    /// On success: the TTS provider and voice model (if required) are ready for TTS generation. This method does not modify or persist session state.
    /// Guard behavior: if the provider is not ready and a model download is not required, a <see cref="PipelineProviderException"/> is thrown with the provider's blocking reason. If a required model download fails, an <see cref="InvalidOperationException"/> is thrown.
    /// Cancellation: the method observes <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when cancelled.
    /// </remarks>
    /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider/runtime is not ready and no model download is available to resolve the condition.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a required voice/model download fails to complete successfully.</exception>
    private async Task EnsureTtsProviderReadyAsync(
        string voice,
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        ReportStage(
            stageContext,
            $"Checking TTS runtime, provider readiness, voice assets, and speaker/reference setup for {CurrentSettings.TtsProvider} / {voice}…",
            progress01: 0,
            isIndeterminate: true);

        await EnsureContainerizedExecutionRuntimeStartedAsync(
            CurrentSettings.TtsRuntime,
            "TTS",
            cancellationToken);

        var readiness = CurrentSettings.TtsRuntime == InferenceRuntime.Containerized && _containerizedProbe is not null
            ? await ContainerizedProviderReadiness.CheckTtsForExecutionAsync(
                CurrentSettings,
                _containerizedProbe,
                cancellationToken)
            : TtsRegistry.CheckReadiness(
                CurrentSettings.TtsProvider,
                voice,
                CurrentSettings,
                KeyStore,
                CurrentSettings.TtsProfile);

        if (!readiness.IsReady && !readiness.RequiresModelDownload)
            throw new PipelineProviderException(readiness.BlockingReason!);

        if (readiness.RequiresModelDownload)
        {
            var downloadProgress = CreateStageDownloadProgress(
                stageContext,
                progress,
                $"Preparing TTS voice '{voice}'");
            if (!await TtsRegistry.EnsureModelAsync(
                    CurrentSettings.TtsProvider,
                    voice,
                    CurrentSettings,
                    downloadProgress,
                    cancellationToken,
                    CurrentSettings.TtsProfile,
                    KeyStore))
            {
                throw new InvalidOperationException($"Failed to download voice '{voice}'.");
            }
        }
    }

    /// <summary>
    /// Generates per-segment TTS audio clips for the current translation and returns produced paths and durations.
    /// </summary>
    /// <remarks>
    /// Entry requirements: <see cref="CurrentSession.TranslationPath"/> must be set and point to an existing translation artifact.
    /// On success: does not modify or persist <see cref="CurrentSession"/> stage/state; it only produces segment audio artifacts on disk.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will throw <see cref="OperationCanceledException"/> when cancelled.
    /// </remarks>
    /// <param name="voice">The TTS voice to use for generated segments.</param>
    /// <param name="ttsLanguage">Optional language identifier to pass to the TTS provider; may be null to use provider defaults.</param>
    /// <param name="segmentsDir">Directory where per-segment audio files will be written.</param>
    /// <param name="stageContext">Optional pipeline stage context used for progress/stage reporting.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <returns>
    /// A tuple containing:
    /// - `SegmentAudioPaths`: a thread-safe map from segment ID to generated audio file path;
    /// - `SegmentDurations`: a thread-safe map from segment ID to the audio duration in seconds (only present for segments that reported duration);
    /// - `TotalSegments`: the number of candidate segments considered for generation;
    /// - `OrderedSegments`: the ordered list of translation segments that were processed.
    /// <summary>
    /// Generates per-segment TTS audio clips from the current session's translation for preview, seeking, and segment-level rendering.
    /// </summary>
    /// <param name="voice">The TTS voice to use for generated segment audio.</param>
    /// <param name="ttsLanguage">Optional language hint for the TTS provider.</param>
    /// <param name="segmentsDir">Filesystem directory where segment audio files will be written.</param>
    /// <param name="stageContext">Optional pipeline stage context used to report progress and messages.</param>
    /// <param name="cancellationToken">Token to observe for cancellation; operation will throw <see cref="OperationCanceledException"/> when cancelled.</param>
    /// <returns>
    /// A tuple containing:
    /// - `SegmentAudioPaths`: a mapping from segment id to the generated audio file path,
    /// - `SegmentDurations`: a mapping from segment id to its audio duration in seconds (when available),
    /// - `TotalSegments`: the total number of candidate segments considered,
    /// - `OrderedSegments`: the list of translation segments that were processed or considered for generation.
    /// </returns>
    /// <remarks>
    /// Requires that <see cref="CurrentSession.TranslationPath"/> points to a valid translation artifact; the method will load that artifact and skip segments with empty ids or empty translated text. The method does not modify or persist <c>CurrentSession</c>. Cancellation is honored via <paramref name="cancellationToken"/> and will propagate as <see cref="OperationCanceledException"/>. Exceptions other than cancellation are logged and rethrown. 
    /// </remarks>
    private async Task<(ConcurrentDictionary<string, string> SegmentAudioPaths, ConcurrentDictionary<string, double> SegmentDurations, int TotalSegments, IReadOnlyList<TranslationSegmentArtifact> OrderedSegments)> GenerateSegmentClipsAsync(
        string voice,
        string? ttsLanguage,
        string segmentsDir,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var segmentAudioPaths = new ConcurrentDictionary<string, string>();
        var segmentDurations = new ConcurrentDictionary<string, double>();

        ReportStage(
            stageContext,
            "Generating per-segment dubbed clips for preview, seek, and segment-level refinement…",
            progress01: 0,
            isIndeterminate: true);

        var translationData = await _artifactReader.LoadTranslationAsync(CurrentSession.TranslationPath!, cancellationToken);
        var candidateSegments = translationData.Segments?
            .Where(seg => !string.IsNullOrWhiteSpace(seg.Id) && !string.IsNullOrWhiteSpace(seg.TranslatedText))
            .ToList()
            ?? [];

        int totalSegments = candidateSegments.Count;
        ReportStage(
            stageContext,
            $"Generating {totalSegments} segment clips…",
            progress01: 0,
            isIndeterminate: true);

        try
        {
            if (_ttsService is QwenContainerTtsProvider qwenProvider)
            {
                await GenerateQwenBatchSegmentAudioAsync(
                    qwenProvider,
                    candidateSegments,
                    segmentsDir,
                    voice,
                    ttsLanguage,
                    stageContext,
                    segmentAudioPaths,
                    segmentDurations,
                    cancellationToken);
            }
            else
            {
                int completed = 0;
                // Honor the provider's advertised MaxConcurrency so cloud and
                // worker-pool-backed providers (ElevenLabs, OpenAI, Edge TTS,
                // Piper, etc.) synthesize segments in parallel. Shared state
                // (ConcurrentDictionary paths/durations, Interlocked counter)
                // is already thread-safe; Qwen takes the batch branch above.
                var maxConcurrency = Math.Max(1, _ttsService?.MaxConcurrency ?? 1);
                var parallelism = Math.Max(1, Math.Min(maxConcurrency, candidateSegments.Count));
                _log.Info(
                    $"TTS segment generation: provider={_ttsService?.GetType().Name ?? "<none>"} " +
                    $"max_concurrency={maxConcurrency} parallelism={parallelism} " +
                    $"segments={candidateSegments.Count}");
                await Parallel.ForEachAsync(
                    candidateSegments,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = parallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (seg, ct) => await GenerateSingleSegmentAsync(
                        seg,
                        voice,
                        ttsLanguage,
                        segmentsDir,
                        segmentAudioPaths,
                        segmentDurations,
                        totalSegments,
                        stageContext,
                        ct,
                        onSucceeded: () => Interlocked.Increment(ref completed))
                        .ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"TTS stage failed: {ex.Message}", ex);
            throw;
        }

        return (segmentAudioPaths, segmentDurations, totalSegments, candidateSegments);
    }

    /// <summary>
    /// Generates TTS audio for a single translation segment, records generated artifact paths/durations, and reports per-segment progress.
    /// </summary>
    /// <param name="seg">The translation segment to synthesize; its <see cref="TranslationSegmentArtifact.Id"/> and <see cref="TranslationSegmentArtifact.TranslatedText"/> must be non-empty.</param>
    /// <param name="defaultVoice">Fallback voice to use when the segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional language hint for the TTS engine.</param>
    /// <param name="segmentsDir">Directory where the segment audio file will be written (file named "{id}.mp3").</param>
    /// <param name="segmentAudioPaths">Concurrent map updated with the segment id → output audio path on successful generation.</param>
    /// <param name="segmentDurations">Concurrent map updated with the segment id → duration in seconds when the TTS result provides it.</param>
    /// <param name="totalSegments">Total number of candidate segments used to compute progress reporting.</param>
    /// <param name="stageContext">Optional stage context used for progress and status reporting.</param>
    /// <param name="cancellationToken">Cancellation token that will be passed to the TTS provider call; operation observes cancellation prior to awaiting the provider.</param>
    /// <param name="onSucceeded">Callback invoked when this segment is recorded as succeeded; should return the updated completed-segment count.</param>
    /// <remarks>
    /// Entry state: any pipeline stage that has a valid translation artifact containing this segment. On success this method updates <paramref name="segmentAudioPaths"/> and optionally <paramref name="segmentDurations"/>, invokes <paramref name="onSucceeded"/>, and reports stage progress; it does not persist session state. Failures from the TTS provider are logged and suppressed (do not propagate), while cancellation is honored via <paramref name="cancellationToken"/>. If the segment has empty text or id the method returns without performing work.
    /// </remarks>
    private async Task GenerateSingleSegmentAsync(
        TranslationSegmentArtifact seg,
        string defaultVoice,
        string? ttsLanguage,
        string segmentsDir,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        ConcurrentDictionary<string, double> segmentDurations,
        int totalSegments,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken,
        Func<int> onSucceeded)
    {
        var id = seg.Id;
        var text = seg.TranslatedText;

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id))
        {
            _log.Info($"Skipping segment {id}: empty text or ID");
            return;
        }

        var segmentAudioPath = Path.Combine(segmentsDir, $"{id}.mp3");
        var resolvedVoice = ResolveVoiceForSegment(seg, defaultVoice);
        var referenceAudioPath = ResolveReferenceAudioForSegment(seg);

        _log.Info($"Generating TTS for segment {id} (voice={resolvedVoice}, speaker={seg.SpeakerId ?? "<none>"}): {text[..Math.Min(30, text.Length)]}...");

        try
        {
            var segTask = _inferenceEngine.GenerateSegmentTtsAsync(
                _ttsService!,
                new SingleSegmentTtsRequest(
                    text,
                    segmentAudioPath,
                    resolvedVoice,
                    seg.SpeakerId,
                    referenceAudioPath,
                    Language: ttsLanguage,
                    SourceVideoPath: CurrentSession.IngestedMediaPath ?? CurrentSession.SourceMediaPath),
                cancellationToken);
            TrackPendingTtsTask(segTask);
            var segResult = await segTask;

            if (segResult.Success && File.Exists(segmentAudioPath))
            {
                segmentAudioPaths[id] = segmentAudioPath;
                if (segResult.DurationSeconds is { } dur)
                    segmentDurations[id] = dur;
                var done = onSucceeded();
                ReportStage(
                    stageContext,
                    $"Generated segment clip {done} of {totalSegments}…",
                    progress01: (double)done / totalSegments,
                    isIndeterminate: false);
                _log.Info($"Segment TTS generated: {id} -> {segmentAudioPath}");
            }
            else
            {
                _log.Warning($"Segment TTS failed or file missing: {id}");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Segment TTS generation failed for {id}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Collects segment audio paths in transcript order, concatenates them into a single dub file,
    /// and validates that the output file was produced.
    /// <paramref name="orderedSegments"/> comes from <see cref="GenerateSegmentClipsAsync"/> to avoid
    /// a redundant disk read of the translation artifact.
    /// <summary>
    /// Composes ordered segment audio clips (and optional ambiance) into a single dub audio render.
    /// </summary>
    /// <remarks>
    /// Expects segment audio paths for at least one segment in <paramref name="segmentAudioPaths"/> that match IDs in <paramref name="orderedSegments"/>.
    /// On success returns the final render result and does not modify or persist session state.
    /// Cancellation is observed and propagated to the underlying render call.
    /// </remarks>
    /// <param name="segmentAudioPaths">A map from segment ID to generated segment audio file path.</param>
    /// <param name="orderedSegments">Ordered translation segments that determine clip ordering and metadata for rendering.</param>
    /// <param name="ttsPath">Path to the full TTS audio (mixed segments) or null/empty when not applicable.</param>
    /// <param name="stageContext">Optional stage progress context used to report status messages during stitching.</param>
    /// <param name="cancellationToken">Cancellation token that will be forwarded to the rendering pipeline; operation will throw <see cref="OperationCanceledException"/> when canceled.</param>
    /// <returns>A <see cref="DubRenderResult"/> describing the outcome and paths of the composed dub audio.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no eligible segment audio files are available for stitching, or when the audio processing service is unavailable.</exception>
    private async Task<DubRenderResult> StitchSegmentClipsAsync(
        ConcurrentDictionary<string, string> segmentAudioPaths,
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        string ttsPath,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var orderedPaths = orderedSegments
            .Where(seg => seg.Id != null && segmentAudioPaths.ContainsKey(seg.Id))
            .Select(seg => segmentAudioPaths[seg.Id!])
            .ToList();

        if (orderedPaths.Count == 0)
            throw new InvalidOperationException(
                "No eligible segment audio files were produced. Stitching cannot proceed. Check provider configuration and logs.");

        _log.Info($"Stitching {orderedPaths.Count} segment clips into combined dub file...");
        ReportStage(
            stageContext,
            "Stitching segment clips into combined dub file…",
            progress01: 1,
            isIndeterminate: true);

        if (_audioProcessingService is null)
        {
            throw new InvalidOperationException("Audio processing service unavailable. Unable to compose dub audio.");
        }

        return await RenderDubAudioAsync(
                orderedSegments,
                segmentAudioPaths,
                ttsPath,
                CurrentSession.AmbianceAudioPath,
                CurrentSettings.AmbianceMixDb,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Finalizes TTS state after segment generation by updating the current session to the TtsGenerated stage, persisting the session, and reporting completion.
    /// </summary>
    /// <param name="voice">The voice identifier used to generate the TTS dub.</param>
    /// <param name="ttsPath">Path to the final stitched dub output file.</param>
    /// <param name="segmentsDir">Directory containing per-segment audio files.</param>
    /// <param name="segmentAudioPaths">Map of segment ID to generated audio file path for successfully produced segments.</param>
    /// <param name="segmentDurations">Optional map of segment ID to duration in seconds; when non-null and non-empty this will be persisted to the session, otherwise durations are not saved.</param>
    /// <param name="totalSegments">Total number of segments expected for the translation/TTS run.</param>
    /// <param name="stageContext">Optional pipeline stage context used for final reporting.</param>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="totalSegments"/> &gt; 0 but no segments were successfully generated.</exception>
    private void CommitTtsSessionState(
        string voice,
        string ttsPath,
        DubRenderResult renderResult,
        string segmentsDir,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        ConcurrentDictionary<string, double>? segmentDurations,
        int totalSegments,
        PipelineStageContext? stageContext)
    {
        int succeeded = segmentAudioPaths.Count;

        if (totalSegments > 0 && succeeded == 0)
        {
            _log.Error("TTS stage completed but no segments were generated.", new InvalidOperationException("Zero TTS segments"));
            throw new InvalidOperationException(
                "TTS stage completed but no segments were generated. Check provider configuration and logs.");
        }

        string statusMessage = succeeded == totalSegments
            ? $"TTS generated ({voice}). Dubbing complete."
            : $"TTS generated ({voice}). {succeeded}/{totalSegments} segments ready — {totalSegments - succeeded} failed.";

        lock (_sessionLock)
        {
            CurrentSession = CurrentSession with
            {
                Stage = SessionWorkflowStage.TtsGenerated,
                TtsPath = ttsPath,
                MixedDubAudioPath = renderResult.AmbianceMixed ? renderResult.MixedWithAmbiancePath : null,
                TtsVoice = voice,
                TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
                TtsSegmentsPath = segmentsDir,
                TtsSegmentAudioPaths = new Dictionary<string, string>(segmentAudioPaths),
                TtsSegmentDurations = segmentDurations is { Count: > 0 }
                    ? new Dictionary<string, double>(segmentDurations)
                    : null,
                TtsRuntime = CurrentSettings.TtsRuntime,
                TtsProvider = CurrentSettings.TtsProvider,
                StatusMessage = statusMessage,
            };
        }

        SaveCurrentSession();

        ReportStage(
            stageContext,
            $"Dub complete. {succeeded}/{totalSegments} segment clips are ready with voice {voice}.",
            progress01: 1,
            isIndeterminate: false);
    }

    private async Task<List<TimelineDubSegment>> BuildTimelineDubSegmentsAsync(
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        CancellationToken cancellationToken)
    {
        var timelineSegments = new List<TimelineDubSegment>(orderedSegments.Count);
        var stretchDir = Path.Combine(GetSessionDirectory(), "tts", "segments", "_timeline");
        Directory.CreateDirectory(stretchDir);

        foreach (var segment in orderedSegments)
        {
            if (string.IsNullOrWhiteSpace(segment.Id))
                continue;
            if (!segmentAudioPaths.TryGetValue(segment.Id, out var sourcePath) || !File.Exists(sourcePath))
                continue;

            var segmentDuration = Math.Max(0.05, segment.End - segment.Start);
            var timingMode = ResolveRenderTimingMode(segment.Id);
            var effectivePath = sourcePath;

            if (timingMode == SegmentTimingMode.Stretch && _audioProcessingService is not null)
            {
                var stretchedPath = Path.Combine(stretchDir, $"{segment.Id}.stretch.mp3");
                var stretched = await _audioProcessingService.TimeStretchAsync(
                    sourcePath,
                    stretchedPath,
                    segmentDuration,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (stretched && File.Exists(stretchedPath))
                    effectivePath = stretchedPath;
            }

            timelineSegments.Add(new TimelineDubSegment(
                segment.Id,
                effectivePath,
                Math.Max(0, segment.Start),
                segmentDuration,
                TrimToSegmentWindow: true));
        }

        if (timelineSegments.Count == 0)
        {
            throw new InvalidOperationException(
                "No eligible segment audio files were produced for timeline composition.");
        }

        return timelineSegments;
    }

    /// <summary>
    /// Determines the render timing mode for a segment, using a per-segment override when present.
    /// </summary>
    /// <param name="segmentId">The identifier of the segment to resolve timing for.</param>
    /// <returns>
    /// The normalized render timing mode to use for rendering the segment. If a per-segment override exists it is used except when the override is <see cref="SegmentTimingMode.Pause"/>, in which case the session default timing mode is used; the returned value is normalized via <c>DubTimingDefaults.NormalizeRenderTimingMode</c>.
    /// </returns>
    private SegmentTimingMode ResolveRenderTimingMode(string segmentId)
    {
        if (CurrentSession.SegmentTimingModeOverrides is not null
            && CurrentSession.SegmentTimingModeOverrides.TryGetValue(segmentId, out var overrideMode))
        {
            if (overrideMode == SegmentTimingMode.Pause)
            {
                _log.Info(
                    $"Ignoring preview-only Pause timing override for render on segment '{segmentId}'; using session default timing mode.");
                return DubTimingDefaults.NormalizeRenderTimingMode(CurrentSettings.DubTimingMode);
            }

            return DubTimingDefaults.NormalizeRenderTimingMode(overrideMode);
        }

        return DubTimingDefaults.NormalizeRenderTimingMode(CurrentSettings.DubTimingMode);
    }

    /// <summary>
    /// Generates TTS audio for multiple translation segments in a single Qwen batch request and records produced audio file paths.
    /// </summary>
    /// <remarks>
    /// Entry state: caller must supply translation segments (typically loaded from the session's translation artifact); no session stage changes are performed by this method.
    /// Exit state: records any successfully produced segment audio paths into <paramref name="segmentAudioPaths"/>; does not persist session state.
    /// Guard: returns immediately when no eligible segments (non-empty Id and TranslatedText) are present.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will observe cancellation propagated from the provider; provider exceptions and other errors propagate to the caller.
    /// Note: this method records produced audio paths into <paramref name="segmentAudioPaths"/>; it does not populate <paramref name="segmentDurations"/> in the current implementation.
    /// </remarks>
    /// <param name="qwenProvider">The Qwen TTS provider used to generate segments in batch.</param>
    /// <param name="candidateSegments">Translation segments to consider for batch generation; segments with empty Id or TranslatedText are skipped.</param>
    /// <param name="segmentsDir">Directory where per-segment output files will be written (each output is {Id}.mp3).</param>
    /// <param name="defaultVoice">Fallback voice to use when a segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional language hint for TTS generation.</param>
    /// <param name="stageContext">Optional stage context used for progress reporting.</param>
    /// <param name="segmentAudioPaths">Concurrent map that will be populated with segment id → output audio path for successfully generated segments.</param>
    /// <param name="segmentDurations">Concurrent map accepted for durations but not populated by this method.</param>
    /// <summary>
    /// Generates TTS audio files for eligible translation segments using the Qwen container TTS provider and records produced paths.
    /// </summary>
    /// <remarks>
    /// Entry state: called during TTS segment generation; does not change pipeline stage. On success it updates <paramref name="segmentAudioPaths"/> with produced segment audio file paths (does not populate <paramref name="segmentDurations"/>). The method reports per-segment progress via <paramref name="stageContext"/>. If no eligible segments are found, the method returns immediately without error. The method observes <paramref name="cancellationToken"/> and will cancel the provider request when requested.
    /// </remarks>
    /// <param name="qwenProvider">The Qwen container TTS provider used to generate segment audio in batch.</param>
    /// <param name="candidateSegments">Translation segments to consider; segments with empty Id or empty TranslatedText are skipped.</param>
    /// <param name="segmentsDir">Directory where generated segment files will be written (each output is {id}.mp3).</param>
    /// <param name="defaultVoice">Default voice to use when a segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional language hint passed to the TTS provider.</param>
    /// <param name="stageContext">Optional stage context used to report progress messages for the current pipeline stage.</param>
    /// <param name="segmentAudioPaths">Concurrent dictionary that will be populated with successful segmentId -> outputPath entries for produced audio files.</param>
    /// <param name="segmentDurations">Concurrent dictionary for segment durations (accepted but not populated by this method).</param>
    /// <param name="cancellationToken">Cancellation token to observe while performing the batch operation.</param>
    private async Task GenerateQwenBatchSegmentAudioAsync(
        QwenContainerTtsProvider qwenProvider,
        IReadOnlyList<TranslationSegmentArtifact> candidateSegments,
        string segmentsDir,
        string defaultVoice,
        string? ttsLanguage,
        PipelineStageContext? stageContext,
        ConcurrentDictionary<string, string> segmentAudioPaths,
        ConcurrentDictionary<string, double> segmentDurations,
        CancellationToken cancellationToken)
    {
        var batchRequests = new List<QwenBatchSegmentRequest>(candidateSegments.Count);
        foreach (var segment in candidateSegments)
        {
            var id = segment.Id;
            var text = segment.TranslatedText;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
                continue;

            var outputPath = Path.Combine(segmentsDir, $"{id}.mp3");
            var resolvedVoice = ResolveVoiceForSegment(segment, defaultVoice);
            var referenceAudioPath = ResolveReferenceAudioForSegment(segment);

            batchRequests.Add(new QwenBatchSegmentRequest(
                id,
                text,
                outputPath,
                resolvedVoice,
                segment.SpeakerId,
                referenceAudioPath,
                ttsLanguage,
                CurrentSession.IngestedMediaPath ?? CurrentSession.SourceMediaPath));
        }

        if (batchRequests.Count == 0)
            return;

        _log.Info($"Generating {batchRequests.Count} Qwen batch TTS segments.");
        var generatedPaths = await qwenProvider.GenerateSegmentsAsync(
            batchRequests,
            new Progress<(int Completed, int Total)>(update =>
            {
                ReportStage(
                    stageContext,
                    $"Generated segment clip {update.Completed} of {update.Total}…",
                    progress01: update.Total <= 0 ? 0 : (double)update.Completed / update.Total,
                    isIndeterminate: false);
            }),
            cancellationToken);

        foreach (var batchRequest in batchRequests)
        {
            if (generatedPaths.TryGetValue(batchRequest.SegmentId, out var outputPath) && File.Exists(outputPath))
            {
                segmentAudioPaths[batchRequest.SegmentId] = outputPath;
                _log.Info($"Qwen batch segment TTS generated: {batchRequest.SegmentId} -> {outputPath}");
            }
            else
            {
                _log.Warning($"Qwen batch segment TTS missing output: {batchRequest.SegmentId}");
            }
        }
    }

    /// <summary>
    /// Runs the full streaming pipeline (transcription, translation, and TTS) via the streaming orchestrator.
    /// </summary>
    /// <remarks>
    /// Entry state: can be invoked from any pipeline stage; the orchestrator will execute the remaining streaming stages as appropriate.
    /// Exit state on success: the session will have progressed through transcription, translation, and TTS stages and end at the TtsGenerated stage when applicable.
    /// Persistence: stage progress and session updates are persisted by the orchestrator as each stage completes.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will observe cancellation requests (may throw <see cref="OperationCanceledException"/>).
    /// </remarks>
    /// <param name="progress">Optional progress reporter receiving values between 0.0 and 1.0 for overall pipeline progress.</param>
    /// <param name="transcriptionStageContext">Optional context for the transcription stage; when provided, it customizes how the transcription stage is executed.</param>
    /// <param name="translationStageContext">Optional context for the translation stage; when provided, it customizes how the translation stage is executed.</param>
    /// <param name="ttsStageContext">Optional context for the TTS stage; when provided, it customizes how the TTS stage is executed.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <summary>
            /// Executes the full streaming pipeline (transcription, translation, and TTS) using the provided stage contexts and progress reporter.
            /// Entry: expects the session to be in a state where the streaming pipeline can be executed; the orchestrator determines and runs the remaining streaming stages.
            /// Success: advances the session to the stages produced by the pipeline; session persistence is performed by the orchestrator or downstream stage handlers.
            /// Cancellation: honors the provided cancellation token and will observe cancellation by throwing an <see cref="OperationCanceledException"/> when requested.
            /// </summary>
            /// <param name="progress">Optional overall progress reporter (range 0.0–1.0) for the streaming pipeline.</param>
            /// <param name="transcriptionStageContext">Optional shared stage context used for the transcription stage reporting.</param>
            /// <param name="translationStageContext">Optional shared stage context used for the translation stage reporting.</param>
            /// <param name="ttsStageContext">Optional shared stage context used for the TTS stage reporting.</param>
            /// <param name="cancellationToken">Cancellation token to cancel pipeline execution; operation observes and propagates cancellation.</param>
            /// <returns>A task that completes when the orchestrator finishes executing the full streaming pipeline.</returns>
    private Task ExecuteStreamingPipelineAsync(
        IProgress<double>? progress,
        PipelineStageContext? transcriptionStageContext,
        PipelineStageContext? translationStageContext,
        PipelineStageContext? ttsStageContext,
        CancellationToken cancellationToken) =>
        _streamingPipelineOrchestrator.ExecuteFullPipelineAsync(
            progress,
            transcriptionStageContext,
            translationStageContext,
            ttsStageContext,
            cancellationToken);

    /// <summary>
    /// Runs a streaming pipeline that translates the existing transcript and generates corresponding TTS output from that transcript.
    /// </summary>
    /// <param name="progress">Optional progress reporter for overall pipeline progress.</param>
    /// <param name="translationStageContext">Optional stage context to control translation stage behavior (e.g., target/override language); may be null.</param>
    /// <param name="ttsStageContext">Optional stage context to control TTS stage behavior (e.g., target voice); may be null.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation; cancellation is honored and will propagate.</param>
    /// <returns>Completes when translation and TTS generation finish and the session has been advanced to the TtsGenerated stage.</returns>
    /// <remarks>
    /// Entry state: expects a transcript to be available (session at or beyond the Transcribed stage).
    /// On success: advances session state to include translation and generated TTS artifacts (TtsGenerated).
    /// Persistence: session state for translation and TTS is persisted by the pipeline/orchestrator on successful completion.
    /// Guard conditions: throws <see cref="InvalidOperationException"/> if a required transcript is not available; throws <see cref="OperationCanceledException"/> when cancelled; other exceptions propagate from the orchestrator.
    /// <summary>
            /// Runs the streaming translation and TTS pipeline starting from the existing transcript.
            /// </summary>
            /// <remarks>
            /// Entry state: expects the session to contain a completed transcript (session stage &gt;= Transcribed). 
            /// On success: advances the session through translation and TTS stages and results in translation and TTS artifacts being created and recorded by the orchestrator (session stage will be updated accordingly, typically to a translated/tts-generated state). 
            /// Persistence: the orchestrator is responsible for persisting session updates. 
            /// Cancellation: honors <paramref name="cancellationToken"/> and may throw <see cref="OperationCanceledException"/> when canceled.
            /// </remarks>
            /// <param name="progress">Optional progress reporter for overall pipeline progress (0.0–1.0).</param>
            /// <param name="translationStageContext">Optional stage context to scope translation progress and reporting; when null a default context is used.</param>
            /// <param name="ttsStageContext">Optional stage context to scope TTS progress and reporting; when null a default context is used.</param>
            /// <param name="cancellationToken">Token to observe for cancellation.</param>
            /// <returns>A task that completes when the streaming translation and TTS pipeline finishes.</returns>
    private Task ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
        IProgress<double>? progress,
        PipelineStageContext? translationStageContext,
        PipelineStageContext? ttsStageContext,
        CancellationToken cancellationToken) =>
        _streamingPipelineOrchestrator.ExecuteTranslationAndTtsFromTranscriptAsync(
            progress,
            translationStageContext,
            ttsStageContext,
            cancellationToken);

    /// <summary>
    /// Advances the session pipeline from its current stage through any remaining stages (transcription, diarization, translation, and/or TTS) according to the pipeline state machine and current settings.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for the pipeline advance.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <summary>
        /// Advances the session pipeline from its current stage through the remaining stages until no further actions remain.
        /// </summary>
        /// <remarks>
        /// Entry state: can be invoked from any pipeline stage; the coordinator will determine and execute the appropriate next actions (including streaming or discrete stage flows) based on CurrentSession.Stage and configuration (e.g., diarization).  
        /// Exit state: the session will be advanced to the next completed pipeline stage(s); individual stage commits are persisted as they complete so the persisted session will reflect the progressed stages.
        /// </remarks>
        /// <param name="progress">Optional overall progress reporter (0.0 to 1.0) for the advance operation.</param>
        /// <param name="cancellationToken">Token to cancel the operation. If canceled, the method throws <see cref="OperationCanceledException"/>.</param>
        /// <returns>A task that completes when pipeline advancement finishes.</returns>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled via <paramref name="cancellationToken"/>.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the state machine stalls or an unexpected pipeline action occurs.</exception>
    public Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        AdvancePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Advances the session pipeline from its current stage through the remaining stages (transcription, diarization, translation, and TTS) according to the pipeline state machine.
    /// </summary>
    /// <remarks>
    /// Entry state: the method expects CurrentSession.Stage to reflect the pipeline's current persisted stage.
    /// On success: the session will be advanced to the next terminal or intermediate stage(s) determined by the state machine; individual stage handlers invoked by this method are responsible for updating and persisting CurrentSession as they complete.
    /// Behavior: if the state machine indicates the full streaming pipeline should run first, the method delegates to the streaming orchestrator; otherwise it drives stage-by-stage advancement using the state machine decisions. The method may return early after initiating streaming translation+TTS or TTS-only actions when those paths are chosen.
    /// </remarks>
    /// <param name="progress">Optional overall progress reporter for long-running operations; may be null.</param>
    /// <param name="stageProgress">Optional per-stage progress reporter used to report stage messages and progress; may be null.</param>
    /// <param name="cancellationToken">Cancellation token that will be observed; the method throws <see cref="OperationCanceledException"/> when cancellation is requested.</param>
    /// <returns>A task that completes when the pipeline advancement (and any delegated orchestration it synchronously awaits) has finished.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is canceled during execution.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the pipeline state machine returns an unexpected advance action.</exception>
    internal async Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        var shouldRunDiarization = ShouldRunDiarization();
        var remainingStages = GetAdvancePipelineStages(CurrentSession.Stage, shouldRunDiarization);

        if (PipelineStateMachine.ShouldRunFullStreamingPipelineFirst(CurrentSession.Stage, shouldRunDiarization))
        {
            await ExecuteStreamingPipelineAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stageBeforeAction = CurrentSession.Stage;
            var action = PipelineStateMachine.GetNextAdvanceAction(stageBeforeAction, shouldRunDiarization);
            switch (action)
            {
                case null:
                    return;
                case PipelineAdvanceAction.Transcribe:
                    await TranscribeMediaAsync(
                        progress,
                        GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    break;
                case PipelineAdvanceAction.Diarize:
                    await _diarizationStageOrchestrator.ExecuteAsync(
                        GetStageContext(remainingStages, SessionWorkflowStage.Diarized, stageProgress) is { } stageContext
                            ? stageContext.ToShared()
                            : null,
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    break;
                case PipelineAdvanceAction.TranslateAndDubFromTranscript:
                    await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                        progress,
                        GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                        GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    return;
                case PipelineAdvanceAction.GenerateTts:
                    await GenerateTtsAsync(
                        progress,
                        null,
                        GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                        cancellationToken);
                    if (CurrentSession.Stage <= stageBeforeAction)
                        throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {action} (still at {CurrentSession.Stage}).");
                    return;
                default:
                    throw new InvalidOperationException($"Unexpected pipeline advance action: {action}");
            }
        }
    }

    /// <summary>
    /// Continues pipeline execution after diarization by advancing through translation and TTS as needed.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for remaining continuation stages.</param>
    /// <param name="cancellationToken">Cancellation token used to stop continuation before completion.</param>
    /// <remarks>
    /// Requires <see cref="CurrentSession"/>.Stage to be at least <see cref="SessionWorkflowStage.Diarized"/>.
    /// Depending on the current stage, this operation may advance to <see cref="SessionWorkflowStage.Translated"/>
    /// and then <see cref="SessionWorkflowStage.TtsGenerated"/>. Stage transitions persist via stage methods
    /// that call <see cref="SaveCurrentSession"/> after successful completion.
    /// <summary>
        /// Continues the pipeline from a diarized session to the next configured stage(s).
        /// </summary>
        /// <remarks>
        /// Entry state: requires the current session stage to be at or past Diarized.
        /// On success: the session will advance to the next pipeline stage determined by the state machine
        /// (for example Translated or TtsGenerated) or remain unchanged if there is no continuation action.
        /// Session persistence is performed by the downstream pipeline stages that execute (translation or TTS).
        /// </remarks>
        /// <param name="progress">Optional overall progress reporter for the continuation operation.</param>
        /// <param name="cancellationToken">Token to observe for cancellation; operation will throw <see cref="OperationCanceledException"/> if cancelled.</param>
        /// <returns>A task that completes when the continuation (if any) finishes.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the current session stage is before Diarized.</exception>
    public Task ContinuePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ContinuePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Continues the pipeline from a diarized session toward translation and/or TTS according to the pipeline state machine.
    /// </summary>
    /// <remarks>
    /// Entry requirements: CurrentSession.Stage must be at or after <see cref="SessionWorkflowStage.Diarized"/>; otherwise an <see cref="InvalidOperationException"/> is thrown.
    /// On success:
    /// - If the state machine chooses <see cref="PipelineAdvanceAction.TranslateAndDubFromTranscript"/>, the method advances through translation and TTS, leaving the session at <see cref="SessionWorkflowStage.TtsGenerated"/> (translation and TTS stage contexts are used as provided).
    /// - If the state machine chooses <see cref="PipelineAdvanceAction.GenerateTts"/>, the method advances to <see cref="SessionWorkflowStage.TtsGenerated"/>.
    /// - If there is no continuation action, the session stage is unchanged.
    /// Persistence: this method does not directly persist session state; the invoked orchestrators/stage handlers are responsible for persisting changes.
    /// Cancellation: respects <paramref name="cancellationToken"/> and will observe cancellation propagated to the invoked orchestrators.
    /// </remarks>
    /// <param name="progress">Optional overall numeric progress reporter (0.0–1.0) used by invoked orchestrators.</param>
    /// <param name="stageProgress">Optional per-stage progress reporter used to build stage contexts for downstream orchestrators.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the continuation operation.</param>
    /// <returns>Completion of the pipeline continuation operation.</returns>
    internal async Task ContinuePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Diarized)
            throw new InvalidOperationException("Speaker mapping is not ready yet. Run the pipeline through diarization first.");

        var remainingStages = GetContinuationPipelineStages(CurrentSession.Stage);

        var continuationStageBeforeAction = CurrentSession.Stage;
        switch (PipelineStateMachine.GetContinuationActionAfterDiarized(CurrentSession.Stage))
        {
            case null:
                return;
            case PipelineAdvanceAction.TranslateAndDubFromTranscript:
                await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                    progress,
                    GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                    GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                    cancellationToken);
                if (CurrentSession.Stage <= continuationStageBeforeAction)
                    throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {PipelineAdvanceAction.TranslateAndDubFromTranscript} (still at {CurrentSession.Stage}).");
                return;
            case PipelineAdvanceAction.GenerateTts:
                await GenerateTtsAsync(
                    progress,
                    null,
                    GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                    cancellationToken);
                if (CurrentSession.Stage <= continuationStageBeforeAction)
                    throw new InvalidOperationException($"Pipeline stalled: stage did not advance after {PipelineAdvanceAction.GenerateTts} (still at {CurrentSession.Stage}).");
                return;
            default:
                throw new InvalidOperationException("Unexpected continuation pipeline action.");
        }
    }

    /// <summary>
    /// Runs only the TTS stage for an already translated session.
    /// </summary>
    /// <param name="progress">Optional progress reporter for TTS stage execution.</param>
    /// <param name="voice">Optional voice override; when null the configured session/provider voice is used.</param>
    /// <param name="cancellationToken">Cancellation token used to stop TTS generation before completion.</param>
    /// <remarks>
    /// Requires <see cref="CurrentSession"/>.Stage to be at least <see cref="SessionWorkflowStage.Translated"/>.
    /// On success, advances and persists the session to <see cref="SessionWorkflowStage.TtsGenerated"/>.
    /// <summary>
        /// Generate TTS audio for the current session using the existing translation.
        /// </summary>
        /// <param name="progress">Progress reporter for overall TTS generation progress (0.0–1.0).</param>
        /// <param name="voice">Optional voice identifier to use for generation; if null the session/default voice is used.</param>
        /// <param name="cancellationToken">Token to observe for cancellation of the operation.</param>
        /// <remarks>
        /// Entry requirements: the session must have reached the Translated stage; otherwise an <see cref="InvalidOperationException"/> is thrown.
        /// On success the session will progress to the TtsGenerated stage; session persistence is performed by the TTS pipeline and not directly by this wrapper.
        /// The method observes <paramref name="cancellationToken"/> and may throw <see cref="OperationCanceledException"/> when canceled.
        /// </remarks>
    public Task RunTtsOnlyAsync(
        IProgress<double>? progress = null,
        string? voice = null,
        CancellationToken cancellationToken = default) =>
        RunTtsOnlyAsync(progress, voice, stageProgress: null, cancellationToken);

    /// <summary>
    /// Runs only the TTS stage using the existing translation; requires a translated session and results in the session reaching the TtsGenerated stage on success.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for the TTS pipeline.</param>
    /// <param name="voice">Optional voice override to use for synthesis.</param>
    /// <param name="stageProgress">Optional per-stage progress updates.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <exception cref="InvalidOperationException">Thrown if the current session has not been translated (i.e., <see cref="SessionWorkflowStage"/> is less than <c>Translated</c>).</exception>
    /// <remarks>
    /// Entry state: requires <see cref="CurrentSession.Stage"/> >= <c>Translated</c>.
    /// Exit state on success: <see cref="CurrentSession.Stage"/> will be set to <c>TtsGenerated</c> and session state will be persisted.
    /// Cancellation: honors <paramref name="cancellationToken"/> and may throw <see cref="OperationCanceledException"/> if canceled.
    /// <summary>
    /// Runs only the text-to-speech generation stage using the existing translation.
    /// </summary>
    /// <remarks>
    /// Entry state: requires <see cref="CurrentSession.Stage"/> to be at or after <see cref="SessionWorkflowStage.Translated"/>.
    /// Exit state: on success the session advances to <see cref="SessionWorkflowStage.TtsGenerated"/> (persistence is performed by the TTS pipeline, not by this method).
    /// Cancellation: the operation observes the supplied <paramref name="cancellationToken"/> and may throw <see cref="OperationCanceledException"/> if cancelled.
    /// </remarks>
    /// <param name="progress">Optional progress reporter for audio-generation progress (0.0–1.0).</param>
    /// <param name="voice">Optional voice identifier to use for generation; when null the default voice is used.</param>
    /// <param name="stageProgress">Optional pipeline stage progress reporter used to report stage-level updates.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <exception cref="InvalidOperationException">Thrown when the current session has not been translated and therefore cannot generate TTS.</exception>
    internal async Task RunTtsOnlyAsync(
        IProgress<double>? progress,
        string? voice,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Translated)
            throw new InvalidOperationException("No translation is available. Continue the pipeline through translation first.");

        var remainingStages = GetContinuationPipelineStages(CurrentSession.Stage);
        await GenerateTtsAsync(
            progress,
            voice,
            GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
            cancellationToken);
    }

    /// <summary>
    /// Determines whether diarization is enabled in the current session settings.
    /// </summary>
    /// <summary>
        /// Determines whether diarization is enabled in the current settings.
        /// </summary>
        /// <returns><c>true</c> if <see cref="CurrentSettings.DiarizationProvider"/> contains a non-empty identifier; <c>false</c> otherwise.</returns>
    private bool ShouldRunDiarization() =>
        !string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider);

    /// <summary>
            /// Normalize a pipeline language hint into the canonical language code used by the pipeline.
            /// </summary>
            /// <param name="raw">The raw language hint or code; may be null or empty.</param>
            /// <param name="nonNormalizedFallback">A fallback language code used when <paramref name="raw"/> is null, empty, or cannot be normalized.</param>
            /// <returns>The normalized language code to use for pipeline stages.</returns>
            private static string NormalizePipelineLanguage(string? raw, string nonNormalizedFallback) =>
        Babel.Player.Services.Orchestration.PipelineStageReporter.NormalizePipelineLanguage(
            raw,
            nonNormalizedFallback);

    /// <summary>
    /// Resets the pipeline state to MediaLoaded and re-runs transcription, optionally continuing through remaining downstream stages.
    /// </summary>
    /// <remarks>
    /// Entry state: expects the session to have media ingested (a valid source media path) since the pipeline is reset to MediaLoaded.
    /// On success:
    /// - If <paramref name="remainingDownstream"/> is true, the method advances the pipeline from MediaLoaded through whatever downstream stages are required (the final stage depends on pipeline progression).
    /// - If <paramref name="remainingDownstream"/> is false, the method runs only the transcription stage and leaves the session at <see cref="SessionWorkflowStage.Transcribed"/>.
    /// The method persists the session immediately after resetting the pipeline (synchronous save) and delegates persistence of later stage changes to downstream operations invoked.
    /// Cancellation: honors the provided <paramref name="cancellationToken"/> and will propagate cancellation to the advanced or transcription operations, which may throw <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <param name="remainingDownstream">If true, continue running the pipeline after transcription (advance through remaining stages); if false, run only the transcription stage.</param>
    /// <param name="stageProgress">Optional progress reporter that will be forwarded to downstream stage execution for progress updates.</param>
    /// <summary>
    /// Resets the pipeline to the "MediaLoaded" state and re-runs transcription, optionally continuing with downstream stages.
    /// </summary>
    /// <remarks>
    /// Entry state: any stage (method resets session state to MediaLoaded). 
    /// On success: if <paramref name="remainingDownstream"/> is true, advances the pipeline through remaining stages; otherwise leaves the session at Transcribed.
    /// The method persists the session state synchronously after resetting to MediaLoaded.
    /// Cancellation: the operation observes <paramref name="cancellationToken"/> and may be canceled while advancing or transcribing (causing an <see cref="OperationCanceledException"/>).</remarks>
    /// <param name="remainingDownstream">If true, runs transcription and then advances the pipeline through remaining downstream stages; if false, runs transcription only and stops at the Transcribed stage.</param>
    /// <param name="stageProgress">Optional progress reporter for pipeline stage updates.</param>
    /// <param name="cancellationToken">Cancellation token to observe while performing downstream operations.</param>
    internal async Task RerunTranscriptionAsync(
        bool remainingDownstream,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineToMediaLoaded();
        SaveCurrentSession();

        if (remainingDownstream)
        {
            await AdvancePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleStage = new[] { SessionWorkflowStage.Transcribed };
        await TranscribeMediaAsync(
            null,
            GetStageContext(singleStage, SessionWorkflowStage.Transcribed, stageProgress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reruns diarization for the current session and updates pipeline state; then either stops, continues, or advances the pipeline based on the caller's intent.
    /// </summary>
    /// <remarks>
    /// Entry state: intended to run on a session that contains transcribed audio (the method will operate correctly if transcription output exists).  
    /// Exit state on success: the session stage will be left at one of Transcribed, Diarized, or Translated depending on whether diarization markers are present and whether downstream stages are requested.  
    /// Persistence: when the pipeline is reset to Transcribed, Diarized, or Translated this method saves the session state via SaveCurrentSession().  
    /// Cancellation: the provided <paramref name="cancellationToken"/> is honored and will cause the operation to cancel when requested.
    /// </remarks>
    /// <param name="remainingDownstream">If true, continue executing downstream pipeline stages after rerunning diarization; if false, only rerun diarization and return.</param>
    /// <param name="stageProgress">Optional progress reporter used when continuing or advancing pipeline stages.</param>
    /// <summary>
    /// Re-runs diarization for the current session and advances or continues the pipeline according to the configured downstream behavior.
    /// </summary>
    /// <remarks>
    /// Entry state: can be called from any session stage; the method detects whether the session previously produced translated output to determine downstream effects.
    /// On success:
    /// - If <paramref name="remainingDownstream"/> is false, the method updates session state to preserve translated artifacts when speaker assignments changed and persists the session, then returns.
    /// - If <paramref name="remainingDownstream"/> is true and the session contains diarization markers, the method resets the pipeline to the Diarized stage, persists the session, and continues the pipeline.
    /// - If <paramref name="remainingDownstream"/> is true and no diarization markers exist, the method resets the pipeline to the Transcribed stage, persists the session, and advances the pipeline from there.
    /// Persistence: the session is persisted synchronously via SaveCurrentSession() whenever the pipeline is reset to Transcribed, Diarized, or Translated as part of this operation.
    /// Cancellation: the provided <paramref name="cancellationToken"/> is observed by the diarization operation and by downstream continuation/advancement calls; the method will throw on cancellation if the underlying calls do so.
    /// </remarks>
    /// <param name="remainingDownstream">If true, continue executing downstream pipeline stages after diarization; if false, only update session state and do not advance the pipeline.</param>
    /// <param name="stageProgress">Optional progress reporter to receive pipeline stage updates while continuing or advancing the pipeline.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    internal async Task RerunDiarizationAsync(
        bool remainingDownstream,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        var hadTranslatableOutput = CurrentSession.Stage >= SessionWorkflowStage.Translated;
        var speakerAssignmentsChanged = await RunDiarizationAsync(cancellationToken).ConfigureAwait(false);

        if (!remainingDownstream)
        {
            if (speakerAssignmentsChanged && hadTranslatableOutput)
            {
                ResetPipelineToTranslated();
                SaveCurrentSession();
            }

            return;
        }

        if (HasDiarizationMarker(CurrentSession))
        {
            ResetPipelineToDiarized();
            SaveCurrentSession();
            await ContinuePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ResetPipelineToTranscribed();
            SaveCurrentSession();
            await AdvancePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-run translation from the current transcript; optionally continue with TTS.
    /// </summary>
    internal async Task RerunTranslationAsync(
        bool remainingDownstream,
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineForTranslationRetry();

        if (remainingDownstream)
        {
            if (CurrentSession.Stage >= SessionWorkflowStage.Diarized)
                await ContinuePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
            else
                await AdvancePipelineAsync(null, stageProgress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var singleStage = new[] { SessionWorkflowStage.Translated };
        await TranslateTranscriptAsync(
            null,
            null,
            null,
            GetStageContext(singleStage, SessionWorkflowStage.Translated, stageProgress),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-generate dub (TTS) from the current translation artifact.
    /// <summary>
    /// Resets the pipeline to the Translated stage and regenerates dub (TTS) from the existing translation.
    /// </summary>
    /// <remarks>
    /// On entry, the current session is reset to the Translated stage. This method persists that reset synchronously.
    /// On success, the session will progress to the TtsGenerated stage (persistence of TTS-specific fields is performed by the TTS pipeline). 
    /// Cancellation is observed and may cause the operation to throw <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <param name="stageProgress">Optional progress reporter to receive pipeline stage updates during TTS generation.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    internal async Task RerunDubAsync(
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineToTranslated();
        SaveCurrentSession();
        await RunTtsOnlyAsync(null, null, stageProgress, cancellationToken).ConfigureAwait(false);
    }
}