using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Rendering.Composition;

namespace Babel.Player.Services.Rendering;

/// <summary>
/// Optional entry points for Avalonia's <see cref="Compositor"/>. Nothing here runs on a timer or at startup;
/// the framework still uses its compositor internally for normal UI rendering.
/// </summary>
/// <remarks>
/// <para>
/// Video is rendered by libmpv into a Win32 child <c>HWND</c> via <see cref="Views.MpvVideoView"/> (<see cref="Avalonia.Controls.NativeControlHost"/>).
/// That path is outside Avalonia's composition tree; the compositor does not replace mpv's swap chain unless we redesign around
/// GPU textures / <see cref="Compositor.TryGetCompositionGpuInterop"/> and a custom presentation path.
/// </para>
/// <para>
/// Call <see cref="TryGetDefaultCompositor"/> or composition APIs from here when we add custom composition visuals, GPU interop,
/// or composition-thread work. Until then this type stays idle.
/// </para>
/// </remarks>
public static class AvaloniaComposition
{
    /// <summary>Returns <see cref="Compositor.TryGetDefaultCompositor"/> — may be null on some backends or configurations.</summary>
    public static Compositor? TryGetDefaultCompositor() => Compositor.TryGetDefaultCompositor();

    /// <summary>
    /// Optional manual diagnostic (e.g. from dev tools): logs default compositor and GPU interop availability. Not invoked automatically.
    /// </summary>
    public static async Task ProbeDevCapabilitiesAsync()
    {
        try
        {
            var compositor = TryGetDefaultCompositor();
            if (compositor is null)
            {
                Debug.WriteLine("[Babel] Compositor: no default instance (backend may use per-surface compositors).");
                return;
            }

            var gpu = await compositor.TryGetCompositionGpuInterop().ConfigureAwait(true);
            Debug.WriteLine(gpu is not null
                ? "[Babel] Compositor: default OK; GPU interop available (future texture/shared-surface work)."
                : "[Babel] Compositor: default OK; GPU interop not available on this backend.");
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[Babel] Compositor probe failed: {ex.Message}");
        }
    }
}
