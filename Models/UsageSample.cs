namespace Gauge.Models;

/// <summary>
/// One recorded usage reading for a window, as stored in the usage history. Ordered by
/// capture time; consumed by trend/ETA computation.
/// </summary>
public readonly record struct UsageSample(DateTimeOffset CapturedAt, double UsedRatio);
