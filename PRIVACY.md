# Gauge: Privacy Policy

**Effective date:** 2026-06-19
**Contact:** baemingwan@gmail.com
**Repository:** https://github.com/ghostface2232/gauge

---

## 1. Summary
Gauge is a Windows tray app that displays usage limits for developer tools such as Claude Code, Codex, and Cursor. **Gauge does not collect or store personal information, and does not transmit any data to the developer.** Gauge operates no servers of its own and contains no analytics or tracking. All processing happens locally on the user's PC.

## 2. Information Accessed and Processed
Gauge accesses the following information **read-only on the user's device** solely to display usage. This information is never sent to the developer or any third party.

| Information | Source | Purpose |
| --- | --- | --- |
| OAuth tokens | Local files managed by each CLI (`%USERPROFILE%\.claude\.credentials.json`, `%USERPROFILE%\.codex\auth.json`, Cursor `state.vscdb`) | Authenticate requests to each tool's official usage API |
| Usage data | Responses from each tool's official API | Display limits and usage in the tray popover |

Gauge **never writes or deletes** credential files and does not log or store tokens or login output.

## 3. Network Communication
Gauge communicates only with the following external endpoints. All of them are the user's own first-party services; there is no developer-operated server.

| Endpoint | Purpose | Data sent |
| --- | --- | --- |
| `api.anthropic.com` | Fetch Claude Code usage | User's Anthropic OAuth token |
| `chatgpt.com` | Fetch Codex usage | User's OpenAI OAuth token |
| `cursor.com` | Fetch Cursor usage | User's Cursor session token |
| `api.github.com` | Check for app updates | None (public release info only) |

Tokens sent to each service, and the resulting data handling, are governed by that service's privacy policy (Anthropic, OpenAI, Cursor — links above).

## 4. Data Stored on the Device
The only data Gauge stores on the user's PC is the following, which contains no personally identifying information:

- `%APPDATA%\Gauge\settings.json` — only the **list of registered tools** to display
- Windows registry (`HKCU\...\Run`) — the **auto-start setting**, if enabled by the user

This data can be removed at any time by uninstalling the app or deleting the file/registry entry.

## 5. Third-Party Sharing / Sale
Gauge does not share or sell personal information to third parties. The communication in Section 3 is strictly usage lookups against the user's own first-party services.

## 6. Children's Privacy
Gauge is not directed at children and does not knowingly collect children's personal information.

## 7. Changes to This Policy
Changes will be announced through this document and the repository. The effective date is updated for material changes.

## 8. Contact
Privacy inquiries: **baemingwan@gmail.com**
