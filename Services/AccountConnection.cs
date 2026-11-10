using Gauge.Localization;
using Gauge.Models;
using Gauge.Providers;

namespace Gauge.Services;

/// <summary>Builds isolated credential, login, refresh, and usage state for one account.</summary>
internal sealed record AccountConnection(IAuthenticationProvider Authentication, IUsageProvider Usage)
{
    public static AccountConnection Create(ToolAccount account, HttpClient httpClient, ICliLocator locator)
    {
        var tool = account.Tool;
        if (tool == ToolKind.GitHubCopilot)
        {
            var source = new GitHubCopilotCredentialSource(
                new GitHubCliTokenReader(locator, account.Label), useLocalFiles: false);
            var authentication = new CliAuthenticationProvider(tool, source, locator, new CliProcessRunner(),
                missingMessage: Loc.Get("Account_CopilotSignIn"));
            var copilot = new GitHubCopilotProvider(httpClient, source, accountLogin: account.Label);
            return new(authentication, new AccountUsageProvider(account, copilot));
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Gauge", "accounts", account.Id.ToString("N"));
        var isClaude = tool == ToolKind.ClaudeCode;
        var environment = isClaude
            ? new Dictionary<string, string?>
            {
                ["CLAUDE_CONFIG_DIR"] = directory,
                ["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = directory,
                ["CLAUDE_CODE_FORCE_WINDOWS_CREDMAN"] = null,
                ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
                ["CLAUDE_CODE_OAUTH_TOKEN_FILE"] = null,
                ["CLAUDE_CODE_OAUTH_TOKEN_FD"] = null,
                ["ANTHROPIC_AUTH_TOKEN"] = null,
                ["ANTHROPIC_API_KEY"] = null,
            }
            : new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = directory,
                ["CODEX_ACCESS_TOKEN"] = null,
                ["CODEX_API_KEY"] = null,
                ["OPENAI_API_KEY"] = null,
            };
        var runner = new CliProcessRunner(environment, directory);
        var credentials = new CliCredentialSource(codexHome: () => directory, claudeConfig: () => directory, scopeHistory: true);
        const string fileCredentials = "-c cli_auth_credentials_store=file";
        var auth = new CliAuthenticationProvider(tool, credentials, locator, runner,
            loginArguments: isClaude ? "auth login" : $"login {fileCredentials}");
        var descriptor = ToolCatalog.For(tool);
        var refresher = new DelegatedTokenRefresher(descriptor.LoginCommand,
            isClaude ? "mcp list" : $"{fileCredentials} doctor",
            TimeSpan.FromSeconds(isClaude ? 15 : 30), locator, runner);
        IUsageProvider usage = isClaude
            ? new ClaudeProvider(httpClient, credentials, refresher)
            : new CodexProvider(httpClient, credentials, refresher);
        return new(auth, new AccountUsageProvider(account, usage, auth));
    }
}
