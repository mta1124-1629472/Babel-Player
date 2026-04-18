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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;

namespace Babel.Player.Tools.LocaleGenerator;

internal static class Program
{
    private const int DeepLTranslateBatchSize = 50;

    // Canonical set of Babel Player UI target languages (lowercase ISO 639-1).
    // Must match the NLLB catalog minus "en".
    private static readonly string[] DefaultTargets =
    [
        "ar", "de", "es", "fr", "hi", "it", "ja", "ko",
        "nl", "pl", "pt", "ru", "sv", "tr", "zh"
    ];

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

        var sourcePath = ResolveSourcePath(options.SourcePath);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Source .resx not found: {sourcePath}");
            return 2;
        }

        var outputDir = Path.GetFullPath(options.OutputDirectory);
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

        var translations = new List<DeepLTranslationItem>(entries.Count);
        foreach (var batch in entries.Chunk(DeepLTranslateBatchSize))
        {
            var batchTexts = batch.Select(entry => entry.Value).ToList();
            var batchTranslations = await client.TranslateTextsAsync(
                    batchTexts,
                    deepLTargetCode,
                    sourceLanguage: "EN",
                    cancellationToken)
                .ConfigureAwait(false);

            if (batchTranslations.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"Expected {batch.Length} translations in batch, got {batchTranslations.Count}.");
            }

            translations.AddRange(batchTranslations);
        }

        if (translations.Count != entries.Count)
        {
            throw new InvalidOperationException(
                $"Expected {entries.Count} translations, got {translations.Count}.");
        }

        var outputPath = Path.Combine(outputDir, $"Strings.{lang}.resx");
        WriteResx(outputPath, entries.Select((kv, i) => new KeyValuePair<string, string>(
            kv.Key,
            string.IsNullOrEmpty(translations[i].Text) ? kv.Value : translations[i].Text)));

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

    private static string ResolveSourcePath(string configured)
    {
        if (File.Exists(configured))
            return Path.GetFullPath(configured);

        // Walk up from the running binary to find the repo root that holds
        // Resources/Strings.resx so the tool works from any working directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return Path.GetFullPath(configured);
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
        string[] targets = DefaultTargets;
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
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
