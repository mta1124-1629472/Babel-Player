using NAudio.Wave;
using System.Runtime.Versioning;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Windows-only audio extractor backed by Media Foundation / NAudio.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationAudioExtractor : IAudioExtractor
{
    public bool IsAvailable => true;

    public string Extract(string mediaPath)
    {
        ValidateInput(mediaPath);

        const int SampleRate = 16000;
        const int BitsPerSample = 16;
        const int Channels = 1;
        const int ResamplerQuality = 60;

        var outputPath = MakeTempWavPath(mediaPath);

        try
        {
            using var reader = new MediaFoundationReader(mediaPath);
            var targetFormat = new WaveFormat(SampleRate, BitsPerSample, Channels);
            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = ResamplerQuality
            };
            WaveFileWriter.CreateWaveFile(outputPath, resampler);
            return outputPath;
        }
        catch (Exception ex) when (ex is not ArgumentNullException and not FileNotFoundException)
        {
            TryDelete(outputPath);
            throw new InvalidOperationException($"MediaFoundation audio extraction failed: {mediaPath}", ex);
        }
    }

    internal static string MakeTempWavPath(string mediaPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "BabelPlayer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(mediaPath)}-{Guid.NewGuid():N}.wav");
    }

    internal static void ValidateInput(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            throw new ArgumentNullException(nameof(mediaPath), "Media path cannot be null or empty.");
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException($"Media file not found: {mediaPath}", mediaPath);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
