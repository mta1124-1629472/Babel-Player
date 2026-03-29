using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Babel.Deck.Services;
using Babel.Deck.ViewModels;

namespace Babel.Deck.Views;

public partial class MainWindow : Window
{
    private LibMpvEmbeddedTransport? _embeddedTransport;

    public MainWindow()
    {
        InitializeComponent();

        var videoView = this.FindControl<MpvVideoView>("VideoView");
        if (videoView is not null)
        {
            videoView.HandleReady += OnVideoHandleReady;
        }
    }

    private void OnVideoHandleReady(object? sender, IntPtr hwnd)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                var player = vm.Coordinator.GetOrCreateSourcePlayer();
                if (player is LibMpvEmbeddedTransport embedded)
                {
                    _embeddedTransport = embedded;
                    embedded.AttachToWindow(hwnd);

                    // Resume prior session if media is already loaded
                    var ingestedPath = vm.Coordinator.CurrentSession.IngestedMediaPath;
                    if (!string.IsNullOrEmpty(ingestedPath) && System.IO.File.Exists(ingestedPath))
                    {
                        embedded.Load(ingestedPath);
                        embedded.Play();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Video init error: {ex.Message}");
            }
        }
    }

    public async void OnOpenMediaClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Media File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.avi", "*.webm", "*.mov" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
            }
        });

        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        if (DataContext is MainWindowViewModel vm)
        {
            try
            {
                vm.Coordinator.LoadMedia(path);

                if (_embeddedTransport is not null)
                {
                    var ingestedPath = vm.Coordinator.CurrentSession.IngestedMediaPath;
                    if (!string.IsNullOrEmpty(ingestedPath) && System.IO.File.Exists(ingestedPath))
                    {
                        _embeddedTransport.Load(ingestedPath);
                        _embeddedTransport.Play();
                    }
                }
            }
            catch (Exception ex)
            {
                vm.Playback.StatusText = $"Failed to open: {ex.Message}";
            }
        }
    }
}