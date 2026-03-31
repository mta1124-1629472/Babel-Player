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

    public ApiKeysViewModel(ApiKeyStore store)
    {
        Entries = ApiKeyStore.KnownProviders
            .Select(key => new ApiKeyEntryViewModel(key, store))
            .ToList();
    }
}

/// <summary>ViewModel for a single provider row in the API Keys dialog.</summary>
public sealed partial class ApiKeyEntryViewModel : ViewModelBase
{
    private readonly ApiKeyStore _store;

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

    /// <summary>Character used to mask the TextBox. '\0' means no masking (plaintext).</summary>
    public char MaskChar      => IsRevealed ? '\0' : '●';
    public string RevealIcon  => IsRevealed ? "🙈" : "👁";
    public string StatusText  => IsKeySet ? "Saved" : "Not set";
    public string StatusDotColor  => IsKeySet ? "#22C55E" : "#404058";
    public string StatusTextColor => IsKeySet ? "#22C55E" : "#606070";

    public ApiKeyEntryViewModel(string providerKey, ApiKeyStore store)
    {
        _store            = store;
        ProviderKey        = providerKey;
        ProviderDisplayName = ApiKeyStore.GetDisplayName(providerKey);
        _isKeySet          = store.HasKey(providerKey);
    }

    [RelayCommand]
    private void SaveKey()
    {
        if (string.IsNullOrWhiteSpace(KeyValue)) return;
        _store.SetKey(ProviderKey, KeyValue);
        KeyValue  = "";      // clear field — don't keep plaintext in memory
        IsKeySet  = true;
        IsRevealed = false;
    }

    [RelayCommand]
    private void ClearKey()
    {
        _store.ClearKey(ProviderKey);
        KeyValue  = "";
        IsKeySet  = false;
        IsRevealed = false;
    }

    [RelayCommand]
    private void ToggleReveal() => IsRevealed = !IsRevealed;
}
