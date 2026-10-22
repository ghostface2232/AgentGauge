<img width="1440" alt="banner" src="https://github.com/user-attachments/assets/ba1971e5-3081-47a2-9212-90115f249c87" />



# AgentGauge

AgentGauge is a lightweight desktop app for **macOS** (menu bar) and **Windows** (system tray) that lets you monitor real usage limits and quotas for AI coding tools at a glance: **Claude Code**, **Codex**, **Cursor**, **Antigravity**, and **GitHub Copilot**.

It reads each tool's official usage endpoints using local, read-only credentials — displaying your exact rate-limit windows, reset timers, and pace tracking without manual guesswork.

## Screenshots

<img width="1440" alt="1" src="https://github.com/user-attachments/assets/2e961b59-b9d9-4be7-8bf8-78c42dc19e39" />
<img width="1440" alt="2" src="https://github.com/user-attachments/assets/6e83ea37-2855-4c00-90e8-3e18f6a047a4" />
<img width="1440" alt="3" src="https://github.com/user-attachments/assets/4a30e33e-f116-45e8-b85f-e8947fdf6cda" />


## Features

- **Accurate rate-limit quotas**: Shows live usage windows reported directly by official APIs, including Claude's 5-hour session and model-scoped weekly limits (such as Fable), Codex's 5-hour and weekly windows, Cursor's billing-cycle limits, GitHub Copilot's monthly metered allowances, and Antigravity's plan-enabled model families (Gemini and Claude/GPT).
- **Flexible display styles**: Switch between horizontal **bars** and circular **gauges** in Settings. Progress bars/gauges and the tray/menu bar icon change color as limits fill up (warning at 70%, critical at 90%).
- **Pace tracking & run-out projections**: An unobtrusive warning appears when usage runs ahead of an even pace. Weekly pace curves can be adjusted to every day, workdays only (Mon–Fri), or an automatic curve based on your personal weekday rhythm.
- **Sparklines & burn rates**: Glanceable sparklines trace recent quota trends, complemented by measured burn-rate projections for when active windows will run out.
- **Optional API-equivalent cost**: Displays estimated monthly usage priced at public API list rates (e.g. "≈ $340") calculated from local session token logs (off by default; reads token counts only — see [PRIVACY.md](PRIVACY.md)).
- **Native OS integration**:
  - **macOS**: Native menu bar app with Liquid Glass styling on macOS 26+ (standard look on macOS 14/15), needle-gauge status icon, and system notifications.
  - **Windows**: WinUI 3 popover styled after the Windows 11 Quick Settings panel, docking smoothly to the taskbar notification area.
- **Tool management**: Enable or hide tools from Settings (Claude Code and Codex are shown by default; enable Cursor, Antigravity, or GitHub Copilot anytime).
- **Background refresh**: Automatically polls usage every few minutes and immediately upon opening the popover.
- **Launch at startup & theme options**: Configurable start-on-boot, light/dark mode support, and internal scrolling for tall tool lists.
- **Multilingual**: Available in English, Korean, and Japanese — automatically matching your system display language.


## Requirements

| Platform | Requirements |
| --- | --- |
| **macOS** | macOS 14 Sonoma or later (Apple silicon and Intel supported) |
| **Windows** | Windows 10 version 2004 (build 19041) or later, or Windows 11 (x64) |

You will also need the tools you wish to monitor installed and signed in on your system (the Claude Code CLI, Codex CLI, Cursor app, Antigravity app, and/or GitHub CLI for Copilot).


## Installation

### macOS

1. Download **AgentGauge-macOS.dmg** from the [latest release](https://github.com/ghostface2232/AgentGauge/releases/latest).
2. Open the disk image and drag **AgentGauge.app** to your **Applications** folder.
3. Launch AgentGauge from Applications. Click the gauge icon in your menu bar to open the panel.

*(Manual zip archives and source build instructions are available in [`mac/`](mac/README.md).)*

### Windows

1. Download **GaugeSetup-win-x64.exe** from the [latest release](https://github.com/ghostface2232/AgentGauge/releases/latest).
2. Run the installer (per-user installation; no administrator privileges required) and launch the app from the finish page.
3. Click the AgentGauge icon in the taskbar notification area. Right-click the tray icon to configure startup options or exit.


## Sign-in and Data Privacy

AgentGauge never issues, stores, or transmits your credentials to external servers. It reads the local authentication state already maintained by each official tool strictly in **read-only** mode.

| Tool | Credential Source (macOS) | Credential Source (Windows) | Sign-in Command / Method |
| --- | --- | --- | --- |
| **Claude Code** | Login Keychain (`Claude Code-credentials`) or `~/.claude/.credentials.json` | `%USERPROFILE%\.claude\.credentials.json` | `claude /login` |
| **Codex** | `$CODEX_HOME/auth.json` or `~/.codex/auth.json` | `%CODEX_HOME%\auth.json` or `%USERPROFILE%\.codex\auth.json` | `codex login` |
| **Cursor** | `~/Library/Application Support/Cursor/.../state.vscdb` (read-only) | `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (read-only) | Sign in via the Cursor app |
| **Antigravity** | None read: fetched via local loopback engine | None read: fetched via local loopback engine | Sign in via the Antigravity app |
| **GitHub Copilot** | `gh auth token`, or fallback config files (`~/.config/github-copilot/`) | `gh auth token`, or fallback config files (`%LOCALAPPDATA%\github-copilot\`) | `gh auth login` |

- **Cursor**: AgentGauge reads its session token directly from Cursor's local state database in read-only mode — no secondary CLI login is required.
- **Antigravity**: AgentGauge reads no credential files. Quotas are fetched over an authenticated local loopback (`127.0.0.1`) connection to Antigravity's language server (either the currently running instance, or a short-lived process launched to inspect current quota).
- **Token Freshness**: When a CLI token expires after prolonged inactivity, AgentGauge triggers the CLI's own non-interactive background refresh commands (`claude mcp list`, `codex doctor`) so the official CLI renews its token safely.


## Updates

AgentGauge includes an automatic update checker:
- On startup, AgentGauge quietly checks GitHub Releases for new versions. When an update is detected, the button in Settings changes to **Update**. You can also trigger an on-demand check at any time via **Check for updates**.
- Clicking **Update** downloads the release asset and verifies its SHA-256 digest against GitHub's published hash before applying the update and restarting.


## Current Limitations

- **CLI Token Expiry**: AgentGauge relies on credentials maintained by official CLIs. If a tool remains unused for an extended period and its session fully expires, you may need to re-authenticate via the CLI.
- **Code Signing**: Release binaries are not currently signed with paid commercial certificates. You may see a Gatekeeper prompt on macOS ("Open Anyway" in System Settings) or Windows SmartScreen on first launch.
- **Architecture**: macOS builds are Universal (supporting Apple silicon and Intel). Windows builds currently target x64.


## Credits

- Inspired by [CodexBar](https://github.com/steipete/codexbar), the macOS menu bar app for tracking AI coding tool usage.
- Typography set in [Pretendard](https://github.com/orioncactus/pretendard) by Kil Hyung-jin, bundled under the SIL Open Font License 1.1.
