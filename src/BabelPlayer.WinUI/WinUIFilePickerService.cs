using System.Linq;
using BabelPlayer.App;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BabelPlayer.WinUI;

public sealed class WinUIFilePickerService : IFilePickerService
{
    private readonly Window _window;

    public WinUIFilePickerService(Window window)
    {
        _window = window;
    }

    public async Task<IReadOnlyList<string>> PickMediaFilesAsync(CancellationToken cancellationToken = default)
    {
        var picker = new FileOpenPicker
        {
            CommitButtonText = "Open"
        };

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        foreach (var extension in new[] { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".mp3", ".wav", ".flac", ".ogg" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var files = await picker.PickMultipleFilesAsync();
        return files.Select(file => file.Path).ToList();
    }

    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var picker = new FolderPicker
        {
            CommitButtonText = "Select folder"
        };

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        picker.FileTypeFilter.Add("*");
        cancellationToken.ThrowIfCancellationRequested();
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default)
    {
        var picker = new FileOpenPicker
        {
            CommitButtonText = "Import subtitles"
        };

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        foreach (var extension in new[] { ".srt", ".vtt", ".ass", ".ssa" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickExecutableAsync(string title, string filterDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
    {
        var picker = new FileOpenPicker
        {
            CommitButtonText = title
        };

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        foreach (var extension in extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string fileTypeDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedName
        };

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        picker.FileTypeChoices.Add(fileTypeDescription, extensions.ToList());
        cancellationToken.ThrowIfCancellationRequested();
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
