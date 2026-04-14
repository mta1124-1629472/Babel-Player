using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
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

public sealed partial class SpeakerReferenceWizardViewModel : ViewModelBase
{
    private readonly EmbeddedPlaybackViewModel _playback;
    private readonly SessionWorkflowCoordinator _coordinator;

    public SpeakerReferenceWizardViewModel(
        EmbeddedPlaybackViewModel playback,
        SessionWorkflowCoordinator coordinator)
    {
        _playback = playback;
        _coordinator = coordinator;
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

    public bool HasPendingChanges => AllDraftItems.Any(item => item.IsChanged);

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
                };
                await RefreshConfidenceAsync(item, IsCloningTts, cancellationToken);
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(HasPendingChanges));
                drafts.Add(item);
            }

            AllDraftItems = new ObservableCollection<SpeakerReferenceDraftItem>(drafts);
            RecomputeCounts();
            ApplyFilter();
            HasLoaded = true;
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
        StatusText = "Speaker reference draft changes were discarded.";
    }

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
}
