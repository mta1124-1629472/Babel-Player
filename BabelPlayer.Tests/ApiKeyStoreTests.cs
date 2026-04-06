using System;
using System.IO;
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
    public void DirectoryPathMode_PersistsAndReloadsKey()
    {
        var store = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "state", "api-keys.json")));
        store.SetKey(CredentialKeys.OpenAi, "test-openai-key");


        var reloaded = new ApiKeyStore(new FileSystemCredentialProvider(Path.Combine(_dir, "state", "api-keys.json")));


        Assert.True(reloaded.HasKey(CredentialKeys.OpenAi));
        Assert.Equal("test-openai-key", reloaded.GetKey(CredentialKeys.OpenAi));
        Assert.True(File.Exists(Path.Combine(_dir, "state", "api-keys.json")));
    }

    [Fact]
    public void FilePathMode_PersistsAndReloadsKey()
    {
        var filePath = Path.Combine(_dir, "state", "keys.json");
        var store = new ApiKeyStore(new FileSystemCredentialProvider(filePath));
        store.SetKey(CredentialKeys.Deepl, "test-deepl-key");


        var reloaded = new ApiKeyStore(new FileSystemCredentialProvider(filePath));


        Assert.True(reloaded.HasKey(CredentialKeys.Deepl));
        Assert.Equal("test-deepl-key", reloaded.GetKey(CredentialKeys.Deepl));
        Assert.True(File.Exists(filePath));
    }
}