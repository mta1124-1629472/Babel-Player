using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

public sealed partial class EmbeddedPlaybackSpeakerRoutingViewModel : ViewModelBase
{
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;
    private string _autoSpeakerDetectionStatus = "Manual speaker mapping is the default release flow.";

    internal EmbeddedPlaybackSpeakerRoutingViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator)
    {
        _parent = parent;
        _coordinator = coordinator;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMultiSpeakerNoSpeakersYet))]
    [NotifyPropertyChangedFor(nameof(CanReviewSpeakerReferences))]
    private bool _isMultiSpeakerEnabled;

    [ObservableProperty]
    private IReadOnlyList<string> _diarizationProviderOptions = [string.Empty];

    [ObservableProperty]
    private string _diarizationProvider = string.Empty;

    [ObservableProperty]
    private decimal? _diarizationMinSpeakers;

    [ObservableProperty]
    private decimal? _diarizationMaxSpeakers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSpeakers))]
    [NotifyPropertyChangedFor(nameof(IsMultiSpeakerNoSpeakersYet))]
    [NotifyPropertyChangedFor(nameof(CanReviewSpeakerReferences))]
    private ObservableCollection<string> _speakerIds = new();

    [ObservableProperty]
    private string? _selectedSpeakerId;

    [ObservableProperty]
    private string _selectedSpeakerAssignedVoice = string.Empty;

    [ObservableProperty]
    private string _selectedSpeakerReferenceAudioPath = string.Empty;

    [ObservableProperty]
    private string _defaultTtsVoiceFallback = string.Empty;

    public string AutoSpeakerDetectionStatus
    {
        get => _autoSpeakerDetectionStatus;
        private set
        {
            if (!SetProperty(ref _autoSpeakerDetectionStatus, value))
                return;

            OnPropertyChanged(nameof(HasAutoSpeakerDetectionStatus));
        }
    }

    public bool HasSpeakers => SpeakerIds.Count > 0;
    public bool HasAutoSpeakerDetectionStatus => !string.IsNullOrWhiteSpace(AutoSpeakerDetectionStatus);
    public bool IsMultiSpeakerNoSpeakersYet => IsMultiSpeakerEnabled && !HasSpeakers;
    public bool IsTtsCloningProvider => string.Equals(_parent.TtsProvider, ProviderNames.Qwen, StringComparison.Ordinal);
    public bool CanReviewSpeakerReferences => IsMultiSpeakerEnabled && HasSpeakers && IsTtsCloningProvider;

    internal void SyncFromSettings()
    {
        _parent.IsSynchronizingPipelineSettings = true;
        try
        {
            if (!_coordinator.CurrentSession.MultiSpeakerEnabled)
                _coordinator.SetMultiSpeakerEnabled(true);

            IsMultiSpeakerEnabled = true;
            RebuildDiarizationProviderOptions();
            DiarizationProvider = NormalizeDiarizationProviderSelection(_coordinator.CurrentSettings.DiarizationProvider);
            DiarizationMinSpeakers = null;
            DiarizationMaxSpeakers = null;
            DefaultTtsVoiceFallback = _coordinator.CurrentSession.DefaultTtsVoiceFallback ?? string.Empty;
            RebuildSpeakerIds(_parent.Preview.Segments, _parent.Preview.SelectedSegment?.SpeakerId);
        }
        finally
        {
            _parent.IsSynchronizingPipelineSettings = false;
        }
    }

    internal void SetAutoSpeakerDetectionStatus(string status) => AutoSpeakerDetectionStatus = status;

    internal void NotifyTtsProviderChanged()
    {
        OnPropertyChanged(nameof(IsTtsCloningProvider));
        OnPropertyChanged(nameof(CanReviewSpeakerReferences));
    }

    internal void RebuildSpeakerIds(IEnumerable<WorkflowSegmentState> segments, string? preferredSpeakerId = null)
    {
        var ordered = segments
            .Select(segment => segment.SpeakerId)
            .Where(speakerId => !string.IsNullOrWhiteSpace(speakerId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(speakerId => speakerId, StringComparer.Ordinal)
            .ToList();

        SpeakerIds = new ObservableCollection<string>(ordered!);

        var candidate = !string.IsNullOrWhiteSpace(preferredSpeakerId) && SpeakerIds.Contains(preferredSpeakerId)
            ? preferredSpeakerId
            : !string.IsNullOrWhiteSpace(SelectedSpeakerId) && SpeakerIds.Contains(SelectedSpeakerId)
                ? SelectedSpeakerId
                : SpeakerIds.FirstOrDefault();

        SelectedSpeakerId = candidate;
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
    }

    internal void TrySelectSpeakerForSegment(string? speakerId)
    {
        if (!string.IsNullOrWhiteSpace(speakerId) && SpeakerIds.Contains(speakerId))
            SelectedSpeakerId = speakerId;
    }

    [RelayCommand]
    public async Task AssignSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId) || string.IsNullOrWhiteSpace(_parent.TtsModelOrVoice))
            return;

        _coordinator.SetSpeakerVoiceAssignment(SelectedSpeakerId, _parent.TtsModelOrVoice);
        _parent.StatusText = $"Assigned {_parent.TtsModelOrVoice} to {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    [RelayCommand]
    public async Task ClearSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerVoiceAssignment(SelectedSpeakerId);
        _parent.StatusText = $"Cleared voice assignment for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    public async Task SetReferenceAudioForSelectedSpeakerAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId) || string.IsNullOrWhiteSpace(path))
            return;

        _coordinator.SetSpeakerReferenceAudioPath(SelectedSpeakerId, path);
        _parent.StatusText = $"Set reference audio for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    [RelayCommand]
    public async Task ClearSelectedSpeakerReferenceAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerReferenceAudioPath(SelectedSpeakerId);
        _parent.StatusText = $"Cleared reference audio for {SelectedSpeakerId}.";
        UpdateSelectedSpeakerDetails(SelectedSpeakerId);
        await _parent.Preview.RefreshSegmentsAsync();
    }

    partial void OnSelectedSpeakerIdChanged(string? value)
    {
        UpdateSelectedSpeakerDetails(value);
    }

    partial void OnIsMultiSpeakerEnabledChanged(bool value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        if (!value)
            DiarizationProvider = string.Empty;

        _coordinator.SetMultiSpeakerEnabled(value);
        _ = _parent.Preview.RefreshSegmentsAsync();
    }

    partial void OnDiarizationProviderChanged(string value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        var normalized = NormalizeDiarizationProviderSelection(value);
        _parent.IsSynchronizingPipelineSettings = true;
        try
        {
            if (!string.Equals(normalized, value, StringComparison.Ordinal))
                DiarizationProvider = normalized;
        }
        finally
        {
            _parent.IsSynchronizingPipelineSettings = false;
        }

        _coordinator.CurrentSettings.DiarizationProvider = normalized;
        _coordinator.CurrentSettings.DiarizationMinSpeakers = null;
        _coordinator.CurrentSettings.DiarizationMaxSpeakers = null;
        _coordinator.NotifySettingsModified();
        _parent.RefreshProviderHealthDiagnostics();
        _parent.Pipeline.NotifySessionStateChanged();
    }

    partial void OnDiarizationMinSpeakersChanged(decimal? value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        var normalized = NormalizeSpeakerCount(value);
        if (HasInvalidDiarizationSpeakerBounds(normalized, _coordinator.CurrentSettings.DiarizationMaxSpeakers))
        {
            RejectInvalidDiarizationSpeakerBoundsChange(
                nameof(DiarizationMinSpeakers),
                _coordinator.CurrentSettings.DiarizationMinSpeakers);
            return;
        }

        _coordinator.CurrentSettings.DiarizationMinSpeakers = normalized;
        _coordinator.NotifySettingsModified();
    }

    partial void OnDiarizationMaxSpeakersChanged(decimal? value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        var normalized = NormalizeSpeakerCount(value);
        if (HasInvalidDiarizationSpeakerBounds(_coordinator.CurrentSettings.DiarizationMinSpeakers, normalized))
        {
            RejectInvalidDiarizationSpeakerBoundsChange(
                nameof(DiarizationMaxSpeakers),
                _coordinator.CurrentSettings.DiarizationMaxSpeakers);
            return;
        }

        _coordinator.CurrentSettings.DiarizationMaxSpeakers = normalized;
        _coordinator.NotifySettingsModified();
    }

    partial void OnDefaultTtsVoiceFallbackChanged(string value)
    {
        if (_parent.IsSynchronizingPipelineSettings)
            return;

        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var normalizedDisplayValue = normalized ?? string.Empty;
        if (!string.Equals(value, normalizedDisplayValue, StringComparison.Ordinal))
        {
            _parent.IsSynchronizingPipelineSettings = true;
            try
            {
                DefaultTtsVoiceFallback = normalizedDisplayValue;
            }
            finally
            {
                _parent.IsSynchronizingPipelineSettings = false;
            }
        }

        _coordinator.SetDefaultTtsVoiceFallback(normalized);
    }

    private void RebuildDiarizationProviderOptions()
    {
        var options = new List<string> { string.Empty };
        if (_coordinator.DiarizationRegistry is not null)
        {
            foreach (var providerId in _coordinator.DiarizationRegistry
                         .GetAvailableProviders()
                         .Where(provider => provider.IsImplemented)
                         .Select(provider => provider.Id))
            {
                if (!string.IsNullOrWhiteSpace(providerId) &&
                    !options.Contains(providerId, StringComparer.Ordinal))
                {
                    options.Add(providerId);
                }
            }
        }

        DiarizationProviderOptions = options;
    }

    private string NormalizeDiarizationProviderSelection(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        return DiarizationProviderOptions.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : string.Empty;
    }

    internal void UpdateSelectedSpeakerDetails(string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId))
        {
            SelectedSpeakerAssignedVoice = string.Empty;
            SelectedSpeakerReferenceAudioPath = string.Empty;
            return;
        }

        var voiceMap = _coordinator.GetSpeakerVoiceAssignments();
        var referenceMap = _coordinator.GetSpeakerReferenceAudioPaths();
        SelectedSpeakerAssignedVoice = voiceMap.TryGetValue(speakerId, out var voice) ? voice : string.Empty;
        SelectedSpeakerReferenceAudioPath = referenceMap.TryGetValue(speakerId, out var path) ? path : string.Empty;
    }

    private static int? NormalizeSpeakerCount(decimal? value)
    {
        if (!value.HasValue)
            return null;

        var rounded = (int)Math.Round(value.Value, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 1, 20);
    }

    private static bool HasInvalidDiarizationSpeakerBounds(int? minSpeakers, int? maxSpeakers) =>
        minSpeakers.HasValue && maxSpeakers.HasValue && minSpeakers.Value > maxSpeakers.Value;

    private void RejectInvalidDiarizationSpeakerBoundsChange(string propertyName, int? previousValue)
    {
        _parent.IsSynchronizingPipelineSettings = true;
        try
        {
            var previousDecimal = previousValue.HasValue ? (decimal?)previousValue.Value : null;
            if (string.Equals(propertyName, nameof(DiarizationMinSpeakers), StringComparison.Ordinal))
                DiarizationMinSpeakers = previousDecimal;
            else
                DiarizationMaxSpeakers = previousDecimal;
        }
        finally
        {
            _parent.IsSynchronizingPipelineSettings = false;
        }

        _parent.StatusText = "Diarization min speakers cannot be greater than max speakers.";
        _parent.ClearStatusErrorDetail();
    }
}
