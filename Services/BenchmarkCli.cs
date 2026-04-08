using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Headless CLI entry-point for the benchmark suite.
///
/// Invoked when the app is started with the <c>--benchmark</c> flag:
/// <code>
/// dotnet run -- --benchmark
/// dotnet run -- --benchmark --manifest path/to/manifest.json
/// dotnet run -- --benchmark --model base --matrix fw-base-cpu-int8 --warmup 1 --runs 5
/// dotnet run -- --benchmark --output benchmarks/results/my-run
/// </code>
///
/// Exit codes:
///   0  All clips completed successfully.
///   1  Bad arguments, missing manifest, or runtime error.
/// </summary>
public static class BenchmarkCli
{
    // ── Defaults ───────────────────────────────────────────────────────────

    private const string DefaultOutput    = "benchmarks/results";
    private const string DefaultModel     = "tiny";
    private const int    DefaultWarmup    = 1;
    private const int    DefaultRuns      = 5;

    // ── Public entry-point ──────────────────────────────────────────────────

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        // ── Parse args ──────────────────────────────────────────────────

        if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(a, "-h",     StringComparison.OrdinalIgnoreCase)))
        {
            PrintUsage();
            return 0;
        }

        string? manifest = GetArg(args, "--manifest");
        string output    = GetArg(args, "--output")    ?? DefaultOutput;
        string provider  = GetArg(args, "--provider")  ?? ProviderNames.FasterWhisper;
        string model     = GetArg(args, "--model")     ?? DefaultModel;
        string matrix    = GetArg(args, "--matrix")    ?? $"{provider}-{model}-cpu-int8";
        int    warmup    = GetArgInt(args, "--warmup")  ?? DefaultWarmup;
        int    runs      = GetArgInt(args, "--runs")    ?? DefaultRuns;

        // Check for unrecognised flags while respecting flags that consume a value.
        var known = new[] { "--benchmark", "--manifest", "--output", "--provider", "--model",
                            "--matrix", "--warmup", "--runs", "--help", "-h" };
        var knownValueFlags = new[] { "--manifest", "--output", "--provider", "--model",
                                      "--matrix", "--warmup", "--runs" };
        var unknown = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (!arg.StartsWith("-"))
            {
                continue;
            }

            if (known.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                if (knownValueFlags.Contains(arg, StringComparer.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    i++;
                }

                continue;
            }

            unknown.Add(arg);
        }

        if (unknown.Count > 0)
        {
            Console.Error.WriteLine($"[benchmark] Unknown flag(s): {string.Join(", ", unknown)}");
            PrintUsage();
            return 1;
        }

        if (manifest is null)
        {
            Console.Error.WriteLine("[benchmark] --manifest <path> is required.");
            Console.Error.WriteLine("  Tip: pass --manifest pointing at your dataset manifest.json.");
            PrintUsage();
            return 1;
        }

        if (!File.Exists(manifest))
        {
            Console.Error.WriteLine($"[benchmark] Manifest not found: {manifest}");
            Console.Error.WriteLine(
                "  Tip: pass --manifest <path> or ensure the default dataset is present.");
            return 1;
        }

        // ── Print header ────────────────────────────────────────────────

        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────┐");
        Console.WriteLine("│  Babel Player — Benchmark Mode        │");
        Console.WriteLine("├──────────────────────────────────────┤");
        Console.WriteLine($"│  manifest : {manifest,-25} │");
        Console.WriteLine($"│  output   : {output,-25} │");
        Console.WriteLine($"│  provider : {provider,-25} │");
        Console.WriteLine($"│  model    : {model,-25} │");
        Console.WriteLine($"│  matrix   : {matrix,-25} │");
        Console.WriteLine($"│  warmup   : {warmup,-25} │");
        Console.WriteLine($"│  runs     : {runs,-25} │");
        Console.WriteLine("└──────────────────────────────────────┘");
        Console.WriteLine();

        // ── Boot hardware detection off the UI thread ─────────────────────

        HardwareSnapshot hardware;
        try
        {
            Console.Write("[benchmark] Detecting hardware… ");
            hardware = await Task.Run(() => HardwareSnapshot.Run(), cancellationToken);
            Console.WriteLine("done.");
            Console.WriteLine($"[benchmark] CPU : {hardware.CpuLine}");
            Console.WriteLine($"[benchmark] GPU : {hardware.GpuLine}");
            Console.WriteLine($"[benchmark] RAM : {hardware.RamLine}");
            Console.WriteLine();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.Error.WriteLine("[benchmark] Hardware detection was canceled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"[benchmark] Hardware detection failed: {ex.Message}");
            return 1;
        }

        // ── Build settings ──────────────────────────────────────────────

        var settings = new AppSettings
        {
            TranscriptionModel          = model,
            TranscriptionCpuComputeType = "int8",
            TranscriptionCpuThreads     = 0,   // auto
            TranscriptionNumWorkers     = 1,
        };

        // ── Run orchestrator ─────────────────────────────────────────────

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BabelPlayer", "logs", "benchmark.log");
        var log = new AppLog(logPath);

        ITranscriptionProvider transcriptionProvider = provider switch
        {
            ProviderNames.FasterWhisper => new FasterWhisperTranscriptionProvider(log),
            _ => null!
        };

        if (transcriptionProvider is null)
        {
            Console.Error.WriteLine($"[benchmark] Unknown provider: \"{provider}\"");
            Console.Error.WriteLine("  Supported: faster-whisper");
            return 1;
        }

        var orchestrator = new BenchmarkOrchestrator(log, hardware);

        var outputDir = Path.Combine(output, $"{matrix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

        try
        {
            Console.WriteLine($"[benchmark] Starting… results → {outputDir}");
            Console.WriteLine();

            await orchestrator.RunAsync(
                manifestPath:      manifest,
                outputDir:         outputDir,
                provider:          transcriptionProvider,
                settings:          settings,
                matrixId:          matrix,
                warmupRuns:        warmup,
                measuredRuns:      runs,
                cancellationToken: cancellationToken);

            PrintSummary(outputDir);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[benchmark] Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[benchmark] Fatal error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    // ── Summary table ─────────────────────────────────────────────────

    private static void PrintSummary(string outputDir)
    {
        Console.WriteLine();
        Console.WriteLine("┌──────────────────────────────────────┐");
        Console.WriteLine("│  Benchmark complete                   │");
        Console.WriteLine("├──────────────────────────────────────┤");

        if (Directory.Exists(outputDir))
        {
            var files = Directory.GetFiles(outputDir, "*.json");
            Console.WriteLine($"│  {files.Length} result file(s) written          │");
            Console.WriteLine($"│  {outputDir,-37} │");
        }
        else
        {
            Console.WriteLine("│  (output directory not found)        │");
        }

        Console.WriteLine("└──────────────────────────────────────┘");
        Console.WriteLine();
    }

    // ── Usage ──────────────────────────────────────────────────────────

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Babel Player — Benchmark Mode");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --benchmark [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --manifest <path>   Dataset manifest.json  (required)");
        Console.WriteLine($"  --output   <dir>    Results output dir      (default: {DefaultOutput})");
        Console.WriteLine($"  --provider <name>   Transcription provider  (default: faster-whisper)");
        Console.WriteLine($"  --model    <name>   Model name              (default: {DefaultModel})");
        Console.WriteLine($"  --matrix   <id>     Benchmark matrix ID     (default: auto from model)");
        Console.WriteLine($"  --warmup   <n>      Warmup run count        (default: {DefaultWarmup})");
        Console.WriteLine($"  --runs     <n>      Measured run count      (default: {DefaultRuns})");
        Console.WriteLine("  --help, -h          Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- --benchmark");
        Console.WriteLine("  dotnet run -- --benchmark --model base --runs 3");
        Console.WriteLine("  dotnet run -- --benchmark --manifest my-dataset/manifest.json --output out/");
        Console.WriteLine();
    }

    // ── Arg helpers ──────────────────────────────────────────────────────

    /// <summary>Returns the value after <paramref name="flag"/> in <paramref name="args"/>, or null.</summary>
    internal static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    /// <summary>Returns the integer value after <paramref name="flag"/>, or null if absent/unparseable.</summary>
    internal static int? GetArgInt(string[] args, string flag)
    {
        var raw = GetArg(args, flag);
        return raw != null && int.TryParse(raw, out var v) ? v : null;
    }
}
