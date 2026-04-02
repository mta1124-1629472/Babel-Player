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

public sealed class GoogleSttTranscriptionProvider : ITranscriptionProvider
{
    private readonly AppLog _log;
    private readonly string _apiKey;
    private readonly Func<GoogleApiClient> _clientFactory;

    public GoogleSttTranscriptionProvider(
        AppLog log,
        string apiKey,
        Func<GoogleApiClient>? clientFactory = null)
    {
        _log = log;
        _apiKey = apiKey;
        _clientFactory = clientFactory ?? (() => new GoogleApiClient(_apiKey));
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return new ProviderReadiness(false, "API key missing for provider 'Google STT'.");

        return ProviderReadiness.Ready;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var wavPath = await EnsureLinear16WavAsync(request.SourceAudioPath, cancellationToken);

        try
        {
            var audioBytes = await File.ReadAllBytesAsync(wavPath, cancellationToken);
            var languageCode = NormalizeLanguageCode(request.LanguageHint);

            _log.Info($"[GoogleSTT] Transcribing: {wavPath} (lang={languageCode})");

            using var client = _clientFactory();
            var recognize = await client.RecognizeSpeechAsync(audioBytes, languageCode, cancellationToken);

            var transcript = string.Join(" ",
                (recognize.Results ?? [])
                    .Select(r => r.Alternatives?.FirstOrDefault()?.Transcript)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!.Trim()));

            var segments = new List<TranscriptSegment>();
            if (!string.IsNullOrWhiteSpace(transcript))
                segments.Add(new TranscriptSegment(0, 0, transcript));

            var artifact = new TranscriptArtifact
            {
                Language = languageCode,
                LanguageProbability = 1.0,
                Segments =
                [
                    .. segments.Select(s => new TranscriptSegmentArtifact
                    {
                        Start = s.StartSeconds,
                        End = s.EndSeconds,
                        Text = s.Text,
                    })
                ]
            };

            var outputDir = Path.GetDirectoryName(request.OutputJsonPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            await File.WriteAllTextAsync(request.OutputJsonPath, ArtifactJson.SerializeTranscript(artifact), cancellationToken);

            _log.Info($"[GoogleSTT] Complete: {segments.Count} segments.");

            return new TranscriptionResult(true, segments, artifact.Language ?? "unknown", artifact.LanguageProbability, null);
        }
        finally
        {
            if (!string.Equals(wavPath, request.SourceAudioPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(wavPath))
            {
                File.Delete(wavPath);
                _log.Info($"[GoogleSTT] Deleted temporary wav: {wavPath}");
            }
        }
    }

    private static string NormalizeLanguageCode(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint) || string.Equals(hint, "auto", StringComparison.OrdinalIgnoreCase))
            return "en-US";

        var normalized = hint.Trim().Replace('_', '-');
        return normalized.Length == 2
            ? normalized.ToLowerInvariant() + "-US"
            : normalized;
    }

    private async Task<string> EnsureLinear16WavAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension == ".wav")
            return sourcePath;

        var wavPath = Path.Combine(Path.GetTempPath(), $"google_stt_{Guid.NewGuid():N}.wav");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException(
                "ffmpeg not found. Expected bundled ffmpeg.exe next to the app or ffmpeg on PATH.");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(wavPath);

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");

        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0 || !File.Exists(wavPath))
            throw new InvalidOperationException($"Audio extraction failed: {stderr}");

        return wavPath;
    }
}
