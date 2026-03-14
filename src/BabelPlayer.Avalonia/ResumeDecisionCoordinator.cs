using Avalonia.Controls;
using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

public interface IResumeDecisionCoordinator
{
    Task<ShellResumeDecisionResult> ResolveAsync(
        Window owner,
        string path,
        TimeSpan resumePosition,
        CancellationToken cancellationToken = default);
}

public sealed class AvaloniaResumeDecisionCoordinator : IResumeDecisionCoordinator
{
    private readonly IShellPlaybackCommands _shellPlaybackCommands;

    public AvaloniaResumeDecisionCoordinator(IShellPlaybackCommands shellPlaybackCommands)
    {
        _shellPlaybackCommands = shellPlaybackCommands ?? throw new ArgumentNullException(nameof(shellPlaybackCommands));
    }

    public async Task<ShellResumeDecisionResult> ResolveAsync(
        Window owner,
        string path,
        TimeSpan resumePosition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await _shellPlaybackCommands.PauseAsync(cancellationToken);

        var displayName = Path.GetFileName(path);
        var dialog = new ResumeDecisionPromptWindow(displayName, resumePosition);
        await dialog.ShowDialog(owner);

        var decision = MapDecision(dialog.Choice ?? ResumeDecisionChoice.Dismiss);
        return await _shellPlaybackCommands.ApplyResumeDecisionAsync(decision, cancellationToken);
    }

    private static ShellResumeDecision MapDecision(ResumeDecisionChoice choice)
    {
        return choice switch
        {
            ResumeDecisionChoice.Resume => ShellResumeDecision.Resume,
            ResumeDecisionChoice.StartOver => ShellResumeDecision.StartOver,
            _ => ShellResumeDecision.Dismiss
        };
    }
}
