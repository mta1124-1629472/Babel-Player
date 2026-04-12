using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class PiperTtsProvider : PythonSubprocessServiceBase, ITtsProvider, IDisposable
{
    private const int WorkerCount = 2;

    private readonly string _modelDir;
    private readonly SegmentedTtsComposer _composer;
    private readonly Func<CancellationToken, Task> _ensureRuntimeReadyAsync;
    private readonly PythonJsonWorkerPool<PiperWorkerRequest, PiperWorkerResponse> _workerPool;
    private int _disposed;

    public PiperTtsProvider(AppLog log, string modelDir)
        : this(log, modelDir, new ManagedCpuRuntimeManager(log))
    {
    }

    internal PiperTtsProvider(
        AppLog log,
        string modelDir,
        ManagedCpuRuntimeManager cpuRuntimeManager,
        string? workerScriptPath = null,
        SegmentedTtsComposer? composer = null)
        : base(log, cpuRuntimeManager)
    {
        ArgumentNullException.ThrowIfNull(cpuRuntimeManager);

        _modelDir = modelDir;
        _composer = composer ?? new SegmentedTtsComposer();
        _ensureRuntimeReadyAsync = ct => EnsureManagedRuntimeReadyAsync(cpuRuntimeManager, ct);
        _workerPool = CreateWorkerPool(log, workerScriptPath);
    }

    internal PiperTtsProvider(
        AppLog log,
        string modelDir,
        string pythonPath,
        string? workerScriptPath = null,
        SegmentedTtsComposer? composer = null)
        : base(log, pythonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonPath);

        _modelDir = modelDir;
        _composer = composer ?? new SegmentedTtsComposer();
        _ensureRuntimeReadyAsync = EnsurePythonExecutableReadyAsync;
        _workerPool = CreateWorkerPool(log, workerScriptPath);
    }

    public int MaxConcurrency => WorkerCount;

    public Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCombinedRequest(request);

        return _composer.GenerateAsync(
            request,
            Log,
            providerLabel: "Piper",
            maxConcurrency: MaxConcurrency,
            requestFactory: (segment, segmentAudioPath) =>
            {
                var voice = SegmentedTtsComposer.ResolveVoiceForSegment(request, segment);
                var referenceAudioPath = ResolveReferenceAudioPath(request, segment.SpeakerId);
                return new SingleSegmentTtsRequest(
                    segment.TranslatedText!,
                    segmentAudioPath,
                    voice,
                    SpeakerId: segment.SpeakerId,
                    ReferenceAudioPath: referenceAudioPath,
                    Language: request.Language,
                    SourceVideoPath: request.SourceVideoPath);
            },
            generateSegmentAsync: GenerateSegmentTtsAsync,
            cancellationToken: cancellationToken);
    }

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSegmentRequest(request);

        Log.Info(
            $"Starting Piper segment TTS ({request.VoiceName}): {request.Text[..Math.Min(30, request.Text.Length)]}... -> {request.OutputAudioPath}");

        var response = await _workerPool.ExecuteAsync(
            new PiperWorkerRequest(
                request.Text,
                request.OutputAudioPath,
                request.VoiceName,
                request.Language),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response.OutputPath))
            throw new InvalidOperationException("Piper worker returned an empty output path.");
        if (!File.Exists(response.OutputPath))
            throw new InvalidOperationException($"Piper segment TTS output file not created: {response.OutputPath}");

        var fileSize = response.FileSizeBytes > 0
            ? response.FileSizeBytes
            : new FileInfo(response.OutputPath).Length;

        Log.Info($"Piper segment TTS completed: {response.OutputPath}");
        return new TtsResult(true, response.OutputPath, response.Voice, fileSize, null);
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        var voice = settings.TtsVoice;
        if (!ModelDownloader.IsPiperVoiceDownloaded(voice, settings.PiperModelDir))
            return new ProviderReadiness(false,
                $"Voice '{voice}' not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: $"Download Piper voice {voice}");
        if (DependencyLocator.FindPiper() is null)
            return new ProviderReadiness(false,
                "Piper CLI not found on PATH. Install from https://github.com/rhasspy/piper/releases.");
        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        var voice = settings.TtsVoice;
        if (!ModelDownloader.IsPiperVoiceDownloaded(voice, settings.PiperModelDir))
        {
            Log.Info($"Voice {voice} requires download. Starting download...");
            return await new ModelDownloader(Log).DownloadPiperVoiceAsync(voice, settings.PiperModelDir, progress, ct);
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _workerPool.Dispose();
    }

    private PythonJsonWorkerPool<PiperWorkerRequest, PiperWorkerResponse> CreateWorkerPool(
        AppLog log,
        string? workerScriptPath)
    {
        return new PythonJsonWorkerPool<PiperWorkerRequest, PiperWorkerResponse>(
            log,
            poolName: "Piper TTS",
            pythonPath: PythonPath,
            scriptPath: ResolveWorkerScriptPath(workerScriptPath),
            workerCount: WorkerCount,
            ensureRuntimeReadyAsync: _ensureRuntimeReadyAsync,
            scriptArguments:
            [
                ModelDownloader.ResolvePiperModelDir(_modelDir),
            ]);
    }

    private static string? ResolveReferenceAudioPath(TtsRequest request, string? speakerId)
    {
        if (string.IsNullOrWhiteSpace(speakerId) || request.SpeakerReferenceAudioPaths is null)
            return null;

        return request.SpeakerReferenceAudioPaths.TryGetValue(speakerId, out var path)
            ? path
            : null;
    }

    private static void ValidateCombinedRequest(TtsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TranslationJsonPath))
            throw new ArgumentException("Translation JSON path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));
        if (!File.Exists(request.TranslationJsonPath))
            throw new FileNotFoundException($"Translation file not found: {request.TranslationJsonPath}");
    }

    private static void ValidateSegmentRequest(SingleSegmentTtsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));
    }

    private static string ResolveWorkerScriptPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        var directCandidate = Path.Combine(AppContext.BaseDirectory, "inference", "workers", "piper_worker.py");
        if (File.Exists(directCandidate))
            return directCandidate;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "inference", "workers", "piper_worker.py");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return directCandidate;
    }

    private async Task EnsurePythonExecutableReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(PythonPath) || !File.Exists(PythonPath))
        {
            throw new InvalidOperationException(
                "Python subprocess runtime is not ready. A valid Python executable path must be provided.");
        }

        await Task.CompletedTask;
    }

    private async Task EnsureManagedRuntimeReadyAsync(
        ManagedCpuRuntimeManager cpuRuntimeManager,
        CancellationToken cancellationToken)
    {
        await cpuRuntimeManager.EnsureInstalledAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (cpuRuntimeManager.State != ManagedCpuState.Ready || !File.Exists(PythonPath))
        {
            var failureReason = cpuRuntimeManager.FailureReason
                ?? $"Expected managed CPU Python at {PythonPath}.";
            throw new InvalidOperationException(
                $"Managed CPU runtime is not ready for subprocess providers. {failureReason}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
    }

    private sealed record PiperWorkerRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("output_path")] string OutputPath,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("language")] string? Language);

    private sealed record PiperWorkerResponse(
        [property: JsonPropertyName("output_path")] string OutputPath,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("file_size_bytes")] long FileSizeBytes);
}
