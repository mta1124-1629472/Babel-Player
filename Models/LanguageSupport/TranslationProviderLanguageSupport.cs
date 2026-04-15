using Babel.Player.Models;

namespace Babel.Player.Models.LanguageSupport;

/// <summary>Open-ended cloud translation: no per-language allowlist in Babel.</summary>
public enum TranslationLanguageSupportMode
{
    /// <summary>Finite allowlist (e.g. NLLB, DeepL curated).</summary>
    Finite,

    /// <summary>Any non-empty canonical target is accepted for translation stage; TTS may still be limiting.</summary>
    Multilingual,
}

public static class TranslationProviderLanguageSupport
{
    public static TranslationLanguageSupportMode GetTranslationMode(string providerId) =>
        providerId switch
        {
            ProviderNames.OpenAi => TranslationLanguageSupportMode.Multilingual,
            ProviderNames.GeminiTranslation => TranslationLanguageSupportMode.Multilingual,
            _ => TranslationLanguageSupportMode.Finite,
        };

    public static bool IsMultilingualTranslationProvider(string? providerId) =>
        providerId is not null && GetTranslationMode(providerId) == TranslationLanguageSupportMode.Multilingual;
}
