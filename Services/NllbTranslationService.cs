using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class NllbTranslationService : PythonSubprocessServiceBase, ITranslationService
{
    private readonly string _model;

    public NllbTranslationService(AppLog log, string model) : base(log)
    {
        _model = model;
    }

    private const string NllbScript = @"
import sys, json

try:
    import torch
    from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
except ImportError:
    import subprocess
    print('Installing torch and transformers (large download, first run only)...')
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'torch', 'transformers'])
    import torch
    from transformers import AutoTokenizer, AutoModelForSeq2SeqLM

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

device = 'cuda' if torch.cuda.is_available() else 'cpu'
tokenizer = AutoTokenizer.from_pretrained(model_id)
model     = AutoModelForSeq2SeqLM.from_pretrained(model_id).to(device)

def translate_text(text):
    if not text.strip():
        return ''
    inputs = tokenizer(text, return_tensors='pt', padding=True, truncation=True, max_length=512).to(device)
    tgt_id = tokenizer.lang_code_to_id[tgt_flores]
    with torch.no_grad():
        tokens = model.generate(**inputs, forced_bos_token_id=tgt_id, max_length=512)
    return tokenizer.batch_decode(tokens, skip_special_tokens=True)[0]

with open(input_path, encoding='utf-8') as f:
    data = json.load(f)

results = []
for seg in data.get('segments', []):
    text   = seg.get('text', '')
    xlated = translate_text(text)
    results.append({
        'id':             f'segment_{seg[""start""]}',
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

try:
    import torch
    from transformers import AutoTokenizer, AutoModelForSeq2SeqLM
except ImportError:
    import subprocess
    print('Installing torch and transformers (large download, first run only)...')
    subprocess.check_call([sys.executable, '-m', 'pip', 'install', 'torch', 'transformers'])
    import torch
    from transformers import AutoTokenizer, AutoModelForSeq2SeqLM

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

device    = 'cuda' if torch.cuda.is_available() else 'cpu'
tokenizer = AutoTokenizer.from_pretrained(model_id)
model     = AutoModelForSeq2SeqLM.from_pretrained(model_id).to(device)

if text.strip():
    inputs = tokenizer(text, return_tensors='pt', padding=True, truncation=True, max_length=512).to(device)
    tgt_id = tokenizer.lang_code_to_id[tgt_flores]
    with torch.no_grad():
        tokens = model.generate(**inputs, forced_bos_token_id=tgt_id, max_length=512)
    xlated = tokenizer.batch_decode(tokens, skip_special_tokens=True)[0]
else:
    xlated = ''

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
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(transcriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {transcriptJsonPath}");

        Log.Info($"Starting NLLB-200 translation ({_model}): {transcriptJsonPath} ({sourceLanguage} -> {targetLanguage})");

        var result = await RunPythonScriptAsync(
            NllbScript,
            $"\"{transcriptJsonPath}\" \"{outputJsonPath}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{_model}\"",
            "nllb",
            cancellationToken);
        ThrowIfFailed(result, "NLLB translation");

        Log.Info($"NLLB translation completed: {outputJsonPath}");

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

        Log.Info($"Starting NLLB-200 single segment translation ({_model}): {text[..Math.Min(30, text.Length)]}...");

        var result = await RunPythonScriptAsync(
            NllbSegmentScript,
            $"\"{text}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{translationJsonPath}\" \"{segmentId}\" \"{_model}\"",
            "nllb_seg",
            cancellationToken);
        ThrowIfFailed(result, "NLLB segment translation");

        Log.Info($"NLLB single segment translation completed: {segmentId}");

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
