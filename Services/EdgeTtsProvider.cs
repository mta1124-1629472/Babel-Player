using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;

namespace Babel.Player.Services;

public sealed class EdgeTtsProvider : PythonSubprocessServiceBase, ITtsProvider, IDisposable
{
    private const int WorkerCount = 4;

    private readonly SegmentedTtsComposer _composer;
    private readonly PythonJsonWorkerPool<EdgeTtsWorkerRequest, EdgeTtsWorkerResponse> _workerPool;

    public int MaxConcurrency => WorkerCount;

    public EdgeTtsProvider(AppLog log)
        : this(
            log,
            new ManagedCpuRuntimeManager(log),
            ResolveDefaultWorkerScriptPath())
    {
    }

    internal EdgeTtsProvider(
        AppLog log,
        string pythonPath,
        string workerScriptPath,
        IReadOnlyList<string>? scriptArguments = null,
        SegmentedTtsComposer? composer = null,
        Func<CancellationToken, Task>? ensureRuntimeReadyAsync = null)
        : base(log, pythonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerScriptPath);

        _composer = composer ?? new SegmentedTtsComposer();
        _workerPool = CreateWorkerPool(
            log,
            PythonPath,
            workerScriptPath,
            scriptArguments,
            ensureRuntimeReadyAsync ?? (_ => Task.CompletedTask));
    }

    private EdgeTtsProvider(
        AppLog log,
        ManagedCpuRuntimeManager runtimeManager,
        string workerScriptPath,
        IReadOnlyList<string>? scriptArguments = null,
        SegmentedTtsComposer? composer = null)
        : base(log, runtimeManager.GetPythonExecutablePath(), runtimeManager)
    {
        ArgumentNullException.ThrowIfNull(runtimeManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerScriptPath);

        _composer = composer ?? new SegmentedTtsComposer();
        _workerPool = CreateWorkerPool(
            log,
            PythonPath,
            workerScriptPath,
            scriptArguments,
            cancellationToken => runtimeManager.EnsureInstalledAsync(cancellationToken: cancellationToken));
    }

    public Task<TtsResult> GenerateTtsAsync(
        TtsRequest request,
        CancellationToken cancellationToken = default) =>
        _composer.GenerateAsync(
            request,
            Log,
            "Edge TTS",
            MaxConcurrency,
            (segment, outputPath) => CreateSegmentRequest(request, segment, outputPath),
            GenerateSegmentTtsAsync,
            cancellationToken);

    public async Task<TtsResult> GenerateSegmentTtsAsync(
        SingleSegmentTtsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Segment text cannot be empty", nameof(request));
        if (string.IsNullOrWhiteSpace(request.OutputAudioPath))
            throw new ArgumentException("Output audio path cannot be null or empty.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.VoiceName))
            throw new ArgumentException("Voice name cannot be null or empty.", nameof(request));

        Log.Info($"Starting Edge TTS segment generation: {request.Text[..Math.Min(30, request.Text.Length)]}... -> {request.OutputAudioPath}");

        var response = await _workerPool.ExecuteAsync(
            new EdgeTtsWorkerRequest
            {
                Text = request.Text,
                OutputPath = request.OutputAudioPath,
                Voice = request.VoiceName,
                Language = request.Language,
            },
            cancellationToken).ConfigureAwait(false);

        if (!File.Exists(response.OutputPath))
            throw new InvalidOperationException($"Segment TTS output file not created: {response.OutputPath}");

        Log.Info($"Edge TTS segment completed: {response.OutputPath} ({response.FileSizeBytes} bytes)");
        return new TtsResult(true, response.OutputPath, response.Voice, response.FileSizeBytes, null);
    }

    public void Dispose() => _workerPool.Dispose();

    private static PythonJsonWorkerPool<EdgeTtsWorkerRequest, EdgeTtsWorkerResponse> CreateWorkerPool(
        AppLog log,
        string pythonPath,
        string workerScriptPath,
        IReadOnlyList<string>? scriptArguments,
        Func<CancellationToken, Task> ensureRuntimeReadyAsync) =>
        new(
            log,
            "Edge TTS",
            pythonPath,
            Path.GetFullPath(workerScriptPath),
            WorkerCount,
            ensureRuntimeReadyAsync,
            scriptArguments);

    private static SingleSegmentTtsRequest CreateSegmentRequest(
        TtsRequest request,
        TranslationSegmentArtifact segment,
        string outputPath) =>
        new(
            segment.TranslatedText!,
            outputPath,
            SegmentedTtsComposer.ResolveVoiceForSegment(request, segment),
            segment.SpeakerId,
            Language: request.Language,
            SourceVideoPath: request.SourceVideoPath);

    private static string ResolveDefaultWorkerScriptPath() =>
        Path.Combine(AppContext.BaseDirectory, "inference", "workers", "edge_tts_worker.py");

    private sealed class EdgeTtsWorkerRequest
    {
        [JsonPropertyName("text")]
        public required string Text { get; init; }

        [JsonPropertyName("output_path")]
        public required string OutputPath { get; init; }

        [JsonPropertyName("voice")]
        public required string Voice { get; init; }

        [JsonPropertyName("language")]
        public string? Language { get; init; }
    }

    private sealed class EdgeTtsWorkerResponse
    {
        [JsonPropertyName("output_path")]
        public required string OutputPath { get; init; }

        [JsonPropertyName("voice")]
        public required string Voice { get; init; }

        [JsonPropertyName("file_size_bytes")]
        public long FileSizeBytes { get; init; }
    }
}
