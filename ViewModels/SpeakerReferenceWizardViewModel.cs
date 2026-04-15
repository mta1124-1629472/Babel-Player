using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Models.LanguageSupport;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public enum UseSelectedSegmentStatus
{
    Applied,
    MissingSelection,
    RequiresSpeakerMismatchConfirmation,
    Failed,
}

public sealed record UseSelectedSegmentOutcome(UseSelectedSegmentStatus Status, string? Message = null);

public sealed partial class SpeakerReferenceWizardViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedPlaybackViewModel _playback;
    private readonly SessionWorkflowCoordinator _coordinator;

    public SpeakerReferenceWizardViewModel(
        EmbeddedPlaybackViewModel playback,
        SessionWorkflowCoordinator coordinator,
        ModelDownloader modelDownloader)
    {
        _playback = playback;
        _coordinator = coordinator;
        MiniPreview = new SpeakerWizardMiniPreviewViewModel(coordinator);
        _playback.PropertyChanged += OnPlaybackPropertyChanged;

        PiperVoiceCatalogRows = new ObservableCollection<PiperVoiceCatalogRowViewModel>(
            PiperTtsCatalog.Voices.Select(v =>
                new PiperVoiceCatalogRowViewModel(modelDownloader, coordinator, v, RefreshPiperVoices)));
    }

    public SpeakerWizardMiniPreviewViewModel MiniPreview { get; }

    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EmbeddedPlaybackViewModel.TtsProvider) or nameof(EmbeddedPlaybackViewModel.TtsModelOrVoice))
        {
            OnPropertyChanged(nameof(ShowPiperVoicePicker));
            RefreshPiperVoices();
        }
    }

    [ObservableProperty]
    private ObservableCollection<SpeakerReferenceDraftItem> _allDraftItems = new();

    [ObservableProperty]
    private ObservableCollection<SpeakerReferenceDraftItem> _visibleDraftItems = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
    private bool _showLowConfidenceOnly;

    [ObservableProperty]
    private int _goodCount;

    [ObservableProperty]
    private int _reviewCount;

    [ObservableProperty]
    private int _poorCount;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Load detected speakers to review reference clips.";

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private ObservableCollection<string> _piperVoices = new();

    /// <summary>Catalog Piper voices with download controls (when TTS is Piper).</summary>
    [ObservableProperty]
    private ObservableCollection<PiperVoiceCatalogRowViewModel> _piperVoiceCatalogRows = new();

    [ObservableProperty]
    private ObservableCollection<string> _speakerIdOptions = new();

    [ObservableProperty]
    private string? _mergeSourceSpeakerId;

    [ObservableProperty]
    private string? _mergeTargetSpeakerId;

    /// <summary>Reference clip length when using the main preview playhead (seconds, clamped 3–15 at extract).</summary>
    [ObservableProperty]
    private double _playheadClipWindowSeconds = 8.0;

    public bool HasPendingChanges => AllDraftItems.Any(item => item.IsChanged);

    public bool ShowPiperVoicePicker =>
        string.Equals(_playback.TtsProvider, ProviderNames.Piper, StringComparison.Ordinal);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            StatusText = "Loading detected speakers...";
            var segments = await _coordinator.GetSegmentWorkflowListAsync();
            var speakerIds = segments
                .Select(segment => segment.SpeakerId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            var references = _coordinator.GetSpeakerReferenceAudioPaths();
            var voices = _coordinator.GetSpeakerVoiceAssignments();
            var drafts = new List<SpeakerReferenceDraftItem>(speakerIds.Count);
            foreach (var speakerId in speakerIds)
            {
                references.TryGetValue(speakerId!, out var path);
                voices.TryGetValue(speakerId!, out var voice);
                var item = new SpeakerReferenceDraftItem(speakerId!, path, voice)
                {
                    ReferenceActionsEnabled = IsCloningTts,
                    ShowPiperVoiceRow = ShowPiperVoicePicker,
                };
                var segsForSpeaker = segments
                    .Where(s => string.Equals(s.SpeakerId, speakerId, StringComparison.Ordinal))
                    .OrderBy(s => s.StartSeconds)
                    .ToList();
                item.SetSourceSegments(segsForSpeaker);
                await RefreshConfidenceAsync(item, IsCloningTts, cancellationToken);
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasPendingChanges));
                drafts.Add(item);
            }

            AllDraftItems = new ObservableCollection<SpeakerReferenceDraftItem>(drafts);
            SpeakerIdOptions = new ObservableCollection<string>(speakerIds!);
            if (MergeSourceSpeakerId is null || !SpeakerIdOptions.Contains(MergeSourceSpeakerId))
                MergeSourceSpeakerId = SpeakerIdOptions.FirstOrDefault();
            if (MergeTargetSpeakerId is null || !SpeakerIdOptions.Contains(MergeTargetSpeakerId))
                MergeTargetSpeakerId = SpeakerIdOptions.Skip(1).FirstOrDefault() ?? SpeakerIdOptions.FirstOrDefault();
            RefreshPiperVoices();
            RecomputeCounts();
            ApplyFilter();
            HasLoaded = true;
            OnPropertyChanged(nameof(ShowPiperVoicePicker));
            MergeSpeakersCommand.NotifyCanExecuteChanged();
            MiniPreview.TryReloadAfterSessionChange();
            StatusText = AllDraftItems.Count == 0
                ? "No diarized speakers found yet."
                : $"Loaded {AllDraftItems.Count} speakers for review.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void KeepAuto(SpeakerReferenceDraftItem? item)
    {
        if (item is null)
            return;

        item.RestoreAuto();
        _ = RefreshConfidenceAsync(item, IsCloningTts);
        RecomputeCounts();
        ApplyFilter();
        StatusText = $"Restored auto settings for {item.SpeakerId}.";
    }

    public async Task ApplyBrowseSelectionAsync(SpeakerReferenceDraftItem item, string? selectedPath, CancellationToken cancellationToken = default)
    {
        if (item is null || string.IsNullOrWhiteSpace(selectedPath))
            return;

        item.SetDraftReferencePath(selectedPath, "Browse file");
        await RefreshConfidenceAsync(item, IsCloningTts, cancellationToken);
        RecomputeCounts();
        ApplyFilter();
        StatusText = $"Selected a custom clip for {item.SpeakerId}.";
    }

    [RelayCommand]
    private async Task AutoPickAnotherAsync(SpeakerReferenceDraftItem? item)
    {
        if (item is null)
            return;

        item.SetInlineError(string.Empty);
        var alternate = await _coordinator.AutoPickAlternateSpeakerReferenceAsync(
            item.SpeakerId,
            [item.EffectiveReferencePath]);
        if (string.IsNullOrWhiteSpace(alternate))
        {
            item.SetInlineError("No alternate clip candidate was found for this speaker.");
            StatusText = $"No alternate clip candidates were found for {item.SpeakerId}.";
            return;
        }

        item.SetDraftReferencePath(alternate, "Auto-pick another");
        await RefreshConfidenceAsync(item, IsCloningTts);
        RecomputeCounts();
        ApplyFilter();
        StatusText = $"Auto-picked a different clip for {item.SpeakerId}.";
    }

    public async Task<UseSelectedSegmentOutcome> UseSelectedSegmentAsync(
        SpeakerReferenceDraftItem item,
        bool allowSpeakerMismatch,
        CancellationToken cancellationToken = default)
    {
        var selected = _playback.Preview.SelectedSegment;
        if (selected is null)
        {
            item.SetInlineError("Select a segment in the preview panel first.");
            return new UseSelectedSegmentOutcome(
                UseSelectedSegmentStatus.MissingSelection,
                "Select a segment in the preview panel first.");
        }

        if (!allowSpeakerMismatch &&
            !string.IsNullOrWhiteSpace(selected.SpeakerId) &&
            !string.Equals(selected.SpeakerId, item.SpeakerId, StringComparison.Ordinal))
        {
            return new UseSelectedSegmentOutcome(
                UseSelectedSegmentStatus.RequiresSpeakerMismatchConfirmation,
                $"Selected segment belongs to {selected.SpeakerId}, not {item.SpeakerId}.");
        }

        try
        {
            var extractedPath = await _coordinator.ExtractSpeakerReferenceFromSegmentAsync(
                item.SpeakerId,
                selected,
                cancellationToken);

            item.SetDraftReferencePath(extractedPath, "Use selected segment");
            await RefreshConfidenceAsync(item, IsCloningTts, cancellationToken);
            RecomputeCounts();
            ApplyFilter();
            StatusText = $"Extracted a reference clip from the selected segment for {item.SpeakerId}.";
            return new UseSelectedSegmentOutcome(UseSelectedSegmentStatus.Applied);
        }
        catch (Exception ex)
        {
            item.SetInlineError(ex.Message);
            return new UseSelectedSegmentOutcome(UseSelectedSegmentStatus.Failed, ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResetAllAsync()
    {
        foreach (var item in AllDraftItems)
        {
            item.RestoreAuto();
            await RefreshConfidenceAsync(item, IsCloningTts);
        }

        RecomputeCounts();
        ApplyFilter();
        StatusText = "Draft changes reset to auto selections.";
    }

    public async Task FinishAsync(CancellationToken cancellationToken = default)
    {
        _coordinator.StopWizardAudioPreview();
        var refChanges = BuildReferencePersistencePayload(AllDraftItems);
        var voiceChanges = BuildVoicePersistencePayload(AllDraftItems);
        _coordinator.ApplySpeakerReferenceAudioPathChanges(refChanges);
        _coordinator.ApplySpeakerVoiceAssignmentChanges(voiceChanges);
        await _playback.Preview.RefreshSegmentsAsync();
        _playback.SpeakerRouting.UpdateSelectedSpeakerDetails(_playback.SpeakerRouting.SelectedSpeakerId);
        var total = refChanges.Count + voiceChanges.Count;
        StatusText = total == 0
            ? "No speaker setup changes were applied."
            : $"Applied {refChanges.Count} reference and {voiceChanges.Count} voice updates.";
        _playback.StatusText = StatusText;
    }

    public void Cancel()
    {
        _coordinator.StopWizardAudioPreview();
        StatusText = "Speaker reference draft changes were discarded.";
    }

    public void RefreshPiperVoices()
    {
        var list = ModelDownloader.ListDownloadedPiperVoiceIds(_coordinator.CurrentSettings.PiperModelDir);
        PiperVoices = new ObservableCollection<string>(list);
        foreach (var row in PiperVoiceCatalogRows)
            row.RefreshStatus();
    }

    [RelayCommand]
    private async Task PlayReferencePreviewAsync(SpeakerReferenceDraftItem? item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.EffectiveReferencePath))
            return;
        if (!File.Exists(item.EffectiveReferencePath))
        {
            item.SetInlineError("Reference file is missing on disk.");
            return;
        }

        item.SetInlineError(string.Empty);
        await _coordinator.PlayWizardAudioPreviewAsync(item.EffectiveReferencePath);
        StatusText = $"Playing reference clip for {item.SpeakerId}.";
    }

    [RelayCommand]
    private void StopReferencePreview()
    {
        _coordinator.StopWizardAudioPreview();
        StatusText = "Stopped audio preview.";
    }

    public async Task JumpToSegmentAsync(WorkflowSegmentState segment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        await _playback.Preview.SelectSegmentAndSeekAsync(segment, playSource: false);
        MiniPreview.SeekToSegmentStart(segment);
        StatusText = $"Preview jumped to {segment.SegmentId} ({segment.StartSeconds:F1}s).";
    }

    public async Task<UseSelectedSegmentOutcome> UsePlayheadClipAsync(
        SpeakerReferenceDraftItem item,
        CancellationToken cancellationToken = default)
    {
        if (item is null)
            return new UseSelectedSegmentOutcome(UseSelectedSegmentStatus.Failed, "Invalid item.");

        var windowSec = Math.Clamp(PlayheadClipWindowSeconds, 3.0, 15.0);
        double centerSec;
        double mediaDurationSec;
        if (MiniPreview.UseMiniPlayheadForClips)
        {
            centerSec = MiniPreview.PositionMs / 1000.0;
            mediaDurationSec = MiniPreview.DurationMs / 1000.0;
        }
        else
        {
            centerSec = _playback.Preview.SourcePositionMs / 1000.0;
            mediaDurationSec = _playback.Preview.SourceDurationMs / 1000.0;
        }

        var (start, _) = SpeakerWizardPlayheadHelper.ComputeClipStartAndBounds(
            centerSec,
            windowSec,
            mediaDurationSec);

        try
        {
            var extractedPath = await _coordinator.ExtractSpeakerReferenceFromSourceAsync(
                item.SpeakerId,
                start,
                windowSec,
                cancellationToken);

            item.SetDraftReferencePath(extractedPath, "Use playhead clip");
            await RefreshConfidenceAsync(item, IsCloningTts, cancellationToken);
            RecomputeCounts();
            ApplyFilter();
            StatusText = $"Extracted {windowSec:F1}s reference from playhead for {item.SpeakerId}.";
            return new UseSelectedSegmentOutcome(UseSelectedSegmentStatus.Applied);
        }
        catch (Exception ex)
        {
            item.SetInlineError(ex.Message);
            return new UseSelectedSegmentOutcome(UseSelectedSegmentStatus.Failed, ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMergeSpeakers))]
    private async Task MergeSpeakersAsync()
    {
        if (string.IsNullOrWhiteSpace(MergeSourceSpeakerId) || string.IsNullOrWhiteSpace(MergeTargetSpeakerId))
            return;
        if (string.Equals(MergeSourceSpeakerId, MergeTargetSpeakerId, StringComparison.Ordinal))
            return;

        IsBusy = true;
        try
        {
            _coordinator.StopWizardAudioPreview();
            var n = await _coordinator.MergeDiarizedSpeakersAsync(
                MergeSourceSpeakerId,
                MergeTargetSpeakerId);

            await LoadAsync();
            await _playback.Preview.RefreshSegmentsAsync();
            StatusText = n == 0
                ? $"Merged {MergeSourceSpeakerId} → {MergeTargetSpeakerId} (session maps updated; no transcript segments matched source id)."
                : $"Merged {MergeSourceSpeakerId} → {MergeTargetSpeakerId} ({n} transcript segments relabeled).";
            _playback.StatusText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"Merge failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanMergeSpeakers() =>
        !string.IsNullOrWhiteSpace(MergeSourceSpeakerId)
        && !string.IsNullOrWhiteSpace(MergeTargetSpeakerId)
        && !string.Equals(MergeSourceSpeakerId, MergeTargetSpeakerId, StringComparison.Ordinal);

    partial void OnMergeSourceSpeakerIdChanged(string? value) => MergeSpeakersCommand.NotifyCanExecuteChanged();

    partial void OnMergeTargetSpeakerIdChanged(string? value) => MergeSpeakersCommand.NotifyCanExecuteChanged();

    internal static Dictionary<string, string?> BuildReferencePersistencePayload(IEnumerable<SpeakerReferenceDraftItem> draftItems)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in draftItems)
        {
            if (!item.IsReferenceChanged)
                continue;
            result[item.SpeakerId] = string.IsNullOrWhiteSpace(item.EffectiveReferencePath)
                ? null
                : item.EffectiveReferencePath;
        }

        return result;
    }

    internal static Dictionary<string, string?> BuildVoicePersistencePayload(IEnumerable<SpeakerReferenceDraftItem> draftItems)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in draftItems)
        {
            if (!item.IsVoiceChanged)
                continue;
            result[item.SpeakerId] = string.IsNullOrWhiteSpace(item.EffectiveAssignedVoice)
                ? null
                : item.EffectiveAssignedVoice;
        }

        return result;
    }

    partial void OnShowLowConfidenceOnlyChanged(bool value) => ApplyFilter();

    private bool IsCloningTts =>
        string.Equals(_playback.TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal);

    public void UseActiveTtsVoiceForSpeaker(SpeakerReferenceDraftItem item)
    {
        if (string.IsNullOrWhiteSpace(_playback.TtsModelOrVoice))
        {
            item.SetInlineError("Select an active TTS voice in the DUB section first.");
            return;
        }

        item.SetDraftVoice(_playback.TtsModelOrVoice, "Use active TTS");
        item.SetInlineError(string.Empty);
        OnPropertyChanged(nameof(HasPendingChanges));
        StatusText = $"Draft: assign active TTS voice to {item.SpeakerId}.";
    }

    public void ClearDraftVoiceForSpeaker(SpeakerReferenceDraftItem item)
    {
        item.SetDraftVoice(string.Empty, "Clear voice");
        item.SetInlineError(string.Empty);
        OnPropertyChanged(nameof(HasPendingChanges));
        StatusText = $"Draft: cleared voice for {item.SpeakerId}.";
    }

    private async Task RefreshConfidenceAsync(
        SpeakerReferenceDraftItem item,
        bool cloningTts,
        CancellationToken cancellationToken = default)
    {
        if (!cloningTts)
        {
            item.ConfidenceTier = SpeakerReferenceConfidenceTier.Good;
            item.ConfidenceReasonSummary = "Reference clips apply when TTS is Qwen (voice cloning).";
            return;
        }

        if (string.IsNullOrWhiteSpace(item.EffectiveReferencePath))
        {
            item.ConfidenceTier = SpeakerReferenceConfidenceTier.Poor;
            item.ConfidenceReasonSummary = "No reference clip selected yet.";
            return;
        }

        var evaluation = await SpeakerReferenceClipQualityEvaluator
            .EvaluateFileAsync(item.EffectiveReferencePath, cancellationToken);
        item.ConfidenceTier = evaluation.Tier;
        item.ConfidenceReasonSummary = string.Join(" ", evaluation.Reasons);
    }

    private void RecomputeCounts()
    {
        GoodCount = AllDraftItems.Count(item => item.ConfidenceTier == SpeakerReferenceConfidenceTier.Good);
        ReviewCount = AllDraftItems.Count(item => item.ConfidenceTier == SpeakerReferenceConfidenceTier.Review);
        PoorCount = AllDraftItems.Count(item => item.ConfidenceTier == SpeakerReferenceConfidenceTier.Poor);
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private void ApplyFilter()
    {
        var filtered = FilterDraftItems(AllDraftItems, ShowLowConfidenceOnly);
        VisibleDraftItems = new ObservableCollection<SpeakerReferenceDraftItem>(filtered);
    }

    internal static IReadOnlyList<SpeakerReferenceDraftItem> FilterDraftItems(
        IEnumerable<SpeakerReferenceDraftItem> draftItems,
        bool showLowConfidenceOnly)
    {
        var source = draftItems.ToList();
        if (!showLowConfidenceOnly)
            return source;
        return source.Where(item => item.IsLowConfidence).ToList();
    }

    public void Dispose()
    {
        _playback.PropertyChanged -= OnPlaybackPropertyChanged;
        MiniPreview.Dispose();
    }
}
