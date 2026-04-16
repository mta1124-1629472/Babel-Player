using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    /// <summary>
    /// Channel-based streaming transcription → translation → TTS pipeline stages.
    /// </summary>
    internal sealed class StreamingPipelineOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        internal StreamingPipelineOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        internal async Task ExecuteFullPipelineAsync(
            IProgress<double>? progress,
            PipelineStageContext? transcriptionStageContext,
            PipelineStageContext? translationStageContext,
            PipelineStageContext? ttsStageContext,
            CancellationToken cancellationToken)
        {
            await _c.EnsureTranscriptionProviderReadyAsync(progress, transcriptionStageContext, cancellationToken);

            if (_c._transcriptionService is not IStreamingTranscriptionProvider streamingProvider)
            {
                await _c.TranscribeMediaAsync(progress, transcriptionStageContext, cancellationToken);
                await ExecuteTranslationAndTtsFromTranscriptAsync(
                    progress,
                    translationStageContext,
                    ttsStageContext,
                    cancellationToken);
                return;
            }

            ReportStage(
                transcriptionStageContext,
                $"Starting transcription with {_c.CurrentSettings.TranscriptionProvider} / {_c.CurrentSettings.TranscriptionModel}. Translation will begin as segments arrive.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Downstream translation and dubbing will overlap with ASR output.");

            var transcriptPath = BuildTranscriptArtifactPath();
            var transcriptPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(transcriptPath);
            var targetLanguage = NormalizePipelineLanguage(_c.CurrentSettings.TargetLanguage, _c.CurrentSettings.TargetLanguage);
            var translationPath = BuildTranslationArtifactPath(transcriptPath, targetLanguage);
            var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
            var voice = _c.CurrentSettings.TtsVoice;
            var ttsLanguage = NormalizePipelineLanguage(
                _c.CurrentSession.TargetLanguage ?? _c.CurrentSettings.TargetLanguage,
                _c.CurrentSettings.TargetLanguage);
            var (ttsPath, segmentsDir) = BuildTtsArtifacts(translationPath, voice);

            var transcriptArtifactWriter = new TranscriptArtifactStreamingWriter(
                transcriptPartialPath,
                _c.CurrentSession.SourceLanguage ?? "unknown",
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
            var ttsResultChannel = Channel.CreateBounded<TtsChannelItem>(new BoundedChannelOptions(Math.Max(4, _c._ttsService?.MaxConcurrency ?? 4))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

            var translationWriter = new TranslationArtifactStreamingWriter(
                translationPartialPath,
                _c.CurrentSession.SourceLanguage ?? "unknown",
                targetLanguage);
            await translationWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var translationDownloadProgress = CreateStageDownloadProgress(
                translationStageContext,
                progress,
                $"Preparing translation model '{_c.CurrentSettings.TranslationModel}'");
            await _c.EnsureTranslationExecutionReadyAsync(translationDownloadProgress, cancellationToken).ConfigureAwait(false);
            _c._translationService ??= _c.CreateTranslationService();
            await _c.EnsureTtsProviderReadyAsync(voice, progress, ttsStageContext, cancellationToken).ConfigureAwait(false);
            _c._ttsService ??= _c.CreateTtsService();
            await _c.EnsureSingleSpeakerQwenReferenceClipAsync(cancellationToken).ConfigureAwait(false);
            await _c.EnsureMultiSpeakerReferenceClipsAsync(cancellationToken).ConfigureAwait(false);

            ReportStage(
                translationStageContext,
                $"Streaming translation to {targetLanguage} with {_c.CurrentSettings.TranslationProvider} / {_c.CurrentSettings.TranslationModel}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Dub generation will start as translated segments arrive.");
            ReportStage(
                ttsStageContext,
                $"Streaming TTS synthesis with {_c.CurrentSettings.TtsProvider} / {voice}.",
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
                transcriptionResult = await _c._inferenceEngine.TranscribeStreamingAsync(
                    streamingProvider,
                    new TranscriptionRequest(
                        _c.CurrentSession.IngestedMediaPath!,
                        transcriptPath,
                        _c.CurrentSettings.TranscriptionModel,
                        SessionSnapshotSemantics.NormalizeTranscriptionLanguageHint(_c.CurrentSettings.TranscriptionLanguageHint),
                        _c.CurrentSettings.TranscriptionCpuComputeType,
                        _c.CurrentSettings.TranscriptionCpuThreads,
                        _c.CurrentSettings.TranscriptionNumWorkers),
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
            _c.CommitTranscriptionSessionState(transcriptionResult, transcriptPath);
            ReportStage(
                transcriptionStageContext,
                $"Transcription complete. {transcriptionResult.Segments.Count} segments were detected in {transcriptionResult.Language}.",
                progress01: 1,
                isIndeterminate: false);

            var translationResult = await translationTask.ConfigureAwait(false);
            await translationWriter.CompleteAsync(translationPath, cancellationToken).ConfigureAwait(false);
            _c.CommitTranslationSessionState(translationResult, translationPath, translationResult.SourceLanguage, translationResult.TargetLanguage);
            ReportStage(
                translationStageContext,
                $"Translation complete. {translationResult.Segments.Count} segments were translated from {translationResult.SourceLanguage} to {translationResult.TargetLanguage}.",
                progress01: 1,
                isIndeterminate: false);

            await ttsStageTask.ConfigureAwait(false);
            var segmentAudioPaths = await ttsCollectorTask.ConfigureAwait(false);
            await _c.StitchSegmentClipsAsync(segmentAudioPaths, translationWriter.OrderedSegments, ttsPath, ttsStageContext, cancellationToken).ConfigureAwait(false);
            _c.CommitTtsSessionState(voice, ttsPath, segmentsDir, segmentAudioPaths, null, translationWriter.OrderedSegments.Count, ttsStageContext);
        }

        internal async Task ExecuteTranslationAndTtsFromTranscriptAsync(
            IProgress<double>? progress,
            PipelineStageContext? translationStageContext,
            PipelineStageContext? ttsStageContext,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_c.CurrentSession.TranscriptPath))
                throw new InvalidOperationException("No transcript available. Please transcribe media first.");

            var transcript = await _c._artifactReader.LoadTranscriptAsync(_c.CurrentSession.TranscriptPath, cancellationToken).ConfigureAwait(false);
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
                                _c.CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
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

            var targetLanguage = NormalizePipelineLanguage(_c.CurrentSettings.TargetLanguage, _c.CurrentSettings.TargetLanguage);
            var translationPath = BuildTranslationArtifactPath(_c.CurrentSession.TranscriptPath!, targetLanguage);
            var translationPartialPath = TranscriptArtifactStreamingWriter.GetPartialPath(translationPath);
            var voice = _c.CurrentSettings.TtsVoice;
            var ttsLanguage = NormalizePipelineLanguage(
                _c.CurrentSession.TargetLanguage ?? _c.CurrentSettings.TargetLanguage,
                _c.CurrentSettings.TargetLanguage);
            var (ttsPath, segmentsDir) = BuildTtsArtifacts(translationPath, voice);

            var translationDownloadProgress = CreateStageDownloadProgress(
                translationStageContext,
                progress,
                $"Preparing translation model '{_c.CurrentSettings.TranslationModel}'");
            await _c.EnsureTranslationExecutionReadyAsync(translationDownloadProgress, cancellationToken).ConfigureAwait(false);
            _c._translationService ??= _c.CreateTranslationService();
            await _c.EnsureTtsProviderReadyAsync(voice, progress, ttsStageContext, cancellationToken).ConfigureAwait(false);
            _c._ttsService ??= _c.CreateTtsService();
            await _c.EnsureSingleSpeakerQwenReferenceClipAsync(cancellationToken).ConfigureAwait(false);
            await _c.EnsureMultiSpeakerReferenceClipsAsync(cancellationToken).ConfigureAwait(false);

            var translationWriter = new TranslationArtifactStreamingWriter(
                translationPartialPath,
                _c.CurrentSession.SourceLanguage ?? transcript.Language ?? "unknown",
                targetLanguage);
            await translationWriter.InitializeAsync(cancellationToken).ConfigureAwait(false);

            ReportStage(
                translationStageContext,
                $"Streaming translation to {targetLanguage} with {_c.CurrentSettings.TranslationProvider} / {_c.CurrentSettings.TranslationModel}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Dub generation will start as translated segments arrive.");
            ReportStage(
                ttsStageContext,
                $"Streaming TTS synthesis with {_c.CurrentSettings.TtsProvider} / {voice}.",
                progress01: 0,
                isIndeterminate: true,
                streamingStatus: "Segment clips are generated as translation continues.");

            var translationChannel = Channel.CreateBounded<TranslationChannelItem>(new BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            var ttsResultChannel = Channel.CreateBounded<TtsChannelItem>(new BoundedChannelOptions(Math.Max(4, _c._ttsService?.MaxConcurrency ?? 4))
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
            _c.CommitTranslationSessionState(translationResult, translationPath, translationResult.SourceLanguage, translationResult.TargetLanguage);
            ReportStage(
                translationStageContext,
                $"Translation complete. {translationResult.Segments.Count} segments were translated from {translationResult.SourceLanguage} to {translationResult.TargetLanguage}.",
                progress01: 1,
                isIndeterminate: false);

            await ttsStageTask.ConfigureAwait(false);
            var segmentAudioPaths = await ttsCollectorTask.ConfigureAwait(false);
            await _c.StitchSegmentClipsAsync(segmentAudioPaths, translationWriter.OrderedSegments, ttsPath, ttsStageContext, cancellationToken).ConfigureAwait(false);
            _c.CommitTtsSessionState(voice, ttsPath, segmentsDir, segmentAudioPaths, null, translationWriter.OrderedSegments.Count, ttsStageContext);
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

                    var result = await _c._inferenceEngine.TranslateSingleSegmentAsync(
                        _c._translationService!,
                        new SingleSegmentTranslationRequest(
                            item.Segment.Text ?? string.Empty,
                            item.SegmentId,
                            artifactWriter.PartialPath,
                            artifactWriter.PartialPath,
                            item.SourceLanguage,
                            targetLanguage,
                            _c.CurrentSettings.TranslationModel),
                        cancellationToken).ConfigureAwait(false);

                    if (!result.Success)
                    {
                        var errorMsg = result.ErrorMessage ?? "Unknown translation error";
                        _c.Log.Error($"Streaming translation failed: {errorMsg}", new Exception(errorMsg));
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
                var source = sourceLanguage ?? _c.CurrentSession.SourceLanguage ?? "unknown";
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
            var parallelism = Math.Max(1, _c._ttsService?.MaxConcurrency ?? 1);
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
            var resolvedVoice = _c.ResolveVoiceForSegment(item.Segment, defaultVoice);
            var referenceAudioPath = _c.ResolveReferenceAudioForSegment(item.Segment);

            try
            {
                var task = _c._inferenceEngine.GenerateSegmentTtsAsync(
                    _c._ttsService!,
                    new SingleSegmentTtsRequest(
                        text,
                        segmentAudioPath,
                        resolvedVoice,
                        item.Segment.SpeakerId,
                        referenceAudioPath,
                        Language: ttsLanguage,
                        SourceVideoPath: _c.CurrentSession.IngestedMediaPath ?? _c.CurrentSession.SourceMediaPath),
                    cancellationToken);
                _c._pendingTtsTasks.Add(task);
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
                    _c.Log.Warning($"Streaming TTS failed or file missing for segment {id}.");
                }
            }
            catch (Exception ex)
            {
                _c.Log.Error($"Streaming TTS generation failed for {id}: {ex.Message}", ex);
            }
        }

        private string BuildTranscriptArtifactPath()
        {
            var sessionDir = _c.GetSessionDirectory();
            var transcriptDir = Path.Combine(sessionDir, "transcripts");
            Directory.CreateDirectory(transcriptDir);
            var fileName = Path.GetFileNameWithoutExtension(_c.CurrentSession.IngestedMediaPath);
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
    }
}
