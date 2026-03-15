using System.Security.Cryptography;
using System.Text;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Linux/macOS credential store.
/// Sensitive values (API keys) are encrypted with AES-256-GCM using a key
/// derived from the current user identity and machine ID via HKDF-SHA256.
/// Non-sensitive values (model keys, paths, flags) are stored as plain text.
///
/// Encryption scheme (per value):
///   [12-byte nonce][16-byte tag][ciphertext]
/// Key derivation:
///   HKDF-SHA256(ikm = machineId + username, salt = "BabelPlayer", info = fieldName)
/// </summary>
public sealed class XdgCredentialStore : ICredentialStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BabelPlayer");

    // ── Sensitive (AES-256-GCM encrypted) ────────────────────────────────────
    private static readonly string OpenAiApiKeyPath          = Path.Combine(ConfigDir, "openai-api-key.enc");
    private static readonly string GoogleTranslateApiKeyPath = Path.Combine(ConfigDir, "google-translate-api-key.enc");
    private static readonly string DeepLApiKeyPath           = Path.Combine(ConfigDir, "deepl-api-key.enc");
    private static readonly string MsTranslatorApiKeyPath    = Path.Combine(ConfigDir, "microsoft-translator-api-key.enc");

    // ── Non-sensitive (plain text) ────────────────────────────────────────────
    private static readonly string MsTranslatorRegionPath    = Path.Combine(ConfigDir, "microsoft-translator-region.txt");
    private static readonly string LlamaCppServerPath        = Path.Combine(ConfigDir, "llama-server-path.txt");
    private static readonly string LlamaCppRuntimeVersionPath= Path.Combine(ConfigDir, "llama-runtime-version.txt");
    private static readonly string LlamaCppRuntimeSourcePath = Path.Combine(ConfigDir, "llama-runtime-source.txt");
    private static readonly string SubtitleModelPath         = Path.Combine(ConfigDir, "subtitle-model.txt");
    private static readonly string TranslationModelPath      = Path.Combine(ConfigDir, "translation-model.txt");
    private static readonly string AutoTranslatePath         = Path.Combine(ConfigDir, "auto-translate-enabled.txt");

    // ── ICredentialStore ─────────────────────────────────────────────────────

    public string? GetOpenAiApiKey()                  => ReadEncrypted(OpenAiApiKeyPath, nameof(OpenAiApiKeyPath));
    public void    SaveOpenAiApiKey(string apiKey)    => WriteEncrypted(OpenAiApiKeyPath, nameof(OpenAiApiKeyPath), apiKey);

    public string? GetGoogleTranslateApiKey()                      => ReadEncrypted(GoogleTranslateApiKeyPath, nameof(GoogleTranslateApiKeyPath));
    public void    SaveGoogleTranslateApiKey(string apiKey)        => WriteEncrypted(GoogleTranslateApiKeyPath, nameof(GoogleTranslateApiKeyPath), apiKey);

    public string? GetDeepLApiKey()               => ReadEncrypted(DeepLApiKeyPath, nameof(DeepLApiKeyPath));
    public void    SaveDeepLApiKey(string apiKey) => WriteEncrypted(DeepLApiKeyPath, nameof(DeepLApiKeyPath), apiKey);

    public string? GetMicrosoftTranslatorApiKey()                   => ReadEncrypted(MsTranslatorApiKeyPath, nameof(MsTranslatorApiKeyPath));
    public void    SaveMicrosoftTranslatorApiKey(string apiKey)     => WriteEncrypted(MsTranslatorApiKeyPath, nameof(MsTranslatorApiKeyPath), apiKey);

    public string? GetMicrosoftTranslatorRegion()                   => ReadPlaintext(MsTranslatorRegionPath);
    public void    SaveMicrosoftTranslatorRegion(string region)     => WritePlaintext(MsTranslatorRegionPath, region);

    public string? GetLlamaCppServerPath()                          => ReadPlaintext(LlamaCppServerPath);
    public void    SaveLlamaCppServerPath(string path)              => WritePlaintext(LlamaCppServerPath, path);

    public string? GetLlamaCppRuntimeVersion()                      => ReadPlaintext(LlamaCppRuntimeVersionPath);
    public void    SaveLlamaCppRuntimeVersion(string version)       => WritePlaintext(LlamaCppRuntimeVersionPath, version);

    public string? GetLlamaCppRuntimeSource()                       => ReadPlaintext(LlamaCppRuntimeSourcePath);
    public void    SaveLlamaCppRuntimeSource(string source)         => WritePlaintext(LlamaCppRuntimeSourcePath, source);

    public string? GetSubtitleModelKey()                            => ReadPlaintext(SubtitleModelPath);
    public void    SaveSubtitleModelKey(string modelKey)            => WritePlaintext(SubtitleModelPath, modelKey);

    public string? GetTranslationModelKey()                         => ReadPlaintext(TranslationModelPath);
    public void    SaveTranslationModelKey(string modelKey)         => WritePlaintext(TranslationModelPath, modelKey);
    public void    ClearTranslationModelKey()                       => TryDelete(TranslationModelPath);

    public bool GetAutoTranslateEnabled()
    {
        var v = ReadPlaintext(AutoTranslatePath);
        return bool.TryParse(v, out var parsed) && parsed;
    }

    public void SaveAutoTranslateEnabled(bool enabled) =>
        WritePlaintext(AutoTranslatePath, enabled ? "true" : "false");

    // ── Encryption helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Derives a 32-byte AES-256 key that is unique per field and per user/machine.
    /// Using the field name as HKDF "info" ensures each field has a distinct key
    /// even though the same IKM is used everywhere.
    /// </summary>
    private static byte[] DeriveKey(string fieldName)
    {
        // IKM: machine-id file (Linux) or MachineName fallback, mixed with username
        var machineId = ReadMachineId();
        var username  = Environment.UserName ?? "default";
        var ikm       = Encoding.UTF8.GetBytes(machineId + "\0" + username);
        var salt      = Encoding.UTF8.GetBytes("BabelPlayer-XDG-v1");
        var info      = Encoding.UTF8.GetBytes(fieldName);

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info);
    }

    private static string ReadMachineId()
    {
        // Linux standard location
        const string machineIdPath = "/etc/machine-id";
        if (File.Exists(machineIdPath))
        {
            try { return File.ReadAllText(machineIdPath).Trim(); }
            catch { /* fallthrough */ }
        }

        // macOS fallback
        const string macOsIdPath = "/var/db/com.apple.xpc.launchd/db.plist";
        return File.Exists(macOsIdPath)
            ? Environment.MachineName
            : Environment.MachineName;
    }

    private static void WriteEncrypted(string path, string fieldName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(ConfigDir);

        var key       = DeriveKey(fieldName);
        var nonce     = new byte[AesGcm.NonceByteSizes.MaxSize];   // 12 bytes
        var plaintext = Encoding.UTF8.GetBytes(value.Trim());
        var ciphertext = new byte[plaintext.Length];
        var tag        = new byte[AesGcm.TagByteSizes.MaxSize];    // 16 bytes

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Layout: [nonce 12][tag 16][ciphertext]
        var blob = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce,      0, blob, 0,                             nonce.Length);
        Buffer.BlockCopy(tag,        0, blob, nonce.Length,                  tag.Length);
        Buffer.BlockCopy(ciphertext, 0, blob, nonce.Length + tag.Length,     ciphertext.Length);

        File.WriteAllBytes(path, blob);
    }

    private static string? ReadEncrypted(string path, string fieldName)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var blob  = File.ReadAllBytes(path);
            const int nonceLen = 12;
            const int tagLen   = 16;
            const int headerLen = nonceLen + tagLen;

            if (blob.Length <= headerLen) return null;

            var nonce      = blob[..nonceLen];
            var tag        = blob[nonceLen..(nonceLen + tagLen)];
            var ciphertext = blob[headerLen..];
            var plaintext  = new byte[ciphertext.Length];

            var key = DeriveKey(fieldName);
            using var aes = new AesGcm(key, tagLen);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var value = Encoding.UTF8.GetString(plaintext).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            // Wrong key, corruption, or tampered data — treat as missing
            return null;
        }
    }

    // ── Plaintext helpers ────────────────────────────────────────────────────

    private static string? ReadPlaintext(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var v = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    private static void WritePlaintext(string path, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(path, value.Trim());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
