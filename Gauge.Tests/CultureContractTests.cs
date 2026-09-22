using System.Text.RegularExpressions;

namespace Gauge.Tests;

/// <summary>
/// Tripwire for the invariant-culture rule in AGENTS.md: <c>Loc.Initialize</c> sets the
/// process culture to the UI language, so a machine-facing parse that relies on the ambient
/// culture silently misreads under Korean or Japanese. The scan is lexical and deliberately
/// narrow — it catches the honest mistake of a bare <c>double.Parse(text)</c>, not every way
/// a culture can leak (formatting, <c>Convert.To*</c>, a provider passed through a variable).
/// </summary>
public sealed class CultureContractTests
{
    // The numeric and temporal types whose Parse/TryParse read the ambient culture.
    // Version, Guid and Enum parse culture-free and are left out on purpose.
    private static readonly Regex ParseCall = new(
        @"\b(?:double|float|decimal|int|long|short|byte|sbyte|uint|ulong|ushort"
        + @"|Double|Single|Decimal|Int32|Int64|Int16|Byte|SByte|UInt32|UInt64|UInt16"
        + @"|DateTime|DateTimeOffset|TimeSpan|DateOnly|TimeOnly|Half|BigInteger)"
        + @"\.(?:Try)?Parse(?:Exact)?\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void MachineFacingParsesPassInvariantCulture()
    {
        var root = RepoRoot();
        var failures = new List<string>();
        var files = SourceFiles(root).ToList();
        // A tripwire that scanned nothing passes vacuously; the app has well over this many
        // source files, so a filter or root mistake shows up here instead of as a green run.
        Assert.True(files.Count > 50, $"Scanned only {files.Count} source files under {root}.");
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in ParseCall.Matches(source))
            {
                var arguments = ArgumentList(source, match.Index + match.Length);
                if (arguments.Contains("InvariantCulture", StringComparison.Ordinal)) continue;
                var line = source.AsSpan(0, match.Index).Count('\n') + 1;
                failures.Add($"{Path.GetRelativePath(root, file)}:{line}: {match.Value.TrimEnd('(', ' ')}");
            }
        }
        Assert.True(failures.Count == 0,
            "Machine-facing parses must pass CultureInfo.InvariantCulture:" + Environment.NewLine
            + string.Join(Environment.NewLine, failures));
    }

    // The text between a call's opening parenthesis and its matching close. Nested calls are
    // kept; string and char literals are skipped so a parenthesis inside one cannot end the
    // scan early. Raw string literals are not handled — none sit inside a parse call.
    private static string ArgumentList(string source, int start)
    {
        var depth = 1;
        var i = start;
        while (i < source.Length)
        {
            var c = source[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(source, i);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return source[start..i];
            i++;
        }
        return source[start..];
    }

    // Index just past a "…", @"…" or '…' literal that starts at quote.
    private static int SkipLiteral(string source, int quote)
    {
        var verbatim = quote > 0 && source[quote - 1] == '@';
        var q = source[quote];
        var i = quote + 1;
        while (i < source.Length)
        {
            if (source[i] == q)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == q)
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            if (source[i] == '\\' && !verbatim) i++;
            i++;
        }
        return source.Length;
    }

    // Excluded by the path's top-level folder relative to the repo root, not by a substring
    // of the absolute path — a clone living under some ...\bin\... folder would otherwise
    // exclude every file. Build output can also nest (bin/obj under a project folder), so
    // those two are excluded at any depth within the repo.
    private static readonly HashSet<string> ExcludedTopLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gauge.Tests", "Ref", "dist", ".claude", ".git",
    };

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p =>
            {
                var parts = Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !ExcludedTopLevel.Contains(parts[0])
                    && !parts.Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                       || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
            });

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Gauge.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Gauge.csproj).");
    }
}
