using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public partial class MainWindow
{
    private readonly DispatcherTimer _fullscreenControlsHideTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    private readonly Dictionary<string, ResolvedShortcutBinding> _resolvedShortcutBindings = new(StringComparer.OrdinalIgnoreCase);

    private Border? _transportBarBorder;
    private Button? _fullscreenButton;
    private bool _windowModeInitialized;

    private void InitializeShortcutAndWindowModeControls()
    {
        _transportBarBorder ??= this.FindControl<Border>("TransportBarBorder");
        _fullscreenButton ??= this.FindControl<Button>("FullscreenButton");

        _fullscreenControlsHideTimer.Tick -= FullscreenControlsHideTimer_Tick;
        _fullscreenControlsHideTimer.Tick += FullscreenControlsHideTimer_Tick;

        KeyDown -= HandleWindowKeyDown;
        KeyDown += HandleWindowKeyDown;
        PointerMoved -= HandleWindowPointerMoved;
        PointerMoved += HandleWindowPointerMoved;

        if (_videoHost is not null)
        {
            _videoHost.MouseActivity -= HandleVideoHostMouseActivity;
            _videoHost.MouseActivity += HandleVideoHostMouseActivity;
            _videoHost.HostDoubleClicked -= HandleVideoHostDoubleClicked;
            _videoHost.HostDoubleClicked += HandleVideoHostDoubleClicked;
        }

        _shell.ShortcutProfileService.SnapshotChanged -= HandleShortcutProfileSnapshotChanged;
        _shell.ShortcutProfileService.SnapshotChanged += HandleShortcutProfileSnapshotChanged;

        RebuildShortcutBindings();
        UpdateWindowModeControls();
    }

    private void DisposeShortcutAndWindowModeControls()
    {
        _fullscreenControlsHideTimer.Stop();
        _fullscreenControlsHideTimer.Tick -= FullscreenControlsHideTimer_Tick;

        KeyDown -= HandleWindowKeyDown;
        PointerMoved -= HandleWindowPointerMoved;

        if (_videoHost is not null)
        {
            _videoHost.MouseActivity -= HandleVideoHostMouseActivity;
            _videoHost.HostDoubleClicked -= HandleVideoHostDoubleClicked;
        }

        _shell.ShortcutProfileService.SnapshotChanged -= HandleShortcutProfileSnapshotChanged;
    }

    private async Task EnsureWindowModeInitializedAsync()
    {
        if (_windowModeInitialized)
        {
            return;
        }

        _windowModeInitialized = true;
        await ApplyWindowModeAsync(_shell.ShellPreferencesService.Current.WindowMode, persistPreference: false);
    }

    private void HandleShortcutProfileSnapshotChanged(ShortcutProfileSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(RebuildShortcutBindings);
    }

    private void HandleWindowKeyDown(object? sender, KeyEventArgs e)
    {
        RegisterFullscreenControlsInteraction();

        if (ShouldIgnoreShortcutInput())
        {
            return;
        }

        if (!TryHandleShortcut(e.Key, e.KeyModifiers))
        {
            return;
        }

        e.Handled = true;
    }

    private void HandleWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        RegisterFullscreenControlsInteraction();
    }

    private void HandleVideoHostMouseActivity()
    {
        Dispatcher.UIThread.Post(RegisterFullscreenControlsInteraction);
    }

    private void HandleVideoHostDoubleClicked()
    {
        Dispatcher.UIThread.Post(() => _ = ToggleFullscreenAsync());
    }

    private void VideoSurfaceBorder_DoubleTapped(object? sender, TappedEventArgs e)
    {
        _ = ToggleFullscreenAsync();
        e.Handled = true;
    }

    private async void FullscreenButton_Click(object? sender, RoutedEventArgs e)
    {
        await ToggleFullscreenAsync();
    }

    private bool TryHandleShortcut(Key key, KeyModifiers modifiers)
    {
        if (!TryGetShortcutCommand(key, modifiers, out var commandId))
        {
            return false;
        }

        _ = ExecuteShortcutCommandAsync(commandId);
        return true;
    }

    private async Task ExecuteShortcutCommandAsync(string commandId)
    {
        var result = await _shell.ShortcutCommandExecutor.ExecuteAsync(commandId);
        if (result.RequiresOverlayInteraction)
        {
            RegisterFullscreenControlsInteraction();
        }

        if (result.UpdatedPreferences is not null)
        {
            ApplyPreferencesSnapshot(result.UpdatedPreferences);
        }

        switch (result.ShellAction)
        {
            case ShortcutShellAction.ToggleFullscreen:
                await ToggleFullscreenAsync();
                break;

            case ShortcutShellAction.ExitFullscreen:
                if (_shell.WindowModeService.CurrentMode == ShellPlaybackWindowMode.Fullscreen)
                {
                    await ApplyWindowModeAsync(ShellPlaybackWindowMode.Standard, persistPreference: true);
                }
                break;

            case ShortcutShellAction.TogglePictureInPicture:
                UpdateStatus("Picture-in-picture is not implemented in the Avalonia shell yet.");
                break;

            case ShortcutShellAction.ToggleSubtitleVisibility:
                CycleSubtitleRenderMode();
                break;
        }

        if (result.ItemToLoad is not null)
        {
            await OpenQueueItemAsync(result.ItemToLoad, result.StatusMessage ?? $"Now playing {result.ItemToLoad.DisplayName}.");
        }

        if (!string.IsNullOrWhiteSpace(result.StatusMessage))
        {
            UpdateStatus(result.StatusMessage);
        }
    }

    private bool TryGetShortcutCommand(Key key, KeyModifiers modifiers, out string commandId)
    {
        foreach (var binding in _resolvedShortcutBindings.Values)
        {
            if (!binding.Matches(key, modifiers))
            {
                continue;
            }

            commandId = binding.CommandId;
            return true;
        }

        commandId = string.Empty;
        return false;
    }

    private void RebuildShortcutBindings()
    {
        _resolvedShortcutBindings.Clear();
        foreach (var binding in _shell.ShortcutProfileService.Current.NormalizedBindings)
        {
            if (!TryResolveShortcutBinding(binding.CommandId, binding.NormalizedGesture, out var resolved))
            {
                continue;
            }

            _resolvedShortcutBindings[binding.CommandId] = resolved;
        }
    }

    private static bool TryResolveShortcutBinding(string commandId, string gestureText, out ResolvedShortcutBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(gestureText))
        {
            return false;
        }

        var tokens = gestureText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !TryParseShortcutKey(tokens[^1], out var key))
        {
            return false;
        }

        var modifiers = KeyModifiers.None;
        foreach (var modifier in tokens[..^1])
        {
            if (modifier.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || modifier.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Control;
            }
            else if (modifier.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                     || modifier.Equals("Menu", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Alt;
            }
            else if (modifier.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= KeyModifiers.Shift;
            }
            else
            {
                return false;
            }
        }

        binding = new ResolvedShortcutBinding(commandId, key, modifiers);
        return true;
    }

    private static bool TryParseShortcutKey(string keyToken, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(keyToken))
        {
            return false;
        }

        switch (keyToken.Trim())
        {
            case "Space":
                key = Key.Space;
                return true;
            case "Left":
                key = Key.Left;
                return true;
            case "Right":
                key = Key.Right;
                return true;
            case "Up":
                key = Key.Up;
                return true;
            case "Down":
                key = Key.Down;
                return true;
            case "Escape":
                key = Key.Escape;
                return true;
            case "PageUp":
                key = Key.PageUp;
                return true;
            case "PageDown":
                key = Key.PageDown;
                return true;
            case "OemMinus":
                key = Key.OemMinus;
                return true;
            case "OemPlus":
                key = Key.OemPlus;
                return true;
            case "OemComma":
                key = Key.OemComma;
                return true;
            case "OemPeriod":
                key = Key.OemPeriod;
                return true;
            case "D0":
                key = Key.D0;
                return true;
        }

        if (keyToken.Length == 1)
        {
            var upper = char.ToUpperInvariant(keyToken[0]).ToString();
            return Enum.TryParse(upper, ignoreCase: false, out key);
        }

        return Enum.TryParse(keyToken, ignoreCase: true, out key);
    }

    private bool ShouldIgnoreShortcutInput()
    {
        var focused = FocusManager?.GetFocusedElement();
        var current = focused as Visual;
        while (current is not null)
        {
            if (current is TextBox or ComboBox or Slider)
            {
                return true;
            }

            current = current.GetVisualParent();
        }

        return false;
    }

    private async Task ToggleFullscreenAsync()
    {
        var targetMode = _shell.WindowModeService.CurrentMode == ShellPlaybackWindowMode.Fullscreen
            ? ShellPlaybackWindowMode.Standard
            : ShellPlaybackWindowMode.Fullscreen;
        await ApplyWindowModeAsync(targetMode, persistPreference: true);
    }

    private async Task ApplyWindowModeAsync(ShellPlaybackWindowMode mode, bool persistPreference)
    {
        await _shell.WindowModeService.SetModeAsync(mode);

        if (persistPreference)
        {
            var current = _shell.ShellPreferencesService.Current;
            ApplyPreferencesSnapshot(_shell.ShellPreferencesService.ApplyLayoutChange(new ShellLayoutPreferencesChange(
                current.ShowBrowserPanel,
                current.ShowPlaylistPanel,
                mode)));
        }

        UpdateWindowModeControls();
        RegisterFullscreenControlsInteraction();
        SyncSubtitleOverlay();
    }

    private void UpdateWindowModeControls()
    {
        if (_fullscreenButton is not null)
        {
            _fullscreenButton.Content = _shell.WindowModeService.CurrentMode == ShellPlaybackWindowMode.Fullscreen
                ? "Exit Fullscreen"
                : "Fullscreen";
        }

        if (_shell.WindowModeService.CurrentMode == ShellPlaybackWindowMode.Fullscreen)
        {
            SetTransportControlsVisible(true);
            RestartFullscreenControlsHideTimer();
        }
        else
        {
            _fullscreenControlsHideTimer.Stop();
            SetTransportControlsVisible(true);
        }
    }

    private void RegisterFullscreenControlsInteraction()
    {
        if (_shell.WindowModeService.CurrentMode != ShellPlaybackWindowMode.Fullscreen)
        {
            _fullscreenControlsHideTimer.Stop();
            SetTransportControlsVisible(true);
            return;
        }

        SetTransportControlsVisible(true);
        RestartFullscreenControlsHideTimer();
    }

    private void RestartFullscreenControlsHideTimer()
    {
        _fullscreenControlsHideTimer.Stop();
        _fullscreenControlsHideTimer.Start();
    }

    private void FullscreenControlsHideTimer_Tick(object? sender, EventArgs e)
    {
        if (_shell.WindowModeService.CurrentMode != ShellPlaybackWindowMode.Fullscreen)
        {
            _fullscreenControlsHideTimer.Stop();
            return;
        }

        if ((_transportBarBorder?.IsPointerOver).GetValueOrDefault()
            || (_timelineSlider?.IsPointerOver).GetValueOrDefault()
            || (_volumeSlider?.IsPointerOver).GetValueOrDefault())
        {
            RestartFullscreenControlsHideTimer();
            return;
        }

        SetTransportControlsVisible(false);
    }

    private void SetTransportControlsVisible(bool visible)
    {
        if (_transportBarBorder is null)
        {
            return;
        }

        _transportBarBorder.IsVisible = visible || _shell.WindowModeService.CurrentMode != ShellPlaybackWindowMode.Fullscreen;
    }

    private void CycleSubtitleRenderMode()
    {
        var currentMode = _shell.ShellPreferencesService.Current.SubtitleRenderMode;
        var nextMode = currentMode switch
        {
            ShellSubtitleRenderMode.Off => ShellSubtitleRenderMode.SourceOnly,
            ShellSubtitleRenderMode.SourceOnly => ShellSubtitleRenderMode.TranslationOnly,
            ShellSubtitleRenderMode.TranslationOnly => ShellSubtitleRenderMode.Dual,
            ShellSubtitleRenderMode.Dual => ShellSubtitleRenderMode.Off,
            _ => ShellSubtitleRenderMode.TranslationOnly
        };

        ApplySubtitleRenderMode(nextMode);
    }

    private readonly record struct ResolvedShortcutBinding(string CommandId, Key Key, KeyModifiers Modifiers)
    {
        public bool Matches(Key key, KeyModifiers modifiers)
        {
            var normalizedModifiers = modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);
            return key == Key && normalizedModifiers == Modifiers;
        }
    }
}
