using NAudio.Wave;
using System.Net.Http.Headers;
using System.Text.Json;
using Whisper.net;
using Whisper.net.Ggml;

namespace BabelPlayer.Core;

public enum CaptionTranscriptionMode
{
    Local,
    Cloud
}

public sealed class CaptionGenerationOptions
{
    public CaptionTranscriptionMode Mode { get; init; } = CaptionTranscriptionMode.Local;
    public string? LanguageHint { get; init; }
    public string? OpenAiApiKey { get; init; }
    public GgmlType? LocalModelType { get; init; }
    public string? CloudModel { get; init; }
}

public class TranscriptChunk
{
    public string Text { get; set; } = string.Empty;
    public double StartTimeSec { get; set; }
    public double EndTimeSec { get; set; }
    public bool IsFinal { get; set; }
}

public sealed class ModelTransferProgress
{
    public string Stage { get; init; } = string.Empty;
    public string ModelLabel { get; init; } = string.Empty;
    public long BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }

    public double? ProgressRatio => TotalBytes is > 0 ? (double)BytesTransferred / TotalBytes.Value : null;
}

public class AsrService
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://api.openai.com/v1/")
    };

    private static readonly SemaphoreSlim WhisperFactoryGate = new(1, 1);
    private readonly IBabelLogger _logger;
    private readonly IWindowsSpeechTranscriber _windowsSpeech;
    private readonly IAudioExtractor _audioExtractor;

    public AsrService(
        IAudioExtractor? audioExtractor = null,
        IWindowsSpeechTranscriber? windowsSpeech = null,
        string category = "transcription.asr",
        IBabelLogFactory? logFactory = null)
    {
        _audioExtractor = audioExtractor ?? new FfmpegFallbackAudioExtractor();
        _windowsSpeech = windowsSpeech ?? new NullWindowsSpeechTranscriber();
        _logger = (logFactory ?? NullBabelLogFactory.Instance).CreateLogger(category);
    }

    // Convenience ctor for callers that don't supply platform services
    public AsrService(string category, IBabelLogFactory? logFactory = null)
        : this(null, null, category, logFactory) { }

    public event Action<TranscriptChunk>? OnFinal;
    public event Action<ModelTransferProgress>? OnModelTransferProgress;

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeVideoAsync(
        string videoPath,
        CaptionGenerationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        _logger.LogInfo("ASR transcription starting.", BabelLogContext.Create(
            ("videoPath", videoPath),
            ("mode", options.Mode),
            ("cloudModel", options.CloudModel),
            ("localModelType", options.LocalModelType?.ToString())));

        var extractedWavePath = await Task.Run(() => _audioExtractor.Extract(videoPath), cancellationToken);

        try
        {
            if (options.Mode == CaptionTranscriptionMode.Cloud && !string.IsNullOrWhiteSpace(options.OpenAiApiKey))
            {
                try
                {
                    return await TranscribeWithCloudAsync(extractedWavePath, options, cancellationToken);
                }
                catch
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                }
            }

            var result = await TranscribeLocallyAsync(
                extractedWavePath,
                options.LocalModelType ?? GgmlType.Base,
                options.LanguageHint,
                cancellationToken);

            _logger.LogInfo("ASR transcription completed.", BabelLogContext.Create(
                ("videoPath", videoPath),
                ("cueCount", result.Count),
                ("mode", options.Mode)));

            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("ASR transcription failed.", ex, BabelLogContext.Create(
                ("videoPath", videoPath),
                ("mode", options.Mode)));
            throw;
        }
        finally
        {
            TryDeleteFile(extractedWavePath);
        }
    }

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeVideoWithWhisperAsync(
        string videoPath,
        GgmlType localModelType,
        string? languageHint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var extractedWavePath = await Task.Run(() => _audioExtractor.Extract(videoPath), cancellationToken);

        try
        {
            return await TranscribeWithWhisperAsync(extractedWavePath, localModelType, languageHint, cancellationToken);
        }
        finally
        {
            TryDeleteFile(extractedWavePath);
        }
    }

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeVideoWithWindowsSpeechAsync(
        string videoPath,
        string? languageHint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var extractedWavePath = await Task.Run(() => _audioExtractor.Extract(videoPath), cancellationToken);

        try
        {
            return await _windowsSpeech.TranscribeAsync(extractedWavePath, languageHint, cancellationToken);
        }
        finally
        {
            TryDeleteFile(extractedWavePath);
        }
    }

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeVideoWithOpenAiAsync(
        string videoPath,
        CaptionGenerationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        var extractedWavePath = await Task.Run(() => _audioExtractor.Extract(videoPath), cancellationToken);

        try
        {
            return await TranscribeWithCloudAsync(extractedWavePath, options, cancellationToken);
        }
        finally
        {
            TryDeleteFile(extractedWavePath);
        }
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeLocallyAsync(
        string wavePath,
        GgmlType localModelType,
        string? languageHint,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TranscribeWithWhisperAsync(wavePath, localModelType, languageHint, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Whisper transcription failed, falling back to Windows Speech.",
                ex,
                BabelLogContext.Create(
                    ("wavePath", wavePath),
                    ("languageHint", languageHint),
                    ("localModelType", localModelType.ToString())));

            if (_windowsSpeech.IsAvailable)
                return await _windowsSpeech.TranscribeAsync(wavePath, languageHint, cancellationToken);

            throw;
        }
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeWithWhisperAsync(
        string wavePath,
        GgmlType localModelType,
        string? languageHint,
        CancellationToken cancellationToken)
    {
        var factory = await GetWhisperFactoryAsync(localModelType, cancellationToken);
        using var audioStream = File.OpenRead(wavePath);
        var builder = factory.CreateBuilder()
            .WithNoContext()
            .SplitOnWord();

        if (string.IsNullOrWhiteSpace(languageHint))
            builder.WithLanguageDetection();
        else
            builder.WithLanguage(NormalizeWhisperLanguage(languageHint));

        using var processor = builder.Build();
        var cues = new List<SubtitleCue>();

        await foreach (var segment in processor.ProcessAsync(audioStream, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = segment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var cue = new SubtitleCue
            {
                Start = segment.Start,
                End = segment.End,
                SourceText = text,
                SourceLanguage = string.IsNullOrWhiteSpace(segment.Language)
                    ? LanguageDetector.Detect(text)
                    : segment.Language
            };

            cues.Add(cue);
            PublishFinalChunk(cue);
        }

        return cues;
    }

    private static readonly Dictionary<GgmlType, WhisperFactory> WhisperFactories = [];

    private async Task<WhisperFactory> GetWhisperFactoryAsync(
        GgmlType localModelType, CancellationToken cancellationToken)
    {
        if (WhisperFactories.TryGetValue(localModelType, out var existingFactory))
            return existingFactory;

        await WhisperFactoryGate.WaitAsync(cancellationToken);
        try
        {
            if (WhisperFactories.TryGetValue(localModelType, out existingFactory))
                return existingFactory;

            var whisperModelPath = ModelManager.GetAsrModelPath(localModelType);
            if (!File.Exists(whisperModelPath))
            {
                PublishModelTransferProgress("downloading", localModelType, 0, null);
                await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    localModelType, QuantizationType.Q5_0, cancellationToken);
                await using var fileStream = File.Create(whisperModelPath);
                await CopyModelStreamWithProgressAsync(modelStream, fileStream, localModelType, cancellationToken);
            }

            PublishModelTransferProgress("loading", localModelType, 0, null);
            var factory = WhisperFactory.FromPath(whisperModelPath);
            WhisperFactories[localModelType] = factory;
            PublishModelTransferProgress("ready", localModelType, 0, null);
            return factory;
        }
        finally
        {
            WhisperFactoryGate.Release();
        }
    }

    private async Task CopyModelStreamWithProgressAsync(
        Stream source, Stream destination, GgmlType localModelType, CancellationToken cancellationToken)
    {
        long? totalBytes = source.CanSeek ? source.Length : null;
        var buffer = new byte[1024 * 128];
        long transferred = 0;
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            transferred += bytesRead;
            PublishModelTransferProgress("downloading", localModelType, transferred, totalBytes);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private void PublishModelTransferProgress(
        string stage, GgmlType localModelType, long bytesTransferred, long? totalBytes)
    {
        OnModelTransferProgress?.Invoke(new ModelTransferProgress
        {
            Stage = stage,
            ModelLabel = localModelType.ToString(),
            BytesTransferred = bytesTransferred,
            TotalBytes = totalBytes
        });
    }

    private static string NormalizeWhisperLanguage(string? languageHint)
    {
        if (string.IsNullOrWhiteSpace(languageHint)) return "auto";
        var normalized = languageHint.Trim().ToLowerInvariant();
        return normalized switch
        {
            "en-us" => "en",
            "en-gb" => "en",
            _ => normalized
        };
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeWithCloudAsync(
        string wavePath, CaptionGenerationOptions options, CancellationToken cancellationToken)
    {
        var cues = new List<SubtitleCue>();
        var chunks = SplitWaveFile(wavePath, maxBytes: 24 * 1024 * 1024, segmentLength: TimeSpan.FromMinutes(10));

        try
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.OpenAiApiKey);

                using var form = new MultipartFormDataContent();
                using var stream = File.OpenRead(chunk.Path);
                using var audioContent = new StreamContent(stream);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

                form.Add(audioContent, "file", Path.GetFileName(chunk.Path));
                form.Add(new StringContent(options.CloudModel ?? "gpt-4o-mini-transcribe"), "model");
                form.Add(new StringContent("verbose_json"), "response_format");
                form.Add(new StringContent("segment"), "timestamp_granularities[]");

                if (!string.IsNullOrWhiteSpace(options.LanguageHint))
                    form.Add(new StringContent(options.LanguageHint), "language");

                request.Content = form;

                using var response = await HttpClient.SendAsync(request, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Cloud subtitle generation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");

                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("segments", out var segments)) continue;

                foreach (var segment in segments.EnumerateArray())
                {
                    var text = segment.GetProperty("text").GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var start = chunk.Offset + TimeSpan.FromSeconds(segment.GetProperty("start").GetDouble());
                    var end   = chunk.Offset + TimeSpan.FromSeconds(segment.GetProperty("end").GetDouble());

                    var cue = new SubtitleCue
                    {
                        Start = start,
                        End = end,
                        SourceText = text,
                        SourceLanguage = LanguageDetector.Detect(text)
                    };

                    cues.Add(cue);
                    PublishFinalChunk(cue);
                }
            }
        }
        finally
        {
            foreach (var chunk in chunks.Where(c => c.IsTemporary))
                TryDeleteFile(chunk.Path);
        }

        return cues.OrderBy(c => c.Start).ToList();
    }

    private static IReadOnlyList<TranscriptionChunkFile> SplitWaveFile(
        string wavePath, int maxBytes, TimeSpan segmentLength)
    {
        var fileInfo = new FileInfo(wavePath);
        if (fileInfo.Length <= maxBytes)
            return [new TranscriptionChunkFile(wavePath, TimeSpan.Zero, false)];

        var chunks = new List<TranscriptionChunkFile>();
        using var reader = new WaveFileReader(wavePath);

        var bytesPerSecond = reader.WaveFormat.AverageBytesPerSecond;
        var bytesPerChunk = (int)Math.Max(
            bytesPerSecond,
            Math.Min(maxBytes - (512 * 1024), segmentLength.TotalSeconds * bytesPerSecond));
        bytesPerChunk -= bytesPerChunk % reader.WaveFormat.BlockAlign;

        var buffer = new byte[bytesPerChunk];
        var offset = TimeSpan.Zero;
        var index = 0;

        while (reader.Position < reader.Length)
        {
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0) break;

            var tempPath = Path.Combine(
                Path.GetTempPath(), "BabelPlayer",
                $"{Path.GetFileNameWithoutExtension(wavePath)}-part-{index++:D4}.wav");

            using (var writer = new WaveFileWriter(tempPath, reader.WaveFormat))
                writer.Write(buffer, 0, bytesRead);

            chunks.Add(new TranscriptionChunkFile(tempPath, offset, true));
            offset += TimeSpan.FromSeconds((double)bytesRead / bytesPerSecond);
        }

        return chunks;
    }

    private void PublishFinalChunk(SubtitleCue cue)
    {
        OnFinal?.Invoke(new TranscriptChunk
        {
            Text = cue.SourceText,
            StartTimeSec = cue.Start.TotalSeconds,
            EndTimeSec = cue.End.TotalSeconds,
            IsFinal = true
        });
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record TranscriptionChunkFile(string Path, TimeSpan Offset, bool IsTemporary);

    // Nested null-objects keep AsrService self-contained with safe defaults.
    private sealed class NullWindowsSpeechTranscriber : IWindowsSpeechTranscriber
    {
        public bool IsAvailable => false;
        public Task<IReadOnlyList<SubtitleCue>> TranscribeAsync(
            string wavePath, string? languageHint, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SubtitleCue>>(Array.Empty<SubtitleCue>());
    }

    /// <summary>
    /// Minimal FFmpeg-based extractor used as the default when no extractor is injected.
    /// On Windows, callers in the App layer should inject <see cref="CompositeAudioExtractor"/>
    /// (MediaFoundation → FFmpeg) for best compatibility.
    /// </summary>
    private sealed class FfmpegFallbackAudioExtractor : IAudioExtractor
    {
        private readonly FfmpegAudioExtractor _inner = new();
        public bool IsAvailable => _inner.IsAvailable;
        public string Extract(string mediaPath) => _inner.Extract(mediaPath);
    }
}
