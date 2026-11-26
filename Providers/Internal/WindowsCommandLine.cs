using System.Text;

namespace Gauge.Providers.Internal;

/// <summary>
/// Builds a single command-line string from an executable path and arguments using the Windows
/// quoting rules that <c>CommandLineToArgvW</c> reverses, for passing to <c>CreateProcessW</c>.
/// Doing this ourselves (rather than via <c>Process.ArgumentList</c>) is required because
/// delegate-mode launch goes through CREATE_SUSPENDED, which the managed Process API does not
/// expose. The engine splits the line back with <c>CommandLineToArgvW</c>, so the two must
/// round-trip.
/// </summary>
internal static class WindowsCommandLine
{
    public static string Join(string executablePath, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(executablePath));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string argument)
    {
        // An argument with no separator or quote needs no quoting (an empty one still needs "").
        if (argument.Length > 0 && !argument.AsSpan().ContainsAny(" \t\n\v\""))
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Trailing backslashes precede the closing quote: double them so they stay literal.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                // Backslashes before a quote are doubled, then the quote itself is escaped.
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
