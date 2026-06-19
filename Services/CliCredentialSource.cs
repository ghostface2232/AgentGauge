using System.Diagnostics;
using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;

namespace Gauge.Services;

/// <summary>Reads credentials owned by the official CLIs. This class never writes them.</summary>
public sealed class CliCredentialSource : ICredentialSource
{
    private readonly Func<string> _userProfile;
    private readonly Func<string?> _codexHome;

    public CliCredentialSource(Func<string>? userProfile = null, Func<string?>? codexHome = null)
    {
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _codexHome = codexHome ?? (() => Environment.GetEnvironmentVariable("CODEX_HOME"));
    }

    public CredentialOwner Owner => CredentialOwner.CliLocal;
    public CredentialSource Source => CredentialSource.CliLocal;

    public Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(tool switch
        {
            ToolKind.ClaudeCode => ReadClaude(),
            ToolKind.Codex => ReadCodex(),
            // Tools this source does not own (e.g. Antigravity, Cursor) are handled by
            // their dedicated sources in the chain. Report Missing so we don't shadow them.
            _ => new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing, Message = Loc.Get("Cred_Missing") },
        });
    }

    private CredentialReadResult ReadClaude()
    {
        var path = Path.Combine(_userProfile(), ".claude", ".credentials.json");
        return ReadJson(ToolKind.ClaudeCode, path, root =>
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

    private CredentialReadResult ReadCodex()
    {
        var home = _codexHome();
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(_userProfile(), ".codex");
        }
        var path = Path.Combine(home, "auth.json");
        return ReadJson(ToolKind.Codex, path, root =>
        {
            if (root.GetObjectOrNull("tokens") is not { } tokens
                || tokens.GetStringOrNull("access_token") is not { Length: > 0 } token)
            {
                return Invalid(ToolKind.Codex, Loc.Get("Cred_CodexNoToken"));
            }
            return Available(ToolKind.Codex, new ToolCredential
            {
                Tool = ToolKind.Codex,
                Owner = Owner,
                Source = Source,
                AccessToken = token,
                AccountId = tokens.GetStringOrNull("account_id"),
            });
        });
    }

    private static CredentialReadResult ReadJson(
        ToolKind tool, string path, Func<JsonElement, CredentialReadResult> parse)
    {
        if (!File.Exists(path))
        {
            return new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing, Message = Loc.Get("Cred_Missing") };
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return parse(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never include file contents or token values in diagnostics.
            Debug.WriteLine($"[Gauge] Credential read failed for {tool}: {ex.GetType().Name}");
            return Invalid(tool, Loc.Get("Cred_ReadFailed"));
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
