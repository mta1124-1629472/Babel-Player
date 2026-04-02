using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ArtifactContractTests : IDisposable
{
    private readonly string _dir;

    public ArtifactContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-artifact-contract-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public void SerializeTranslation_UsesCanonicalFieldNames()
    {
        var artifact = new TranslationArtifact
        {
            SourceLanguage = "es",
            TargetLanguage = "en",
            Segments =
            [
                new TranslationSegmentArtifact
                {
                    Id = "segment_0.0",
                    Start = 0.0,
                    End = 1.5,
                    Text = "hola",
                    TranslatedText = "hello",
                }
            ],
        };

        var json = ArtifactJson.SerializeTranslation(artifact);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("sourceLanguage", out _));
        Assert.True(root.TryGetProperty("targetLanguage", out _));
        Assert.True(root.TryGetProperty("segments", out var segments));
        Assert.False(root.TryGetProperty("SourceLanguage", out _));
        Assert.False(root.TryGetProperty("TargetLanguage", out _));

        var first = segments[0];
        Assert.True(first.TryGetProperty("translatedText", out _));
        Assert.False(first.TryGetProperty("TranslatedText", out _));
    }

    [Fact]
    public void DeserializeTranslation_MissingSegments_Throws()
    {
        var json = """
        {
          "sourceLanguage": "es",
          "targetLanguage": "en"
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ArtifactJson.DeserializeTranslation(json, "test-translation"));

        Assert.Contains("Missing required 'segments'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeTranslation_WrongPropertyCasing_Throws()
    {
        var json = """
        {
          "sourcelanguage": "es",
          "targetlanguage": "en",
          "segments": []
        }
        """;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ArtifactJson.DeserializeTranslation(json, "wrong-casing"));

        Assert.Contains("sourceLanguage", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPythonScriptAsync_ArgumentListAndStdinPreserveUserText()
    {
        if (DependencyLocator.FindPython() is null)
            return;

        var outputPath = Path.Combine(_dir, "stdout.json");
        var service = new TestPythonSubprocessService(Path.Combine(_dir, "test.log"));
        var text = "line 1\n\"quoted\" \\ backslash";
        var script = """
import json
import sys

payload = {
    "args": sys.argv[1:],
    "stdin": sys.stdin.read(),
}

with open(sys.argv[2], "w", encoding="utf-8") as f:
    json.dump(payload, f, ensure_ascii=False)
print("ok")
""";

        var result = await service.RunAsync(
            script,
            ["alpha", outputPath],
            standardInput: text);

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        Assert.Equal("alpha", doc.RootElement.GetProperty("args")[0].GetString());
        Assert.Equal(outputPath, doc.RootElement.GetProperty("args")[1].GetString());
        Assert.Equal(text, doc.RootElement.GetProperty("stdin").GetString());
    }

    private sealed class TestPythonSubprocessService : PythonSubprocessServiceBase
    {
        public TestPythonSubprocessService(string logPath)
            : base(new AppLog(logPath))
        {
        }

        public Task<Result> RunAsync(
            string scriptContent,
            IReadOnlyList<string> arguments,
            string? standardInput = null) =>
            RunCoreAsync(scriptContent, arguments, standardInput);

        private async Task<Result> RunCoreAsync(
            string scriptContent,
            IReadOnlyList<string> arguments,
            string? standardInput)
        {
            var result = await RunPythonScriptAsync(
                scriptContent,
                arguments,
                "test_script",
                standardInput);

            return new Result(result.ExitCode, result.Stdout, result.Stderr);
        }
    }

    private sealed record Result(int ExitCode, string Stdout, string Stderr);
}
