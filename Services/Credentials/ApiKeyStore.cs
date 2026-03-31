using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Babel.Player.Models;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// Persists API keys for external providers to an encrypted local file.
/// On Windows, values are protected with DPAPI (current-user scope).
/// On other platforms, values are stored as base64 (obfuscated only — not cryptographically secure).
/// Keys are never written to logs.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly string _filePath;

    /// <summary>Canonical provider IDs managed by this store (in display order).</summary>
    public static IReadOnlyList<string> KnownProviders { get; } =
        [CredentialKeys.OpenAi, CredentialKeys.GoogleAi, CredentialKeys.ElevenLabs, CredentialKeys.Deepl];

    public static string GetDisplayName(string providerKey) => providerKey switch
    {
        CredentialKeys.OpenAi     => "OpenAI",
        CredentialKeys.GoogleAi   => "Google AI",
        CredentialKeys.ElevenLabs => "ElevenLabs",
        CredentialKeys.Deepl      => "DeepL",
        _                         => providerKey,
    };

    public ApiKeyStore(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
    }

    // ── Public API ──────────────────────────────────────────────────────────────

    public bool HasKey(string provider)
    {
        var keys = LoadRaw();
        return keys.TryGetValue(provider, out var val) && !string.IsNullOrEmpty(val);
    }

    /// <summary>
    /// Saves an API key. The value is encrypted before writing.
    /// Passing an empty string removes the key (same as ClearKey).
    /// </summary>
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

    /// <summary>
    /// Returns the plaintext key, or an empty string if not set or decryption fails.
    /// Callers should avoid holding the return value longer than necessary.
    /// </summary>
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

    // ── Storage helpers ────────────────────────────────────────────────────────

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
            var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json, Encoding.UTF8);
        }
        catch
        {
            // Best-effort — don't crash the app if save fails
        }
    }

    // ── Encryption helpers ─────────────────────────────────────────────────────

    private static string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        // Non-Windows: base64 obfuscation only (not cryptographically secure)
        return Convert.ToBase64String(data);
    }

    private static string Unprotect(string stored)
    {
        try
        {
            var bytes = Convert.FromBase64String(stored);
            if (OperatingSystem.IsWindows())
            {
                var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                    bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
