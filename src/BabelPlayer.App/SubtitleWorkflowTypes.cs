using BabelPlayer.Core;
using Whisper.net.Ggml;

namespace BabelPlayer.App;

public enum TranscriptionProvider
{
    Local,
    Cloud
}

public enum TranslationProvider
{
    None,
    LocalHyMt15_1_8B,
    LocalHyMt15_7B,
    OpenAi,
    Google,
    DeepL,
    MicrosoftTranslator
}

public enum SubtitlePipelineSource
{
    None,
    Sidecar,
    Manual,
    EmbeddedTrack,
    Generated
}

public enum LlamaCppBootstrapChoice
{
    Cancel,
    InstallAutomatically,
    ChooseExisting,
    OpenOfficialDownloadPage
}

public sealed record TranscriptionModelSelection(
    string Key,
    string DisplayName,
    TranscriptionProvider Provider,
    GgmlType? LocalModelType,
    string? CloudModel);

public sealed record TranslationModelSelection(
    string Key,
    string DisplayName,
    TranslationProvider Provider,
    string? CloudModel);

public sealed record SubtitleLoadResult(
    SubtitlePipelineSource Source,
    int CueCount,
    bool UsedSidecar,
    bool UsedGeneratedCaptions);

public sealed record SubtitleWorkflowSnapshot
{
    public string? CurrentVideoPath { get; init; }
    public string SelectedTranscriptionModelKey { get; init; } = SubtitleWorkflowCatalog.DefaultTranscriptionModelKey;
    public string SelectedTranscriptionLabel { get; init; } = SubtitleWorkflowCatalog.GetTranscriptionModel(SubtitleWorkflowCatalog.DefaultTranscriptionModelKey).DisplayName;
    public string? SelectedTranslationModelKey { get; init; }
    public string SelectedTranslationLabel { get; init; } = SubtitleWorkflowCatalog.GetTranslationModel(null).DisplayName;
    public bool IsTranslationEnabled { get; init; }
    public bool AutoTranslateEnabled { get; init; }
    public bool IsCaptionGenerationInProgress { get; init; }
    public string CurrentSourceLanguage { get; init; } = "und";
    public SubtitlePipelineSource SubtitleSource { get; init; }
    public string? OverlayStatus { get; init; }
    public SubtitleCue? ActiveCue { get; init; }
    public IReadOnlyList<SubtitleCue> Cues { get; init; } = [];
}

public sealed record SubtitleOverlayPresentation
{
    public bool IsVisible { get; init; }
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
}

public static class SubtitleWorkflowCatalog
{
    public const string DefaultTranscriptionModelKey = "local:tiny";

    public static TranscriptionModelSelection GetTranscriptionModel(string? modelKey)
    {
        return modelKey switch
        {
            "local:tiny" => new TranscriptionModelSelection("local:tiny", "Local Tiny.en", TranscriptionProvider.Local, GgmlType.TinyEn, null),
            "local:base" => new TranscriptionModelSelection("local:base", "Local Base.en", TranscriptionProvider.Local, GgmlType.BaseEn, null),
            "local:small" => new TranscriptionModelSelection("local:small", "Local Small.en", TranscriptionProvider.Local, GgmlType.SmallEn, null),
            "local:tiny-multilingual" => new TranscriptionModelSelection("local:tiny-multilingual", "Local Tiny (multilingual)", TranscriptionProvider.Local, GgmlType.Tiny, null),
            "local:base-multilingual" => new TranscriptionModelSelection("local:base-multilingual", "Local Base (multilingual)", TranscriptionProvider.Local, GgmlType.Base, null),
            "local:small-multilingual" => new TranscriptionModelSelection("local:small-multilingual", "Local Small (multilingual)", TranscriptionProvider.Local, GgmlType.Small, null),
            "cloud:gpt-4o-mini-transcribe" => new TranscriptionModelSelection("cloud:gpt-4o-mini-transcribe", "Cloud GPT-4o Mini Transcribe", TranscriptionProvider.Cloud, null, "gpt-4o-mini-transcribe"),
            "cloud:gpt-4o-transcribe" => new TranscriptionModelSelection("cloud:gpt-4o-transcribe", "Cloud GPT-4o Transcribe", TranscriptionProvider.Cloud, null, "gpt-4o-transcribe"),
            "cloud:whisper-1" => new TranscriptionModelSelection("cloud:whisper-1", "Cloud Whisper-1", TranscriptionProvider.Cloud, null, "whisper-1"),
            _ => new TranscriptionModelSelection(DefaultTranscriptionModelKey, "Local Tiny.en", TranscriptionProvider.Local, GgmlType.TinyEn, null)
        };
    }

    public static TranslationModelSelection GetTranslationModel(string? modelKey)
    {
        return modelKey switch
        {
            "local:hymt-1.8b" => new TranslationModelSelection("local:hymt-1.8b", "Local HY-MT1.5 1.8B", TranslationProvider.LocalHyMt15_1_8B, null),
            "local:hymt-7b" => new TranslationModelSelection("local:hymt-7b", "Local HY-MT1.5 7B", TranslationProvider.LocalHyMt15_7B, null),
            "cloud:gpt-5-mini" => new TranslationModelSelection("cloud:gpt-5-mini", "Cloud OpenAI GPT-5 Mini", TranslationProvider.OpenAi, "gpt-5-mini"),
            "cloud:google-translate" => new TranslationModelSelection("cloud:google-translate", "Cloud Google Translate", TranslationProvider.Google, null),
            "cloud:deepl" => new TranslationModelSelection("cloud:deepl", "Cloud DeepL API", TranslationProvider.DeepL, null),
            "cloud:microsoft-translator" => new TranslationModelSelection("cloud:microsoft-translator", "Cloud Microsoft Translator", TranslationProvider.MicrosoftTranslator, null),
            _ => new TranslationModelSelection(string.Empty, "No translation model", TranslationProvider.None, null)
        };
    }

    public static bool IsCloudTranslationProvider(TranslationProvider provider)
    {
        return provider is TranslationProvider.OpenAi
            or TranslationProvider.Google
            or TranslationProvider.DeepL
            or TranslationProvider.MicrosoftTranslator;
    }
}
