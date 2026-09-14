using Gauge.Models;
using Gauge.ViewModels;

namespace Gauge.Tests;

public sealed class RefreshIndicatorTests
{
    [Fact]
    public void HungRefreshExpiresWithoutLosingTimestampOrRestartingOnCache()
    {
        var time = new Clock();
        var vm = new UsageViewModel(time: time);
        var captured = DateTimeOffset.UtcNow.AddHours(-1);
        var state = new UsageState { LastUpdatedAt = captured, Tools = [new CachedUsage { ToolName = "Codex", LastUpdatedAt = captured,
            Snapshot = new UsageSnapshot { ToolName = "Codex", CapturedAt = captured, Windows = [] } }] };
        vm.Apply(state);
        vm.SetRefreshing(["Codex"]);
        Assert.True(vm.Cards[0].IsRefreshing);
        time.Advance(UsageViewModel.RefreshIndicatorLimit - TimeSpan.FromSeconds(1));
        vm.SetRefreshing(["Codex"]);
        Assert.True(vm.ExpireRefreshIndicators());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(vm.ExpireRefreshIndicators());
        vm.Apply(state);
        vm.SetRefreshing(["Codex"]);
        Assert.False(vm.Cards[0].IsRefreshing);
        Assert.Equal(captured, vm.LastUpdatedAt);
        vm.ClearRefreshing(["Codex"]);
        vm.SetRefreshing(["Codex"]);
        Assert.True(vm.Cards[0].IsRefreshing);
        vm.ClearRefreshing(["Codex"]);
        Assert.False(vm.Cards[0].IsRefreshing);
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }
}
