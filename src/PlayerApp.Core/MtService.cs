namespace PlayerApp.Core;

public class MtService
{
    private readonly Dictionary<string, string> _phrasebook = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hola"] = "hello",
        ["gracias"] = "thank you",
        ["adios"] = "goodbye",
        ["buenos dias"] = "good morning",
        ["bonjour"] = "hello",
        ["merci"] = "thank you",
        ["au revoir"] = "goodbye",
        ["こんにちは"] = "hello",
        ["ありがとう"] = "thank you",
        ["你好"] = "hello",
        ["谢谢"] = "thank you",
        ["привет"] = "hello",
        ["спасибо"] = "thank you",
        ["مرحبا"] = "hello",
        ["شكرا"] = "thank you"
    };

    public string LoadedModelPath { get; private set; } = string.Empty;

    public void LoadModel(string modelPath)
    {
        LoadedModelPath = modelPath;
    }

    public string Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Trim();
        if (string.Equals(LanguageDetector.Detect(normalized), "en", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var translated = normalized;
        foreach (var pair in _phrasebook)
        {
            translated = translated.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return translated == normalized ? $"[AI->EN] {normalized}" : translated;
    }
}
