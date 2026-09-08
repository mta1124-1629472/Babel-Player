using System;
using System.IO;

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
        bool isMultilingual = rootDirectory.Contains("multilingual", StringComparison.OrdinalIgnoreCase);
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
