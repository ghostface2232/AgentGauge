using Gauge.Models;
using Gauge.Services;

namespace Gauge.Providers;

/// <summary>Scopes an existing provider's snapshots to an additional account's storage key.</summary>
internal sealed class AccountUsageProvider(
    ToolAccount account, IUsageProvider provider, IAuthenticationProvider? authentication = null) : IUsageProvider
{
    public ToolKind Tool => account.Tool;
    public string ToolName => account.Key;
    public bool IsSigningIn => authentication?.IsLoginRunning == true;

    public Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        GetSnapshotAsync(FetchInteraction.Background, cancellationToken);

    public async Task<UsageSnapshot> GetSnapshotAsync(FetchInteraction interaction, CancellationToken cancellationToken)
    {
        var snapshot = await provider.GetSnapshotAsync(interaction, cancellationToken);
        return snapshot with { ToolName = ToolName };
    }

    async Task<UsageSnapshot> IUsageProvider.ReadAsync(
        FetchInteraction interaction, UsageReadContext context, CancellationToken cancellationToken)
    {
        var snapshot = await provider.ReadAsync(interaction, context, cancellationToken);
        return snapshot with { ToolName = ToolName };
    }
}
