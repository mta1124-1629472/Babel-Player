using System;
using Avalonia.Controls;
using Babel.Deck.Services;
using Babel.Deck.ViewModels;

namespace Babel.Deck.Views;

public partial class MainWindow : Window
{
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
            var sourcePlayer = vm.Coordinator.SourceMediaPlayer;
            if (sourcePlayer is LibMpvEmbeddedTransport embedded)
            {
                embedded.AttachToWindow(hwnd);
            }
        }
    }
}