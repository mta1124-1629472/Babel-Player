using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Babel.Deck.Services;

public sealed class TtsService(AppLog log)
{
    private readonly AppLog _log = log;
    private readonly string _pythonPath = FindPythonPath();

    private static string FindPythonPath()
    {
        var appDir = AppContext.BaseDirectory;

        var possiblePaths = new[]
        {
            Path.Combine(appDir, "python.exe"),
            Path.Combine(appDir, "python", "python.exe"),
            "python",
            "python3",
        };

        foreach (var path in possiblePaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0)
                    {
                        return path;
                    }
                }
            }
            catch
            {
            }
        }

        throw new InvalidOperationException(
            "Python not found. Expected bundled python next to the app or python on PATH.");
    }

    public async Task<TtsResult> GenerateTtsAsync(
        string translationJsonPath, 
        string outputAudioPath,
        string voice = "en-US-AriaNeural")
    {
        if (!File.Exists(translationJsonPath))
        {
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");
        }

        var script = @"
import sys
import json
import asyncio
import edge_tts

async def generate():
    with open(sys.argv[1], 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    voice = sys.argv[3] if len(sys.argv) > 3 else 'en-US-AriaNeural'
    output_path = sys.argv[2]
    
    texts = []
    if 'segments' in data:
        for seg in data['segments']:
            text = seg.get('translatedText', '')
            if text:
                texts.append(text)
    
    if not texts:
        print('No translated text found', file=sys.stderr)
        sys.exit(1)
    
    combined_text = ' '.join(texts)
    
    communicate = edge_tts.Communicate(combined_text, voice)
    await communicate.save(output_path)
    
    print(f'TTS generated: {output_path}')

asyncio.run(generate())
";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            _log.Info($"Starting TTS generation: {translationJsonPath} -> {outputAudioPath}");

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{scriptPath}\" \"{translationJsonPath}\" \"{outputAudioPath}\" \"{voice}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start TTS process.");

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"TTS failed with exit code {proc.ExitCode}", new Exception(stderr));
                throw new InvalidOperationException($"TTS failed: {stderr}");
            }

            if (!File.Exists(outputAudioPath))
            {
                throw new InvalidOperationException($"TTS output file not created: {outputAudioPath}");
            }

            _log.Info($"TTS completed: {outputAudioPath}");

            var fileInfo = new FileInfo(outputAudioPath);
            
            return new TtsResult(
                true,
                outputAudioPath,
                voice,
                fileInfo.Length,
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

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        string text,
        string outputAudioPath,
        string voice = "en-US-AriaNeural")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Segment text cannot be empty", nameof(text));
        }

        var script = @"
import sys
import asyncio
import edge_tts

async def generate():
    text = sys.argv[1]
    voice = sys.argv[2] if len(sys.argv) > 2 else 'en-US-AriaNeural'
    output_path = sys.argv[3]
    
    communicate = edge_tts.Communicate(text, voice)
    await communicate.save(output_path)
    
    print(f'Segment TTS generated: {output_path}')

asyncio.run(generate())
";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"tts_seg_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            _log.Info($"Starting segment TTS generation: {text[..Math.Min(30, text.Length)]}... -> {outputAudioPath}");

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{scriptPath}\" \"{text}\" \"{voice}\" \"{outputAudioPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start TTS process.");

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"Segment TTS failed with exit code {proc.ExitCode}", new Exception(stderr));
                throw new InvalidOperationException($"Segment TTS failed: {stderr}");
            }

            if (!File.Exists(outputAudioPath))
            {
                throw new InvalidOperationException($"Segment TTS output file not created: {outputAudioPath}");
            }

            _log.Info($"Segment TTS completed: {outputAudioPath}");

            var fileInfo = new FileInfo(outputAudioPath);
            
            return new TtsResult(
                true,
                outputAudioPath,
                voice,
                fileInfo.Length,
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
}

public sealed record TtsResult(
    bool Success,
    string AudioPath,
    string Voice,
    long FileSizeBytes,
    string? ErrorMessage);
