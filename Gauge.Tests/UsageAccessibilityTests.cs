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
}
