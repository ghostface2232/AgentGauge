# AgentGauge: Privacy Policy

**Effective date:** 2026-11-04
**Contact:** baemingwan@gmail.com
**Repository:** https://github.com/ghostface2232/AgentGauge

---

## 1. Summary
AgentGauge is a Windows tray app and macOS menu bar app that displays usage limits for developer tools: Claude Code, Codex, Antigravity, GitHub Copilot and, on macOS, Cursor. **AgentGauge sends nothing to the developer.** The developer operates no servers for it, and it contains no analytics or tracking.

To work, AgentGauge reads the sign-ins those tools already store on your device and sends each one to that tool's own service. It also keeps a few files on your device, described in Section 4. Most of them hold only settings and usage figures, but one optional file holds file paths that can contain your account and project names.

## 2. Information Accessed and Processed
AgentGauge reads the following **read-only on your device**, solely to display usage. Each credential goes only to the service it belongs to, as listed in Section 3. Credentials are held in memory and are never written to AgentGauge's own files or logs.

| Information | Windows source | macOS source | Purpose |
| --- | --- | --- | --- |
| Claude Code OAuth token, plan and rate-limit tier | `%USERPROFILE%\.claude\.credentials.json` | Login Keychain item `Claude Code-credentials`, or `~/.claude/.credentials.json` (under `CLAUDE_CONFIG_DIR` when set) if no item exists | Authenticate the Claude usage request and label the plan |
| Claude Code profile (`oauthAccount`: organization type and rate-limit tier only) | `%USERPROFILE%\.claude.json` | `~/.claude.json` (under `CLAUDE_CONFIG_DIR` when set) | Show a plan change sooner than the credential does. The file also holds other Claude Code state, such as account details and project history. It is parsed in memory, and nothing else from it is used or kept |
| Codex OAuth token and account ID | `%USERPROFILE%\.codex\auth.json`, or under `CODEX_HOME` | `~/.codex/auth.json`, or under `CODEX_HOME` | Authenticate the Codex usage request. The token's expiry claim is also read, to detect expiry |
| GitHub OAuth token (Copilot) | The `gh` CLI (`gh auth token`), or a `github-copilot` `apps.json`/`hosts.json` file under `%LOCALAPPDATA%` or `~\.config` | The `gh` CLI, or `apps.json`/`hosts.json` under `$XDG_CONFIG_HOME/github-copilot` or `~/.config/github-copilot` | Authenticate the Copilot quota request |
| Cursor session token and user ID | Not supported on Windows | Cursor's `state.vscdb` database, opened read-only (see below) | Authenticate the Cursor usage request |
| Antigravity process information | `language_server` command lines and listening ports (see below) | The same, through macOS process APIs (see below) | Find and talk to Antigravity's local engine |
| Usage data | Responses from each tool's service, or from Antigravity's local engine | Same | Display limits and usage |
| Token counts from local session logs (**only when "Show API-equivalent cost" is on; off by default**) | `%USERPROFILE%\.claude\projects\**\*.jsonl` and `%USERPROFILE%\.config\claude\projects` (or under `CLAUDE_CONFIG_DIR` instead); `%USERPROFILE%\.codex\sessions` and `archived_sessions` (or under `CODEX_HOME`) | The equivalent paths under `~` | Estimate this month's cost at public API list prices |

**Antigravity.** AgentGauge reads no Antigravity credential file, and does not read, write, refresh or log Antigravity's account credentials. While the app is running, AgentGauge looks for Antigravity's language server among running processes. On Windows it uses WMI to read the process ID, executable path and command line of each process named `language_server`, whichever program it belongs to. On macOS it uses `sysctl` to read the arguments of your own processes whose executable name starts with `language_server`. It keeps only a process inside the Antigravity install, discards the others' details without storing or logging them, takes the `--csrf_token` value from that process's command line, finds its loopback port, and sends the token as a header to that port on `127.0.0.1`. The token guards the local connection, and it is held in memory and never logged. When the app is closed, AgentGauge briefly starts Antigravity's own engine with a token it generates itself. That engine signs itself in from your existing on-disk login, and AgentGauge shuts it down right after the reading. On macOS, the engine also gets an environment marker so AgentGauge can recognize its helper processes.

**Session logs.** These logs also contain your conversations. AgentGauge uses only each response's token counts, model name, timestamp and message/response identifiers (to count a response once). It never stores or logs conversation content, prompts, or file paths from the conversation. The files are opened read-only and never modified. Nothing is read while the option is off, and turning it off stops the reading immediately. Prices come from a table bundled with the app, so this feature makes no network request.

**Delegated refresh.** AgentGauge never writes or deletes credential files itself, and it does not log or store tokens or login output. When a CLI's stored token has expired, AgentGauge may briefly run that official CLI in the background (`claude mcp list`, `codex doctor`) so the CLI refreshes its **own** token and rewrites its **own** credentials, exactly as it would on next use. That refresh and its network traffic belong to the CLI, and AgentGauge discards the output unread. Signing in always runs the official CLI where you can see it, or opens the tool's own app.

**Account history.** To keep usage records from different logins separate, the local cache and history use a one-way fingerprint of the CLI's account and workspace identifiers where available, or of the access token when those identifiers are unavailable. The fingerprint stays on the device.

### macOS only
- **Keychain, Claude Code.** AgentGauge reads the `Claude Code-credentials` item's attributes on every refresh, which never prompts. It reads the secret only when the item has changed, by running `/usr/bin/security find-generic-password`, the tool Claude Code itself uses, so normally no prompt appears. If that tool cannot be used, it falls back to the Security framework, and macOS asks for your permission. If you deny access, only a refresh you start yourself asks again.
- **Keychain and database, Cursor.** AgentGauge reads the `cursorAuth/accessToken` value from `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`. Cursor 3.27 and later store that value encrypted. AgentGauge then reads the "Cursor Safe Storage" Keychain password and uses it to decrypt the token in memory. This is a deliberate exception to AgentGauge's rule against opening other apps' encrypted stores, made because Cursor offers no CLI or other readable source. Background refreshes never show the access prompt for this item; only a refresh you start can. The user ID sent to Cursor comes from the token itself.
- **Finding CLIs.** Apps opened from Finder don't inherit your shell's `PATH`, so if a CLI isn't in the usual install folders, AgentGauge runs your login shell once (`$SHELL -ilc`) to read `PATH`. This runs your shell's startup files, as opening a terminal would. Only `PATH` is captured.
- **Signing in.** AgentGauge writes a small `<cli>-login.command` script under `$TMPDIR/AgentGauge` and opens it in Terminal. The script contains only the CLI's path and its login command.

## 3. Network Communication
AgentGauge contacts only the endpoints below. There is no developer-operated server. As with any request, each service can see your IP address and the request's headers.

| Endpoint | Purpose | Data sent |
| --- | --- | --- |
| `api.anthropic.com` | Fetch Claude Code usage and saved-reset deadlines | Your Anthropic OAuth token and a Claude CLI User-Agent |
| `chatgpt.com` | Fetch Codex usage and saved-reset deadlines | Your OpenAI OAuth token and your account ID (`ChatGPT-Account-Id` header) |
| `api.github.com` (`/copilot_internal/user`) | Fetch GitHub Copilot usage (only when Copilot is registered) | Your GitHub OAuth token and an editor-style client identity |
| `cursor.com` (macOS only) | Fetch Cursor usage | Your Cursor user ID and session token, as a cookie |
| `status.anthropic.com` (may redirect to `status.claude.com`), `status.openai.com`, `www.githubstatus.com`, and on macOS `status.cursor.com` | Read public service health every 15 minutes for registered tools | Nothing beyond the request itself: no token or cookie |
| `api.github.com` (`/repos/.../releases/latest`) | Check for app updates at launch and on request | Nothing beyond the request itself: no token |
| `github.com` and its release download hosts | Download a new release (see Section 5) | Nothing beyond the request itself: no token |

Tokens sent to each service, and the resulting data handling, are governed by that service's privacy policy (Anthropic, OpenAI, GitHub, Cursor).

For Antigravity, AgentGauge itself makes **no external network call**: it talks only to the engine on the loopback address (`127.0.0.1`). That engine is part of Antigravity, whether it is the app's own or one AgentGauge briefly starts, and it may contact Antigravity's cloud services (Google's `generativelanguage.googleapis.com` and `daily-cloudcode-pa.googleapis.com`) to fetch quota. That traffic is the engine's own and is governed by Antigravity's privacy policy.

## 4. Data Stored on the Device

### Windows
- `%APPDATA%\Gauge\settings.json`: **app preferences** only. These include the registered and hidden tools, labels or GitHub usernames entered for additional accounts, UI language, notification kinds, card display choices, the tray icon's target, weekly pace model, API-cost and automatic-update switches, and the Windows foreground-lock timeout value AgentGauge restores when it exits.
- `%LOCALAPPDATA%\Gauge\accounts\`: separate CLI profiles for additional Claude and Codex accounts. The official CLIs create and manage their sign-in files here. AgentGauge reads them in the same way it reads the default CLI credentials. The files remain on disk after removing an account card or uninstalling AgentGauge.
- `%APPDATA%\Gauge\settings.corrupt-<date>-<time>.json`: if `settings.json` stops being valid JSON and you open Settings, AgentGauge keeps the unreadable file as this copy and writes a new `settings.json`. The copy holds whatever the old file held and is not deleted automatically.
- `%APPDATA%\Gauge\usage-cache.json`: the **last good usage values** (tool name, plan label, window percentages and counts, scheduled reset times, and saved-reset balances and deadlines), so cards can show a last-known value after a reboot. It holds no tokens or credentials.
- `%APPDATA%\Gauge\usage-history.db`: a local SQLite database of **usage samples** (tool, window, percentage, scheduled reset time, timestamp, and available saved-reset metadata for detecting manual resets), kept for **90 days** and pruned automatically, for trends and forecasts. It holds no tokens.
- `%APPDATA%\Gauge\api-cost-ledger.json`: present only once the API-cost option has been turned on. For each scanned log file it stores the file's **full path**, size, how far it was read, and a hash of its first few kilobytes (to recognize the file if it is moved or rewritten), plus per-day, per-model **token counts**. For each message/response identifier already counted, it stores that identifier with its day, model and token counts. **These paths can contain your Windows account name and project names**: Claude Code names its log folders after each project's path, for example `C:\Users\<account>\.claude\projects\C--Users-<account>-<project>\…`. Everything older than about two months is pruned. The file holds no conversation content or tokens (credentials), and turning the option off leaves it on disk until you delete it.
- `%APPDATA%\Gauge\logs\gauge.log`: a small rotating **diagnostics log** of error types and messages. Tokens, credentials and CLI output are never written to it, but an error message can include a local file path, which may contain your account name. The log never leaves your device.
- `%TEMP%\Gauge\GaugeSetup-<version>.exe`: the **downloaded update installer**. Gauge removes it the next time it starts, and it is safe to delete.
- `%TEMP%\Gauge\install.log`: the **installer's log** from the last update, kept for troubleshooting. It stays on this computer, can contain file paths that include your Windows account name, and is safe to delete.
- Windows registry: the **auto-start setting** (`HKCU\...\Run`), if you enable it, and the entries Windows needs to show AgentGauge's notifications (under `HKCU`).

The uninstaller removes the program and the Run value. The `%APPDATA%\Gauge` folder and `%TEMP%\Gauge` stay until you delete them.

### macOS
- **Preferences** are stored in UserDefaults under `io.github.ghostface2232.agentgauge`. They include labels or GitHub usernames entered for additional accounts. The display language follows macOS.
- `~/Library/Application Support/AgentGauge/accounts/` holds separate profiles managed by the official Claude and Codex CLIs. Claude uses a separate Keychain entry for each profile. Removing an account from AgentGauge leaves its CLI sign-in data in place.
- `~/Library/Application Support/AgentGauge/` holds `usage-cache.json`, `usage-history.db` (90 days) and, once the option has been turned on, `api-cost-ledger.json`. These have the same contents as on Windows, so **the ledger's paths can contain your macOS account name and project names**.
- **Diagnostics** go to the macOS unified log (subsystem `io.github.ghostface2232.agentgauge`), under the same rule: no tokens, credentials or CLI output. macOS manages how long they are kept, and they do not leave your device through AgentGauge.
- `$TMPDIR/AgentGauge/`: the sign-in scripts described in Section 2. A sign-in for an additional account writes a temporary success or failure marker here and the app removes it when the attempt ends.
- **Open at login** is registered with macOS as a login item, if you enable it.
- While updating, the download is unpacked under `$TMPDIR/AgentGauge-update-<id>` and staged as `.AgentGauge-update-<version>.app` next to the installed app. Both are gone once the update finishes.

To remove this data, delete the AgentGauge folder above, then run `defaults delete io.github.ghostface2232.agentgauge`.

## 5. Automatic Updates
By default, each time it starts, AgentGauge asks GitHub whether a newer release exists. If one does, it downloads the release, checks the download against the SHA-256 digest GitHub reports, **installs it without asking**, and restarts. You can turn this off in Settings (General on Windows, About on macOS). AgentGauge then installs an update only when you ask it to, though it still checks at launch.

The digest check catches a corrupted or truncated download. It doesn't prove who built the release, because the digest comes from the same GitHub response as the download link, and release builds are not code-signed by a commercial certificate.

## 6. Third-Party Sharing / Sale
AgentGauge does not share or sell personal information. The communication in Section 3 covers usage, public service health and app updates.

## 7. Children's Privacy
AgentGauge is not directed at children and does not knowingly collect children's personal information.

## 8. Changes to This Policy
Changes will be announced through this document and the repository. The effective date is updated for material changes.

## 9. Contact
Privacy inquiries: **baemingwan@gmail.com**
