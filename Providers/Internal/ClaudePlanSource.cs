using System.Net.Http;
using System.Text.Json;
using Gauge.Models;
using Gauge.Services;

namespace Gauge.Providers.Internal;

/// <summary>Enriches the Claude usage credential with the current server-side plan.</summary>
internal sealed class ClaudePlanSource(
    ICredentialSource credentials, HttpClient http, TimeProvider? time = null) : ICredentialSource
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private string? _identity;
    private string? _accountIdentity;
    private string? _plan;
    private long _checkedAt;
    private TimeSpan _interval;

    public CredentialOwner Owner => credentials.Owner;
    public CredentialSource Source => credentials.Source;

    public async Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken)
    {
        var result = await credentials.ReadAsync(tool, cancellationToken);
        if (result.Status == CredentialReadStatus.Missing)
        {
            _identity = null;
            _accountIdentity = null;
            _plan = null;
        }
        if (tool != ToolKind.ClaudeCode || result.Status != CredentialReadStatus.Available
            || result.Credential is not { AccessToken.Length: > 0 } credential)
        {
            return result;
        }

        // Tokens can rotate or switch accounts. Never reuse a plan under a different token.
        var identity = UsageIdentity.Hash(null, credential.AccessToken);
        if (_identity != identity || _time.GetElapsedTime(_checkedAt) >= _interval)
        {
            if (_identity != identity)
            {
                _accountIdentity = null;
                _plan = null;
            }
            _identity = identity;
            _checkedAt = _time.GetTimestamp();
            _interval = RetryInterval;
            await RefreshPlanAsync(credential, cancellationToken);
        }

        if (_plan is null && _accountIdentity is null) return result;
        return result with
        {
            Credential = credential with
            {
                AccountIdentity = _accountIdentity ?? credential.AccountIdentity,
                Plan = _plan ?? credential.Plan,
                VerifiedPlan = _plan,
            },
        };
    }

    private async Task RefreshPlanAsync(ToolCredential credential, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = ClaudeProvider.CreateOAuthRequest(ProfileUrl, ClaudeProvider.UserAgent, credential);
        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var retryAfter = response.Headers.RetryAfter;
                var delay = retryAfter?.Delta ?? retryAfter?.Date - _time.GetUtcNow();
                if (delay is { } retryDelay && retryDelay > RetryInterval) _interval = retryDelay;
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            if (document.RootElement.GetObjectOrNull("organization") is not { } organization) return;
            if (document.RootElement.GetObjectOrNull("account")?.GetStringOrNull("uuid") is { Length: > 0 } accountId
                && organization.GetStringOrNull("uuid") is { Length: > 0 } organizationId)
            {
                var accountIdentity = UsageIdentity.Hash($"{accountId}\0{organizationId}", credential.AccessToken);
                if (_accountIdentity is not null && _accountIdentity != accountIdentity) _plan = null;
                _accountIdentity = accountIdentity;
            }

            var plan = ProviderText.Normalize(organization.GetStringOrNull("plan_display_name"));
            if (plan is null or "Max"
                && organization.GetStringOrNull("organization_type") is { } type
                && type.StartsWith("claude_", StringComparison.OrdinalIgnoreCase))
            {
                plan = CliCredentialSource.MapClaudePlan(type["claude_".Length..],
                    organization.GetStringOrNull("rate_limit_tier")) ?? plan;
            }
            if (plan is null) return;
            _plan = plan;
            _interval = RefreshInterval;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }
    }
}
