using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Babel.Player.Services;

namespace BabelPlayer.Tests;

internal static class ProbeTestHelpers
{
    /// <summary>
    /// Forces the cached probe result for the given service URL to appear expired
    /// so that the next call triggers a fresh background probe.
    /// </summary>
    public static void ExpireCachedProbeResult(ContainerizedServiceProbe probe, string serviceUrl)
    {
        var normalizedUrl = ContainerizedInferenceClient.NormalizeBaseUrl(serviceUrl);

        var entriesField = typeof(ContainerizedServiceProbe).GetField(
            "_entries",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMemberException(nameof(ContainerizedServiceProbe), "_entries");

        var entries = (System.Collections.IDictionary)entriesField.GetValue(probe)!;
        var entry = entries[normalizedUrl];
        if (entry is null)
            return;

        var entryType = entry.GetType();
        var gateProp = entryType.GetProperty(
            "Gate",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("ProbeEntry", "Gate");

        var expiresAtUtcProp = entryType.GetProperty(
            "ExpiresAtUtc",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException("ProbeEntry", "ExpiresAtUtc");

        var gate = gateProp.GetValue(entry) as object
            ?? throw new InvalidOperationException("ProbeEntry.Gate returned null.");

        lock (gate)
        {
            expiresAtUtcProp.SetValue(entry, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Polls <paramref name="getCallCount"/> until its return value is at least
    /// <paramref name="expectedMinimum"/>, or throws <see cref="TimeoutException"/>
    /// when <paramref name="timeout"/> elapses.
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
            $"Timed out waiting for probe call count >= {expectedMinimum}. " +
            $"Actual: {getCallCount()}");
    }
}
