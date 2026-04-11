using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Diarization provider backed by the managed CPU runtime and the upstream WeSpeaker Python package.
/// </summary>
public sealed class WeSpeakerCpuDiarizationProvider : PythonSubprocessServiceBase, IDiarizationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ManagedCpuRuntimeManager _cpuRuntimeManager;

    /// <summary>
    /// Creates a WeSpeakerCpuDiarizationProvider and initializes an internal ManagedCpuRuntimeManager for CPU-based WeSpeaker diarization.
    /// </summary>
    /// <param name="log">Application logging instance used by the provider and the created ManagedCpuRuntimeManager.</param>
    public WeSpeakerCpuDiarizationProvider(AppLog log)
        : this(log, new ManagedCpuRuntimeManager(log))
    {
    }

    /// <summary>
    /// Initializes a new instance of WeSpeakerCpuDiarizationProvider using the supplied application log and managed CPU runtime manager.
    /// </summary>
    /// <param name="log">Application logging facility used by the provider.</param>
    /// <param name="cpuRuntimeManager">Managed CPU runtime manager that controls bootstrap, installation, and runtime state for WeSpeaker.</param>
    internal WeSpeakerCpuDiarizationProvider(AppLog log, ManagedCpuRuntimeManager cpuRuntimeManager)
        : base(log, cpuRuntimeManager)
    {
        _cpuRuntimeManager = cpuRuntimeManager;
    }

    /// <summary>
    /// Checks whether the managed CPU runtime is prepared to run the WeSpeaker diarization provider.
    /// </summary>
    /// <returns>A <see cref="ProviderReadiness"/> that is Ready when the managed CPU runtime is available; otherwise not ready with a diagnostic message explaining whether the runtime failed or still requires bootstrapping.</returns>
    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore)
    {
        if (_cpuRuntimeManager.State == ManagedCpuState.Failed)
        {
            return new ProviderReadiness(
                false,
                $"Managed CPU runtime is not ready for WeSpeaker: {_cpuRuntimeManager.FailureReason ?? "bootstrap failed"}");
        }

        if (_cpuRuntimeManager.NeedsBootstrap)
        {
            return new ProviderReadiness(
                false,
                "Managed CPU runtime is not bootstrapped for WeSpeaker yet.");
        }

        return ProviderReadiness.Ready;
    }

    /// <summary>
    /// Ensures the managed CPU runtime required for WeSpeaker is installed and reports progress.
    /// </summary>
    /// <param name="settings">Application settings used during installation or bootstrap (may influence installation behavior).</param>
    /// <param name="progress">Optional progress reporter that receives a completion value (1.0) when installation finishes.</param>
    /// <param name="ct">Cancellation token to cancel the installation/bootstrap operation.</param>
    /// <returns>`true` if the managed CPU runtime reached the Ready state, `false` otherwise.</returns>
    public async Task<bool> EnsureReadyAsync(
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            await _cpuRuntimeManager.EnsureInstalledAsync(cancellationToken: ct).ConfigureAwait(false);
            progress?.Report(1.0);
            return _cpuRuntimeManager.State == ManagedCpuState.Ready;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Managed CPU runtime bootstrap failed for WeSpeaker: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Run WeSpeaker diarization on the provided audio file and return normalized segments with speaker labels.
    /// </summary>
    /// <param name="request">Diarization request; its SourceAudioPath must point to an existing audio file to process.</param>
    /// <param name="ct">Cancellation token to cancel the operation.</param>
    /// <returns>
    /// A DiarizationResult containing:
    /// - success state,
    /// - a list of normalized diarized segments,
    /// - the number of distinct normalized speakers,
    /// - and an error message when the operation failed.
    /// </returns>
    /// <exception cref="FileNotFoundException">Thrown when the file at request.SourceAudioPath does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the WeSpeaker subprocess returns no JSON payload.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled via the cancellation token.</exception>
    public async Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        CancellationToken ct = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        Log.Info($"[WeSpeakerCpuDiarization] Diarizing: {request.SourceAudioPath}");

        try
        {
            var result = await RunPythonScriptAsync(
                DiarizeScript,
                [request.SourceAudioPath],
                "wespeaker_diarize",
                cancellationToken: ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                Log.Error($"WeSpeaker CPU diarization failed (exit {result.ExitCode})", new Exception(result.Stderr));
                return new DiarizationResult(false, [], 0, string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr);
            }

            var payload = JsonSerializer.Deserialize<WeSpeakerDiarizationPayloadDto>(result.Stdout, JsonOptions)
                ?? throw new InvalidOperationException("WeSpeaker diarization returned no JSON payload.");

            var normalizedSegments = NormalizeSegments(payload.Segments ?? []);
            var speakerCount = normalizedSegments.Count == 0
                ? 0
                : normalizedSegments.Select(segment => segment.SpeakerId).Distinct(StringComparer.Ordinal).Count();

            return new DiarizationResult(true, normalizedSegments, speakerCount, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"WeSpeaker CPU diarization failed: {ex.Message}", ex);
            return new DiarizationResult(false, [], 0, ex.Message);
        }
    }

    /// <summary>
    /// Converts raw WeSpeaker segment DTOs into diarized segments and assigns stable normalized speaker labels.
    /// </summary>
    /// <param name="segments">Raw segments produced by the WeSpeaker model.</param>
    /// <returns>A read-only list of <see cref="DiarizedSegment"/> where each segment has a normalized speaker label (e.g., "spk_00").</returns>
    private static IReadOnlyList<DiarizedSegment> NormalizeSegments(IReadOnlyList<WeSpeakerRawSegmentDto> segments)
    {
        var normalized = new List<DiarizedSegment>(segments.Count);
        var assignedLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var segment in segments)
        {
            normalized.Add(new DiarizedSegment(
                segment.Start,
                segment.End,
                NormalizeSpeakerId(segment.SpeakerId, assignedLabels)));
        }

        return normalized;
    }

    /// <summary>
    /// Normalize or assign a stable speaker label for a raw speaker identifier.
    /// </summary>
    /// <param name="rawSpeakerId">The raw speaker identifier which may be null or whitespace; if null/whitespace a synthetic lookup key is generated.</param>
    /// <param name="assignedLabels">A mapping from lookup keys to normalized speaker labels; this dictionary is updated when a new normalized label is created.</param>
    /// <returns>The normalized speaker label (for example, "spk_00"); returns an existing label if the lookup key is already present in <paramref name="assignedLabels"/>.</returns>
    private static string NormalizeSpeakerId(string? rawSpeakerId, IDictionary<string, string> assignedLabels)
    {
        var key = string.IsNullOrWhiteSpace(rawSpeakerId)
            ? $"speaker_{assignedLabels.Count}"
            : rawSpeakerId.Trim();

        if (assignedLabels.TryGetValue(key, out var existing))
            return existing;

        var normalized = $"spk_{assignedLabels.Count:00}";
        assignedLabels[key] = normalized;
        return normalized;
    }

    /// <summary>
    /// Gets the embedded Python script used to perform WeSpeaker diarization.
    /// </summary>
    public static string Script => DiarizeScript;

    private const string DiarizeScript = """
import json
import sys
import traceback

import wespeaker

def _coerce_float(value):
    try:
        return float(value)
    except Exception:
        return 0.0

def _coerce_string(value):
    return "" if value is None else str(value)

def _extract_items(raw_result):
    if raw_result is None:
        return []
    if isinstance(raw_result, dict):
        for key in ("segments", "result", "data", "diarization"):
            value = raw_result.get(key)
            if value is not None:
                return value
        return []
    return raw_result

def _parse_item(item):
    if isinstance(item, dict):
        return {
            "utt_id": _coerce_string(item.get("utt_id") or item.get("id")),
            "start": _coerce_float(item.get("start") or item.get("start_time") or item.get("begin")),
            "end": _coerce_float(item.get("end") or item.get("end_time") or item.get("stop")),
            "speaker_id": _coerce_string(
                item.get("speaker_id")
                or item.get("speaker")
                or item.get("speaker_label")
                or item.get("label")
            ),
        }

    if isinstance(item, (list, tuple)):
        if len(item) >= 4:
            return {
                "utt_id": _coerce_string(item[0]),
                "start": _coerce_float(item[1]),
                "end": _coerce_float(item[2]),
                "speaker_id": _coerce_string(item[3]),
            }
        if len(item) == 3:
            return {
                "utt_id": "",
                "start": _coerce_float(item[0]),
                "end": _coerce_float(item[1]),
                "speaker_id": _coerce_string(item[2]),
            }

    return None

def main():
    audio_path = sys.argv[1]
    model = wespeaker.load_model("english")
    model.set_device("cpu")
    raw_result = model.diarize(audio_path)

    segments = []
    for item in _extract_items(raw_result):
        parsed = _parse_item(item)
        if parsed is not None:
            segments.append(parsed)

    print(json.dumps({"segments": segments}, ensure_ascii=False))

if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"WeSpeaker diarization failed: {exc}", file=sys.stderr)
        traceback.print_exc()
        sys.exit(1)
""";

    private sealed class WeSpeakerDiarizationPayloadDto
    {
        [JsonPropertyName("segments")]
        public List<WeSpeakerRawSegmentDto>? Segments { get; set; }
    }

    private sealed class WeSpeakerRawSegmentDto
    {
        [JsonPropertyName("utt_id")]
        public string? UtteranceId { get; set; }

        [JsonPropertyName("start")]
        public double Start { get; set; }

        [JsonPropertyName("end")]
        public double End { get; set; }

        [JsonPropertyName("speaker_id")]
        public string? SpeakerId { get; set; }
    }
}