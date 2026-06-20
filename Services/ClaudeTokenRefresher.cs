using System.Diagnostics;

namespace Gauge.Services;

/// <summary>
/// Recovers from an expired Claude OAuth access token without ever refreshing the
/// token itself.
/// </summary>
public interface IClaudeTokenRefresher
{
    /// <summary>
    /// Attempts to make the Claude CLI refresh its own OAuth token so the freshened
    /// credentials can be re-read. Returns true if the CLI was actually run (the caller
    /// should re-read credentials), false if the attempt was skipped (cooldown active or
    /// the CLI is not installed).
    /// </summary>
    Task<bool> TryRefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Delegated OAuth refresh for Claude Code.
///
/// THE PROBLEM: the CLI's access token in <c>~/.claude/.credentials.json</c> lives only
/// a few hours. After the PC has been off overnight, that token is expired at boot, so
/// Gauge can't read usage until Claude Code is launched once (the CLI then uses its
/// refresh token to mint a new access token and rewrites the file).
///
/// THE FIX: do exactly what launching Claude Code does — but headlessly. We run
/// <c>claude auth status</c> in the background; when the access token is expired the CLI
/// refreshes it via its refresh token and rewrites the credentials file, after which
/// Gauge re-reads the fresh token. We never call the OAuth token endpoint ourselves, so
/// the refresh-token rotation stays entirely owned by the CLI and Claude Code's own
/// login can never be broken by Gauge. (With a still-valid token the command is a no-op
/// that doesn't even touch the file.)
///
/// To avoid spawning the CLI on every 60s poll — e.g. when the user is genuinely logged
/// out and no refresh is possible — each attempt sets a cooldown.
/// </summary>
public sealed class ClaudeTokenRefresher : IClaudeTokenRefresher
{
    private const string Command = "claude";
    private const string Arguments = "auth status";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    // After a completed attempt the token is good for hours, so wait a while before the
    // next nudge. After a timeout/error retry sooner in case it was transient.
    private static readonly TimeSpan SuccessCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(1);

    private readonly ICliLocator _locator;
    private readonly ICliProcessRunner _runner;
    private readonly Func<long> _nowTicks;

    // Accessed only from the coordinator's serialized refresh (one provider call at a
    // time), matching ClaudeProvider's threading model, so no locking is needed.
    private long _cooldownUntilTick;

    public ClaudeTokenRefresher(ICliLocator locator, ICliProcessRunner runner, Func<long>? nowTicks = null)
    {
        _locator = locator;
        _runner = runner;
        _nowTicks = nowTicks ?? (() => Environment.TickCount64);
    }

    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        var now = _nowTicks();
        if (_cooldownUntilTick != 0 && now < _cooldownUntilTick)
        {
            return false;
        }

        var executable = _locator.Find(Command);
        if (executable is null)
        {
            // No CLI to delegate to; back off so we don't probe PATH every cycle.
            SetCooldown(SuccessCooldown);
            return false;
        }

        try
        {
            var result = await _runner.RunHiddenAsync(executable, Arguments, ProcessTimeout, cancellationToken);
            SetCooldown(result.TimedOut ? RetryCooldown : SuccessCooldown);
            Debug.WriteLine($"[Gauge] Claude delegated refresh ran (exit={result.ExitCode}, timedOut={result.TimedOut})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetCooldown(RetryCooldown);
            Debug.WriteLine($"[Gauge] Claude delegated refresh failed: {ex.GetType().Name}");
            return false;
        }
    }

    private void SetCooldown(TimeSpan cooldown) =>
        _cooldownUntilTick = _nowTicks() + (long)cooldown.TotalMilliseconds;
}
