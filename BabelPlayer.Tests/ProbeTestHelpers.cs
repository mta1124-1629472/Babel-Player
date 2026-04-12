using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

internal static class ProbeTestHelpers
{
    /// <summary>
    /// Back-dates the cached probe entry's <c>ExpiresAtUtc</c> by one minute so that the
    /// next call to <see cref="ContainerizedServiceProbe.GetCurrentOrStartBackgroundProbe"/>
    /// treats the result as stale and triggers a background refresh.
    /// </summary>
    public static void ExpireCachedProbeResult(ContainerizedServiceProbe probe, string serviceUrl)
    {
        var field = typeof(ContainerizedServiceProbe)
            .GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not locate _entries field on ContainerizedServiceProbe.");

        var entriesObj = field.GetValue(probe)
            ?? throw new InvalidOperationException("_entries field was null.");
        var entries = (IDictionary)entriesObj;

        // Normalize the URL the same way the probe does (OrdinalIgnoreCase comparer means
        // the key lookup is case-insensitive, so we just need to find the matching key).
        object? entry = null;
        foreach (DictionaryEntry kv in entries)
        {
            if (string.Equals(kv.Key as string, serviceUrl, StringComparison.OrdinalIgnoreCase))
            {
                entry = kv.Value;
                break;
            }
        }

        if (entry is null)
            return; // No cached entry yet — nothing to expire.

        var expiresAtProp = entry.GetType()
            .GetProperty("ExpiresAtUtc", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not locate ExpiresAtUtc property on ProbeEntry.");

        expiresAtProp.SetValue(entry, DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    /// <summary>
    /// Polls <paramref name="getCallCount"/> every 25 ms until the value reaches
    /// <paramref name="expectedMinimum"/> or the optional <paramref name="timeout"/> elapses.
    /// </summary>
    public static async Task WaitForCallCountAsync(
        Func<int> getCallCount,
        int expectedMinimum,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (getCallCount() >= expectedMinimum)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Expected call count >= {expectedMinimum} but reached {getCallCount()} after timeout elapsed.");
    }
}
