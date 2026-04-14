using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Babel.Player.Services;

/// <summary>
/// Provides secure methods to open folders and URLs without using shell execution,
/// mitigating potential arbitrary code execution vulnerabilities.
/// </summary>
public static class FileOpener
{
    /// <summary>
    /// Opens the specified folder in the system's default file manager.
    /// </summary>
    /// <param name="path">The directory path to open.</param>
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Ensure we are working with a directory
        if (!Directory.Exists(path)) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, start explorer.exe directly with the path as an argument.
            // Using UseShellExecute = false avoids the shell interpretation of the path.
            // ArgumentList ensures safe quoting of the path.
            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
    }

    /// <summary>
    /// Opens the specified URL in the system's default web browser.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // Validate that it's a web URL
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, using UseShellExecute = true with a validated http/https URL
            // is safe and handles all browser registration logic correctly.
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xdg-open",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(url);
            Process.Start(psi);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false
            };
            psi.ArgumentList.Add(url);
            Process.Start(psi);
        }
    }
}
