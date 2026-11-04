using System.Text.Json;
using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers.Internal;

namespace Gauge.Services;

/// <summary>
/// Resolves the GitHub OAuth token used to read Copilot quota, trying the sources a Copilot
/// user is likely to have so it works across Windows setups, not just one:
///   1. the gh CLI (<c>gh auth token</c>) — gh owns the token's refresh, so it is the most
///      robust source and the one the standalone Copilot CLI itself authenticates "via";
///   2. a <c>github-copilot</c> <c>apps.json</c>/<c>hosts.json</c> file (<c>oauth_token</c>)
///      under <c>%LOCALAPPDATA%</c> or <c>~/.config</c> — written by copilot.vim/Neovim, older
///      editor extensions, and several third-party tools, for users without the gh CLI.
///
/// The token is read-only and kept in memory only; Gauge never writes or refreshes it, so this
/// can't disturb the owning client's login. All paths come from environment folders and PATH —
/// never a hardcoded user name or absolute path — so the same build works on any machine.
///
/// A setup that stores the token only in the editor's encrypted secret store (e.g. the newest
/// VS Code Copilot Chat) exposes no readable token here; that resolves to a clean "signed out"
/// card, the same as a tool that isn't installed.
/// </summary>
public sealed class GitHubCopilotCredentialSource : ICredentialSource
{
    private readonly IGitHubCliTokenReader _ghTokenReader;
    private readonly Func<string> _localAppData;
    private readonly Func<string> _userProfile;

    public GitHubCopilotCredentialSource(
        IGitHubCliTokenReader? ghTokenReader = null,
        Func<string>? localAppData = null,
        Func<string>? userProfile = null)
    {
        _ghTokenReader = ghTokenReader ?? new GitHubCliTokenReader();
        _localAppData = localAppData ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _userProfile = userProfile ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public CredentialOwner Owner => CredentialOwner.CliLocal;
    public CredentialSource Source => CredentialSource.CliLocal;

    public async Task<CredentialReadResult> ReadAsync(ToolKind tool, CancellationToken cancellationToken = default)
    {
        if (tool != ToolKind.GitHubCopilot)
        {
            // Not this source's tool; let the chain fall through to the others.
            return Missing();
        }

        // 1) gh CLI first — gh maintains and refreshes the token itself.
        string? token = null;
        var ghFailed = false;
        try
        {
            token = await _ghTokenReader.ReadTokenAsync(cancellationToken);
        }
        catch (GitHubCliReadException)
        {
            ghFailed = true;
        }

        // 2) Fall back to a github-copilot apps.json/hosts.json file (gh-less setups).
        token ??= ReadOAuthTokenFromFile();

        if (string.IsNullOrWhiteSpace(token))
        {
            // gh timing out after a wake or under an antivirus scan is not a logout.
            return ghFailed
                ? new CredentialReadResult
                {
                    Tool = ToolKind.GitHubCopilot,
                    Status = CredentialReadStatus.TemporarilyUnavailable,
                }
                : Missing();
        }

        return new CredentialReadResult
        {
            Tool = ToolKind.GitHubCopilot,
            Status = CredentialReadStatus.Available,
            Message = Loc.Get("Cred_CopilotInUse"),
            Credential = new ToolCredential
            {
                Tool = ToolKind.GitHubCopilot,
                Owner = Owner,
                Source = Source,
                AccessToken = token,
            },
        };
    }

    private string? ReadOAuthTokenFromFile()
    {
        foreach (var path in CandidateFiles())
        {
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream);
                if (FindOAuthToken(document.RootElement) is { Length: > 0 } token)
                {
                    return token;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Never log the file contents or token; the type name is enough to diagnose.
                DiagnosticsLog.Write("auth", $"Copilot token file read failed: {ex.GetType().Name}");
            }
        }
        return null;
    }

    private IEnumerable<string> CandidateFiles()
    {
        var localDir = Path.Combine(_localAppData(), "github-copilot");
        var configDir = Path.Combine(_userProfile(), ".config", "github-copilot");
        // apps.json is the current name; hosts.json is the legacy one. Both share the schema.
        foreach (var dir in new[] { localDir, configDir })
        {
            yield return Path.Combine(dir, "apps.json");
            yield return Path.Combine(dir, "hosts.json");
        }
    }

    /// <summary>
    /// The file maps an app/host key (e.g. <c>"github.com:Iv1.abc"</c>) to an object carrying an
    /// <c>oauth_token</c>. Only an entry whose host is exactly github.com qualifies: the token is
    /// sent to api.github.com, and a token issued for an Enterprise host must not leave for
    /// another server just because it was the only one in the file.
    /// </summary>
    internal static string? FindOAuthToken(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in root.EnumerateObject())
        {
            var host = property.Name.Split(':', 2)[0];
            if (host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && property.Value.GetStringOrNull("oauth_token") is { Length: > 0 } token)
            {
                return token;
            }
        }
        return null;
    }

    private static CredentialReadResult Missing() => new()
    {
        Tool = ToolKind.GitHubCopilot,
        Status = CredentialReadStatus.Missing,
        Message = Loc.Get("Cred_Missing"),
    };
}
