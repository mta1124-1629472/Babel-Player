using System.Runtime.Versioning;
using BabelPlayer.Core;

namespace BabelPlayer.App;

/// <summary>
/// Selects the correct <see cref="ICredentialStore"/> implementation for the current OS.
/// Windows : DPAPI-backed <see cref="SecureSettingsStore"/>.
/// Linux/macOS : AES-256-GCM <see cref="XdgCredentialStore"/>.
/// </summary>
public static class CredentialStoreFactory
{
    [SupportedOSPlatformGuard("windows")]
    public static ICredentialStore Create() =>
        OperatingSystem.IsWindows()
            ? new SecureSettingsStore()
            : new XdgCredentialStore();
}
