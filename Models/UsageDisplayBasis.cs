namespace Gauge.Models;

/// <summary>
/// Which quantity a usage window's percent (and its bar/gauge fill) represents.
/// <see cref="Used"/> is the original presentation — an untouched window reads 0% and the
/// fill grows as quota is consumed. <see cref="Remaining"/> inverts it — an untouched window
/// reads 100% with a full fill that drains toward empty. Only the presentation flips: the
/// ok/caution/danger color still tracks how much has been used, so a nearly drained bar is
/// red in either basis. App-wide — chosen once in settings and applied to every card.
/// </summary>
public enum UsageDisplayBasis
{
    Used = 0,
    Remaining = 1,
}
