using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input.Platform;
using Babel.Player.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels.Dev;

/// <summary>
/// Backs the <see cref="Views.Dev.DevLogWindow"/>.
/// Reads the on-disk log file on demand and exposes line-level filtering.
/// </summary>
public partial class DevLogViewModel : ObservableObject, IDisposable
{
    private readonly AppLog _log;
    private readonly IClipboard? _clipboard;

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _rawContent = string.Empty;

    public DevLogViewModel(AppLog log, IClipboard? clipboard)
    {
        _log = log;
        _clipboard = clipboard;
    }

    public ObservableCollection<string> Lines { get; } = [];

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            var all = File.ReadAllText(_log.LogFilePath, Encoding.UTF8);
            RawContent = all;
            Lines.Clear();
            foreach (var line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(FilterText) ||
                    line.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                    Lines.Add(line);
            }
        }
        catch (Exception ex)
        {
            Lines.Clear();
            Lines.Add($"[DevLog] Failed to read log: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CopyAll()
    {
        if (_clipboard is not null)
            await ClipboardExtensions.SetTextAsync(_clipboard, RawContent);
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        var dir = Path.GetDirectoryName(_log.LogFilePath);
        if (dir is not null && Directory.Exists(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
    }

    partial void OnFilterTextChanged(string value) => Refresh();

    public void Dispose() { }
}
