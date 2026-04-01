using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class FasterWhisperTranscriptionProvider : PythonSubprocessServiceBase, ITranscriptionProvider
{
    public FasterWhisperTranscriptionProvider(AppLog log) : base(log) { }

    private async Task<string> ExtractAudioAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var audioPath = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid():N}.wav");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException(
                "ffmpeg not found. Expected bundled ffmpeg.exe next to the app or ffmpeg on PATH.");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-i \"{videoPath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 -af \"loudnorm=I=-16:LRA=11:TP=-1.5\" -y \"{audioPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");

        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0 || !File.Exists(audioPath))
            throw new InvalidOperationException($"Audio extraction failed: {stderr}");

        Log.Info($"Extracted audio to: {audioPath}");
        return audioPath;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var inputPath = request.SourceAudioPath;
        var extension = Path.GetExtension(request.SourceAudioPath).ToLowerInvariant();

        if (extension == ".mp4" || extension == ".avi" || extension == ".mkv" || extension == ".mov")
        {
            inputPath = await ExtractAudioAsync(request.SourceAudioPath, cancellationToken);
        }
        else if (extension != ".wav" && extension != ".mp3" && extension != ".flac" && extension != ".ogg")
        {
            throw new InvalidOperationException($"Unsupported audio format: {extension}. Supported formats: wav, mp3, flac, ogg, mp4, avi, mkv, mov");
        }

        // model has already been validated against the whitelist by ProviderCapability before this call
        var script = $@"
import sys, json

try:
    from faster_whisper import WhisperModel
except ImportError:
    import subprocess
    print('Installing faster-whisper (this may take a few minutes on first run)...')
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'faster-whisper'])
    from faster_whisper import WhisperModel

model_name = '{request.ModelName}'
model = WhisperModel(model_name, device='cpu', compute_type='int8')

segments, info = model.transcribe(sys.argv[1])

result = {{
    'language': info.language,
    'language_probability': info.language_probability,
    'segments': []
}}

for seg in segments:
    result['segments'].append({{
        'start': seg.start,
        'end': seg.end,
        'text': seg.text
    }})

with open(sys.argv[2], 'w', encoding='utf-8') as f:
    json.dump(result, f, ensure_ascii=False, indent=2)

print('Transcription complete')
";

        Log.Info($"Starting transcription of: {inputPath}");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{inputPath}\" \"{request.OutputJsonPath}\"",
            "transcribe",
            cancellationToken);
        ThrowIfFailed(result, "Transcription");

        Log.Info($"Transcription completed: {request.OutputJsonPath}");

        var jsonContent = await File.ReadAllTextAsync(request.OutputJsonPath, cancellationToken);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var transcriptionData = JsonSerializer.Deserialize<TranscriptionJson>(jsonContent, jsonOptions);

        var segments = new List<TranscriptSegment>();
        if (transcriptionData?.Segments != null)
        {
            foreach (var seg in transcriptionData.Segments)
            {
                if (!string.IsNullOrEmpty(seg.Text))
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
            }
        }

        return new TranscriptionResult(
            true,
            segments,
            transcriptionData?.Language ?? "unknown",
            transcriptionData?.LanguageProbability ?? 0.0,
            null);
    }

    private class TranscriptionJson
    {
        public string? Language { get; set; }
        public double LanguageProbability { get; set; }
        public List<SegmentJson>? Segments { get; set; }
    }

    private class SegmentJson
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        var model = settings.TranscriptionModel;
        if (!ModelDownloader.IsFasterWhisperDownloaded(model))
            return new ProviderReadiness(false,
                $"Model '{model}' not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: $"Download faster-whisper {model} model");
        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        var model = settings.TranscriptionModel;
        if (!ModelDownloader.IsFasterWhisperDownloaded(model))
        {
            Log.Info($"Model {model} requires download. Starting download...");
            return await new ModelDownloader(Log).DownloadFasterWhisperAsync(model, progress, ct);
        }
        return true;
    }
}


