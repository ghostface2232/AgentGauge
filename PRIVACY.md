# AgentGauge: Privacy Policy

**Effective date:** 2026-08-14
**Contact:** baemingwan@gmail.com
**Repository:** https://github.com/ghostface2232/AgentGauge

---

## 1. Summary
AgentGauge is a Windows tray app that displays usage limits for developer tools such as Claude Code, Codex, Cursor, Antigravity, and GitHub Copilot. **AgentGauge does not collect or store personal information, and does not transmit any data to the developer.** AgentGauge operates no servers of its own and contains no analytics or tracking. All processing happens locally on the user's PC.

## 2. Information Accessed and Processed
AgentGauge accesses the following information **read-only on the user's device** solely to display usage. This information is never sent to the developer or any third party.

| Information | Source | Purpose |
| --- | --- | --- |
| OAuth tokens | Local files managed by each CLI (`%USERPROFILE%\.claude\.credentials.json`, `%USERPROFILE%\.codex\auth.json`, Cursor `state.vscdb`) | Authenticate requests to each tool's official usage API |
| GitHub OAuth token (Copilot) | The `gh` CLI (`gh auth token`), or a `github-copilot` `apps.json`/`hosts.json` file under `%LOCALAPPDATA%` or `~\.config` (written by Copilot editor integrations) | Authenticate the request to GitHub's Copilot quota endpoint |
| Usage data | Responses from each tool's official API, or — for Antigravity — from the app's local engine over a loopback (127.0.0.1) connection | Display limits and usage in the tray popover |

For Antigravity, AgentGauge reads **no** credential file. It obtains usage from Antigravity's own local engine: either the one the running app already hosts, or, when the app is closed, an engine AgentGauge briefly launches that signs itself in from the user's existing on-disk Antigravity login and is shut down again immediately after the reading. AgentGauge does not read, write, refresh, or log Antigravity's credentials.

AgentGauge **never writes or deletes** credential files itself and does not log or store tokens or login output. One nuance: when a CLI's on-disk token has expired, AgentGauge may briefly run that official CLI in the background (`claude`, `codex`) so the CLI refreshes its **own** token and rewrites its **own** credentials file — exactly as it would on next use. That refresh and its network traffic belong to the CLI, and its output is discarded unread by AgentGauge.

## 3. Network Communication
AgentGauge communicates only with the following external endpoints. All of them are the user's own first-party services; there is no developer-operated server.

| Endpoint | Purpose | Data sent |
| --- | --- | --- |
| `api.anthropic.com` | Fetch Claude Code usage | User's Anthropic OAuth token |
| `chatgpt.com` | Fetch Codex usage | User's OpenAI OAuth token |
| `cursor.com` | Fetch Cursor usage | User's Cursor session token |
| `api.github.com` (`/copilot_internal/user`) | Fetch GitHub Copilot usage (only when Copilot is registered) | User's GitHub OAuth token |
| `api.github.com` (`/repos/.../releases/latest`) | Check for app updates | None (public release info only; no token attached) |
| `github.com` / `objects.githubusercontent.com` | Download the release installer, only when the user clicks Update | None (public file; no token attached) |

Tokens sent to each service, and the resulting data handling, are governed by that service's privacy policy (Anthropic, OpenAI, Cursor, GitHub).

For Antigravity, AgentGauge itself makes **no external network call**: it talks only to the Antigravity engine on the local loopback address (127.0.0.1), which never leaves the device. That engine — part of Antigravity, whether the app's own or one AgentGauge briefly launches — may in turn contact Antigravity's first-party cloud to fetch quota, exactly as it does for the Antigravity app; that traffic is the engine's own and is governed by Antigravity's privacy policy, not the developer's.

## 4. Data Stored on the Device
The only data AgentGauge stores on the user's PC is the following, which contains no personally identifying information:

- `%APPDATA%\Gauge\settings.json` — only the user's own **app preferences**: the list of registered tools to display, the UI language, whether usage notifications are on, and the card view mode (bars or gauges)
- `%APPDATA%\Gauge\usage-cache.json` — the **last good usage values** AgentGauge itself computed (tool name, plan label, window percentages and counts, reset times), kept so cards can show a last-known value right after a reboot. No tokens or credentials ever touch this file.
- `%APPDATA%\Gauge\usage-history.db` — a local SQLite database of **usage samples** (tool, window, percentage, timestamp) retained for **90 days** and pruned automatically; used for trend/forecast features. Contains no tokens and no personal information.
- `%APPDATA%\Gauge\logs\gauge.log` — a small rotating **diagnostics log** (error types and messages only). Tokens, credentials, and CLI output are never written to it.
- Windows registry (`HKCU\...\Run`) — the **auto-start setting**, if enabled by the user

This data can be removed at any time by deleting the files/registry entry. The uninstaller removes the Run key; the `%APPDATA%\Gauge` folder can be deleted manually if desired.

## 5. Third-Party Sharing / Sale
AgentGauge does not share or sell personal information to third parties. The communication in Section 3 is strictly usage lookups against the user's own first-party services.

## 6. Children's Privacy
AgentGauge is not directed at children and does not knowingly collect children's personal information.

## 7. Changes to This Policy
Changes will be announced through this document and the repository. The effective date is updated for material changes.

## 8. Contact
Privacy inquiries: **baemingwan@gmail.com**
