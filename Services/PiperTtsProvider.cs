using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class PiperTtsProvider : PythonSubprocessServiceBase, ITtsProvider
{
    private readonly string _modelDir;

    public PiperTtsProvider(AppLog log, string modelDir) : base(log)
    {
        _modelDir = modelDir;
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


output_path = sys.argv[1]
voice      = sys.argv[2]
model_dir  = sys.argv[3]
text       = sys.stdin.read()

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
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TranslationJsonPath))
            throw new ArgumentException("Translation JSON path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        Log.Info($"Starting Piper TTS ({request.VoiceName}): {request.TranslationJsonPath} -> {request.OutputAudioPath}");

        var result = await RunPythonScriptAsync(
            PiperScript,
            [request.TranslationJsonPath, request.OutputAudioPath, request.VoiceName, _modelDir],
            "piper_tts",
            cancellationToken: cancellationToken);
        ThrowIfFailed(result, "Piper TTS");

        if (!File.Exists(request.OutputAudioPath))
            throw new InvalidOperationException($"Piper TTS output file not created: {request.OutputAudioPath}");

        Log.Info($"Piper TTS completed: {request.OutputAudioPath}");
        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));

        Log.Info($"Starting Piper segment TTS ({request.VoiceName}): {request.Text[..Math.Min(30, request.Text.Length)]}... -> {request.OutputAudioPath}");

        var result = await RunPythonScriptAsync(
            PiperSegmentScript,
            [request.OutputAudioPath, request.VoiceName, _modelDir],
            "piper_tts_seg",
            standardInput: request.Text,
            cancellationToken: cancellationToken);
        ThrowIfFailed(result, "Piper segment TTS");

        if (!File.Exists(request.OutputAudioPath))
            throw new InvalidOperationException($"Piper segment TTS output file not created: {request.OutputAudioPath}");

        Log.Info($"Piper segment TTS completed: {request.OutputAudioPath}");
        return new TtsResult(true, request.OutputAudioPath, request.VoiceName, new FileInfo(request.OutputAudioPath).Length, null);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        var voice = settings.TtsVoice;
        if (!ModelDownloader.IsPiperVoiceDownloaded(voice, settings.PiperModelDir))
            return new ProviderReadiness(false,
                $"Voice '{voice}' not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: $"Download Piper voice {voice}");
        if (DependencyLocator.FindPiper() is null)
            return new ProviderReadiness(false,
                "Piper CLI not found on PATH. Install from https://github.com/rhasspy/piper/releases.");
        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        var voice = settings.TtsVoice;
        if (!ModelDownloader.IsPiperVoiceDownloaded(voice, settings.PiperModelDir))
        {
            Log.Info($"Voice {voice} requires download. Starting download...");
            return await new ModelDownloader(Log).DownloadPiperVoiceAsync(voice, settings.PiperModelDir, progress, ct);
        }
        return true;
    }
}
