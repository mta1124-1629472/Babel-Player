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

                Assert.True(
                    IsEnclosedInSessionLock(lines, index),
                    $"'CurrentSession =' assignment at {Path.GetFileName(file)}:{index + 1} is not wrapped in 'lock (_sessionLock)'.");
            }
        }
    }

    // Walks backwards from the assignment line, tracking curly-brace balance, until it finds
    // the opening brace of the enclosing block. That block must be headed by `lock (_sessionLock)`
    // (either on the same line or on the immediately preceding line for the canonical
    // `lock (_sessionLock)` / `{` formatting).
    private static bool IsEnclosedInSessionLock(string[] lines, int assignmentIndex)
    {
        var depth = 0;
        for (var back = assignmentIndex - 1; back >= 0; back--)
        {
            var text = lines[back];
            for (var ci = text.Length - 1; ci >= 0; ci--)
            {
                var ch = text[ci];
                if (ch == '}')
                {
                    depth++;
                }
                else if (ch == '{')
                {
                    if (depth == 0)
                    {
                        if (text.Contains("lock (_sessionLock)", StringComparison.Ordinal))
                            return true;
                        if (back > 0 && lines[back - 1].Contains("lock (_sessionLock)", StringComparison.Ordinal))
                            return true;
                        continue;
                    }
                    depth--;
                }
            }
        }
        return false;
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
