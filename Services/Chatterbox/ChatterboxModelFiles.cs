using System;
using System.IO;
using System.Text.Json;

namespace Babel.Player.Services.Chatterbox;

internal sealed record ChatterboxModelFiles(
    string RootDirectory,
    string SpeechEncoderPath,
    string EmbedTokensPath,
    string LanguageModelPath,
    string ConditionalDecoderPath,
    string TokenizerPath,
    bool IsTurbo,
    bool IsMultilingual)
{
    public static ChatterboxModelFiles Resolve(string modelDir, string? variant = null)
    {
        var rootDirectory = Path.GetFullPath(modelDir);
        var languageModelPath = ResolveGraphPath(rootDirectory, "language_model", variant);
        var speechEncoderPath = ResolveGraphPath(rootDirectory, "speech_encoder", variant);
        var embedTokensPath = ResolveGraphPath(rootDirectory, "embed_tokens", variant);
        var conditionalDecoderPath = ResolveGraphPath(rootDirectory, "conditional_decoder", variant);
        var tokenizerPath = Path.Combine(rootDirectory, "tokenizer.json");
        foreach (var path in new[] { languageModelPath, speechEncoderPath, embedTokensPath, conditionalDecoderPath, tokenizerPath })
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Chatterbox voice cloning requires the full ONNX package.", path);
        }

        bool isTurbo = rootDirectory.Contains("turbo", StringComparison.OrdinalIgnoreCase);
        bool isMultilingual = DetectMultilingualFromTokenizer(tokenizerPath, rootDirectory);
        return new ChatterboxModelFiles(
            rootDirectory,
            speechEncoderPath,
            embedTokensPath,
            languageModelPath,
            conditionalDecoderPath,
            tokenizerPath,
            isTurbo,
            isMultilingual);
    }

    private static bool DetectMultilingualFromTokenizer(string tokenizerPath, string rootDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(tokenizerPath));
            if (document.RootElement.TryGetProperty("model", out var model) &&
                model.TryGetProperty("vocab", out var vocab))
            {
                foreach (var entry in vocab.EnumerateObject())
                {
                    var name = entry.Name;
                    if (name.Length == 4 && name[0] == '[' && name[3] == ']' &&
                        char.IsLower(name[1]) && char.IsLower(name[2]))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        catch
        {
        }

        return rootDirectory.Contains("multilingual", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveGraphPath(string modelRootPath, string graphName, string? variant)
    {
        var onnxDirectory = Path.Combine(modelRootPath, "onnx");
        if (!string.IsNullOrWhiteSpace(variant) &&
            !variant.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            var variantPath = Path.Combine(onnxDirectory, $"{graphName}_{variant}.onnx");
            if (File.Exists(variantPath))
                return variantPath;
        }

        return Path.Combine(onnxDirectory, $"{graphName}.onnx");
    }
}
