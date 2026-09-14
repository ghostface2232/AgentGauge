using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Gauge.Tests;

/// <summary>Source-level contracts for bindings duplicated across the bar and gauge templates.</summary>
public sealed class PopoverXamlContractTests
{
    [Fact]
    public void XamlColorsOnlyAppearInExplicitThemePaletteDefinitions()
    {
        // These palette definitions are the source of the theme brushes, not use sites.
        // Exact path/theme/key/value entries forbid expanding an entire file's exemption.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "App.xaml|Default|UsageOkBrush|#2E9E4F",
            "App.xaml|Default|UsageCautionBrush|#FEAD0C",
            "App.xaml|Default|UsageDangerBrush|#FE140C",
            "App.xaml|Default|UsageOkTextBrush|#6CCB5F",
            "App.xaml|Default|UsageCautionTextBrush|#FEAD0C",
            "App.xaml|Default|UsageDangerTextBrush|#FF99A4",
            "App.xaml|Default|ResetChipFillBrush|#3D2F6FED",
            "App.xaml|Default|ResetChipTextBrush|#A8CBFF",
            "App.xaml|Light|UsageOkBrush|#2E9E4F",
            "App.xaml|Light|UsageCautionBrush|#FEAD0C",
            "App.xaml|Light|UsageDangerBrush|#FE140C",
            "App.xaml|Light|UsageOkTextBrush|#0F7B0F",
            "App.xaml|Light|UsageCautionTextBrush|#9D5D00",
            "App.xaml|Light|UsageDangerTextBrush|#C42B1C",
            "App.xaml|Light|ResetChipFillBrush|#242F6FED",
            "App.xaml|Light|ResetChipTextBrush|#0B4A94",
            "Views/PopoverWindow.xaml|Light|IconButtonHoverBrush|#0D000000",
            "Views/PopoverWindow.xaml|Light|IconButtonPressedBrush|#1A000000",
            "Views/PopoverWindow.xaml|Dark|IconButtonHoverBrush|#1FFFFFFF",
            "Views/PopoverWindow.xaml|Dark|IconButtonPressedBrush|#33FFFFFF",
        };
        Assert.Equal(20, allowed.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        var root = RepoRoot();
        foreach (var path in XamlFiles(root))
        {
            var document = XDocument.Load(path);
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var value = attribute.Value;
                    var isHex = Regex.IsMatch(value, @"(?i)#[0-9a-f]{3,8}\b");
                    // A Setter carries the target property in Property and the literal in Value.
                    var property = element.Name.LocalName == "Setter" && attribute.Name.LocalName == "Value"
                        ? ((string?)element.Attribute("Property") ?? "").Split('.')[^1]
                        : attribute.Name.LocalName;
                    var isNamed = IsColorProperty(property) && System.Drawing.Color.FromName(value).IsKnownColor;
                    if (!isHex && !isNamed) continue;
                    var key = $"{relative}|{(string?)element.Parent?.Attribute(x + "Key")}|{(string?)element.Attribute(x + "Key")}|{value}";
                    var palette = element.Name.LocalName == "SolidColorBrush" && attribute.Name.LocalName == "Color"
                        && element.Parent?.Parent?.Name.LocalName == "ResourceDictionary.ThemeDictionaries";
                    if (!palette || !allowed.Contains(key) || !seen.Add(key)) failures.Add($"{relative}: {attribute}");
                }
                foreach (var node in element.Nodes().OfType<XText>())
                    if (Regex.IsMatch(node.Value, @"(?i)#[0-9a-f]{3,8}\b")
                        || element.Name.LocalName == "Color" && System.Drawing.Color.FromName(node.Value.Trim()).IsKnownColor)
                        failures.Add($"{relative}: {node.Value.Trim()}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(allowed.SetEquals(seen), "Remove stale palette exemptions or explicitly review changed colors.");
    }

    private static bool IsColorProperty(string name) =>
        name is "Fill" or "Stroke"
        || name.EndsWith("Color", StringComparison.Ordinal) || name.EndsWith("Brush", StringComparison.Ordinal)
        || name.EndsWith("Foreground", StringComparison.Ordinal) || name.EndsWith("Background", StringComparison.Ordinal);

    private static IEnumerable<string> XamlFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.xaml")) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            // Mirrors the untracked directories in .gitignore; Ref/ holds other projects' XAML.
            if (Path.GetFileName(child).ToLowerInvariant() is "bin" or "obj" or "dist" or "build" or "publish" or "packages"
                or "ref" or ".vs" or ".idea" or ".git" or ".codex" or ".agents") continue;
            foreach (var file in XamlFiles(child)) yield return file;
        }
    }

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
        var captionBindings = new[] { "CaptionText", "CountsText", "PaceText", "EtaText" };

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

    // The kind switches are the whole notification setting, so nothing may gate them: no
    // IsEnabled, and no dimmed/inert treatment on their row.
    [Fact]
    public void NotificationKindTogglesAreNeverGatedByAnotherSetting()
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
