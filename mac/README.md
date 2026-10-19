# AgentGauge for macOS

A native menu bar app that shows the real usage limits of **Claude** and **Codex** at a glance — the same figures each tool's own `/usage` reports.

- Click the gauge in the menu bar to see each tool's 5-hour and weekly windows (and model-scoped limits such as Fable), when they reset, and whether you are ahead of or behind an even pace.
- The menu bar symbol's needle follows the busiest window; the percentage beside it can be turned off.
- Bars turn orange above 70% and red above 90%. Optional alerts when a limit is nearly used or resets.
- Open at Login, used/remaining display, and per-tool visibility in Settings (⌘,).
- English, Korean and Japanese, following your macOS language (or the per-app language in System Settings › General › Language & Region).
- Liquid Glass styling on macOS 26 and later; macOS 14 and 15 get the standard look.

## Requirements

- macOS 14 Sonoma or later (Apple silicon or Intel).
- The Claude Code CLI and/or the Codex CLI, signed in.

## Sign-in and data

AgentGauge never issues or refreshes credentials itself; it reads what each official CLI already stores, read-only.

| Tool | Where the sign-in is read from | Sign-in |
| --- | --- | --- |
| Claude | Login keychain item `Claude Code-credentials` (or `~/.claude/.credentials.json`) | `claude /login` |
| Codex | `$CODEX_HOME/auth.json` or `~/.codex/auth.json` | `codex login` |

The first time AgentGauge reads Claude's keychain item, macOS asks whether to allow it. Choose **Always Allow** so it is not asked again; AgentGauge only re-reads the item when Claude Code rotates its token. If you choose Deny, use **Try Again** on the card to be asked once more.

When a sign-in is needed, **Sign In…** opens the CLI's own login in Terminal.

If a token has expired (after a long idle), AgentGauge runs the CLI's own non-interactive command (`claude mcp list`, `codex doctor`) in the background so the CLI refreshes its token, then reads it again.

## Building

```bash
cd mac
swift test
./scripts/build-app.sh
open build/AgentGauge.app
```

Xcode 26 (macOS 26 SDK) is needed for the Liquid Glass styling; older Xcode versions build the fallback look. `ARCHS=native` skips the universal build for faster iteration. Set `SIGN_IDENTITY` to a Developer ID identity to sign with it; ad-hoc builds make macOS ask for keychain access again after every rebuild.

Unsigned builds downloaded from the internet are blocked by Gatekeeper; right-click the app and choose **Open** the first time, or build locally.
