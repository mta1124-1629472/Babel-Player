using System;

namespace Babel.Player.Services;

public enum ReadinessSignalKind
{
    BootstrapApplied,
    RuntimeWarmupStatusChanged,
    SettingsChanged,
    DiagnosticsRefreshRequested,
    ProbeResultUpdated,
}

public sealed record ReadinessSignal(
    ReadinessSignalKind Kind,
    DateTimeOffset TimestampUtc,
    string Summary,
    string? Source = null,
    bool ForceRefresh = false);
