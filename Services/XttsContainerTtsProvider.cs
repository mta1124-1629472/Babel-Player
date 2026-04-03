using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly Func<IReadOnlyList<string>, string, CancellationToken, Task> _combineAudioFunc;
    private readonly Dictionary<string, string> _referenceIdBySpeakerPath = new(StringComparer.OrdinalIgnoreCase);

    public XttsContainerTtsProvider(
        ContainerizedInferenceClient client,
        AppLog log,
        Func<IReadOnlyList<string>, string, CancellationToken, Task>? combineAudioFunc = null)
    {
        _client = client;
        _log = log;
        _combineAudioFunc = combineAudioFunc ?? CombineAudioSegmentsAsync;
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
        if (string.IsNullOrWhiteSpace(request.ReferenceAudioPath) && string.IsNullOrWhiteSpace(referenceId))
        {
            throw new InvalidOperationException(
                "XTTS requires a speaker reference audio clip. Assign a reference clip before generating GPU TTS.");
        }
        var language = ResolveLanguage(request.Language);
        var model = ResolveModel(request.VoiceName);

        var result = await _client.XttsSegmentAsync(
            request.Text,
            model,
            language,
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
        var segmentAudioPaths = new List<string>();
        var workDir = Path.Combine(
            Path.GetTempPath(),
            $"babel-xtts-{Path.GetFileNameWithoutExtension(request.OutputAudioPath)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var language = ResolveLanguage(request.Language ?? translation.TargetLanguage);

        try
        {
            var segmentIndex = 0;
            foreach (var seg in translation.Segments ?? [])
            {
                if (string.IsNullOrWhiteSpace(seg.TranslatedText))
                    continue;

                segmentIndex++;
                var resolvedModel = ResolveModel(ResolveVoiceForSegment(seg, request));
                var referenceAudioPath = ResolveReferenceAudioForSegment(seg, request);
                var referenceId = await ResolveReferenceIdAsync(
                    seg.SpeakerId,
                    referenceAudioPath,
                    null,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(referenceAudioPath) && string.IsNullOrWhiteSpace(referenceId))
                {
                    throw new InvalidOperationException(
                        $"XTTS requires a reference clip for speaker '{seg.SpeakerId ?? "default"}'.");
                }

                _log.Info(
                    $"[XttsContainerTts] Combined synth segment start " +
                    $"(segment={seg.Id ?? segmentIndex.ToString()}, speaker={seg.SpeakerId ?? "<none>"}, model={resolvedModel})");

                var result = await _client.XttsSegmentAsync(
                    seg.TranslatedText!,
                    resolvedModel,
                    language,
                    seg.SpeakerId,
                    referenceAudioPath,
                    referenceId,
                    null,
                    cancellationToken);

                if (!result.Success)
                    throw new InvalidOperationException($"Containerized XTTS combined synthesis failed: {result.ErrorMessage}");

                var segmentAudioPath = Path.Combine(
                    workDir,
                    $"{segmentIndex:D4}_{SanitizeFileComponent(seg.Id ?? $"segment_{segmentIndex}")}.mp3");

                await DownloadToOutputPathAsync(result.AudioPath, segmentAudioPath, cancellationToken);
                segmentAudioPaths.Add(segmentAudioPath);
            }

            if (segmentAudioPaths.Count == 0)
                throw new InvalidOperationException("No translated text found for XTTS synthesis.");

            var outputDir = Path.GetDirectoryName(request.OutputAudioPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
                Directory.CreateDirectory(outputDir);

            if (segmentAudioPaths.Count == 1)
            {
                File.Copy(segmentAudioPaths[0], request.OutputAudioPath, overwrite: true);
            }
            else
            {
                await _combineAudioFunc(segmentAudioPaths, request.OutputAudioPath, cancellationToken);
            }

            _log.Info($"[XttsContainerTts] Combined synth saved: {request.OutputAudioPath}");
            return new TtsResult(
                true,
                request.OutputAudioPath,
                ResolveModel(request.VoiceName),
                new FileInfo(request.OutputAudioPath).Length,
                null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task CombineAudioSegmentsAsync(
        IReadOnlyList<string> segmentAudioPaths,
        string outputAudioPath,
        CancellationToken cancellationToken)
    {
        if (segmentAudioPaths.Count == 0)
            throw new InvalidOperationException("Cannot combine zero XTTS segment audio files.");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg not found. XTTS combined output requires ffmpeg.");
        var concatListDir = Path.Combine(Path.GetTempPath(), $"babel-xtts-concat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(concatListDir);
        var concatListPath = Path.Combine(concatListDir, "inputs.txt");
        var concatFile = string.Join(
            Environment.NewLine,
            segmentAudioPaths.Select(path => $"file '{EscapeConcatListPath(path)}'"));

        await File.WriteAllTextAsync(concatListPath, concatFile, cancellationToken);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("concat");
            psi.ArgumentList.Add("-safe");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(concatListPath);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-q:a");
            psi.ArgumentList.Add("3");
            psi.ArgumentList.Add(outputAudioPath);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start ffmpeg for XTTS segment concatenation.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg XTTS concatenation failed with exit code {process.ExitCode}: {stderr} {stdout}".Trim());
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(concatListDir))
                    Directory.Delete(concatListDir, recursive: true);
            }
            catch
            {
            }
        }
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

    private static string ResolveVoiceForSegment(TranslationSegmentArtifact segment, TtsRequest request)
    {
        if (!string.IsNullOrWhiteSpace(segment.SpeakerId)
            && request.SpeakerVoiceAssignments is not null
            && request.SpeakerVoiceAssignments.TryGetValue(segment.SpeakerId!, out var mappedVoice)
            && !string.IsNullOrWhiteSpace(mappedVoice))
        {
            return mappedVoice;
        }

        return !string.IsNullOrWhiteSpace(request.DefaultVoiceFallback)
            ? request.DefaultVoiceFallback
            : request.VoiceName;
    }

    private static string? ResolveReferenceAudioForSegment(TranslationSegmentArtifact segment, TtsRequest request)
    {
        if (request.SpeakerReferenceAudioPaths is null)
            return null;

        if (!string.IsNullOrWhiteSpace(segment.SpeakerId)
            && request.SpeakerReferenceAudioPaths.TryGetValue(segment.SpeakerId!, out var referencePath)
            && !string.IsNullOrWhiteSpace(referencePath))
        {
            return referencePath;
        }

        return request.SpeakerReferenceAudioPaths.TryGetValue(XttsReferenceKeys.SingleSpeakerDefault, out var defaultReferencePath)
               && !string.IsNullOrWhiteSpace(defaultReferencePath)
            ? defaultReferencePath
            : null;
    }

    private static string ResolveLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "en";

        var normalized = language.Trim().ToLowerInvariant();
        return normalized switch
        {
            "zh" or "zh-hans" or "zh-cn" => "zh-cn",
            "pt-br" or "pt-pt" => "pt",
            _ when normalized.Contains('-') => normalized.Split('-', 2)[0],
            _ => normalized,
        };
    }

    private static string ResolveModel(string? voiceName) =>
        string.IsNullOrWhiteSpace(voiceName) ? "xtts-v2" : voiceName.Trim();

    private static string SanitizeFileComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string EscapeConcatListPath(string path) =>
        path.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);
}
