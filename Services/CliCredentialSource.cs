using System.Globalization;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;

namespace Gauge.Services;

/// <summary>Reads credentials owned by the official CLIs. This class never writes them.</summary>
public sealed class CliCredentialSource : ICredentialSource
{
    // A CLI rewriting its credential file is a sub-second event, so a couple of short
    // waits comfortably outlast it while adding nothing to the common path (they run
    // only after a read has already failed).
    private const int TransientReadAttempts = 3;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(60);

    private readonly Func<string> _userProfile;
    private readonly Func<string?> _codexHome;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">
    /// Seam for the retry wait, so tests exercise the retry without spending its duration.
    /// </param>
    public CliCredentialSource(
        Func<string>? userProfile = null,
        Func<string?>? codexHome = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _codexHome = codexHome ?? (() => Environment.GetEnvironmentVariable("CODEX_HOME"));
        _delay = delay ?? Task.Delay;
    }

    public CredentialOwner Owner => CredentialOwner.CliLocal;
    public CredentialSource Source => CredentialSource.CliLocal;

    public async Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return tool switch
        {
            ToolKind.ClaudeCode => await ReadClaudeAsync(cancellationToken),
            ToolKind.Codex => await ReadCodexAsync(cancellationToken),
            // Tools this source does not own (e.g. Antigravity, Cursor) are handled by
            // their dedicated sources in the chain. Report Missing so we don't shadow them.
            _ => new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing, Message = Loc.Get("Cred_Missing") },
        };
    }

    private Task<CredentialReadResult> ReadClaudeAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_userProfile(), ".claude", ".credentials.json");
        return ReadJsonAsync(ToolKind.ClaudeCode, path, cancellationToken, root =>
        {
            if (root.GetObjectOrNull("claudeAiOauth") is not { } oauth
                || oauth.GetStringOrNull("accessToken") is not { Length: > 0 } token)
            {
                return Invalid(ToolKind.ClaudeCode, Loc.Get("Cred_ClaudeNoToken"));
            }

            var expiresAt = oauth.GetInt64OrNull("expiresAt") is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : (DateTimeOffset?)null;
            if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                return Invalid(ToolKind.ClaudeCode, Loc.Get("Cred_ClaudeExpired"));
            }

            return Available(ToolKind.ClaudeCode, new ToolCredential
            {
                Tool = ToolKind.ClaudeCode,
                Owner = Owner,
                Source = Source,
                AccessToken = token,
                ExpiresAt = expiresAt,
                Plan = MapClaudePlan(oauth.GetStringOrNull("subscriptionType"), oauth.GetStringOrNull("rateLimitTier")),
            });
        });
    }

    private Task<CredentialReadResult> ReadCodexAsync(CancellationToken cancellationToken)
    {
        var home = _codexHome();
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(_userProfile(), ".codex");
        }
        var path = Path.Combine(home, "auth.json");
        return ReadJsonAsync(ToolKind.Codex, path, cancellationToken, root =>
        {
            if (root.GetObjectOrNull("tokens") is not { } tokens
                || tokens.GetStringOrNull("access_token") is not { Length: > 0 } token)
            {
                return Invalid(ToolKind.Codex, Loc.Get("Cred_CodexNoToken"));
            }

            // The Codex access token is a ChatGPT-issued JWT (~10-day lifetime). Unlike
            // Claude's credentials file there is no expiry field, so we read it from the
            // JWT's own `exp` claim. After ~10 days without running Codex the token is
            // expired at boot; reporting Invalid here lets the provider trigger a delegated
            // refresh (codex doctor) instead of waiting for a 401 from the usage endpoint.
            var expiresAt = ParseJwtExpiry(token);
            if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                return Invalid(ToolKind.Codex, Loc.Get("Cred_CodexExpired"));
            }

            return Available(ToolKind.Codex, new ToolCredential
            {
                Tool = ToolKind.Codex,
                Owner = Owner,
                Source = Source,
                AccessToken = token,
                AccountId = tokens.GetStringOrNull("account_id"),
                ExpiresAt = expiresAt,
            });
        });
    }

    /// <summary>
    /// Reads and parses a CLI's credential file, retrying briefly on a failure that looks
    /// like the CLI rewriting it.
    ///
    /// Gauge reads these files without coordination, so a read can land in the middle of the
    /// CLI's own token rotation — the very moment the read-only policy exists to tolerate.
    /// That surfaces as a sharing violation (IOException) or as a parse failure on a
    /// half-written file (JsonException), neither distinguishable from real corruption on a
    /// single attempt. Reporting Invalid there is expensive and wrong: the provider spawns a
    /// pointless delegated-refresh CLI and the auth card reads "signed out" until the next
    /// live fetch, for a file that was valid a few milliseconds earlier and valid again a few
    /// milliseconds later. Retrying settles it. A denied file (UnauthorizedAccessException) is
    /// not transient — no wait fixes permissions — so it fails on the first attempt, and a
    /// file that stays unreadable still ends as Invalid.
    ///
    /// Absence is decided once, before any attempt: only a file that was never there means
    /// "not signed in". A file that disappears mid-retry throws FileNotFoundException, which
    /// is an IOException and so retries with the rest — a rotation that recreates the file
    /// still resolves, and one that does not ends as Invalid, exactly as it did before the
    /// retry existed. Deciding absence per attempt would instead report Missing, whose empty
    /// snapshot the coordinator treats as a success and writes over the last good one.
    /// </summary>
    private async Task<CredentialReadResult> ReadJsonAsync(
        ToolKind tool, string path, CancellationToken cancellationToken, Func<JsonElement, CredentialReadResult> parse)
    {
        if (!File.Exists(path))
        {
            return new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing, Message = Loc.Get("Cred_Missing") };
        }

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement root;
            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                // Detached from the document so parsing can happen outside this try: the
                // retry must cover reading and parsing the file, never the callback's own
                // interpretation of it.
                root = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is IOException or JsonException && attempt < TransientReadAttempts)
            {
                await _delay(TransientRetryDelay, cancellationToken);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Never include file contents or token values in diagnostics.
                DiagnosticsLog.Write(
                    "auth",
                    $"Credential read failed for {tool} after {attempt.ToString(CultureInfo.InvariantCulture)} attempt(s): {ex.GetType().Name}");
                return Invalid(tool, Loc.Get("Cred_ReadFailed"));
            }

            return parse(root);
        }
    }

    private CredentialReadResult Available(ToolKind tool, ToolCredential credential) => new()
    {
        Tool = tool, Status = CredentialReadStatus.Available, Credential = credential,
        Message = Loc.Get("Cred_CliInUse"),
    };

    private static CredentialReadResult Invalid(ToolKind tool, string message) => new()
    {
        Tool = tool, Status = CredentialReadStatus.Invalid, Message = message,
    };

    /// <summary>
    /// Reads the <c>exp</c> claim (Unix seconds) from a JWT's payload without validating
    /// the signature — we only need the expiry, not to trust the token. Returns null if the
    /// value isn't a well-formed JWT with a numeric exp.
    /// </summary>
    internal static DateTimeOffset? ParseJwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            if (document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            // Not a decodable JWT, or an out-of-range exp from a corrupt token; treat as
            // "no expiry known" rather than letting it escape as an unhandled exception.
        }
        return null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    internal static string? MapClaudePlan(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType)) return null;
        return subscriptionType.ToLowerInvariant() switch
        {
            "max" when rateLimitTier?.Contains("20x", StringComparison.OrdinalIgnoreCase) == true => "Max 20x",
            "max" when rateLimitTier?.Contains("5x", StringComparison.OrdinalIgnoreCase) == true => "Max 5x",
            "max" => "Max",
            "pro" => "Pro",
            "free" => "Free",
            "team" => "Team",
            "enterprise" => "Enterprise",
            var value => char.ToUpperInvariant(value[0]) + value[1..],
        };
    }
}
