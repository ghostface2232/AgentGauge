using Gauge.Models;

namespace Gauge.Providers;

/// <summary>
/// Who asked for a fetch. Mirrors CodexBar's ProviderInteraction: a rate-limit cooldown
/// may hold back <see cref="Background"/> fetches (the scheduler's cycles and the
/// popover-open forced refresh), but a <see cref="UserInitiated"/> one (the refresh
/// button, a completed sign-in, adding a tool) is never blocked by it — an explicit
/// user action always gets a real attempt.
/// </summary>
public enum FetchInteraction
{
    Background,
    UserInitiated,
}

/// <summary>
/// A source of usage data for one tool. Every provider normalizes its results into
/// the shared <see cref="UsageSnapshot"/> model, so the UI never depends on how the
/// data was obtained.
/// </summary>
public interface IUsageProvider
{
    /// <summary>The tool this provider reports on (used to filter by the registry).</summary>
    ToolKind Tool { get; }

    /// <summary>Display name of the tool this provider reports on.</summary>
    string ToolName { get; }

    /// <summary>
    /// Collects a current snapshot. Implementations should degrade gracefully
    /// (omit windows that cannot be obtained) rather than throwing for ordinary
    /// conditions like a missing credential or a tool that has no usage windows.
    /// </summary>
    Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="GetSnapshotAsync(CancellationToken)"/>, carrying who asked.
    /// The default ignores the interaction, so providers without throttling state need
    /// not implement it; throttled providers override to let a user-initiated refresh
    /// bypass their failure cooldown.
    /// </summary>
    Task<UsageSnapshot> GetSnapshotAsync(FetchInteraction interaction, CancellationToken cancellationToken)
        => GetSnapshotAsync(cancellationToken);
}
