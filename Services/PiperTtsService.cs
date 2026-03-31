using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class PiperTtsService : ITtsService
{
    private readonly AppLog _log;
    private readonly string _modelDir;
    private readonly string _pythonPath;

    public PiperTtsService(AppLog log, string modelDir)
    {
        _log = log;
        _modelDir = modelDir;
        _pythonPath = DependencyLocator.FindPython()
            ?? throw new InvalidOperationException(
                "Python not found. Piper TTS requires Python and the piper CLI installed.");
    }

    private const string PiperScript = @"
import sys, json, os, subprocess, platform, shutil

if not shutil.which('piper'):
    raise RuntimeError(
        'piper CLI not found on PATH. '
        'Install it from https://github.com/rhasspy/piper/releases '
        'and ensure the piper executable is on your system PATH.')


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
import sys, os, subprocess, platform, shutil

if not shutil.which('piper'):
    raise RuntimeError(
        'piper CLI not found on PATH. '
        'Install it from https://github.com/rhasspy/piper/releases '
        'and ensure the piper executable is on your system PATH.')


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

    public async Task<TtsResult> GenerateTtsAsync(
        string translationJsonPath,
        string outputAudioPath,
        string voice)
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
                Arguments              = $"\"{scriptPath}\" \"{translationJsonPath}\" \"{outputAudioPath}\" \"{voice}\" \"{_modelDir}\"",
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Piper TTS process.");
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

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        string text,
        string outputAudioPath,
        string voice)
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
                Arguments              = $"\"{scriptPath}\" \"{text}\" \"{outputAudioPath}\" \"{voice}\" \"{_modelDir}\"",
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Piper segment TTS process.");
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
