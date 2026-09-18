namespace Gauge.Models;

/// <summary>
/// How the pace caption spreads a multi-day window's quota over its cycle — the curve the
/// row's actual utilization is compared against. <see cref="Uniform"/> is the original
/// model: every hour of the cycle is expected to consume the same share. It calls a user
/// who works Monday to Friday "in deficit" all week, because it expects the weekend's share
/// to have been spent too. <see cref="WorkDays"/> expects nothing on Saturday and Sunday,
/// so the expected curve is flat over the weekend. <see cref="Automatic"/> shapes the curve
/// from the recorded usage history's per-weekday consumption once enough of it exists, and
/// reads as <see cref="Uniform"/> until then. App-wide — chosen once in settings and applied
/// to every card. Windows shorter than two days (the 5-hour sessions) always use the uniform
/// spread: a weekday model has nothing to say inside one afternoon.
/// </summary>
public enum WeeklyPaceModel
{
    Uniform = 0,
    WorkDays = 1,
    Automatic = 2,
}
