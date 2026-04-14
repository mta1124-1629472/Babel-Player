using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
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

        await EnsureTranscriptionProviderReadyAsync(progress, stageContext, cancellationToken);

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

        var transcriptionService = _transcriptionService ??= CreateTranscriptionService();
        var result = await transcriptionService.TranscribeAsync(
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

        CommitTranscriptionSessionState(result, transcriptPath);

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

        CommitTranslationSessionState(result, translationPath, src, lang);

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

        await EnsureTtsProviderReadyAsync(v, progress, stageContext, cancellationToken);

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
        var ttsLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        var segmentsDir = Path.Combine(ttsDir, "segments", Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!));
        Directory.CreateDirectory(segmentsDir);

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var (segmentAudioPaths, totalSegments, orderedSegments) = await GenerateSegmentClipsAsync(
            v, ttsLanguage, segmentsDir, stageContext, cancellationToken);

        await StitchSegmentClipsAsync(segmentAudioPaths, orderedSegments, ttsPath, stageContext, cancellationToken);

        CommitTtsSessionState(v, ttsPath, segmentsDir, segmentAudioPaths, totalSegments, stageContext);
    }

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
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = transcriptPath,
            SourceLanguage = result.Language,
            TranscribedAtUtc = nowUtc,
            TranscriptionRuntime = CurrentSettings.TranscriptionRuntime,
            TranscriptionProvider = CurrentSettings.TranscriptionProvider,
            TranscriptionModel = CurrentSettings.TranscriptionModel,
            StatusMessage = ShouldRunDiarization()
                ? $"Transcribed {result.Segments.Count} segments ({result.Language}). Speaker mapping is available before translation."
                : $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
        };

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
            StatusMessage = $"Translated {result.Segments.Count} segments to {targetLanguage}. Ready for TTS/dubbing.",
        };

        _log.Info($"Translation complete: {result.Segments.Count} segments, {sourceLanguage} -> {targetLanguage}");
        SaveCurrentSession();
    }

    /// <summary>
    /// Checks TTS runtime and provider readiness, then downloads the voice model if needed.
    /// Mirrors the guard pattern used by TranscribeMediaAsync and TranslateTranscriptAsync.
    /// </summary>
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
    /// Loads the translation, dispatches per-segment TTS generation (Qwen batch or parallel generic),
    /// and returns the produced audio paths keyed by segment ID, the total candidate count,
    /// and the ordered segment list (for stitch ordering without a second disk read).
    /// </summary>
    private async Task<(ConcurrentDictionary<string, string> SegmentAudioPaths, int TotalSegments, IReadOnlyList<TranslationSegmentArtifact> OrderedSegments)> GenerateSegmentClipsAsync(
        string voice,
        string? ttsLanguage,
        string segmentsDir,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var segmentAudioPaths = new ConcurrentDictionary<string, string>();

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
        int parallelism = Math.Max(1, Math.Min(_ttsService!.MaxConcurrency, candidateSegments.Count));

        ReportStage(
            stageContext,
            $"Generating {totalSegments} segment clips (concurrency={parallelism})…",
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
                    cancellationToken);
            }
            else
            {
                int completed = 0;
                await Parallel.ForEachAsync(
                    candidateSegments,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    async (seg, ct) =>
                        await GenerateSingleSegmentAsync(
                            seg, voice, ttsLanguage, segmentsDir, segmentAudioPaths,
                            totalSegments, stageContext, ct,
                            onSucceeded: () => Interlocked.Increment(ref completed)));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error($"TTS stage failed: {ex.Message}", ex);
            throw;
        }

        return (segmentAudioPaths, totalSegments, candidateSegments);
    }

    /// <summary>
    /// Generates TTS audio for a single translation segment and records the output path on success.
    /// Called concurrently inside Parallel.ForEachAsync for non-Qwen providers.
    /// </summary>
    /// <summary>
    /// Generates TTS audio for a single translation segment and records the output path on success.
    /// Called concurrently inside Parallel.ForEachAsync for non-Qwen providers.
    /// <paramref name="onSucceeded"/> is invoked (thread-safely by the caller) to increment the shared
    /// progress counter; returning the new count lets this method report accurate progress without
    /// holding a ref parameter across an async boundary.
    /// </summary>
    private async Task GenerateSingleSegmentAsync(
        TranslationSegmentArtifact seg,
        string defaultVoice,
        string? ttsLanguage,
        string segmentsDir,
        ConcurrentDictionary<string, string> segmentAudioPaths,
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
            var segTask = _ttsService!.GenerateSegmentTtsAsync(
                new SingleSegmentTtsRequest(
                    text,
                    segmentAudioPath,
                    resolvedVoice,
                    seg.SpeakerId,
                    referenceAudioPath,
                    Language: ttsLanguage,
                    SourceVideoPath: CurrentSession.IngestedMediaPath ?? CurrentSession.SourceMediaPath),
                cancellationToken);
            _pendingTtsTasks.Add(segTask);
            var segResult = await segTask;

            if (segResult.Success && File.Exists(segmentAudioPath))
            {
                segmentAudioPaths[id] = segmentAudioPath;
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
    /// </summary>
    private async Task StitchSegmentClipsAsync(
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

    /// <summary>
    /// Validates segment yield, advances session to TtsGenerated, persists state, and reports stage completion.
    /// </summary>
    private void CommitTtsSessionState(
        string voice,
        string ttsPath,
        string segmentsDir,
        ConcurrentDictionary<string, string> segmentAudioPaths,
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

        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = ttsPath,
            TtsVoice = voice,
            TtsGeneratedAtUtc = DateTimeOffset.UtcNow,
            TtsSegmentsPath = segmentsDir,
            TtsSegmentAudioPaths = new Dictionary<string, string>(segmentAudioPaths),
            TtsRuntime = CurrentSettings.TtsRuntime,
            TtsProvider = CurrentSettings.TtsProvider,
            StatusMessage = statusMessage,
        };

        SaveCurrentSession();

        ReportStage(
            stageContext,
            $"Dub complete. {succeeded}/{totalSegments} segment clips are ready with voice {voice}.",
            progress01: 1,
            isIndeterminate: false);
    }

    private async Task GenerateQwenBatchSegmentAudioAsync(
        QwenContainerTtsProvider qwenProvider,
        IReadOnlyList<TranslationSegmentArtifact> candidateSegments,
        string segmentsDir,
        string defaultVoice,
        string? ttsLanguage,
        PipelineStageContext? stageContext,
        ConcurrentDictionary<string, string> segmentAudioPaths,
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

    private async Task ExecuteStreamingPipelineAsync(
        IProgress<double>? progress,
        PipelineStageContext? transcriptionStageContext,
        PipelineStageContext? translationStageContext,
        PipelineStageContext? ttsStageContext,
        CancellationToken cancellationToken)
    {
        await EnsureTranscriptionProviderReadyAsync(progress, transcriptionStageContext, cancellationToken);

        if (_transcriptionService is not IStreamingTranscriptionProvider streamingProvider)
        {
            await TranscribeMediaAsync(progress, transcriptionStageContext, cancellationToken);
            await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                progress,
                translationStageContext,
                ttsStageContext,
                cancellationToken);
            return;
        }

        ReportStage(
            transcriptionStageContext,
            $"Starting transcription with {CurrentSettings.TranscriptionProvider} / {CurrentSettings.TranscriptionModel}. Translation will begin as segments arrive.",
            progress01: 0,
            isIndeterminate: true,
            streamingStatus: "Downstream translation and dubbing will overlap with ASR output.");

        var transcriptPath = BuildTranscriptArtifactPath();
        var transcriptPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(transcriptPath);
        var targetLanguage = CurrentSettings.TargetLanguage;
        var translationPath = BuildTranslationArtifactPath(transcriptPath, targetLanguage);
        var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
        var voice = CurrentSettings.TtsVoice;
        var ttsLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        var (ttsPath, segmentsDir) = BuildTtsArtifacts(translationPath, voice);

        var transcriptArtifactWriter = new TranscriptArtifactStreamingWriter(
            transcriptPartialPath,
            CurrentSession.SourceLanguage ?? "unknown",
            0d);
        await transcriptArtifactWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var transcriptChannel = Channel.CreateBounded<TranscriptChannelItem>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var translationChannel = Channel.CreateBounded<TranslationChannelItem>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var ttsResultChannel = Channel.CreateBounded<TtsChannelItem>(new BoundedChannelOptions(Math.Max(4, _ttsService?.MaxConcurrency ?? 4))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var translationWriter = new TranslationArtifactStreamingWriter(
            translationPartialPath,
            CurrentSession.SourceLanguage ?? "unknown",
            targetLanguage);
        await translationWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var translationDownloadProgress = CreateStageDownloadProgress(
            translationStageContext,
            progress,
            $"Preparing translation model '{CurrentSettings.TranslationModel}'");
        await EnsureTranslationExecutionReadyAsync(translationDownloadProgress, cancellationToken).ConfigureAwait(false);
        _translationService ??= CreateTranslationService();
        await EnsureTtsProviderReadyAsync(voice, progress, ttsStageContext, cancellationToken).ConfigureAwait(false);
        _ttsService ??= CreateTtsService();
        await EnsureSingleSpeakerQwenReferenceClipAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMultiSpeakerReferenceClipsAsync(cancellationToken).ConfigureAwait(false);

        ReportStage(
            translationStageContext,
            $"Streaming translation to {targetLanguage} with {CurrentSettings.TranslationProvider} / {CurrentSettings.TranslationModel}.",
            progress01: 0,
            isIndeterminate: true,
            streamingStatus: "Dub generation will start as translated segments arrive.");
        ReportStage(
            ttsStageContext,
            $"Streaming TTS synthesis with {CurrentSettings.TtsProvider} / {voice}.",
            progress01: 0,
            isIndeterminate: true,
            streamingStatus: "Segment clips are generated as translation continues.");

        var ttsCollectorTask = CollectStreamingTtsResultsAsync(ttsResultChannel.Reader, ttsStageContext, cancellationToken);
        var ttsStageTask = RunStreamingTtsStageAsync(
            translationChannel.Reader,
            ttsResultChannel.Writer,
            voice,
            ttsLanguage,
            segmentsDir,
            cancellationToken);
        var translationTask = RunStreamingTranslationStageAsync(
            transcriptChannel.Reader,
            translationChannel.Writer,
            translationWriter,
            targetLanguage,
            translationStageContext,
            cancellationToken);

        var forwardingWriter = new TranscriptChannelForwardingWriter(transcriptArtifactWriter, transcriptChannel.Writer);
        TranscriptionResult transcriptionResult;
        try
        {
            transcriptionResult = await streamingProvider.TranscribeStreamingAsync(
                new TranscriptionRequest(
                    CurrentSession.IngestedMediaPath!,
                    transcriptPath,
                    CurrentSettings.TranscriptionModel,
                    null,
                    CurrentSettings.TranscriptionCpuComputeType,
                    CurrentSettings.TranscriptionCpuThreads,
                    CurrentSettings.TranscriptionNumWorkers),
                forwardingWriter,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            forwardingWriter.TryComplete(ex);
            throw;
        }
        finally
        {
            forwardingWriter.TryComplete();
        }

        await transcriptArtifactWriter.CompleteAsync(transcriptionResult, transcriptPath, cancellationToken).ConfigureAwait(false);
        CommitTranscriptionSessionState(transcriptionResult, transcriptPath);
        ReportStage(
            transcriptionStageContext,
            $"Transcription complete. {transcriptionResult.Segments.Count} segments were detected in {transcriptionResult.Language}.",
            progress01: 1,
            isIndeterminate: false);

        var translationResult = await translationTask.ConfigureAwait(false);
        await translationWriter.CompleteAsync(translationPath, cancellationToken).ConfigureAwait(false);
        CommitTranslationSessionState(translationResult, translationPath, translationResult.SourceLanguage, translationResult.TargetLanguage);
        ReportStage(
            translationStageContext,
            $"Translation complete. {translationResult.Segments.Count} segments were translated from {translationResult.SourceLanguage} to {translationResult.TargetLanguage}.",
            progress01: 1,
            isIndeterminate: false);

        await ttsStageTask.ConfigureAwait(false);
        var segmentAudioPaths = await ttsCollectorTask.ConfigureAwait(false);
        await StitchSegmentClipsAsync(segmentAudioPaths, translationWriter.OrderedSegments, ttsPath, ttsStageContext, cancellationToken).ConfigureAwait(false);
        CommitTtsSessionState(voice, ttsPath, segmentsDir, segmentAudioPaths, translationWriter.OrderedSegments.Count, ttsStageContext);
    }

    private async Task ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
        IProgress<double>? progress,
        PipelineStageContext? translationStageContext,
        PipelineStageContext? ttsStageContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(CurrentSession.TranscriptPath))
            throw new InvalidOperationException("No transcript available. Please transcribe media first.");

        var transcript = await _artifactReader.LoadTranscriptAsync(CurrentSession.TranscriptPath, cancellationToken).ConfigureAwait(false);
        var transcriptChannel = Channel.CreateBounded<TranscriptChannelItem>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var producerTask = Task.Run(async () =>
        {
            try
            {
                foreach (var segment in transcript.Segments ?? [])
                {
                    if (string.IsNullOrWhiteSpace(segment.Text))
                        continue;

                    var segmentId = SegmentId(segment.Start);
                    await transcriptChannel.Writer.WriteAsync(
                        new TranscriptChannelItem(
                            segmentId,
                            CloneTranscriptSegment(segment),
                            CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
                            transcript.LanguageProbability),
                        cancellationToken).ConfigureAwait(false);
                }

                transcriptChannel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                transcriptChannel.Writer.TryComplete(ex);
                throw;
            }
        }, cancellationToken);

        var targetLanguage = CurrentSettings.TargetLanguage;
        var translationPath = BuildTranslationArtifactPath(CurrentSession.TranscriptPath, targetLanguage);
        var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
        var voice = CurrentSettings.TtsVoice;
        var ttsLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        var (ttsPath, segmentsDir) = BuildTtsArtifacts(translationPath, voice);

        var translationDownloadProgress = CreateStageDownloadProgress(
            translationStageContext,
            progress,
            $"Preparing translation model '{CurrentSettings.TranslationModel}'");
        await EnsureTranslationExecutionReadyAsync(translationDownloadProgress, cancellationToken).ConfigureAwait(false);
        _translationService ??= CreateTranslationService();
        await EnsureTtsProviderReadyAsync(voice, progress, ttsStageContext, cancellationToken).ConfigureAwait(false);
        _ttsService ??= CreateTtsService();
        await EnsureSingleSpeakerQwenReferenceClipAsync(cancellationToken).ConfigureAwait(false);
        await EnsureMultiSpeakerReferenceClipsAsync(cancellationToken).ConfigureAwait(false);

        var translationWriter = new TranslationArtifactStreamingWriter(
            translationPartialPath,
            CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
            targetLanguage);
        await translationWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

        ReportStage(
            translationStageContext,
            $"Streaming translation to {targetLanguage} with {CurrentSettings.TranslationProvider} / {CurrentSettings.TranslationModel}.",
            progress01: 0,
            isIndeterminate: true,
            streamingStatus: "Dub generation will start as translated segments arrive.");
        ReportStage(
            ttsStageContext,
            $"Streaming TTS synthesis with {CurrentSettings.TtsProvider} / {voice}.",
            progress01: 0,
            isIndeterminate: true,
            streamingStatus: "Segment clips are generated as translation continues.");

        var translationChannel = Channel.CreateBounded<TranslationChannelItem>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var ttsResultChannel = Channel.CreateBounded<TtsChannelItem>(new BoundedChannelOptions(Math.Max(4, _ttsService?.MaxConcurrency ?? 4))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var ttsCollectorTask = CollectStreamingTtsResultsAsync(ttsResultChannel.Reader, ttsStageContext, cancellationToken);
        var ttsStageTask = RunStreamingTtsStageAsync(
            translationChannel.Reader,
            ttsResultChannel.Writer,
            voice,
            ttsLanguage,
            segmentsDir,
            cancellationToken);
        var translationTask = RunStreamingTranslationStageAsync(
            transcriptChannel.Reader,
            translationChannel.Writer,
            translationWriter,
            targetLanguage,
            translationStageContext,
            cancellationToken);

        await producerTask.ConfigureAwait(false);
        var translationResult = await translationTask.ConfigureAwait(false);
        await translationWriter.CompleteAsync(translationPath, cancellationToken).ConfigureAwait(false);
        CommitTranslationSessionState(translationResult, translationPath, translationResult.SourceLanguage, translationResult.TargetLanguage);
        ReportStage(
            translationStageContext,
            $"Translation complete. {translationResult.Segments.Count} segments were translated from {translationResult.SourceLanguage} to {translationResult.TargetLanguage}.",
            progress01: 1,
            isIndeterminate: false);

        await ttsStageTask.ConfigureAwait(false);
        var segmentAudioPaths = await ttsCollectorTask.ConfigureAwait(false);
        await StitchSegmentClipsAsync(segmentAudioPaths, translationWriter.OrderedSegments, ttsPath, ttsStageContext, cancellationToken).ConfigureAwait(false);
        CommitTtsSessionState(voice, ttsPath, segmentsDir, segmentAudioPaths, translationWriter.OrderedSegments.Count, ttsStageContext);
    }

    private async Task<TranslationResult> RunStreamingTranslationStageAsync(
        ChannelReader<TranscriptChannelItem> transcriptReader,
        ChannelWriter<TranslationChannelItem> translationWriter,
        TranslationArtifactStreamingWriter artifactWriter,
        string targetLanguage,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        string? sourceLanguage = null;

        try
        {
            await foreach (var item in transcriptReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                sourceLanguage ??= item.SourceLanguage;
                await artifactWriter.AppendPendingSegmentAsync(item, cancellationToken).ConfigureAwait(false);

                var result = await _translationService!.TranslateSingleSegmentAsync(
                    new SingleSegmentTranslationRequest(
                        item.Segment.Text ?? string.Empty,
                        item.SegmentId,
                        artifactWriter.PartialPath,
                        artifactWriter.PartialPath,
                        item.SourceLanguage,
                        targetLanguage,
                        CurrentSettings.TranslationModel),
                    cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    var errorMsg = result.ErrorMessage ?? "Unknown translation error";
                    _log.Error($"Streaming translation failed: {errorMsg}", new Exception(errorMsg));
                    throw new InvalidOperationException($"Translation failed: {errorMsg}");
                }

                await artifactWriter.ReloadFromDiskAsync(cancellationToken).ConfigureAwait(false);
                var translatedSegment = artifactWriter.OrderedSegments.FirstOrDefault(segment =>
                    string.Equals(segment.Id, item.SegmentId, StringComparison.Ordinal));
                if (translatedSegment is null)
                    throw new InvalidOperationException($"Translated segment '{item.SegmentId}' was not written to the partial artifact.");

                completed++;
                ReportStage(
                    stageContext,
                    $"Translated segment {completed}…",
                    progress01: 0,
                    isIndeterminate: true,
                    streamingStatus: "Dub is consuming translated segments in parallel.");
                await translationWriter.WriteAsync(
                    new TranslationChannelItem(
                        item.SegmentId,
                        CloneTranslationSegment(translatedSegment),
                        item.SourceLanguage,
                        targetLanguage),
                    cancellationToken).ConfigureAwait(false);
            }

            await artifactWriter.ReloadFromDiskAsync(cancellationToken).ConfigureAwait(false);
            var source = sourceLanguage ?? CurrentSession.SourceLanguage ?? "unknown";
            return BuildTranslationResult(artifactWriter.OrderedSegments, source, targetLanguage);
        }
        catch (Exception ex)
        {
            translationWriter.TryComplete(ex);
            throw;
        }
        finally
        {
            translationWriter.TryComplete();
        }
    }

    private async Task RunStreamingTtsStageAsync(
        ChannelReader<TranslationChannelItem> translationReader,
        ChannelWriter<TtsChannelItem> resultWriter,
        string defaultVoice,
        string? ttsLanguage,
        string segmentsDir,
        CancellationToken cancellationToken)
    {
        var parallelism = Math.Max(1, _ttsService?.MaxConcurrency ?? 1);
        var semaphore = new SemaphoreSlim(parallelism, parallelism);
        var tasks = new List<Task>();

        try
        {
            await foreach (var item in translationReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await GenerateStreamingTtsSegmentAsync(
                            item,
                            defaultVoice,
                            ttsLanguage,
                            segmentsDir,
                            resultWriter,
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            resultWriter.TryComplete();
        }
        catch (Exception ex)
        {
            resultWriter.TryComplete(ex);
            throw;
        }
    }

    private async Task<ConcurrentDictionary<string, string>> CollectStreamingTtsResultsAsync(
        ChannelReader<TtsChannelItem> resultReader,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken)
    {
        var segmentAudioPaths = new ConcurrentDictionary<string, string>();
        var completed = 0;

        await foreach (var item in resultReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            completed++;
            if (item.Result.Success && !string.IsNullOrWhiteSpace(item.Result.AudioPath) && File.Exists(item.Result.AudioPath))
                segmentAudioPaths[item.SegmentId] = item.Result.AudioPath;

            ReportStage(
                stageContext,
                $"Generated segment clip {completed}…",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Translation is still feeding new segments downstream.");
        }

        return segmentAudioPaths;
    }

    private async Task GenerateStreamingTtsSegmentAsync(
        TranslationChannelItem item,
        string defaultVoice,
        string? ttsLanguage,
        string segmentsDir,
        ChannelWriter<TtsChannelItem> resultWriter,
        CancellationToken cancellationToken)
    {
        var id = item.SegmentId;
        var text = item.Segment.TranslatedText;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(text))
            return;

        var segmentAudioPath = Path.Combine(segmentsDir, $"{id}.mp3");
        var resolvedVoice = ResolveVoiceForSegment(item.Segment, defaultVoice);
        var referenceAudioPath = ResolveReferenceAudioForSegment(item.Segment);

        try
        {
            var task = _ttsService!.GenerateSegmentTtsAsync(
                new SingleSegmentTtsRequest(
                    text,
                    segmentAudioPath,
                    resolvedVoice,
                    item.Segment.SpeakerId,
                    referenceAudioPath,
                    Language: ttsLanguage,
                    SourceVideoPath: CurrentSession.IngestedMediaPath ?? CurrentSession.SourceMediaPath),
                cancellationToken);
            _pendingTtsTasks.Add(task);
            var result = await task.ConfigureAwait(false);
            if (result.Success && File.Exists(segmentAudioPath))
            {
                await resultWriter.WriteAsync(
                    new TtsChannelItem(
                        id,
                        CloneTranslationSegment(item.Segment),
                        result with { AudioPath = segmentAudioPath }),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _log.Warning($"Streaming TTS failed or file missing for segment {id}.");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Streaming TTS generation failed for {id}: {ex.Message}", ex);
        }
    }

    private string BuildTranscriptArtifactPath()
    {
        var sessionDir = GetSessionDirectory();
        var transcriptDir = Path.Combine(sessionDir, "transcripts");
        Directory.CreateDirectory(transcriptDir);
        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.IngestedMediaPath);
        return Path.Combine(transcriptDir, $"{fileName}.json");
    }

    private static string BuildTranslationArtifactPath(string transcriptPath, string targetLanguage)
    {
        var translationDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(transcriptPath)!)!, "translations");
        Directory.CreateDirectory(translationDir);
        var fileName = Path.GetFileNameWithoutExtension(transcriptPath);
        return Path.Combine(translationDir, $"{fileName}_{targetLanguage}.json");
    }

    private static (string TtsPath, string SegmentsDir) BuildTtsArtifacts(string translationPath, string voice)
    {
        var sessionDir = Path.GetDirectoryName(Path.GetDirectoryName(translationPath)!)!;
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);
        var fileName = Path.GetFileNameWithoutExtension(translationPath);
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{voice}.mp3");
        var segmentsDir = Path.Combine(ttsDir, "segments", Path.GetFileNameWithoutExtension(translationPath));
        Directory.CreateDirectory(segmentsDir);
        return (ttsPath, segmentsDir);
    }

    private static TranslationResult BuildTranslationResult(
        IReadOnlyList<TranslationSegmentArtifact> segments,
        string sourceLanguage,
        string targetLanguage) =>
        new(
            true,
            segments.Select(segment => new TranslatedSegment(
                segment.Start,
                segment.End,
                segment.Text ?? string.Empty,
                segment.TranslatedText ?? string.Empty,
                segment.SpeakerId)).ToList(),
            sourceLanguage,
            targetLanguage,
            null);

    private static TranscriptSegmentArtifact CloneTranscriptSegment(TranscriptSegmentArtifact segment) =>
        new()
        {
            Start = segment.Start,
            End = segment.End,
            Text = segment.Text,
            SpeakerId = segment.SpeakerId,
            OriginalStart = segment.OriginalStart,
            Words = segment.Words is null ? null : [.. segment.Words],
        };

    private static TranslationSegmentArtifact CloneTranslationSegment(TranslationSegmentArtifact segment) =>
        new()
        {
            Id = segment.Id,
            Start = segment.Start,
            End = segment.End,
            Text = segment.Text,
            TranslatedText = segment.TranslatedText,
            SpeakerId = segment.SpeakerId,
        };

    public Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        AdvancePipelineAsync(progress, stageProgress: null, cancellationToken);

    /// <summary>
    /// Advances the pipeline from the current session stage through transcription, optional diarization, translation, and TTS.
    /// </summary>
    /// <param name="progress">Optional combined progress reporter for the overall advance operation.</param>
    /// <param name="stageProgress">Optional per-stage reporter that receives stage title/detail and per-stage progress updates.</param>
    /// <param name="cancellationToken">Cancellation token observed throughout stage execution.</param>
    /// <remarks>
    /// Entry starts at <see cref="CurrentSession"/>.Stage. Automatic runs continue through translation and TTS
    /// after diarization instead of stopping for a manual speaker-mapping confirmation. Depending on cancellation or
    /// prior stage state, possible return stages include <see cref="SessionWorkflowStage.Transcribed"/>,
    /// <see cref="SessionWorkflowStage.Diarized"/>, <see cref="SessionWorkflowStage.Translated"/>, and
    /// <see cref="SessionWorkflowStage.TtsGenerated"/>. State changes are persisted by the invoked stage methods
    /// (for example via <see cref="SaveCurrentSession"/>). Cancellation is respected and propagated via
    /// <paramref name="cancellationToken"/>.
    /// </remarks>
    internal async Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        var shouldRunDiarization = ShouldRunDiarization();
        var remainingStages = GetAdvancePipelineStages(CurrentSession.Stage, shouldRunDiarization);

        if (CurrentSession.Stage < SessionWorkflowStage.Transcribed && !shouldRunDiarization)
        {
            await ExecuteStreamingPipelineAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
            return;
        }

        if (CurrentSession.Stage < SessionWorkflowStage.Transcribed)
        {
            await TranscribeMediaAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                cancellationToken);
        }

        if (shouldRunDiarization && CurrentSession.Stage < SessionWorkflowStage.Diarized)
        {
            await ExecuteDiarizationStageAsync(
                GetStageContext(remainingStages, SessionWorkflowStage.Diarized, stageProgress),
                cancellationToken);
        }

        if (CurrentSession.Stage < SessionWorkflowStage.Translated)
        {
            await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
            return;
        }

        if (CurrentSession.Stage < SessionWorkflowStage.TtsGenerated)
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

        if (CurrentSession.Stage < SessionWorkflowStage.Translated)
        {
            await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                progress,
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                cancellationToken);
            return;
        }

        if (CurrentSession.Stage < SessionWorkflowStage.TtsGenerated)
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
            statusMessage: "Speaker mapping complete. Continuing translation and dubbing.");

        ReportStage(
            stageContext,
            $"Speaker mapping complete. Identified {outcome.SpeakerCount} speakers across {outcome.SegmentCount} segments.",
            progress01: 1,
            isIndeterminate: false);
    }

    private bool ShouldRunDiarization() =>
        CurrentSession.MultiSpeakerEnabled
        && !string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider);
}
