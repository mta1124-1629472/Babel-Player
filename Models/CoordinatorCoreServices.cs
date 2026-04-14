using Babel.Player.Services;
using Babel.Player.Services.Settings;

namespace Babel.Player.Models;

/// <summary>
/// Bundles the required core services that every <see cref="SessionWorkflowCoordinator"/> instance
/// needs regardless of host environment.
/// </summary>
public sealed record CoordinatorCoreServices(
    SessionSnapshotStore Store,
    AppLog Log,
    AppSettings Settings);
