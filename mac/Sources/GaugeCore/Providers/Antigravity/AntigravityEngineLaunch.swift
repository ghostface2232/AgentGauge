import Foundation

/// The arguments Gauge launches the Antigravity language server with in delegate mode (app
/// closed). They mirror what `Antigravity.app` itself passes when it spawns the same engine
/// (`startLanguageServer` in the app bundle, identical to the Windows IDE's), including
/// `--app_data_dir antigravity` — how the engine finds the login on disk and authenticates
/// itself, so Gauge never touches those credentials. Three deliberate substitutions:
/// - Gauge supplies its own CSRF token, so it never reads one back from a process;
/// - `--https_server_port 0` picks a random port, learned from the kernel's socket table;
/// - `--exit_on_stdin_close` (the app's own flag for its WSL mode) ties the engine's life to
///   a pipe only Gauge holds, so an AgentGauge crash or force-quit mid-read cannot leave it
///   running — the kernel closes the pipe and the engine exits on its own.
enum AntigravityEngineLaunch {
    static func arguments(csrfToken: String, ideVersion: String?) -> [String] {
        var arguments = [
            "--standalone",
            "--override_ide_name", "antigravity",
            "--subclient_type", "hub",
        ]
        if let ideVersion = ProviderText.normalize(ideVersion) {
            arguments += ["--override_ide_version", ideVersion]
        }
        arguments += [
            "--override_user_agent_name", "antigravity",
            "--https_server_port", "0",
            "--csrf_token", csrfToken,
            "--app_data_dir", "antigravity",
            "--api_server_url", "https://generativelanguage.googleapis.com",
            "--cloud_code_endpoint", "https://daily-cloudcode-pa.googleapis.com",
            "--enable_sidecars",
            "--exit_on_stdin_close",
        ]
        return arguments
    }
}
