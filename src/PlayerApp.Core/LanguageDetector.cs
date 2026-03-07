namespace PlayerApp.Core;

public static class LanguageDetector
{
    private static readonly string[] EnglishHints = [" the ", " and ", " is ", " are ", " of ", " to ", " in "];
    private static readonly string[] SpanishHints = [" hola ", " gracias ", " adios ", " buenos ", " que ", " para "];
    private static readonly string[] FrenchHints = [" bonjour ", " merci ", " salut ", " pour ", " avec "];

    public static string Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "en";
        }

        var sample = $" {text.Trim().ToLowerInvariant()} ";
        if (sample.Length > 220)
        {
            sample = sample[..220];
        }

        foreach (var ch in sample)
        {
            var code = (int)ch;
            if (code is >= 0x4E00 and <= 0x9FFF) return "zh";
            if ((code is >= 0x3040 and <= 0x309F) || (code is >= 0x30A0 and <= 0x30FF)) return "ja";
            if (code is >= 0x0400 and <= 0x04FF) return "ru";
            if (code is >= 0x0600 and <= 0x06FF) return "ar";
        }

        if (EnglishHints.Any(sample.Contains))
        {
            return "en";
        }

        if (SpanishHints.Any(sample.Contains))
        {
            return "es";
        }

        if (FrenchHints.Any(sample.Contains))
        {
            return "fr";
        }

        return sample.All(c => c <= 127) ? "en" : "und";
    }
}
