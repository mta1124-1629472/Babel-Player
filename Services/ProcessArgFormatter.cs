using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Babel.Player.Services;

/// <summary>
/// Formats a process argument list as a single display string for use in log and error messages.
/// Arguments that contain whitespace, quotes, or empty values are wrapped in double-quotes and any
/// embedded double-quote characters are escaped for readability. The actual process launch uses
/// <c>ProcessStartInfo.ArgumentList</c>, which handles escaping independently.
/// </summary>
internal static class ProcessArgFormatter
{
    internal static string FormatArgs(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(FormatArg));

    private static string FormatArg(string argument)
    {
        if (argument.Length == 0)
            return "\"\"";

        var escaped = EscapeForDisplay(argument);
        var needsQuoting =
            argument.Any(char.IsWhiteSpace)
            || argument.Contains('"')
            || argument.Any(char.IsControl);
        if (!needsQuoting)
            return escaped;

        return $"\"{escaped}\"";
    }

    private static string EscapeForDisplay(string argument)
    {
        var builder = new StringBuilder(argument.Length);
        foreach (var ch in argument)
        {
            switch (ch)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                default:
                    if (char.IsControl(ch))
                        builder.Append(@"\u").Append(((int)ch).ToString("X4"));
                    else
                        builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }
}
