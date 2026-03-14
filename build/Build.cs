using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
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
    AbsolutePath NativeX64Asset => RootDirectory / "src" / "BabelPlayer.Avalonia" / "native" / "win-x64" / "libmpv-2.dll";
    AbsolutePath NativeArm64Asset => RootDirectory / "src" / "BabelPlayer.Avalonia" / "native" / "win-arm64" / "libmpv-2.dll";
    string DefaultRuntime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
    string _rawVersionToken = string.Empty;

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
            DotNetRestore(settings => settings
                .SetProjectFile(Solution));
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

    Target PublishWinX64 => _ => _
        .DependsOn(Restore, ValidateNativeX64Asset)
        .Executes(() => PublishRuntime("win-x64"));

    Target PublishWinArm64 => _ => _
        .DependsOn(Restore, ValidateNativeArm64Asset)
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
            {
                throw new Exception($"ISCC.exe not found at '{innoSetupExecutable}'. Install Inno Setup 6 or set INNO_SETUP_EXE.");
            }
        });

    Target BuildInstallerWinX64 => _ => _
        .DependsOn(PublishWinX64, EnsureInnoSetup)
        .Executes(() => BuildInstaller("win-x64"));

    Target BuildInstallerWinArm64 => _ => _
        .DependsOn(PublishWinArm64, EnsureInnoSetup)
        .Executes(() => BuildInstaller("win-arm64"));

    Target ReleaseRuntimeWinX64 => _ => _
        .DependsOn(PackagePortableWinX64, BuildInstallerWinX64);

    Target ReleaseRuntimeWinArm64 => _ => _
        .DependsOn(PackagePortableWinArm64, BuildInstallerWinArm64);

    Target ReleasePackage => _ => _
        .DependsOn(ReleaseRuntimeWinX64, ReleaseRuntimeWinArm64);

    Target ValidateNativeAssets => _ => _
        .DependsOn(ValidateNativeX64Asset, ValidateNativeArm64Asset);

    Target ValidateNativeX64Asset => _ => _
        .Executes(() =>
        {
            EnsureNativeAssetExists(NativeX64Asset, "native/win-x64/libmpv-2.dll");
        });

    Target ValidateNativeArm64Asset => _ => _
        .Executes(() =>
        {
            EnsureNativeAssetExists(NativeArm64Asset, "native/win-arm64/libmpv-2.dll");
        });

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
        EnsureDirectoryExists(publishDir, $"Publish output is missing for runtime '{runtime}' at '{publishDir}'.");
        Directory.CreateDirectory(ArtifactsDirectory);

        var archivePath = GetPortableArchivePath(runtime);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        ZipFile.CreateFromDirectory(publishDir, archivePath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    void BuildInstaller(string runtime)
    {
        var publishDir = PublishDirectory / runtime;
        EnsureDirectoryExists(publishDir, $"Publish output is missing for runtime '{runtime}' at '{publishDir}'.");
        Directory.CreateDirectory(ArtifactsDirectory);

        var installerBaseName = GetInstallerBaseName(runtime);
        var installerVersion = GetInstallerVersion();
        var innoSetupExecutable = GetInnoSetupExecutablePath();
        var arguments =
            $"/DMyAppVersion={installerVersion} /DMyPublishDir=\"{publishDir}\" /DMyOutputDir=\"{ArtifactsDirectory}\" /DMyOutputBaseFilename={installerBaseName} \"{InstallerScript}\"";

        ProcessTasks.StartProcess(innoSetupExecutable, arguments)
            .AssertZeroExitCode();
    }

    AbsolutePath GetPortableArchivePath(string runtime)
    {
        return ArtifactsDirectory / $"BabelPlayer-{GetRawVersionToken()}-portable-{GetRuntimeSuffix(runtime)}.zip";
    }

    string GetInstallerBaseName(string runtime)
    {
        return $"BabelPlayer-{GetRawVersionToken()}-setup-{GetRuntimeSuffix(runtime)}";
    }

    string GetInstallerVersion()
    {
        var rawVersionToken = GetRawVersionToken();
        return rawVersionToken.StartsWith("v", StringComparison.OrdinalIgnoreCase) && rawVersionToken.Length > 1
            ? rawVersionToken[1..]
            : rawVersionToken;
    }

    string GetRawVersionToken()
    {
        if (!string.IsNullOrWhiteSpace(_rawVersionToken))
        {
            return _rawVersionToken;
        }

        if (!string.IsNullOrWhiteSpace(ReleaseVersion))
        {
            _rawVersionToken = ReleaseVersion.Trim();
            return _rawVersionToken;
        }

        var githubRefName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
        if (!string.IsNullOrWhiteSpace(githubRefName))
        {
            _rawVersionToken = githubRefName.Trim();
            return _rawVersionToken;
        }

        var githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(githubSha))
        {
            _rawVersionToken = githubSha.Trim();
            return _rawVersionToken[..Math.Min(12, _rawVersionToken.Length)];
        }

        _rawVersionToken = "dev";
        return _rawVersionToken;
    }

    static string GetRuntimeSuffix(string runtime)
    {
        return runtime switch
        {
            "win-x64" => "win-x64",
            "win-arm64" => "win-arm64",
            _ => runtime.Replace(' ', '-')
        };
    }

    static string GetInnoSetupExecutablePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("INNO_SETUP_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe");
    }

    static void DeleteDirectoryIfExists(AbsolutePath path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    static void EnsureDirectoryExists(AbsolutePath path, string errorMessage)
    {
        if (!Directory.Exists(path))
        {
            throw new Exception(errorMessage);
        }
    }

    static void EnsureNativeAssetExists(AbsolutePath path, string relativePath)
    {
        if (!File.Exists(path))
        {
            throw new Exception($"Required native asset is missing: {relativePath}");
        }
    }
}