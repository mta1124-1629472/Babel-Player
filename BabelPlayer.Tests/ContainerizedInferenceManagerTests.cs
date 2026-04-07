using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace BabelPlayer.Tests;

public sealed class ContainerizedInferenceManagerTests : IDisposable
{
    private readonly string _dir;
    private readonly AppLog _log;
    private readonly string _composeFilePath;

    public ContainerizedInferenceManagerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"babel-container-manager-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _log = new AppLog(Path.Combine(_dir, "test.log"));
        _composeFilePath = Path.Combine(_dir, "docker-compose.yml");
        File.WriteAllText(_composeFilePath, "services:\n  inference:\n    image: test\n");
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task EnsureStartedAsync_AppStartup_SkipsWhenNoContainerizedRuntimeAndAlwaysRunDisabled()
    {
        var startCalls = 0;
        var manager = CreateManager(
            healthCheckFunc: (_, _, _) => Task.FromResult(ContainerHealthStatus.Unavailable("http://localhost:8000", "down")),
            startComposeFunc: (_, _) =>
            {
                startCalls++;
                return Task.FromResult(new ContainerComposeStartResult(true, "started", string.Empty));
            });

        var result = await manager.EnsureStartedAsync(
            new AppSettings { AlwaysStartLocalGpuRuntimeAtAppStart = false },
            ContainerizedStartupTrigger.AppStartup);

        Assert.False(result.Attempted);
        Assert.Equal(0, startCalls);
        Assert.Contains("no containerized runtime requested", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureStartedAsync_AppStartup_AttemptsWhenAlwaysRunEnabled()
    {
        var healthy = false;
        var startCalls = 0;
        var manager = CreateManager(
            healthCheckFunc: (_, _, _) => Task.FromResult(
                healthy
                    ? new ContainerHealthStatus(true, false, null, "http://localhost:8000", null)
                    : ContainerHealthStatus.Unavailable("http://localhost:8000", "down")),
            startComposeFunc: (_, _) =>
            {
                startCalls++;
                healthy = true;
                return Task.FromResult(new ContainerComposeStartResult(true, "started", string.Empty));
            });

        var settings = new AppSettings
        {
            AlwaysRunContainerAtAppStart = true,
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            ContainerizedServiceUrl = "http://localhost:8000",
        };

        var result = await manager.EnsureStartedAsync(settings, ContainerizedStartupTrigger.AppStartup);

        Assert.True(result.Attempted);
        Assert.True(result.IsReady);
        Assert.Equal(1, startCalls);
    }

    [Fact]
    public async Task EnsureStartedAsync_SkipsWhenEffectiveServiceUrlIsRemote()
    {
        var startCalls = 0;
        var manager = CreateManager(
            healthCheckFunc: (_, _, _) => Task.FromResult(ContainerHealthStatus.Unavailable("http://example.com:8000", "down")),
            startComposeFunc: (_, _) =>
            {
                startCalls++;
                return Task.FromResult(new ContainerComposeStartResult(true, "started", string.Empty));
            });

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            TranscriptionProfile = ComputeProfile.Gpu,
            ContainerizedServiceUrl = "http://example.com:8000",
        };

        var result = await manager.EnsureStartedAsync(settings, ContainerizedStartupTrigger.SettingsChanged);

        Assert.False(result.Attempted);
        Assert.Equal(0, startCalls);
        Assert.Contains("not local loopback", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureStartedAsync_DedupesConcurrentRequests()
    {
        var healthy = false;
        var startCalls = 0;
        var manager = CreateManager(
            healthCheckFunc: (_, _, _) => Task.FromResult(
                healthy
                    ? new ContainerHealthStatus(true, false, null, "http://localhost:8000", null)
                    : ContainerHealthStatus.Unavailable("http://localhost:8000", "down")),
            startComposeFunc: async (_, _) =>
            {
                startCalls++;
                await Task.Delay(100);
                healthy = true;
                return new ContainerComposeStartResult(true, "started", string.Empty);
            });

        var settings = new AppSettings
        {
            PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
            TranscriptionProfile = ComputeProfile.Gpu,
            ContainerizedServiceUrl = "http://localhost:8000",
        };

        var first = manager.EnsureStartedAsync(settings, ContainerizedStartupTrigger.Execution);
        var second = manager.EnsureStartedAsync(settings, ContainerizedStartupTrigger.SettingsChanged);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, startCalls);
        Assert.All(results, result => Assert.True(result.IsReady));
    }

    [Fact]
    public async Task RequestEnsureStarted_UnexpectedFailure_IsLoggedWithoutFaultingBackgroundTask()
    {
        var manager = CreateManager(
            healthCheckFunc: (_, _, _) => Task.FromResult(ContainerHealthStatus.Unavailable("http://localhost:8000", "down")),
            startComposeFunc: (_, _) => throw new InvalidOperationException("docker compose crashed"));

        manager.RequestEnsureStarted(
            new AppSettings
            {
                AlwaysRunContainerAtAppStart = true,
                PreferredLocalGpuBackend = GpuHostBackend.DockerHost,
                ContainerizedServiceUrl = "http://localhost:8000",
            },
            ContainerizedStartupTrigger.AppStartup);

        var logContents = await WaitForLogAsync("Container autostart failed unexpectedly.");

        Assert.Contains("Container autostart failed unexpectedly.", logContents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unobserved task exception", logContents, StringComparison.OrdinalIgnoreCase);
    }

    private ContainerizedInferenceManager CreateManager(
        Func<string, TimeSpan, CancellationToken, Task<ContainerHealthStatus>> healthCheckFunc,
        Func<ContainerComposeStartRequest, CancellationToken, Task<ContainerComposeStartResult>> startComposeFunc)
    {
        return new ContainerizedInferenceManager(
            _log,
            probe: null,
            healthCheckFunc: healthCheckFunc,
            startComposeFunc: startComposeFunc,
            dockerResolver: () => "docker",
            composeFileResolver: () => _composeFilePath);
    }

    private async Task<string> WaitForLogAsync(string expectedText)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();
            if (File.Exists(_log.LogFilePath))
            {
                var contents = await File.ReadAllTextAsync(_log.LogFilePath, cts.Token);
                if (contents.Contains(expectedText, StringComparison.OrdinalIgnoreCase))
                    return contents;
            }

            await Task.Delay(25, cts.Token);
        }
    }
}
