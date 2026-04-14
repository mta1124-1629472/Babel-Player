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
    public SpeakerReferenceDraftItem(string speakerId, string? autoReferencePath)
    {
        SpeakerId = string.IsNullOrWhiteSpace(speakerId)
            ? throw new ArgumentException("Speaker id is required.", nameof(speakerId))
            : speakerId.Trim();
        AutoReferencePath = NormalizePath(autoReferencePath) ?? string.Empty;
        _draftReferencePath = AutoReferencePath;
    }

    public string SpeakerId { get; }

    public string AutoReferencePath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyPropertyChangedFor(nameof(EffectiveReferencePath))]
    private string _draftReferencePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLowConfidence))]
    private SpeakerReferenceConfidenceTier _confidenceTier = SpeakerReferenceConfidenceTier.Review;

    [ObservableProperty]
    private string _confidenceReasonSummary = "No reference clip selected yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInlineError))]
    private string _inlineError = string.Empty;

    [ObservableProperty]
    private string _lastActionLabel = "Auto";

    public bool IsChanged =>
        !string.Equals(
            NormalizePath(DraftReferencePath),
            NormalizePath(AutoReferencePath),
            StringComparison.OrdinalIgnoreCase);

    public bool IsLowConfidence => ConfidenceTier != SpeakerReferenceConfidenceTier.Good;

    public bool HasInlineError => !string.IsNullOrWhiteSpace(InlineError);

    public string EffectiveReferencePath => DraftReferencePath;

    public void SetDraftReferencePath(string? path, string actionLabel)
    {
        DraftReferencePath = NormalizePath(path) ?? string.Empty;
        LastActionLabel = string.IsNullOrWhiteSpace(actionLabel) ? "Manual" : actionLabel.Trim();
        InlineError = string.Empty;
    }

    public void RestoreAuto()
    {
        SetDraftReferencePath(AutoReferencePath, "Keep auto");
    }

    public void SetInlineError(string message)
    {
        InlineError = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
