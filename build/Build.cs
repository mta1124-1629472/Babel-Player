using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Solution] readonly Solution Solution = null!;

    [Parameter("Configuration to build - default is Debug locally and Release on CI")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    AbsolutePath AvaloniaProject => RootDirectory / "src" / "BabelPlayer.Avalonia" / "BabelPlayer.Avalonia.csproj";
    AbsolutePath AppTestsProject => RootDirectory / "tests" / "BabelPlayer.App.Tests" / "BabelPlayer.App.Tests.csproj";
    AbsolutePath RunAvaloniaScript => RootDirectory / "scripts" / "run-avalonia.ps1";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath PublishDirectory => RootDirectory / "artifacts" / "publish";
    AbsolutePath TestResultsDirectory => RootDirectory / "artifacts" / "test-results";
    AbsolutePath InstallerScript => RootDirectory / "installer" / "BabelPlayer.iss";
    AbsolutePath NativeX64Asset   => RootDirectory / "src" / "BabelPlayer.Avalonia" / "native" / "win-x64"   / "libmpv-2.dll";
    AbsolutePath NativeArm64Asset => RootDirectory / "src" / "BabelPlayer.Avalonia" / "native" / "win-arm64" / "libmpv-2.dll";
    string DefaultRuntime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    string _rawVersionToken = string.Empty;

    // libmpv dev packages from https://github.com/shinchiro/mpv-winbuild-cmake/releases
    // Update URL + SHA256 together whenever bumping the mpv version.
    const string LibMpvArchiveX64        = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260307/mpv-dev-x86_64-20260307-git-f9190e5.7z";
    const string LibMpvArchiveX64Sha256  = "274db632b4a1849f392e2044bbeafd1e7079acd04b3eb281a03a9769a1c48bf6";
    const string LibMpvArchiveArm64      = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260307/mpv-dev-aarch64-20260307-git-f9190e5.7z";
    const string LibMpvArchiveArm64Sha256 = "faefb9d3c75d19df1d52d86f9316340f472ba81c76d3c400362d82c61ab11b39";

    [Parameter("Release version token for artifact names. Defaults to tag name or commit SHA when available")]
    readonly string ReleaseVersion = string.Empty;

    Target Clean => _ => _
        .Executes(() =>
        {
            foreach (var projectDirectory in Solution.AllProjects
                         .Select(project => (AbsolutePath) Path.GetDirectoryName(project.Path)!)
                         .Distinct())
            {
                DeleteDirectoryIfExists(projectDirectory / "bin");
                DeleteDirectoryIfExists(projectDirectory / "obj");
            }
            DeleteDirectoryIfExists(PublishDirectory);
            DeleteDirectoryIfExists(TestResultsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(settings => settings.SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(settings => settings
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            Directory.CreateDirectory(TestResultsDirectory);
            DotNetTest(settings => settings
                .SetProjectFile(AppTestsProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore()
                .SetResultsDirectory(TestResultsDirectory)
                .SetLoggers("trx;LogFileName=test-results.trx"));
        });

    Target RunAvalonia => _ => _
        .Executes(() =>
        {
            ProcessTasks.StartProcess(
                    "pwsh",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{RunAvaloniaScript}\" -Runtime {DefaultRuntime}")
                .AssertZeroExitCode();
        });

    // ── Windows targets ────────────────────────────────────────────────────────

    Target PublishWinX64 => _ => _
        .DependsOn(Restore, FetchNativeX64Asset)
        .Executes(() => PublishRuntime("win-x64"));

    Target PublishWinArm64 => _ => _
        .DependsOn(Restore, FetchNativeArm64Asset)
        .Executes(() => PublishRuntime("win-arm64"));

    Target PackagePortableWinX64 => _ => _
        .DependsOn(PublishWinX64)
        .Executes(() => PackagePortable("win-x64"));

    Target PackagePortableWinArm64 => _ => _
        .DependsOn(PublishWinArm64)
        .Executes(() => PackagePortable("win-arm64"));

    Target EnsureInnoSetup => _ => _
        .Executes(() =>
        {
            var innoSetupExecutable = GetInnoSetupExecutablePath();
            if (!File.Exists(innoSetupExecutable))
                throw new Exception($"ISCC.exe not found at '{innoSetupExecutable}'. Install Inno Setup 6 or set INNO_SETUP_EXE.");
        });

    Target BuildInstallerWinX64 => _ => _
        .DependsOn(PublishWinX64, EnsureInnoSetup)
        .Executes(() => BuildInstaller("win-x64"));

    Target BuildInstallerWinArm64 => _ => _
        .DependsOn(PublishWinArm64, EnsureInnoSetup)
        .Executes(() => BuildInstaller("win-arm64"));

    Target ReleaseRuntimeWinX64   => _ => _.DependsOn(PackagePortableWinX64,   BuildInstallerWinX64);
    Target ReleaseRuntimeWinArm64 => _ => _.DependsOn(PackagePortableWinArm64, BuildInstallerWinArm64);
    Target ReleasePackage         => _ => _.DependsOn(ReleaseRuntimeWinX64,    ReleaseRuntimeWinArm64, ReleaseRuntimeLinuxX64);

    // ── Linux targets ──────────────────────────────────────────────────────────
    // libmpv is a system package on Linux (apt install libmpv-dev / libmpv2).
    // No native asset bundling needed — users must have libmpv installed.

    Target PublishLinuxX64 => _ => _
        .DependsOn(Restore)
        .Executes(() => PublishRuntime("linux-x64"));

    Target PackagePortableLinuxX64 => _ => _
        .DependsOn(PublishLinuxX64)
        .Executes(() => PackagePortableTar("linux-x64"));

    Target ReleaseRuntimeLinuxX64 => _ => _.DependsOn(PackagePortableLinuxX64);

    // ── Native asset fetch targets (Windows only) ──────────────────────────────

    Target FetchNativeAssets => _ => _
        .DependsOn(FetchNativeX64Asset, FetchNativeArm64Asset);

    Target FetchNativeX64Asset => _ => _
        .Executes(() => FetchLibMpvDll("win-x64", LibMpvArchiveX64, LibMpvArchiveX64Sha256, NativeX64Asset));

    Target FetchNativeArm64Asset => _ => _
        .Executes(() => FetchLibMpvDll("win-arm64", LibMpvArchiveArm64, LibMpvArchiveArm64Sha256, NativeArm64Asset));

    // ── Helpers ────────────────────────────────────────────────────────────────

    void PublishRuntime(string runtime)
    {
        DotNetPublish(settings => settings
            .SetProject(AvaloniaProject)
            .SetConfiguration(Configuration.Release)
            .SetRuntime(runtime)
            .SetSelfContained(false)
            .SetOutput(PublishDirectory / runtime)
            .EnableNoRestore());
    }

    void PackagePortable(string runtime)
    {
        var publishDir = PublishDirectory / runtime;
        EnsureDirectoryExists(publishDir, $"Publish output missing for '{runtime}' at '{publishDir}'.");
        Directory.CreateDirectory(ArtifactsDirectory);
        var archivePath = GetPortableArchivePath(runtime);
        if (File.Exists(archivePath)) File.Delete(archivePath);
        ZipFile.CreateFromDirectory(publishDir, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
        Console.WriteLine($"Portable archive: {archivePath}");
    }

    void PackagePortableTar(string runtime)
    {
        var publishDir = PublishDirectory / runtime;
        EnsureDirectoryExists(publishDir, $"Publish output missing for '{runtime}' at '{publishDir}'.");
        Directory.CreateDirectory(ArtifactsDirectory);
        var archivePath = ArtifactsDirectory / $"BabelPlayer-{GetRawVersionToken()}-portable-{runtime}.tar.gz";
        if (File.Exists(archivePath)) File.Delete(archivePath);
        // Use tar (available on all modern Linux and GitHub Actions ubuntu runners)
        ProcessTasks.StartProcess("tar", $"-czf \"{archivePath}\" -C \"{publishDir}\" .")
            .AssertZeroExitCode();
        Console.WriteLine($"Portable archive: {archivePath}");
    }

    void BuildInstaller(string runtime)
    {
        var publishDir = PublishDirectory / runtime;
        EnsureDirectoryExists(publishDir, $"Publish output missing for '{runtime}' at '{publishDir}'.");
        Directory.CreateDirectory(ArtifactsDirectory);
        var installerBaseName   = GetInstallerBaseName(runtime);
        var installerVersion    = GetInstallerVersion();
        var innoSetupExecutable = GetInnoSetupExecutablePath();
        var arguments =
            $"/DMyAppVersion={installerVersion} /DMyPublishDir=\"{publishDir}\" /DMyOutputDir=\"{ArtifactsDirectory}\" /DMyOutputBaseFilename={installerBaseName} \"{InstallerScript}\"";
        ProcessTasks.StartProcess(innoSetupExecutable, arguments).AssertZeroExitCode();
    }

    AbsolutePath GetPortableArchivePath(string runtime)
        => ArtifactsDirectory / $"BabelPlayer-{GetRawVersionToken()}-portable-{GetRuntimeSuffix(runtime)}.zip";

    string GetInstallerBaseName(string runtime)
        => $"BabelPlayer-{GetRawVersionToken()}-setup-{GetRuntimeSuffix(runtime)}";

    string GetInstallerVersion()
    {
        var raw = GetRawVersionToken();
        return raw.StartsWith("v", StringComparison.OrdinalIgnoreCase) && raw.Length > 1 ? raw[1..] : raw;
    }

    string GetRawVersionToken()
    {
        if (!string.IsNullOrWhiteSpace(_rawVersionToken)) return _rawVersionToken;
        if (!string.IsNullOrWhiteSpace(ReleaseVersion))   return _rawVersionToken = ReleaseVersion.Trim();
        var githubRefName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        if (!string.IsNullOrWhiteSpace(githubRefName))    return _rawVersionToken = githubRefName.Trim();
        var githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(githubSha))
        {
            _rawVersionToken = githubSha.Trim();
            return _rawVersionToken[..Math.Min(12, _rawVersionToken.Length)];
        }
        return _rawVersionToken = "dev";
    }

    static string GetRuntimeSuffix(string runtime) => runtime switch
    {
        "win-x64"   => "win-x64",
        "win-arm64" => "win-arm64",
        "linux-x64" => "linux-x64",
        _           => runtime.Replace(' ', '-')
    };

    static string GetInnoSetupExecutablePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("INNO_SETUP_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe");
    }

    static string Find7ZipExecutable()
    {
        var fromPath = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "7z.exe"))
            .FirstOrDefault(File.Exists);
        if (fromPath != null) return fromPath;

        var programFiles = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var pf in programFiles)
        {
            var candidate = Path.Combine(pf, "7-Zip", "7z.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var gitRoot = Environment.GetEnvironmentVariable("PATH")
            ?.Split(Path.PathSeparator)
            .Select(dir =>
            {
                var d = new DirectoryInfo(dir);
                while (d?.Parent != null)
                {
                    if (d.Name.Equals("Git", StringComparison.OrdinalIgnoreCase) ||
                        d.Name.StartsWith("Git", StringComparison.OrdinalIgnoreCase))
                        return d.FullName;
                    d = d.Parent;
                }
                return null;
            })
            .FirstOrDefault(r => r != null);

        if (gitRoot != null)
        {
            foreach (var rel in new[] { @"usr\bin\7z.exe", @"mingw64\bin\7z.exe" })
            {
                var candidate = Path.Combine(gitRoot, rel);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new Exception(
            "7-Zip (7z.exe) could not be found. " +
            "Install 7-Zip from https://www.7-zip.org/ or add its folder to PATH.");
    }

    static void FetchLibMpvDll(string runtime, string downloadUrl, string expectedSha256, AbsolutePath destinationPath)
    {
        var destFile = new FileInfo(destinationPath);
        if (destFile.Exists && destFile.Length > 1_000_000)
        {
            Console.WriteLine($"[{runtime}] libmpv-2.dll already present ({destFile.Length / 1024 / 1024} MB), skipping download.");
            return;
        }

        Console.WriteLine($"[{runtime}] Fetching libmpv archive from GitHub Releases...");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var tempId  = Guid.NewGuid().ToString("N");
        var tempZip = Path.Combine(Path.GetTempPath(), $"libmpv-{runtime}-{tempId}.7z");
        var tempDir = Path.Combine(Path.GetTempPath(), $"libmpv-extract-{runtime}-{tempId}");

        try
        {
            Console.WriteLine($"[{runtime}] Downloading {downloadUrl} ...");
            ProcessTasks.StartProcess("curl", $"-L --retry 3 --retry-delay 5 -o \"{tempZip}\" \"{downloadUrl}\"")
                .AssertZeroExitCode();

            var zipInfo = new FileInfo(tempZip);
            if (!zipInfo.Exists || zipInfo.Length < 1_000_000)
                throw new Exception($"[{runtime}] Downloaded archive is too small ({zipInfo.Length} bytes).");

            Console.WriteLine($"[{runtime}] Verifying SHA256...");
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(tempZip))
            {
                var actualHash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new Exception($"[{runtime}] SHA256 mismatch!\n  Expected: {expectedSha256}\n  Actual:   {actualHash}");
            }
            Console.WriteLine($"[{runtime}] SHA256 OK. Extracting libmpv-2.dll...");

            Directory.CreateDirectory(tempDir);
            var sevenZip = Find7ZipExecutable();
            Console.WriteLine($"[{runtime}] Using 7z at: {sevenZip}");
            ProcessTasks.StartProcess(sevenZip, $"e \"{tempZip}\" -o\"{tempDir}\" libmpv-2.dll -r -y")
                .AssertZeroExitCode();

            var extracted = Directory.GetFiles(tempDir, "libmpv-2.dll", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new Exception($"[{runtime}] libmpv-2.dll not found inside the archive.");

            File.Copy(extracted, destinationPath, overwrite: true);

            var finalSize = new FileInfo(destinationPath).Length;
            Console.WriteLine($"[{runtime}] libmpv-2.dll placed at {destinationPath} ({finalSize / 1024 / 1024} MB).");

            if (finalSize < 1_000_000)
                throw new Exception($"[{runtime}] Extracted DLL is too small ({finalSize} bytes).");
        }
        finally
        {
            if (File.Exists(tempZip))      try { File.Delete(tempZip); }                      catch { }
            if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    static void DeleteDirectoryIfExists(AbsolutePath path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    static void EnsureDirectoryExists(AbsolutePath path, string errorMessage)
    {
        if (!Directory.Exists(path)) throw new Exception(errorMessage);
    }
}
