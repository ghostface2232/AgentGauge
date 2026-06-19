# AGENTS.md

## Project: Gauge

Gauge is a Windows system-tray app that monitors Claude Code, Codex, and Cursor usage. Clicking the tray icon opens a small popover at the bottom-right screen corner, styled like the Windows 11 Quick Settings panel. It shows each tool's usage windows together per tool (Claude Code and Codex expose a 5-hour session window and a weekly window; Cursor exposes a single billing-cycle window). Claude Code and Codex are registered by default; Cursor can be added from settings. Gemini and Antigravity are intentionally excluded.

## Tech stack

- .NET 10 (LTS), target net10.0-windows
- WinUI 3 via Windows App SDK 2.1.x stable
- Deployment: unpackaged win32, self-contained. No MSIX.
- MVVM: CommunityToolkit.Mvvm
- Data: each tool's official OAuth usage API, called over HTTPS with the token the tool's own CLI stores locally (read-only)
- Single instance only; second launch exits silently
- Distribution: minimal per-user Inno Setup 6 installer wrapping the self-contained x64 payload

## Packaging

- `build-installer.ps1` is the release command. It publishes the self-contained x64 payload to `dist/app/win-x64`, then compiles `installer/Gauge.iss` into `dist/GaugeSetup-win-x64.exe`.
- The installer is per-user (`PrivilegesRequired=lowest`) and installs to `%LOCALAPPDATA%\Programs\Gauge`, so it must not require UAC.
- Keep the wizard minimal: modern style, no welcome, directory, program-group, or ready page. Retain only progress and the finished page with the optional launch checkbox.
- Do not add desktop shortcuts or an install-time start-on-boot choice. Gauge owns that preference through its tray menu. The uninstaller must delete Gauge's HKCU Run value if present.
- `Gauge.csproj` is the source of the installer version. Keep its `<Version>` updated for releases.
- The installer's `[Run]` section has two entries: the interactive finished-page launch checkbox (`postinstall skipifsilent`) and a `Check: WizardSilent` relaunch for silent updates. The in-app updater runs Setup with `/SILENT`, which has no finished page, so the WizardSilent entry is what restarts Gauge after an update.

## Releases and in-app updates

- Releases live on GitHub Releases, tagged `v<Version>` (matching `Gauge.csproj`), with `GaugeSetup-win-x64.exe` as the asset. Pushing a `v*` tag triggers `.github/workflows/release.yml`, which builds the installer and creates a **draft** release (asset + auto-generated notes); writing the final notes and publishing the release are done manually. `release.ps1 -Draft` is the local equivalent. The in-app updater only sees a release once it is published.
- `UpdateService` checks `releases/latest` against the running assembly version, downloads the installer asset, and launches it with `/SILENT`; the app then exits (`UpdateViewModel.ExitRequested` → `App.ShutdownAndExit`) so the installer can replace files and relaunch.
- Update checks are automatic-on-launch (quiet) plus on-demand from the settings footer's single action button ("업데이트 확인" → "업데이트" once a newer release is found). Applying an update is always a deliberate click — never auto-installed.
- The asset name `GaugeSetup-win-x64.exe` is contract: it is hard-coded in `UpdateService` and produced by `installer/Gauge.iss` (`OutputBaseFilename`). Keep them in sync.

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
- CursorProvider: `GET https://cursor.com/api/usage-summary`. Cursor has no OAuth bearer token; instead it authenticates with its web-session cookie, assembled as `Cookie: WorkosCursorSessionToken=<userId>%3A%3A<token>` (the literal separator is `::`, URL-encoded). The token is the JWT Cursor stores in its VS Code-style global state DB at `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (table `ItemTable`, key `cursorAuth/accessToken`), opened **read-only** (shared cache) so a running Cursor is never disturbed. The user id comes from the JWT's `sub` claim (last `|`-segment, e.g. `auth0|user_x` → `user_x`) and `exp` gives expiry; an expired token is a clean re-login state. Cursor bills by credit consumption over a billing cycle rather than rolling 5h/weekly windows, so it produces a single `UsageWindowType.BillingCycle` window: percent precedence mirrors Cursor's dashboard (`individualUsage.plan.totalPercentUsed` → avg(auto, api) → either lane → plan used/limit → overall personal cap → pooled team), with `billingCycleEnd` as the reset and `membershipType` mapped to the plan label. 401/403 surfaces as an authentication-required state; other fetch failures propagate so the last good snapshot is kept; a missing token is a clean "no data" state.
- Plan label: Claude maps credential fields `subscriptionType`+`rateLimitTier` (e.g. `max` + `…max_5x`/`…max_20x` → "Max 5x"/"Max 20x"), so it is available independently of a usage response. Codex maps response `plan_type` (plus/pro/…) and retains it through the coordinator's last-good snapshot on later failures.
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

- Current implementation: H.NotifyIcon.WinUI 2.4.1. It builds successfully with Windows App SDK 2.1.x and is the selected path.
- If a future SDK update breaks H.NotifyIcon.WinUI, remove it and implement the tray icon with Win32 Shell_NotifyIcon via CsWin32, using a hidden message window to receive click events. This fallback has no SDK-version dependency; record the change here if it is taken.
- The icon is redrawable at runtime and swaps among themed variants by the highest usage level (normal / ≥70% / ≥90%), for both light and dark taskbars.
- Left-click toggles the popover. Right-click opens a context menu: start-on-boot toggle, exit.

## Popover window

This is a separate borderless AppWindow, not a WinUI Flyout.

- Presenter: OverlappedPresenter with no title bar or border; not resizable, maximizable, or minimizable; always on top; hidden from Alt-Tab and the taskbar (IsShownInSwitchers false).
- Backdrop: Window.SystemBackdrop set to DesktopAcrylicBackdrop for the frosted Quick Settings look.
- Borderless frame: WinUI's non-resizable OverlappedPresenter restores `WS_DLGFRAME` even after `WS_CAPTION` is cleared. Subclass the HWND with `SetWindowSubclass` and return the full window as client area from `WM_NCCALCSIZE`; otherwise a square non-client seam remains inside the rounded DWM clip. Keep the subclass delegate alive for the window lifetime and remove it when the window closes.
- Rounded corners: set the DWM window corner preference to round. Suppress the separate DWM outline with `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`; DWM still owns the rounded clip and shadow. Do not add an XAML edge-mask Border, because it introduces a second corner geometry.
- Positioning: compute from DisplayArea WorkArea (which excludes the taskbar) and place at the bottom-right corner with a small margin. Must still hold if the taskbar is moved to another edge. Account for display DPI scaling at 100/125/150%.
- Light dismiss: implement manually. Hide the window on the Activated event when the state is Deactivated. Also close on Esc.
- Keyboard focus: Esc is a tree-level `KeyboardAccelerator`. Do not make the full-window root Grid a tab stop or focus it programmatically; doing so draws a square focus visual along the client boundary in dark mode.
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
- Progress bar color steps by usage level (ok / caution / danger). Define colors as theme resources, never hardcoded. The current named thresholds are 70% and 90%, shared with the tray-icon variants.
- Always show the percent number, not color alone, for accessibility.
- Update the tray icon to reflect the highest usage level so state is glanceable without opening the popover.
- Follow the Quick Settings panel's generous spacing and low information density. Exact spacing and typography are left for manual tuning; do not over-fix them.

## Code style

- Nullable reference types enabled.
- async/await throughout; never block on async.
- Isolate all network calls and JSON parsing with exception handling and timeouts.
- No hardcoded colors and no magic numbers for thresholds; use theme resources and named constants.
