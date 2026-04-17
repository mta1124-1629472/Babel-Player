using System;
using System.IO;
using Babel.Player.Services;

namespace Babel.Player.Tests;

/// <summary>
/// Unit tests for <see cref="BenchmarkCli"/> argument parsing.
/// These tests exercise only the CLI layer — no provider, no hardware detection.
/// </summary>
public sealed class BenchmarkCliTests
{
    // ── GetArg ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetArg_FlagPresent_ReturnsValue()
    {
        var args = new[] { "--benchmark", "--model", "base" };
        Assert.Equal("base", BenchmarkCli.GetArg(args, "--model"));
    }

    [Fact]
    public void GetArg_FlagAbsent_ReturnsNull()
    {
        var args = new[] { "--benchmark" };
        Assert.Null(BenchmarkCli.GetArg(args, "--model"));
    }

    [Fact]
    public void GetArg_FlagAtEnd_ReturnsNull()
    {
        // Flag is last element — no value after it
        var args = new[] { "--benchmark", "--model" };
        Assert.Null(BenchmarkCli.GetArg(args, "--model"));
    }

    [Fact]
    public void GetArg_CaseInsensitive_ReturnsValue()
    {
        var args = new[] { "--benchmark", "--MODEL", "base" };
        Assert.Equal("base", BenchmarkCli.GetArg(args, "--model"));
    }

    // ── GetArgInt ──────────────────────────────────────────────────────────

    [Fact]
    public void GetArgInt_ValidInt_ReturnsValue()
    {
        var args = new[] { "--runs", "5" };
        Assert.Equal(5, BenchmarkCli.GetArgInt(args, "--runs"));
    }

    [Fact]
    public void GetArgInt_NonNumericValue_ReturnsNull()
    {
        var args = new[] { "--runs", "many" };
        Assert.Null(BenchmarkCli.GetArgInt(args, "--runs"));
    }

    [Fact]
    public void GetArgInt_FlagAbsent_ReturnsNull()
    {
        var args = new[] { "--benchmark" };
        Assert.Null(BenchmarkCli.GetArgInt(args, "--runs"));
    }

    // ── RunAsync — argument validation (no real provider invoked) ──────────

    [Fact]
    public async System.Threading.Tasks.Task RunAsync_UnknownFlag_ReturnsExitCode1()
    {
        var result = await BenchmarkCli.RunAsync(
            new[] { "--benchmark", "--unknown-flag" });
        Assert.Equal(1, result);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunAsync_HelpFlag_ReturnsExitCode0()
    {
        var result = await BenchmarkCli.RunAsync(new[] { "--benchmark", "--help" });
        Assert.Equal(0, result);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunAsync_ShortHelpFlag_ReturnsExitCode0()
    {
        var result = await BenchmarkCli.RunAsync(new[] { "--benchmark", "-h" });
        Assert.Equal(0, result);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunAsync_ManifestNotFound_ReturnsExitCode1()
    {
        var result = await BenchmarkCli.RunAsync(
            new[] { "--benchmark", "--manifest", "/nonexistent/path/manifest.json" });
        Assert.Equal(1, result);
    }

    [Fact]
    public async System.Threading.Tasks.Task RunAsync_ManifestExists_ReturnsExitCode0()
    {
        // Write a real manifest with no clips so the orchestrator loop is a no-op
        var dir          = Path.Combine(Path.GetTempPath(), $"bp_cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "manifest.json");
        File.WriteAllText(manifestPath, @"{
            ""dataset_id"": ""test"",
            ""description"": """",
            ""version"": ""0"",
            ""language"": ""es"",
            ""total_clips"": 0,
            ""clips"": []
        }");

        try
        {
            var result = await BenchmarkCli.RunAsync(
                new[] { "--benchmark", "--manifest", manifestPath, "--runs", "1", "--output", dir });
            // 0 clips => orchestrator loops zero times => success
            Assert.Equal(0, result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
