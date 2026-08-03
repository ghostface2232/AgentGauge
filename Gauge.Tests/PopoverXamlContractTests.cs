using System.Text.RegularExpressions;

namespace Gauge.Tests;

/// <summary>Source-level contracts for bindings duplicated across the bar and gauge templates.</summary>
public sealed class PopoverXamlContractTests
{
    [Fact]
    public void BothUsageViewModesRenderEta()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "PopoverWindow.xaml"));

        Assert.Equal(2, Regex.Matches(xaml, "Text=\"\\{Binding EtaText\\}\"").Count);
        Assert.Equal(2, Regex.Matches(xaml, "Visibility=\"\\{Binding HasEta,").Count);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gauge.csproj")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repo root (Gauge.csproj).");
    }
}
