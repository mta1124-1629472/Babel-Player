using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

/// <summary>ViewModel for the API Keys dialog, containing one entry per known provider.</summary>
public sealed class ApiKeysViewModel(ApiKeyStore store, ApiKeyValidationService? validationService = null) : ViewModelBase
{
    public IReadOnlyList<ApiKeyEntryViewModel> Entries { get; } = [.. ApiKeyStore.KnownProviders
            .Select(key => new ApiKeyEntryViewModel(key, store, validationService))];

    public string StorageProviderName { get; } = store.StorageProviderName;

    public string SecurityStatusDetail => $"{StorageProviderName}. Your keys are stored locally and never leave this machine.";
}



/// <summary>ViewModel for a single provider row in the API Keys dialog.</summary>
public sealed partial class ApiKeyEntryViewModel(string providerKey, ApiKeyStore store, ApiKeyValidationService? validationService = null) : ViewModelBase
{
    private readonly string _validationAvailabilityText = validationService?.GetAvailabilityMessage(providerKey) ?? string.Empty;

    public string ProviderKey { get; } = providerKey;
    public string ProviderDisplayName { get; } = ApiKeyStore.GetDisplayName(providerKey);


    [ObservableProperty]
    private string _keyValue = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskChar))]
    [NotifyPropertyChangedFor(nameof(RevealIcon))]
    private bool _isRevealed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusDotColor))]
    [NotifyPropertyChangedFor(nameof(StatusTextColor))]
    [NotifyPropertyChangedFor(nameof(PlaceholderText))]
    private bool _isKeySet = store.HasKey(providerKey);


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidateButtonText))]
    [NotifyPropertyChangedFor(nameof(CanValidateLive))]
    private bool _isValidating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationText))]
    [NotifyPropertyChangedFor(nameof(ValidationText))]
    [NotifyPropertyChangedFor(nameof(ValidationTextColor))]
    private string _validationResultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationTextColor))]
    private bool _isValidationSuccess;

    /// <summary>Character used to mask the TextBox. '\0' means no masking (plaintext).</summary>
    public char MaskChar      => IsRevealed ? '\0' : '●';
    public string RevealIcon  => IsRevealed ? "🙈" : "👁";
    public string StatusText  => IsKeySet ? "Saved" : "Not set";
    public string StatusDotColor  => IsKeySet ? "#22C55E" : "#404058";
    public string StatusTextColor => IsKeySet ? "#22C55E" : "#606070";
    public string PlaceholderText => IsKeySet ? "Key saved — paste to replace" : "Paste key here…";

    public bool CanValidateLive => string.IsNullOrEmpty(_validationAvailabilityText) && !IsValidating;

    public string ValidateButtonText => IsValidating ? "Validating…" : "Validate";

    public bool HasValidationText => !string.IsNullOrWhiteSpace(ValidationText);

    public string ValidationText => !string.IsNullOrWhiteSpace(ValidationResultText)
        ? ValidationResultText
        : _validationAvailabilityText;

    public string ValidationTextColor => !string.IsNullOrWhiteSpace(ValidationResultText)
        ? (IsValidationSuccess ? "#22C55E" : "#F59E0B")
        : "#686878";



    [RelayCommand]
    private void SaveKey()
    {
        if (string.IsNullOrWhiteSpace(KeyValue)) return;
        store.SetKey(ProviderKey, KeyValue.Trim());
        KeyValue  = "";      // clear field — don't keep plaintext in memory
        IsKeySet  = true;
        IsRevealed = false;
        ValidationResultText = "";
        IsValidationSuccess = false;
    }

    [RelayCommand]
    private void ClearKey()
    {
        store.ClearKey(ProviderKey);
        KeyValue  = "";
        IsKeySet  = false;
        IsRevealed = false;
        ValidationResultText = "";
        IsValidationSuccess = false;
    }


    [RelayCommand]
    private void ToggleReveal() => IsRevealed = !IsRevealed;

    [RelayCommand]
    private async System.Threading.Tasks.Task ValidateKeyAsync()
    {
        if (validationService is null)
        {
            ValidationResultText = "Live validation service unavailable.";
            IsValidationSuccess = false;
            return;
        }

        var candidateKey = !string.IsNullOrWhiteSpace(KeyValue)
            ? KeyValue.Trim()
            : store.GetKey(ProviderKey);

        IsValidating = true;
        try
        {
            var result = await validationService.ValidateAsync(ProviderKey, candidateKey);
            ValidationResultText = result.Message;
            IsValidationSuccess = result.IsValid;
        }
        finally
        {
            IsValidating = false;
        }
    }

}
