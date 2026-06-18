using System.Diagnostics;
using System.Text.Json;
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
            _ => Invalid(tool, "지원하지 않는 도구입니다."),
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
                return Invalid(ToolKind.ClaudeCode, "Claude Code 자격증명에 OAuth 토큰이 없습니다.");
            }

            var expiresAt = oauth.GetInt64OrNull("expiresAt") is { } ms
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : (DateTimeOffset?)null;
            if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            {
                return Invalid(ToolKind.ClaudeCode, "Claude Code 로그인이 만료되었습니다. 다시 로그인하세요.");
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
                return Invalid(ToolKind.Codex, "Codex 자격증명에 OAuth 토큰이 없습니다.");
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
            return new CredentialReadResult { Tool = tool, Status = CredentialReadStatus.Missing, Message = "로그인 정보가 없습니다." };
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
            return Invalid(tool, "자격증명 파일을 읽을 수 없습니다. CLI에서 다시 로그인하세요.");
        }
    }

    private CredentialReadResult Available(ToolKind tool, ToolCredential credential) => new()
    {
        Tool = tool, Status = CredentialReadStatus.Available, Credential = credential,
        Message = "공식 CLI 로그인 정보를 사용 중입니다.",
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
