using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Speaker diarization via pyannote.audio running as a Python subprocess.
///
/// Calls <c>scripts/diarize.py</c> (embedded verbatim below) with the audio file path.
/// The script outputs JSON: { "speaker_count": N, "segments": [{ "start": f, "end": f, "speaker": "SPEAKER_00" }] }
///
/// Must have pyannote.audio installed: pip install pyannote.audio
/// Requires HuggingFace model files accepted via the pyannote hub and a valid HF token.
/// </summary>
public sealed class PyannoteDiarizationProvider : PythonSubprocessServiceBase, IDiarizationProvider
{
    // Language consistent with the rest of the codebase — no inline literals
    private const string ScriptPrefix = "diarize";

    private readonly string? _huggingFaceToken;

    public PyannoteDiarizationProvider(AppLog log, string? huggingFaceToken = null) : base(log)
    {
        _huggingFaceToken = string.IsNullOrWhiteSpace(huggingFaceToken) ? null : huggingFaceToken.Trim();
    }

    // ── Script ────────────────────────────────────────────────────────────────

    private const string DiarizeScript = @"
import sys, os, json

try:
    from pyannote.audio import Pipeline
except ImportError:
    print('pyannote.audio is not installed. Run: pip install pyannote.audio', file=sys.stderr)
    sys.exit(1)

audio_path   = sys.argv[1]
min_speakers = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2] != 'null' else None
max_speakers = int(sys.argv[3]) if len(sys.argv) > 3 and sys.argv[3] != 'null' else None
hf_token     = os.environ.get('HF_TOKEN') or None

pipeline = Pipeline.from_pretrained(
    'pyannote/speaker-diarization-3.1',
    use_auth_token=hf_token
)

kwargs = {}
if min_speakers is not None:
    kwargs['min_speakers'] = min_speakers
if max_speakers is not None:
    kwargs['max_speakers'] = max_speakers

diarization = pipeline(audio_path, **kwargs)

speakers = set()
segments = []
for turn, _, speaker in diarization.itertracks(yield_label=True):
    speakers.add(speaker)
    segments.append({
        'start':   round(turn.start, 3),
        'end':     round(turn.end,   3),
        'speaker': speaker,
    })

result = {
    'speaker_count': len(speakers),
    'segments':      segments,
}
print(json.dumps(result))
";

    // ── IDiarizationProvider ──────────────────────────────────────────────────

    public ProviderReadiness CheckReadiness(AppSettings settings, ApiKeyStore? keyStore)
    {
        var store = keyStore ?? _keyStore;
        var token = store.GetKey(CredentialKeys.HuggingFace);
        if (string.IsNullOrWhiteSpace(token))
            return new ProviderReadiness(false,
                "HuggingFace token is required for pyannote diarization. " +
                "Set it in Settings > API Keys > HuggingFace.");

        // Fast import probe — verifies pyannote.audio is installed without loading any model.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName              = PythonPath,
                RedirectStandardError = true,
                UseShellExecute       = false,
                CreateNoWindow        = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import pyannote.audio");
            using var proc = Process.Start(psi);
            if (proc == null)
                return new ProviderReadiness(false,
                    "Python probe failed. Ensure Python and pyannote.audio are installed.");

            bool exited = proc.WaitForExit(5_000);
            if (!exited)
            {
                proc.Kill();
                return new ProviderReadiness(false,
                    "Python probe timed out. Ensure Python and pyannote.audio are installed.");
            }

            if (proc.ExitCode != 0)
                return new ProviderReadiness(false,
                    "pyannote.audio is not installed. Run: pip install pyannote.audio");
        }
        catch
        {
            return new ProviderReadiness(false,
                "Python probe failed. Ensure Python and pyannote.audio are installed.");
        }

        return ProviderReadiness.Ready;
    }

    public Task<bool> EnsureReadyAsync(
        AppSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // pyannote models are downloaded on first use by Pipeline.from_pretrained.
        // We do not pre-download them here — the model files can be large and
        // require HuggingFace token acceptance which cannot be automated silently.
        return Task.FromResult(true);
    }

    public async Task<DiarizationResult> DiarizeAsync(
        DiarizationRequest request,
        CancellationToken ct = default)
    {
        if (!File.Exists(request.SourceAudioPath))
            throw new FileNotFoundException($"Audio file not found: {request.SourceAudioPath}");

        var minArg = request.MinSpeakers?.ToString() ?? "null";
        var maxArg = request.MaxSpeakers?.ToString() ?? "null";
        var hfToken = _keyStore.GetKey(CredentialKeys.HuggingFace);

        if (string.IsNullOrWhiteSpace(hfToken))
        {
            const string msg = "HuggingFace token is not set. Configure it in Settings > API Keys > HuggingFace.";
            Log.Error(msg, new InvalidOperationException(msg));
            return new DiarizationResult(false, [], 0, msg);
        }

        // Resolve token: per-call override wins over constructor-injected value.
        // Pass via environment variable — not argv — so the token is not visible
        // in process listings.
        var token = !string.IsNullOrWhiteSpace(request.HuggingFaceToken)
            ? request.HuggingFaceToken!.Trim()
            : _huggingFaceToken;

        var envVars = string.IsNullOrWhiteSpace(token)
            ? null
            : new Dictionary<string, string> { ["HF_TOKEN"] = token };

        Log.Info($"Starting diarization: {request.SourceAudioPath}");

        var result = await RunPythonScriptAsync(
            DiarizeScript,
            [request.SourceAudioPath, minArg, maxArg],
            ScriptPrefix,
            environmentVariables: envVars,
            cancellationToken: ct);

        if (result.ExitCode != 0)
        {
            Log.Error($"Diarization failed (exit {result.ExitCode})", new Exception(result.Stderr));
            return new DiarizationResult(false, [], 0, result.Stderr);
        }

        try
        {
            var parsed = ParseResult(result.Stdout);
            Log.Info($"Diarization complete: {parsed.SpeakerCount} speakers, {parsed.Segments.Count} segments.");
            return parsed;
        }
        catch (Exception ex)
        {
            Log.Error("Diarization result parse failure", ex);
            return new DiarizationResult(false, [], 0, $"Parse error: {ex.Message}");
        }
    }

    // ── Result parsing ────────────────────────────────────────────────────────

    private static DiarizationResult ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var speakerCount = root.GetProperty("speaker_count").GetInt32();
        var rawSegments  = root.GetProperty("segments");
        var segments     = new List<DiarizedSegment>(rawSegments.GetArrayLength());

        foreach (var seg in rawSegments.EnumerateArray())
        {
            var start   = seg.GetProperty("start").GetDouble();
            var end     = seg.GetProperty("end").GetDouble();
            var speaker = seg.GetProperty("speaker").GetString()
                ?? throw new InvalidOperationException("Segment missing 'speaker' field.");

            // Normalise "SPEAKER_00" → "spk_00" to match segment ID conventions.
            var speakerId = NormaliseSpeakerId(speaker);
            segments.Add(new DiarizedSegment(start, end, speakerId));
        }

        return new DiarizationResult(true, segments, speakerCount, null);
    }

    /// <summary>
    /// Normalises pyannote speaker labels (e.g. "SPEAKER_00", "SPEAKER_1") to the
    /// "spk_NN" form used throughout the session model. This keeps diarization speaker
    /// IDs consistent with any manually assigned IDs.
    /// </summary>
    private static string NormaliseSpeakerId(string pyannoteLabel)
    {
        // "SPEAKER_00" → "spk_00", "SPEAKER_1" → "spk_01"
        if (pyannoteLabel.StartsWith("SPEAKER_", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = pyannoteLabel[8..]; // after "SPEAKER_"
            if (int.TryParse(suffix, out var index))
                return $"spk_{index:D2}";
        }
        // Fall through: return lowercased label as-is to avoid null/empty
        return pyannoteLabel.ToLowerInvariant();
    }
}
