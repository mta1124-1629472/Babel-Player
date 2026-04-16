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
    public async Task<DubExportRenderResult?> TryRenderDubAudioForExportAsync(
        CancellationToken cancellationToken = default)
    {
        if (_audioProcessingService is null)
            return null;

        var session = CurrentSession;
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

        var exportDir = Path.Combine(GetSessionDirectory(), "exports", "render");
        Directory.CreateDirectory(exportDir);
        var dubPath = Path.Combine(exportDir, $"dub-timeline-{Guid.NewGuid():N}.mp3");

        var timeline = await BuildTimelineDubSegmentsAsync(ordered, session.TtsSegmentAudioPaths, cancellationToken)
            .ConfigureAwait(false);

        await _audioProcessingService.ComposeTimelineDubAsync(timeline, dubPath, cancellationToken)
            .ConfigureAwait(false);

        string? mixedPath = null;
        if (!string.IsNullOrWhiteSpace(session.AmbianceAudioPath) && File.Exists(session.AmbianceAudioPath))
        {
            mixedPath = Path.Combine(exportDir, $"dub-mixed-{Guid.NewGuid():N}.mp3");
            await _audioProcessingService.MixDubOverAmbianceAsync(
                    dubPath,
                    session.AmbianceAudioPath,
                    mixedPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new DubExportRenderResult(dubPath, mixedPath);
    }
}
