using System;
using System.Collections.Generic;
using System.IO;
using Babel.Player.Models;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// Orchestrates API key storage via an <see cref="ISecureCredentialProvider"/>.
/// Handles automatic migration from legacy file-based storage to hardware-backed stores.
/// Keys are never written to logs.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly ISecureCredentialProvider _provider;

    /// <summary>Returns the name of the underlying storage provider.</summary>
    public string StorageProviderName => _provider.StorageProviderName;

    /// <summary>Canonical provider IDs managed by this store (in display order).</summary>

    public static IReadOnlyList<string> KnownProviders { get; } =
        [CredentialKeys.OpenAi, CredentialKeys.GoogleAi, CredentialKeys.GoogleGemini, CredentialKeys.ElevenLabs, CredentialKeys.Deepl, CredentialKeys.HuggingFace];

    public static string GetDisplayName(string providerKey) => providerKey switch
    {
        CredentialKeys.OpenAi       => "OpenAI",
        CredentialKeys.GoogleAi     => "Google AI (STT / Cloud TTS)",
        CredentialKeys.GoogleGemini => "Google Gemini",
        CredentialKeys.ElevenLabs   => "ElevenLabs",
        CredentialKeys.Deepl        => "DeepL",
        CredentialKeys.HuggingFace  => "HuggingFace (pyannote diarization)",
        _                           => providerKey,
    };

    /// <summary>
    /// Initializes the store with a provider and optionally migrates from a legacy path.
    /// </summary>
    public ApiKeyStore(ISecureCredentialProvider provider, string? legacyFilePath = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        if (!string.IsNullOrEmpty(legacyFilePath))
        {
            PerformMigrationIfRequired(legacyFilePath);
        }
    }

    private void PerformMigrationIfRequired(string legacyPath)
    {
        if (!File.Exists(legacyPath)) return;

        try
        {
            var legacyProvider = new FileSystemCredentialProvider(legacyPath);
            var migratedCount = 0;

            foreach (var providerId in KnownProviders)
            {
                var key = legacyProvider.GetKey(providerId);
                if (!string.IsNullOrEmpty(key))
                {
                    // Only migrate if the new provider doesn't already have a key
                    if (!_provider.HasKey(providerId))
                    {
                        _provider.SetKey(providerId, key);
                        migratedCount++;
                    }
                }
            }

            if (migratedCount > 0)
            {
                // Logic for "shredding" or just deleting the old file.
                // For now, simple delete is safer than leaving encrypted keys in a known location.
                File.Delete(legacyPath);
            }
        }
        catch
        {
            // Migration is best-effort. If it fails, we don't crash.
        }
    }

    // ── Public API ───────────────────────────────────────────────────────

    public bool HasKey(string provider) => _provider.HasKey(provider);

    public void SetKey(string provider, string key) => _provider.SetKey(provider, key);

    public string GetKey(string provider) => _provider.GetKey(provider);

    public void ClearKey(string provider) => _provider.ClearKey(provider);
}
