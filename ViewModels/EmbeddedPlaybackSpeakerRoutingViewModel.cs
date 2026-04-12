using System;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace Babel.Player.ViewModels;

public sealed class EmbeddedPlaybackSpeakerRoutingViewModel : ViewModelBase
{
    private readonly EmbeddedPlaybackViewModel _parent;
    private readonly SessionWorkflowCoordinator _coordinator;

    internal EmbeddedPlaybackSpeakerRoutingViewModel(
        EmbeddedPlaybackViewModel parent,
        SessionWorkflowCoordinator coordinator)
    {
        _parent = parent;
        _coordinator = coordinator;
    }

    public async Task AssignSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(_parent.SelectedSpeakerId) || string.IsNullOrWhiteSpace(_parent.TtsModelOrVoice))
            return;

        _coordinator.SetSpeakerVoiceAssignment(_parent.SelectedSpeakerId, _parent.TtsModelOrVoice);
        _parent.StatusText = $"Assigned {_parent.TtsModelOrVoice} to {_parent.SelectedSpeakerId}.";
        _parent.UpdateSelectedSpeakerDetails(_parent.SelectedSpeakerId);
        await _parent.RefreshSegmentsAsync();
    }

    public async Task ClearSelectedSpeakerVoiceAsync()
    {
        if (string.IsNullOrWhiteSpace(_parent.SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerVoiceAssignment(_parent.SelectedSpeakerId);
        _parent.StatusText = $"Cleared voice assignment for {_parent.SelectedSpeakerId}.";
        _parent.UpdateSelectedSpeakerDetails(_parent.SelectedSpeakerId);
        await _parent.RefreshSegmentsAsync();
    }

    public async Task SetReferenceAudioForSelectedSpeakerAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(_parent.SelectedSpeakerId) || string.IsNullOrWhiteSpace(path))
            return;

        _coordinator.SetSpeakerReferenceAudioPath(_parent.SelectedSpeakerId, path);
        _parent.StatusText = $"Set reference audio for {_parent.SelectedSpeakerId}.";
        _parent.UpdateSelectedSpeakerDetails(_parent.SelectedSpeakerId);
        await _parent.RefreshSegmentsAsync();
    }

    public async Task ClearSelectedSpeakerReferenceAudioAsync()
    {
        if (string.IsNullOrWhiteSpace(_parent.SelectedSpeakerId))
            return;

        _coordinator.RemoveSpeakerReferenceAudioPath(_parent.SelectedSpeakerId);
        _parent.StatusText = $"Cleared reference audio for {_parent.SelectedSpeakerId}.";
        _parent.UpdateSelectedSpeakerDetails(_parent.SelectedSpeakerId);
        await _parent.RefreshSegmentsAsync();
    }
}
