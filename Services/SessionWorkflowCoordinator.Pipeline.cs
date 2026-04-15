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
                SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(CurrentSettings.TranscriptionLanguageHint),
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

        var rawLang = targetLanguage ?? CurrentSettings.TargetLanguage;
        var lang = NormalizePipelineLanguage(rawLang, CurrentSettings.TargetLanguage);
        var rawSrc = sourceLanguage ?? CurrentSession.SourceLanguage ?? "auto";
        var src = string.Equals(rawSrc, "auto", StringComparison.OrdinalIgnoreCase)
            ? "auto"
            : NormalizePipelineLanguage(rawSrc, rawSrc);

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
        var ttsLanguage = NormalizePipelineLanguage(
            CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage,
            CurrentSettings.TargetLanguage);
        var segmentsDir = Path.Combine(ttsDir, "segments", Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!));
        Directory.CreateDirectory(segmentsDir);

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var (segmentAudioPaths, segmentDurations, totalSegments, orderedSegments) = await GenerateSegmentClipsAsync(
            v, ttsLanguage, segmentsDir, stageContext, cancellationToken);

        await StitchSegmentClipsAsync(segmentAudioPaths, orderedSegments, ttsPath, stageContext, cancellationToken);

        CommitTtsSessionState(v, ttsPath, segmentsDir, segmentAudioPaths, segmentDurations, totalSegments, stageContext);
    }

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
    /// Runs the end-to-end streaming pipeline: starts streaming transcription (if a streaming ASR provider is available),
    /// forwards segments to the streaming translation and TTS stages as they arrive, and persists transcription, translation,
    /// and TTS session state when each stage completes. If no streaming ASR provider is available, falls back to the non‑streaming
    /// transcription path and then runs translation and TTS from the completed transcript.
    /// </summary>
    /// <remarks>
    /// Entry requirements:
    /// - A media file must already be ingested (CurrentSession.IngestedMediaPath must be set and exist).
    /// - Transcription provider readiness is verified via EnsureTranscriptionProviderReadyAsync; that guard may throw
    ///   a <see cref="PipelineProviderException"/> when blocked or an <see cref="InvalidOperationException"/> on failed model download.
    /// On success:
    /// - Commits and persists session state for the transcription, translation, and TTS stages as each completes.
    /// Cancellation:
    /// - Honors <paramref name="cancellationToken"/> and will observe cancellation for async operations; cancellation will surface as
    ///   <see cref="OperationCanceledException"/> from awaited calls.
    /// </remarks>
    /// <param name="progress">Optional progress reporter used for stage-level progress updates and model download reporting.</param>
    /// <param name="transcriptionStageContext">Context used to report transcription stage messages and progress.</param>
    /// <param name="translationStageContext">Context used to report translation stage messages and progress.</param>
    /// <param name="ttsStageContext">Context used to report TTS stage messages and progress.</param>
    /// <param name="cancellationToken">Token to observe for cancellation of the pipeline execution.</param>
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
        var targetLanguage = NormalizePipelineLanguage(CurrentSettings.TargetLanguage, CurrentSettings.TargetLanguage);
        var translationPath = BuildTranslationArtifactPath(transcriptPath, targetLanguage);
        var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
        var voice = CurrentSettings.TtsVoice;
        var ttsLanguage = NormalizePipelineLanguage(
            CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage,
            CurrentSettings.TargetLanguage);
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
                    SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(CurrentSettings.TranscriptionLanguageHint),
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
        CommitTtsSessionState(voice, ttsPath, segmentsDir, segmentAudioPaths, null, translationWriter.OrderedSegments.Count, ttsStageContext);
    }

    /// <summary>
    /// Translates an existing transcript into the target language and generates streaming TTS dubs for the translated segments, producing final translation and TTS artifacts persisted to the current session.
    /// </summary>
    /// <param name="progress">Optional progress reporter used for stage download and progress updates.</param>
    /// <param name="translationStageContext">Context used to report translation stage progress and status.</param>
    /// <param name="ttsStageContext">Context used to report TTS stage progress and status.</param>
    /// <param name="cancellationToken">Cancellation token that aborts the streaming translation and TTS pipeline.</param>
    /// <remarks>
    /// Preconditions: <see cref="CurrentSession.TranscriptPath"/> must be set and point to an existing transcript; otherwise an <see cref="InvalidOperationException"/> is thrown. 
    /// On success: commits and persists translation and TTS session state (translation artifact and final dub), and stitches per-segment audio into the final TTS file. 
    /// The method ensures translation and TTS providers/runtimes are ready (including any required model downloads) before processing; readiness failures will surface as provider-specific exceptions. 
    /// Cancellation: respects <paramref name="cancellationToken"/> for all async operations and will stop producing further segments when cancelled.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when no transcript is available at <see cref="CurrentSession.TranscriptPath"/>.</exception>
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

        var targetLanguage = NormalizePipelineLanguage(CurrentSettings.TargetLanguage, CurrentSettings.TargetLanguage);
        var translationPath = BuildTranslationArtifactPath(CurrentSession.TranscriptPath, targetLanguage);
        var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
        var voice = CurrentSettings.TtsVoice;
        var ttsLanguage = NormalizePipelineLanguage(
            CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage,
            CurrentSettings.TargetLanguage);
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
        CommitTtsSessionState(voice, ttsPath, segmentsDir, segmentAudioPaths, null, translationWriter.OrderedSegments.Count, ttsStageContext);
    }

    /// <summary>
    /// Consumes transcript segments from the provided reader, translates each segment into the specified target language, appends translated segments to the streaming translation artifact, and publishes translated segments to the translation writer for downstream TTS consumption.
    /// </summary>
    /// <param name="transcriptReader">Channel reader that yields incoming transcript segments to translate.</param>
    /// <param name="translationWriter">Channel writer that receives translated segments for downstream stages.</param>
    /// <param name="artifactWriter">Streaming artifact writer used to append pending segments and reload the partial translation artifact on disk.</param>
    /// <param name="targetLanguage">Normalized target language code to translate segments into.</param>
    /// <param name="stageContext">Optional pipeline stage context used to report progress messages.</param>
    /// <param name="cancellationToken">Token to observe for cooperative cancellation; when canceled the method will stop reading and propagate cancellation.</param>
    /// <returns>A <see cref="TranslationResult"/> built from the completed partial translation artifact (ordered segments and detected source language).</returns>
    /// <remarks>
    /// Entry state: expects a populated transcription stream available from <paramref name="transcriptReader"/>.  
    /// Exit state on success: writes translated segments into the partial translation artifact on disk and emits corresponding items to <paramref name="translationWriter"/>; does not persist overall session state.  
    /// Cancellation: honors <paramref name="cancellationToken"/> and will cease processing when cancellation is requested.  
    /// Guard conditions: requires an initialized translation service; translation failures or missing translated segments will abort the stage.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a translation call fails or when a translated segment is not present in the partial artifact after translation.</exception>
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
            await TranslateTranscriptAsync(
                progress,
                targetLanguage: null,
                sourceLanguage: null,
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress),
                cancellationToken);
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
