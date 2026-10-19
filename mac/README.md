# AgentGauge for macOS

A native menu bar app that shows the real usage limits of **Claude** and **Codex** — and, if you switch them on in Settings, **Cursor**, **Antigravity** and **GitHub Copilot** — at a glance: the same figures each tool reports itself.

- Click the gauge in the menu bar to see each tool's 5-hour and weekly windows (and model-scoped limits such as Fable), when they reset, and whether you are ahead of or behind pace — an even pace, workdays only, or (after two weeks of history) your own weekday rhythm.
- Bars or circular gauges. In bar view, a sparkline traces the last six hours of remaining quota (hover it to read a past reading), and a measured burn rate projects when a window will run out.
- The menu bar symbol's needle follows the busiest window; the percentage beside it can be turned off.
- Bars turn orange above 70% and red above 90%. Optional alerts when a limit is nearly used or resets.
- A chip flags a provider's public service incident, and an optional estimate shows what this month's Claude Code and Codex work would cost at API list prices.
- Updates itself from GitHub Releases when you click **Update** (Settings › About, or the panel footer).
- Open at Login, used/remaining display, view mode, and per-tool visibility in Settings (⌘,).
- English, Korean and Japanese, following your macOS language (or the per-app language in System Settings › General › Language & Region).
- Liquid Glass styling on macOS 26 and later; macOS 14 and 15 get the standard look.

## Requirements

- macOS 14 Sonoma or later (Apple silicon or Intel).
- The Claude Code CLI and/or the Codex CLI, signed in. For Cursor, the Cursor app signed in; for GitHub Copilot, the GitHub CLI (`gh`) signed in, or a `github-copilot` token file.

## Sign-in and data

AgentGauge never issues or refreshes credentials itself; it reads what each official CLI already stores, read-only.

| Tool | Where the sign-in is read from | Sign-in |
| --- | --- | --- |
| Claude | Login keychain item `Claude Code-credentials` (or `~/.claude/.credentials.json`) | `claude /login` |
| Codex | `$CODEX_HOME/auth.json` or `~/.codex/auth.json` | `codex login` |
| Cursor | `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb` (opened read-only) | the Cursor app |
| Antigravity | No credential is read: usage comes from the Antigravity app's own local language server — the running one, or (with the app closed) one AgentGauge starts briefly, which signs itself in from Antigravity's saved login | the Antigravity app |
| GitHub Copilot | `gh auth token`, else `~/.config/github-copilot/apps.json` / `hosts.json` | `gh auth login` |

The first time AgentGauge reads Claude's keychain item, macOS asks whether to allow it. Choose **Always Allow** so it is not asked again; AgentGauge only re-reads the item when Claude Code rotates its token. If you choose Deny, use **Try Again** on the card to be asked once more.

When a sign-in is needed, **Sign In…** opens the CLI's own login in Terminal (for Cursor, **Open Cursor** opens the app).

If a token has expired (after a long idle), AgentGauge runs the CLI's own non-interactive command (`claude mcp list`, `codex doctor`) in the background so the CLI refreshes its token, then reads it again.

Other local data, all under `~/Library/Application Support/AgentGauge` and never containing credentials:

- `usage-cache.json` — the last readings, shown at launch.
- `usage-history.db` — 90 days of readings for the sparkline, run-out estimate and automatic pace.
- `api-cost-ledger.json` — only when **Show API-Equivalent Cost** is on: token counts per day and model from Claude Code's `~/.claude/projects` and Codex's `~/.codex/sessions` logs. Nothing is read while the option is off.

Service status comes from each provider's public status page, without credentials. Update checks read `api.github.com`'s latest release; a download must match GitHub's SHA-256 digest and pass `codesign --verify` (an integrity check — release builds are ad-hoc signed, so it does not prove who built them) before it replaces the app.

## Building

```bash
cd mac
swift test
./scripts/build-app.sh
open build/AgentGauge.app
```

Xcode 26 (macOS 26 SDK) is needed for the Liquid Glass styling; older Xcode versions build the fallback look. `ARCHS=native` skips the universal build for faster iteration. Set `SIGN_IDENTITY` to a Developer ID identity to sign with it; ad-hoc builds make macOS ask for keychain access again after every rebuild.

Release builds are ad-hoc signed, so Gatekeeper blocks the first launch of a downloaded copy. Open it once, then go to System Settings › Privacy & Security and choose **Open Anyway** (on macOS 14, right-clicking the app and choosing **Open** also works), or build locally. Updates installed from inside the app are not quarantined and open normally.
