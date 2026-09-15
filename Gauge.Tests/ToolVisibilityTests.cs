using Gauge.Models;
using Gauge.Services;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class ToolVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedSettingsLeaveVisibilityAndPollingUnchanged(bool initiallyHidden)
    {
        var dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(AppSettingsFile.TrySave(dir, dto =>
            {
                dto.EnabledTools = ["Codex"];
                dto.HiddenTools = initiallyHidden ? ["Codex"] : [];
            }));
            var registry = new ToolRegistry(new ToolRegistryStore(() => dir));
            var events = 0;
            registry.VisibilityChanged += (_, _) => events++;
            var path = Path.Combine(dir, "settings.json");
            var original = File.ReadAllText(path);

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.False(registry.SetHidden(ToolKind.Codex, !initiallyHidden));

            Assert.Equal(initiallyHidden, registry.IsHidden(ToolKind.Codex));
            Assert.Equal(!initiallyHidden, registry.IsActive(ToolKind.Codex));
            Assert.Equal(0, events);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.Equal(initiallyHidden, new ToolRegistry(new ToolRegistryStore(() => dir)).IsHidden(ToolKind.Codex));

            Assert.True(registry.SetHidden(ToolKind.Codex, !initiallyHidden));
            Assert.Equal(1, events);
            Assert.Equal(!initiallyHidden, new ToolRegistry(new ToolRegistryStore(() => dir)).IsHidden(ToolKind.Codex));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void VisibilityPersistsWithoutLosingOtherSettingsAndNewToolsAreVisible()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), """{"Future":{"keep":42},"Language":"ja"}""");
            var registry = new ToolRegistry(new ToolRegistryStore(() => dir));
            Assert.All(registry.Enabled, k => Assert.False(registry.IsHidden(k)));
            registry.SetHidden(ToolKind.Codex, true);
            Assert.True(registry.IsEnabled(ToolKind.Codex));
            Assert.False(registry.IsActive(ToolKind.Codex));
            Assert.True(new ToolRegistry(new ToolRegistryStore(() => dir)).IsHidden(ToolKind.Codex));
            Assert.Contains("Future", File.ReadAllText(Path.Combine(dir, "settings.json")));
            registry.Remove(ToolKind.Codex);
            registry.Add(ToolKind.Codex);
            Assert.True(registry.IsActive(ToolKind.Codex));
            registry.SetHidden(ToolKind.Codex, true);
            var vm = new UsageViewModel(registry);
            vm.Apply(new UsageState { Tools = [new CachedUsage { ToolName = "Codex", Snapshot = new UsageSnapshot
            { ToolName = "Codex", Windows = [new UsageWindow { Type = UsageWindowType.Weekly, Label = "Weekly", UsedRatio = .99 }] } }] });
            Assert.Empty(vm.Cards);
            Assert.Equal(0, vm.HighestUsageRatio);
            Assert.DoesNotContain("99", vm.TrayTooltipSummary);
        }
        finally { Directory.Delete(dir, true); }
    }
}
