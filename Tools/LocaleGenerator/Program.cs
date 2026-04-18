// Tools/LocaleGenerator — DeepL-driven regeneration of satellite
// Resources/Strings.<lang>.resx files.
//
// Usage:
//   dotnet run --project Tools/LocaleGenerator -- --api-key <DEEPL_KEY>
//
// Reads the English base Resources/Strings.resx produced by
// scripts/build_strings_resx.py, batches every value through
// DeepLApiClient.TranslateTextsAsync for each of the 15 non-English
// UI languages supported by Babel Player, and writes the results to
// Resources/Strings.<lang>.resx using the same header schema as the
// base file so ResourceManager can load them as satellite resources.
//
// Languages where DeepL has no API coverage (none of the current
// 15 today, but kept for forward compatibility) are skipped with a
// clear message and listed in the final summary so they can be
// regenerated via a different provider or reviewed manually.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;

namespace Babel.Player.Tools.LocaleGenerator;

internal static class Program
{
    /// <summary>DeepL translate API accepts at most 50 <c>text</c> entries per request.</summary>
    private const int DeepLMaxTextsPerRequest = 50;

    // Protected overrides for product branding and UI terminology that DeepL
    // routinely mistranslates in short command labels.
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ProtectedOverrides =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ar"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Menu_File_ForceClose"] = "إغلاق إجباري",
                ["Language_nl"] = "الهولندية",
                ["Wizard_Button_Play"] = "تشغيل",
                ["Wizard_Tooltip_JumpToSegments"] = "انقر فوق الطابع الزمني للانتقال إلى تلك النقطة في الفيديو، ثم استخدم \"استخدام رأس التشغيل\".",
                ["Settings_Nav_General"] = "عام",
                ["Settings_Group_DockerGpu"] = "خدمة Docker لوحدة معالجة الرسومات",
                ["Section_Transcription"] = "التفريغ النصي",
                ["Label_Compute"] = "تنفيذ",
                ["Crash_Button_OpenLogFolder"] = "فتح مجلد السجل",
            },
            ["de"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Status_Stale"] = "veraltet",
                ["Tooltip_Volume"] = "Lautstärke",
                ["Settings_Placeholder_TargetPeak"] = "Auto oder Nits",
                ["Wizard_Button_Finish"] = "Fertigstellen",
                ["Section_Transcription"] = "TRANSKRIPTION",
                ["Section_Translation"] = "ÜBERSETZUNG",
                ["Common_Clear"] = "Leeren",
                ["Common_Apply"] = "Anwenden",
                ["Window_Title_Main"] = "Babel Player",
            },
            ["es"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Common_Clear"] = "Limpiar",
                ["Common_Ok"] = "Aceptar",
                ["Common_Apply"] = "Aplicar",
                ["Common_Browse"] = "Examinar",
                ["Common_Restart"] = "Reiniciar",
                ["Label_Compute"] = "Calcular",
                ["Option_Off"] = "Desactivado",
                ["Window_Title_Main"] = "Babel Player",
                ["Settings_About_AppName"] = "Babel Player",
            },
            ["fr"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Common_Save"] = "Enregistrer",
                ["Common_Clear"] = "Effacer",
                ["Common_Restart"] = "Redémarrer",
                ["Status_NeedsDownload"] = "⬇ Téléchargement requis",
                ["Wizard_Button_Finish"] = "Terminer",
                ["Language_ru"] = "Russe",
                ["Window_Title_Main"] = "Babel Player",
                ["Button_RunPipeline"] = "Exécuter le pipeline",
            },
            ["hi"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Settings_Label_WorkersManual"] = "मैनुअल कर्मचारियों की संख्या:",
            },
            ["it"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Common_Save"] = "Salva",
                ["Common_Clear"] = "Cancella",
                ["Section_Pipeline"] = "PIPELINE",
                ["Label_TargetSubDub"] = "Sottotitoli/Doppiaggio di destinazione",
                ["Button_RunPipeline"] = "Esegui pipeline",
                ["Button_CancelPipeline"] = "Annulla pipeline",
                ["Wizard_Button_Play"] = "Riproduci",
                ["Wizard_Button_Finish"] = "Completa",
            },
            ["ja"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Window_Title_Main"] = "Babel Player",
                ["Settings_About_AppName"] = "Babel Player",
                ["Label_ActiveAsr"] = "使用中のASR:",
                ["Button_Export"] = "エクスポート",
                ["Language_sv"] = "スウェーデン語",
                ["Wizard_Playhead_Clip_Tooltip"] = "オーディオクリップの長さ（3～15秒）",
            },
            ["ko"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Status_Stale"] = "오래됨",
                ["Option_Stretch"] = "늘이기",
                ["Common_Apply"] = "적용",
                ["Section_Transcription"] = "전사",
                ["Section_Diarization"] = "화자 분리 / 화자",
            },
            ["nl"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Common_Save"] = "Opslaan",
                ["Common_Clear"] = "Wissen",
                ["Section_Transcription"] = "TRANSCRIPTIE",
                ["Status_NeedsDownload"] = "⬇ Download vereist",
                ["Wizard_Toggle_NeedsAttention"] = "Vereist aandacht",
            },
            ["pl"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Menu_File_ForceClose"] = "Wymuś zamknięcie",
                ["Label_TargetSubDub"] = "Docelowe napisy/dubbing",
                ["Button_RunPipeline"] = "Uruchom pipeline",
                ["Settings_Label_GpuRenderApi"] = "Interfejs renderowania GPU:",
                ["Settings_Group_GpuNext"] = "Backend GPU-Next",
            },
            ["pt"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Section_Pipeline"] = "PIPELINE",
                ["Status_NeedsDownload"] = "⬇ Download necessário",
                ["Button_RunPipeline"] = "Executar pipeline",
            },
            ["ru"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Window_Title_Main"] = "Babel Player",
                ["Window_Title_Settings"] = "Babel Player - Настройки",
                ["Section_Translation"] = "ПЕРЕВОД",
                ["Section_Dub"] = "ДУБЛЯЖ",
                ["Option_Off"] = "Выкл.",
                ["Settings_Group_Gpu"] = "Графический процессор (GPU)",
                ["Settings_Group_DockerGpu"] = "Служба Docker GPU",
                ["Language_nl"] = "Нидерландский",
                ["Language_pl"] = "Польский",
                ["Language_tr"] = "Турецкий",
            },
            ["sv"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Common_Close"] = "Stäng",
                ["Menu_File_Close"] = "Stäng",
                ["Crash_Button_Close"] = "Stäng",
                ["Window_Title_Main"] = "Babel Player",
                ["Settings_About_AppName"] = "Babel Player",
                ["Section_Pipeline"] = "PIPELINE",
                ["Section_Transcription"] = "TRANSKRIBERING",
                ["Tooltip_RerunTranslation"] = "Kör översättningen igen (välj bara det här steget eller inkludera dubbning)",
            },
            ["tr"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Tooltip_OpenSettings"] = "Ayarları aç",
                ["Language_de"] = "Almanca",
                ["Language_it"] = "İtalyanca",
                ["Language_sv"] = "İsveççe",
                ["Common_Apply"] = "Uygula",
                ["Section_Transcription"] = "TRANSKRİPSİYON",
                ["Wizard_Button_Play"] = "Oynat",
                ["Window_Title_Main"] = "Babel Player",
            },
            ["zh"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Common_Save"] = "保存",
                ["Common_Clear"] = "清空",
                ["Language_de"] = "德语",
                ["Language_ru"] = "俄语",
                ["Button_Export"] = "导出",
                ["Window_Title_Main"] = "Babel Player",
            },
        };

    private static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }

        if (options.PrintHelp)
        {
            PrintUsage();
            return 0;
        }

        var apiKey = options.ApiKey
            ?? Environment.GetEnvironmentVariable("DEEPL_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine(
                "Missing DeepL API key. Pass --api-key <KEY> or set the DEEPL_API_KEY environment variable.");
            return 2;
        }

        var sourcePath = ResolveRepoPath(options.SourcePath);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source .resx not found: {sourcePath}");
            return 2;
        }

        var outputDir = ResolveRepoPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDir);

        var entries = ReadResx(sourcePath);
        Console.WriteLine($"Loaded {entries.Count} keys from {sourcePath}.");
        Console.WriteLine($"Output directory: {outputDir}");
        Console.WriteLine();

        using var client = new DeepLApiClient(apiKey);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var succeeded = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        foreach (var lang in options.Targets)
        {
            if (cts.IsCancellationRequested)
            {
                Console.Error.WriteLine("Cancelled by user.");
                break;
            }

            var normalizedApiCode = NormalizeDeepLTargetCode(lang);
            if (!DeepLTranslationCatalog.IsSupportedApiCode(normalizedApiCode))
            {
                Console.WriteLine(
                    $"[{lang}] SKIP — DeepL does not support '{normalizedApiCode}'. " +
                    "Flag for manual review or regenerate via an NLLB/CTranslate2 pipeline.");
                skipped.Add(lang);
                continue;
            }

            try
            {
                await TranslateOneLanguageAsync(client, entries, lang, normalizedApiCode, outputDir, cts.Token)
                    .ConfigureAwait(false);
                succeeded.Add(lang);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"[{lang}] CANCELLED");
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{lang}] FAIL — {ex.GetType().Name}: {ex.Message}");
                failed.Add(lang);
            }
        }

        Console.WriteLine();
        Console.WriteLine("───── Summary ─────");
        Console.WriteLine($"Translated : {succeeded.Count}  ({string.Join(", ", succeeded)})");
        if (skipped.Count > 0)
            Console.WriteLine($"Skipped    : {skipped.Count}  ({string.Join(", ", skipped)})");
        if (failed.Count > 0)
            Console.WriteLine($"Failed     : {failed.Count}  ({string.Join(", ", failed)})");

        return failed.Count > 0 ? 1 : 0;
    }

    private static async Task TranslateOneLanguageAsync(
        DeepLApiClient client,
        IReadOnlyList<KeyValuePair<string, string>> entries,
        string lang,
        string deepLTargetCode,
        string outputDir,
        CancellationToken cancellationToken)
    {
        Console.Write($"[{lang}] Translating {entries.Count} strings via DeepL ({deepLTargetCode})... ");

        var texts = entries.Select(e => e.Value).ToList();
        var translatedTexts = new List<string>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += DeepLMaxTextsPerRequest)
        {
            var batchSize = Math.Min(DeepLMaxTextsPerRequest, texts.Count - offset);
            var batch = texts.GetRange(offset, batchSize);
            var batchTranslations = await client.TranslateTextsAsync(
                    batch,
                    deepLTargetCode,
                    sourceLanguage: "EN",
                    cancellationToken)
                .ConfigureAwait(false);

            if (batchTranslations.Count != batchSize)
            {
                throw new InvalidOperationException(
                    $"Expected {batchSize} translations for batch at offset {offset}, got {batchTranslations.Count}.");
            }

            foreach (var item in batchTranslations)
                translatedTexts.Add(item.Text);
        }

        if (translatedTexts.Count != entries.Count)
        {
            throw new InvalidOperationException(
                $"Expected {entries.Count} translations, got {translatedTexts.Count}.");
        }

        var localizedEntries = entries.Select((kv, i) => new KeyValuePair<string, string>(
            kv.Key,
            string.IsNullOrEmpty(translatedTexts[i]) ? kv.Value : translatedTexts[i])).ToList();

        ApplyProtectedOverrides(lang, localizedEntries);
        ValidateLocalizedEntries(lang, localizedEntries);

        var outputPath = Path.Combine(outputDir, $"Strings.{lang}.resx");
        WriteResx(outputPath, localizedEntries);

        Console.WriteLine($"wrote {Path.GetFileName(outputPath)}");
    }

    /// <summary>
    /// DeepL expects uppercase ISO codes plus a few regional variants.  For the
    /// 15 canonical Babel target languages, uppercasing the ISO 639-1 code is
    /// enough; we pick PT-PT for "pt" to avoid an implicit PT-BR fallback.
    /// </summary>
    private static string NormalizeDeepLTargetCode(string lang)
    {
        var upper = lang.Trim().ToUpperInvariant();
        return upper switch
        {
            "PT" => "PT-PT",
            _ => upper,
        };
    }

    private static List<KeyValuePair<string, string>> ReadResx(string path)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = doc.Root
            ?? throw new InvalidOperationException($"Missing <root> element in {path}.");

        var result = new List<KeyValuePair<string, string>>();
        foreach (var data in root.Elements("data"))
        {
            var name = data.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
                continue;

            var value = data.Element("value")?.Value ?? string.Empty;
            result.Add(new KeyValuePair<string, string>(name, value));
        }
        return result;
    }

    private static void WriteResx(string path, IEnumerable<KeyValuePair<string, string>> entries)
    {
        var sb = new StringBuilder();
        sb.Append(ResxHeader);
        foreach (var kv in entries)
        {
            sb.Append("  <data name=\"").Append(Escape(kv.Key)).Append("\" xml:space=\"preserve\">\n");
            sb.Append("    <value>").Append(Escape(kv.Value)).Append("</value>\n");
            sb.Append("  </data>\n");
        }
        sb.Append("</root>\n");

        // Match scripts/build_strings_resx.py byte layout: UTF-8 without BOM, LF newlines.
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    /// <summary>Mirrors <c>RESX_HEADER</c> in scripts/build_strings_resx.py.</summary>
    private const string ResxHeader =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<root>\n" +
        "  <!--\n" +
        "    Generated by Tools/LocaleGenerator (DeepL).\n" +
        "    The English base Resources/Strings.resx is maintained by\n" +
        "    scripts/build_strings_resx.py; do not hand-edit this XML.\n" +
        "  -->\n" +
        "  <xsd:schema id=\"root\" xmlns=\"\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:msdata=\"urn:schemas-microsoft-com:xml-msdata\">\n" +
        "    <xsd:import namespace=\"http://www.w3.org/XML/1998/namespace\" />\n" +
        "    <xsd:element name=\"root\" msdata:IsDataSet=\"true\">\n" +
        "      <xsd:complexType>\n" +
        "        <xsd:choice maxOccurs=\"unbounded\">\n" +
        "          <xsd:element name=\"metadata\">\n" +
        "            <xsd:complexType>\n" +
        "              <xsd:sequence>\n" +
        "                <xsd:element name=\"value\" type=\"xsd:string\" minOccurs=\"0\" />\n" +
        "              </xsd:sequence>\n" +
        "              <xsd:attribute name=\"name\" use=\"required\" type=\"xsd:string\" />\n" +
        "              <xsd:attribute name=\"type\" type=\"xsd:string\" />\n" +
        "              <xsd:attribute name=\"mimetype\" type=\"xsd:string\" />\n" +
        "              <xsd:attribute ref=\"xml:space\" />\n" +
        "            </xsd:complexType>\n" +
        "          </xsd:element>\n" +
        "          <xsd:element name=\"assembly\">\n" +
        "            <xsd:complexType>\n" +
        "              <xsd:attribute name=\"alias\" type=\"xsd:string\" />\n" +
        "              <xsd:attribute name=\"name\" type=\"xsd:string\" />\n" +
        "            </xsd:complexType>\n" +
        "          </xsd:element>\n" +
        "          <xsd:element name=\"data\">\n" +
        "            <xsd:complexType>\n" +
        "              <xsd:sequence>\n" +
        "                <xsd:element name=\"value\" type=\"xsd:string\" minOccurs=\"0\" msdata:Ordinal=\"1\" />\n" +
        "                <xsd:element name=\"comment\" type=\"xsd:string\" minOccurs=\"0\" msdata:Ordinal=\"2\" />\n" +
        "              </xsd:sequence>\n" +
        "              <xsd:attribute name=\"name\" type=\"xsd:string\" use=\"required\" msdata:Ordinal=\"1\" />\n" +
        "              <xsd:attribute name=\"type\" type=\"xsd:string\" msdata:Ordinal=\"3\" />\n" +
        "              <xsd:attribute name=\"mimetype\" type=\"xsd:string\" msdata:Ordinal=\"4\" />\n" +
        "              <xsd:attribute ref=\"xml:space\" />\n" +
        "            </xsd:complexType>\n" +
        "          </xsd:element>\n" +
        "          <xsd:element name=\"resheader\">\n" +
        "            <xsd:complexType>\n" +
        "              <xsd:sequence>\n" +
        "                <xsd:element name=\"value\" type=\"xsd:string\" minOccurs=\"0\" msdata:Ordinal=\"1\" />\n" +
        "              </xsd:sequence>\n" +
        "              <xsd:attribute name=\"name\" type=\"xsd:string\" use=\"required\" />\n" +
        "            </xsd:complexType>\n" +
        "          </xsd:element>\n" +
        "        </xsd:choice>\n" +
        "      </xsd:complexType>\n" +
        "    </xsd:element>\n" +
        "  </xsd:schema>\n" +
        "  <resheader name=\"resmimetype\">\n" +
        "    <value>text/microsoft-resx</value>\n" +
        "  </resheader>\n" +
        "  <resheader name=\"version\">\n" +
        "    <value>2.0</value>\n" +
        "  </resheader>\n" +
        "  <resheader name=\"reader\">\n" +
        "    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>\n" +
        "  </resheader>\n" +
        "  <resheader name=\"writer\">\n" +
        "    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>\n" +
        "  </resheader>\n";

    private static void ApplyProtectedOverrides(string lang, List<KeyValuePair<string, string>> entries)
    {
        if (!ProtectedOverrides.TryGetValue(lang, out var overrides))
            return;

        var indexByKey = entries
            .Select((entry, index) => (entry.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);

        foreach (var pair in overrides)
        {
            if (!indexByKey.TryGetValue(pair.Key, out var index))
                throw new InvalidOperationException($"Protected override key '{pair.Key}' is missing from the base Strings.resx.");

            entries[index] = new KeyValuePair<string, string>(pair.Key, pair.Value);
        }
    }

    private static void ValidateLocalizedEntries(string lang, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        var failures = new List<string>();

        foreach (var entry in entries)
        {
            if (HasUnbalancedUiQuotes(entry.Value))
                failures.Add($"{entry.Key}: unmatched UI quotes");
            if (HasDuplicatedAdjacentWords(entry.Value))
                failures.Add($"{entry.Key}: duplicated adjacent words");
        }

        if (ProtectedOverrides.TryGetValue(lang, out var overrides))
        {
            var entryMap = entries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            foreach (var pair in overrides)
            {
                if (!entryMap.TryGetValue(pair.Key, out var actual) ||
                    !string.Equals(actual, pair.Value, StringComparison.Ordinal))
                {
                    failures.Add($"{pair.Key}: expected protected override '{pair.Value}'");
                }
            }
        }

        if (failures.Count == 0)
            return;

        var preview = string.Join("; ", failures.Take(12));
        if (failures.Count > 12)
            preview += $"; ... ({failures.Count - 12} more)";

        throw new InvalidOperationException($"[{lang}] generated .resx failed validation: {preview}");
    }

    private static bool HasUnbalancedUiQuotes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Count(text, '"') % 2 != 0
               || Count(text, '“') != Count(text, '”')
               || Count(text, '«') != Count(text, '»');
    }

    private static int Count(string text, char ch) => text.Count(c => c == ch);

    private static bool HasDuplicatedAdjacentWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string? previous = null;
        foreach (var token in Regex.Split(text, @"\s+"))
        {
            var normalized = NormalizeToken(token);
            if (string.IsNullOrEmpty(normalized))
                continue;

            if (previous is not null && string.Equals(previous, normalized, StringComparison.OrdinalIgnoreCase))
                return true;

            previous = normalized;
        }

        return false;
    }

    private static string NormalizeToken(string token) =>
        token.Trim().Trim('"', '\'', '“', '”', '«', '»', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '-', '–', '—');

    private static string ResolveRepoPath(string configured)
    {
        if (Path.IsPathRooted(configured))
            return Path.GetFullPath(configured);

        var repoRoot = FindRepoRoot();
        return repoRoot is null
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(repoRoot, configured));
    }

    private sealed record Options(
        string? ApiKey,
        string SourcePath,
        string OutputDirectory,
        string[] Targets,
        bool PrintHelp);

    private static Options ParseArgs(string[] args)
    {
        string? apiKey = null;
        string sourcePath = Path.Combine("Resources", "Strings.resx");
        string outputDirectory = "Resources";
        string[] targets = GetDefaultTargets();
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--api-key":
                    apiKey = RequireValue(args, ref i, arg);
                    break;
                case "--source":
                    sourcePath = RequireValue(args, ref i, arg);
                    break;
                case "--out":
                case "--output":
                    outputDirectory = RequireValue(args, ref i, arg);
                    break;
                case "--languages":
                    targets = RequireValue(args, ref i, arg)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(CanonicalizeTargetLanguage)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (targets.Length == 0)
                        throw new ArgumentException("No valid --languages values were provided.");
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return new Options(apiKey, sourcePath, outputDirectory, targets, help);
    }

    private static string RequireValue(string[] args, ref int index, string arg)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {arg}.");
        index++;
        return args[index];
    }

    private static string[] GetDefaultTargets() =>
        SupportedUiLanguageCatalog.IsoCodes
            .Where(code => !string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

    private static string CanonicalizeTargetLanguage(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("LocaleGenerator target languages cannot be blank.");

        var trimmed = token.Trim().Replace('_', '-');
        string canonical;
        try
        {
            canonical = CultureInfo.GetCultureInfo(trimmed).TwoLetterISOLanguageName.ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            var separator = trimmed.IndexOf('-');
            canonical = (separator > 0 ? trimmed[..separator] : trimmed).ToLowerInvariant();
        }

        if (string.Equals(canonical, "en", StringComparison.Ordinal))
            throw new ArgumentException("English is the base resource. Omit 'en' from --languages.");

        if (!SupportedUiLanguageCatalog.IsSupported(canonical))
        {
            throw new ArgumentException(
                $"Unsupported UI language '{token}'. Supported values: {string.Join(", ", GetDefaultTargets())}");
        }

        return canonical;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Babel-Player.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: dotnet run --project Tools/LocaleGenerator -- \\\n" +
            "           [--api-key <DEEPL_KEY>]           DeepL auth key (or DEEPL_API_KEY env var)\n" +
            "           [--source Resources/Strings.resx] English base .resx\n" +
            "           [--out Resources]                 Directory to write Strings.<lang>.resx\n" +
            "           [--languages ar,de,fr,...]        Comma-separated ISO 639-1 target list");
    }
}
