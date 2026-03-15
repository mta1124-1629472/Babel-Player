using System.Runtime.Versioning;
using BabelPlayer.App;
using BabelPlayer.Core;

namespace BabelPlayer.App.Tests;

/// <summary>
/// Tests the non-sensitive (plaintext) credential operations of SecureCredentialStore.
/// These operations do not use DPAPI but write plain-text files to LOCALAPPDATA\BabelPlayer.
/// Each test uses a cleanup pattern to avoid dirtying real user settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecureCredentialStorePlaintextTests
{
    // Note: SecureCredentialStore writes to %LOCALAPPDATA%\BabelPlayer.
    // The plaintext operations tested here (model keys, paths, flags) are safe
    // to exercise and will restore any pre-existing values on cleanup.

    [Fact]
    public void SecureCredentialStore_SubtitleModelKey_RoundTrips()
    {
        var store = new SecureCredentialStore();
        var original = store.GetSubtitleModelKey();
        try
        {
            store.SaveSubtitleModelKey("test-subtitle-model-key");

            Assert.Equal("test-subtitle-model-key", store.GetSubtitleModelKey());
        }
        finally
        {
            if (original is not null)
            {
                store.SaveSubtitleModelKey(original);
            }
        }
    }

    [Fact]
    public void SecureCredentialStore_TranslationModelKey_RoundTripsAndClears()
    {
        var store = new SecureCredentialStore();
        var original = store.GetTranslationModelKey();
        try
        {
            store.SaveTranslationModelKey("cloud:deepl");
            Assert.Equal("cloud:deepl", store.GetTranslationModelKey());

            store.ClearTranslationModelKey();
            Assert.Null(store.GetTranslationModelKey());
        }
        finally
        {
            if (original is not null)
            {
                store.SaveTranslationModelKey(original);
            }
        }
    }

    [Fact]
    public void SecureCredentialStore_AutoTranslateEnabled_RoundTrips()
    {
        var store = new SecureCredentialStore();
        var original = store.GetAutoTranslateEnabled();
        try
        {
            store.SaveAutoTranslateEnabled(true);
            Assert.True(store.GetAutoTranslateEnabled());

            store.SaveAutoTranslateEnabled(false);
            Assert.False(store.GetAutoTranslateEnabled());
        }
        finally
        {
            store.SaveAutoTranslateEnabled(original);
        }
    }

    [Fact]
    public void SecureCredentialStore_LlamaCppServerPath_RoundTrips()
    {
        var store = new SecureCredentialStore();
        var original = store.GetLlamaCppServerPath();
        try
        {
            store.SaveLlamaCppServerPath("C:\\Tools\\llama-server.exe");
            Assert.Equal("C:\\Tools\\llama-server.exe", store.GetLlamaCppServerPath());
        }
        finally
        {
            if (original is not null)
            {
                store.SaveLlamaCppServerPath(original);
            }
        }
    }

    [Fact]
    public void SecureCredentialStore_LlamaCppRuntimeVersion_RoundTrips()
    {
        var store = new SecureCredentialStore();
        var original = store.GetLlamaCppRuntimeVersion();
        try
        {
            store.SaveLlamaCppRuntimeVersion("b5188");
            Assert.Equal("b5188", store.GetLlamaCppRuntimeVersion());
        }
        finally
        {
            if (original is not null)
            {
                store.SaveLlamaCppRuntimeVersion(original);
            }
        }
    }

    [Fact]
    public void SecureCredentialStore_MicrosoftTranslatorRegion_RoundTrips()
    {
        var store = new SecureCredentialStore();
        var original = store.GetMicrosoftTranslatorRegion();
        try
        {
            store.SaveMicrosoftTranslatorRegion("eastus");
            Assert.Equal("eastus", store.GetMicrosoftTranslatorRegion());
        }
        finally
        {
            if (original is not null)
            {
                store.SaveMicrosoftTranslatorRegion(original);
            }
        }
    }

    [Fact]
    public void SecureCredentialStore_ImplementsICredentialStore()
    {
        // Verify the contract is satisfied
        ICredentialStore store = new SecureCredentialStore();

        Assert.NotNull(store);
    }
}
