using System.Text.RegularExpressions;
using Gauge.Localization;

namespace Gauge.Tests;

/// <summary>
/// Guards against referencing a localization key that doesn't exist in the table — a typo
/// would otherwise surface only as the raw key showing in the UI. Scans the app's source
/// for <c>Loc.Get/Format("…")</c> and XAML <c>{loc:Localize Key=…}</c> and checks each key.
/// </summary>
public sealed class LocalizationKeyUsageTests
{
    [Fact]
    public void EveryKeyReferencedInCSharpExistsInTable()
    {
        var unknown = new List<string>();
        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"Loc\.(?:Get|Format)\(\s*""([^""]+)"""))
            {
                var key = match.Groups[1].Value;
                if (!Strings.Table.ContainsKey(key)) unknown.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(unknown.Count == 0, "Unknown localization keys in C#: " + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryKeyReferencedInXamlExistsInTable()
    {
        var unknown = new List<string>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"Localize\s+Key=([A-Za-z0-9_]+)"))
            {
                var key = match.Groups[1].Value;
                if (!Strings.Table.ContainsKey(key)) unknown.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(unknown.Count == 0, "Unknown localization keys in XAML: " + string.Join(", ", unknown));
    }

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        var root = RepoRoot();
        var excluded = new HashSet<string>(["obj", "bin", "Gauge.Tests", "Ref"], StringComparer.OrdinalIgnoreCase);
        // SDK globs exclude dot directories, which can contain other Git worktrees.
        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).SkipLast(1)
                .Any(directory => directory.StartsWith('.') || excluded.Contains(directory)));
    }

    // Walk up from the test binary until the directory containing Gauge.csproj — robust to
    // where the assembly was compiled vs. where it runs.
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
