namespace Gauge.Models;

/// <summary>
/// The kind of usage window a tool exposes. Tools differ in which windows they
/// have, so this is open to extension; the UI renders only the windows present
/// on a given <see cref="UsageSnapshot"/>.
/// </summary>
public enum UsageWindowType
{
    FiveHour,
    Weekly,
    // Per-model quota windows (e.g. Antigravity's separate Claude / GPT model limits).
    ModelQuota,
    // Credit/spend style usage measured over a billing cycle (e.g. GitHub Copilot).
    BillingCycle,
}
