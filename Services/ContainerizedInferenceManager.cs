using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Babel.Player.Services;

/// <summary>
/// Thin lifecycle manager for the local Docker-backed inference service.
/// It only handles the current concrete case: ensuring the repo's
/// <c>docker-compose.yml</c> inference service is running when a local
/// containerized provider is selected.
/// </summary>
public sealed class ContainerizedInferenceManager
{
    private readonly AppLog _log;
    private readonly string _serviceUrl;
    private readonly string? _projectRoot;
    private readonly Func<string, int, ContainerHealthStatus> _healthProbe;
    private readonly Func<ProcessInvocation, ProcessInvocationResult> _processRunner;

    public ContainerizedInferenceManager(AppLog log, string serviceUrl)
        : this(log, serviceUrl, null, null, null)
    {
    }

    public ContainerizedInferenceManager(
        AppLog log,
        string serviceUrl,
        string? projectRoot,
        Func<string, int, ContainerHealthStatus>? healthProbe,
        Func<ProcessInvocation, ProcessInvocationResult>? processRunner)
    {
        _log = log;
        _serviceUrl = string.IsNullOrWhiteSpace(serviceUrl) ? "http://localhost:8000" : serviceUrl.Trim();
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? FindProjectRoot() : projectRoot;
        _healthProbe = healthProbe ?? ((url, timeoutSeconds) =>
            ContainerizedInferenceClient.CheckHealth(url, timeoutSeconds));
        _processRunner = processRunner ?? RunProcess;
    }

    public bool StartedByManager { get; private set; }

    public string? LastError { get; private set; }

    public ContainerHealthStatus EnsureAvailable(
        TimeSpan? startupTimeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var initial = _healthProbe(_serviceUrl, 2);
        if (initial.IsAvailable)
        {
            LastError = null;
            return initial;
        }

        if (string.IsNullOrWhiteSpace(_projectRoot))
        {
            LastError = "docker-compose.yml not found relative to the app output.";
            _log.Warning($"Containerized inference auto-start skipped: {LastError}");
            return initial;
        }

        _log.Info($"Containerized inference unavailable at {_serviceUrl}; attempting docker compose startup.");
        var up = _processRunner(new ProcessInvocation(
            FileName: "docker",
            Arguments: "compose up -d inference",
            WorkingDirectory: _projectRoot,
            TimeoutMs: 120000));

        if (up.ExitCode != 0)
        {
            LastError = BuildCommandError("docker compose up -d inference", up);
            _log.Warning($"Containerized inference auto-start failed: {LastError}");
            return ContainerHealthStatus.Unavailable(_serviceUrl, LastError);
        }

        var deadline = DateTimeOffset.UtcNow + (startupTimeout ?? TimeSpan.FromSeconds(90));
        var delay = pollInterval ?? TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var health = _healthProbe(_serviceUrl, 2);
            if (health.IsAvailable)
            {
                StartedByManager = true;
                LastError = null;
                _log.Info($"Containerized inference is healthy at {_serviceUrl}.");
                return health;
            }

            Thread.Sleep(delay);
        }

        var final = _healthProbe(_serviceUrl, 2);
        LastError = string.IsNullOrWhiteSpace(final.ErrorMessage)
            ? $"Timed out waiting for containerized inference at {_serviceUrl}."
            : $"Timed out waiting for containerized inference at {_serviceUrl}: {final.ErrorMessage}";
        _log.Warning(LastError);
        return ContainerHealthStatus.Unavailable(_serviceUrl, LastError);
    }

    public void StopManagedService()
    {
        if (!StartedByManager || string.IsNullOrWhiteSpace(_projectRoot))
            return;

        var stop = _processRunner(new ProcessInvocation(
            FileName: "docker",
            Arguments: "compose stop inference",
            WorkingDirectory: _projectRoot,
            TimeoutMs: 120000));

        if (stop.ExitCode != 0)
        {
            LastError = BuildCommandError("docker compose stop inference", stop);
            _log.Warning($"Failed to stop managed containerized inference: {LastError}");
            return;
        }

        StartedByManager = false;
        LastError = null;
        _log.Info("Stopped managed containerized inference service.");
    }

    public sealed record ProcessInvocation(
        string FileName,
        string Arguments,
        string WorkingDirectory,
        int TimeoutMs);

    public sealed record ProcessInvocationResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var compose = Path.Combine(dir.FullName, "docker-compose.yml");
            var csproj = Path.Combine(dir.FullName, "BabelPlayer.csproj");
            if (File.Exists(compose) && File.Exists(csproj))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static ProcessInvocationResult RunProcess(ProcessInvocation invocation)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = invocation.FileName,
                Arguments = invocation.Arguments,
                WorkingDirectory = invocation.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return new ProcessInvocationResult(-1, "", $"Failed to start {invocation.FileName}.");

            if (!proc.WaitForExit(invocation.TimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new ProcessInvocationResult(-1, "", $"{invocation.FileName} timed out.");
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            return new ProcessInvocationResult(proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ProcessInvocationResult(-1, "", ex.Message);
        }
    }

    private static string BuildCommandError(string command, ProcessInvocationResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        detail = string.IsNullOrWhiteSpace(detail) ? "no output" : detail.Trim();
        return $"{command} failed: {detail}";
    }
}
