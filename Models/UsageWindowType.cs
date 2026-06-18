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
}
