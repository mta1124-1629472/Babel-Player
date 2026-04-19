using System;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class ModelDownloaderTests
{
    [Fact]
    public void BuildCTranslate2TranslationModelPrepScript_ImportsTransformersDependenciesBeforeConverter()
    {
        var script = ModelDownloader.BuildCTranslate2TranslationModelPrepScript();

        var huggingFaceIndex = script.IndexOf("from huggingface_hub import snapshot_download", StringComparison.Ordinal);
        var transformersIndex = script.IndexOf("import transformers", StringComparison.Ordinal);
        var sentencePieceIndex = script.IndexOf("import sentencepiece", StringComparison.Ordinal);
        var converterIndex = script.IndexOf("from ctranslate2.converters import TransformersConverter", StringComparison.Ordinal);

        Assert.True(huggingFaceIndex >= 0, "The prep script should verify huggingface_hub before conversion.");
        Assert.True(transformersIndex > huggingFaceIndex, "The prep script should import transformers before TransformersConverter.");
        Assert.True(sentencePieceIndex > transformersIndex, "The prep script should import sentencepiece before TransformersConverter.");
        Assert.True(converterIndex > sentencePieceIndex, "The prep script should only import TransformersConverter after its masked dependencies succeed.");
    }

    [Fact]
    public void BuildCTranslate2TranslationModelPrepScript_InstallsMissingDependenciesWithFailFastPipCall()
    {
        var script = ModelDownloader.BuildCTranslate2TranslationModelPrepScript();

        Assert.Contains("subprocess.check_call", script, StringComparison.Ordinal);
        Assert.Contains("'huggingface_hub'", script, StringComparison.Ordinal);
        Assert.Contains("'ctranslate2'", script, StringComparison.Ordinal);
        Assert.Contains("'transformers'", script, StringComparison.Ordinal);
        Assert.Contains("'sentencepiece'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("os.system", script, StringComparison.Ordinal);
    }
}
