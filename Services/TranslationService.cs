using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Babel.Player.Services;

public sealed class TranslationService
{
    private readonly AppLog _log;
    private readonly string _pythonPath;

    public TranslationService(AppLog log)
    {
        _log = log;
        _pythonPath = FindPythonPath();
    }

    private static string FindPythonPath()
    {
        var appDir = AppContext.BaseDirectory;

        var possiblePaths = new[]
        {
            Path.Combine(appDir, "python.exe"),
            Path.Combine(appDir, "python", "python.exe"),
            "python",
            "python3",
        };

        foreach (var path in possiblePaths)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit(5000);
                    if (proc.ExitCode == 0)
                    {
                        return path;
                    }
                }
            }
            catch
            {
            }
        }

        throw new InvalidOperationException(
            "Python not found. Expected bundled python next to the app or python on PATH.");
    }

    public async Task<TranslationResult> TranslateAsync(
        string transcriptJsonPath, 
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage)
    {
        if (!File.Exists(transcriptJsonPath))
        {
            throw new FileNotFoundException($"Transcript file not found: {transcriptJsonPath}");
        }

        var script = @"
import sys
import json
import asyncio
from googletrans import Translator

async def translate():
    translator = Translator()
    
    with open(sys.argv[1], 'r', encoding='utf-8') as f:
        data = json.load(f)
    
    source_lang = sys.argv[3] if len(sys.argv) > 3 else 'es'
    target_lang = sys.argv[4] if len(sys.argv) > 4 else 'en'
    
    result = {
        'sourceLanguage': source_lang,
        'targetLanguage': target_lang,
        'segments': []
    }
    
    if 'segments' in data:
        for idx, seg in enumerate(data['segments']):
            text = seg.get('text', '')
            translated_text = ''
            if text:
                try:
                    translated = await translator.translate(text, src=source_lang, dest=target_lang)
                    translated_text = translated.text if translated else ''
                except Exception as e:
                    print(f'Error translating: {e}', file=sys.stderr)
            
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

asyncio.run(translate())
";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"translate_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            _log.Info($"Starting translation: {transcriptJsonPath} ({sourceLanguage} -> {targetLanguage})");

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{scriptPath}\" \"{transcriptJsonPath}\" \"{outputJsonPath}\" \"{sourceLanguage}\" \"{targetLanguage}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException("Failed to start translation process.");
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"Translation failed with exit code {proc.ExitCode}", new Exception(stderr));
                throw new InvalidOperationException($"Translation failed: {stderr}");
            }

            _log.Info($"Translation completed: {outputJsonPath}");

            var jsonContent = await File.ReadAllTextAsync(outputJsonPath);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var translationData = JsonSerializer.Deserialize<TranslationJson>(jsonContent, jsonOptions);

            var segments = new List<TranslatedSegment>();
            if (translationData?.Segments != null)
            {
                _log.Info($"Translation JSON parsed: {translationData.Segments.Count} segments found");
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
                _log.Warning("Translation JSON parsing failed or no segments found");
            }

            _log.Info($"TranslationResult created with {segments.Count} segments");
            return new TranslationResult(
                true,
                segments,
                translationData?.SourceLanguage ?? sourceLanguage,
                translationData?.TargetLanguage ?? targetLanguage,
                null);
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
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
        {
            throw new ArgumentException("Source text cannot be empty", nameof(text));
        }

        if (!File.Exists(translationJsonPath))
        {
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");
        }

        var script = @"
import sys
import json
import asyncio
from googletrans import Translator

async def translate():
    text = sys.argv[1]
    source_lang = sys.argv[2] if len(sys.argv) > 2 else 'es'
    target_lang = sys.argv[3] if len(sys.argv) > 3 else 'en'
    
    translator = Translator()
    
    translated = await translator.translate(text, src=source_lang, dest=target_lang)
    translated_text = translated.text if translated else ''
    
    # Read existing translation to preserve structure
    try:
        with open(sys.argv[4], 'r', encoding='utf-8') as f:
            data = json.load(f)
    except:
        data = {'segments': []}
    
    # Update the specific segment
    seg_id = sys.argv[5] if len(sys.argv) > 5 else ''
    if 'segments' in data and seg_id:
        for seg in data['segments']:
            if seg.get('id') == seg_id:
                seg['translatedText'] = translated_text
                break
    
    output_path = sys.argv[4]
    with open(output_path, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    
    print(f'Single segment translated: {seg_id}')

asyncio.run(translate())
";

        var scriptPath = Path.Combine(Path.GetTempPath(), $"translate_seg_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, script);

        try
        {
            _log.Info($"Starting single segment translation: {text.Substring(0, Math.Min(30, text.Length))}...");

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = $"\"{scriptPath}\" \"{text}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{translationJsonPath}\" \"{segmentId}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException("Failed to start translation process.");
            }

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();

            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"Single segment translation failed with exit code {proc.ExitCode}", new Exception(stderr));
                throw new InvalidOperationException($"Single segment translation failed: {stderr}");
            }

            _log.Info($"Single segment translation completed: {translationJsonPath}");

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
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    // ── NLLB-200 (local inference, no internet required) ──────────────────────

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

    public async Task<TranslationResult> TranslateWithNllbAsync(
        string transcriptJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage,
        string model)
    {
        if (!File.Exists(transcriptJsonPath))
            throw new FileNotFoundException($"Transcript file not found: {transcriptJsonPath}");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"nllb_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, NllbScript);

        try
        {
            _log.Info($"Starting NLLB-200 translation ({model}): {transcriptJsonPath} ({sourceLanguage} -> {targetLanguage})");

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonPath,
                Arguments              = $"\"{scriptPath}\" \"{transcriptJsonPath}\" \"{outputJsonPath}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{model}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start NLLB translation process.");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"NLLB translation failed (exit {proc.ExitCode})", new Exception(stderr));
                throw new InvalidOperationException($"NLLB translation failed: {stderr}");
            }

            _log.Info($"NLLB translation completed: {outputJsonPath}");

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var translationData = JsonSerializer.Deserialize<TranslationJson>(
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

    public async Task<TranslationResult> TranslateSingleSegmentWithNllbAsync(
        string text,
        string segmentId,
        string translationJsonPath,
        string outputJsonPath,
        string sourceLanguage,
        string targetLanguage,
        string model)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Source text cannot be empty", nameof(text));
        if (!File.Exists(translationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {translationJsonPath}");

        var scriptPath = Path.Combine(Path.GetTempPath(), $"nllb_seg_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(scriptPath, NllbSegmentScript);

        try
        {
            _log.Info($"Starting NLLB-200 single segment translation ({model}): {text[..Math.Min(30, text.Length)]}...");

            var psi = new ProcessStartInfo
            {
                FileName               = _pythonPath,
                Arguments              = $"\"{scriptPath}\" \"{text}\" \"{sourceLanguage}\" \"{targetLanguage}\" \"{translationJsonPath}\" \"{segmentId}\" \"{model}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start NLLB segment translation process.");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                _log.Error($"NLLB segment translation failed (exit {proc.ExitCode})", new Exception(stderr));
                throw new InvalidOperationException($"NLLB segment translation failed: {stderr}");
            }

            _log.Info($"NLLB single segment translation completed: {segmentId}");

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var translationData = JsonSerializer.Deserialize<TranslationJson>(
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
