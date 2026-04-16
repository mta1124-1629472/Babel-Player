using System;
using System.Collections.Generic;
using System.IO;

namespace Babel.Player.Services;

/// <summary>
/// Registers temp files and disposes them together — use in pipeline stages that write scratch artifacts.
/// </summary>
public sealed class PipelineResourceScope : IDisposable
{
    private readonly List<string> _tempPaths = [];
    private bool _disposed;

    public void RegisterTempFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _tempPaths.Add(path);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var path in _tempPaths)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup — mirrors subprocess temp script deletion patterns.
            }
        }
    }
}
