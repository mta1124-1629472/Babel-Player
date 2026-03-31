using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Babel.Player.Services;

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
    // ── Piper TTS (local inference, no internet required) ─────────────────────

    private const string PiperScript = @"
import sys, json, os, subprocess, platform

input_path = sys.argv[1]
output_path = sys.argv[2]
voice      = sys.argv[3]
model_dir  = sys.argv[4]

def find_model(voice, model_dir):
    search_dirs = []
    if model_dir:
        search_dirs.append(model_dir)
    if platform.system() == 'Windows':
        local = os.environ.get('LOCALAPPDATA', '')
        search_dirs.append(os.path.join(local, 'piper', 'voices'))
    else:
        search_dirs.append(os.path.expanduser('~/.local/share/piper/voices'))
    for d in search_dirs:
        for name in [f'{voice}.onnx', os.path.join(voice, f'{voice}.onnx')]:
            path = os.path.join(d, name)
            if os.path.exists(path):
                return path
    return None

model_path = find_model(voice, model_dir)
if model_path is None:
    raise FileNotFoundError(
        f'Piper voice model not found: {voice}. '
        f'Download the .onnx file to %LOCALAPPDATA%\\piper\\voices\\ (Windows) '
        f'or ~/.local/share/piper/voices/ (Linux/macOS).')

with open(input_path, encoding='utf-8') as f:
    data = json.load(f)

text = ' '.join(
    seg.get('translatedText', '')
    for seg in data.get('segments', [])
    if seg.get('translatedText', '').strip()
)

result = subprocess.run(
    ['piper', '--model', model_path, '--output_file', output_path],
    input=text, text=True, capture_output=True)
if result.returncode != 0:
    raise RuntimeError(f'Piper failed: {result.stderr}')
print(f'Piper TTS generated: {output_path}')
";

    private const string PiperSegmentScript = @"
import sys, os, subprocess, platform

text       = sys.argv[1]
output_path = sys.argv[2]
voice      = sys.argv[3]
model_dir  = sys.argv[4]

def find_model(voice, model_dir):
    search_dirs = []
    if model_dir:
        search_dirs.append(model_dir)
    if platform.system() == 'Windows':
        local = os.environ.get('LOCALAPPDATA', '')
        search_dirs.append(os.path.join(local, 'piper', 'voices'))
    else:
        search_dirs.append(os.path.expanduser('~/.local/share/piper/voices'))
    for d in search_dirs:
        for name in [f'{voice}.onnx', os.path.join(voice, f'{voice}.onnx')]:
            path = os.path.join(d, name)
            if os.path.exists(path):
                return path
    return None

model_path = find_model(voice, model_dir)
if model_path is None:
    raise FileNotFoundError(
        f'Piper voice model not found: {voice}. '
        f'Download the .onnx file to %LOCALAPPDATA%\\piper\\voices\\ (Windows) '
        f'or ~/.local/share/piper/voices/ (Linux/macOS).')

result = subprocess.run(
    ['piper', '--model', model_path, '--output_file', output_path],
    input=text, text=True, capture_output=True)
if result.returncode != 0:
    raise RuntimeError(f'Piper failed: {result.stderr}')
print(f'Piper segment TTS generated: {output_path}')
";

    public async Task<TtsResult> GeneratePiperTtsAsync(
        string translationJsonPath,
        string outputAudioPath,
        string voice,
        string modelDir)
    {
        if (!File.Exists(translationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"piper_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, PiperScript);

        try
        {
            _log.Info($"Starting Piper TTS ({voice}): {translationJsonPath} -> {outputAudioPath}");

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonPath,
                Arguments              = $"\"{scriptPath}\" \"{translationJsonPath}\" \"{outputAudioPath}\" \"{voice}\" \"{modelDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Piper TTS process.");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"Piper TTS failed (exit {proc.ExitCode})", new Exception(stderr));
                throw new InvalidOperationException($"Piper TTS failed: {stderr}");
            }

            if (!File.Exists(outputAudioPath))
                throw new InvalidOperationException($"Piper TTS output file not created: {outputAudioPath}");

            _log.Info($"Piper TTS completed: {outputAudioPath}");
            return new TtsResult(true, outputAudioPath, voice, new FileInfo(outputAudioPath).Length, null);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    public async Task<TtsResult> GenerateSegmentPiperTtsAsync(
        string text,
        string outputAudioPath,
        string voice,
        string modelDir)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Segment text cannot be empty", nameof(text));

        var scriptPath = Path.Combine(Path.GetTempPath(), $"piper_seg_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, PiperSegmentScript);

        try
        {
            _log.Info($"Starting Piper segment TTS ({voice}): {text[..Math.Min(30, text.Length)]}... -> {outputAudioPath}");

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonPath,
                Arguments              = $"\"{scriptPath}\" \"{text}\" \"{outputAudioPath}\" \"{voice}\" \"{modelDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Piper segment TTS process.");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"Piper segment TTS failed (exit {proc.ExitCode})", new Exception(stderr));
                throw new InvalidOperationException($"Piper segment TTS failed: {stderr}");
            }

            if (!File.Exists(outputAudioPath))
                throw new InvalidOperationException($"Piper segment TTS output file not created: {outputAudioPath}");

            _log.Info($"Piper segment TTS completed: {outputAudioPath}");
            return new TtsResult(true, outputAudioPath, voice, new FileInfo(outputAudioPath).Length, null);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }
}

public sealed record TtsResult(
    bool Success,
    string AudioPath,
    string Voice,
    long FileSizeBytes,
    string? ErrorMessage);
