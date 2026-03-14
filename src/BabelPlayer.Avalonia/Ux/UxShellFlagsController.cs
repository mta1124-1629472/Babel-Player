using BabelPlayer.App;

namespace BabelPlayer.Avalonia;

internal sealed class UxShellFlagsController
{
    public event Action<UxShellFlags>? Changed;

    public UxShellFlags Current { get; private set; } = UxShellFlags.Default;

    public void BeginOpenRequest(string? mediaPath)
    {
        Update(Current with
        {
            IsOpenRequestInFlight = true,
            PendingOpenMediaPath = mediaPath,
            IsEndActionsVisible = false,
            EndedMediaPath = null
        });
    }

    public void CompleteOpenRequest()
    {
        Update(Current with
        {
            IsOpenRequestInFlight = false,
            PendingOpenMediaPath = null
        });
    }

    public void CancelOpenRequest()
    {
        Update(Current with
        {
            IsOpenRequestInFlight = false,
            PendingOpenMediaPath = null,
            IsStartupGateBlocking = false,
            StartupGateMediaPath = null
        });
    }

    public void ShowResumePrompt(string mediaPath, TimeSpan resumePosition)
    {
        Update(Current with
        {
            IsResumePromptVisible = true,
            ResumePromptMediaPath = mediaPath,
            ResumePromptPosition = resumePosition,
            IsOpenRequestInFlight = false,
            PendingOpenMediaPath = null
        });
    }

    public void HideResumePrompt()
    {
        Update(Current with
        {
            IsResumePromptVisible = false,
            ResumePromptMediaPath = null,
            ResumePromptPosition = null
        });
    }

    public void SetStartupGateBlocking(bool isBlocking, string? mediaPath = null)
    {
        Update(Current with
        {
            IsStartupGateBlocking = isBlocking,
            StartupGateMediaPath = isBlocking ? mediaPath : null
        });
    }

    public void SetSubtitlePanelOpen(bool isOpen)
    {
        Update(Current with
        {
            IsSubtitlePanelOpen = isOpen
        });
    }

    public void SetSettingsPanelOpen(bool isOpen)
    {
        Update(Current with
        {
            IsSettingsPanelOpen = isOpen
        });
    }

    public void SetRuntimePromptVisible(bool isVisible)
    {
        Update(Current with
        {
            IsRuntimePromptVisible = isVisible
        });
    }

    public void SetCredentialPromptVisible(bool isVisible)
    {
        Update(Current with
        {
            IsCredentialPromptVisible = isVisible
        });
    }

    public void ShowEndActions(string? mediaPath)
    {
        Update(Current with
        {
            IsEndActionsVisible = true,
            EndedMediaPath = mediaPath,
            IsOpenRequestInFlight = false,
            PendingOpenMediaPath = null,
            IsStartupGateBlocking = false,
            StartupGateMediaPath = null,
            IsResumePromptVisible = false,
            ResumePromptMediaPath = null,
            ResumePromptPosition = null
        });
    }

    public void HideEndActions()
    {
        Update(Current with
        {
            IsEndActionsVisible = false,
            EndedMediaPath = null
        });
    }

    public void ShowBanner(string message, string severity)
    {
        Update(Current with
        {
            BannerMessage = message,
            BannerSeverity = severity
        });
    }

    public void ClearBanner()
    {
        Update(Current with
        {
            BannerMessage = null,
            BannerSeverity = null
        });
    }

    private void Update(UxShellFlags next)
    {
        if (Equals(Current, next))
        {
            return;
        }

        Current = next;
        Changed?.Invoke(Current);
    }
}
