using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed class GoogleTranslationProvider : PythonSubprocessServiceBase, ITranslationProvider
{
    public GoogleTranslationProvider(AppLog log) : base(log) { }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TranscriptJsonPath))
            throw new ArgumentException("Transcript JSON path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputJsonPath))
            throw new ArgumentException("Output JSON path cannot be null or empty.", nameof(request));
        if (!File.Exists(request.TranscriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {request.TranscriptJsonPath}");

        // googletrans.Translator.translate() is synchronous — no asyncio wrapper needed.
        var script = @"
import sys, json, asyncio

from googletrans import Translator

async def main():
    with open(sys.argv[1], 'r', encoding='utf-8') as f:
        data = json.load(f)

    source_lang = sys.argv[3] if len(sys.argv) > 3 else 'es'
    target_lang = sys.argv[4] if len(sys.argv) > 4 else 'en'

    translator = Translator()
    result = {
        'sourceLanguage': source_lang,
        'targetLanguage': target_lang,
        'segments': []
    }

    for seg in data.get('segments', []):
        text = seg.get('text', '')
        translated_text = ''
        if text:
            try:
                translated = await translator.translate(text, src=source_lang, dest=target_lang)
                translated_text = translated.text if translated else ''
            except Exception as e:
                print(f'Error translating segment: {e}', file=sys.stderr)

        start = seg.get('start', 0)
        result['segments'].append({
            'id': f'segment_{start}',
            'start': start,
            'end': seg.get('end', 0),
            'text': text,
            'translatedText': translated_text
        })

    with open(sys.argv[2], 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    print('Translation complete')

asyncio.run(main())
";

        Log.Info($"Starting translation: {request.TranscriptJsonPath} ({request.SourceLanguage} -> {request.TargetLanguage})");

        var result = await RunPythonScriptAsync(
            script,
            [request.TranscriptJsonPath, request.OutputJsonPath, request.SourceLanguage, request.TargetLanguage],
            "translate",
            cancellationToken: cancellationToken);
        ThrowIfFailed(result, "Translation");

        Log.Info($"Translation completed: {request.OutputJsonPath}");

        var transcriptData = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
        var translationData = await ArtifactJson.LoadTranslationAsync(request.OutputJsonPath, cancellationToken);
        if (translationData.Segments?.Count != transcriptData.Segments?.Count)
        {
            throw new InvalidOperationException(
                $"Translation artifact segment count mismatch: expected {transcriptData.Segments?.Count ?? 0}, got {translationData.Segments?.Count ?? 0}.");
        }

        var segments = new List<TranslatedSegment>();
        if (translationData.Segments != null)
        {
            Log.Info($"Translation JSON parsed: {translationData.Segments.Count} segments found");
            foreach (var seg in translationData.Segments)
            {
                segments.Add(new TranslatedSegment(
                    seg.Start,
                    seg.End,
                    seg.Text ?? "",
                    seg.TranslatedText ?? ""));
            }
        }
        else
        {
            Log.Warning("Translation JSON parsing failed or no segments found");
        }

        Log.Info($"TranslationResult created with {segments.Count} segments");
        return new TranslationResult(
            true,
            segments,
            translationData.SourceLanguage ?? request.SourceLanguage,
            translationData.TargetLanguage ?? request.TargetLanguage,
            null);
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        SingleSegmentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceText))
            throw new ArgumentException("Source text cannot be empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TranslationJsonPath))
            throw new ArgumentException("Translation JSON path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputJsonPath))
            throw new ArgumentException("Output JSON path cannot be null or empty.", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");

        var script = @"
import sys, json, asyncio

from googletrans import Translator

async def main():
    src_lang   = sys.argv[1]
    tgt_lang   = sys.argv[2]
    json_path  = sys.argv[3]
    seg_id     = sys.argv[4]
    text       = sys.stdin.read()

    translator = Translator()
    try:
        translated = await translator.translate(text, src=src_lang, dest=tgt_lang)
        translated_text = translated.text if translated else ''
    except Exception as e:
        print(f'Error translating segment: {e}', file=sys.stderr)
        sys.exit(1)

    try:
        with open(json_path, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except FileNotFoundError:
        data = {'segments': []}

    updated = False
    for seg in data.get('segments', []):
        if seg.get('id') == seg_id:
            seg['translatedText'] = translated_text
            updated = True
            break

    if not updated:
        print(f'Segment not found: {seg_id}', file=sys.stderr)
        sys.exit(1)

    with open(json_path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

    print(f'Single segment translated: {seg_id}')

asyncio.run(main())
";

        Log.Info($"Starting single segment translation: {request.SourceText[..Math.Min(30, request.SourceText.Length)]}...");

        var result = await RunPythonScriptAsync(
            script,
            [request.SourceLanguage, request.TargetLanguage, request.TranslationJsonPath, request.SegmentId],
            "translate_seg",
            standardInput: request.SourceText,
            cancellationToken: cancellationToken);
        ThrowIfFailed(result, "Single segment translation");

        Log.Info($"Single segment translation completed: {request.TranslationJsonPath}");

        var translationData = await ArtifactJson.LoadTranslationAsync(request.TranslationJsonPath, cancellationToken);

        var segments = new List<TranslatedSegment>();
        if (translationData.Segments != null)
        {
            foreach (var seg in translationData.Segments)
            {
                segments.Add(new TranslatedSegment(
                    seg.Start,
                    seg.End,
                    seg.Text ?? "",
                    seg.TranslatedText ?? ""));
            }
        }

        return new TranslationResult(
            true,
            segments,
            translationData.SourceLanguage ?? request.SourceLanguage,
            translationData.TargetLanguage ?? request.TargetLanguage,
            null);
    }


}


