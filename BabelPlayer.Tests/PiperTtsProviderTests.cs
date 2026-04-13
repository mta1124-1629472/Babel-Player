using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;
using Babel.Player.Services.Settings;
using Xunit;

namespace BabelPlayer.Tests;

[CollectionDefinition("PiperTtsProviderTests", DisableParallelization = true)]
public sealed class PiperTtsProviderTestsCollectionDefinition;

[Collection("PiperTtsProviderTests")]
public sealed class PiperTtsProviderTests : IDisposable
{
    private const string TestVoice = "en_US-lessac-medium";

    private readonly string _testDir;
    private readonly string _translationJsonPath;
    private readonly string _outputAudioPath;
    private readonly string _modelDir;
    private readonly AppLog _log;

    public PiperTtsProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"babel-piper-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _modelDir = Path.Combine(_testDir, "models");
        Directory.CreateDirectory(_modelDir);
        _translationJsonPath = Path.Combine(_testDir, "translation.json");
        _outputAudioPath = Path.Combine(_testDir, "output.wav");
        _log = new AppLog(Path.Combine(_testDir, "test.log"));
    }

    public void Dispose()
    {
        _log.Dispose();

        try { Directory.Delete(_testDir, recursive: true); }
        catch { }
    }

    [Fact]
    public void Constructor_AcceptsValidParameters()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        Assert.NotNull(provider);
    }

    [Fact]
    public void Constructor_AcceptsNullModelDir()
    {
        using var provider = new PiperTtsProvider(_log, null!);
        Assert.NotNull(provider);
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsFileNotFoundException_WhenTranslationJsonNotFound()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new TtsRequest("nonexistent.json", _outputAudioPath, TestVoice);

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenTranslationJsonPathNull()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new TtsRequest(null!, _outputAudioPath, TestVoice);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateTtsAsync_ThrowsArgumentException_WhenOutputAudioPathNull()
    {
        CreateSampleTranslationJson();

        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new TtsRequest(_translationJsonPath, null!, TestVoice);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsEmpty()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest(string.Empty, _outputAudioPath, TestVoice);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenTextIsWhitespace()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest("   ", _outputAudioPath, TestVoice);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_ThrowsArgumentException_WhenOutputPathNull()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest("Hello world", null!, TestVoice);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GenerateSegmentTtsAsync(request));
    }

    [Fact]
    public async Task GenerateSegmentTtsAsync_SupportsCancellation()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var request = new SingleSegmentTtsRequest("This is a test", _outputAudioPath, TestVoice);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GenerateSegmentTtsAsync(request, cts.Token));
    }

    [Fact]
    public void CheckReadiness_ReturnsNotReady_WhenVoiceNotDownloaded()
    {
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var settings = new AppSettings
        {
            TtsVoice = "nonexistent-voice",
            PiperModelDir = _modelDir,
        };

        var readiness = provider.CheckReadiness(settings);

        Assert.False(readiness.IsReady);
        Assert.True(readiness.RequiresModelDownload);
        Assert.Contains("not downloaded", readiness.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CheckReadiness_ReturnsReady_WhenVoiceDownloadedAndPiperExists()
    {
        CreateVoiceModel(TestVoice);
        using var fakePiper = new FakePiperContext(_testDir);
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var settings = new AppSettings
        {
            TtsVoice = TestVoice,
            PiperModelDir = _modelDir,
        };

        var readiness = provider.CheckReadiness(settings);

        Assert.True(readiness.IsReady);
        Assert.Null(readiness.BlockingReason);
    }

    [Fact]
    public async Task EnsureReadyAsync_ReturnsTrue_WhenVoiceAlreadyDownloaded()
    {
        CreateVoiceModel(TestVoice);
        using var provider = new PiperTtsProvider(_log, _modelDir);
        var settings = new AppSettings
        {
            TtsVoice = TestVoice,
            PiperModelDir = _modelDir,
        };

        var ready = await provider.EnsureReadyAsync(settings, progress: null);

        Assert.True(ready);
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task GenerateSegmentTtsAsync_ReusesPersistentWorkerAcrossRequests()
    {
        CreateVoiceModel(TestVoice);
        using var fakePiper = new FakePiperContext(_testDir);
        using var provider = CreateWorkerBackedProvider();
        var outputPath1 = Path.Combine(_testDir, "segment-1.wav");
        var outputPath2 = Path.Combine(_testDir, "segment-2.wav");

        var first = await provider.GenerateSegmentTtsAsync(
            new SingleSegmentTtsRequest("Hello from Piper", outputPath1, TestVoice));
        var second = await provider.GenerateSegmentTtsAsync(
            new SingleSegmentTtsRequest("Second request", outputPath2, TestVoice));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(new FileInfo(outputPath1).Length, first.FileSizeBytes);
        Assert.Equal(new FileInfo(outputPath2).Length, second.FileSizeBytes);
        Assert.Single(ReadNonEmptyLines(fakePiper.WorkerStartLogPath));
        Assert.Equal(2, ReadNonEmptyLines(fakePiper.CliLogPath).Length);
        Assert.Contains("Hello from Piper", File.ReadAllText(outputPath1));
        Assert.Contains("Second request", File.ReadAllText(outputPath2));
    }

    [Fact]
    [Trait("Category", "RequiresPython")]
    public async Task GenerateTtsAsync_UsesSegmentComposerAndWorkerPath()
    {
        CreateVoiceModel(TestVoice);
        CreateSampleTranslationJson("Hello world from composer");
        using var fakePiper = new FakePiperContext(_testDir);
        using var provider = CreateWorkerBackedProvider();

        var result = await provider.GenerateTtsAsync(
            new TtsRequest(_translationJsonPath, _outputAudioPath, TestVoice));

        Assert.True(result.Success);
        Assert.True(File.Exists(_outputAudioPath));
        Assert.Single(ReadNonEmptyLines(fakePiper.WorkerStartLogPath));
        Assert.Single(ReadNonEmptyLines(fakePiper.CliLogPath));
        Assert.Contains("Hello world from composer", File.ReadAllText(_outputAudioPath));
    }

    private PiperTtsProvider CreateWorkerBackedProvider()
    {
        var pythonPath = DependencyLocator.FindPython();
        Assert.False(string.IsNullOrWhiteSpace(pythonPath));

        return new PiperTtsProvider(
            _log,
            _modelDir,
            pythonPath!,
            workerScriptPath: FindRepoPath("inference", "workers", "piper_worker.py"));
    }

    private void CreateVoiceModel(string voice)
    {
        File.WriteAllText(Path.Combine(_modelDir, $"{voice}.onnx"), "fake-model");
        File.WriteAllText(Path.Combine(_modelDir, $"{voice}.onnx.json"), "{}");
    }

    private void CreateSampleTranslationJson(string translatedText = "Hello world")
    {
        var json = $$"""
        {
          "sourceLanguage": "es",
          "targetLanguage": "en",
          "segments": [
            {
              "id": "segment_0.0",
              "start": 0.0,
              "end": 2.5,
              "text": "Hola mundo",
              "translatedText": "{{translatedText}}"
            }
          ]
        }
        """;
        File.WriteAllText(_translationJsonPath, json);
    }

    private static string[] ReadNonEmptyLines(string path)
    {
        if (!File.Exists(path))
            return [];

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static string FindRepoPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repo path for: {Path.Combine(parts)}");
    }

    private sealed class FakePiperContext : IDisposable
    {
        private readonly string? _previousPath;
        private readonly string? _previousCliLog;
        private readonly string? _previousWorkerStartLog;
        private readonly string _binDir;

        public FakePiperContext(string rootDir)
        {
            _binDir = Path.Combine(rootDir, $"fake-piper-bin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_binDir);

            CliLogPath = Path.Combine(rootDir, $"piper-cli-{Guid.NewGuid():N}.log");
            WorkerStartLogPath = Path.Combine(rootDir, $"piper-worker-{Guid.NewGuid():N}.log");

            CreateExecutable();

            _previousPath = Environment.GetEnvironmentVariable("PATH");
            _previousCliLog = Environment.GetEnvironmentVariable("PIPER_FAKE_LOG");
            _previousWorkerStartLog = Environment.GetEnvironmentVariable("PIPER_WORKER_START_LOG");

            var newPath = string.IsNullOrWhiteSpace(_previousPath)
                ? _binDir
                : string.Join(Path.PathSeparator, _binDir, _previousPath);

            Environment.SetEnvironmentVariable("PATH", newPath);
            Environment.SetEnvironmentVariable("PIPER_FAKE_LOG", CliLogPath);
            Environment.SetEnvironmentVariable("PIPER_WORKER_START_LOG", WorkerStartLogPath);
        }

        public string CliLogPath { get; }

        public string WorkerStartLogPath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _previousPath);
            Environment.SetEnvironmentVariable("PIPER_FAKE_LOG", _previousCliLog);
            Environment.SetEnvironmentVariable("PIPER_WORKER_START_LOG", _previousWorkerStartLog);

            try { Directory.Delete(_binDir, recursive: true); }
            catch { }
        }

        private void CreateExecutable()
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(_binDir, "piper.cmd"), WindowsScript);
                return;
            }

            var scriptPath = Path.Combine(_binDir, "piper");
            File.WriteAllText(scriptPath, UnixScript);
            File.SetUnixFileMode(
                scriptPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute |
                UnixFileMode.GroupRead |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead |
                UnixFileMode.OtherExecute);
        }

        private const string WindowsScript = """
@echo off
if "%~1"=="--version" (
  echo fake piper 1.0
  exit /b 0
)
setlocal EnableDelayedExpansion
set "output="
set "model="
:parse
if "%~1"=="" goto run
if /I "%~1"=="--model" (
  set "model=%~2"
  shift
  shift
  goto parse
)
if /I "%~1"=="--output_file" (
  set "output=%~2"
  shift
  shift
  goto parse
)
shift
goto parse

:run
if not defined output (
  echo missing --output_file 1>&2
  exit /b 1
)
if defined PIPER_FAKE_LOG echo cli model=!model! output=!output!>>"%PIPER_FAKE_LOG%"
for %%I in ("!output!") do if not exist "%%~dpI" mkdir "%%~dpI" >nul 2>&1
set "stdin_file=%TEMP%\piper-stdin-%RANDOM%%RANDOM%.txt"
more > "!stdin_file!"
(
  <nul set /p "=model=!model!;text="
  type "!stdin_file!"
) > "!output!"
del "!stdin_file!" >nul 2>&1
exit /b 0
""";

        private const string UnixScript = """
#!/usr/bin/env sh
if [ "$1" = "--version" ]; then
  echo "fake piper 1.0"
  exit 0
fi

output=""
model=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --model)
      model="$2"
      shift 2
      ;;
    --output_file)
      output="$2"
      shift 2
      ;;
    *)
      shift
      ;;
  esac
done

if [ -n "$PIPER_FAKE_LOG" ]; then
  printf 'cli model=%s output=%s\n' "$model" "$output" >> "$PIPER_FAKE_LOG"
fi

mkdir -p "$(dirname "$output")"
printf 'model=%s;text=' "$model" > "$output"
cat >> "$output"
""";
    }
}
