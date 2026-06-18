namespace Gauge.Models;

/// <summary>
/// Usage severity used to color the progress bar. Always pair with the percent
/// number for accessibility — never convey state by color alone.
/// </summary>
public enum UsageLevel
{
    Ok,
    Caution,
    Danger,
}
