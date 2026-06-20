using Gauge.Services;

namespace Gauge.Tests;

/// <summary>
/// Real-process coverage for the hidden runner, focused on the .cmd/.bat routing: an
/// npm-installed `claude.cmd` must run via cmd.exe, since a script shim can't be started
/// directly once output is redirected.
/// </summary>
public sealed class CliProcessRunnerTests : IDisposable
{
    // A space in the directory verifies the quoting of the wrapped command path.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Gauge Runner " + Guid.NewGuid().ToString("N"));

    public CliProcessRunnerTests() => Directory.CreateDirectory(_dir);

    [Theory]
    [InlineData("shim.cmd")]
    [InlineData("shim.bat")]
    public async Task RunHiddenRunsScriptShimAndPropagatesExitCode(string fileName)
    {
        var script = Path.Combine(_dir, fileName);
        // Exit with a distinctive code so we know the shim actually executed.
        await File.WriteAllTextAsync(script, "@echo off\r\nexit /b 7\r\n");

        var result = await new CliProcessRunner()
            .RunHiddenAsync(script, "mcp list", TimeSpan.FromSeconds(15), default);

        Assert.False(result.TimedOut);
        Assert.Equal(7, result.ExitCode);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }
}
