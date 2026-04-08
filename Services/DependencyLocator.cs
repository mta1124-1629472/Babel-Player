using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Babel.Player.Models;
using Babel.Player.Services.Credentials;
using Babel.Player.Services.Registries;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

/// <summary>
/// Probes the local environment for required external tools (Python, ffmpeg).
/// Returns the working executable path, or null if the tool cannot be found.
/// </summary>
public static class DependencyLocator
{
    private const int ProbeTimeoutMs = 500;

    /// <summary>Returns a working Python executable path, or null if not found.</summary>
    public static string? FindPython()
    {
        var appDir = AppContext.BaseDirectory;
        
        // Check managed CPU runtime first (preferred for CPU-only operations)
        var managedCpuPython = ManagedRuntimeLayout.GetCpuPythonPath();
        if (ProbePythonCandidate(managedCpuPython, requirePip: true))
            return managedCpuPython;
        
        // Fall back to managed GPU runtime
        var managedGpuPython = ManagedRuntimeLayout.GetManagedPythonPath();
        if (ProbePythonCandidate(managedGpuPython, requirePip: true))
            return managedGpuPython;


        var candidates = new[]
        {
            Path.Combine(appDir, "python.exe"),
            Path.Combine(appDir, "python", "python.exe"),
            "python",
            "python3",
        };

        return ProbePython(candidates, requirePip: true)
            ?? ProbePython(candidates, requirePip: false);
    }

    /// <summary>Returns a working uv executable path, or null if not found.</summary>
    public static string? FindUv()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "uv.exe"),
            Path.Combine(appDir, "tools", "uv.exe"),
            Path.Combine(appDir, "tools", "win-x64", "uv.exe"),
            "uv",
        };
        return Probe(candidates, "--version");
    }

    /// <summary>Returns a working ffmpeg executable path, or null if not found.</summary>
    public static string? FindFfmpeg()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "ffmpeg.exe"),
            Path.Combine(appDir, "tools", "ffmpeg.exe"),
            "ffmpeg",
        };
        return Probe(candidates, "-version");
    }

    /// <summary>Returns a working piper executable path, or null if not found.</summary>
    public static string? FindPiper()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, $"{ProviderNames.Piper}.exe"),
            Path.Combine(appDir, ProviderNames.Piper, $"{ProviderNames.Piper}.exe"),
            ProviderNames.Piper,
        };
        return Probe(candidates, "--version");
    }

    /// <summary>Returns a working docker executable path, or null if not found.</summary>
    public static string? FindDocker()
    {
        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "docker.exe"),
            Path.Combine(appDir, "tools", "docker.exe"),
            "docker",
        };
        return Probe(candidates, "--version");
    }

    private static string? Probe(string[] candidates, string versionArg)
    {
        foreach (var path in candidates)
        {
            var resolved = ResolveExecutable(path);
            if (resolved is null)
                continue;

            if (ProbeExecutable(resolved, versionArg))
                return resolved;
        }
        return null;
    }

    private static string? ProbePython(string[] candidates, bool requirePip)
    {
        foreach (var candidate in candidates)
        {
            var resolved = ResolveExecutable(candidate);
            if (resolved is null)
                continue;

            if (ProbePythonCandidate(resolved, requirePip))
                return resolved;
        }

        return null;
    }

    private static bool ProbePythonCandidate(string candidate, bool requirePip)
    {
        var resolved = ResolveExecutable(candidate);
        if (resolved is null || !ProbeExecutable(resolved, "--version"))
            return false;

        return !requirePip || ProbeExecutable(resolved, "-m pip --version");
    }

    private static bool ProbeExecutable(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            if (proc.WaitForExit(ProbeTimeoutMs) && proc.ExitCode == 0)
                return true;

            try { proc.Kill(); } catch { /* best-effort cleanup */ }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves a candidate executable name or path to an existing filesystem path.
    /// </summary>
    /// <param name="candidate">An executable file path or command name; if it contains directory separators or is rooted it is treated as an explicit path, otherwise it is looked up on the system PATH (and PATHEXT on Windows).</param>
    /// <returns>The full path to an existing executable if found, or <c>null</c> if the candidate is empty or no matching file exists.</returns>
    private static string? ResolveExecutable(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        // Absolute or relative explicit path
        if (candidate.Contains(Path.DirectorySeparatorChar) ||
            candidate.Contains(Path.AltDirectorySeparatorChar) ||
            Path.IsPathRooted(candidate))
        {
            return File.Exists(candidate) ? candidate : null;
        }

        // Command name: resolve against PATH (and PATHEXT on Windows) before spawning.
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        var extensions = GetExecutableExtensions();
        var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in dirs)
        {
            var trimmedDir = dir.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmedDir))
                continue;

            foreach (var ext in extensions)
            {
                var full = Path.Combine(trimmedDir, candidate + ext);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }

    /// <summary>
    /// Get the file extensions to try when resolving an executable name on the current platform.
    /// </summary>
    /// <summary>
    /// Get the list of filename extensions to try when resolving an executable name on the current platform.
    /// </summary>
    /// <returns>An array of extensions to append when searching for executables. On Windows the list is parsed from the PATHEXT environment variable (entries start with a dot and an empty string is included); if PATHEXT is missing returns { ".exe", ".cmd", ".bat", "" }. On non-Windows returns an array containing only the empty string.</returns>
    private static string[] GetExecutableExtensions()
    {
        if (!OperatingSystem.IsWindows())
            return [string.Empty];

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathext))
            return [".exe", ".cmd", ".bat", string.Empty];

        var parsed = pathext
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(ext => ext.Trim())
            .Where(ext => !string.IsNullOrWhiteSpace(ext))
            .ToList();

        if (!parsed.Contains(string.Empty))
            parsed.Add(string.Empty);

        return [.. parsed];
    }

    /// <summary>
    /// Bootstraps and returns a SessionWorkflowCoordinator by constructing and wiring host managers, registries, and a session snapshot store, and by requesting containerized services to start.
    /// Handles fallback creation if the primary initialization fails (e.g., due to corrupt state files).
    /// </summary>
    /// <param name="appDataRoot">Filesystem root used to locate the session snapshot at '{appDataRoot}/state/current-session.json'.</param>
    /// <param name="startupLog">Optional logger that receives startup errors if primary initialization fails.</param>
    /// <param name="primaryGpuManager">Outputs the ManagedVenvHostManager instance chosen as the primary GPU-capable host manager.</param>
    /// <summary>
    /// Constructs and initializes a SessionWorkflowCoordinator wired with host managers, registries, stores, and containerized probes; if primary initialization fails, performs a fallback initialization and continues with an empty session state.
    /// </summary>
    /// <param name="appDataRoot">Root application data directory where the session snapshot is stored (the snapshot file is placed at {appDataRoot}/state/current-session.json).</param>
    /// <param name="startupLog">Optional logger used to record initialization failures encountered during the primary initialization path.</param>
    /// <param name="primaryGpuManager">Outputs the ManagedVenvHostManager selected as the primary GPU-capable host manager (may be null if initialization fails before manager creation).</param>
    /// <returns>The initialized SessionWorkflowCoordinator.</returns>
    /// <exception cref="InvalidOperationException">Thrown if both primary and fallback initialization paths fail to produce a coordinator.</exception>
    public static SessionWorkflowCoordinator CreateSessionCoordinator(
        AppLog appLog,
        AppSettings appSettings,
        PerSessionSnapshotStore perSessionStore,
        RecentSessionsStore recentStore,
        ApiKeyStore apiKeyStore,
        IMediaTransportManager transportManager,
        string appDataRoot,
        AppLog? startupLog,
        out ManagedVenvHostManager? primaryGpuManager)
    {
        SessionWorkflowCoordinator? coordinator = null;
        primaryGpuManager = null;

        try
        {
            appLog.Info("App startup: initializing session coordinator.");
            var containerizedProbe = new ContainerizedServiceProbe(appLog);
            var managedHostManager = new ManagedVenvHostManager(appLog, containerizedProbe);
            primaryGpuManager = managedHostManager;
            var dockerHostManager = new ContainerizedInferenceManager(appLog, containerizedProbe);
            var containerizedManager = new CompositeInferenceHostManager(managedHostManager, dockerHostManager);
            
            var transcriptionRegistry = new TranscriptionRegistry(appLog, containerizedProbe);
            var translationRegistry = new TranslationRegistry(appLog, containerizedProbe);
            var ttsRegistry = new TtsRegistry(appLog, containerizedProbe);
            
            var store = new SessionSnapshotStore(Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
            
            coordinator = new SessionWorkflowCoordinator(
                store, appLog, appSettings, perSessionStore, recentStore, 
                transcriptionRegistry, translationRegistry, ttsRegistry, 
                transportManager: transportManager, keyStore: apiKeyStore, 
                containerizedProbe: containerizedProbe, containerizedInferenceManager: containerizedManager);
            
            coordinator.Initialize();
            containerizedManager.RequestEnsureStarted(appSettings, ContainerizedStartupTrigger.AppStartup);
            appLog.Info("App startup: session coordinator ready.");
        }
        catch (Exception ex)
        {
            startupLog?.Error("App startup: session initialization failed. Continuing with empty session.", ex);
            
            // Fallback initialization
            var containerizedProbe = new ContainerizedServiceProbe(appLog);
            var managedHostManager = new ManagedVenvHostManager(appLog, containerizedProbe);
            primaryGpuManager = managedHostManager;
            var dockerHostManager = new ContainerizedInferenceManager(appLog, containerizedProbe);
            var containerizedManager = new CompositeInferenceHostManager(managedHostManager, dockerHostManager);
            
            var transcriptionRegistry = new TranscriptionRegistry(appLog, containerizedProbe);
            var translationRegistry = new TranslationRegistry(appLog, containerizedProbe);
            var ttsRegistry = new TtsRegistry(appLog, containerizedProbe);
            
            var fallbackStore = new SessionSnapshotStore(
                Path.Combine(appDataRoot, "state", "current-session.json"), appLog);
            
            coordinator = new SessionWorkflowCoordinator(
                fallbackStore, appLog, appSettings, perSessionStore, recentStore, 
                transcriptionRegistry, translationRegistry, ttsRegistry, 
                transportManager: transportManager, keyStore: apiKeyStore, 
                containerizedProbe: containerizedProbe, containerizedInferenceManager: containerizedManager);
            
            containerizedManager.RequestEnsureStarted(appSettings, ContainerizedStartupTrigger.AppStartup);
        }

        return coordinator ?? throw new InvalidOperationException("Failed to initialize session coordinator after both primary and fallback initialization paths.");
    }
}