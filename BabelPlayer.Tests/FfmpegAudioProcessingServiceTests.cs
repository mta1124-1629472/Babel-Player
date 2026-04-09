using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Xunit;

namespace BabelPlayer.Tests;

/// <summary>
/// Unit tests for <see cref="FfmpegAudioProcessingService"/>.
/// Tests that can run without an ffmpeg binary cover edge cases (empty list, single-file copy).
/// Tests that require ffmpeg are skipped when the binary is absent.
/// </summary>
public sealed class FfmpegAudioProcessingServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly FfmpegAudioProcessingService _service;

    public FfmpegAudioProcessingServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-ffmpeg-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _service = new FfmpegAudioProcessingService(_log);
    }

    public void Dispose()
    {
        try { _log.Dispose(); }
        catch { }
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // ── CombineAudioSegmentsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CombineAudioSegmentsAsync_EmptyList_ThrowsInvalidOperationException()
    {
        var outputPath = Path.Combine(_dir, "out.mp3");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CombineAudioSegmentsAsync([], outputPath, CancellationToken.None));

        Assert.Contains("zero", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CombineAudioSegmentsAsync_SingleSegment_CopiesFileToOutput()
    {
        var sourceContent = new byte[] { 0x49, 0x44, 0x33, 0x01, 0x02, 0x03 }; // fake MP3 header
        var sourcePath = Path.Combine(_dir, "seg1.mp3");
        var outputPath = Path.Combine(_dir, "out.mp3");

        await File.WriteAllBytesAsync(sourcePath, sourceContent);

        await _service.CombineAudioSegmentsAsync([sourcePath], outputPath, CancellationToken.None);

        Assert.True(File.Exists(outputPath));
        Assert.Equal(sourceContent, await File.ReadAllBytesAsync(outputPath));
    }

    [Fact]
    public async Task CombineAudioSegmentsAsync_SingleSegment_OverwritesExistingOutput()
    {
        var sourcePath = Path.Combine(_dir, "seg1.mp3");
        var outputPath = Path.Combine(_dir, "out.mp3");

        await File.WriteAllTextAsync(sourcePath, "new-content");
        await File.WriteAllTextAsync(outputPath, "old-content");

        await _service.CombineAudioSegmentsAsync([sourcePath], outputPath, CancellationToken.None);

        var result = await File.ReadAllTextAsync(outputPath);
        Assert.Equal("new-content", result);
    }

    [Fact]
    public async Task CombineAudioSegmentsAsync_SingleSegment_CreatesOutputDirectory()
    {
        var sourcePath = Path.Combine(_dir, "seg1.mp3");
        var nestedOutputDir = Path.Combine(_dir, "nested", "output");
        var outputPath = Path.Combine(nestedOutputDir, "out.mp3");

        await File.WriteAllTextAsync(sourcePath, "audio-data");

        await _service.CombineAudioSegmentsAsync([sourcePath], outputPath, CancellationToken.None);

        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task CombineAudioSegmentsAsync_MultipleSegments_WhenFfmpegMissing_ThrowsInvalidOperationException()
    {
        // Skip this test if ffmpeg is actually available (would succeed instead of throw)
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (ffmpegPath is not null)
            return;

        var seg1 = Path.Combine(_dir, "seg1.mp3");
        var seg2 = Path.Combine(_dir, "seg2.mp3");
        var outputPath = Path.Combine(_dir, "out.mp3");

        await File.WriteAllTextAsync(seg1, "audio1");
        await File.WriteAllTextAsync(seg2, "audio2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CombineAudioSegmentsAsync([seg1, seg2], outputPath, CancellationToken.None));

        Assert.Contains("ffmpeg", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CombineAudioSegmentsAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Skip if ffmpeg is not available (test path requires ffmpeg to be invoked)
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (ffmpegPath is null)
            return;

        var seg1 = Path.Combine(_dir, "seg1.mp3");
        var seg2 = Path.Combine(_dir, "seg2.mp3");
        var outputPath = Path.Combine(_dir, "out.mp3");

        await File.WriteAllBytesAsync(seg1, new byte[1024]);
        await File.WriteAllBytesAsync(seg2, new byte[1024]);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Already cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _service.CombineAudioSegmentsAsync([seg1, seg2], outputPath, cts.Token));
    }

    // ── ExtractAudioClipAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task ExtractAudioClipAsync_WhenFfmpegMissing_ThrowsInvalidOperationException()
    {
        // Skip this test if ffmpeg is actually available
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (ffmpegPath is not null)
            return;

        var inputPath = Path.Combine(_dir, "video.mp4");
        var outputPath = Path.Combine(_dir, "clip.wav");

        await File.WriteAllTextAsync(inputPath, "fake video content");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ExtractAudioClipAsync(inputPath, outputPath, 0.0, 5.0, CancellationToken.None));

        Assert.Contains("ffmpeg", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAudioClipAsync_WhenFfmpegMissing_DoesNotCreateOutputFile()
    {
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (ffmpegPath is not null)
            return;

        var inputPath = Path.Combine(_dir, "video.mp4");
        var outputPath = Path.Combine(_dir, "clip.wav");

        await File.WriteAllTextAsync(inputPath, "fake video");

        try
        {
            await _service.ExtractAudioClipAsync(inputPath, outputPath, 0.0, 5.0, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected
        }

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ExtractAudioClipAsync_CreatesOutputDirectory()
    {
        var ffmpegPath = DependencyLocator.FindFfmpeg();
        if (ffmpegPath is null)
            return; // Only runs when ffmpeg is available

        // This test verifies directory creation happens before ffmpeg is invoked.
        // We use a real media file path even if it will fail — we just care about dir creation.
        var inputPath = Path.Combine(_dir, "video.mp4");
        var nestedOutputDir = Path.Combine(_dir, "clips", "output");
        var outputPath = Path.Combine(nestedOutputDir, "clip.wav");

        await File.WriteAllBytesAsync(inputPath, new byte[512]);

        // May throw because input is not a real media file, but directory should exist
        try
        {
            await _service.ExtractAudioClipAsync(inputPath, outputPath, 0.0, 1.0, CancellationToken.None);
        }
        catch
        {
            // Expected failure on invalid media
        }

        Assert.True(Directory.Exists(nestedOutputDir));
    }
}