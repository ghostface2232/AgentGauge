using Gauge.Localization;
using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class UsageAccessibilityTests
{
    [Theory]
    [InlineData(AppLanguage.Korean, "퍼센트")]
    [InlineData(AppLanguage.English, "percent")]
    [InlineData(AppLanguage.Japanese, "パーセント")]
    public void CompositeNameIncludesScopeQuotaAndAllAvailableCaptions(AppLanguage language, string percent)
    {
        try
        {
            Loc.Initialize(language);
            var row = new UsageWindowRowViewModel(new UsageWindow { Type = UsageWindowType.BillingCycle,
                Label = "Premium requests", GroupLabel = "Family", UsedRatio = .42,
                ResetTime = DateTimeOffset.UtcNow.AddDays(1), UsedTokens = 42, LimitTokens = 100 });
            row.PaceText = "pace";
            row.EtaText = "eta";
            Assert.Contains("Family Premium requests", row.AccessibilityName);
            Assert.Contains("42" + (language == AppLanguage.English ? " " : "") + percent, row.AccessibilityName);
            Assert.Contains(row.ResetText, row.AccessibilityName);
            Assert.Contains("42 / 100", row.AccessibilityName);
            Assert.Contains("pace. eta", row.AccessibilityName);
            var updates = new List<string?>();
            row.PropertyChanged += (_, e) => updates.Add(e.PropertyName);
            row.ResetText = "new reset";
            Assert.Contains(nameof(row.AccessibilityName), updates);
            Assert.Contains("new reset", row.AccessibilityName);
        }
        finally { Loc.Initialize(AppLanguage.Korean); }
    }

    [Theory]
    [InlineData(AppLanguage.Korean, "5h, 58퍼센트 남음")]
    [InlineData(AppLanguage.English, "5h, 58 percent remaining")]
    [InlineData(AppLanguage.Japanese, "5h、残り58パーセント")]
    public void CompositeNameSpeaksRemainingInRemainingBasis(AppLanguage language, string expected)
    {
        try
        {
            Loc.Initialize(language);
            var row = new UsageWindowRowViewModel(new UsageWindow { Type = UsageWindowType.FiveHour,
                Label = "5h", UsedRatio = .42, ResetTime = null }, UsageDisplayBasis.Remaining);
            Assert.Equal(expected, row.AccessibilityName);
        }
        finally { Loc.Initialize(AppLanguage.Korean); }
    }

    [Fact]
    public void SwitchingBasisReRaisesCompositeNameEvenWhenTheNumberIsUnchanged()
    {
        try
        {
            Loc.Initialize(AppLanguage.English);
            // 50% used is also 50% remaining, so only the wording changes — the name must
            // still be re-announced.
            var row = new UsageWindowRowViewModel(new UsageWindow { Type = UsageWindowType.FiveHour,
                Label = "5h", UsedRatio = .5, ResetTime = null });
            var updates = new List<string?>();
            row.PropertyChanged += (_, e) => updates.Add(e.PropertyName);

            row.DisplayBasis = UsageDisplayBasis.Remaining;

            Assert.Contains(nameof(row.AccessibilityName), updates);
            Assert.Equal("5h, 50 percent remaining", row.AccessibilityName);
        }
        finally { Loc.Initialize(AppLanguage.Korean); }
    }

    [Fact]
    public void CompositeNameOmitsUnknownResetWithoutDanglingSeparator()
    {
        try
        {
            Loc.Initialize(AppLanguage.English);
            var row = new UsageWindowRowViewModel(new UsageWindow { Type = UsageWindowType.FiveHour,
                Label = "5h", UsedRatio = .42, ResetTime = null });
            Assert.Equal("5h, 42 percent used", row.AccessibilityName);
        }
        finally { Loc.Initialize(AppLanguage.Korean); }
    }
}
