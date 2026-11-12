namespace Gauge.Providers.Internal;

/// <summary>
/// One usage read from an Antigravity engine: the raw quota JSON plus the best-effort plan
/// label. The two come from separate Connect calls (RetrieveUserQuotaSummary and GetUserStatus),
/// and the plan is optional — a missing plan never invalidates the quota.
/// </summary>
internal sealed record AntigravityReading(string QuotaJson, string? Plan, bool PlanUnavailable = false);

/// <summary>
/// A way to read usage from an Antigravity engine. There are two: attaching to a running IDE's
/// language server, and launching the engine ourselves (delegate). The provider tries them in
/// order and treats a null result as "this source could not reach an engine — try the next".
/// </summary>
internal interface IAntigravityReader
{
    Task<AntigravityReading?> ReadAsync(CancellationToken cancellationToken);
}
