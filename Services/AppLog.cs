using System;
using System.IO;

namespace Babel.Player.Services;

public sealed class AppLog
{
    private readonly object _gate = new();

    public AppLog(string logFilePath)
    {
        LogFilePath = logFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
    }

    public string LogFilePath { get; }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    public void Error(string message, Exception exception)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}";

        lock (_gate)
        {
            File.AppendAllText(LogFilePath, line);
        }
    }
}
