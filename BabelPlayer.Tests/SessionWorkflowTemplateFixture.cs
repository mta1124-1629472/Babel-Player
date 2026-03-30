using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

public sealed class SessionWorkflowTemplateFixture : IAsyncDisposable
{
    private readonly string _sharedRootDir;
    private readonly string _templateRootDir;
    private readonly string _logRootDir;
    private readonly string _testMediaPath;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _templates = new();

    public SessionWorkflowTemplateFixture()
    {
        _sharedRootDir = Path.Combine(Path.GetTempPath(), $"BabelPlayerSharedTemplates_{Guid.NewGuid():N}");
        _templateRootDir = Path.Combine(_sharedRootDir, "templates");
        _logRootDir = Path.Combine(_sharedRootDir, "logs");

        Directory.CreateDirectory(_templateRootDir);
        Directory.CreateDirectory(_logRootDir);

        _testMediaPath = Path.Combine(AppContext.BaseDirectory, "test-assets", "video", "sample.mp4");
    }

    public string TestMediaPath => _testMediaPath;

    public async Task<string> GetPreparedTemplateAsync(string templateName)
    {
        var lazy = _templates.GetOrAdd(
            templateName,
            name => new Lazy<Task<string>>(
                () => BuildTemplateAsync(name),
                LazyThreadSafetyMode.ExecutionAndPublication));

        return await lazy.Value;
    }

    public string CreateCaseDirectory(string caseName)
    {
        var dir = Path.Combine(_sharedRootDir, "cases", $"{Sanitize(caseName)}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetStateFilePath(string dir) => Path.Combine(dir, "session.json");

    public static void CopyDirectory(string sourceDir, string destDir)
    {
        if (Directory.Exists(destDir))
        {
            Directory.Delete(destDir, recursive: true);
        }

        Directory.CreateDirectory(destDir);

        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(sourceDir, destDir);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task<string> BuildTemplateAsync(string templateName)
    {
        if (!File.Exists(_testMediaPath))
        {
            throw new FileNotFoundException($"Test media not found: {_testMediaPath}");
        }

        var dir = Path.Combine(_templateRootDir, templateName);
        var stateFilePath = GetStateFilePath(dir);

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);

        var log = new AppLog(Path.Combine(_logRootDir, $"{templateName}.log"));
        var store = new SessionSnapshotStore(stateFilePath, log);
        var coordinator = new SessionWorkflowCoordinator(store, log);

        coordinator.Initialize();

        switch (templateName)
        {
            case "media-loaded":
                coordinator.LoadMedia(_testMediaPath);
                break;

            case "transcribed":
                coordinator.LoadMedia(_testMediaPath);
                await coordinator.TranscribeMediaAsync();
                break;

            case "translated":
                coordinator.LoadMedia(_testMediaPath);
                await coordinator.TranscribeMediaAsync();
                await coordinator.TranslateTranscriptAsync("en", "es");
                break;

            case "tts":
                coordinator.LoadMedia(_testMediaPath);
                await coordinator.TranscribeMediaAsync();
                await coordinator.TranslateTranscriptAsync("en", "es");
                await coordinator.GenerateTtsAsync();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(templateName), templateName, "Unknown template name.");
        }

        return dir;
    }

    private static string Sanitize(string input)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            input = input.Replace(ch, '_');
        }

        return input;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_sharedRootDir))
        {
            Directory.Delete(_sharedRootDir, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
