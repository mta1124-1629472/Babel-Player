using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Babel.Player.Services;

public sealed class TranscriptionService
{
    private readonly AppLog _log;
    private readonly string _pythonPath;

    public TranscriptionService(AppLog log)
    {
        _log = log;
        _pythonPath = DependencyLocator.FindPython()
            ?? throw new InvalidOperationException(
                "Python not found. Expected bundled python next to the app or python on PATH.");
    }

    private async Task<string> ExtractAudioAsync(string videoPath)
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
        {
            throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");
        }

        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 || !File.Exists(audioPath))
        {
            throw new InvalidOperationException($"Audio extraction failed: {stderr}");
        }

        _log.Info($"Extracted audio to: {audioPath}");
        return audioPath;
    }

    public async Task<TranscriptionResult> TranscribeAsync(string audioPath, string outputJsonPath)
    {
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException($"Audio file not found: {audioPath}");
        }

        var inputPath = audioPath;
        var extension = Path.GetExtension(audioPath).ToLowerInvariant();
        
        if (extension == ".mp4" || extension == ".avi" || extension == ".mkv" || extension == ".mov")
        {
            inputPath = await ExtractAudioAsync(audioPath);
        }
        else if (extension != ".wav" && extension != ".mp3" && extension != ".flac" && extension != ".ogg")
        {
            throw new InvalidOperationException($"Unsupported audio format: {extension}. Supported formats: wav, mp3, flac, ogg, mp4, avi, mkv, mov");
        }

        var script = @"
import sys
import json
from faster_whisper import WhisperModel

model_name = 'base'
model = WhisperModel(model_name, device='cpu', compute_type='int8')

segments, info = model.transcribe(sys.argv[1])

result = {
    'language': info.language,
    'language_probability': info.language_probability,
    'segments': []
}

for seg in segments:
    result['segments'].append({
        'start': seg.start,
        'end': seg.end,
        'text': seg.text
    })

with open(sys.argv[2], 'w', encoding='utf-8') as f:
    json.dump(result, f, ensure_ascii=False, indent=2)

print('Transcription complete')
";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"transcribe_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            _log.Info($"Starting transcription of: {inputPath}");

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{scriptPath}\" \"{audioPath}\" \"{outputJsonPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException("Failed to start transcription process.");
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"Transcription failed with exit code {proc.ExitCode}", new Exception(stderr));
                throw new InvalidOperationException($"Transcription failed: {stderr}");
            }

            _log.Info($"Transcription completed: {outputJsonPath}");

            var jsonContent = await File.ReadAllTextAsync(outputJsonPath);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var transcriptionData = JsonSerializer.Deserialize<TranscriptionJson>(jsonContent, jsonOptions);

            var segments = new List<TranscriptSegment>();
            if (transcriptionData?.Segments != null)
            {
                foreach (var seg in transcriptionData.Segments)
                {
                    if (!string.IsNullOrEmpty(seg.Text))
                    {
                        segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
                    }
                }
            }

            return new TranscriptionResult(
                true,
                segments,
                transcriptionData?.Language ?? "unknown",
                transcriptionData?.LanguageProbability ?? 0.0,
                null);
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
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
}

public sealed record TranscriptionResult(
    bool Success,
    IReadOnlyList<TranscriptSegment> Segments,
    string Language,
    double LanguageProbability,
    string? ErrorMessage);

public sealed record TranscriptSegment(
    double StartSeconds,
    double EndSeconds,
    string Text);
