using System;
using System.IO;
using Babel.Player.Models;
using Babel.Player.Services.Chatterbox;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class ChatterboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"babel-chatterbox-{Guid.NewGuid():N}");

    public ChatterboxTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void ModelFiles_ResolveFindsGraphsAndDetectsMultilingual()
    {
        var root = Path.Combine(_dir, "chatterbox-multilingual-ONNX");
        var onnxDir = Path.Combine(root, "onnx");
        Directory.CreateDirectory(onnxDir);
        foreach (var name in new[] { "speech_encoder.onnx", "embed_tokens.onnx", "language_model.onnx", "conditional_decoder.onnx" })
            File.WriteAllText(Path.Combine(onnxDir, name), "fake");
        File.WriteAllText(Path.Combine(root, "tokenizer.json"), "{}");

        var files = ChatterboxModelFiles.Resolve(root);

        Assert.Equal(Path.Combine(onnxDir, "language_model.onnx"), files.LanguageModelPath);
        Assert.False(files.IsTurbo);
        Assert.True(files.IsMultilingual);
    }

    [Fact]
    public void ModelFiles_ResolveThrowsWhenGraphMissing()
    {
        var root = Path.Combine(_dir, "incomplete");
        Directory.CreateDirectory(Path.Combine(root, "onnx"));
        File.WriteAllText(Path.Combine(root, "tokenizer.json"), "{}");

        Assert.Throws<FileNotFoundException>(() => ChatterboxModelFiles.Resolve(root));
    }

    [Fact]
    public void ApplyMultilingualLanguagePrefix_PassesThroughNonMultilingual()
    {
        Assert.Equal("Bonjour", ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Bonjour", "fr", false));
    }

    [Fact]
    public void ApplyMultilingualLanguagePrefix_PrependsLanguageToken()
    {
        Assert.Equal("[fr]Bonjour", ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Bonjour", "fr", true));
        Assert.Equal("[fr]Bonjour", ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Bonjour", "FR", true));
    }

    [Fact]
    public void ApplyMultilingualLanguagePrefix_RejectsUnsupportedLanguage()
    {
        Assert.Throws<NotSupportedException>(() =>
            ChatterboxTtsEngine.ApplyMultilingualLanguagePrefix("Hello", "zh", true));
    }

    [Fact]
    public void BuildTextInputIds_FramesTurboAndBase()
    {
        var turbo = ChatterboxTtsEngine.BuildTextInputIds(new long[] { 10, 20 }, true);
        Assert.Equal(new long[] { 10, 20, 50256, 50256 }, turbo);

        var @base = ChatterboxTtsEngine.BuildTextInputIds(new long[] { 10, 20 }, false);
        Assert.Equal(new long[] { 6563, 255, 10, 20, 0, 6561, 6561 }, @base);
    }

    [Fact]
    public void ResolveMaxNewTokens_DefaultsAndBudgets()
    {
        Assert.Equal(256, ChatterboxTtsEngine.ResolveMaxNewTokens(null));
        Assert.Equal(256, ChatterboxTtsEngine.ResolveMaxNewTokens(0));
        var budgeted = ChatterboxTtsEngine.ResolveMaxNewTokens(4.0);
        Assert.InRange(budgeted, 128, 256);
    }

    [Fact]
    public void MapPresentOutputToPastInputName_HandlesExportVariants()
    {
        Assert.Equal(
            "past_key_values.0.key",
            ChatterboxTtsEngine.MapLanguageModelPresentOutputToPastInputName("present_key_values.0.key"));
        Assert.Equal(
            "past_key_values.0.key",
            ChatterboxTtsEngine.MapLanguageModelPresentOutputToPastInputName("present.0.key"));
    }

    [Fact]
    public void AudioEncodeDecode_RoundTripsMonoPcm16()
    {
        var samples = new float[] { 0f, 0.5f, -0.5f, 1f, -1f };
        var bytes = ChatterboxAudio.EncodeMonoPcm16(samples, 24000);

        var (decoded, rate) = ChatterboxAudio.DecodePcm16Mono(bytes);

        Assert.Equal(24000, rate);
        Assert.Equal(samples.Length, decoded.Length);
        for (int index = 0; index < samples.Length; index++)
            Assert.Equal(samples[index], decoded[index], precision: 3);
    }

    [Fact]
    public void AudioResampleLinear_UpsamplesAndPreservesEndpoints()
    {
        var samples = new float[] { 0f, 1f };
        var upsampled = ChatterboxAudio.ResampleLinear(samples, 1, 3);

        Assert.Equal(6, upsampled.Length);
        Assert.Equal(0f, upsampled[0], precision: 5);
        Assert.Equal(1f, upsampled[5], precision: 5);
    }

    [Fact]
    public void ModelCatalog_CoversExpectedLanguages()
    {
        Assert.NotEmpty(ChatterboxModelCatalog.RequiredFiles);
        Assert.Contains("onnx/language_model.onnx", ChatterboxModelCatalog.RequiredFiles);
        Assert.Contains("fr", ChatterboxModelCatalog.SupportedLanguages);
        Assert.DoesNotContain("zh", ChatterboxModelCatalog.SupportedLanguages);
    }
}
