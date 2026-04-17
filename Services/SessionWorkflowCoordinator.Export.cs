using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    /// <summary>
    /// Renders dub audio using the same timeline rules as the pipeline (timing overrides, stretch, ambiance mix),
    /// so export matches current session configuration rather than stale on-disk TTS/mixed artifacts.
    /// </summary>
    public async Task<DubRenderResult?> TryRenderDubAudioForExportAsync(
        CancellationToken cancellationToken = default)
    {
        if (_audioProcessingService is null)
            return null;

        var session = CurrentSession;
        var ambiancePath = session.AmbianceAudioPath;
        var ambianceMixDb = CurrentSettings.AmbianceMixDb;
        if (string.IsNullOrWhiteSpace(session.TranslationPath) || !File.Exists(session.TranslationPath))
            return null;

        if (session.TtsSegmentAudioPaths is null || session.TtsSegmentAudioPaths.Count == 0)
            return null;

        var translation = await ArtifactJson.LoadTranslationAsync(session.TranslationPath, cancellationToken)
            .ConfigureAwait(false);

        var ordered = translation.Segments?
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .OrderBy(s => s.Start)
            .ToList();

        if (ordered is null || ordered.Count == 0)
            return null;

        var exportDir = Path.Combine(SessionDirectoryFor(session.SessionId), "exports", "render");
        Directory.CreateDirectory(exportDir);
        var dubPath = Path.Combine(exportDir, $"dub-timeline-{Guid.NewGuid():N}.mp3");
        return await RenderDubAudioAsync(
                ordered,
                session.TtsSegmentAudioPaths,
                dubPath,
                ambiancePath,
                ambianceMixDb,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DubRenderResult> RenderDubAudioAsync(
        IReadOnlyList<TranslationSegmentArtifact> orderedSegments,
        IReadOnlyDictionary<string, string> segmentAudioPaths,
        string dubPath,
        string? ambiancePath,
        double ambianceMixDb,
        CancellationToken cancellationToken)
    {
        if (_audioProcessingService is null)
            throw new InvalidOperationException("Audio processing service unavailable. Unable to compose dub audio.");

        var timeline = await BuildTimelineDubSegmentsAsync(orderedSegments, segmentAudioPaths, cancellationToken)
            .ConfigureAwait(false);
        await _audioProcessingService.ComposeTimelineDubAsync(timeline, dubPath, cancellationToken)
            .ConfigureAwait(false);

        if (!File.Exists(dubPath))
        {
            throw new InvalidOperationException(
                $"Dub composition completed but the expected output file was not created at '{dubPath}'.");
        }

        _log.Info($"TTS combined complete: {dubPath}");

        var ambianceExpected = !string.IsNullOrWhiteSpace(ambiancePath);
        if (ambianceExpected && !File.Exists(ambiancePath))
        {
            throw new InvalidOperationException(
                $"Ambiance stem was expected for this session but was not found at '{ambiancePath}'.");
        }
        string? mixedPath = null;

        if (ambianceExpected)
        {
            mixedPath = BuildMixedDubPath(dubPath);
            _log.Info(
                $"Starting ambiance mix: dub='{dubPath}', ambiance='{ambiancePath}', output='{mixedPath}', " +
                $"gainDb={ambianceMixDb:F1}");

            try
            {
                await _audioProcessingService.MixDubOverAmbianceAsync(
                        dubPath,
                        ambiancePath!,
                        mixedPath,
                        ambianceMixDb,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Error($"Ambiance mix failed for '{dubPath}'.", ex);
                throw new InvalidOperationException("Dub generation failed while mixing ambiance back under the dub.", ex);
            }

            if (!File.Exists(mixedPath))
            {
                var error = new InvalidOperationException(
                    $"Ambiance mix was expected but the mixed dub file was not created at '{mixedPath}'.");
                _log.Error("Ambiance expected but mixed dub file is missing.", error);
                throw error;
            }

            _log.Info($"Ambiance mix complete: {mixedPath}");
        }

        return new DubRenderResult(
            dubPath,
            mixedPath,
            ambianceExpected,
            !string.IsNullOrWhiteSpace(mixedPath) && File.Exists(mixedPath));
    }

    private static string BuildMixedDubPath(string dubPath)
    {
        var outputDir = Path.GetDirectoryName(dubPath) ?? AppContext.BaseDirectory;
        var fileName = Path.GetFileNameWithoutExtension(dubPath);
        return Path.Combine(outputDir, $"{fileName}_mixed.mp3");
    }
}
