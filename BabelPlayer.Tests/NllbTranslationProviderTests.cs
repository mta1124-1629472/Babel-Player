using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

public sealed class NllbTranslationProviderTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;

    public NllbTranslationProviderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-nllb-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "nllb-provider.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task TranslateAsync_ThrowsInvalidOperationException_WhenPythonScriptFails()
    {
        var transcriptPath = Path.Combine(_dir, "transcript.json");
        var outputPath = Path.Combine(_dir, "translation.json");

        await File.WriteAllTextAsync(
            transcriptPath,
            "{\"language\":\"es\",\"language_probability\":1.0,\"segments\":[{\"start\":0.0,\"end\":1.0,\"text\":\"hola\"}]}");

        var provider = new TestNllbTranslationProvider(_log, "nllb-200-distilled-600M")
        {
            OnRun = (arguments, _, _) =>
            {
                return Task.FromResult(new PythonSubprocessServiceBase.ScriptResult(1, "", "Simulated Python error"));
            }
        };

        var request = new TranslationRequest(
            transcriptPath,
            outputPath,
            "es",
            "en",
            "nllb-200-distilled-600M");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranslateAsync(request));
        Assert.Contains("Simulated Python error", ex.Message);
    }

    private sealed class TestNllbTranslationProvider : NllbTranslationProvider
    {
        public TestNllbTranslationProvider(AppLog log, string model) : base(log, model)
        {
        }

        public Func<IReadOnlyList<string>, string, string?, Task<ScriptResult>>? OnRun { get; set; }

        protected override Task<ScriptResult> RunPythonScriptAsync(
            string scriptContent,
            IReadOnlyList<string>? arguments = null,
            string scriptPrefix = "script",
            string? standardInput = null,
            IReadOnlyDictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default) =>
            OnRun?.Invoke(arguments ?? Array.Empty<string>(), scriptPrefix, standardInput)
            ?? Task.FromResult(new ScriptResult(0, string.Empty, string.Empty));
    }
}
