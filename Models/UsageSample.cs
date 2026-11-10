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
    DateTimeOffset CapturedAt, double UsedRatio, DateTimeOffset? ResetTime = null,
    int? ResetCredits = null, DateTimeOffset? ResetCreditExpiry = null)
{
    private const double MinimumManualResetDrop = 0.05;

    public bool IsManualResetAfter(UsageSample previous)
        // A balance change alone does not tell us which window reset. A drop after the
        // old window expired cannot be separated from an ordinary rollover.
        => CapturedAt > previous.CapturedAt
            && ResetCredits is { } balance && previous.ResetCredits is { } prior && balance < prior
            && UsedRatio <= previous.UsedRatio - MinimumManualResetDrop
            && (previous.ResetTime is not { } scheduled || CapturedAt < scheduled)
            && (previous.ResetCreditExpiry is not { } expiry || CapturedAt < expiry);
}
