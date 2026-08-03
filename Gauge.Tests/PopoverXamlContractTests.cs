using System.Text.RegularExpressions;
using System.Xml.Linq;

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

    [Fact]
    public void BarCaptionsStackAndWrapWithinTheCard()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot(), "Views", "PopoverWindow.xaml"));
        var xaml = document.Root!.Name.Namespace;
        var captionBindings = new[] { "ResetText", "CountsText", "PaceText", "EtaText" };

        var captions = document.Descendants(xaml + "StackPanel").Single(panel =>
            panel.Elements(xaml + "TextBlock").Any(text =>
                (string?)text.Attribute("Text") == "{Binding CountsText}"));

        Assert.NotEqual("Horizontal", (string?)captions.Attribute("Orientation"));
        foreach (var binding in captionBindings)
        {
            var text = captions.Elements(xaml + "TextBlock").Single(element =>
                (string?)element.Attribute("Text") == $"{{Binding {binding}}}");
            Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
        }
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
