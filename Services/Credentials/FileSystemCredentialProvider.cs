using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// A credential provider that stores encrypted keys in a JSON file.
/// On Windows, it uses DPAPI (CurrentUser) for protection.
/// On other platforms, it uses AES-256-GCM.
/// </summary>
public sealed class FileSystemCredentialProvider : ISecureCredentialProvider
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public string StorageProviderName => OperatingSystem.IsWindows() ? "Local File (DPAPI Encrypted)" : "Local File (AES-256-GCM Encrypted)";

    private static readonly byte[] _salt = Encoding.UTF8.GetBytes("BabelPlayer_SecureSalt_2024");

    private static byte[] DeriveKey()
    {
        var password = Encoding.UTF8.GetBytes(Environment.MachineName + Environment.UserName);
        return System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, _salt, 100000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
    }

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

        var key = DeriveKey();
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

        return Convert.ToBase64String(result);
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

            if (bytes.Length < 12 + 16) return "";
            var key = DeriveKey();
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
        catch
        {
            return "";
        }
    }
}
