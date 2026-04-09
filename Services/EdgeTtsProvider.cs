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
        if (string.IsNullOrWhiteSpace(request.TranslationJsonPath))
            throw new ArgumentException("Translation JSON path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var script = @"
import sys, json, asyncio
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
            [request.TranslationJsonPath, request.OutputAudioPath, request.VoiceName],
            "tts",
            cancellationToken: cancellationToken);
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
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));

        var script = @"
import sys, asyncio
import edge_tts

async def generate():
    voice = sys.argv[1] if len(sys.argv) > 1 else 'en-US-AriaNeural'
    output_path = sys.argv[2]
    text = sys.stdin.read()

    communicate = edge_tts.Communicate(text, voice)
    await communicate.save(output_path)

    print(f'Segment TTS generated: {output_path}')

asyncio.run(generate())
";

        Log.Info($"Starting segment TTS generation: {request.Text[..Math.Min(30, request.Text.Length)]}... -> {request.OutputAudioPath}");

        var result = await RunPythonScriptAsync(
            script,
            [request.VoiceName, request.OutputAudioPath],
            "tts_seg",
            standardInput: request.Text,
            cancellationToken: cancellationToken);
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


