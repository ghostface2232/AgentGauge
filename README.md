<img width="1440" alt="agentgauge_banner" src="https://github.com/user-attachments/assets/b6d6a7df-ca9b-4ed9-8a7a-bf22d8730c9e" />

# AgentGauge

AgentGauge is a Windows system-tray app that lets you check the real usage limits of Claude Code, Codex, and Cursor at a glance.


## Screenshot

<img width="1440" alt="agentgauge_img1" src="https://github.com/user-attachments/assets/e349d33f-9bc4-4b69-ab2a-340bc01d1bd6" />
<img width="1440" alt="agentgauge_img2" src="https://github.com/user-attachments/assets/cd691d04-cc4b-4caf-95a3-efc620ad6f6b" />


## Features

- Shows real usage for Claude Code, Codex, and Cursor.
- Register the services you want with **+ Add service** in settings, and remove them from their card (default: Claude Code · Codex).
- Progress bars and the tray icon turn yellow above 70% and red above 90%.
- Refreshes usage every 60 seconds, and immediately when you open the app from the tray.
- Caps the popover height and scrolls internally when you add many tools.
- Optional run on Windows startup.
- Light and dark mode.
- UI in English, Korean, or Japanese — detected from your Windows display language on first run (English for anything else).


## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11.
- An x64 PC.
- The tools you want to track installed: the Claude Code CLI, the Codex CLI, and/or the Cursor app.
- Being signed in to those tools (for Cursor, sign in from the Cursor app).


## Running

1. Install with `GaugeSetup-win-x64.exe` (no administrator rights required). You can launch it straight from the finish page.
2. Left-click the AgentGauge icon in the taskbar notification area.

AgentGauge has no ordinary main window. Right-click the tray icon to toggle run-on-startup or quit the app.

Unsigned local builds may trigger Windows SmartScreen's unknown-publisher warning.


## Sign-in and data

AgentGauge never issues or refreshes credentials itself. It reads the files managed by each official CLI, read-only.

| Tool | Credential location | Sign-in |
| --- | --- | --- |
| Claude Code | `%USERPROFILE%\.claude\.credentials.json` | `claude /login` |
| Codex | `%CODEX_HOME%\auth.json` or `%USERPROFILE%\.codex\auth.json` | `codex login` |
| Cursor | `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (read-only) | Sign in from the Cursor app |

Cursor has no separate CLI login: once you sign in to the Cursor app, AgentGauge reads its local session token to display usage (the file is opened read-only).

When a sign-in is needed, you can start the relevant CLI login process from the settings screen in the popover. AgentGauge does not write to or delete CLI credential files, and never logs tokens or CLI login output.


## Updates

On startup AgentGauge quietly checks GitHub for the latest release and, if a newer version exists, surfaces it on the **Update** card in settings. You can also check manually with the card's **Check for updates** button.

When an update is available, clicking **Update** downloads the installer and runs it silently: the running AgentGauge exits, the new version is installed in the same location, and the app restarts automatically. No administrator rights are required.


## Current limitations

- AgentGauge relies on the tokens each CLI refreshes, so if you go a long time without using a CLI and its token expires, you may need to sign in again from that CLI.
- The app does not implement its own OAuth/PKCE or token refresh.
- It includes no official code signing.
- Release automation currently targets x64 only.


## Credits

AgentGauge was inspired by [CodexBar](https://github.com/steipete/codexbar), the macOS menu bar app for tracking AI coding tool usage. Several design ideas — including reading the official CLIs' usage endpoints read-only and delegating token refresh back to the CLI — were learned from it.

The UI is set in [Pretendard](https://github.com/orioncactus/pretendard) by Kil Hyung-jin, bundled with the app under the SIL Open Font License 1.1 (see `Assets/Fonts/OFL.txt`).
