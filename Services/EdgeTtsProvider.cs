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

    /// <summary>
    /// Generates speech for a single TTS segment using the Edge TTS worker and writes the audio to the specified output path.
    /// </summary>
    /// <remarks>
    /// Entry: expects the provider and its Python worker pool to be ready to accept work; the request must contain valid text, voice, and an output path. 
    /// Exit: on success, a completed audio file exists at the returned output path and the provider remains ready for additional requests. 
    /// Cancellation: operation honors <paramref name="cancellationToken"/>; if cancelled, the underlying worker call is aborted and no result is returned.
    /// </remarks>
    /// <param name="request">The segment request. <see cref="SingleSegmentTtsRequest.Text"/>, <see cref="SingleSegmentTtsRequest.VoiceName"/>, and <see cref="SingleSegmentTtsRequest.OutputAudioPath"/> must be non-empty; <see cref="SingleSegmentTtsRequest.Language"/> may be null.</param>
    /// <param name="cancellationToken">Token to cancel the segment generation operation.</param>
    /// <returns>
    /// A <see cref="TtsResult"/> describing the generated audio: success flag, output path, voice name, file size in bytes, any error (null on success), and the audio duration in seconds (nullable).
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when required request fields (text, voice name, or output audio path) are null, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the worker reports an output path but the audio file is not present after worker completion.</exception>
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
        return new TtsResult(true, response.OutputPath, response.Voice, response.FileSizeBytes, null, response.DurationSeconds);
    }

    /// <summary>
/// Releases resources used by the provider by disposing its Python worker pool.
/// </summary>
/// <remarks>
/// After calling this method the provider should not be used to generate TTS; the underlying worker pool is disposed.
/// This method forwards disposal to the internal worker pool and does not throw on normal disposal semantics.
/// </remarks>
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

        [JsonPropertyName("duration_seconds")]
        public double? DurationSeconds { get; init; }
    }
}
