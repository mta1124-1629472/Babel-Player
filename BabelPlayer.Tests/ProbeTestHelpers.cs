using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

/// <summary>
/// Shared helpers for tests that exercise <see cref="ContainerizedServiceProbe"/> caching and polling behavior.
/// </summary>
internal static class ProbeTestHelpers
{
    /// <summary>
    /// Forces the cached probe entry for <paramref name="serviceUrl"/> to look expired
    /// so the next call triggers a fresh background probe.
    /// </summary>
    internal static void ExpireCachedProbeResult(ContainerizedServiceProbe probe, string serviceUrl)
    {
        var entriesField = typeof(ContainerizedServiceProbe).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _entries field.");

        var entries = entriesField.GetValue(probe)
            ?? throw new InvalidOperationException("ContainerizedServiceProbe entries cache was null.");

        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);
        var tryGetValue = entries.GetType().GetMethod("TryGetValue")
            ?? throw new InvalidOperationException("Could not find TryGetValue on probe cache.");

        var tryGetArgs = new object?[] { normalizedUrl, null };
        var found = (bool)(tryGetValue.Invoke(entries, tryGetArgs) ?? false);
        if (!found)
            throw new InvalidOperationException($"No cached probe entry found for {normalizedUrl}.");

        var entry = tryGetArgs[1] ?? throw new InvalidOperationException("Probe cache entry was null.");
        var expiresProperty = entry.GetType().GetProperty("ExpiresAtUtc")
            ?? throw new InvalidOperationException("Could not find ExpiresAtUtc on probe cache entry.");
        expiresProperty.SetValue(entry, DateTimeOffset.UtcNow.AddSeconds(-1));
    }

    /// <summary>
    /// Waits until <paramref name="getCount"/> returns at least <paramref name="expectedMinimum"/>,
    /// or throws after <paramref name="timeoutMs"/> milliseconds.
    /// </summary>
    internal static async Task WaitForCallCountAsync(Func<int> getCount, int expectedMinimum, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (getCount() >= expectedMinimum)
                return;

            await Task.Delay(10);
        }

        var observed = getCount();
        throw new TimeoutException(
            $"Timed out waiting for call count. Expected at least {expectedMinimum}, observed {observed} after {timeoutMs}ms.");
    }
}
