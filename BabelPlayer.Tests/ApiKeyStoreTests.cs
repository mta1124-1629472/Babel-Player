using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;

namespace BabelPlayer.Tests;

public sealed class ApiKeyStoreTests : IDisposable
{
    private readonly string _dir;

    public ApiKeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-api-key-store-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FileSystemCredentialProvider_PersistsAndReloads_OpenAiKey()
    {
        var store = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "state", "api-keys.json")));
        store.SetKey(CredentialKeys.OpenAi, "test-openai-key");


        var reloaded = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "state", "api-keys.json")));


        Assert.True(reloaded.HasKey(CredentialKeys.OpenAi));
        Assert.Equal("test-openai-key", reloaded.GetKey(CredentialKeys.OpenAi));
        Assert.True(File.Exists(Path.Combine(_dir, "state", "api-keys.json")));
    }

    [Fact]
    public void FileSystemCredentialProvider_PersistsAndReloads_DeeplKey()
    {
        var filePath = Path.Combine(_dir, "state", "keys.json");
        var store = new ApiKeyStore(new FileSystemCredentialProvider(filePath));
        store.SetKey(CredentialKeys.Deepl, "test-deepl-key");


        var reloaded = new ApiKeyStore(new FileSystemCredentialProvider(filePath));


        Assert.True(reloaded.HasKey(CredentialKeys.Deepl));
        Assert.Equal("test-deepl-key", reloaded.GetKey(CredentialKeys.Deepl));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void LegacyHuggingFaceOnlyFile_IsDeletedDuringMigrationCleanup()
    {
        var legacyPath = Path.Combine(_dir, "legacy", "api-keys.json");
        var currentPath = Path.Combine(_dir, "current", "api-keys.json");

        var legacyStore = new FileSystemCredentialProvider(legacyPath);
        legacyStore.SetKey(CredentialKeys.LegacyHuggingFace, "legacy-hf-token");

        _ = new ApiKeyStore(new FileSystemCredentialProvider(currentPath), legacyPath);

        Assert.False(File.Exists(legacyPath));
    }

    [Fact]
    public void FileSystemCredentialProvider_ReadsLegacyBase64PlaintextEntry()
    {
        // Simulate a credential file written by the pre-AES-GCM implementation,
        // where the stored value was simply base64(UTF-8 plaintext).
        var filePath = Path.Combine(_dir, "legacy-compat", "api-keys.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        var legacyEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("my-legacy-api-key"));
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [CredentialKeys.OpenAi] = legacyEncoded
        });
        File.WriteAllText(filePath, json, Encoding.UTF8);

        var provider = new FileSystemCredentialProvider(filePath);

        Assert.Equal("my-legacy-api-key", provider.GetKey(CredentialKeys.OpenAi));
    }

    [Fact]
    public void FileSystemCredentialProvider_InstallKey_IsStableAcrossInstances()
    {
        // Two separate provider instances pointing at the same directory should
        // encrypt/decrypt with the same per-install secret.
        var filePath = Path.Combine(_dir, "install-key-stability", "api-keys.json");

        var providerA = new FileSystemCredentialProvider(filePath);
        providerA.SetKey(CredentialKeys.OpenAi, "stable-key-test");

        // A brand-new instance shares the install key file → must read the same value.
        var providerB = new FileSystemCredentialProvider(filePath);
        Assert.Equal("stable-key-test", providerB.GetKey(CredentialKeys.OpenAi));
    }
}