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

    public CompositeInferenceHostManager(
        ManagedVenvHostManager managedHostManager,
        IContainerizedInferenceManager dockerHostManager)
    {
        _managedHostManager = managedHostManager;
        _dockerHostManager = dockerHostManager;
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
        catch
        {
        }

        if (_dockerHostManager is IDisposable disposableDockerManager)
        {
            try
            {
                disposableDockerManager.Dispose();
            }
            catch
            {
            }
        }
    }

    private IContainerizedInferenceManager SelectManager(AppSettings settings) =>
        settings.PreferredLocalGpuBackend == GpuHostBackend.ManagedVenv
            ? _managedHostManager
            : _dockerHostManager;
}
