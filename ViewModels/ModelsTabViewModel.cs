using System.Collections.ObjectModel;
using Babel.Player.Models;
using Babel.Player.Services;


namespace Babel.Player.ViewModels;

/// <summary>
/// ViewModel for the Settings → Models tab.
/// Builds one ModelDownloadEntry per locally-hosted model and exposes them for display.
/// Cloud API providers (OpenAI, DeepL, etc.) are intentionally excluded — they have no local files.
/// </summary>
public sealed class ModelsTabViewModel : ViewModelBase
{
    public ObservableCollection<ModelDownloadEntry> Models { get; } = [];

    public ModelsTabViewModel(ModelDownloader downloader, string piperModelDir)
    {
        // ── Faster Whisper ────────────────────────────────────────────────────
        foreach (var model in ProviderOptions.GetTranscriptionModels(ProviderNames.FasterWhisper))
        {
            var m = model; // capture
            Models.Add(new ModelDownloadEntry(
                providerLabel: "Faster Whisper",
                modelId: m,
                isDownloadedFunc: () => ProviderReadinessResolver.IsFasterWhisperDownloaded(m),
                downloadFunc: (progress, token) => downloader.DownloadFasterWhisperAsync(m, progress, token),
                downloader: downloader));
        }

        // ── NLLB-200 ──────────────────────────────────────────────────────────
        foreach (var model in ProviderOptions.GetTranslationModels(ProviderNames.Nllb200))
        {
            var m = model;
            Models.Add(new ModelDownloadEntry(
                providerLabel: "NLLB-200",
                modelId: m,
                isDownloadedFunc: () => ProviderReadinessResolver.IsNllbDownloaded(m),
                downloadFunc: (progress, token) => downloader.DownloadNllbAsync(m, progress, token),
                downloader: downloader));
        }

        // ── Piper ─────────────────────────────────────────────────────────────
        foreach (var voice in ProviderOptions.PiperVoices)
        {
            var v = voice;
            var dir = piperModelDir;
            Models.Add(new ModelDownloadEntry(
                providerLabel: "Piper",
                modelId: v,
                isDownloadedFunc: () => ProviderReadinessResolver.IsPiperVoiceDownloaded(v, dir),
                downloadFunc: (progress, token) => downloader.DownloadPiperVoiceAsync(v, dir, progress, token),
                downloader: downloader));
        }
    }
}
