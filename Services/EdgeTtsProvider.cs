using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class EdgeTtsProvider : PythonSubprocessServiceBase, ITtsProvider
{
    public EdgeTtsProvider(AppLog log) : base(log) { }

    public async Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

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

        Log.Info($"Starting TTS generation: {request.TranslationJsonPath} -> {request.OutputAudioPath}");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{request.TranslationJsonPath}\" \"{request.OutputAudioPath}\" \"{request.VoiceName}\"",
            "tts",
            cancellationToken);
        ThrowIfFailed(result, "TTS");

        if (!File.Exists(request.OutputAudioPath))
            throw new InvalidOperationException($"TTS output file not created: {request.OutputAudioPath}");

        Log.Info($"TTS completed: {request.OutputAudioPath}");

        return new TtsResult(
            true,
            request.OutputAudioPath,
            request.VoiceName,
            new FileInfo(request.OutputAudioPath).Length,
            null);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));

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

        Log.Info($"Starting segment TTS generation: {request.Text[..Math.Min(30, request.Text.Length)]}... -> {request.OutputAudioPath}");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{request.Text}\" \"{request.VoiceName}\" \"{request.OutputAudioPath}\"",
            "tts_seg",
            cancellationToken);
        ThrowIfFailed(result, "Segment TTS");

        if (!File.Exists(request.OutputAudioPath))
            throw new InvalidOperationException($"Segment TTS output file not created: {request.OutputAudioPath}");

        Log.Info($"Segment TTS completed: {request.OutputAudioPath}");

        return new TtsResult(
            true,
            request.OutputAudioPath,
            request.VoiceName,
            new FileInfo(request.OutputAudioPath).Length,
            null);
    }
    // Piper TTS implementation lives in PiperTtsProvider.cs
}


