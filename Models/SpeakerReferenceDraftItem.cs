using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Babel.Player.Models;

public enum SpeakerReferenceConfidenceTier
{
    Good,
    Review,
    Poor,
}

public sealed partial class SpeakerReferenceDraftItem : ObservableObject
{
    public SpeakerReferenceDraftItem(string speakerId, string? autoReferencePath, string? autoAssignedVoice = null)
    {
        SpeakerId = string.IsNullOrWhiteSpace(speakerId)
            ? throw new ArgumentException("Speaker id is required.", nameof(speakerId))
            : speakerId.Trim();
        AutoReferencePath = NormalizePath(autoReferencePath) ?? string.Empty;
        _draftReferencePath = AutoReferencePath;
        AutoAssignedVoice = NormalizeVoice(autoAssignedVoice) ?? string.Empty;
        _draftAssignedVoice = AutoAssignedVoice;
    }

    public string SpeakerId { get; }

    public string AutoReferencePath { get; }

    public string AutoAssignedVoice { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyPropertyChangedFor(nameof(EffectiveReferencePath))]
    private string _draftReferencePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _draftAssignedVoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLowConfidence))]
    private SpeakerReferenceConfidenceTier _confidenceTier = SpeakerReferenceConfidenceTier.Review;

    [ObservableProperty]
    private string _confidenceReasonSummary = "No reference clip selected yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInlineError))]
    private string _inlineError = string.Empty;

    [ObservableProperty]
    private bool _referenceActionsEnabled = true;

    [ObservableProperty]
    private string _lastActionLabel = "Auto";

    public bool IsReferenceChanged =>
        !string.Equals(
            NormalizePath(DraftReferencePath),
            NormalizePath(AutoReferencePath),
            StringComparison.OrdinalIgnoreCase);

    public bool IsVoiceChanged =>
        !string.Equals(
            NormalizeVoice(DraftAssignedVoice),
            NormalizeVoice(AutoAssignedVoice),
            StringComparison.Ordinal);

    public bool IsChanged => IsReferenceChanged || IsVoiceChanged;

    public bool IsLowConfidence => ConfidenceTier != SpeakerReferenceConfidenceTier.Good;

    public bool HasInlineError => !string.IsNullOrWhiteSpace(InlineError);

    public string EffectiveReferencePath => DraftReferencePath;

    public string EffectiveAssignedVoice => DraftAssignedVoice;

    public void SetDraftReferencePath(string? path, string actionLabel)
    {
        DraftReferencePath = NormalizePath(path) ?? string.Empty;
        LastActionLabel = string.IsNullOrWhiteSpace(actionLabel) ? "Manual" : actionLabel.Trim();
        InlineError = string.Empty;
    }

    public void SetDraftVoice(string? voiceOrModel, string actionLabel)
    {
        DraftAssignedVoice = NormalizeVoice(voiceOrModel) ?? string.Empty;
        LastActionLabel = string.IsNullOrWhiteSpace(actionLabel) ? "Voice" : actionLabel.Trim();
        InlineError = string.Empty;
    }

    public void RestoreAuto()
    {
        SetDraftReferencePath(AutoReferencePath, "Keep auto");
        DraftAssignedVoice = AutoAssignedVoice;
    }

    public void SetInlineError(string message)
    {
        InlineError = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    private static string? NormalizeVoice(string? voice) =>
        string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
}
