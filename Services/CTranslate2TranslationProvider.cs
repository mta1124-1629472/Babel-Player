using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public class CTranslate2TranslationProvider : PythonSubprocessServiceBase, ITranslationProvider
{
    private readonly string _model;

    public CTranslate2TranslationProvider(AppLog log, string model) : base(log)
    {
        _model = string.IsNullOrWhiteSpace(model) ? "nllb-200-distilled-600M" : model;
    }

    private const string TranslateScriptTemplate = @"
import json
import os
import sys

import ctranslate2
from transformers import AutoTokenizer

FLORES = __FLORES_DICT_BODY__

def translate_text(translator, tokenizer, target_prefix, text):
    if not text.strip():
        return ''

    source_tokens = tokenizer.convert_ids_to_tokens(tokenizer.encode(text))
    results = translator.translate_batch([source_tokens], target_prefix=[target_prefix], beam_size=4)
    translated_tokens = results[0].hypotheses[0][1:]
    return tokenizer.decode(tokenizer.convert_tokens_to_ids(translated_tokens), skip_special_tokens=True)

input_path = sys.argv[1]
output_path = sys.argv[2]
src_lang = sys.argv[3]
tgt_lang = sys.argv[4]
model_dir = sys.argv[5]
repo_id = sys.argv[6]

if not os.path.isdir(model_dir):
    print(f'Converted CTranslate2 model not found: {model_dir}', file=sys.stderr)
    sys.exit(1)

src_flores = FLORES.get(src_lang, src_lang)
tgt_flores = FLORES.get(tgt_lang, tgt_lang)
translator = ctranslate2.Translator(model_dir, device='cpu')
tokenizer = AutoTokenizer.from_pretrained(repo_id, src_lang=src_flores, local_files_only=True)
target_prefix = [tgt_flores]

with open(input_path, encoding='utf-8') as f:
    data = json.load(f)

results = []
for seg in data.get('segments', []):
    text = seg.get('text', '')
    translated = translate_text(translator, tokenizer, target_prefix, text) if text else ''
    results.append({
        'id': f'segment_{seg.get(""start"", 0)}',
        'start': seg.get('start', 0),
        'end': seg.get('end', 0),
        'text': text,
        'translatedText': translated,
    })

with open(output_path, 'w', encoding='utf-8') as f:
    json.dump({'sourceLanguage': src_lang, 'targetLanguage': tgt_lang, 'segments': results}, f, ensure_ascii=False, indent=2)

print('CTranslate2 translation complete')
";

    private static readonly string TranslateScript = TranslateScriptTemplate.Replace(
        "__FLORES_DICT_BODY__",
        "{" + NllbLanguageCatalog.BuildPythonDictLiteral() + "}",
        StringComparison.Ordinal);

    private const string TranslateSingleSegmentScriptTemplate = @"
import json
import os
import sys

import ctranslate2
from transformers import AutoTokenizer

FLORES = __FLORES_DICT_BODY__

src_lang = sys.argv[1]
tgt_lang = sys.argv[2]
model_dir = sys.argv[3]
repo_id = sys.argv[4]
text = sys.stdin.read()

if not os.path.isdir(model_dir):
    print(f'Converted CTranslate2 model not found: {model_dir}', file=sys.stderr)
    sys.exit(1)

src_flores = FLORES.get(src_lang, src_lang)
tgt_flores = FLORES.get(tgt_lang, tgt_lang)
translator = ctranslate2.Translator(model_dir, device='cpu')
tokenizer = AutoTokenizer.from_pretrained(repo_id, src_lang=src_flores, local_files_only=True)

if text.strip():
    source_tokens = tokenizer.convert_ids_to_tokens(tokenizer.encode(text))
    results = translator.translate_batch([source_tokens], target_prefix=[[tgt_flores]], beam_size=4)
    translated_tokens = results[0].hypotheses[0][1:]
    translated_text = tokenizer.decode(tokenizer.convert_tokens_to_ids(translated_tokens), skip_special_tokens=True)
else:
    translated_text = ''
print(json.dumps({'translatedText': translated_text, 'sourceLanguage': src_lang, 'targetLanguage': tgt_lang}, ensure_ascii=False))
";

    private static readonly string TranslateSingleSegmentScript = TranslateSingleSegmentScriptTemplate.Replace(
        "__FLORES_DICT_BODY__",
        "{" + NllbLanguageCatalog.BuildPythonDictLiteral() + "}",
        StringComparison.Ordinal);

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

        Log.Debug($"Starting CTranslate2 translation: {request.TranscriptJsonPath} ({request.SourceLanguage} -> {request.TargetLanguage})");

        var result = await RunCTranslate2ScriptAsync(
            TranslateScript,
            [
                request.TranscriptJsonPath,
                request.OutputJsonPath,
                request.SourceLanguage,
                request.TargetLanguage,
                ModelDownloader.GetCTranslate2TranslationModelDir(_model),
                GetTokenizerRepositoryId(_model),
            ],
            "ct2_translate",
            cancellationToken: cancellationToken);
        ThrowIfFailed(result, "CTranslate2 translation");

        var transcriptData = await ArtifactJson.LoadTranscriptAsync(request.TranscriptJsonPath, cancellationToken);
        var translationData = await ArtifactJson.LoadTranslationAsync(request.OutputJsonPath, cancellationToken);
        if (translationData.Segments?.Count != transcriptData.Segments?.Count)
        {
            throw new InvalidOperationException(
                $"CTranslate2 translation artifact segment count mismatch: expected {transcriptData.Segments?.Count ?? 0}, got {translationData.Segments?.Count ?? 0}.");
        }

        return BuildTranslationResult(translationData, request.SourceLanguage, request.TargetLanguage);
    }

    public async Task<SingleSegmentTranslationTextResult> TranslateSingleSegmentTextAsync(
        SingleSegmentTranslationTextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceText))
            throw new ArgumentException("Source text cannot be empty", nameof(request));

        Log.Debug($"Starting CTranslate2 single segment translation: {request.SegmentId}");

        var result = await RunCTranslate2ScriptAsync(
            TranslateSingleSegmentScript,
            [
                request.SourceLanguage,
                request.TargetLanguage,
                ModelDownloader.GetCTranslate2TranslationModelDir(_model),
                GetTokenizerRepositoryId(_model),
            ],
            "ct2_translate_seg",
            standardInput: request.SourceText,
            cancellationToken: cancellationToken);
        ThrowIfFailed(result, "CTranslate2 segment translation");
        using var json = JsonDocument.Parse(result.Stdout);
        return new SingleSegmentTranslationTextResult(
            true,
            json.RootElement.GetProperty("translatedText").GetString() ?? string.Empty,
            json.RootElement.GetProperty("sourceLanguage").GetString() ?? request.SourceLanguage,
            json.RootElement.GetProperty("targetLanguage").GetString() ?? request.TargetLanguage,
            null);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        if (!ModelDownloader.IsCTranslate2TranslationModelDownloaded(_model))
        {
            return new ProviderReadiness(
                false,
                $"CTranslate2 translation model '{_model}' is not prepared yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: $"Prepare CTranslate2 translation model {_model}");
        }

        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!ModelDownloader.IsCTranslate2TranslationModelDownloaded(_model))
        {
            Log.Debug($"CTranslate2 model {_model} requires preparation. Starting conversion...");
            return await new ModelDownloader(Log).DownloadCTranslate2TranslationModelAsync(_model, progress, ct);
        }

        return true;
    }

    protected virtual Task<ScriptResult> RunCTranslate2ScriptAsync(
        string scriptContent,
        IReadOnlyList<string> arguments,
        string scriptPrefix,
        string? standardInput = null,
        CancellationToken cancellationToken = default) =>
        RunPythonScriptAsync(
            scriptContent,
            arguments,
            scriptPrefix,
            standardInput,
            cancellationToken: cancellationToken);

    private static TranslationResult BuildTranslationResult(
        TranslationArtifact translationData,
        string sourceLanguage,
        string targetLanguage)
    {
        var segments = new List<TranslatedSegment>();
        foreach (var seg in translationData.Segments ?? [])
        {
            segments.Add(new TranslatedSegment(
                seg.Start,
                seg.End,
                seg.Text ?? string.Empty,
                seg.TranslatedText ?? string.Empty));
        }

        return new TranslationResult(
            true,
            segments,
            translationData.SourceLanguage ?? sourceLanguage,
            translationData.TargetLanguage ?? targetLanguage,
            null);
    }

    private static string GetTokenizerRepositoryId(string model) => $"facebook/{model}";
}
