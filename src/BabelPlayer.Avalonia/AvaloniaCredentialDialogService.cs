using Avalonia.Controls;
using Avalonia.Threading;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class AvaloniaCredentialDialogService : ICredentialDialogService
{
    public AvaloniaCredentialDialogService(Window ownerWindow)
    {
        OwnerWindow = ownerWindow;
    }

    public Window OwnerWindow { get; }

    public async Task<string?> PromptForApiKeyAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ApiKeyPromptWindow(title, message, submitButtonText);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(OwnerWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Result;
    }

    public async Task<(string ApiKey, string Region)?> PromptForApiKeyWithRegionAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ApiKeyWithRegionPromptWindow(title, message, submitButtonText);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(OwnerWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Result;
    }

    public async Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new LlamaCppBootstrapPromptWindow(title, message);
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        await dialog.ShowDialog(OwnerWindow);
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Choice;
    }

    public Task<ShellShortcutProfile?> EditShortcutsAsync(ShellShortcutProfile currentProfile, CancellationToken cancellationToken = default)
        => Task.FromResult<ShellShortcutProfile?>(null);
}
