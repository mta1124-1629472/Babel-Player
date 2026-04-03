using System;
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
        TranscribeMediaAsync(progress, cancellationToken, stageContext: null);

    internal async Task TranscribeMediaAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        PipelineStageContext? stageContext)
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
            StatusMessage = $"Transcribed {result.Segments.Count} segments ({result.Language}). Ready for translation.",
        };

        _log.Info($"Transcription complete: {result.Segments.Count} segments, language: {result.Language}");
        SaveCurrentSession();

        if (CurrentSession.MultiSpeakerEnabled && !string.IsNullOrEmpty(CurrentSettings.DiarizationProvider))
        {
            ReportStage(
                stageContext,
                "Transcript complete. Running diarization to assign speaker turns before translation and dubbing…",
                progress01: 0,
                isIndeterminate: true);
            await RunDiarizationAsync(CurrentSession.IngestedMediaPath!, transcriptPath, cancellationToken);
        }

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
        TranslateTranscriptAsync(progress, targetLanguage, sourceLanguage, cancellationToken, stageContext: null);

    internal async Task TranslateTranscriptAsync(
        IProgress<double>? progress,
        string? targetLanguage,
        string? sourceLanguage,
        CancellationToken cancellationToken,
        PipelineStageContext? stageContext)
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
        GenerateTtsAsync(progress, voice, cancellationToken, stageContext: null);

    internal async Task GenerateTtsAsync(
        IProgress<double>? progress,
        string? voice,
        CancellationToken cancellationToken,
        PipelineStageContext? stageContext)
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
        await EnsureSingleSpeakerXttsReferenceClipAsync(cancellationToken);

        ReportStage(
            stageContext,
            $"Generating combined dub audio with {CurrentSettings.TtsProvider} / {v}. After the full output is ready, per-segment clips will be generated for in-context playback.",
            progress01: 0,
            isIndeterminate: true);

        var sessionDir = GetSessionDirectory();
        var ttsDir = Path.Combine(sessionDir, "tts");
        Directory.CreateDirectory(ttsDir);

        var fileName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath);
        var ttsPath = Path.Combine(ttsDir, $"{fileName}_{v}.mp3");

        _log.Info($"Starting TTS generation: {CurrentSession.TranslationPath} -> {ttsPath}");

        var ttsLanguage = CurrentSession.TargetLanguage ?? CurrentSettings.TargetLanguage;
        var result = await _ttsService.GenerateTtsAsync(
            new TtsRequest(
                CurrentSession.TranslationPath,
                ttsPath,
                v,
                CurrentSession.SpeakerVoiceAssignments,
                CurrentSession.SpeakerReferenceAudioPaths,
                CurrentSession.DefaultTtsVoiceFallback,
                ttsLanguage),
            cancellationToken);

        if (!result.Success)
        {
            var errorMsg = result.ErrorMessage ?? "Unknown TTS error";
            _log.Error($"TTS failed: {errorMsg}", new Exception(errorMsg));
            throw new InvalidOperationException($"TTS failed: {errorMsg}");
        }

        _log.Info($"TTS complete: {ttsPath}, size: {result.FileSizeBytes} bytes");

        ReportStage(
            stageContext,
            "Combined dub audio is ready. Generating per-segment dubbed clips for preview, seek, and segment-level refinement…",
            progress01: 0,
            isIndeterminate: true);

        var mediaName = Path.GetFileNameWithoutExtension(CurrentSession.TranslationPath!);
        var segmentsDir = Path.Combine(ttsDir, "segments", mediaName);
        Directory.CreateDirectory(segmentsDir);

        var segmentAudioPaths = new Dictionary<string, string>();
        int totalSegments = 0;
        try
        {
            var translationData = await _artifactReader.LoadTranslationAsync(CurrentSession.TranslationPath, cancellationToken);
            var candidateSegments = translationData.Segments?
                .Where(seg => !string.IsNullOrWhiteSpace(seg.Id) && !string.IsNullOrWhiteSpace(seg.TranslatedText))
                .ToList()
                ?? [];
            var segmentOrdinal = 0;

            foreach (var seg in candidateSegments)
            {
                var id = seg.Id;
                var text = seg.TranslatedText;

                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(id))
                {
                    _log.Info($"Skipping segment {id}: empty text or ID");
                    continue;
                }

                totalSegments++;
                segmentOrdinal++;
                var segmentAudioPath = Path.Combine(segmentsDir, $"{id}.mp3");
                var resolvedVoice = ResolveVoiceForSegment(seg, v);
                var referenceAudioPath = ResolveReferenceAudioForSegment(seg);

                ReportStage(
                    stageContext,
                    $"Generating segment clip {segmentOrdinal} of {candidateSegments.Count} for {id} using voice {resolvedVoice}{(string.IsNullOrWhiteSpace(seg.SpeakerId) ? string.Empty : $" (speaker {seg.SpeakerId})")}…",
                    progress01: 0,
                    isIndeterminate: true);

                _log.Info($"Generating TTS for segment {id} (voice={resolvedVoice}, speaker={seg.SpeakerId ?? "<none>"}): {text.Substring(0, Math.Min(30, text.Length))}...");

                try
                {
                    var segResult = await _ttsService.GenerateSegmentTtsAsync(
                        new SingleSegmentTtsRequest(
                            text,
                            segmentAudioPath,
                            resolvedVoice,
                            seg.SpeakerId,
                            referenceAudioPath,
                            Language: ttsLanguage),
                        cancellationToken);

                    if (segResult.Success && File.Exists(segmentAudioPath))
                    {
                        segmentAudioPaths[id] = segmentAudioPath;
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
        }
        catch (Exception ex)
        {
            _log.Error($"Error generating per-segment TTS: {ex.Message}", ex);
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
            TtsSegmentAudioPaths = segmentAudioPaths,
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

    internal async Task AdvancePipelineAsync(
        IProgress<double>? progress = null,
        IProgress<PipelineStageUpdate>? stageProgress = null,
        CancellationToken cancellationToken = default)
    {
        var remainingStages = GetRemainingPipelineStages(CurrentSession.Stage);
        var stage = CurrentSession.Stage;

        if (stage < SessionWorkflowStage.Transcribed)
        {
            await TranscribeMediaAsync(
                progress,
                cancellationToken,
                GetStageContext(remainingStages, SessionWorkflowStage.Transcribed, stageProgress));
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.Translated)
        {
            await TranslateTranscriptAsync(
                progress,
                null,
                null,
                cancellationToken,
                GetStageContext(remainingStages, SessionWorkflowStage.Translated, stageProgress));
        }

        stage = CurrentSession.Stage;
        if (stage < SessionWorkflowStage.TtsGenerated)
        {
            await GenerateTtsAsync(
                progress,
                null,
                cancellationToken,
                GetStageContext(remainingStages, SessionWorkflowStage.TtsGenerated, stageProgress));
        }
    }
}
