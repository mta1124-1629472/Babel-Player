using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Validates the content of inference/gpu-requirements.txt and inference/gpu-constraints.txt
/// following the changes in this PR:
///   - pydantic pin relaxed from ==2.9.2 to >=2.10.6
///   - TTS==0.22.0 removed
///   - nemo-toolkit added (gpu-requirements: [asr]>=2.7.0, gpu-constraints: ==2.7.2)
/// </summary>
public sealed class InferenceRequirementsTests
{
    private static string FindInferenceDirectory()
    {
        // When the test project is built, the parent project's inference/ files are copied
        // to the output directory via CopyToOutputDirectory=PreserveNewest.
        var outputDir = Path.Combine(AppContext.BaseDirectory, "inference");
        if (Directory.Exists(outputDir))
            return outputDir;

        // Fallback: walk up from test assembly to find the repo inference/ directory
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "inference");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "gpu-requirements.txt")))
                return candidate;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;
            dir = parent;
        }

        throw new InvalidOperationException(
            $"Could not locate inference/ directory containing gpu-requirements.txt. " +
            $"Searched from: {AppContext.BaseDirectory}");
    }

    private static string[] ReadRequirementsLines(string filePath) =>
        File.ReadAllLines(filePath)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('-'))
            .ToArray();

    // ── gpu-requirements.txt ────────────────────────────────────────────────

    [Fact]
    public void GpuRequirements_PydanticUsesMinVersionConstraint()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "gpu-requirements.txt");
        var lines = ReadRequirementsLines(requirementsPath);
        var pydanticLine = lines.FirstOrDefault(l =>
            l.StartsWith("pydantic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pydanticLine);
        Assert.Contains(">=", pydanticLine);
        Assert.DoesNotContain("==", pydanticLine);
    }

    [Fact]
    public void GpuRequirements_PydanticVersionIsAtLeast2_10_6()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "gpu-requirements.txt");
        var lines = ReadRequirementsLines(requirementsPath);
        var pydanticLine = lines.FirstOrDefault(l =>
            l.StartsWith("pydantic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pydanticLine);
        // Expect "pydantic>=2.10.6" - the minimum version must be at least 2.10.6
        Assert.Contains("2.10", pydanticLine);
    }

    [Fact]
    public void GpuRequirements_PydanticVersionIsNotOldPinnedVersion_Regression()
    {
        // Regression: pydantic was pinned to ==2.9.2; now it is >=2.10.6
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "gpu-requirements.txt");
        var lines = ReadRequirementsLines(requirementsPath);
        var pydanticLine = lines.FirstOrDefault(l =>
            l.StartsWith("pydantic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pydanticLine);
        Assert.DoesNotContain("2.9.2", pydanticLine);
    }

    [Fact]
    public void GpuRequirements_TtsPackageIsNotPresent()
    {
        // TTS==0.22.0 was removed in this PR; it must not reappear
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "gpu-requirements.txt");
        var allLines = File.ReadAllLines(requirementsPath);
        var ttsLines = allLines.Where(l =>
            l.TrimStart().StartsWith("TTS", StringComparison.OrdinalIgnoreCase) &&
            !l.TrimStart().StartsWith('#')).ToArray();

        Assert.Empty(ttsLines);
    }

    [Fact]
    public void GpuRequirements_NemoToolkitAsr_IsPresent()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "gpu-requirements.txt");
        var lines = ReadRequirementsLines(requirementsPath);
        var nemoLine = lines.FirstOrDefault(l =>
            l.StartsWith("nemo-toolkit", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(nemoLine);
        Assert.Contains("[asr]", nemoLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GpuRequirements_NemoToolkitAsr_HasMinVersionConstraint()
    {
        var requirementsPath = Path.Combine(FindInferenceDirectory(), "gpu-requirements.txt");
        var lines = ReadRequirementsLines(requirementsPath);
        var nemoLine = lines.FirstOrDefault(l =>
            l.StartsWith("nemo-toolkit", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(nemoLine);
        // Should specify >=2.7.0 per the PR diff
        Assert.Contains(">=", nemoLine);
        Assert.Contains("2.7", nemoLine);
    }

    // ── gpu-constraints.txt ─────────────────────────────────────────────────

    [Fact]
    public void GpuConstraints_PydanticUsesMinVersionConstraint()
    {
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var lines = ReadRequirementsLines(constraintsPath);
        var pydanticLine = lines.FirstOrDefault(l =>
            l.StartsWith("pydantic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pydanticLine);
        Assert.Contains(">=", pydanticLine);
        Assert.DoesNotContain("==", pydanticLine);
    }

    [Fact]
    public void GpuConstraints_PydanticVersionIsAtLeast2_10_6()
    {
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var lines = ReadRequirementsLines(constraintsPath);
        var pydanticLine = lines.FirstOrDefault(l =>
            l.StartsWith("pydantic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pydanticLine);
        Assert.Contains("2.10", pydanticLine);
    }

    [Fact]
    public void GpuConstraints_PydanticVersionIsNotOldPinnedVersion_Regression()
    {
        // Regression: pydantic was pinned to ==2.9.2; now it is >=2.10.6
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var lines = ReadRequirementsLines(constraintsPath);
        var pydanticLine = lines.FirstOrDefault(l =>
            l.StartsWith("pydantic", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pydanticLine);
        Assert.DoesNotContain("2.9.2", pydanticLine);
    }

    [Fact]
    public void GpuConstraints_TtsPackageIsNotPresent()
    {
        // TTS==0.22.0 was removed in this PR; it must not reappear
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var allLines = File.ReadAllLines(constraintsPath);
        var ttsLines = allLines.Where(l =>
            l.TrimStart().StartsWith("TTS", StringComparison.OrdinalIgnoreCase) &&
            !l.TrimStart().StartsWith('#')).ToArray();

        Assert.Empty(ttsLines);
    }

    [Fact]
    public void GpuConstraints_NemoToolkitIsPresent()
    {
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var lines = ReadRequirementsLines(constraintsPath);
        var nemoLine = lines.FirstOrDefault(l =>
            l.StartsWith("nemo-toolkit", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(nemoLine);
    }

    [Fact]
    public void GpuConstraints_NemoToolkitVersionIsPinnedAt2_7_2()
    {
        // The constraints file pins nemo-toolkit to exactly ==2.7.2 for reproducible builds
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var lines = ReadRequirementsLines(constraintsPath);
        var nemoLine = lines.FirstOrDefault(l =>
            l.StartsWith("nemo-toolkit", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(nemoLine);
        Assert.Contains("==", nemoLine);
        Assert.Contains("2.7.2", nemoLine);
    }

    [Fact]
    public void GpuConstraints_NemoToolkitHasNoExtras()
    {
        // The constraints file should NOT include [asr] extras — extras belong only in requirements
        var constraintsPath = Path.Combine(FindInferenceDirectory(), "gpu-constraints.txt");
        var lines = ReadRequirementsLines(constraintsPath);
        var nemoLine = lines.FirstOrDefault(l =>
            l.StartsWith("nemo-toolkit", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(nemoLine);
        Assert.DoesNotContain("[asr]", nemoLine, StringComparison.OrdinalIgnoreCase);
    }
}