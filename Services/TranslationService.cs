using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class TranslationService : PythonSubprocessServiceBase, ITranslationService
{
    public TranslationService(AppLog log) : base(log) { }

    public async Task<TranslationResult> TranslateAsync(
        string transcriptJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(transcriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {transcriptJsonPath}");

        // googletrans.Translator.translate() is synchronous — no asyncio wrapper needed.
        var script = @"
import sys, json

try:
    from googletrans import Translator
except ImportError:
    import subprocess
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'googletrans==4.0.0rc1'])
    from googletrans import Translator

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
            translated = translator.translate(text, src=source_lang, dest=target_lang)
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
";

        Log.Info($"Starting translation: {transcriptJsonPath} ({sourceLanguage} -> {targetLanguage})");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{transcriptJsonPath}\" \"{outputJsonPath}\" \"{sourceLanguage}\" \"{targetLanguage}\"",
            "translate",
            cancellationToken);
        ThrowIfFailed(result, "Translation");

        Log.Info($"Translation completed: {outputJsonPath}");

        var jsonContent = await File.ReadAllTextAsync(outputJsonPath);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var translationData = JsonSerializer.Deserialize<TranslationJson>(jsonContent, jsonOptions);

        var segments = new List<TranslatedSegment>();
        if (translationData?.Segments != null)
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
            translationData?.SourceLanguage ?? sourceLanguage,
            translationData?.TargetLanguage ?? targetLanguage,
            null);
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        string text,
        string segmentId,
        string translationJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Source text cannot be empty", nameof(text));
        if (!File.Exists(translationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");

        var script = @"
import sys, json

try:
    from googletrans import Translator
except ImportError:
    import subprocess
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'googletrans==4.0.0rc1'])
    from googletrans import Translator

text        = sys.argv[1]
source_lang = sys.argv[2] if len(sys.argv) > 2 else 'es'
target_lang = sys.argv[3] if len(sys.argv) > 3 else 'en'
json_path   = sys.argv[4]
seg_id      = sys.argv[5] if len(sys.argv) > 5 else ''

translator    = Translator()
translated    = translator.translate(text, src=source_lang, dest=target_lang)
translated_text = translated.text if translated else ''

try:
    with open(json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)
except Exception:
    data = {'segments': []}

for seg in data.get('segments', []):
    if seg.get('id') == seg_id:
        seg['translatedText'] = translated_text
        break

with open(json_path, 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)

print(f'Single segment translated: {seg_id}')
";

        Log.Info($"Starting single segment translation: {text.Substring(0, Math.Min(30, text.Length))}...");

        var result = await RunPythonScriptAsync(
            script,
            $"\"{text}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{translationJsonPath}\" \"{segmentId}\"",
            "translate_seg",
            cancellationToken);
        ThrowIfFailed(result, "Single segment translation");

        Log.Info($"Single segment translation completed: {translationJsonPath}");

        var jsonContent = await File.ReadAllTextAsync(translationJsonPath);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var translationData = JsonSerializer.Deserialize<TranslationJson>(jsonContent, jsonOptions);

        var segments = new List<TranslatedSegment>();
        if (translationData?.Segments != null)
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
            translationData?.SourceLanguage ?? sourceLanguage,
            translationData?.TargetLanguage ?? targetLanguage,
            null);
    }

    private class TranslationJson
    {
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public List<SegmentJson>? Segments { get; set; }
    }

    private class SegmentJson
    {
        public string? Id { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public string? TranslatedText { get; set; }
    }
}

public sealed record TranslationResult(
    bool Success,
    IReadOnlyList<TranslatedSegment> Segments,
    string SourceLanguage,
    string TargetLanguage,
    string? ErrorMessage);

public sealed record TranslatedSegment(
    double StartSeconds,
    double EndSeconds,
    string Text,
    string TranslatedText);
