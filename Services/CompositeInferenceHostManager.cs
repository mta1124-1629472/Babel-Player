using System;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Models;
using Babel.Player.Services.Settings;

namespace Babel.Player.Services;

public sealed class CompositeInferenceHostManager : IContainerizedInferenceManager, IDisposable
{
    private readonly ManagedVenvHostManager _managedHostManager;
    private readonly IContainerizedInferenceManager _dockerHostManager;
    private readonly AppLog _log;

    public CompositeInferenceHostManager(
        ManagedVenvHostManager managedHostManager,
        IContainerizedInferenceManager dockerHostManager,
        AppLog log)
    {
        _managedHostManager = managedHostManager;
        _dockerHostManager = dockerHostManager;
        _log = log;
    }

    public void RequestEnsureStarted(AppSettings settings, ContainerizedStartupTrigger trigger)
    {
        SelectManager(settings).RequestEnsureStarted(settings, trigger);
    }

    public Task<ContainerizedStartResult> EnsureStartedAsync(
        AppSettings settings,
        ContainerizedStartupTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        return SelectManager(settings).EnsureStartedAsync(settings, trigger, cancellationToken);
    }

    public ContainerizedProbeResult GetCurrentStatus(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return SelectManager(settings).GetCurrentStatus(settings);
    }

    public void Dispose()
    {
        try
        {
            _managedHostManager.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to dispose managed host manager.", ex);
        }

        if (_dockerHostManager is IDisposable disposableDockerManager)
        {
            try
            {
                disposableDockerManager.Dispose();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to dispose docker host manager.", ex);
            }
        }
    }

    private IContainerizedInferenceManager SelectManager(AppSettings settings) =>
        settings.PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv
            ? _managedHostManager
            : _dockerHostManager;
}
