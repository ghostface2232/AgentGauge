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

    [Fact]
    public void NotificationKindTogglesRemainConfigurableWhileGloballyPaused()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot(), "Views", "PopoverWindow.xaml"));
        var xaml = document.Root!.Name.Namespace;

        foreach (var setting in new[] { "NotifyThresholds", "NotifyResets" })
        {
            var toggle = document.Descendants(xaml + "ToggleSwitch").Single(element =>
                (string?)element.Attribute("IsOn") == $"{{Binding Global.{setting}, Mode=TwoWay}}");
            Assert.Null(toggle.Attribute("IsEnabled"));
            Assert.Null(toggle.Parent!.Attribute("IsHitTestVisible"));
            Assert.Null(toggle.Parent!.Attribute("Opacity"));
        }
    }

    [Fact]
    public void GlobalSettingsRowsUseSingleLabelsAndConsistentHeight()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot(), "Views", "PopoverWindow.xaml"));
        var xaml = document.Root!.Name.Namespace;
        var bindings = new[]
        {
            "{Binding Global.NotifyThresholds, Mode=TwoWay}",
            "{Binding Global.NotifyResets, Mode=TwoWay}",
            "{Binding Global.StartOnBoot, Mode=TwoWay}",
            "{Binding Global.ViewModeIndex, Mode=TwoWay}",
            "{Binding Global.LanguageIndex, Mode=TwoWay}",
        };

        XElement? firstRow = null;
        foreach (var binding in bindings)
        {
            var control = document.Descendants().Single(element =>
                element.Attributes().Any(attribute => attribute.Value == binding));
            var row = control.Parent!;
            firstRow ??= row;

            Assert.Equal("48", (string?)row.Attribute("MinHeight"));
            Assert.Single(row.Elements(xaml + "TextBlock"));
            Assert.DoesNotContain(row.Descendants(xaml + "TextBlock"), text =>
                (string?)text.Attribute("Style") == "{StaticResource CaptionTextBlockStyle}");
        }

        var card = firstRow!.Ancestors(xaml + "Border").First();
        Assert.Equal("14,4", (string?)card.Attribute("Padding"));
    }

    [Fact]
    public void NotificationKindsUseAnInlineDisclosureRow()
    {
        var document = XDocument.Load(Path.Combine(RepoRoot(), "Views", "PopoverWindow.xaml"));
        var xaml = document.Root!.Name.Namespace;
        var disclosureButton = document.Descendants(xaml + "Button").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                == "NotificationOptionsButton");
        var nestedRows = document.Descendants(xaml + "StackPanel").Single(element =>
            (string?)element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                == "NotificationKindRows");

        Assert.DoesNotContain(document.Descendants(xaml + "Expander"), _ => true);
        Assert.Equal("40", (string?)disclosureButton.Attribute("Width"));
        Assert.Equal("40", (string?)disclosureButton.Attribute("Height"));
        Assert.Equal("Collapsed", (string?)nestedRows.Attribute("Visibility"));
        Assert.Equal("0", (string?)nestedRows.Attribute("Height"));
        Assert.Equal("0", (string?)nestedRows.Attribute("Opacity"));
        var rowsTransform = nestedRows
            .Element(xaml + "StackPanel.RenderTransform")!
            .Element(xaml + "TranslateTransform")!;
        Assert.Equal("-6", (string?)rowsTransform.Attribute("Y"));
        var glyphRotation = disclosureButton
            .Descendants(xaml + "RotateTransform")
            .Single();
        Assert.Equal(
            "NotificationOptionsGlyphRotation",
            (string?)glyphRotation.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")));
        var masterRow = disclosureButton.Parent!;
        Assert.Equal("48", (string?)masterRow.Attribute("MinHeight"));
        Assert.Single(masterRow.Elements(xaml + "TextBlock"));
        Assert.DoesNotContain(masterRow.Elements(xaml + "ToggleSwitch"), _ => true);
        foreach (var setting in new[] { "NotifyThresholds", "NotifyResets" })
        {
            Assert.Contains(nestedRows.Descendants(xaml + "ToggleSwitch"), element =>
                (string?)element.Attribute("IsOn") == $"{{Binding Global.{setting}, Mode=TwoWay}}");
        }
    }

    [Fact]
    public void NotificationDisclosureMotionIsResponsiveAndAccessible()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "PopoverWindow.xaml.cs"));

        Assert.Contains("NotificationExpandDurationMs = 180", source, StringComparison.Ordinal);
        Assert.Contains("NotificationCollapseDurationMs = 140", source, StringComparison.Ordinal);
        Assert.Contains("_uiSettings.AnimationsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_notificationOptionsStoryboard, storyboard)", source, StringComparison.Ordinal);
        Assert.Contains("ControlPoint1 = new Point(0.23, 1)", source, StringComparison.Ordinal);
        Assert.Contains("ControlPoint2 = new Point(0.32, 1)", source, StringComparison.Ordinal);
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
