using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class CTranslate2TranslationProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public CTranslate2TranslationProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-ct2-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "ct2-provider.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task TranslateAsync_WritesTranslationArtifact()
    {
        var transcriptPath = Path.Combine(_dir, "transcript.json");
        var outputPath = Path.Combine(_dir, "translation.json");

        await File.WriteAllTextAsync(
            transcriptPath,
            "{\"language\":\"es\",\"language_probability\":1.0,\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\"}]}");

        var provider = new TestCTranslate2TranslationProvider(_log, "nllb-200-distilled-600M");
        provider.OnRun = (arguments, _, _) =>
        {
            File.WriteAllText(
                arguments[1],
                "{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"hello\"}]}");
            return Task.FromResult(TestCTranslate2TranslationProvider.SuccessResult());
        };

        var result = await provider.TranslateAsync(new TranslationRequest(
            transcriptPath,
            outputPath,
            "es",
            "en",
            "nllb-200-distilled-600M"));

        Assert.True(result.Success);
        var artifact = await ArtifactJson.LoadTranslationAsync(outputPath, CancellationToken.None);
        var segment = Assert.Single(artifact.Segments!);
        Assert.Equal("hello", segment.TranslatedText);
    }

    [Fact]
    public async Task TranslateSingleSegmentAsync_UpdatesRequestedSegment()
    {
        var translationPath = Path.Combine(_dir, "translation.json");
        await File.WriteAllTextAsync(
            translationPath,
            "{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"hello\"},{\"id\":\"segment_1.0\",\"start\":1.0,\"end\":2.0,\"text\":\"adios\",\"translatedText\":\"bye\"}]}");

        var provider = new TestCTranslate2TranslationProvider(_log, "nllb-200-distilled-600M");
        provider.OnRun = (arguments, _, standardInput) =>
        {
            Assert.Equal("hola", standardInput);
            File.WriteAllText(
                arguments[2],
                "{\"sourceLanguage\":\"es\",\"targetLanguage\":\"en\",\"segments\":[{\"id\":\"segment_0.0\",\"start\":0.0,\"end\":1.0,\"text\":\"hola\",\"translatedText\":\"greetings\"},{\"id\":\"segment_1.0\",\"start\":1.0,\"end\":2.0,\"text\":\"adios\",\"translatedText\":\"bye\"}]}");
            return Task.FromResult(TestCTranslate2TranslationProvider.SuccessResult());
        };

        var result = await provider.TranslateSingleSegmentAsync(new SingleSegmentTranslationRequest(
            "hola",
            "segment_0.0",
            translationPath,
            translationPath,
            "es",
            "en",
            "nllb-200-distilled-600M"));

        Assert.True(result.Success);
        var artifact = await ArtifactJson.LoadTranslationAsync(translationPath, CancellationToken.None);
        var updated = Assert.Single(artifact.Segments!, s => s.Id == "segment_0.0");
        var untouched = Assert.Single(artifact.Segments!, s => s.Id == "segment_1.0");
        Assert.Equal("greetings", updated.TranslatedText);
        Assert.Equal("bye", untouched.TranslatedText);
    }

    private sealed class TestCTranslate2TranslationProvider : CTranslate2TranslationProvider
    {
        public TestCTranslate2TranslationProvider(AppLog log, string model) : base(log, model)
        {
        }

        public Func<IReadOnlyList<string>, string, string?, Task<ScriptResult>>? OnRun { get; set; }

        protected override Task<ScriptResult> RunCTranslate2ScriptAsync(
            string scriptContent,
            IReadOnlyList<string> arguments,
            string scriptPrefix,
            string? standardInput = null,
            CancellationToken cancellationToken = default) =>
            OnRun?.Invoke(arguments, scriptPrefix, standardInput)
            ?? Task.FromResult(new ScriptResult(0, string.Empty, string.Empty));

        public static ScriptResult SuccessResult() => new(0, string.Empty, string.Empty);
    }
}
