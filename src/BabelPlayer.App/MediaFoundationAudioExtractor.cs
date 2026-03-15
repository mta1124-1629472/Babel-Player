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
        AudioExtractorHelpers.ValidateInput(mediaPath);

        const int SampleRate = 16000;
        const int BitsPerSample = 16;
        const int Channels = 1;
        const int ResamplerQuality = 60;

        var outputPath = AudioExtractorHelpers.MakeTempWavPath(mediaPath);

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
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            throw new InvalidOperationException($"MediaFoundation audio extraction failed: {mediaPath}", ex);
        }
    }
}
