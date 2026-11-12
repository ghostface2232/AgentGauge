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
    private readonly Func<string?> _claudeConfig;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _scopeHistory;

    /// <param name="delay">
    /// Seam for the retry wait, so tests exercise the retry without spending its duration.
    /// </param>
    public CliCredentialSource(
        Func<string>? userProfile = null,
        Func<string?>? codexHome = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string?>? claudeConfig = null,
        bool scopeHistory = false)
    {
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _codexHome = codexHome ?? (() => Environment.GetEnvironmentVariable("CODEX_HOME"));
        _claudeConfig = claudeConfig ?? (() => Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));
        _delay = delay ?? Task.Delay;
        _scopeHistory = scopeHistory;
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
            // Tools this source does not own (e.g. Antigravity, GitHub Copilot) are handled by
            // their dedicated sources in the chain. Report Missing so we don't shadow them.
            _ => new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing, Message = Loc.Get("Cred_Missing") },
        };
    }

    private Task<CredentialReadResult> ReadClaudeAsync(CancellationToken cancellationToken)
    {
        var profile = _userProfile();
        var config = _claudeConfig();
        var hasConfig = !string.IsNullOrWhiteSpace(config);
        var path = Path.Combine(hasConfig ? config! : Path.Combine(profile, ".claude"), ".credentials.json");
        var profilePath = Path.Combine(hasConfig ? config! : profile, ".claude.json");
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

            var (plan, subject) = ReadClaudeProfile(profilePath);
            return Available(ToolKind.ClaudeCode, new ToolCredential
            {
                Tool = ToolKind.ClaudeCode,
                Owner = Owner,
                Source = Source,
                AccessToken = token,
                ExpiresAt = expiresAt,
                Plan = plan ?? MapClaudePlan(oauth.GetStringOrNull("subscriptionType"), oauth.GetStringOrNull("rateLimitTier")),
                // Account changes require both the user and organization. Older profiles can
                // have only the user, which is still enough for their existing history key.
                AccountIdentity = subject is not null && !subject.EndsWith('\0') ? UsageIdentity.Hash(subject, token) : null,
                HistoryIdentity = _scopeHistory ? UsageIdentity.Hash(subject, token) : null,
            });
        });
    }

    /// <summary>
    /// The plan from the profile the CLI caches in <c>~/.claude.json</c> (<c>oauthAccount</c>).
    /// The credentials file only rewrites <c>subscriptionType</c>/<c>rateLimitTier</c> when the
    /// access token next rotates, so after a plan change it keeps naming the old plan for
    /// hours while the usage windows already follow the new one; the profile is refetched
    /// from the server and moves first. Only personal Max/Pro accounts are read here, and any
    /// failure falls back to the credentials file's own values.
    /// </summary>
    private static (string? Plan, string? Subject) ReadClaudeProfile(string profilePath)
    {
        try
        {
            using var stream = File.OpenRead(profilePath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.GetObjectOrNull("oauthAccount") is not { } account) return default;
            var subject = account.GetStringOrNull("accountUuid") is { Length: > 0 } user
                ? $"{user}\0{account.GetStringOrNull("organizationUuid")}" : null;
            if (account.GetStringOrNull("organizationType") is not { } type
                || !type.StartsWith("claude_", StringComparison.OrdinalIgnoreCase))
            {
                return (null, subject);
            }

            var subscription = type["claude_".Length..];
            if (!subscription.Equals("max", StringComparison.OrdinalIgnoreCase)
                && !subscription.Equals("pro", StringComparison.OrdinalIgnoreCase))
            {
                return (null, subject);
            }
            return (MapClaudePlan(subscription, account.GetStringOrNull("organizationRateLimitTier")), subject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
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

            var accountId = tokens.GetStringOrNull("account_id");
            var subject = CodexSubject(tokens, token, accountId);
            return Available(ToolKind.Codex, new ToolCredential
            {
                Tool = ToolKind.Codex,
                Owner = Owner,
                Source = Source,
                AccessToken = token,
                AccountId = accountId,
                AccountIdentity = subject is not null && !subject.EndsWith('\0') ? UsageIdentity.Hash(subject, token) : null,
                HistoryIdentity = _scopeHistory ? UsageIdentity.Hash(subject, token) : null,
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
        try
        {
            return ReadJwtClaims(jwt)?.GetInt64OrNull("exp") is { } seconds
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? CodexSubject(JsonElement tokens, string token, string? accountId)
    {
        const string authClaim = "https://api.openai.com/auth";
        var identity = ReadJwtClaims(tokens.GetStringOrNull("id_token"));
        var access = ReadJwtClaims(token);
        var identityAuth = identity?.GetObjectOrNull(authClaim);
        var accessAuth = access?.GetObjectOrNull(authClaim);
        // An account ID can be shared by multiple users in the same ChatGPT workspace.
        var user = accessAuth?.GetStringOrNull("chatgpt_user_id") ?? identityAuth?.GetStringOrNull("chatgpt_user_id")
            ?? identity?.GetStringOrNull("sub") ?? access?.GetStringOrNull("sub");
        var workspace = accountId ?? accessAuth?.GetStringOrNull("chatgpt_account_id")
            ?? identityAuth?.GetStringOrNull("chatgpt_account_id");
        return string.IsNullOrEmpty(user) ? null : $"{user}\0{workspace}";
    }

    private static JsonElement? ReadJwtClaims(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
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
            // Anthropic folded the 20x Max tier into Ultra, which has no 5-hour window.
            "max" when rateLimitTier?.Contains("20x", StringComparison.OrdinalIgnoreCase) == true
                || rateLimitTier?.Contains("ultra", StringComparison.OrdinalIgnoreCase) == true => "Ultra",
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
