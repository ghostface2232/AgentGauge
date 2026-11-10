using Gauge.Services;

namespace Gauge.Providers.Internal;

internal sealed class AntigravityCliReader(ICliLocator locator) : IAntigravityReader
{
    private static readonly Version MinimumVersion = new(1, 1, 11);
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true",
    };

    public bool IsInstalled => locator.Find("agy") is not null;

    public async Task<AntigravityReading?> ReadAsync(CancellationToken cancellationToken)
    {
        if (locator.Find("agy") is not { } executable) return null;
        var version = await CliUsageProbe.RunAsync(executable, "--version", TimeSpan.FromSeconds(3),
            cancellationToken, environment: Environment);
        // Older versions could send /usage to a model as a literal prompt.
        if (!Version.TryParse(version?.Trim(), out var installed) || installed < MinimumVersion) return null;
        var report = await CliUsageProbe.RunAsync(executable, "-p /usage --output-format json",
            TimeSpan.FromSeconds(15), cancellationToken, environment: Environment);
        return report is null ? null : new AntigravityReading(report, null);
    }
}
