using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class NllbTranslationService : ITranslationService
{
    private readonly AppLog _log;
    private readonly string _model;
    private readonly string _pythonPath;

    public NllbTranslationService(AppLog log, string model)
    {
        _log = log;
        _model = model;
        _pythonPath = DependencyLocator.FindPython()
            ?? throw new InvalidOperationException(
                "Python not found. NLLB-200 translation requires Python with the transformers package installed.");
    }

    private const string NllbScript = @"
import sys, json
from transformers import pipeline as hf_pipeline

FLORES = {
    'en':'eng_Latn','es':'spa_Latn','fr':'fra_Latn','de':'deu_Latn',
    'it':'ita_Latn','pt':'por_Latn','ru':'rus_Cyrl','zh':'zho_Hans',
    'ja':'jpn_Jpan','ko':'kor_Hang','ar':'arb_Arab','hi':'hin_Deva',
    'nl':'nld_Latn','pl':'pol_Latn','sv':'swe_Latn','tr':'tur_Latn',
}

input_path  = sys.argv[1]
output_path = sys.argv[2]
src_lang    = sys.argv[3]
tgt_lang    = sys.argv[4]
model_name  = sys.argv[5]

src_flores = FLORES.get(src_lang, src_lang)
tgt_flores = FLORES.get(tgt_lang, tgt_lang)
model_id   = f'facebook/{model_name}'

pipe = hf_pipeline('translation', model=model_id,
                   src_lang=src_flores, tgt_lang=tgt_flores, max_length=512)

with open(input_path, encoding='utf-8') as f:
    data = json.load(f)

results = []
for seg in data.get('segments', []):
    text   = seg.get('text', '')
    xlated = pipe(text)[0]['translation_text'] if text.strip() else ''
    results.append({
        'id':             f""segment_{seg['start']}"",
        'start':          seg['start'],
        'end':            seg['end'],
        'text':           text,
        'translatedText': xlated,
    })

with open(output_path, 'w', encoding='utf-8') as f:
    json.dump({'sourceLanguage': src_lang, 'targetLanguage': tgt_lang,
               'segments': results}, f, ensure_ascii=False, indent=2)
print('NLLB translation complete')
";

    private const string NllbSegmentScript = @"
import sys, json
from transformers import pipeline as hf_pipeline

FLORES = {
    'en':'eng_Latn','es':'spa_Latn','fr':'fra_Latn','de':'deu_Latn',
    'it':'ita_Latn','pt':'por_Latn','ru':'rus_Cyrl','zh':'zho_Hans',
    'ja':'jpn_Jpan','ko':'kor_Hang','ar':'arb_Arab','hi':'hin_Deva',
    'nl':'nld_Latn','pl':'pol_Latn','sv':'swe_Latn','tr':'tur_Latn',
}

text       = sys.argv[1]
src_lang   = sys.argv[2]
tgt_lang   = sys.argv[3]
json_path  = sys.argv[4]
seg_id     = sys.argv[5]
model_name = sys.argv[6]

src_flores = FLORES.get(src_lang, src_lang)
tgt_flores = FLORES.get(tgt_lang, tgt_lang)
model_id   = f'facebook/{model_name}'

pipe   = hf_pipeline('translation', model=model_id,
                     src_lang=src_flores, tgt_lang=tgt_flores, max_length=512)
xlated = pipe(text)[0]['translation_text'] if text.strip() else ''

try:
    with open(json_path, encoding='utf-8') as f:
        data = json.load(f)
except Exception:
    data = {'segments': []}

for seg in data.get('segments', []):
    if seg.get('id') == seg_id:
        seg['translatedText'] = xlated
        break

with open(json_path, 'w', encoding='utf-8') as f:
    json.dump(data, f, ensure_ascii=False, indent=2)
print(f'NLLB single segment translated: {seg_id}')
";

    public async Task<TranslationResult> TranslateAsync(
        string transcriptJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage)
    {
        if (!File.Exists(transcriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {transcriptJsonPath}");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"nllb_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, NllbScript);

        try
        {
            _log.Info($"Starting NLLB-200 translation ({_model}): {transcriptJsonPath} ({sourceLanguage} -> {targetLanguage})");

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonPath,
                Arguments              = $"\"{scriptPath}\" \"{transcriptJsonPath}\" \"{outputJsonPath}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{_model}\"",
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start NLLB translation process.");
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"NLLB translation failed (exit {proc.ExitCode})", new Exception(stderr));
                throw new InvalidOperationException($"NLLB translation failed: {stderr}");
            }

            _log.Info($"NLLB translation completed: {outputJsonPath}");

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var translationData = JsonSerializer.Deserialize<TranslationJsonHelper>(
                await File.ReadAllTextAsync(outputJsonPath), jsonOptions);

            var segments = new List<TranslatedSegment>();
            foreach (var seg in translationData?.Segments ?? [])
                segments.Add(new TranslatedSegment(seg.Start, seg.End, seg.Text ?? "", seg.TranslatedText ?? ""));

            return new TranslationResult(
                true, segments,
                translationData?.SourceLanguage ?? sourceLanguage,
                translationData?.TargetLanguage ?? targetLanguage,
                null);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    public async Task<TranslationResult> TranslateSingleSegmentAsync(
        string text,
        string segmentId,
        string translationJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Source text cannot be empty", nameof(text));
        if (!File.Exists(translationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"nllb_seg_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, NllbSegmentScript);

        try
        {
            _log.Info($"Starting NLLB-200 single segment translation ({_model}): {text[..Math.Min(30, text.Length)]}...");

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonPath,
                Arguments              = $"\"{scriptPath}\" \"{text}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{translationJsonPath}\" \"{segmentId}\" \"{_model}\"",
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start NLLB segment translation process.");
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"NLLB segment translation failed (exit {proc.ExitCode})", new Exception(stderr));
                throw new InvalidOperationException($"NLLB segment translation failed: {stderr}");
            }

            _log.Info($"NLLB single segment translation completed: {segmentId}");

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var translationData = JsonSerializer.Deserialize<TranslationJsonHelper>(
                await File.ReadAllTextAsync(translationJsonPath), jsonOptions);

            var segments = new List<TranslatedSegment>();
            foreach (var seg in translationData?.Segments ?? [])
                segments.Add(new TranslatedSegment(seg.Start, seg.End, seg.Text ?? "", seg.TranslatedText ?? ""));

            return new TranslationResult(
                true, segments,
                translationData?.SourceLanguage ?? sourceLanguage,
                translationData?.TargetLanguage ?? targetLanguage,
                null);
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
    }

    // JSON deserialization helpers — internal so TranslationService can share them
    internal class TranslationJsonHelper
    {
        public string? SourceLanguage { get; set; }
        public string? TargetLanguage { get; set; }
        public List<SegmentJsonHelper>? Segments { get; set; }
    }

    internal class SegmentJsonHelper
    {
        public string? Id { get; set; }
        public double Start { get; set; }
        public double End { get; set; }
        public string? Text { get; set; }
        public string? TranslatedText { get; set; }
    }
}
