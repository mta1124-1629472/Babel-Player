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

        /// <summary>
/// Initializes a new orchestrator instance bound to the provided session workflow coordinator.
/// </summary>
/// <param name="coordinator">The coordinator that provides session state, services, and helpers used to execute the TTS pipeline.</param>
internal TtsPipelineOrchestrator(SessionWorkflowCoordinator coordinator) => _c = coordinator;

        /// <summary>
        /// Orchestrates the end-to-end text-to-speech pipeline for the current session: validates translation availability, ensures the TTS provider and reference clips are ready, generates per-segment audio, stitches segments into a combined MP3, and commits TTS state to the session.
        /// </summary>
        /// <remarks>
        /// Entry state: a session must be loaded; a translated text file path is expected to be present on <c>_c.CurrentSession.TranslationPath</c>. On success: the session's TTS metadata is persisted via <c>CommitTtsSessionState</c> and a combined MP3 is written under the session <c>tts</c> directory. The method reports stage progress via <paramref name="stageContext"/> and <paramref name="progress"/> and honours <paramref name="cancellationToken"/> for cancellable operations invoked during provider readiness, segment generation, and stitching.
        /// Guard conditions: throws <see cref="InvalidOperationException"/> when no translation path is configured, and <see cref="FileNotFoundException"/> when the configured translation file does not exist.
        /// </remarks>
        /// <param name="progress">Optional progress reporter for overall pipeline progress (0.0–1.0).</param>
        /// <param name="voice">Optional explicit voice identifier; if null the coordinator's configured TTS voice is used to resolve provider readiness and output naming.</param>
        /// <param name="stageContext">Optional pipeline stage context used for reporting stage messages and progress details.</param>
        /// <param name="cancellationToken">Token used to cancel provider readiness, segment generation, and stitching operations.</param>
        /// <exception cref="InvalidOperationException">Thrown when the current session has no translation path configured.</exception>
        /// <exception cref="FileNotFoundException">Thrown when the translation file specified by the current session does not exist.</exception>
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
