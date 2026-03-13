using Avalonia.Controls;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public sealed class AvaloniaCredentialDialogService : ICredentialDialogService
{
    public AvaloniaCredentialDialogService(Window ownerWindow)
    {
        OwnerWindow = ownerWindow;
    }

    public Window OwnerWindow { get; }

    public Task<string?> PromptForApiKeyAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<(string ApiKey, string Region)?> PromptForApiKeyWithRegionAsync(string title, string message, string submitButtonText, CancellationToken cancellationToken = default)
        => Task.FromResult<(string ApiKey, string Region)?>(null);

    public Task<LlamaCppBootstrapChoice> PromptForLlamaCppBootstrapChoiceAsync(string title, string message, CancellationToken cancellationToken = default)
        => Task.FromResult(LlamaCppBootstrapChoice.Cancel);

    public Task<ShellShortcutProfile?> EditShortcutsAsync(ShellShortcutProfile currentProfile, CancellationToken cancellationToken = default)
        => Task.FromResult<ShellShortcutProfile?>(null);
}
