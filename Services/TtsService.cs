using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class TtsService : PythonSubprocessServiceBase, ITtsService
{
    public TtsService(AppLog log) : base(log) { }

    public async Task<TtsResult> GenerateTtsAsync(
        string translationJsonPath,
        string outputAudioPath,
        string voice = "en-US-AriaNeural",
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(translationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");

        var script = @"
import sys, json, asyncio

try:
    import edge_tts
except ImportError:
    import subprocess
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'edge-tts'])
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

        Log.Info($"Starting TTS generation: {translationJsonPath} -> {outputAudioPath}");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{translationJsonPath}\" \"{outputAudioPath}\" \"{voice}\"",
            "tts",
            cancellationToken);
        ThrowIfFailed(result, "TTS");

        if (!File.Exists(outputAudioPath))
            throw new InvalidOperationException($"TTS output file not created: {outputAudioPath}");

        Log.Info($"TTS completed: {outputAudioPath}");

        return new TtsResult(
            true,
            outputAudioPath,
            voice,
            new FileInfo(outputAudioPath).Length,
            null);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        string text,
        string outputAudioPath,
        string voice = "en-US-AriaNeural",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Segment text cannot be empty", nameof(text));

        var script = @"
import sys, asyncio

try:
    import edge_tts
except ImportError:
    import subprocess
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'edge-tts'])
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

        Log.Info($"Starting segment TTS generation: {text[..Math.Min(30, text.Length)]}... -> {outputAudioPath}");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{text}\" \"{voice}\" \"{outputAudioPath}\"",
            "tts_seg",
            cancellationToken);
        ThrowIfFailed(result, "Segment TTS");

        if (!File.Exists(outputAudioPath))
            throw new InvalidOperationException($"Segment TTS output file not created: {outputAudioPath}");

        Log.Info($"Segment TTS completed: {outputAudioPath}");

        return new TtsResult(
            true,
            outputAudioPath,
            voice,
            new FileInfo(outputAudioPath).Length,
            null);
    }
    // Piper TTS implementation lives in PiperTtsService.cs
}

public sealed record TtsResult(
    bool Success,
    string AudioPath,
    string Voice,
    long FileSizeBytes,
    string? ErrorMessage);
