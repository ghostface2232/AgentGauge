using System.Diagnostics;

namespace Gauge.Services;

/// <summary>
/// Recovers from an expired CLI OAuth access token without ever refreshing the token
/// itself — it delegates the refresh to the official CLI.
/// </summary>
public interface IDelegatedTokenRefresher
{
    /// <summary>
    /// Attempts to make the official CLI refresh its own OAuth token so the freshened
    /// credentials can be re-read. Returns true if the CLI was actually run (the caller
    /// should re-read credentials), false if the attempt was skipped (cooldown active or
    /// the CLI is not installed).
    /// </summary>
    Task<bool> TryRefreshAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Delegated OAuth refresh for a CLI-owned credential (Claude Code, Codex).
///
/// THE PROBLEM: a CLI's local access token expires. After a boot it may already be
/// expired before the CLI/app is ever launched, so Gauge can't read usage until the tool
/// is opened once (the CLI then uses its refresh token to mint a new access token and
/// rewrites its credentials file).
///
/// THE FIX: do exactly what launching the tool does — but headlessly. We run a CLI command
/// that goes through the CLI's full authenticated bootstrap; that bootstrap is what
/// refreshes an expired access token (via the refresh token) and rewrites the credentials
/// file, after which Gauge re-reads the fresh token. We never call the OAuth token endpoint
/// ourselves, so the refresh-token rotation stays entirely owned by the CLI and the tool's
/// own login can never be broken by Gauge. (With a still-valid token the bootstrap leaves
/// the credentials file untouched.)
///
/// Which command refreshes is tool-specific and was verified against each real CLI — a
/// status command (e.g. <c>claude auth status</c> / <c>codex login status</c>) only prints
/// cached auth and does NOT refresh, so the caller must pass a command that establishes a
/// full authenticated context (see <see cref="App"/> wiring for the exact commands and
/// rationale). The token refresh happens early in the bootstrap, before the command does
/// its own work, so even if the run is slow or hits the timeout, the freshened token is
/// already on disk for Gauge to re-read.
///
/// To avoid spawning the CLI on every 60s poll — e.g. when the user is genuinely logged
/// out and no refresh is possible — each attempt sets a cooldown.
/// </summary>
public sealed class DelegatedTokenRefresher : IDelegatedTokenRefresher
{
    // After a completed attempt the token is good for hours/days, so wait a while before
    // the next nudge. After a timeout/error retry sooner in case it was transient.
    private static readonly TimeSpan SuccessCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromMinutes(1);

    private readonly string _command;
    private readonly string _arguments;
    private readonly TimeSpan _processTimeout;
    private readonly ICliLocator _locator;
    private readonly ICliProcessRunner _runner;
    private readonly Func<long> _nowTicks;

    // Accessed only from the coordinator's serialized refresh (one provider call at a
    // time), matching the provider threading model, so no locking is needed.
    private long _cooldownUntilTick;

    public DelegatedTokenRefresher(
        string command,
        string arguments,
        TimeSpan processTimeout,
        ICliLocator locator,
        ICliProcessRunner runner,
        Func<long>? nowTicks = null)
    {
        _command = command;
        _arguments = arguments;
        _processTimeout = processTimeout;
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

        var executable = _locator.Find(_command);
        if (executable is null)
        {
            // No CLI to delegate to; back off so we don't probe PATH every cycle.
            SetCooldown(SuccessCooldown);
            return false;
        }

        try
        {
            var result = await _runner.RunHiddenAsync(executable, _arguments, _processTimeout, cancellationToken);
            SetCooldown(result.TimedOut ? RetryCooldown : SuccessCooldown);
            Debug.WriteLine($"[Gauge] {_command} delegated refresh ran (exit={result.ExitCode}, timedOut={result.TimedOut})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetCooldown(RetryCooldown);
            Debug.WriteLine($"[Gauge] {_command} delegated refresh failed: {ex.GetType().Name}");
            return false;
        }
    }

    private void SetCooldown(TimeSpan cooldown) =>
        _cooldownUntilTick = _nowTicks() + (long)cooldown.TotalMilliseconds;
}
