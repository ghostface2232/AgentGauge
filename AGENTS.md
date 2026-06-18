# AGENTS.md

## Project: Gauge

Gauge is a Windows system-tray app that monitors Claude Code and Codex usage. Clicking the tray icon opens a small popover at the bottom-right screen corner, styled like the Windows 11 Quick Settings panel. It shows 5-hour session usage and weekly usage together per tool. v1 scope is Claude Code and Codex only; Gemini and Antigravity are intentionally excluded.

## Tech stack

- .NET 10 (LTS), target net10.0-windows
- WinUI 3 via Windows App SDK 2.1.x stable
- Deployment: unpackaged win32, self-contained. No MSIX.
- MVVM: CommunityToolkit.Mvvm
- Data: each tool's official OAuth usage API, called over HTTPS with the token the tool's own CLI stores locally (read-only)
- Single instance only; second launch exits silently

## Core architecture rule

Separate data collection from UI. This is the most important constraint in the project.

- Every data source implements IUsageProvider with GetSnapshotAsync(CancellationToken).
- All providers normalize their results into one shared UsageSnapshot model.
- The UI and ViewModel depend only on UsageSnapshot, never on a provider's implementation or on a data-source's specifics.
- Rationale: the data source has already been swapped once (ccusage → OAuth usage APIs) with zero UI changes. Any future swap must touch provider implementations only, not the model or the UI.

## UsageSnapshot model

- One snapshot represents one tool at one point in time.
- Fields: tool name, optional plan label (e.g. "Max 5x", "Plus"), and a list of usage windows.
- Each window has: type (e.g. fiveHour, weekly), used ratio (0-1), reset time, display label.
- Windows are a list because tools differ. A tool may expose a 5-hour window, a weekly window, both, or neither. The UI renders only the windows a tool actually has. Do not hardcode the assumption that every tool has exactly a 5-hour and a weekly window.

Providers read each tool's **real** rate-limit usage from its official OAuth usage API — the same figures the tool's own `/usage` shows — over one shared HttpClient. The OAuth token is read from the file the tool's CLI already maintains; it is read-only (never written back) so we never race the CLI's own token rotation.

- ClaudeProvider: `GET https://api.anthropic.com/api/oauth/usage` with headers `Authorization: Bearer <token>` and `anthropic-beta: oauth-2025-04-20`. Token, plan, and reset tier come from `%USERPROFILE%\.claude\.credentials.json` (`claudeAiOauth.accessToken`, `subscriptionType`, `rateLimitTier`). Response `five_hour`/`seven_day` → `{utilization 0-100, resets_at ISO8601}`.
- CodexProvider: `GET https://chatgpt.com/backend-api/wham/usage` with `Authorization: Bearer <token>` and `ChatGPT-Account-Id`. Token from `%USERPROFILE%\.codex\auth.json` (`tokens.access_token`, `tokens.account_id`). Response `plan_type` plus `rate_limit.primary_window` (5-hour) / `secondary_window` (weekly) → `{used_percent, reset_at epochSeconds}`. This is the same endpoint the Codex CLI itself polls every 60s, so the normal 60s cadence needs no throttling (unlike Claude). Fetch failures propagate (not swallowed into an empty success) so the coordinator keeps the last good snapshot; only a missing token is a clean "no data" state.
- Plan label: Claude maps `subscriptionType`+`rateLimitTier` (e.g. `max` + `…max_5x`/`…max_20x` → "Max 5x"/"Max 20x"); Codex maps `plan_type` (plus/pro/…). The plan is reported even when the usage call fails, since it comes from the credential/response separately.
- Never assume the JSON schema from memory. Inspect a live response from the real endpoint first, then write parsing against that actual structure.
- Why not ccusage: ccusage only counts tokens from local logs — it has no access to actual quotas or reset schedules. Its activity-based blocks, calendar-Monday weeks, and historical-max normalization do not match the real rate-limit windows, so the percentages and resets were wrong. It was removed.
- No token refresh is implemented. On an expired token, the provider keeps showing the last good snapshot; the token stays fresh because the tool's own CLI rotates it on use.

### Authentication ownership

- Initial OAuth login is delegated to each official CLI from the Gauge settings window: `claude /login` and `codex login` run as visible processes.
- CLI-owned credentials remain read-only. Gauge never writes, refreshes, deletes, or logs these credentials or CLI login output.
- Credential lookup is behind `ICredentialSource`; the fixed future priority is `GaugeManaged` then `CliLocal`. Only `CliLocal` is implemented in this version.
- Any future Gauge-owned PKCE flow must use an app-owned secure store and take explicit ownership of token refresh. It must not write to or refresh CLI-owned credential files.

### Claude endpoint rate limiting (important)

`/api/oauth/usage` is throttled hard: ~3 reads in a short window, then 429 with a penalty cooldown and NO Retry-After header, on a bucket shared per account/IP — so over-polling here also starves the real CLI. Naively calling it every 60s would keep the Claude card stuck at "no data". ClaudeProvider therefore:
- sends the `claude-code` User-Agent (bucketed less aggressively than arbitrary agents — the one place we deliberately match a known client's UA, for interop, not deception);
- hits the network at most once per ~5 minutes and serves its cached snapshot in between, so neither the 60s cycle nor a popover-open forced refresh makes a call each time;
- backs off exponentially on 429 (we pick the schedule since there's no Retry-After);
- returns the cached snapshot as a success while throttled/cooling down, so a 429 keeps the last good value on screen instead of clearing the card. A genuine failure (cold start with nothing cached) is the only case that propagates.
- A failed/throttled fetch must propagate as a thrown failure ONLY when there is no cached snapshot — otherwise the coordinator would overwrite the cache with an empty success.

## Tray icon

- Try H.NotifyIcon.WinUI first.
- If it conflicts with Windows App SDK 2.1.x or fails to build, remove it and implement the tray icon with Win32 Shell_NotifyIcon via CsWin32, using a hidden message window to receive click events. This path has no SDK-version dependency. Record which path was taken.
- The icon is redrawable at runtime and swaps among themed variants by the highest usage level (normal / ≥70% / ≥90%), for both light and dark taskbars.
- Left-click toggles the popover. Right-click opens a context menu: start-on-boot toggle, exit.

## Popover window

This is a separate borderless AppWindow, not a WinUI Flyout.

- Presenter: OverlappedPresenter with no title bar or border; not resizable, maximizable, or minimizable; always on top; hidden from Alt-Tab and the taskbar (IsShownInSwitchers false).
- Backdrop: Window.SystemBackdrop set to DesktopAcrylicBackdrop for the frosted Quick Settings look.
- Rounded corners: set the DWM window corner preference to round first. If a larger radius is needed, make the window background transparent and round an inner Border instead. Keep this switchable.
- Positioning: compute from DisplayArea WorkArea (which excludes the taskbar) and place at the bottom-right corner with a small margin. Must still hold if the taskbar is moved to another edge. Account for display DPI scaling at 100/125/150%.
- Light dismiss: implement manually. Hide the window on the Activated event when the state is Deactivated. Also close on Esc.
- Toggle guard: when the popover is focused and the tray icon is clicked again, the click first deactivates and hides the window, then the handler reopens it, causing flicker. Record the last-hidden timestamp and ignore any open request within ~200ms of a hide, treating it as a toggle-close. Tray left-click must pass through this guard.
- Slide-in: on show, translate the root element up from a small offset while fading in, ~150-200ms ease-out. Keep the duration and offset easy to tune.

## Polling and refresh

- A PeriodicTimer drives a 60s refresh of all providers.
- On each cycle, call providers in parallel, each call isolated in try-catch.
- Opening the popover triggers one immediate forced refresh, debounced: skip if the last refresh was under 10s ago and show the cached value instead.
- Cache the last successful snapshot. On failure, keep it and display it with a last-updated time.
- The toggle guard and the refresh debounce must not conflict: tray left-click passes the toggle guard first; if it resolves to open, a debounced forced refresh then runs.

## Failure isolation

- A single provider's exception must never block other providers' snapshots or the UI update.
- A failed provider shows an empty state or its last successful value.
- A credential file may be missing, a token expired, or the network unreachable. Treat these as normal flows with a clear in-app message, not as crashes.

## UI rules

- One card per tool shows all of that tool's windows (5-hour and weekly) together — there is no view switch.
- Card header: tool name with the plan label beside it in a lighter font (e.g. "Claude Code  Max 5x").
- Each window renders a row: a label, a progress bar, a percent number, and time until reset.
- If a tool has no windows at all, show a no-data state for that card without breaking.
- Progress bar color steps by usage level (ok / caution / danger). Define colors as theme resources, never hardcoded. Extract threshold boundaries (e.g. 75%, 90%) as named constants.
- Always show the percent number, not color alone, for accessibility.
- Update the tray icon to reflect the highest usage level so state is glanceable without opening the popover.
- Follow the Quick Settings panel's generous spacing and low information density. Exact spacing and typography are left for manual tuning; do not over-fix them.

## Code style

- Nullable reference types enabled.
- async/await throughout; never block on async.
- Isolate all network calls and JSON parsing with exception handling and timeouts.
- No hardcoded colors and no magic numbers for thresholds; use theme resources and named constants.
