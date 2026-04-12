using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    public Task TranscribeMediaAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        TranscribeMediaAsync(progress, stageContext: null, cancellationToken);

    /// <summary>
    /// Transcribes the session's ingested media, writes the transcript to the session directory, updates session state, and optionally runs diarization.
    /// </summary>
    /// <param name="progress">Optional progress reporter receiving values from 0 to 1 for the overall transcription stage.</param>
    /// <param name="stageContext">Optional pipeline stage context used for reporting stage-specific updates.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no media is loaded, when a required model download fails, or when transcription itself fails.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the ingested media file cannot be found on disk.</exception>
    /// <exception cref="PipelineProviderException">Thrown when the configured transcription provider/runtime is not ready for execution and the blocking reason prevents continuation.</exception>
    internal async Task TranscribeMediaAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No media loaded. Please load media first.");

        if (!File.Exists(CurrentSession.IngestedMediaPath))
            throw new FileNotFoundException($"Ingested media file not found: {CurrentSession.IngestedMediaPath}");

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

        ReportStage(
            stageContext,
            $"Starting transcription with {CurrentSettings.TranscriptionProvider} / {CurrentSettings.TranscriptionModel}. Audio will be segmented and the spoken language will be detected before translation.",
            progress01: 0,
            isIndeterminate: true);

        var sessionDir = GetSessionDirectory();
        var transcriptDir = Path.Combine(sessionDir, "transcripts");
        Directory.CreateDirectory(transcriptDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.IngestedMediaPath);
        var transcriptPath = Path.Combine(transcriptDir, $"{fileName}.json");

        var cpuThreads = CurrentSettings.TranscriptionCpuThreads > 0
            ? CurrentSettings.TranscriptionCpuThreads.ToString()
            : "auto";
        var cpuWorkers = Math.Max(1, CurrentSettings.TranscriptionNumWorkers);
        var routeSummary =
            $"provider={CurrentSettings.TranscriptionProvider}, model={CurrentSettings.TranscriptionModel}, " +
            $"cpu_compute={CurrentSettings.TranscriptionCpuComputeType}, cpu_threads={cpuThreads}, cpu_workers={cpuWorkers}";
        var hwSummary =
            $"avx2={(HardwareSnapshot.HasAvx2 ? "yes" : "no")}, " +
            $"avx512={(HardwareSnapshot.HasAvx512F ? "yes" : "no")}, " +
            $"cuda={(HardwareSnapshot.HasCuda ? "yes" : "no")}";

        _log.Info($"Starting transcription: {CurrentSession.IngestedMediaPath} " +
                  $"[{CurrentSettings.TranscriptionProvider}/{CurrentSettings.TranscriptionModel}] " +
                  $"route=({routeSummary}) hw=({hwSummary})");

        var result = await _transcriptionService.TranscribeAsync(
            new TranscriptionRequest(
                CurrentSession.IngestedMediaPath,
                transcriptPath,
                CurrentSettings.TranscriptionModel,
                null,
                CurrentSettings.TranscriptionCpuComputeType,
                CurrentSettings.TranscriptionCpuThreads,
                CurrentSettings.TranscriptionNumWorkers),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown transcription error";
            _log.Error($"Transcription failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Transcription failed: {errorMsg}");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = transcriptPath,
            SourceLanguage = result.Language,
            TranscribedAtUtc = nowUtc,
            TranscriptionRuntime = CurrentSettings.TranscriptionRuntime,
            TranscriptionProvider = CurrentSettings.TranscriptionProvider,
            TranscriptionModel = CurrentSettings.TranscriptionModel,
            StatusMessage = ShouldPauseForSpeakerMapping()
                ? $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for speaker mapping."
                : $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
        };

        _log.Info($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        SaveCurrentSession();

        ReportStage(
            stageContext,
            $"Transcription complete. {result.Segments.Count} segments were detected in {result.Language}.",
            progress01: 1,
            isIndeterminate: false);
    }

    public Task TranslateTranscriptAsync(
        IProgress<double>? progress = null,
        string? targetLanguage = null,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        TranslateTranscriptAsync(progress, targetLanguage, sourceLanguage, stageContext: null, cancellationToken);

    internal async Task TranslateTranscriptAsync(
        IProgress<double>? progress,
        string? targetLanguage,
        string? sourceLanguage,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        if (!File.Exists(CurrentSession.TranscriptPath))
            throw new FileNotFoundException($"Transcript file not found: {CurrentSession.TranscriptPath}");

        var lang = targetLanguage ?? CurrentSettings.TargetLanguage;
        var src = sourceLanguage ?? CurrentSession.SourceLanguage ?? "auto";

        ReportStage(
            stageContext,
            $"Checking translation runtime, provider readiness, language routing, and model availability for {CurrentSettings.TranslationProvider} / {CurrentSettings.TranslationModel}…",
            progress01: 0,
            isIndeterminate: true);

        var downloadProgress = CreateStageDownloadProgress(
            stageContext,
            progress,
            $"Preparing translation model '{CurrentSettings.TranslationModel}'");
        await EnsureTranslationExecutionReadyAsync(downloadProgress, cancellationToken);

        _translationService ??= CreateTranslationService();

        ReportStage(
            stageContext,
            $"Running translation from {src} to {lang} with {CurrentSettings.TranslationProvider} / {CurrentSettings.TranslationModel}. Segment text will be rewritten into the target language for dubbing.",
            progress01: 0,
            isIndeterminate: true);

        var sessionDir = GetSessionDirectory();
        var translationDir = Path.Combine(sessionDir, "translations");
        Directory.CreateDirectory(translationDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranscriptPath);
        var translationPath = Path.Combine(translationDir, $"{fileName}_{lang}.json");

        _log.Info($"Starting translation: {CurrentSession.TranscriptPath} ({src} -> {lang})");

        var result = await _translationService.TranslateAsync(
            new TranslationRequest(
                CurrentSession.TranscriptPath,
                translationPath,
                src,
                lang,
                CurrentSettings.TranslationModel),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown translation error";
            _log.Error($"Translation failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"Translation failed: {errorMsg}");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Translated,
            TranslationPath = translationPath,
            SourceLanguage = src,
            TargetLanguage = lang,
            TranslatedAtUtc = nowUtc,
            TranslationRuntime = CurrentSettings.TranslationRuntime,
            TranslationProvider = CurrentSettings.TranslationProvider,
            TranslationModel = CurrentSettings.TranslationModel,
            StatusMessage = $"Translated {result.Segments.Count} segments to {lang}. Ready for TTS/dubbing.",
        };

        _log.Info($"Translation complete: {result.Segments.Count} segments, {src} -> {lang}");
        SaveCurrentSession();

        ReportStage(
            stageContext,
            $"Translation complete. {result.Segments.Count} segments were translated from {src} to {lang}.",
            progress01: 1,
            isIndeterminate: false);
    }

    public Task GenerateTtsAsync(
        IProgress<double>? progress = null,
        string? voice = null,
        CancellationToken cancellationToken = default) =>
        GenerateTtsAsync(progress, voice, stageContext: null, cancellationToken);

    /// <summary>
    /// Generate per-segment TTS clips for the current translation, stitch them into a combined dub audio file, and update the session state.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter (0.0–1.0) used for stage updates.</param>
    /// <param name="voice">Optional voice identifier to use; if null, the configured TTS voice is used.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <param name="stageContext">Optional context used to report pipeline stage messages and progress.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no translation is available, a required voice model download fails, or when zero segment clips were produced.
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation file referenced by the session cannot be found.</exception>
    /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider/runtime is not ready and cannot proceed.</exception>
    internal async Task GenerateTtsAsync(
        IProgress<double>? progress,
        string? voice,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(CurrentSession.TranslationPath))
            throw new InvalidOperationException("No translation available. Please translate first.");

        if (!File.Exists(CurrentSession.TranslationPath))
            throw new FileNotFoundException($"Translation file not found: {CurrentSession.TranslationPath}");

        var v = voice ?? CurrentSettings.TtsVoice;

        ReportStage(
            stageContext,
            $"Checking TTS runtime, provider readiness, voice assets, and speaker/reference setup for {CurrentSettings.TtsProvider} / {v}…",
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
                v,
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
                $"Preparing TTS voice '{v}'");
            if (!await TtsRegistry.EnsureModelAsync(
                    CurrentSettings.TtsProvider,
                    v,
                    CurrentSettings,
                    downloadProgress,
                    cancellationToken,
                    CurrentSettings.TtsProfile,
                    KeyStore))
            {
                throw new InvalidOperationException($"Failed to download voice '{v}'.");
            }
        }

        _ttsService ??= CreateTtsService();
                await EnsureSingleSpeakerQwenReferenceClipAsync(cancellationToken);
        await EnsureMultiSpeakerReferenceClipsAsync(cancellationToken);

        ReportStage(
            stageContext,
            $"Starting TTS synthesis with {CurrentSettings.TtsProvider} / {v}. Generating combined dub audio — progress will appear below.",
            progress01: 0,
            isIndeterminate: false);

        var sessionDir = GetSessionDirectory();
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath);
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{v}.mp3");

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var ttsLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        ReportStage(
            stageContext,
            "Generating per-segment dubbed clips for preview, seek, and segment-level refinement…",
            progress01: 0,
            isIndeterminate: true);

        var mediaName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!);
        var segmentsDir = Path.Combine(ttsDir, "segments", mediaName);
        Directory.CreateDirectory(segmentsDir);

        var segmentAudioPaths = new ConcurrentDictionary<string, string>();
        int totalSegments = 0;
        try
        {
            var translationData = await _artifactReader.LoadTranslationAsync(CurrentSession.TranslationPath, cancellationToken);
            var candidateSegments = translationData.Segments?
                .Where(seg => !string.IsNullOrWhiteSpace(seg.Id) && !string.IsNullOrWhiteSpace(seg.TranslatedText))
                .ToList()
                ?? [];

            totalSegments = candidateSegments.Count;
            int completed = 0;
            int parallelism = Math.Max(1, Math.Min(_ttsService.MaxConcurrency, candidateSegments.Count));

            ReportStage(
                stageContext,
                $"Generating {totalSegments} segment clips (concurrency={parallelism})…",
                progress01: 0,
                isIndeterminate: true);

            await Parallel.ForEachAsync(
                candidateSegments,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (seg, ct) =>
                {
                    var id = seg.Id;
                    var text = seg.TranslatedText;

                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id))
                    {
                        _log.Info($"Skipping segment {id}: empty text or ID");
                        return;
                    }

                    var segmentAudioPath = Path.Combine(segmentsDir, $"{id}.mp3");
                    var resolvedVoice = ResolveVoiceForSegment(seg, v);
                    var referenceAudioPath = ResolveReferenceAudioForSegment(seg);

                    _log.Info($"Generating TTS for segment {id} (voice={resolvedVoice}, speaker={seg.SpeakerId ?? "<none>"}): {text[..Math.Min(30, text.Length)]}...");

                    try
                    {
                        var segTask = _ttsService.GenerateSegmentTtsAsync(
                            new SingleSegmentTtsRequest(
                                text,
                                segmentAudioPath,
                                resolvedVoice,
                                seg.SpeakerId,
                                referenceAudioPath,
                                Language: ttsLanguage,
                                SourceVideoPath: CurrentSession.IngestedMediaPath ?? CurrentSession.SourceMediaPath),
                            ct);
                        _pendingTtsTasks.Add(segTask);
                        var segResult = await segTask;

                        if (segResult.Success && File.Exists(segmentAudioPath))
                        {
                            segmentAudioPaths[id] = segmentAudioPath;
                            var done = Interlocked.Increment(ref completed);
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
                });

            var orderedPaths = new List<string>();
            foreach (var seg in candidateSegments)
            {
                if (seg.Id != null && segmentAudioPaths.TryGetValue(seg.Id, out var path))
                {
                    orderedPaths.Add(path);
                }
            }

            if (orderedPaths.Count == 0)
                throw new InvalidOperationException(
                    "No eligible segment audio files were produced. Stitching cannot proceed. Check provider configuration and logs.");

            _log.Info($"Stitching {orderedPaths.Count} segment clips into combined dub file...");
            ReportStage(
                stageContext,
                "Stitching segment clips into combined dub file…",
                progress01: 1,
                isIndeterminate: true);
                
            if (_audioProcessingService is not null)
            {
                await _audioProcessingService.CombineAudioSegmentsAsync(orderedPaths, ttsPath, cancellationToken);
            }
            else
            {
                _log.Warning("Audio processing service unavailable. Skipping audio concatenation.");
            }


            if (!File.Exists(ttsPath))
                throw new InvalidOperationException(
                    $"Stitching completed but combined dub file was not created at '{ttsPath}'. Check ffmpeg output and disk permissions.");

            _log.Info($"TTS combined complete: {ttsPath}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"TTS stage failed: {ex.Message}", ex);
            throw;
        }

        int succeeded = segmentAudioPaths.Count;
        if (totalSegments > 0 && succeeded == 0)
        {
            _log.Error("TTS stage completed but no segments were generated.", new InvalidOperationException("Zero TTS segments"));
            throw new InvalidOperationException(
                "TTS stage completed but no segments were generated. Check provider configuration and logs.");
        }

        string ttsStatusMessage = (succeeded == totalSegments)
            ? $"TTS generated ({v}). Dubbing complete."
            : $"TTS generated ({v}). {succeeded}/{totalSegments} segments ready — {totalSegments - succeeded} failed.";

        var nowUtc = DateTimeOffset.UtcNow;
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = ttsPath,
            TtsVoice = v,
            TtsGeneratedAtUtc = nowUtc,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = new Dictionary<string, string>(segmentAudioPaths),
            TtsRuntime = CurrentSettings.TtsRuntime,
            TtsProvider = CurrentSettings.TtsProvider,
            StatusMessage = ttsStatusMessage,
        };

        SaveCurrentSession();

        ReportStage(
            stageContext,
            $"Dub complete. {succeeded}/{totalSegments} segment clips are ready with voice {v}.",
            progress01: 1,
            isIndeterminate: false);
    }

    public Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        AdvancePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Advances the current session through any remaining pipeline stages (transcription, translation, and TTS) in order.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter receiving values from 0.0 to 1.0 for the combined operation.</param>
    /// <param name="stageProgress">Optional per-stage progress reporter that receives detailed stage updates.</param>
    internal async Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        var pauseAfterDiarization = ShouldPauseForSpeakerMapping()
            && CurrentSession.Stage < SessionWorkflowStage.Diarized;
        var remainingStages = GetAdvancePipelineStages(CurrentSession.Stage, pauseAfterDiarization);
        var stage = CurrentSession.Stage;

        if (stage < SessionWorkflowStage.Transcribed)
        {
            await TranscribeMediaAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                cancellationToken);
        }

        stage = CurrentSession.Stage;
        if (pauseAfterDiarization && stage < SessionWorkflowStage.Diarized)
        {
            await ExecuteDiarizationStageAsync(
                GetStageContext(remainingStages, SessionWorkflowStage.Diarized, stageProgress),
                cancellationToken);
            return;
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.Translated)
        {
            await TranslateTranscriptAsync(
                progress,
                null,
                null,
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                cancellationToken);
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.TtsGenerated)
        {
            await GenerateTtsAsync(
                progress,
                null,
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
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
    /// </remarks>
    public Task ContinuePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        ContinuePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Continues pipeline execution after diarization using stage-aware progress reporting.
    /// </summary>
    /// <param name="progress">Optional overall progress reporter for remaining continuation stages.</param>
    /// <param name="stageProgress">Optional per-stage progress/status updates for translation and TTS stages.</param>
    /// <param name="cancellationToken">Cancellation token used to stop continuation before completion.</param>
    /// <remarks>
    /// Entry requires stage <see cref="SessionWorkflowStage.Diarized"/> or later. This method advances the
    /// session toward <see cref="SessionWorkflowStage.TtsGenerated"/> by running translation when below
    /// <see cref="SessionWorkflowStage.Translated"/> and then running TTS when below
    /// <see cref="SessionWorkflowStage.TtsGenerated"/>. Successful stage completions persist updates to
    /// <see cref="CurrentSession"/>. Cancellation propagates via <paramref name="cancellationToken"/>.
    /// </remarks>
    internal async Task ContinuePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (CurrentSession.Stage < SessionWorkflowStage.Diarized)
            throw new InvalidOperationException("Speaker mapping is not ready yet. Run the pipeline through diarization first.");

        var remainingStages = GetContinuationPipelineStages(CurrentSession.Stage);
        var stage = CurrentSession.Stage;

        if (stage < SessionWorkflowStage.Translated)
        {
            await TranslateTranscriptAsync(
                progress,
                null,
                null,
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                cancellationToken);
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.TtsGenerated)
        {
            await GenerateTtsAsync(
                progress,
                null,
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
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
    /// </remarks>
    public Task RunTtsOnlyAsync(
        IProgress<double>? progress = null,
        string? voice = null,
        CancellationToken cancellationToken = default) =>
        RunTtsOnlyAsync(progress, voice, stageProgress: null, cancellationToken);

    /// <summary>
    /// Runs only the TTS stage for an already translated session with stage-aware progress updates.
    /// </summary>
    /// <param name="progress">Optional progress reporter for TTS stage execution.</param>
    /// <param name="voice">Optional voice override; when null the configured session/provider voice is used.</param>
    /// <param name="stageProgress">Optional stage progress updates describing TTS stage activity.</param>
    /// <param name="cancellationToken">Cancellation token used to stop TTS generation before completion.</param>
    /// <remarks>
    /// Entry requires stage <see cref="SessionWorkflowStage.Translated"/> or later. This method executes only
    /// TTS and advances toward terminal stage <see cref="SessionWorkflowStage.TtsGenerated"/>; persistence occurs
    /// when TTS completes and updates <see cref="CurrentSession"/>. Cancellation propagates via
    /// <paramref name="cancellationToken"/>.
    /// </remarks>
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

    private async Task ExecuteDiarizationStageAsync(
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.IngestedMediaPath))
            throw new InvalidOperationException("No ingested media is available for speaker mapping.");
        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("No transcript is available for speaker mapping.");

        ReportStage(
            stageContext,
            $"Running {CurrentSettings.DiarizationProvider} diarization to identify speakers before translation and dubbing…",
            progress01: 0,
            isIndeterminate: true);

        var outcome = await ExecuteDiarizationAsync(
            CurrentSession.IngestedMediaPath,
            CurrentSession.TranscriptPath,
            cancellationToken,
            resultingStage: SessionWorkflowStage.Diarized,
            statusMessage: "Speaker mapping ready. Assign voices, then continue.");

        ReportStage(
            stageContext,
            $"Speaker mapping ready. Identified {outcome.SpeakerCount} speakers across {outcome.SegmentCount} segments.",
            progress01: 1,
            isIndeterminate: false);
    }

    private bool ShouldPauseForSpeakerMapping() =>
        CurrentSession.MultiSpeakerEnabled
        && !string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider);
}
