using System;
using System.Collections.Generic;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// Defines a platform-specific provider for secure credential storage.
/// Implementations may use DPAPI, Windows Vault, macOS Keychain, or Linux Secret Service.
/// </summary>
public interface ISecureCredentialProvider
{
    /// <summary>Returns a user-friendly name for the storage provider (e.g. "Windows Vault").</summary>
    string StorageProviderName { get; }

    /// <summary>Returns true if a key exists for the specified provider.</summary>

    bool HasKey(string provider);

    /// <summary>Saves an API key securely.</summary>
    void SetKey(string provider, string key);

    /// <summary>Retrieves the plaintext key, or an empty string if not found.</summary>
    string GetKey(string provider);

    /// <summary>Removes a key from storage.</summary>
    void ClearKey(string provider);

    /// <summary>Returns all currently stored keys by provider ID.</summary>
    IEnumerable<string> GetStoredProviders();
}
