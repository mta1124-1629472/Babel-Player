using System.Collections.Generic;
using System.Linq;

namespace Babel.Player.Services;

/// <summary>
/// Formats a process argument list as a single display string for use in log and error messages.
/// Arguments that contain spaces are wrapped in double-quotes. The actual process launch uses
/// <c>ProcessStartInfo.ArgumentList</c>, which handles escaping independently.
/// </summary>
internal static class ProcessArgFormatter
{
    internal static string FormatArgs(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
}
