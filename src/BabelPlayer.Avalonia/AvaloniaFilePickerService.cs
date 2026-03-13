using Avalonia.Controls;
using Avalonia.Platform.Storage;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class AvaloniaFilePickerService : IFilePickerService
{
    private static readonly IReadOnlyList<FilePickerFileType> MediaFileTypes =
    [
        new FilePickerFileType("Media files")
        {
            Patterns = ["*.mkv", "*.mp4", "*.avi", "*.mov", "*.m4v", "*.webm", "*.mp3", "*.wav", "*.flac", "*.ogg"]
        }
    ];

    private readonly Window _ownerWindow;

    public AvaloniaFilePickerService(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    public async Task<IReadOnlyList<string>> PickMediaFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!_ownerWindow.StorageProvider.CanOpen)
        {
            return [];
        }

        var files = await _ownerWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Video",
            AllowMultiple = false,
            FileTypeFilter = MediaFileTypes
        });

        cancellationToken.ThrowIfCancellationRequested();
        return files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToArray();
    }

    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        if (!_ownerWindow.StorageProvider.CanPickFolder)
        {
            return null;
        }

        var folders = await _ownerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Folder",
            AllowMultiple = false
        });

        cancellationToken.ThrowIfCancellationRequested();
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSubtitleFileAsync(CancellationToken cancellationToken = default)
    {
        if (!_ownerWindow.StorageProvider.CanOpen)
        {
            return null;
        }

        var files = await _ownerWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Subtitle File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitle files")
                {
                    Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt"]
                }
            ]
        });

        cancellationToken.ThrowIfCancellationRequested();
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickExecutableAsync(string title, string filterDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
    {
        if (!_ownerWindow.StorageProvider.CanOpen)
        {
            return null;
        }

        var files = await _ownerWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(filterDescription)
                {
                    Patterns = extensions.Select(extension => $"*{(extension.StartsWith('.') ? extension : "." + extension)}").ToArray()
                }
            ]
        });

        cancellationToken.ThrowIfCancellationRequested();
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task<string?> PickSaveFileAsync(string suggestedName, string fileTypeDescription, IReadOnlyList<string> extensions, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}
