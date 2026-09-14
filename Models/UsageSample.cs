namespace Gauge.Models;

/// <summary>
/// One recorded usage reading for a window, as stored in the usage history. Ordered by
/// capture time; consumed by trend/ETA computation.
///
/// <c>ResetTime</c> is the reset the provider reported with this reading. It is
/// the authoritative identity of the cycle the reading belongs to (see
/// <c>UsageCycleBoundary</c>), and is null for a provider that reports none.
/// </summary>
public readonly record struct UsageSample(
    DateTimeOffset CapturedAt, double UsedRatio, DateTimeOffset? ResetTime = null);
