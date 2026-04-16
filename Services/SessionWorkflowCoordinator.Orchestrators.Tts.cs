using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    internal sealed class TtsPipelineOrchestrator
    {
        private readonly SessionWorkflowCoordinator _c;

        internal TtsPipelineOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        internal async Task ExecuteAsync(
            IProgress<double>? progress,
            string? voice,
            PipelineStageContext? stageContext,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_c.CurrentSession.TranslationPath))
                throw new InvalidOperationException("No translation available. Please translate first.");

            if (!File.Exists(_c.CurrentSession.TranslationPath))
                throw new FileNotFoundException($"Translation file not found: {_c.CurrentSession.TranslationPath}");

            var v = voice ?? _c.CurrentSettings.TtsVoice;

            await _c.EnsureTtsProviderReadyAsync(v, progress, stageContext, cancellationToken);

            _c._ttsService ??= _c.CreateTtsService();
            await _c.EnsureSingleSpeakerQwenReferenceClipAsync(cancellationToken);
            await _c.EnsureMultiSpeakerReferenceClipsAsync(cancellationToken);

            ReportStage(
                stageContext,
                $"Starting TTS synthesis with {_c.CurrentSettings.TtsProvider} / {v}. Generating combined dub audio — progress will appear below.",
                progress01: 0,
                isIndeterminate: false);

            var sessionDir = _c.GetSessionDirectory();
            var ttsDir = Path.Combine(sessionDir, "tts");
            Directory.CreateDirectory(ttsDir);

            var fileName = Path.GetFileNameWithoutExtension(_c.CurrentSession.TranslationPath);
            // Sanitize the voice identifier so reserved/path characters don't produce invalid file names.
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitizedVoice = string.Concat((v ?? string.Empty).Split(invalidChars)).Trim();
            if (sanitizedVoice.Length == 0) sanitizedVoice = "default";
            var ttsPath = Path.Combine(ttsDir, $"{fileName}_{sanitizedVoice}.mp3");
            var ttsLanguage = NormalizePipelineLanguage(
                _c.CurrentSession.TargetLanguage ?? _c.CurrentSettings.TargetLanguage,
                _c.CurrentSettings.TargetLanguage);
            var segmentsDir = Path.Combine(ttsDir, "segments", Path.GetFileNameWithoutExtension(_c.CurrentSession.TranslationPath!));
            Directory.CreateDirectory(segmentsDir);

            _c.Log.Info($"Starting TTS generation: {_c.CurrentSession.TranslationPath} -> {ttsPath}");

            var (segmentAudioPaths, segmentDurations, totalSegments, orderedSegments) = await _c.GenerateSegmentClipsAsync(
                v, ttsLanguage, segmentsDir, stageContext, cancellationToken);

            await _c.StitchSegmentClipsAsync(segmentAudioPaths, orderedSegments, ttsPath, stageContext, cancellationToken);

            _c.CommitTtsSessionState(v, ttsPath, segmentsDir, segmentAudioPaths, segmentDurations, totalSegments, stageContext);
        }
    }
}
