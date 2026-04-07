using System.Linq;
using System.Reflection;

namespace Babel.Player;

/// <summary>
/// Exposes build-time metadata (version, build date, configuration) baked into
/// the assembly by the release workflow via MSBuild properties.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The informational version (e.g. "1.2.0"), stripped of any git-commit hash suffix.
    /// Falls back to "0.0.0" for local dev builds where -p:InformationalVersion is not set.
    /// </summary>
    public static string Version =>
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// The ISO-8601 build date (e.g. "2026-04-03") injected by the release workflow.
    /// Falls back to "unknown" for local dev builds.
    /// </summary>
    public static string BuildDate =>
        typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")
            ?.Value
        ?? "unknown";

    /// <summary>
    /// True when the app was compiled with the BABEL_DEV symbol (i.e. dotnet run/build -c Dev).
    /// Use this to gate dev-only behaviour at runtime in addition to #if BABEL_DEV guards.
    /// </summary>
    public static bool IsDevBuild =>
#if BABEL_DEV
        true;
#else
        false;
#endif

    /// <summary>
    /// Human-readable configuration name, e.g. "Dev", "Debug", or "Release".
    /// </summary>
    public static string Configuration =>
#if BABEL_DEV
        "Dev";
#elif DEBUG
        "Debug";
#else
        "Release";
#endif
}
