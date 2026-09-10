using Gauge.Models;

namespace Gauge.Localization;

public static class ProviderStatusText
{
    public static bool IsVisible(ProviderStatus status)
        => status.LastCheckFailed || status.Level != ProviderStatusLevel.Operational;

    public static string Label(ProviderStatus status) => Loc.Get(status.LastCheckFailed
        ? "ServiceStatus_Stale" : status.Level switch
        {
            ProviderStatusLevel.Minor => "ServiceStatus_Minor",
            ProviderStatusLevel.Major or ProviderStatusLevel.Critical => "ServiceStatus_Major",
            ProviderStatusLevel.Maintenance => "ServiceStatus_Maintenance",
            _ => "ServiceStatus_Unknown",
        });

    public static string Description(ProviderStatus status) => Loc.Format("ServiceStatus_Detail",
        status.Summary.Length > 0 ? status.Summary : Loc.Get("ServiceStatus_Unknown"),
        status.CheckedAt?.ToLocalTime().ToString("g", Loc.Culture) ?? "–");
}
