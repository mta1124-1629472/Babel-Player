using NAudio.Wave;
using System.Net.Http.Headers;
using System.Speech.Recognition;
using System.Text.Json;
using Whisper.net;
using Whisper.net.Ggml;

namespace PlayerApp.Core;

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

    public event Action<TranscriptChunk>? OnFinal;
    public event Action<ModelTransferProgress>? OnModelTransferProgress;

    public async Task<IReadOnlyList<SubtitleCue>> TranscribeVideoAsync(string videoPath, CaptionGenerationOptions options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);

        var extractedWavePath = await Task.Run(() => ExtractWaveAudio(videoPath), cancellationToken);

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
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                }
            }

            return await TranscribeLocallyAsync(extractedWavePath, options.LocalModelType ?? GgmlType.Base, options.LanguageHint, cancellationToken);
        }
        finally
        {
            TryDeleteFile(extractedWavePath);
        }
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeLocallyAsync(string wavePath, GgmlType localModelType, string? languageHint, CancellationToken cancellationToken)
    {
        try
        {
            return await TranscribeWithWhisperAsync(wavePath, localModelType, languageHint, cancellationToken);
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return await RunOnStaThreadAsync(() => TranscribeWithWindowsSpeech(wavePath, languageHint, cancellationToken), cancellationToken);
        }
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeWithWhisperAsync(string wavePath, GgmlType localModelType, string? languageHint, CancellationToken cancellationToken)
    {
        var factory = await GetWhisperFactoryAsync(localModelType, cancellationToken);
        using var audioStream = File.OpenRead(wavePath);
        var builder = factory.CreateBuilder()
            .WithNoContext()
            .SplitOnWord();

        if (string.IsNullOrWhiteSpace(languageHint))
        {
            builder.WithLanguageDetection();
        }
        else
        {
            builder.WithLanguage(NormalizeWhisperLanguage(languageHint));
        }

        using var processor = builder.Build();

        var cues = new List<SubtitleCue>();

        await foreach (var segment in processor.ProcessAsync(audioStream, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = segment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var cue = new SubtitleCue
            {
                Start = segment.Start,
                End = segment.End,
                SourceText = text,
                SourceLanguage = string.IsNullOrWhiteSpace(segment.Language) ? LanguageDetector.Detect(text) : segment.Language
            };

            cues.Add(cue);
            PublishFinalChunk(cue);
        }

        return cues;
    }

    private static readonly Dictionary<GgmlType, WhisperFactory> WhisperFactories = [];

    private async Task<WhisperFactory> GetWhisperFactoryAsync(GgmlType localModelType, CancellationToken cancellationToken)
    {
        if (WhisperFactories.TryGetValue(localModelType, out var existingFactory))
        {
            return existingFactory;
        }

        await WhisperFactoryGate.WaitAsync(cancellationToken);
        try
        {
            if (WhisperFactories.TryGetValue(localModelType, out existingFactory))
            {
                return existingFactory;
            }

            var whisperModelPath = ModelManager.GetAsrModelPath(localModelType);
            if (!File.Exists(whisperModelPath))
            {
                PublishModelTransferProgress("downloading", localModelType, 0, null);
                await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    localModelType,
                    QuantizationType.Q5_0,
                    cancellationToken);
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

    private async Task CopyModelStreamWithProgressAsync(Stream source, Stream destination, GgmlType localModelType, CancellationToken cancellationToken)
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

    private void PublishModelTransferProgress(string stage, GgmlType localModelType, long bytesTransferred, long? totalBytes)
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
        if (string.IsNullOrWhiteSpace(languageHint))
        {
            return "auto";
        }

        var normalized = languageHint.Trim().ToLowerInvariant();
        return normalized switch
        {
            "en-us" => "en",
            "en-gb" => "en",
            _ => normalized
        };
    }

    private static async Task<T> RunOnStaThreadAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        using var _ = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private IReadOnlyList<SubtitleCue> TranscribeWithWindowsSpeech(string wavePath, string? languageHint, CancellationToken cancellationToken)
    {
        var recognizerInfo = SelectRecognizer(languageHint);
        using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
        using var waitHandle = new ManualResetEventSlim(false);

        Exception? failure = null;
        var cues = new List<SubtitleCue>();

        recognizer.LoadGrammar(new DictationGrammar());
        recognizer.SetInputToWaveFile(wavePath);

        recognizer.SpeechRecognized += (_, args) =>
        {
            if (cancellationToken.IsCancellationRequested || args.Result.Audio is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(args.Result.Text) || args.Result.Confidence < 0.30f)
            {
                return;
            }

            var start = args.Result.Audio.AudioPosition;
            var end = start + args.Result.Audio.Duration;
            var text = args.Result.Text.Trim();

            var cue = new SubtitleCue
            {
                Start = start,
                End = end,
                SourceText = text,
                SourceLanguage = LanguageDetector.Detect(text)
            };

            cues.Add(cue);
            PublishFinalChunk(cue);
        };

        recognizer.RecognizeCompleted += (_, args) =>
        {
            failure = args.Error;
            waitHandle.Set();
        };

        recognizer.RecognizeAsync(RecognizeMode.Multiple);

        while (!waitHandle.Wait(TimeSpan.FromMilliseconds(200)))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            recognizer.RecognizeAsyncCancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (failure is not null)
        {
            throw failure;
        }

        return cues;
    }

    private async Task<IReadOnlyList<SubtitleCue>> TranscribeWithCloudAsync(string wavePath, CaptionGenerationOptions options, CancellationToken cancellationToken)
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
                {
                    form.Add(new StringContent(options.LanguageHint), "language");
                }

                request.Content = form;

                using var response = await HttpClient.SendAsync(request, cancellationToken);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Cloud subtitle generation failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
                }

                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("segments", out var segments))
                {
                    continue;
                }

                foreach (var segment in segments.EnumerateArray())
                {
                    var text = segment.GetProperty("text").GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var start = chunk.Offset + TimeSpan.FromSeconds(segment.GetProperty("start").GetDouble());
                    var end = chunk.Offset + TimeSpan.FromSeconds(segment.GetProperty("end").GetDouble());

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
            {
                TryDeleteFile(chunk.Path);
            }
        }

        return cues.OrderBy(c => c.Start).ToList();
    }

    private static RecognizerInfo SelectRecognizer(string? languageHint)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers().ToList();
        if (installed.Count == 0)
        {
            throw new InvalidOperationException("No Windows speech recognition engine is installed and Whisper local transcription was unavailable.");
        }

        if (!string.IsNullOrWhiteSpace(languageHint))
        {
            var exactCulture = installed.FirstOrDefault(r => string.Equals(r.Culture.Name, languageHint, StringComparison.OrdinalIgnoreCase));
            if (exactCulture is not null)
            {
                return exactCulture;
            }

            var sameLanguage = installed.FirstOrDefault(r => string.Equals(r.Culture.TwoLetterISOLanguageName, languageHint, StringComparison.OrdinalIgnoreCase));
            if (sameLanguage is not null)
            {
                return sameLanguage;
            }
        }

        return installed.FirstOrDefault(r => string.Equals(r.Culture.Name, "en-US", StringComparison.OrdinalIgnoreCase))
            ?? installed[0];
    }

    private static string ExtractWaveAudio(string mediaPath)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "PlayerApp");
        Directory.CreateDirectory(workingDirectory);

        var outputPath = Path.Combine(workingDirectory, $"{Path.GetFileNameWithoutExtension(mediaPath)}-{Guid.NewGuid():N}.wav");

        using var reader = new MediaFoundationReader(mediaPath);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };

        WaveFileWriter.CreateWaveFile(outputPath, resampler);
        return outputPath;
    }

    private static IReadOnlyList<TranscriptionChunkFile> SplitWaveFile(string wavePath, int maxBytes, TimeSpan segmentLength)
    {
        var fileInfo = new FileInfo(wavePath);
        if (fileInfo.Length <= maxBytes)
        {
            return [new TranscriptionChunkFile(wavePath, TimeSpan.Zero, false)];
        }

        var chunks = new List<TranscriptionChunkFile>();
        using var reader = new WaveFileReader(wavePath);

        var bytesPerSecond = reader.WaveFormat.AverageBytesPerSecond;
        var bytesPerChunk = (int)Math.Max(bytesPerSecond, Math.Min(maxBytes - (512 * 1024), segmentLength.TotalSeconds * bytesPerSecond));
        bytesPerChunk -= bytesPerChunk % reader.WaveFormat.BlockAlign;

        var buffer = new byte[bytesPerChunk];
        var offset = TimeSpan.Zero;
        var index = 0;

        while (reader.Position < reader.Length)
        {
            var bytesRead = reader.Read(buffer, 0, buffer.Length);
            if (bytesRead <= 0)
            {
                break;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "PlayerApp", $"{Path.GetFileNameWithoutExtension(wavePath)}-part-{index++:D4}.wav");
            using (var writer = new WaveFileWriter(tempPath, reader.WaveFormat))
            {
                writer.Write(buffer, 0, bytesRead);
            }

            chunks.Add(new TranscriptionChunkFile(tempPath, offset, true));
            offset += TimeSpan.FromSeconds((double)bytesRead / bytesPerSecond);
        }

        return chunks;
    }

    private void PublishFinalChunk(SubtitleCue cue)
    {
        var chunk = new TranscriptChunk
        {
            Text = cue.SourceText,
            StartTimeSec = cue.Start.TotalSeconds,
            EndTimeSec = cue.End.TotalSeconds,
            IsFinal = true
        };

        OnFinal?.Invoke(chunk);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record TranscriptionChunkFile(string Path, TimeSpan Offset, bool IsTemporary);
}
