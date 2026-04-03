namespace Babel.Player.Services;

public sealed partial class SessionWorkflowCoordinator
{
    private void RefreshVideoEnhancementDiagnostics()
    {
        var diagnostics = VideoEnhancementDiagnostics.Create(
            CurrentSettings,
            HardwareSnapshot,
            _latestVsrDiagnostic);

        if (VideoEnhancementDiagnostics == diagnostics)
            return;

        VideoEnhancementDiagnostics = diagnostics;
        _log.Info(
            $"Video enhancement diagnostics updated: support_hint='{diagnostics.SupportHintText}', " +
            $"requested='{diagnostics.RequestedStateText}', resolved='{diagnostics.ResolvedStateText}', " +
            $"backend='{diagnostics.BackendSummaryText}'");
    }

    private void EnsureSourcePlayerDiagnosticsSubscribed(IMediaTransport player)
    {
        if (_subscribedToSourceDiagnostics || player is not LibMpvEmbeddedTransport embedded)
            return;

        embedded.VsrDiagnosticChanged += _vsrDiagnosticChangedHandler;
        _subscribedToSourceDiagnostics = true;

        if (embedded.LastVsrDiagnostic is not null)
            RecordVsrDiagnosticSnapshot(embedded.LastVsrDiagnostic);
    }

    internal void RecordVsrDiagnosticSnapshot(VsrDiagnosticSnapshot snapshot)
    {
        _latestVsrDiagnostic = snapshot;
        RefreshVideoEnhancementDiagnostics();
    }

    partial void OnHardwareSnapshotChanged(HardwareSnapshot value) => RefreshVideoEnhancementDiagnostics();
}
