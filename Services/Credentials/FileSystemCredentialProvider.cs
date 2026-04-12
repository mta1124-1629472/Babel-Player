using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Babel.Player.Models;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// A credential provider that stores encrypted keys in a JSON file.
/// On Windows, it uses DPAPI (CurrentUser) for the install secret and for
/// credential protection.  On other platforms, it uses AES-256-GCM with a
/// per-installation random key that is stored with owner-only permissions.
/// </summary>
public sealed class FileSystemCredentialProvider : ISecureCredentialProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    // Derived key is computed once per instance — PBKDF2 at 100k iterations is expensive.
    private readonly Lazy<byte[]> _derivedKey;

    // Version prefix present on all v1 payloads (both Windows DPAPI and non-Windows AES-GCM).
    private const string V1Prefix = "v1:";

    // Fixed salt is acceptable because the PBKDF2 password is itself a
    // cryptographically random 32-byte per-installation secret.
    private static readonly byte[] _pbkdf2Salt = Encoding.UTF8.GetBytes("BabelPlayer_SecureSalt_2024");

    public string StorageProviderName => OperatingSystem.IsWindows()
        ? ProviderNames.LocalFileDpapi
        : ProviderNames.LocalFileAes256Gcm;

    public FileSystemCredentialProvider(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _derivedKey = new Lazy<byte[]>(DeriveKey);
    }

    // ── Key derivation ────────────────────────────────────────────────────

    private byte[] DeriveKey()
    {
        var secret = GetOrCreateInstallSecret();
        return System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            secret, _pbkdf2Salt, 100000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
    }

    /// <summary>
    /// Returns a stable per-installation 32-byte random secret, creating and persisting
    /// it on first use alongside the credential file.
    /// On Windows, the key file is wrapped with DPAPI before being written to disk.
    /// On other platforms, the file is restricted to owner read/write (chmod 600).
    /// </summary>
    private byte[] GetOrCreateInstallSecret()
    {
        var secretPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? "", ".install_secret");

        if (File.Exists(secretPath))
        {
            try
            {
                var stored = File.ReadAllBytes(secretPath);
                if (OperatingSystem.IsWindows())
                {
                    return System.Security.Cryptography.ProtectedData.Unprotect(
                        stored, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                }
                return stored;
            }
            catch (IOException) { /* fall through to generate a new secret */ }
            catch (UnauthorizedAccessException) { /* fall through to generate a new secret */ }
        }

        var newSecret = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(newSecret);

        if (OperatingSystem.IsWindows())
        {
            var protectedSecret = System.Security.Cryptography.ProtectedData.Protect(
                newSecret, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            File.WriteAllBytes(secretPath, protectedSecret);
        }
        else
        {
            File.WriteAllBytes(secretPath, newSecret);
            try
            {
                File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best-effort permission restriction */ }
        }

        return newSecret;
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

    private string Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(
                data, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return V1Prefix + Convert.ToBase64String(encrypted);
        }

        var key = _derivedKey.Value;
        var nonce = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var tag = new byte[16];
        var ciphertext = new byte[data.Length];

        using var aesGcm = new System.Security.Cryptography.AesGcm(key, tag.Length);
        aesGcm.Encrypt(nonce, data, ciphertext, tag);

        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

        return V1Prefix + Convert.ToBase64String(result);
    }

    private string Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith(V1Prefix, StringComparison.Ordinal))
            {
                var bytes = Convert.FromBase64String(stored.Substring(V1Prefix.Length));

                if (OperatingSystem.IsWindows())
                {
                    var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                        bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decrypted);
                }

                // Non-Windows: AES-GCM format: nonce(12) || tag(16) || ciphertext
                if (bytes.Length < 12 + 16) return "";
                var key = _derivedKey.Value;
                var nonce = new byte[12];
                var tag = new byte[16];
                var ciphertext = new byte[bytes.Length - 12 - 16];

                Buffer.BlockCopy(bytes, 0, nonce, 0, nonce.Length);
                Buffer.BlockCopy(bytes, nonce.Length, tag, 0, tag.Length);
                Buffer.BlockCopy(bytes, nonce.Length + tag.Length, ciphertext, 0, ciphertext.Length);

                var plaintext = new byte[ciphertext.Length];
                using var aesGcm = new System.Security.Cryptography.AesGcm(key, tag.Length);
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
                return Encoding.UTF8.GetString(plaintext);
            }

            // Legacy format: base64(plaintext) on non-Windows, or base64(DPAPI) on Windows,
            // written by the pre-v1 implementation.
            var legacyBytes = Convert.FromBase64String(stored);
            if (OperatingSystem.IsWindows())
            {
                var decrypted = System.Security.Cryptography.ProtectedData.Unprotect(
                    legacyBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            return Encoding.UTF8.GetString(legacyBytes);
        }
        catch
        {
            return "";
        }
    }
}
