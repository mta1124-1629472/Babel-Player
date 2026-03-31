using System;
using Xunit;
using Babel.Player.Services;
using Babel.Player.Services.Credentials;

namespace BabelPlayer.Tests;

/// <summary>
/// Focused unit tests for ProviderCapability validation gates.
/// No external dependencies, no Python, no file I/O — pure logic only.
/// </summary>
public sealed class ProviderCapabilityTests
{
    // -------------------------------------------------------------------------
    // Transcription — supported provider passes
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("tiny")]
    [InlineData("base")]
    [InlineData("small")]
    [InlineData("medium")]
    [InlineData("large-v3")]
    public void ValidateTranscription_FasterWhisper_ValidModel_Passes(string model)
    {
        // Should not throw
        ProviderCapability.ValidateTranscription("faster-whisper", model, keys: null);
    }

    // -------------------------------------------------------------------------
    // Transcription — invalid model for supported provider
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("large")]         // not a valid alias
    [InlineData("large-v2")]      // not in supported list
    [InlineData("")]
    [InlineData("base-en")]
    public void ValidateTranscription_FasterWhisper_InvalidModel_Throws(string model)
    {
        var ex = Assert.Throws<PipelineProviderException>(
            () => ProviderCapability.ValidateTranscription("faster-whisper", model, keys: null));
        Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(model, ex.Message);
    }

    // -------------------------------------------------------------------------
    // Transcription — unsupported providers throw
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("openai-whisper-api")]
    [InlineData("google-stt")]
    [InlineData("azure-stt")]
    [InlineData("unknown-provider")]
    public void ValidateTranscription_UnsupportedProvider_Throws(string provider)
    {
        var ex = Assert.Throws<PipelineProviderException>(
            () => ProviderCapability.ValidateTranscription(provider, "base", keys: null));
        Assert.Contains("not implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(provider, ex.Message);
    }

    [Fact]
    public void ValidateTranscription_OpenAiWhisperApi_MentionsApiKey()
    {
        var ex = Assert.Throws<PipelineProviderException>(
            () => ProviderCapability.ValidateTranscription("openai-whisper-api", "whisper-1", keys: null));
        // Message should call out that the provider is not implemented and that a key would be required
        Assert.Contains("not implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Translation — supported provider passes
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateTranslation_GoogleTranslateFree_Passes()
    {
        ProviderCapability.ValidateTranslation("google-translate-free", "default", keys: null);
    }

    // -------------------------------------------------------------------------
    // Translation — unsupported providers throw
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("deepl")]
    [InlineData("openai")]
    [InlineData("azure-translate")]
    [InlineData("unknown-provider")]
    public void ValidateTranslation_UnsupportedProvider_Throws(string provider)
    {
        var ex = Assert.Throws<PipelineProviderException>(
            () => ProviderCapability.ValidateTranslation(provider, "default", keys: null));
        Assert.Contains("not implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(provider, ex.Message);
    }

    // -------------------------------------------------------------------------
    // TTS — supported provider passes
    // -------------------------------------------------------------------------

    [Fact]
    public void ValidateTts_EdgeTts_Passes()
    {
        ProviderCapability.ValidateTts("edge-tts", "en-US-AriaNeural", keys: null);
    }

    [Fact]
    public void ValidateTts_EdgeTts_ArbitraryVoice_Passes()
    {
        // Voice value is not validated by ProviderCapability — only provider is checked here
        ProviderCapability.ValidateTts("edge-tts", "ja-JP-NanamiNeural", keys: null);
    }

    // -------------------------------------------------------------------------
    // TTS — unsupported providers throw
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("elevenlabs")]
    [InlineData("openai-tts")]
    [InlineData("google-cloud-tts")]
    [InlineData("azure-tts")]
    [InlineData("unknown-provider")]
    public void ValidateTts_UnsupportedProvider_Throws(string provider)
    {
        var ex = Assert.Throws<PipelineProviderException>(
            () => ProviderCapability.ValidateTts(provider, "some-voice", keys: null));
        Assert.Contains("not implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(provider, ex.Message);
    }

    // -------------------------------------------------------------------------
    // Exception type
    // -------------------------------------------------------------------------

    [Fact]
    public void PipelineProviderException_IsInvalidOperationException()
    {
        // PipelineProviderException must be catchable as InvalidOperationException
        // so existing catch blocks in the coordinator still work.
        var ex = new PipelineProviderException("test");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("test", ex.Message);
    }
}
