<img width="1440" alt="AgentGauge_Title" src="https://github.com/user-attachments/assets/359f83c2-0d28-471b-ad06-fccce0777afa" />



# AgentGauge

AgentGauge is a lightweight desktop app for **macOS** (menu bar) and **Windows** (system tray) that lets you monitor real usage limits and quotas for AI coding tools at a glance: **Claude Code**, **Codex**, **Cursor**, **Antigravity**, and **GitHub Copilot**.

It reads the same usage figures each tool shows you — from the vendor's own service using local, read-only credentials, or from Antigravity's local engine — and displays your rate-limit windows, reset timers, and pace tracking without manual guesswork. Most of these endpoints are undocumented, internal ones, so a vendor can change them without notice.

## Screenshots

<img width="1440" alt="Quick & easy" src="https://github.com/user-attachments/assets/2e961b59-b9d9-4be7-8bf8-78c42dc19e39" />
<img width="1440" alt="Dynamic tray icon" src="https://github.com/user-attachments/assets/6e83ea37-2855-4c00-90e8-3e18f6a047a4" />
<img width="1440" alt="Horizontal bars & Circular gauges" src="https://github.com/user-attachments/assets/4a30e33e-f116-45e8-b85f-e8947fdf6cda" />
<img width="1440" alt="Native macOS" src="https://github.com/user-attachments/assets/9a0e80bf-d0ee-43eb-beb5-b2fc4b9f23a8" />

## Features

- **Accurate rate-limit quotas**: Shows live usage windows as each vendor's service reports them, including Claude's 5-hour session and model-scoped weekly limits (such as Fable), Codex's 5-hour and weekly windows, Cursor's billing-cycle limits (macOS only), GitHub Copilot's monthly metered allowances, and Antigravity's plan-enabled model families (Gemini and Claude/GPT).
- **Flexible display styles**: Switch between horizontal **bars** and circular **gauges** in Settings. Progress bars/gauges and the tray/menu bar icon change color as limits fill up (warning at 70%, critical at 90%).
- **Pace tracking & run-out projections**: An unobtrusive warning appears when usage runs ahead of an even pace. Weekly pace curves can be adjusted to every day, workdays only (Mon–Fri), or an automatic curve based on your personal weekday rhythm.
- **Sparklines & burn rates**: Glanceable sparklines trace recent quota trends, complemented by measured burn-rate projections for when active windows will run out.
- **Optional API-equivalent cost**: Displays estimated monthly usage priced at public API list rates (e.g. "≈ $340") calculated from local session token logs (off by default; reads token counts only — see [PRIVACY.md](PRIVACY.md)).
- **Native OS integration**:
  - **macOS**: Native menu bar app with Liquid Glass styling on macOS 26+ (standard look on macOS 14/15), needle-gauge status icon, and system notifications.
  - **Windows**: WinUI 3 popover styled after the Windows 11 Quick Settings panel, docking smoothly to the taskbar notification area.
- **Tool management**: Enable or hide tools from Settings (Claude Code and Codex are shown by default; enable Cursor (macOS only), Antigravity, or GitHub Copilot anytime).
- **Background refresh**: Automatically polls usage every few minutes and immediately upon opening the popover.
- **Launch at startup & theme options**: Configurable start-on-boot, light/dark mode support, and internal scrolling for tall tool lists.
- **Multilingual**: Available in English, Korean, and Japanese — automatically matching your system display language.


## Requirements

| Platform | Requirements |
| --- | --- |
| **macOS** | macOS 14 Sonoma or later (Apple silicon and Intel supported) |
| **Windows** | Windows 10 version 2004 (build 19041) or later, or Windows 11 (x64) |

You will also need the tools you wish to monitor installed and signed in on your system (the Claude Code CLI, Codex CLI, Cursor app (macOS only), Antigravity app, and/or GitHub CLI for Copilot).


## Installation

### macOS

1. Download **AgentGauge-macOS.dmg** from the [latest release](https://github.com/ghostface2232/AgentGauge/releases/latest).
2. Open the disk image and drag **AgentGauge.app** to your **Applications** folder.
3. Launch AgentGauge from Applications. Click the gauge icon in your menu bar to open the panel.

*(Manual zip archives and source build instructions are available in [`mac/`](mac/README.md).)*

### Windows

1. Download **GaugeSetup-win-x64.exe** from the [latest release](https://github.com/ghostface2232/AgentGauge/releases/latest).
2. Run the installer (per-user installation; no administrator privileges required) and launch the app from the finish page. The installer is not code-signed, so Windows SmartScreen may warn you: choose **More info**, then **Run anyway**.
3. Click the AgentGauge icon in the taskbar notification area. Right-click the tray icon to configure startup options or exit.


## Sign-in and Data Privacy

AgentGauge reads the local authentication state already maintained by each official tool strictly in **read-only** mode. It never issues or stores credentials, and it sends a token only to the service that token belongs to. Nothing goes to the developer. See [PRIVACY.md](PRIVACY.md) for exactly what is read, sent, and kept on your device.

| Tool | Credential Source (macOS) | Credential Source (Windows) | Sign-in Command / Method |
| --- | --- | --- | --- |
| **Claude Code** | Login Keychain (`Claude Code-credentials`) or `~/.claude/.credentials.json` | `%USERPROFILE%\.claude\.credentials.json` | `claude /login` |
| **Codex** | `$CODEX_HOME/auth.json` or `~/.codex/auth.json` | `%CODEX_HOME%\auth.json` or `%USERPROFILE%\.codex\auth.json` | `codex login` |
| **Cursor** | `~/Library/Application Support/Cursor/.../state.vscdb` (read-only) | Not supported on Windows | Sign in via the Cursor app |
| **Antigravity** | No credential file: fetched via local loopback engine | No credential file: fetched via local loopback engine | Sign in via the Antigravity app |
| **GitHub Copilot** | `gh auth token`, or fallback config files (`~/.config/github-copilot/`) | `gh auth token`, or fallback config files (`%LOCALAPPDATA%\github-copilot\`) | `gh auth login` |

- **Cursor** (macOS only): AgentGauge reads its session token directly from Cursor's local state database in read-only mode — no secondary CLI login is required. Cursor 3.27+ stores that token encrypted, so macOS may ask for access to the "Cursor Safe Storage" Keychain item when you refresh; background refreshes never prompt. The Windows app does not support Cursor.
- **Antigravity**: AgentGauge reads no credential files. Quotas are fetched over a local loopback (`127.0.0.1`) connection to Antigravity's language server: either the currently running instance, using the session token from its process command line, or a short-lived process launched to inspect current quota.
- **Token Freshness**: When a CLI token expires after prolonged inactivity, AgentGauge triggers the CLI's own non-interactive background refresh commands (`claude mcp list`, `codex doctor`) so the official CLI renews its token safely.


## Updates

AgentGauge updates itself from GitHub Releases:
- **Automatic by default**: Each time it starts, AgentGauge checks for a newer release and, if there is one, downloads it, installs it without asking, and restarts. Since it usually starts at logon, this is typically when an update lands.
- **Turning it off**: Use the switch in Settings (General on Windows, About on macOS). AgentGauge still checks at launch, but installs only when you ask.
- **Updating manually**: Use **Check for updates** in Settings, which becomes **Update** when a newer release is found. The macOS panel footer also offers a found update.
- **Verification**: Every download is checked against the SHA-256 digest GitHub reports before it is installed. This catches a corrupted download, but it doesn't prove who built the release, since releases are not commercially code-signed.


## Current Limitations

- **Undocumented Endpoints**: Most usage endpoints are the vendors' internal ones rather than public APIs. If a vendor changes one, that tool's card can stop updating until AgentGauge is updated.
- **CLI Token Expiry**: AgentGauge relies on credentials maintained by official CLIs. If a tool remains unused for an extended period and its session fully expires, you may need to re-authenticate via the CLI.
- **Code Signing**: Release binaries are not currently signed with paid commercial certificates. You may see a Gatekeeper prompt on macOS ("Open Anyway" in System Settings) or Windows SmartScreen on first launch.
- **Architecture**: macOS builds are Universal (supporting Apple silicon and Intel). Windows builds currently target x64.


## Credits

- Inspired by [CodexBar](https://github.com/steipete/codexbar), the macOS menu bar app for tracking AI coding tool usage.
- Typography set in [Pretendard](https://github.com/orioncactus/pretendard) by Kil Hyung-jin, bundled under the SIL Open Font License 1.1.


## License

AgentGauge is released under the [MIT License](LICENSE). Pretendard keeps its own license, as noted above. Tool names and logos belong to their respective owners.
