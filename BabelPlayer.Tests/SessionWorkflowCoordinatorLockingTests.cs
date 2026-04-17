using System;
using System.IO;
using System.Linq;

namespace BabelPlayer.Tests;

public sealed class SessionWorkflowCoordinatorLockingTests
{
    [Fact]
    public void SessionWorkflowCoordinator_CurrentSessionAssignments_AreWrappedInSessionLock()
    {
        var servicesDir = FindRepoDirectory("Services");
        var files = Directory.GetFiles(servicesDir, "SessionWorkflowCoordinator*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (!line.StartsWith("CurrentSession =", StringComparison.Ordinal))
                    continue;

                var windowStart = Math.Max(0, index - 3);
                var window = string.Join(Environment.NewLine, lines.Skip(windowStart).Take(index - windowStart + 1));
                Assert.Contains("lock (_sessionLock)", window, StringComparison.Ordinal);
            }
        }
    }

    private static string FindRepoDirectory(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, name);
            if (Directory.Exists(candidate))
                return candidate;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate '{name}' from '{AppContext.BaseDirectory}'.");
    }
}
