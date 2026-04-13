using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Babel.Player.Models;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// A credential provider that stores encrypted keys in a JSON file.
/// On Windows, it uses DPAPI (CurrentUser) for protection.
/// On other platforms, it uses base64 obfuscation.
/// </summary>
public sealed class FileSystemCredentialProvider : ISecureCredentialProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public string StorageProviderName => OperatingSystem.IsWindows() ? ProviderNames.LocalFileDpapi : ProviderNames.LocalFileAes256Gcm;

    public FileSystemCredentialProvider(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public bool HasKey(string provider)
    {
        var keys = LoadRaw();
        return keys.TryGetValue(provider, out var val) && !string.IsNullOrEmpty(val);
    }

    public void SetKey(string provider, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            ClearKey(provider);
            return;
        }

        var keys = LoadRaw();
        keys[provider] = Protect(key);
        SaveRaw(keys);
    }

    public string GetKey(string provider)
    {
        var keys = LoadRaw();
        if (!keys.TryGetValue(provider, out var encrypted) || string.IsNullOrEmpty(encrypted))
            return "";

        return Unprotect(encrypted);
    }

    public void ClearKey(string provider)
    {
        var keys = LoadRaw();
        if (keys.Remove(provider))
            SaveRaw(keys);
    }

    public IEnumerable<string> GetStoredProviders()
    {
        return LoadRaw().Keys;
    }

    // ── Internal Helpers ──────────────────────────────────────────────────

    private Dictionary<string, string> LoadRaw()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveRaw(Dictionary<string, string> keys)
    {
        try
        {
            var json = JsonSerializer.Serialize(keys, _jsonOptions);
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }
        catch
        {
            // Silently fail for now, same as original implementation
        }
    }

    private static string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        // WARNING: Non-Windows platforms use base64 encoding (NOT encryption).
        // This is a known security tradeoff. Secrets are stored as obfuscated base64, not encrypted.
        // TODO: Implement AES-256-GCM or integrate with platform secret stores (macOS Keychain, Linux Secret Service).
        return Convert.ToBase64String(data);
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            throw new ArgumentException("Cannot unprotect null or empty credential value.", nameof(stored));

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(stored);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Stored credential is not a valid base64-encoded string. The credential file may be corrupted.", ex);
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                    bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new InvalidOperationException(
                    "Failed to decrypt credential using DPAPI. The credential may have been encrypted by a different user or on a different machine.", ex);
            }
        }

        // Non-Windows: base64 decode only (obfuscation, not encryption)
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to decode obfuscated credential. The credential file may be corrupted.", ex);
        }
    }
}