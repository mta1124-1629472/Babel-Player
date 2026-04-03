using System.Collections.ObjectModel;
using System.Linq;
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

    public ModelsTabViewModel(ModelDownloader downloader, SessionWorkflowCoordinator coordinator)
    {
        // ── Faster Whisper ────────────────────────────────────────────────────
        var fwModels = coordinator.TranscriptionRegistry.GetAvailableProviders()
                           .FirstOrDefault(p => p.Id == ProviderNames.FasterWhisper)?.SupportedModels ?? [];
        foreach (var model in fwModels)
        {
            var m = model; // capture
            Models.Add(new ModelDownloadEntry(
                providerLabel: "Faster Whisper",
                modelId: m,
                isDownloadedFunc: () => ModelDownloader.IsFasterWhisperDownloaded(m),
                downloadFunc: (progress, token) => downloader.DownloadFasterWhisperAsync(m, progress, token),
                downloader: downloader));
        }

        // ── NLLB-200 ──────────────────────────────────────────────────────────
        var nllbModels = coordinator.TranslationRegistry.GetAvailableProviders()
                             .FirstOrDefault(p => p.Id == ProviderNames.Nllb200)?.SupportedModels ?? [];
        foreach (var model in nllbModels)
        {
            var m = model;
            Models.Add(new ModelDownloadEntry(
                providerLabel: "NLLB-200",
                modelId: m,
                isDownloadedFunc: () => ModelDownloader.IsNllbDownloaded(m),
                downloadFunc: (progress, token) => downloader.DownloadNllbAsync(m, progress, token),
                downloader: downloader));
        }

        // ── CTranslate2 ───────────────────────────────────────────────────────
        var ctranslateModels = coordinator.TranslationRegistry.GetAvailableProviders()
                                  .FirstOrDefault(p => p.Id == ProviderNames.CTranslate2)?.SupportedModels ?? [];
        foreach (var model in ctranslateModels)
        {
            var m = model;
            Models.Add(new ModelDownloadEntry(
                providerLabel: "CTranslate2",
                modelId: m,
                isDownloadedFunc: () => ModelDownloader.IsCTranslate2TranslationModelDownloaded(m),
                downloadFunc: (progress, token) => downloader.DownloadCTranslate2TranslationModelAsync(m, progress, token),
                downloader: downloader));
        }

        // ── Piper ─────────────────────────────────────────────────────────────
        var piperVoices = coordinator.TtsRegistry.GetAvailableProviders()
                              .FirstOrDefault(p => p.Id == ProviderNames.Piper)?.SupportedModels ?? [];
        foreach (var voice in piperVoices)
        {
            var v = voice;
            var dir = coordinator.CurrentSettings.PiperModelDir;
            Models.Add(new ModelDownloadEntry(
                providerLabel: "Piper",
                modelId: v,
                isDownloadedFunc: () => ModelDownloader.IsPiperVoiceDownloaded(v, dir),
                downloadFunc: (progress, token) => downloader.DownloadPiperVoiceAsync(v, dir, progress, token),
                downloader: downloader));
        }

        // ── XTTS v2 ───────────────────────────────────────────────────────────
        // Single monolithic model — no per-variant list needed.
        Models.Add(new ModelDownloadEntry(
            providerLabel: "XTTS v2",
            modelId: "xtts-v2",
            isDownloadedFunc: () => ModelDownloader.IsXttsDownloaded(),
            downloadFunc: (progress, token) => downloader.DownloadXttsAsync(progress, token),
            downloader: downloader));
    }
}
