using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// TTS provider backed by containerized XTTS endpoints.
/// Uses per-segment reference audio when available to enable voice cloning.
/// </summary>
public sealed class XttsContainerTtsProvider : ITtsProvider
{
    private readonly ContainerizedInferenceClient _client;
    private readonly AppLog _log;
    private readonly Dictionary<string, string> _referenceIdBySpeakerPath = new(StringComparer.OrdinalIgnoreCase);

    public XttsContainerTtsProvider(ContainerizedInferenceClient client, AppLog log)
    {
        _client = client;
        _log = log;
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null) =>
        ContainerizedProviderReadiness.CheckTts(settings);

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

        _log.Info($"[XttsContainerTts] Segment synth start (speaker={request.SpeakerId ?? "<none>"})");

        var referenceId = await ResolveReferenceIdAsync(
            request.SpeakerId,
            request.ReferenceAudioPath,
            request.ReferenceTranscriptText,
            cancellationToken);

        var result = await _client.XttsSegmentAsync(
            request.Text,
            request.SpeakerId,
            request.ReferenceAudioPath,
            referenceId,
            request.ReferenceTranscriptText,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Containerized XTTS failed: {result.ErrorMessage}");

        await DownloadToOutputPathAsync(result.AudioPath, request.OutputAudioPath, cancellationToken);

        _log.Info($"[XttsContainerTts] Segment synth saved: {request.OutputAudioPath}");
        return result with { AudioPath = request.OutputAudioPath };
    }

    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var translation = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);
        var parts = new List<string>();
        string? defaultReferencePath = null;
        string? defaultSpeakerId = null;

        foreach (var seg in translation.Segments ?? [])
        {
            if (string.IsNullOrWhiteSpace(seg.TranslatedText))
                continue;

            parts.Add(seg.TranslatedText!);

            if (defaultReferencePath is null && !string.IsNullOrWhiteSpace(seg.SpeakerId) && request.SpeakerReferenceAudioPaths is not null &&
                request.SpeakerReferenceAudioPaths.TryGetValue(seg.SpeakerId!, out var referencePath) && !string.IsNullOrWhiteSpace(referencePath))
            {
                defaultReferencePath = referencePath;
                defaultSpeakerId = seg.SpeakerId;
            }
        }

        if (parts.Count == 0)
            throw new InvalidOperationException("No translated text found for XTTS synthesis.");

        var combined = string.Join(" ", parts);
        _log.Info($"[XttsContainerTts] Combined synth start (segments={parts.Count}, speaker={defaultSpeakerId ?? "<none>"})");

        var referenceId = await ResolveReferenceIdAsync(
            defaultSpeakerId,
            defaultReferencePath,
            null,
            cancellationToken);

        var result = await _client.XttsSegmentAsync(
            combined,
            defaultSpeakerId,
            defaultReferencePath,
            referenceId,
            null,
            cancellationToken);

        if (!result.Success)
            throw new InvalidOperationException($"Containerized XTTS combined synthesis failed: {result.ErrorMessage}");

        await DownloadToOutputPathAsync(result.AudioPath, request.OutputAudioPath, cancellationToken);

        _log.Info($"[XttsContainerTts] Combined synth saved: {request.OutputAudioPath}");
        return result with { AudioPath = request.OutputAudioPath };
    }

    private async Task DownloadToOutputPathAsync(string serverAudioPath, string localOutputPath, CancellationToken ct)
    {
        var filename = Path.GetFileName(serverAudioPath);
        if (string.IsNullOrWhiteSpace(filename))
            throw new InvalidOperationException($"Cannot extract filename from server audio path: '{serverAudioPath}'");

        var outputDir = Path.GetDirectoryName(localOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);

        await _client.DownloadTtsAudioAsync(filename, localOutputPath, ct);
    }

    private async Task<string?> ResolveReferenceIdAsync(
        string? speakerId,
        string? referenceAudioPath,
        string? referenceTranscript,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(referenceAudioPath))
            return null;

        var key = $"{speakerId ?? "<none>"}|{referenceAudioPath}";
        if (_referenceIdBySpeakerPath.TryGetValue(key, out var cachedReferenceId))
            return cachedReferenceId;

        var generatedReferenceId = await _client.RegisterXttsReferenceAsync(
            speakerId ?? "speaker",
            referenceAudioPath,
            referenceTranscript,
            ct);

        _referenceIdBySpeakerPath[key] = generatedReferenceId;
        return generatedReferenceId;
    }
}
