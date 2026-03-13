using Avalonia.Controls;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class AvaloniaCredentialDialogService : ICredentialDialogService
{
    private readonly Window _ownerWindow;

    public AvaloniaCredentialDialogService(Window ownerWindow)
    {
        _ownerWindow = ownerWindow;
    }

    public Task<string?> PromptForApiKeyAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<(string ApiKey, string Region)?> PromptForApiKeyWithRegionAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<(string ApiKey, string Region)?>(null);
    }

    public Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LlamaCppBootstrapChoice.Cancel);
    }

    public Task<ShellShortcutProfile?> EditShortcutsAsync(ShellShortcutProfile currentProfile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ShellShortcutProfile?>(currentProfile);
    }
}
