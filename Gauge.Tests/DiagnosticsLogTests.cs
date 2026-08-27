using Gauge.Services;

namespace Gauge.Tests;

/// <summary>Rotating diagnostics log: append format, rotation, and failure-swallowing.</summary>
public sealed class DiagnosticsLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeLogTest_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousOverride = DiagnosticsLog.DirectoryOverride;

    public DiagnosticsLogTests() => DiagnosticsLog.DirectoryOverride = _dir;

    [Fact]
    public void WritesTimestampedLineWithArea()
    {
        DiagnosticsLog.Write("provider", "Codex refresh failed: HttpRequestException: boom");

        var line = Assert.Single(File.ReadAllLines(Path.Combine(_dir, "gauge.log")));
        Assert.Contains("[provider]", line);
        Assert.Contains("Codex refresh failed", line);
    }

    [Fact]
    public void RotatesWhenPrimaryFileExceedsLimit()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "gauge.log"), new string('x', 300 * 1024));

        DiagnosticsLog.Write("app", "after rotation");

        Assert.True(File.Exists(Path.Combine(_dir, "gauge.1.log")));
        var fresh = File.ReadAllText(Path.Combine(_dir, "gauge.log"));
        Assert.Contains("after rotation", fresh);
        Assert.True(new FileInfo(Path.Combine(_dir, "gauge.log")).Length < 1024);
    }

    public void Dispose()
    {
        // Restore the suite-wide override (see TestLogDirectory), not null: clearing it
        // would send every later test's log line to the real %APPDATA%\Gauge\logs.
        DiagnosticsLog.DirectoryOverride = _previousOverride;
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
