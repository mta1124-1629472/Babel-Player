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
    internal Task TranscribeMediaAsync(
        IProgress<double>? progress,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _transcriptionOrchestrator.ExecuteAsync(progress, stageContext, cancellationToken);

    public Task TranslateTranscriptAsync(
        IProgress<double>? progress = null,
        string? targetLanguage = null,
        string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        TranslateTranscriptAsync(progress, targetLanguage, sourceLanguage, stageContext: null, cancellationToken);

    internal Task TranslateTranscriptAsync(
        IProgress<double>? progress,
        string? targetLanguage,
        string? sourceLanguage,
        PipelineStageContext? stageContext,
        CancellationToken cancellationToken) =>
        _translationOrchestrator.ExecuteAsync(progress, targetLanguage, sourceLanguage, stageContext, cancellationToken);

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
    /// <summary>
    /// Generates speech audio (a combined dub) from the current session's translation and persists TTS artifacts to the session state.
    /// </summary>
    /// <param name="progress">Optional progress reporter for overall TTS pipeline progress.</param>
    /// <param name="voice">Optional voice identifier to use; when null the coordinator's configured TTS voice is used.</param>
    /// <param name="stageContext">Optional context used for stage reporting; used to annotate and report stage progress and completion.</param>
    /// <param name="cancellationToken">Cancellation token that aborts the operation; cooperative cancellation is honored by awaited operations.</param>
    /// <remarks>
    /// Preconditions: requires <see cref="CurrentSession.TranslationPath"/> to be non-empty and point to an existing translation artifact.
    /// On success: creates per-segment audio under the session's tts/segments directory, produces a combined dub MP3 under tts/, and updates and persists the session state to the TtsGenerated stage.
    /// Guarding behavior: verifies TTS provider/runtime readiness and downloads any required models before generation; if readiness cannot be achieved the method throws <see cref="PipelineProviderException"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no translation path is available on the current session.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the translation artifact file cannot be found on disk.</exception>
    /// <exception cref="PipelineProviderException">Thrown when the configured TTS provider/runtime is not ready and cannot proceed.</exception>
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
        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.Transcribed,
            TranscriptPath = transcriptPath,
            SourceLanguage = result.Language,
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
    /// </returns>
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
                foreach (var seg in candidateSegments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await GenerateSingleSegmentAsync(
                        seg,
                        voice,
                        ttsLanguage,
                        segmentsDir,
                        segmentAudioPaths,
                        segmentDurations,
                        totalSegments,
                        stageContext,
                        cancellationToken,
                        onSucceeded: () => Interlocked.Increment(ref completed));
                }
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
    /// Generates TTS audio for a single translation segment and records the output path on success.
    /// Called concurrently inside Parallel.ForEachAsync for non-Qwen providers.
    /// </summary>
    /// <summary>
    /// Generates TTS audio for a single translation segment and records the output path on success.
    /// Called concurrently inside Parallel.ForEachAsync for non-Qwen providers.
    /// <paramref name="onSucceeded"/> is invoked (thread-safely by the caller) to increment the shared
    /// progress counter; returning the new count lets this method report accurate progress without
    /// holding a ref parameter across an async boundary.
    /// <summary>
    /// Synchronously schedules and awaits generation of a single segment TTS clip, recording its output path and duration when successful.
    /// </summary>
    /// <param name="seg">The translated segment artifact containing Id, TranslatedText, and speaker metadata.</param>
    /// <param name="defaultVoice">Fallback voice to use if the segment does not specify one.</param>
    /// <param name="ttsLanguage">Optional TTS language hint to pass to the provider.</param>
    /// <param name="segmentsDir">Directory where per-segment audio files are written.</param>
    /// <param name="segmentAudioPaths">Concurrent map to record produced segment audio file paths keyed by segment Id.</param>
    /// <param name="segmentDurations">Concurrent map to record produced segment durations (seconds) keyed by segment Id.</param>
    /// <param name="totalSegments">Total number of segments being generated (used for progress reporting).</param>
    /// <param name="stageContext">Optional pipeline stage context used for progress reporting.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation of the generation operation.</param>
    /// <param name="onSucceeded">Callback invoked when a segment is recorded as succeeded; returns the new count of completed segments.</param>
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
            _pendingTtsTasks.Add(segTask);
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

        CurrentSession = CurrentSession with
        {
            Stage = SessionWorkflowStage.TtsGenerated,
            TtsPath = ttsPath,
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

        SaveCurrentSession();

        ReportStage(
            stageContext,
            $"Dub complete. {succeeded}/{totalSegments} segment clips are ready with voice {voice}.",
            progress01: 1,
            isIndeterminate: false);
    }

    /// <summary>
    /// Generates TTS audio for multiple translated segments in a single batch using the Qwen container TTS provider and records produced file paths and durations.
    /// </summary>
    /// <remarks>
    /// Entry state: expects a prepared translation artifact (segments with Id/TranslatedText) and that TTS provider readiness and reference clips (if required) have already been validated by the caller.
    /// Exit state: populates <paramref name="segmentAudioPaths"/> with produced per-segment output paths and, when available, records per-segment durations into <paramref name="segmentDurations"/>; does not itself persist session state.
    /// Cancellation: honors <paramref name="cancellationToken"/> and will propagate OperationCanceledException when cancelled.
    /// Guard conditions: silently returns when there are no valid candidate segments (no Id or translated text). The method logs and continues when individual segment outputs are missing; it does not throw for missing outputs.
    /// </remarks>
    /// <param name="qwenProvider">The Qwen container TTS provider used to perform the batch generation.</param>
    /// <param name="candidateSegments">The list of translation segments to synthesize; only segments with non-empty Id and TranslatedText are processed.</param>
    /// <param name="segmentsDir">Directory where per-segment MP3 files will be written.</param>
    /// <param name="defaultVoice">Default voice to use when a segment does not specify one.</param>
    /// <param name="ttsLanguage">TTS language hint to pass to the provider, or null to omit.</param>
    /// <param name="stageContext">Optional pipeline stage context used for progress reporting.</param>
    /// <param name="segmentAudioPaths">Thread-safe map that will be filled with segmentId -> generated audio file path for successful outputs.</param>
    /// <param name="segmentDurations">Thread-safe map that will be filled with segmentId -> duration in seconds when the provider returns duration metadata.</param>
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
    /// Delegates to <see cref="StreamingPipelineOrchestrator.ExecuteFullPipelineAsync"/>.
    /// </summary>
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
    /// Delegates to <see cref="StreamingPipelineOrchestrator.ExecuteTranslationAndTtsFromTranscriptAsync"/>.
    /// </summary>
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
            var action = PipelineStateMachine.GetNextAdvanceAction(CurrentSession.Stage, shouldRunDiarization);
            switch (action)
            {
                case null:
                    return;
                case PipelineAdvanceAction.Transcribe:
                    await TranscribeMediaAsync(
                        progress,
                        GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress),
                        cancellationToken);
                    break;
                case PipelineAdvanceAction.Diarize:
                    await _diarizationStageOrchestrator.ExecuteAsync(
                        GetStageContext(remainingStages, SessionWorkflowStage.Diarized, stageProgress),
                        cancellationToken);
                    break;
                case PipelineAdvanceAction.TranslateAndDubFromTranscript:
                    await ExecuteStreamingTranslationAndTtsFromTranscriptAsync(
                        progress,
                        GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                        GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                        cancellationToken);
                    return;
                case PipelineAdvanceAction.GenerateTts:
                    await GenerateTtsAsync(
                        progress,
                        null,
                        GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                        cancellationToken);
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
                return;
            case PipelineAdvanceAction.GenerateTts:
                await GenerateTtsAsync(
                    progress,
                    null,
                    GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress),
                    cancellationToken);
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

    private bool ShouldRunDiarization() =>
        !string.IsNullOrWhiteSpace(CurrentSettings.DiarizationProvider);

    private static string NormalizePipelineLanguage(string? raw, string nonNormalizedFallback)
    {
        var n = LanguageCode.NormalizeForPersistence(raw);
        if (n is not null) return n;
        if (string.IsNullOrWhiteSpace(raw)) return nonNormalizedFallback;
        return raw.Trim();
    }

    /// <summary>
    /// Re-run transcription, optionally continuing through diarization (if enabled), translation, and TTS.
    /// </summary>
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
    /// Re-run diarization; optionally continue with translation and TTS afterward.
    /// </summary>
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
    /// </summary>
    internal async Task RerunDubAsync(
        IProgress<PipelineStageUpdate>? stageProgress,
        CancellationToken cancellationToken)
    {
        ResetPipelineToTranslated();
        SaveCurrentSession();
        await RunTtsOnlyAsync(null, null, stageProgress, cancellationToken).ConfigureAwait(false);
    }
}
