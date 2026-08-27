using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Capture/restore protocol of <see cref="ForegroundLockGuard"/>: the user's
/// foreground-lock timeout survives a clean exit, a hard kill (via the persisted
/// baseline), and a deliberate user zero, and Restore runs exactly once.
/// </summary>
public sealed class ForegroundLockGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DisableZeroesAndRestorePutsTheOriginalBack()
    {
        // The baseline must reach disk BEFORE the zero lands, so a kill at any later
        // instant loses nothing; capture what the file held at the moment of the zero.
        uint? persistedWhenZeroed = null;
        var timeout = new FakeTimeout { Current = 200_000 };
        timeout.OnSet = value =>
        {
            if (value == 0) persistedWhenZeroed = AppSettingsFile.Load(_dir).ForegroundLockTimeoutBaseline;
        };
        var guard = new ForegroundLockGuard(timeout, () => _dir);

        guard.Disable();
        Assert.Equal(0u, timeout.Current);
        Assert.Equal(200_000u, persistedWhenZeroed);

        guard.Restore();
        Assert.Equal(200_000u, timeout.Current);
        Assert.Null(AppSettingsFile.Load(_dir).ForegroundLockTimeoutBaseline);
    }

    [Fact]
    public void HardKilledInstanceLeavesABaselineTheNextLaunchRestores()
    {
        // First instance zeroes the timeout and is hard-killed: no restore path ran.
        var timeout = new FakeTimeout { Current = 200_000 };
        new ForegroundLockGuard(timeout, () => _dir).Disable();
        Assert.Equal(0u, timeout.Current);

        // Next launch reads a live 0 — Gauge's own leftover, not the user's setting. It
        // must adopt the persisted baseline instead of persisting 0 as the new "original".
        var next = new ForegroundLockGuard(timeout, () => _dir);
        next.Disable();
        Assert.Equal(200_000u, AppSettingsFile.Load(_dir).ForegroundLockTimeoutBaseline);

        next.Restore();
        Assert.Equal(200_000u, timeout.Current);
    }

    [Fact]
    public void DeliberateUserZeroIsPreservedWhenNoBaselineIsPersisted()
    {
        // A clean previous exit cleared the key, so a live 0 here is the user's own
        // focus-stealing tweak and must round-trip as-is.
        var timeout = new FakeTimeout { Current = 0 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);

        guard.Disable();
        guard.Restore();

        Assert.Equal(0u, timeout.Current);
    }

    [Fact]
    public void AFailedRestoreKeepsTheBaselineSoItCanBeRetried()
    {
        // The live value is still Gauge's zero when the write fails, so dropping the
        // baseline would strand the user's setting with nothing left to recover it from.
        var timeout = new FakeTimeout { Current = 200_000 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);
        guard.Disable();

        timeout.SetFails = true;
        guard.Restore();

        Assert.Equal(0u, timeout.Current); // the write did not take
        Assert.Equal(200_000u, AppSettingsFile.Load(_dir).ForegroundLockTimeoutBaseline);

        // The ProcessExit net (or the next launch, via the persisted baseline) retries.
        timeout.SetFails = false;
        guard.Restore();

        Assert.Equal(200_000u, timeout.Current);
        Assert.Null(AppSettingsFile.Load(_dir).ForegroundLockTimeoutBaseline);
    }

    [Fact]
    public void ARestoreDoesNotWriteOverAFileThatBecameUnreadableAfterCapture()
    {
        // Readable when the baseline was captured, corrupt by the time the app exits.
        // Whether the key may be cleared is decided by reading at write time, not by
        // remembering that the capture wrote one.
        var timeout = new FakeTimeout { Current = 200_000 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);
        guard.Disable();

        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ corrupted after capture");

        guard.Restore();

        Assert.Equal(200_000u, timeout.Current);
        Assert.Equal("{ corrupted after capture", File.ReadAllText(path));
    }

    [Fact]
    public void UserValueChangedAfterACrashReplacesTheStaleBaseline()
    {
        new ForegroundLockGuard(new FakeTimeout { Current = 200_000 }, () => _dir).Disable(); // crash: no restore

        // The user (or a fresh sign-in) set a new non-zero value before the next launch;
        // that live value wins over the stale persisted one.
        var timeout = new FakeTimeout { Current = 150_000 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);
        guard.Disable();
        guard.Restore();

        Assert.Equal(150_000u, timeout.Current);
    }

    [Fact]
    public void RestoreRunsExactlyOnce()
    {
        var timeout = new FakeTimeout { Current = 200_000 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);
        guard.Disable();
        guard.Restore();
        guard.Restore(); // Dispose + ProcessExit overlap on a normal exit
        Assert.Equal(new uint[] { 0, 200_000 }, timeout.SetCalls);
    }

    [Fact]
    public void UnreadableTimeoutStillZeroesButRestoreIsANoOp()
    {
        // With no readable current value there is no baseline: the zero is still applied
        // (the menu needs it regardless) but Restore has nothing to put back.
        var timeout = new FakeTimeout { Current = 200_000, GetFails = true };
        var guard = new ForegroundLockGuard(timeout, () => _dir);
        guard.Disable();
        guard.Restore();
        Assert.Equal(new uint[] { 0 }, timeout.SetCalls);
    }

    [Fact]
    public void UnreadableSettingsWithALiveZeroNeverAuthorsAWrite()
    {
        // Hard kill left a live 0, and settings.json is transiently unreadable at the
        // next launch. Persisting the 0 here would overwrite the real baseline the file
        // still holds — the exact permanent loss the guard exists to prevent — so the
        // guard must neither write the file nor adopt a baseline.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not valid json");
        var timeout = new FakeTimeout { Current = 0 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);

        guard.Disable();
        guard.Restore();

        Assert.Equal("{ not valid json", File.ReadAllText(path));
        Assert.Equal(new uint[] { 0 }, timeout.SetCalls);
    }

    [Fact]
    public void UnreadableSettingsWithALiveNonZeroRestoresFromMemoryWithoutPersisting()
    {
        // A non-zero live value is unambiguously the user's setting even when the file
        // cannot be read: it must round-trip through this run in memory, while the
        // failed read must not author a baseline write.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not valid json");
        var timeout = new FakeTimeout { Current = 150_000 };
        var guard = new ForegroundLockGuard(timeout, () => _dir);

        guard.Disable();
        Assert.Equal("{ not valid json", File.ReadAllText(path));

        guard.Restore();
        Assert.Equal(150_000u, timeout.Current);
        // Nor may the restore write over a file it still cannot parse — that would rewrite
        // it from defaults and drop every other store's keys (see ForegroundLockGuard.Restore).
        Assert.Equal("{ not valid json", File.ReadAllText(path));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeTimeout : IForegroundLockTimeout
    {
        public uint Current { get; set; }
        public bool GetFails { get; set; }
        public bool SetFails { get; set; }
        public List<uint> SetCalls { get; } = new();
        /// <summary>Observation hook invoked on every set, before the value is applied.</summary>
        public Action<uint>? OnSet { get; set; }

        public bool TryGet(out uint timeout)
        {
            timeout = Current;
            return !GetFails;
        }

        public bool TrySet(uint timeout)
        {
            OnSet?.Invoke(timeout);
            SetCalls.Add(timeout);
            if (SetFails)
            {
                return false; // SystemParametersInfo refused; the live value is unchanged
            }
            Current = timeout;
            return true;
        }
    }
}
