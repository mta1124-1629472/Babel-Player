using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class FasterWhisperTranscriptionProvider : PythonSubprocessServiceBase, ITranscriptionProvider, IBenchmarkableProvider
{
    public string ProviderId => ProviderNames.FasterWhisper;

    public FasterWhisperTranscriptionProvider(AppLog log) : base(log) { }

    private async Task<string> ExtractAudioAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        var audioPath = Path.Combine(Path.GetTempPath(), $"audio_{Guid.NewGuid():N}.wav");

        var ffmpegPath = DependencyLocator.FindFfmpeg()
            ?? throw new InvalidOperationException(
                "ffmpeg not found. Expected bundled ffmpeg.exe next to the app or ffmpeg on PATH.");
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-acodec");
        psi.ArgumentList.Add("pcm_s16le");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add("loudnorm=I=-16:LRA=11:TP=-1.5");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(audioPath);

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("Failed to start ffmpeg for audio extraction.");

        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);

        if (proc.ExitCode != 0 || !File.Exists(audioPath))
            throw new InvalidOperationException($"Audio extraction failed: {stderr}");

        Log.Info($"Extracted audio to: {audioPath}");
        return audioPath;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var inputPath = request.SourceAudioPath;
        string? extractedAudioPath = null;
        var extension = Path.GetExtension(request.SourceAudioPath).ToLowerInvariant();

        try
        {
            if (extension == ".mp4" || extension == ".avi" || extension == ".mkv" || extension == ".mov")
            {
                extractedAudioPath = await ExtractAudioAsync(request.SourceAudioPath, cancellationToken);
                inputPath = extractedAudioPath;
            }
            else if (extension != ".wav" && extension != ".mp3" && extension != ".flac" && extension != ".ogg")
            {
                throw new InvalidOperationException($"Unsupported audio format: {extension}. Supported formats: wav, mp3, flac, ogg, mp4, avi, mkv, mov");
            }

            var cpuComputeType = ResolveEffectiveCpuComputeType(
                string.IsNullOrWhiteSpace(request.CpuComputeType) ? "int8" : request.CpuComputeType);
            var cpuThreads = request.CpuThreads;
            var numWorkers = request.NumWorkers < 1 ? 1 : request.NumWorkers;

            var modelNameLiteral = request.ModelName.Replace("\\", "\\\\").Replace("'", "\\'");
            var cpuComputeTypeLiteral = cpuComputeType.Replace("\\", "\\\\").Replace("'", "\\'");

            var whisperCtorArgs =
                $"model_name, device='cpu', compute_type='{cpuComputeTypeLiteral}', num_workers={numWorkers}";
            if (cpuThreads > 0)
                whisperCtorArgs += $", cpu_threads={cpuThreads}";

            // model has already been validated against the whitelist by ProviderCapability before this call
            var script = $@"
import sys, json

# ── Memory sampling helpers (Step 3: VRAM/RAM instrumentation) ──────────────
def _sample_ram_mb():
    try:
        import psutil, os
        return psutil.Process(os.getpid()).memory_info().rss / (1024 * 1024)
    except Exception:
        return -1

def _sample_vram_mb():
    try:
        import pynvml
        pynvml.nvmlInit()
        try:
            handle = pynvml.nvmlDeviceGetHandleByIndex(0)
            info   = pynvml.nvmlDeviceGetMemoryInfo(handle)
            return info.used / (1024 * 1024)
        finally:
            pynvml.nvmlShutdown()
    except Exception:
        return -1

from faster_whisper import WhisperModel

model_name = '{modelNameLiteral}'
print('CPU transcription runtime: compute_type={cpuComputeTypeLiteral}, cpu_threads={(cpuThreads > 0 ? cpuThreads.ToString() : "auto")}, num_workers={numWorkers}')
model = WhisperModel({whisperCtorArgs})

# Sample baseline memory after model load, before inference
ram_before = _sample_ram_mb()
vram_before = _sample_vram_mb()

segments, info = model.transcribe(sys.argv[1])

# Sample peak memory immediately after inference completes
ram_after  = _sample_ram_mb()
vram_after = _sample_vram_mb()

peak_ram_mb  = max(ram_before, ram_after)
peak_vram_mb = max(vram_before, vram_after)

result = {{
    'language': info.language,
    'language_probability': info.language_probability,
    'peak_ram_mb': peak_ram_mb,
    'peak_vram_mb': peak_vram_mb,
    'segments': []
}}

for seg in segments:
    result['segments'].append({{
        'start': seg.start,
        'end': seg.end,
        'text': seg.text
    }})

with open(sys.argv[2], 'w', encoding='utf-8') as f:
    json.dump(result, f, ensure_ascii=False, indent=2)

print('Transcription complete')
";

            Log.Info($"Starting transcription of: {inputPath} [cpu compute={cpuComputeType}, threads={(cpuThreads > 0 ? cpuThreads.ToString() : "auto")}, workers={numWorkers}]");

            var result = await RunPythonScriptAsync(
                script,
                [inputPath, request.OutputJsonPath],
                "transcribe",
                cancellationToken: cancellationToken);
            ThrowIfFailed(result, "Transcription");

            Log.Info($"Transcription completed: {request.OutputJsonPath}");

            var transcriptionData = await ArtifactJson.LoadTranscriptAsync(request.OutputJsonPath, cancellationToken);

            var segments = new List<TranscriptSegment>();
            foreach (var seg in transcriptionData.Segments ?? [])
            {
                if (!string.IsNullOrWhiteSpace(seg.Text))
                    segments.Add(new TranscriptSegment(seg.Start, seg.End, seg.Text));
            }

            return new TranscriptionResult(
                true,
                segments,
                transcriptionData.Language ?? "unknown",
                transcriptionData.LanguageProbability,
                null,
                ElapsedMs:   result.ElapsedMs,
                PeakVramMb:  transcriptionData.PeakVramMb,
                PeakRamMb:   transcriptionData.PeakRamMb);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(extractedAudioPath) && File.Exists(extractedAudioPath))
            {
                File.Delete(extractedAudioPath);
                Log.Info($"Deleted temporary extracted audio: {extractedAudioPath}");
            }
        }
    }

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore = null)
    {
        var model = settings.TranscriptionModel;
        if (!ModelDownloader.IsFasterWhisperDownloaded(model))
            return new ProviderReadiness(false,
                $"Model '{model}' not downloaded yet.",
                RequiresModelDownload: true,
                ModelDownloadDescription: $"Download faster-whisper {model} model");
        return ProviderReadiness.Ready;
    }

    public async Task<bool> EnsureReadyAsync(AppSettings settings, IProgress<double>? progress, CancellationToken ct = default)
    {
        var model = settings.TranscriptionModel;
        if (!ModelDownloader.IsFasterWhisperDownloaded(model))
        {
            Log.Info($"Model {model} requires download. Starting download...");
            return await new ModelDownloader(Log).DownloadFasterWhisperAsync(model, progress, ct);
        }
        return true;
    }

    /// <summary>
    /// Validates the requested CPU compute type against actual hardware capabilities.
    /// ctranslate2 on CPU requires AVX-512F for int8_float16 and float16; downgrades to int8 when unavailable.
    /// </summary>
    private string ResolveEffectiveCpuComputeType(string requested)
    {
        bool needsAvx512 = requested is "int8_float16" or "float16";
        if (!needsAvx512) return requested;

        bool hasAvx512 = false;
        try { hasAvx512 = Avx512F.IsSupported; } catch { /* hardware intrinsic may throw on unsupported OS */ }

        if (hasAvx512) return requested;

        const string effective = "int8";
        Log.Warning($"CPU compute type '{requested}' requires AVX-512F which is not available on this CPU. " +
                    $"Downgrading to '{effective}' for this run. Change in Settings > Transcription to suppress this warning.");
        return effective;
    }
}
