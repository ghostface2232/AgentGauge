using Gauge.Models;
using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Persistence validation for <see cref="ToolRegistryStore"/>: a missing, malformed, or
/// partly-unknown settings file must always degrade to the default Claude Code + Codex
/// set rather than throw, so first run and corrupted state both show a working UI.
/// </summary>
public sealed class ToolRegistryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GaugeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MalformedJsonFallsBackToDefault()
    {
        WriteSettings("{ not valid json");
        var loaded = new ToolRegistryStore(() => _dir).Load();
        Assert.Equal(new[] { ToolKind.ClaudeCode, ToolKind.Codex }, loaded);
    }

    [Fact]
    public void UnknownToolNamesAreFilteredAndValidOnesKept()
    {
        WriteSettings("""{ "EnabledTools": ["ClaudeCode", "Bogus", "Cursor"] }""");

        var loaded = new ToolRegistryStore(() => _dir).Load();

        Assert.Equal(2, loaded.Count);
        Assert.Contains(ToolKind.ClaudeCode, loaded);
        Assert.Contains(ToolKind.Cursor, loaded);
        Assert.DoesNotContain(ToolKind.Codex, loaded);
    }

    [Theory]
    [InlineData("""{ "EnabledTools": [] }""")]
    [InlineData("{}")]
    [InlineData("""{ "EnabledTools": ["Bogus", "AlsoBogus"] }""")]
    public void EmptyOrAllUnknownFallsBackToDefault(string json)
    {
        WriteSettings(json);
        var loaded = new ToolRegistryStore(() => _dir).Load();
        Assert.Equal(new[] { ToolKind.ClaudeCode, ToolKind.Codex }, loaded);
    }

    private void WriteSettings(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
