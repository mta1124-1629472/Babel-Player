using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Headless end-to-end pipeline driver.
///
/// Invoked when the app is started with the <c>--dub</c> flag:
/// <code>
/// BabelPlayer.exe --dub --media "clip.mp4"
/// BabelPlayer.exe --dub --media "clip.mp4" --lang es --out "C:\out" --no-mp4
/// </code>
///
/// Runs the real <see cref="SessionWorkflowCoordinator"/> (the same composition root
/// as the desktop app) through transcription, optional diarization, translation, and
/// TTS, then writes SRT, MP3, and MP4 exports.
///
/// Exit codes:
///   0    Success.
///   1    Bad arguments.
///   2    Pipeline or runtime failure.
///   130  Cancelled (Ctrl+C).
/// </summary>
public static class DubCli
{
    private const int ExitSuccess = 0;
    private const int ExitArgumentError = 1;
    private const int ExitPipelineFailure = 2;
    private const int ExitCancelled = 130;

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return ExitSuccess;
        }

        string? media = BenchmarkCli.GetArg(args, "--media");
        string? lang = BenchmarkCli.GetArg(args, "--lang");
        string? outDir = BenchmarkCli.GetArg(args, "--out");
        bool noDiarization = HasFlag(args, "--no-diarization");
        bool noMp4 = HasFlag(args, "--no-mp4");

        var known = new[] { "--dub", "--media", "--lang", "--out", "--no-diarization", "--no-mp4", "--help", "-h" };
        var unknown = args.Where(a => a.StartsWith('-') && !known.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
        {
            Console.Error.WriteLine($"[dub] Unknown flag(s): {string.Join(", ", unknown)}");
            PrintUsage();
            return ExitArgumentError;
        }

        if (media is null)
        {
            Console.Error.WriteLine("[dub] --media <path> is required.");
            PrintUsage();
            return ExitArgumentError;
        }

        if (!File.Exists(media))
        {
            Console.Error.WriteLine($"[dub] Media file not found: {media}");
            return ExitArgumentError;
        }

        media = Path.GetFullPath(media);
        string outputDir = string.IsNullOrWhiteSpace(outDir)
            ? Path.GetDirectoryName(media) ?? Environment.CurrentDirectory
            : Path.GetFullPath(outDir);
        Directory.CreateDirectory(outputDir);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer");

        var logPath = Path.Combine(appDataRoot, "logs", "dub.log");
        var log = new AppLog(logPath);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────┐");
        Console.WriteLine("│  Babel Player Dub CLI                 │");
        Console.WriteLine("├──────────────────────────────────────┤");
        Console.WriteLine($"│  media    : {Trim(media),-25} │");
        Console.WriteLine($"│  output   : {Trim(outputDir),-25} │");
        Console.WriteLine("└──────────────────────────────────────┘");
        Console.WriteLine();

        SessionWorkflowCoordinator? coordinator = null;
        try
        {
            var settingsService = new SettingsService(
                Path.Combine(appDataRoot, "settings", "app-settings.json"), log);
            var settings = settingsService.LoadOrDefault();

            if (!string.IsNullOrWhiteSpace(lang))
                settings.TargetLanguage = lang.Trim().ToLowerInvariant();
            if (noDiarization)
                settings.DiarizationProvider = string.Empty;

            Console.WriteLine($"[dub] transcription : {settings.TranscriptionProvider} ({settings.TranscriptionModel})");
            Console.WriteLine($"[dub] translation  : {settings.TranslationProvider} -> {settings.TargetLanguage}");
            Console.WriteLine($"[dub] tts          : {settings.TtsProvider}");
            Console.WriteLine($"[dub] diarization  : {(string.IsNullOrEmpty(settings.DiarizationProvider) ? "off" : settings.DiarizationProvider)}");
            Console.WriteLine();

            var perSessionStore = new PerSessionSnapshotStore(
                Path.Combine(appDataRoot, "sessions"), log);
            var recentStore = new RecentSessionsStore(
                Path.Combine(appDataRoot, "state", "recent-sessions.json"), log);

            var legacyKeyPath = Path.Combine(appDataRoot, "state", "api-keys.json");
            ISecureCredentialProvider keyProvider = OperatingSystem.IsWindows()
                ? new WindowsCredentialProvider()
                : new FileSystemCredentialProvider(legacyKeyPath);
            var apiKeyStore = new ApiKeyStore(keyProvider, legacyKeyPath);

            var transportManager = new MediaTransportManager(
                videoOptionsFactory: () => new VideoPlaybackOptions(
                    HwdecMode: settings.VideoHwdec,
                    GpuApi: settings.VideoGpuApi,
                    UseGpuNext: settings.VideoUseGpuNext,
                    VsrEnabled: settings.VideoVsrEnabled,
                    HdrPlaybackMode: settings.VideoHdrPlaybackMode,
                    AllowHdrPassthrough: settings.VideoHdrPlaybackMode != VideoHdrPlaybackMode.Off
                        && HardwareSnapshot.QueryActiveHdrDisplay(),
                    ToneMapping: settings.VideoToneMapping,
                    TargetPeak: settings.VideoTargetPeak,
                    HdrComputePeak: settings.VideoHdrComputePeak),
                log: log);

            coordinator = DependencyLocator.CreateSessionCoordinator(
                log, settings, perSessionStore, recentStore, apiKeyStore, transportManager,
                appDataRoot, log, out _);

            Console.WriteLine("[dub] loading media…");
            coordinator.LoadMedia(media);
            Console.WriteLine($"[dub] session {coordinator.CurrentSession.SessionId} at stage {coordinator.CurrentSession.Stage}");

            if (coordinator.CurrentSession.Stage >= SessionWorkflowStage.TtsGenerated)
            {
                Console.WriteLine("[dub] session already complete; skipping pipeline, exporting only.");
            }
            else
            {
                await RunPipelineAsync(coordinator, cts.Token).ConfigureAwait(false);
            }

            var stem = Path.GetFileNameWithoutExtension(media);
            var segments = await coordinator.GetSegmentWorkflowListAsync().ConfigureAwait(false);

            var srtPath = Path.Combine(outputDir, $"{stem}-captions.srt");
            File.WriteAllText(srtPath, SrtGenerator.Generate(segments));
            Console.WriteLine($"[dub] wrote {srtPath}");

            var render = await coordinator.TryRenderDubAudioForExportAsync().ConfigureAwait(false);
            if (render is null)
            {
                Console.Error.WriteLine("[dub] Dub render returned no output (need translation + TTS clips).");
                return ExitPipelineFailure;
            }

            var mp3Path = Path.Combine(outputDir, $"{stem}-dub.mp3");
            File.Copy(render.MixedWithAmbiancePath ?? render.DubTimelinePath, mp3Path, overwrite: true);
            Console.WriteLine($"[dub] wrote {mp3Path}");

            if (!noMp4)
            {
                var mp4Path = Path.Combine(outputDir, $"{stem}-dub.mp4");
                var session = coordinator.CurrentSession;
                var encoder = HardwareEncoderHelper.ResolveEncoder(coordinator.CurrentSettings, coordinator.HardwareSnapshot);
                var planner = new VideoExportPlanner();
                var options = new ExportVideoOptions(
                    mp4Path,
                    IncludeTtsAudio: true,
                    IncludeSoftCaptions: segments.Count > 0,
                    BurnInCaptions: false,
                    OverwriteExisting: true,
                    Encoder: encoder,
                    DubAudioPathOverride: render.MixedWithAmbiancePath ?? render.DubTimelinePath);

                var validation = planner.Validate(session, segments, options);
                if (!validation.CanExport)
                {
                    Console.Error.WriteLine($"[dub] MP4 export rejected: {string.Join(" ", validation.Issues)}");
                }
                else
                {
                    var plan = planner.BuildPlan(session, segments, options);
                    await FfmpegVideoExportRunner.RunPlanAsync(plan, coordinator.Log).ConfigureAwait(false);
                    Console.WriteLine($"[dub] wrote {mp4Path}");
                }
            }

            TryDeleteQuiet(render.DubTimelinePath);
            if (!string.Equals(render.MixedWithAmbiancePath, render.DubTimelinePath, StringComparison.OrdinalIgnoreCase))
                TryDeleteQuiet(render.MixedWithAmbiancePath);

            stopwatch.Stop();
            Console.WriteLine();
            Console.WriteLine($"[dub] complete in {stopwatch.Elapsed:mm\\:ss}. log: {logPath}");
            return ExitSuccess;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[dub] Cancelled.");
            return ExitCancelled;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[dub] Pipeline failure: {ex.Message}");
            Console.Error.WriteLine($"[dub] Log: {logPath}");
            log.Error("Dub CLI pipeline failure.", ex);
            return ExitPipelineFailure;
        }
        finally
        {
            try
            {
                coordinator?.Dispose();
            }
            catch
            {
            }
        }
    }

    private static async Task RunPipelineAsync(
        SessionWorkflowCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastStage = coordinator.CurrentSession.Stage;
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, watcherCts.Token).ConfigureAwait(false);
                    var stage = coordinator.CurrentSession.Stage;
                    if (stage != lastStage)
                    {
                        lastStage = stage;
                        Console.WriteLine($"[dub] stage -> {stage}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            Console.WriteLine($"[dub] pipeline running from {lastStage}…");
            await coordinator.AdvancePipelineAsync(
                new Progress<double>(p => Console.Write($"\r[dub] progress {p,3:F0}%   ")),
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"[dub] pipeline finished at {coordinator.CurrentSession.Stage}");
        }
        finally
        {
            watcherCts.Cancel();
            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string Trim(string value) =>
        value.Length <= 25 ? value : value[..22] + "…";

    private static void TryDeleteQuiet(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Babel Player Dub CLI (headless E2E pipeline)");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BabelPlayer.exe --dub --media <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --media <path>          Source media file (required)");
        Console.WriteLine("  --lang <code>           Translation target language (default: settings)");
        Console.WriteLine("  --out <dir>             Output directory (default: alongside media)");
        Console.WriteLine("  --no-diarization        Skip diarization for this run");
        Console.WriteLine("  --no-mp4                Skip MP4 export (SRT + MP3 only)");
        Console.WriteLine("  --help, -h              Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  BabelPlayer.exe --dub --media clip.mp4 --lang es");
        Console.WriteLine("  BabelPlayer.exe --dub --media clip.mp4 --no-diarization --no-mp4");
        Console.WriteLine();
    }
}
