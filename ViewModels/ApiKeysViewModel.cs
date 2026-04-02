using System.Collections.Generic;
using System.Linq;
using Babel.Player.Services.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Babel.Player.ViewModels;

/// <summary>ViewModel for the API Keys dialog, containing one entry per known provider.</summary>
public sealed class ApiKeysViewModel : ViewModelBase
{
    public IReadOnlyList<ApiKeyEntryViewModel> Entries { get; }

    public ApiKeysViewModel(ApiKeyStore store, ApiKeyValidationService? validationService = null)
    {
        Entries = ApiKeyStore.KnownProviders
            .Select(key => new ApiKeyEntryViewModel(key, store, validationService))
            .ToList();
    }
}

/// <summary>ViewModel for a single provider row in the API Keys dialog.</summary>
public sealed partial class ApiKeyEntryViewModel : ViewModelBase
{
    private readonly ApiKeyStore _store;
    private readonly ApiKeyValidationService? _validationService;
    private readonly string _validationAvailabilityText;

    public string ProviderKey         { get; }
    public string ProviderDisplayName { get; }

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
    private bool _isKeySet;

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

    public bool CanValidateLive => string.IsNullOrEmpty(_validationAvailabilityText) && !IsValidating;

    public string ValidateButtonText => IsValidating ? "Validating…" : "Validate";

    public bool HasValidationText => !string.IsNullOrWhiteSpace(ValidationText);

    public string ValidationText => !string.IsNullOrWhiteSpace(ValidationResultText)
        ? ValidationResultText
        : _validationAvailabilityText;

    public string ValidationTextColor => !string.IsNullOrWhiteSpace(ValidationResultText)
        ? (IsValidationSuccess ? "#22C55E" : "#F59E0B")
        : "#686878";

    public ApiKeyEntryViewModel(string providerKey, ApiKeyStore store, ApiKeyValidationService? validationService = null)
    {
        _store = store;
        _validationService = validationService;
        ProviderKey = providerKey;
        ProviderDisplayName = ApiKeyStore.GetDisplayName(providerKey);
        _isKeySet = store.HasKey(providerKey);
        _validationAvailabilityText = validationService?.GetAvailabilityMessage(providerKey) ?? string.Empty;
    }

    [RelayCommand]
    private void SaveKey()
    {
        if (string.IsNullOrWhiteSpace(KeyValue)) return;
        _store.SetKey(ProviderKey, KeyValue.Trim());
        KeyValue  = "";      // clear field — don't keep plaintext in memory
        IsKeySet  = true;
        IsRevealed = false;
        ValidationResultText = "";
        IsValidationSuccess = false;
    }

    [RelayCommand]
    private void ClearKey()
    {
        _store.ClearKey(ProviderKey);
        KeyValue  = "";
        IsKeySet  = false;
        IsRevealed = false;
        ValidationResultText = "";
        IsValidationSuccess = false;
    }

    [RelayCommand]
    private void ToggleReveal() => IsRevealed = !IsRevealed;

    [RelayCommand]
    private async Task ValidateKeyAsync()
    {
        if (_validationService is null)
        {
            ValidationResultText = "Live validation service unavailable.";
            IsValidationSuccess = false;
            return;
        }

        var candidateKey = !string.IsNullOrWhiteSpace(KeyValue)
            ? KeyValue.Trim()
            : _store.GetKey(ProviderKey);

        IsValidating = true;
        try
        {
            var result = await _validationService.ValidateAsync(ProviderKey, candidateKey);
            ValidationResultText = result.Message;
            IsValidationSuccess = result.IsValid;
        }
        finally
        {
            IsValidating = false;
        }
    }
}
