namespace Gauge.Providers.Internal;

/// <summary>
/// Builds the command-line arguments Gauge uses to launch the Antigravity language server in
/// delegate mode. These mirror exactly what the IDE passes when it spawns the same
/// engine — including <c>--app_data_dir antigravity</c>, which is how the engine finds the
/// on-disk login and authenticates itself — with two deliberate substitutions: Gauge supplies
/// its own CSRF token (so it need not read one back from the process) and forces
/// <c>--https_server_port 0</c> for a random port. Faithful replication matters: the engine
/// authenticates with its own stored credentials, exactly like Claude/Codex delegated refresh,
/// and Gauge never touches those credentials.
/// </summary>
internal static class AntigravityEngineLaunch
{
    public static IReadOnlyList<string> BuildArguments(string csrfToken, string? ideVersion)
    {
        var args = new List<string>
        {
            "--standalone",
            "--override_ide_name", "antigravity",
            "--subclient_type", "hub",
        };

        // Replicate the IDE's version override when known; omit it otherwise so the engine falls
        // back to its built-in version rather than receiving a bogus value.
        if (!string.IsNullOrWhiteSpace(ideVersion))
        {
            args.Add("--override_ide_version");
            args.Add(ideVersion);
        }

        args.AddRange(new[]
        {
            "--override_user_agent_name", "antigravity",
            "--https_server_port", "0",      // random port; learned from the OS listener table
            "--csrf_token", csrfToken,        // Gauge-generated; reused as the request header
            "--app_data_dir", "antigravity",  // verbatim — how the engine locates the stored login
            "--api_server_url", "https://generativelanguage.googleapis.com",
            "--cloud_code_endpoint", "https://daily-cloudcode-pa.googleapis.com",
            "--enable_sidecars",
        });

        return args;
    }
}
