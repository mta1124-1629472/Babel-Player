using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace BabelPlayer.WinUI;

public sealed partial class MainWindow
{
    private async void ImportSubtitle_Click(object sender, RoutedEventArgs e)
    {
        var subtitlePath = await _filePickerService.PickSubtitleFileAsync();
        if (string.IsNullOrWhiteSpace(subtitlePath))
        {
            return;
        }

        try
        {
            var result = await _subtitleWorkflowService.ImportExternalSubtitlesAsync(subtitlePath, autoLoaded: false);
            ShowStatus($"Imported {Path.GetFileName(subtitlePath)} with {result.CueCount} cues.");
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, true);
        }
    }

    private void SubtitleVisibilityToggleButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSubtitleVisibility();
    }

    private void OverlaySubtitleToggleButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterFullscreenOverlayInteraction();
        ToggleSubtitleVisibility();
    }

    private void ToggleSubtitleVisibility()
    {
        var currentMode = GetEffectiveSubtitleRenderMode();
        var result = _subtitleWorkflowService.ToggleSubtitleVisibility(ViewModel.Settings.SubtitleRenderMode);
        var subtitlesEnabled = currentMode != SubtitleRenderMode.Off;
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplySubtitlePresentationChange(
            new ShellSubtitlePresentationChange(result.RequestedRenderMode, ViewModel.Settings.SubtitleStyle)));

        UpdateSubtitleVisibility();
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateOverlayControlState();
        ShowStatus(subtitlesEnabled ? "Subtitles hidden." : "Subtitles shown.");
    }

    private SubtitleRenderMode GetEffectiveSubtitleRenderMode()
    {
        return _subtitleWorkflowService.GetEffectiveRenderMode(ViewModel.Settings.SubtitleRenderMode);
    }

    private void SubtitleModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents)
        {
            return;
        }

        if (SubtitleModeComboBox.SelectedItem is not ComboBoxItem { Tag: SubtitleRenderMode mode })
        {
            return;
        }

        ApplySubtitleRenderMode(mode);
    }

    private async void TranscriptionModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents)
        {
            return;
        }

        if (TranscriptionModelComboBox.SelectedItem is not TranscriptionModelSelection selection)
        {
            return;
        }

        await PrepareForTranscriptionRefreshAsync();
        var applied = await _subtitleWorkflowService.SelectTranscriptionModelAsync(selection.Key);
        if (!applied)
        {
            ApplyWorkflowSnapshot(_subtitleWorkflowService.Current);
        }
    }

    private async void TranslationModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorkflowControlEvents)
        {
            return;
        }

        if (TranslationModelComboBox.SelectedItem is not TranslationModelSelection selection)
        {
            return;
        }

        var applied = await _subtitleWorkflowService.SelectTranslationModelAsync(selection.Key);
        if (!applied)
        {
            ApplyWorkflowSnapshot(_subtitleWorkflowService.Current);
        }
    }

    private async void TranslationToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || TranslationToggleSwitch is null)
        {
            return;
        }

        await _subtitleWorkflowService.SetTranslationEnabledAsync(TranslationToggleSwitch.IsOn);
    }

    private async void AutoTranslateToggleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWorkflowControlEvents || AutoTranslateToggleSwitch is null)
        {
            return;
        }

        await _subtitleWorkflowService.SetAutoTranslateEnabledAsync(AutoTranslateToggleSwitch.IsOn);
    }

    private void SubtitleWorkflow_StatusChanged(string message)
    {
        DispatcherQueue.TryEnqueue(() => ShowStatus(message));
    }

    private void SubtitleWorkflow_SnapshotChanged(SubtitleWorkflowSnapshot snapshot)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            UpdateWorkflowDiagnostics(snapshot);
            ApplyWorkflowSnapshot(snapshot);
            await ApplyCaptionStartupGateAsync(snapshot);
        });
    }

    private void ApplyWorkflowSnapshot(SubtitleWorkflowSnapshot snapshot)
    {
        _suppressWorkflowControlEvents = true;
        try
        {
            ViewModel.SelectedTranscriptionLabel = snapshot.SelectedTranscriptionLabel;
            ViewModel.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;
            ViewModel.IsTranslationEnabled = snapshot.IsTranslationEnabled;
            ViewModel.IsAutoTranslateEnabled = snapshot.AutoTranslateEnabled;
            ViewModel.SubtitleSource = snapshot.SubtitleSource;
            ViewModel.IsCaptionGenerationInProgress = snapshot.IsCaptionGenerationInProgress;
            ViewModel.SubtitleOverlay.SelectedTranscriptionLabel = snapshot.SelectedTranscriptionLabel;
            ViewModel.SubtitleOverlay.SelectedTranslationLabel = snapshot.SelectedTranslationLabel;

            if (TranscriptionModelComboBox is not null)
            {
                TranscriptionModelComboBox.ItemsSource = snapshot.AvailableTranscriptionModels;
                TranscriptionModelComboBox.SelectedItem = snapshot.AvailableTranscriptionModels
                    .FirstOrDefault(item => string.Equals(item.Key, snapshot.SelectedTranscriptionModelKey, StringComparison.Ordinal));
            }

            if (TranslationModelComboBox is not null)
            {
                TranslationModelComboBox.ItemsSource = snapshot.AvailableTranslationModels;
                TranslationModelComboBox.SelectedItem = snapshot.AvailableTranslationModels
                    .FirstOrDefault(item => string.Equals(item.Key, snapshot.SelectedTranslationModelKey, StringComparison.Ordinal));
                TranslationModelComboBox.IsEnabled = snapshot.IsTranslationEnabled;
                TranslationModelComboBox.Opacity = snapshot.IsTranslationEnabled ? 1 : 0.55;
            }

            if (TranslationToggleSwitch is not null)
            {
                TranslationToggleSwitch.IsOn = snapshot.IsTranslationEnabled;
            }

            if (AutoTranslateToggleSwitch is not null)
            {
                AutoTranslateToggleSwitch.IsOn = snapshot.AutoTranslateEnabled;
            }

            if (ExportCurrentSubtitlesFlyoutItem is not null)
            {
                ExportCurrentSubtitlesFlyoutItem.IsEnabled = snapshot.Cues.Count > 0;
            }

            UpdateSubtitleRenderModeFlyoutChecks(snapshot);
        }
        finally
        {
            _suppressWorkflowControlEvents = false;
        }

        UpdateSubtitleOverlay(snapshot);
    }

    private void UpdateSubtitleRenderModeFlyoutChecks(SubtitleWorkflowSnapshot? snapshot = null)
    {
        var checkedMode = GetEffectiveSubtitleRenderMode();
        if (SubtitleModeComboBox is not null)
        {
            foreach (var item in SubtitleModeComboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is SubtitleRenderMode mode && mode == checkedMode)
                {
                    _suppressWorkflowControlEvents = true;
                    SubtitleModeComboBox.SelectedItem = item;
                    _suppressWorkflowControlEvents = false;
                    break;
                }
            }
        }
    }

    private void RebuildAudioTrackFlyout()
    {
        if (AudioTracksFlyoutSubItem is null)
        {
            return;
        }

        PopulateAudioTrackFlyout(AudioTracksFlyoutSubItem);
    }

    private void PopulateAudioTrackFlyout(MenuFlyoutSubItem audioTracksFlyoutSubItem)
    {
        audioTracksFlyoutSubItem.Items.Clear();
        var audioTracks = _currentTracks
            .Where(track => track.Kind == MediaTrackKind.Audio)
            .OrderBy(track => track.Id)
            .ToList();
        if (audioTracks.Count == 0)
        {
            audioTracksFlyoutSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No alternate tracks",
                IsEnabled = false
            });
            return;
        }

        foreach (var track in audioTracks)
        {
            audioTracksFlyoutSubItem.Items.Add(CreateTrackFlyoutItem(track, AudioTrackFlyoutItem_Click));
        }
    }

    private void RebuildEmbeddedSubtitleTrackFlyout()
    {
        if (EmbeddedSubtitleTracksFlyoutSubItem is null)
        {
            return;
        }

        PopulateEmbeddedSubtitleTrackFlyout(EmbeddedSubtitleTracksFlyoutSubItem);
    }

    private void PopulateEmbeddedSubtitleTrackFlyout(MenuFlyoutSubItem embeddedSubtitleTracksFlyoutSubItem)
    {
        embeddedSubtitleTracksFlyoutSubItem.Items.Clear();
        var hasSelectedEmbeddedTrack = _currentTracks.Any(track => track.Kind == MediaTrackKind.Subtitle && track.IsSelected);
        var offItem = new ToggleMenuFlyoutItem
        {
            Text = "Off",
            Tag = "off",
            IsChecked = !hasSelectedEmbeddedTrack
        };
        offItem.Click += EmbeddedSubtitleTrackFlyoutItem_Click;
        embeddedSubtitleTracksFlyoutSubItem.Items.Add(offItem);

        var subtitleTracks = _currentTracks
            .Where(track => track.Kind == MediaTrackKind.Subtitle)
            .OrderBy(track => track.Id)
            .ToList();
        if (subtitleTracks.Count == 0)
        {
            embeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
            embeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutItem
            {
                Text = "No embedded subtitle tracks",
                IsEnabled = false
            });
            return;
        }

        embeddedSubtitleTracksFlyoutSubItem.Items.Add(new MenuFlyoutSeparator());
        foreach (var track in subtitleTracks)
        {
            embeddedSubtitleTracksFlyoutSubItem.Items.Add(CreateTrackFlyoutItem(track, EmbeddedSubtitleTrackFlyoutItem_Click));
        }
    }

    private MenuFlyoutItem CreateTrackFlyoutItem(MediaTrackInfo track, RoutedEventHandler clickHandler)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = FormatTrackLabel(track),
            Tag = track.Id,
            IsChecked = track.IsSelected
        };
        item.Click += clickHandler;
        return item;
    }

    private void ApplySubtitleStyleSettings()
    {
        var style = ViewModel.Settings.SubtitleStyle;
        SourceSubtitleTextBlock.FontSize = style.SourceFontSize;
        SourceSubtitleTextBlock.LineHeight = Math.Max(style.SourceFontSize * 1.3, style.SourceFontSize + 4);
        SourceSubtitleTextBlock.Margin = new Thickness(0, 0, 0, style.DualSpacing);
        SourceSubtitleTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.SourceForegroundHex, ColorHelper.FromArgb(255, 241, 246, 251)));

        TranslatedSubtitleTextBlock.FontSize = style.TranslationFontSize;
        TranslatedSubtitleTextBlock.LineHeight = Math.Max(style.TranslationFontSize * 1.25, style.TranslationFontSize + 4);
        TranslatedSubtitleTextBlock.Foreground = new SolidColorBrush(ParseHexColor(style.TranslationForegroundHex, Colors.White));

        var overlayAlpha = (byte)Math.Clamp(Math.Round(style.BackgroundOpacity * 255), 0, 255);
        SubtitleOverlayBorder.Background = new SolidColorBrush(ColorHelper.FromArgb(overlayAlpha, 18, 23, 32));
        SubtitleOverlayBorder.Margin = new Thickness(0, 0, 0, style.BottomMargin);
        _subtitlePresenter.ApplyStyle(style);
    }

    private void UpdateSubtitleStyle(Func<SubtitleStyleSettings, SubtitleStyleSettings> updater, string statusMessage)
    {
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplySubtitlePresentationChange(
            new ShellSubtitlePresentationChange(
                ViewModel.Settings.SubtitleRenderMode,
                updater(ViewModel.Settings.SubtitleStyle))));

        ApplySubtitleStyleSettings();
        UpdateSubtitleVisibility();
        ShowStatus(statusMessage);
    }

    private static string FormatTrackLabel(MediaTrackInfo track)
    {
        var language = string.IsNullOrWhiteSpace(track.Language) ? "und" : track.Language.ToUpperInvariant();
        var label = string.IsNullOrWhiteSpace(track.Title)
            ? $"{language} ┬╖ Track {track.Id}"
            : $"{track.Title} ({language})";

        if (track.Kind == MediaTrackKind.Subtitle && !track.IsTextBased)
        {
            label += " ┬╖ image-based";
        }

        return label;
    }

    private void ApplyTrackSelection(MediaTrackKind kind, int? selectedTrackId)
    {
        for (var index = 0; index < _currentTracks.Count; index++)
        {
            var track = _currentTracks[index];
            if (track.Kind != kind)
            {
                continue;
            }

            _currentTracks[index] = new MediaTrackInfo
            {
                Id = track.Id,
                FfIndex = track.FfIndex,
                Kind = track.Kind,
                Title = track.Title,
                Language = track.Language,
                Codec = track.Codec,
                IsEmbedded = track.IsEmbedded,
                IsSelected = selectedTrackId is not null && track.Id == selectedTrackId.Value,
                IsTextBased = track.IsTextBased
            };
        }
    }

    private void SubtitleRenderModeFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: SubtitleRenderMode mode })
        {
            return;
        }

        ApplySubtitleRenderMode(mode);
    }

    private void ApplySubtitleRenderMode(SubtitleRenderMode mode)
    {
        var result = _subtitleWorkflowService.SelectRenderMode(mode, ViewModel.Settings.SubtitleRenderMode);
        ApplyPreferencesSnapshot(_shellPreferenceCommands.ApplySubtitlePresentationChange(
            new ShellSubtitlePresentationChange(result.RequestedRenderMode, ViewModel.Settings.SubtitleStyle)));
        UpdateSubtitleRenderModeFlyoutChecks();
        UpdateSubtitleVisibility();
        UpdateOverlayControlState();
        ShowStatus($"Subtitle mode: {FormatSubtitleRenderModeLabel(result.EffectiveRenderMode)}.");
    }

    private void AudioTrackFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: int trackId, Text: string label })
        {
            return;
        }

        FireAndForget(_shellPlaybackCommands.SetAudioTrackAsync(trackId));
        ApplyTrackSelection(MediaTrackKind.Audio, trackId);
        RebuildAudioTrackFlyout();
        ShowStatus($"Selected audio track: {label}.");
    }

    private async void EmbeddedSubtitleTrackFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem item)
        {
            return;
        }

        MediaTrackInfo? track = null;
        if (item.Tag is int trackId)
        {
            track = _currentTracks.FirstOrDefault(candidate => candidate.Kind == MediaTrackKind.Subtitle && candidate.Id == trackId);
            if (track is null)
            {
                return;
            }
        }
        else if (item.Tag is not string offValue || offValue != "off")
        {
            return;
        }

        var result = await _shellPlaybackCommands.SelectEmbeddedSubtitleTrackAsync(
            _shellPlaybackCommands.CurrentPlaybackSnapshot.Path,
            ViewModel.SubtitleSource,
            track);

        if (result.TrackSelectionChanged)
        {
            ApplyTrackSelection(MediaTrackKind.Subtitle, result.SelectedSubtitleTrackId);
            RebuildEmbeddedSubtitleTrackFlyout();
        }

        ShowStatus(result.StatusMessage, result.IsError);
    }

    private void IncreaseSubtitleFont_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            SourceFontSize = Math.Min(style.SourceFontSize + 2, 44),
            TranslationFontSize = Math.Min(style.TranslationFontSize + 2, 48)
        },
        "Subtitle text size increased.");

    private void DecreaseSubtitleFont_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            SourceFontSize = Math.Max(style.SourceFontSize - 2, 18),
            TranslationFontSize = Math.Max(style.TranslationFontSize - 2, 20)
        },
        "Subtitle text size decreased.");

    private void IncreaseSubtitleBackground_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BackgroundOpacity = Math.Min(style.BackgroundOpacity + 0.08, 0.95)
        },
        "Subtitle background increased.");

    private void DecreaseSubtitleBackground_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BackgroundOpacity = Math.Max(style.BackgroundOpacity - 0.08, 0.15)
        },
        "Subtitle background decreased.");

    private void RaiseSubtitles_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BottomMargin = Math.Min(style.BottomMargin + 10, 80)
        },
        "Subtitles raised.");

    private void LowerSubtitles_Click(object sender, RoutedEventArgs e) => UpdateSubtitleStyle(
        style => style with
        {
            BottomMargin = Math.Max(style.BottomMargin - 10, 0)
        },
        "Subtitles lowered.");

    private void TranslationColorFlyoutItem_Click(object sender, RoutedEventArgs e)
    {
        string? colorHex = null;
        string? label = null;
        switch (sender)
        {
            case MenuFlyoutItem { Tag: string menuColorHex, Text: string menuLabel }:
                colorHex = menuColorHex;
                label = menuLabel;
                break;
            case Button { Tag: string buttonColorHex, Content: string buttonLabel }:
                colorHex = buttonColorHex;
                label = buttonLabel;
                break;
        }

        if (string.IsNullOrWhiteSpace(colorHex) || string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        UpdateSubtitleStyle(
            style => style with
            {
                TranslationForegroundHex = colorHex
            },
            $"Translation color: {label}.");
    }

    private async void ExportCurrentSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (!_subtitleWorkflowService.HasCurrentCues)
        {
            ShowStatus("No subtitles available to export.");
            return;
        }

        var exportPath = await _filePickerService.PickSaveFileAsync(
            "translated-subtitles",
            "SubRip subtitles",
            [".srt"]);
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            return;
        }

        _subtitleWorkflowService.ExportCurrentSubtitles(exportPath);
        ShowStatus($"Exported subtitles: {Path.GetFileName(exportPath)}");
    }

    private void UpdateSubtitleOverlay(SubtitleWorkflowSnapshot snapshot)
    {
        var presentation = _subtitleWorkflowService.GetOverlayPresentation(
            ViewModel.Settings.SubtitleRenderMode,
            subtitlesVisible: ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off);
        ViewModel.SubtitleOverlay.ShowSource = !string.IsNullOrWhiteSpace(presentation.SecondaryText);
        ViewModel.SubtitleOverlay.SourceText = presentation.SecondaryText;
        ViewModel.SubtitleOverlay.TranslationText = presentation.PrimaryText;
        UpdateSubtitleVisibility();
    }

    private void UpdateSubtitleVisibility()
    {
        var subtitlesEnabled = ViewModel.Settings.SubtitleRenderMode != SubtitleRenderMode.Off;
        var showSource = subtitlesEnabled && ViewModel.SubtitleOverlay.ShowSource && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.SourceText);
        var showPrimary = subtitlesEnabled && !string.IsNullOrWhiteSpace(ViewModel.SubtitleOverlay.TranslationText);

        SourceSubtitleTextBlock.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        SourceSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.SourceText;

        TranslatedSubtitleTextBlock.Visibility = showPrimary ? Visibility.Visible : Visibility.Collapsed;
        TranslatedSubtitleTextBlock.Text = ViewModel.SubtitleOverlay.TranslationText;
        SubtitleOverlayBorder.Visibility = Visibility.Collapsed;
        _stageCoordinator.PresentSubtitles(
            new SubtitlePresentationModel
            {
                IsVisible = showSource || showPrimary,
                PrimaryText = showPrimary ? ViewModel.SubtitleOverlay.TranslationText : string.Empty,
                SecondaryText = showSource ? ViewModel.SubtitleOverlay.SourceText : string.Empty
            },
            ViewModel.Settings.SubtitleStyle,
            !string.IsNullOrWhiteSpace(_currentShellProjection.Transport.Path));
        UpdateOverlayControlState();
    }

    private async Task PrepareForTranscriptionRefreshAsync()
    {
        var result = await _shellPlaybackCommands.PrepareForTranscriptionRefreshAsync(
            _subtitleWorkflowService.Current,
            _shellPlaybackCommands.CurrentPlaybackSnapshot,
            CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ViewModel.Transport.IsPaused = true;
            UpdatePlayPauseButtonVisual();
            UpdateOverlayControlState();
            ShowStatus(result.StatusMessage);
        }
    }

    private async Task ApplyCaptionStartupGateAsync(SubtitleWorkflowSnapshot snapshot)
    {
        if (GetEffectiveSubtitleRenderMode() == SubtitleRenderMode.Off)
        {
            return;
        }

        var result = await _shellPlaybackCommands.EvaluateCaptionStartupGateAsync(
            snapshot,
            _shellPlaybackCommands.CurrentPlaybackSnapshot,
            CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            ShowStatus(result.StatusMessage);
        }
        UpdatePlayPauseButtonVisual();
        UpdateOverlayControlState();
    }

    private static string FormatSubtitleRenderModeLabel(SubtitleRenderMode mode)
    {
        return mode switch
        {
            SubtitleRenderMode.Off => "off",
            SubtitleRenderMode.SourceOnly => "source only",
            SubtitleRenderMode.TranslationOnly => "translation only",
            SubtitleRenderMode.Dual => "dual",
            _ => "translation only"
        };
    }

    private static string FormatSubtitleRenderModeButtonLabel(SubtitleRenderMode mode)
    {
        return mode switch
        {
            SubtitleRenderMode.Off => "Off",
            SubtitleRenderMode.SourceOnly => "Source",
            SubtitleRenderMode.TranslationOnly => "Translation",
            SubtitleRenderMode.Dual => "Dual",
            _ => "Translation"
        };
    }

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var value = hex.Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
        {
            value = value[1..];
        }

        try
        {
            return value.Length switch
            {
                6 => ColorHelper.FromArgb(
                    255,
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16)),
                8 => ColorHelper.FromArgb(
                    Convert.ToByte(value[..2], 16),
                    Convert.ToByte(value.Substring(2, 2), 16),
                    Convert.ToByte(value.Substring(4, 2), 16),
                    Convert.ToByte(value.Substring(6, 2), 16)),
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }
}
