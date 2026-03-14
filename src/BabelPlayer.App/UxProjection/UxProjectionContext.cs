namespace BabelPlayer.App;

public sealed record UxProjectionContext(
    ShellPlaybackStateSnapshot Playback,
    ShellProjectionSnapshot ShellProjection,
    SubtitleWorkflowSnapshot SubtitleWorkflow,
    CredentialSetupSnapshot CredentialSetup,
    UxShellFlags ShellFlags);
