using Gauge.Localization;
using Gauge.Models;

namespace Gauge.Tests;

/// <summary>
/// Exercises the reset-time and notification formatters in each language and asserts every
/// placeholder is substituted (no stray "{0}"), the date format strings are valid, and the
/// window label is localized rather than left as a raw key.
///
/// These mutate the process-wide <see cref="Loc"/>/culture state, so <see cref="Dispose"/>
/// restores the Korean default that the rest of the suite assumes. Assembly-wide
/// parallelization is disabled (see AssemblyInfo.cs) so this never races those tests.
/// </summary>
public sealed class LocalizationFormatTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => Loc.Initialize(AppLanguage.Korean);

    [Theory]
    [InlineData(AppLanguage.Korean)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Japanese)]
    public void ResetFormatterSubstitutesEveryPlaceholder(AppLanguage language)
    {
        Loc.Initialize(language);

        // ForRow measures against the real clock, so use real-now-relative offsets to land
        // in each branch; ForNotification takes an explicit "now" and is fully deterministic.
        var outputs = new[]
        {
            ResetTimeFormatter.ForRow(DateTimeOffset.Now.AddDays(4)),            // days
            ResetTimeFormatter.ForRow(DateTimeOffset.Now.AddHours(3).AddMinutes(20)), // hours+minutes
            ResetTimeFormatter.ForRow(DateTimeOffset.Now.AddMinutes(15)),        // minutes
            ResetTimeFormatter.ForRow(DateTimeOffset.Now.AddSeconds(-5)),        // already reset
            ResetTimeFormatter.ForRow(null),                                     // unknown → empty
            ResetTimeFormatter.ForNotification(Now.AddDays(1), Now),             // 1 day
            ResetTimeFormatter.ForNotification(Now.AddHours(2).AddMinutes(40), Now),
            ResetTimeFormatter.ForNotification(Now.AddHours(4), Now),
            ResetTimeFormatter.ForNotification(Now.AddMinutes(30), Now),
            ResetTimeFormatter.ForNotification(Now.AddSeconds(-5), Now),         // soon
            ResetTimeFormatter.ForNotification(null, Now),                       // unknown
        };

        foreach (var output in outputs)
        {
            Assert.DoesNotContain("{", output);
        }
    }

    [Theory]
    [InlineData(AppLanguage.Korean)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Japanese)]
    public void NotificationTextSubstitutesEveryPlaceholder(AppLanguage language)
    {
        Loc.Initialize(language);

        var title = NotificationText.ThresholdTitle("Codex", Window(UsageWindowType.Weekly), 90);
        var scopedTitle = NotificationText.ThresholdTitle(
            "Claude Code", Window(UsageWindowType.Weekly, group: "Fable"), 70);
        var resetTitle = NotificationText.ResetTitle("Claude Code", Window(UsageWindowType.FiveHour));
        var scopedResetTitle = NotificationText.ResetTitle(
            "Claude Code", Window(UsageWindowType.Weekly, group: "Fable"));
        var resetMessage = NotificationText.ResetMessage(100);

        Assert.DoesNotContain("{", title);
        Assert.Contains("90", title);
        Assert.Contains("Codex", title);
        Assert.DoesNotContain("Label_", title); // label resolved, not a raw key
        Assert.DoesNotContain("{", scopedTitle);
        Assert.Contains("Fable", scopedTitle);
        Assert.DoesNotContain("{", resetTitle);
        Assert.DoesNotContain("{", scopedResetTitle);
        Assert.Contains("Fable", scopedResetTitle);
        Assert.DoesNotContain("{", resetMessage);
        Assert.Contains("100", resetMessage);
    }

    [Fact]
    public void EnglishUsesSingularDayPhrase()
    {
        Loc.Initialize(AppLanguage.English);

        var oneDay = ResetTimeFormatter.ForNotification(Now.AddDays(1), Now);

        Assert.Contains("1 day (", oneDay);
        Assert.DoesNotContain("1 days", oneDay);
    }

    [Theory]
    [InlineData(AppLanguage.Korean)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Japanese)]
    public void SameTypeWindowsWithDistinctLabelsProduceDistinctTitles(AppLanguage language)
    {
        Loc.Initialize(language);

        // GitHub Copilot's quotas are all BillingCycle windows: deriving the title's label
        // from the type alone made a premium-request alert read identically to a chat one.
        var chat = Window(UsageWindowType.BillingCycle, labelKey: "Label_Copilot_Chat");
        var premium = Window(UsageWindowType.BillingCycle, labelKey: "Label_Copilot_Premium");

        Assert.NotEqual(
            NotificationText.ThresholdTitle("GitHub Copilot", chat, 90),
            NotificationText.ThresholdTitle("GitHub Copilot", premium, 90));
        Assert.NotEqual(
            NotificationText.ResetTitle("GitHub Copilot", chat),
            NotificationText.ResetTitle("GitHub Copilot", premium));
        Assert.DoesNotContain("Label_", NotificationText.ThresholdTitle("GitHub Copilot", premium, 90));
    }

    private static UsageWindow Window(
        UsageWindowType type, string? group = null, string? labelKey = null) => new()
    {
        Type = type,
        UsedRatio = 0.9,
        GroupLabel = group,
        LabelKey = labelKey,
        Label = WindowLabels.For(type, labelKey),
    };
}
