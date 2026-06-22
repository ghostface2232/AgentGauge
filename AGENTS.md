# AGENTS.md

## Branding: external "AgentGauge", internal "Gauge"

The product is presented to users as **AgentGauge** (window/tray/installer display name,
docs, and the GitHub repo `ghostface2232/AgentGauge`). Its **internal identity stays
`Gauge`** and must not change, because those names are what let an existing install keep
working and upgrade in place: the `Gauge.exe` name, the installer `AppId`, the install
folder `…\Programs\Gauge`, the Run-key value `Gauge`, the single-instance key
`Gauge.SingleInstance`, the data folder `%APPDATA%\Gauge`, the release asset
`GaugeSetup-win-x64.exe`, and the C# namespaces. When the doc below says "Gauge" for any
of these identifiers it means the internal name; user-facing copy says "AgentGauge".

## Project: Gauge

Gauge is a Windows system-tray app that monitors Claude Code, Codex, Cursor, Antigravity, and GitHub Copilot usage. Clicking the tray icon opens a small popover at the bottom-right screen corner, styled like the Windows 11 Quick Settings panel. It shows each tool's usage windows together per tool (Claude Code and Codex expose a 5-hour session window and a weekly window; Cursor exposes a single billing-cycle window; Antigravity exposes four — a 5-hour and a weekly window for each of its two model families, Gemini and Claude/GPT; GitHub Copilot exposes its monthly metered quotas — chat, code completions, and premium requests — as billing-cycle windows). Claude Code and Codex are registered by default; Cursor, Antigravity, and GitHub Copilot can be added from settings. Gemini is intentionally excluded as a standalone tool — but Antigravity surfaces its own Gemini and Claude/GPT family quotas.

Each card can render its windows either as horizontal bars (default) or as circular gauges; the mode is an app-wide choice in settings (see UI rules).

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
- App-drop size trimming lives in `Gauge.csproj` MSBuild targets and must be preserved: one strips the Windows AI/ML native payload (onnxruntime/DirectML) the App SDK copies in but a managed tray app never loads — keyed on `NuGetPackageId == 'Microsoft.Windows.AI.MachineLearning'`, so re-check it if a future SDK repackages those natives; another prunes WinUI's per-culture control `.mui` files down to a whitelist (en/ko/ja), relying on the neutral English resources baked into the root DLLs. Both only shrink the drop — they must never remove anything loaded at runtime.
- The installer's `[Run]` section has two entries: the interactive finished-page launch checkbox (`postinstall skipifsilent`) and a `Check: WizardSilent` relaunch for silent updates. The in-app updater runs Setup with `/SILENT`, which has no finished page, so the WizardSilent entry is what restarts Gauge after an update.

## Releases and in-app updates

- Releases live on GitHub Releases, tagged `v<Version>` (matching `Gauge.csproj`), with `GaugeSetup-win-x64.exe` as the asset. Pushing a `v*` tag triggers `.github/workflows/release.yml`, which builds the installer and creates a **draft** release (asset + auto-generated notes); writing the final notes and publishing the release are done manually. `release.ps1 -Draft` is the local equivalent. The in-app updater only sees a release once it is published.
- `UpdateService` checks `releases/latest` against the running assembly version, downloads the installer asset, and launches it with `/VERYSILENT` (no installer UI — the settings footer's ring spinner stands in during the download); the app then exits (`UpdateViewModel.ExitRequested` → `App.ShutdownAndExit`) so the installer can replace files and relaunch. The relaunch passes `--updated`, which `App.OnLaunched` detects to open the window once so the user sees the update landed.
- Update checks are automatic-on-launch (quiet) plus on-demand from the settings footer's single action button (localized "Check for updates" → "Update" once a newer release is found; see Localization). Applying an update is always a deliberate click — never auto-installed.
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
- Each window has: type (e.g. fiveHour, weekly), used ratio (0-1), reset time, display label, an optional provider-stable `Id`, and an optional language-neutral `GroupLabel` (model family).
- Windows are a list because tools differ. A tool may expose a 5-hour window, a weekly window, both, or neither. The UI renders only the windows a tool actually has. Do not hardcode the assumption that every tool has exactly a 5-hour and a weekly window — Antigravity, for instance, exposes four (two families × 5h/weekly).
- `Key` (= `Id` ?? `Type.ToString()`) is the stable identity used to reconcile rows across refreshes, key notification history, and round-trip the cache. It exists because two windows of the same `Type` can coexist (Antigravity's two families each have a 5-hour window), so `Type` alone is no longer unique. `GroupLabel` is the family name used to group and divide those windows in the UI; it is persisted so a stale cache still groups correctly.

Providers read each tool's **real** rate-limit usage from its official OAuth usage API — the same figures the tool's own `/usage` shows — over one shared HttpClient. The OAuth token is read from the file the tool's CLI already maintains; it is read-only (never written back) so we never race the CLI's own token rotation.

- ClaudeProvider: `GET https://api.anthropic.com/api/oauth/usage` with headers `Authorization: Bearer <token>` and `anthropic-beta: oauth-2025-04-20`. Token, plan, and reset tier come from `%USERPROFILE%\.claude\.credentials.json` (`claudeAiOauth.accessToken`, `subscriptionType`, `rateLimitTier`). Response `five_hour`/`seven_day` → `{utilization 0-100, resets_at ISO8601}`.
- CodexProvider: `GET https://chatgpt.com/backend-api/wham/usage` with `Authorization: Bearer <token>` and `ChatGPT-Account-Id`. Token from `%USERPROFILE%\.codex\auth.json` (`tokens.access_token`, `tokens.account_id`). Response `plan_type` plus `rate_limit.primary_window` (5-hour) / `secondary_window` (weekly) → `{used_percent, reset_at epochSeconds}`. This is the same endpoint the Codex CLI itself polls every 60s, so it tolerates frequent reads and Gauge's cycle needs no per-provider throttling (unlike Claude). Fetch failures propagate (not swallowed into an empty success) so the coordinator keeps the last good snapshot; only a missing token is a clean "no data" state.
- CursorProvider: `GET https://cursor.com/api/usage-summary`. Cursor has no OAuth bearer token; instead it authenticates with its web-session cookie, assembled as `Cookie: WorkosCursorSessionToken=<userId>%3A%3A<token>` (the literal separator is `::`, URL-encoded). The token is the JWT Cursor stores in its VS Code-style global state DB at `%APPDATA%\Cursor\User\globalStorage\state.vscdb` (table `ItemTable`, key `cursorAuth/accessToken`), opened **read-only** (shared cache) so a running Cursor is never disturbed. The user id comes from the JWT's `sub` claim (last `|`-segment, e.g. `auth0|user_x` → `user_x`) and `exp` gives expiry; an expired token is a clean re-login state. Cursor bills by credit consumption over a billing cycle rather than rolling 5h/weekly windows, so it produces a single `UsageWindowType.BillingCycle` window: percent precedence mirrors Cursor's dashboard (`individualUsage.plan.totalPercentUsed` → avg(auto, api) → either lane → plan used/limit → overall personal cap → pooled team), with `billingCycleEnd` as the reset and `membershipType` mapped to the plan label. 401/403 surfaces as an authentication-required state; other fetch failures propagate so the last good snapshot is kept; a missing token is a clean "no data" state.
- AntigravityProvider: unlike the others there is **no public usage endpoint and no Gauge-owned credential**. Antigravity (a Codeium/VS Code fork) ships a local Go language server (`language_server.exe`) that answers a loopback Connect API (JSON-over-HTTP at `https://127.0.0.1:<port>/exa.language_server_pb.LanguageServerService/<Method>`, headers `Connect-Protocol-Version: 1` + `X-Codeium-Csrf-Token`). The provider reads quota from it two ways, attach first then delegate:
  - **Attach** (IDE running): discover the IDE's own `language_server.exe` via WMI, confirm it by install-root + a `--csrf_token` arg, find its loopback port with `GetExtendedTcpTable`, and read `RetrieveUserQuotaSummary` (falling back to the older `RetrieveUserQuota`). Read-only; the IDE's server is never touched beyond the HTTP call.
  - **Delegate** (IDE closed): Gauge launches its **own** engine from the install's `resources\bin\language_server.exe`, which self-authenticates from the on-disk login exactly like the Claude/Codex delegated refresh — Gauge never reads or writes those credentials. The engine is created **suspended**, placed under a Win32 **Job Object** with `KILL_ON_JOB_CLOSE`, then resumed, so it and every sidecar it spawns belong to a tree Gauge can tear down completely and nothing Gauge did not start is ever at risk. The engine is **spawned per read and torn down again** (not kept warm): usage only changes while the IDE is in use — which is attach mode — so a resident language server between background refreshes would be cost for no benefit. See `AntigravityEngineHost`, `JobObject`, `SuspendedProcess`.
  - The TLS bypass for the loopback call is gated strictly to `127.0.0.1` (`AntigravityLoopbackTls`) and lives only on the provider's own `HttpClient`; Gauge's general clients are never weakened.
  - Parsing (`AntigravityQuotaParser`) is tolerant: groups/buckets → up to four windows keyed by `bucketId` (→ `Id`), with the `window` field selecting 5h/weekly and the family (`gemini-*` → "Gemini", `3p-*` → "Claude/GPT") becoming `GroupLabel`; `remainingFraction` → `UsedRatio = 1 - remaining` (clamped). The plan label is a best-effort `GetUserStatus` read (`userStatus.planStatus.planInfo.planName`). A bucket without a usable fraction is skipped, never assumed spent.
  - Coordinator contract: **not installed → a clean empty snapshot** (so the card clears); signed-out / still-warming / transport-error / availability-only-with-no-windows → **throw `AntigravityUnavailableException`** so the last good snapshot is kept rather than overwritten with nothing.
- GitHubCopilotProvider: `GET https://api.github.com/copilot_internal/user` with headers `Authorization: token <oauth>`, `Accept: application/json`, and an editor identity (`Editor-Version`/`Editor-Plugin-Version`/`User-Agent`) mirroring the official Copilot client — the same deliberate interop UA stance as Claude's `claude-code`. This is the undocumented endpoint the editor integrations use to show the premium-request quota, so parsing (`quota_snapshots` → up to one window per quota) is tolerant. GitHub Copilot bills **monthly metered quotas** rather than rolling 5h/weekly windows, so each metered quota becomes a `UsageWindowType.BillingCycle` window keyed by its quota id (`chat`/`completions`/`premium_interactions` → `Id`), with `UsedRatio = 1 - percent_remaining/100` (fallback `1 - remaining/entitlement`) and the account-level `quota_reset_date_utc` as the monthly reset (the per-quota `quota_reset_at` is unused). A quota that is `unlimited` or has `has_quota=false` is skipped, never assumed spent. Plan label is best-effort from `access_type_sku` (any `free*` → "Free") then `copilot_plan` (paid `individual` → "Pro", `business`/`enterprise`).
  - **No single fixed credential file.** Unlike the other CLI tools, Copilot's GitHub OAuth token lives in different places per setup, so `GitHubCopilotCredentialSource` tries the sources a Copilot user is likely to have, in order, so one build works across machines: (1) the **gh CLI** via `gh auth token` (`GitHubCliTokenReader`; gh owns the token's refresh, and the standalone Copilot CLI itself authenticates "via gh"); (2) a **github-copilot `apps.json`/`hosts.json`** file (`oauth_token`) under `%LOCALAPPDATA%\github-copilot` or `~/.config/github-copilot`, used by copilot.vim/Neovim, older editor extensions, and third-party tools. The token is read-only and in-memory only; Gauge never writes or refreshes it. A setup that keeps the token only in the editor's encrypted secret store (newest VS Code Copilot Chat) exposes no readable token and resolves to a clean signed-out card — decoding that store is deliberately left out (fragile/unofficial). Login is delegated to `gh auth login` (the catalog's `LoginCommand`). No `DelegatedTokenRefresher` is needed: gh manages its own long-lived token, and a server-side 401 surfaces as an authentication-required card.
- Plan label: Claude maps credential fields `subscriptionType`+`rateLimitTier` (e.g. `max` + `…max_5x`/`…max_20x` → "Max 5x"/"Max 20x"), so it is available independently of a usage response. Codex maps response `plan_type` (plus/pro/…) and retains it through the coordinator's last-good snapshot on later failures.
- Never assume the JSON schema from memory. Inspect a live response from the real endpoint first, then write parsing against that actual structure.
- Why not ccusage: ccusage only counts tokens from local logs — it has no access to actual quotas or reset schedules. Its activity-based blocks, calendar-Monday weeks, and historical-max normalization do not match the real rate-limit windows, so the percentages and resets were wrong. It was removed.
- Gauge never refreshes or rewrites credentials itself. After a reboot a CLI access token may already be expired, so rather than wait for the tool to be launched, the provider triggers a **delegated refresh**: it runs the CLI's own non-interactive command in the background so the *CLI* refreshes and rewrites its token, then re-reads it (see Authentication ownership). Rotation stays owned by the CLI, so this can never break its login. Both Claude and Codex use the shared `DelegatedTokenRefresher` (different command/timeout per tool); other providers keep showing the last good snapshot until their CLI rotates the token on use.
  - Claude (token lives only a few hours → expired on most boots): `claude mcp list` (~5s, 15s timeout). Why not `claude auth status`: it only prints cached auth (~0.3s, no network, file untouched) and so does **not** refresh — the original 0.1.3 bug was using it, so the refresh "succeeded" without refreshing and the success cooldown then blocked retries, leaving usage broken after boot. A command that establishes a full authenticated context runs the bootstrap that actually refreshes, and unlike `claude -p` it spends no model usage (critical for a usage monitor).
  - Codex (token is a ~10-day ChatGPT JWT → rarely expired at boot, only after ~10 days idle): `codex doctor` (~3s, 30s timeout for slow networks/low-spec PCs). The Codex credentials file has no expiry field, so `CliCredentialSource` reads the JWT's own `exp` claim to detect expiry. Verified against the real CLI that `codex login status` and `codex mcp list` do **not** touch the ChatGPT token (both pass `auth=None`); only commands that resolve auth via `AuthManager::auth()` refresh it. `codex doctor` does (its websocket reachability check resolves auth) without spending usage, whereas `codex exec` would cost ~16k input tokens per call.
  - For both, the token refresh happens early in the CLI bootstrap, so even a slow run or a timeout still leaves the freshened token on disk for Gauge to re-read.

### Authentication ownership

- Initial OAuth login is delegated to each official CLI from the Gauge settings window: `claude /login` and `codex login` run as visible processes.
- CLI-owned credentials remain read-only **to Gauge**. Gauge never itself writes, refreshes, deletes, or logs these credentials or CLI login output, and never calls an OAuth token endpoint.
- Refresh is *delegated*, not performed: to recover an expired token Gauge may invoke the CLI's own non-interactive command (`claude mcp list` / `codex doctor`, run hidden by the shared `DelegatedTokenRefresher` via `RunHiddenAsync`) so the CLI refreshes and rewrites its own credential file. Gauge then re-reads it. The CLI keeps sole ownership of the refresh-token rotation; Gauge only nudges and reads. The nudge is cooldown-gated so it never spawns the CLI on every poll, and its output (which carries account info) is drained and never logged. A sticky server-side rejection (the auth card marked "signed out" by a 401) is cleared on the next *genuine live* fetch via the coordinator's `AuthenticationRecovered` event, so a transient rejection on an otherwise-valid token doesn't persist. "Genuine live" = a snapshot with real windows whose `CapturedAt` advanced; this deliberately excludes empty no-credential snapshots and cooldown/429/network-error cache re-serves, which are "successes" but do not prove the server accepted the token (otherwise logging out or a network blip after a 401 would wrongly flip the card back to signed-in).
- Antigravity has **no Gauge-readable credential at all**: there is no token file to read, so the same ownership rule is kept by construction — Gauge reads quota from the IDE's running language server (attach) or from an engine it launches that self-authenticates from the on-disk login (delegate), and never reads, writes, refreshes, or logs Antigravity's credentials or calls its OAuth endpoints. Because there is no credential to inspect, its auth card has no CLI-login button and a neutral "reads usage from the Antigravity app" message (`AntigravityAuthenticationProvider`); it flips to signed-in only on a genuine live read.
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
- Left-click toggles the popover. Right-click opens a context menu: a notifications toggle, a start-on-boot toggle, and exit. Both toggles show their on-state as a right-aligned ✓ (via `KeyboardAcceleratorTextOverride`), not a left check column.
- The two menu toggles are mirrors of the same global settings exposed in the settings panel, so each must stay in sync with the other surface — App applies the change and reflects it back on the opposite surface (see Settings panel). The tray service only owns the menu's visual indicator and raises an event with the new desired state; it never writes the setting itself.

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

## Settings panel

The settings screen is a second view hosted **inside** the same popover window (not a separate window), with an animated slide/fade transition to and from the usage view. It keeps the usage view's measured height while shown, so the two views share one window size and one bottom bar.

- Layout is three rows: a fixed header (back button + title), a scrollable body, and a full-bleed bottom bar (version + update action) identical in height/padding to the usage footer.
- Body order (all inside the scroll): the **global settings** card first, then the per-tool authentication cards, then the "+" add-service button. The scroll turns on only when content exceeds the window height (`VerticalScrollBarVisibility=Auto`).
- Settings keeps a 12dip bottom margin on its scroll's inner panel for breathing room above the bottom bar. (The usage view, by contrast, carries **no** trailing margin — its cards space via the panel's `Spacing` so scrolled content clips flush under the footer; see UI rules. The two views are intentionally not matched here.)
- Global settings card: two app-wide toggles — **notifications** and **run-on-startup** — plus a **view-mode** dropdown (bar vs gauge), grouped in one card directly under the title. The toggles are a label + caption with a compact `ToggleSwitch` (empty On/Off content so it stays language-neutral); the view mode is a `ComboBox` rather than a toggle because it is a named choice, not an on/off state. Like the toggles it only raises an intent event — `App` persists it (`ViewModeSettingsStore`) and pushes it to every card via `UsageViewModel.SetViewMode`.
- Ownership and sync: `GlobalSettingsViewModel` only holds the toggle state and raises an intent event; `App` owns the services, applies the change, and reflects the real result back. The two settings are each surfaced in **two** places (this card and the tray menu), so every apply path updates the *other* surface: a settings toggle updates the tray ✓, a tray toggle updates this card. The view model's reflect-back setters suspend their change events so an external update never loops as a new request. On panel open, the toggles are re-synced from the real state (start-on-boot from the registry, notifications from settings.json) to catch changes made via the tray while it was closed.
- Run-on-startup is the registry Run key via `StartupService` (no settings.json entry); the apply reads back the *actual* registry state so a failed write reverts the toggle instead of lying. Notifications enabled-state persists to settings.json and gates the live notification service (see Notifications).

## Polling and refresh

- A PeriodicTimer drives a 3-minute refresh of all providers. The cycle is deliberately slower than the per-tool work: most providers self-throttle below it (Claude caches for ~5 min) and the Antigravity delegate engine is spawned and torn down per read, so a slower cycle keeps that cold-start churn down. Opening the popover still forces an immediate fresh read, so the user always sees current data when they look.
- On each cycle, call providers in parallel, each call isolated in try-catch.
- Opening the popover triggers one immediate forced refresh, debounced: skip if the last refresh was under 10s ago and show the cached value instead.
- Cache the last successful snapshot. On failure, keep it and display it with a last-updated time.
- The toggle guard and the refresh debounce must not conflict: tray left-click passes the toggle guard first; if it resolves to open, a debounced forced refresh then runs.

## Failure isolation

- A single provider's exception must never block other providers' snapshots or the UI update.
- A failed provider shows an empty state or its last successful value.
- A credential file may be missing, a token expired, or the network unreachable. Treat these as normal flows with a clear in-app message, not as crashes.

## Notifications

Gauge raises a toast when a usage window crosses a threshold or resets. The toast is a **Windows app notification** (`Microsoft.Windows.AppNotifications.AppNotificationManager`, built into the Windows App SDK), shown in the Action Center. Even though Gauge is unpackaged, `AppNotificationManager.Default.Register()` sets up the COM activator and toast identity itself — no AUMID, Start-menu shortcut, or `appxmanifest` plumbing is needed (the App SDK removed the old win32 shortcut requirement). An earlier build used a custom acrylic window; it was dropped once its text grew to roughly the size of a native toast, erasing the size advantage that motivated it.

- Detection (`UsageNotificationEvaluator`) is purely a function of consecutive normalized snapshots — it takes a `UsageState` and `now`, returns the notifications to show, and is fully unit-tested. Keep it free of UI and timing side effects.
  - The first observation of a window establishes a baseline, so launching Gauge already above a threshold never replays an old alert.
  - Thresholds reuse the shared usage levels (caution 70%, danger 90%): the 5-hour window alerts at danger only; weekly and billing-cycle windows alert at caution and danger. A crossing fires once per cycle (a per-threshold mask), and is re-armed by a reset.
  - Reset detection is reset-time advance + a usage drop; a fallback covers providers that omit reset times but requires a strong high-to-low drop. A cached/re-emitted snapshot (same or older `CapturedAt`) is never treated as a transition. A polling gap that spans a reset into an already-high new cycle is marked consumed, not replayed.
  - `FiveHour`, `Weekly`, and Cursor's `BillingCycle` windows are evaluated. Billing-cycle alerts use the same caution/danger thresholds as the tray icon, but reset toasts remain limited to 5-hour/weekly windows. A failed refresh keeps the window's key alive but is not evaluated as a transition. Removing a tool drops its alert history.
- Presentation (`UsageNotificationService`) just builds an `AppNotificationBuilder` (title + message + an app-logo override) per evaluated alert and calls `Show`. Windows owns everything the old custom window hand-rolled: queuing, stacking, history, and Do Not Disturb / full-screen suppression — so there is no display queue, no per-alert timing, and no `SHQueryUserNotificationState` gate here anymore. `Register()`/`Unregister()` bracket the service's lifetime; the no-op `NotificationInvoked` handler is wired before `Register()` (required so a click is handled in-process rather than launching a second instance) because the toasts carry no actions.
- The toast is **text-only** (title + message): it already carries Gauge's app icon in the attribution row (from the exe), and an app-logo override of the gauge image read cropped and clashed with it, so no per-level image is attached. Level/kind is conveyed by the text.
- The global notifications toggle gates `Process` (a no-op while off); re-enabling resets the evaluator baseline so flipping back on never replays thresholds crossed while silenced. The enabled state is read at startup from `NotificationSettingsStore` (default on) and applied to the service; it persists to settings.json.
- Developer visual-QA switch (read from the real process command line, since unpackaged WinUI drops EXE args), running beside the normal tray instance via a separate single-instance key: `--notification-demo` fires one toast of every alert kind. (The old `--notification-longest` clip check is gone — native toasts auto-size, so there is no fixed window to overflow.)

## UI rules

- One card per tool shows all of that tool's windows together. There is **one app-wide view mode** (not per-card): **bar** (default) or **gauge**, chosen from the settings dropdown and applied to every card at once (`UsageViewMode`; each card exposes `IsBarMode`/`IsGaugeMode` and the template shows one list or the other over the same `Windows`).
- Card header: tool name with the plan label beside it in a lighter font (e.g. "Claude Code  Max 5x").
- Bar mode renders each window as a row: a label, a progress bar, a percent number, and time until reset. Gauge mode renders each window as a circular gauge (a 270° ring opening at the bottom, `UsageGauge`) with the percent number and "%" stacked over its center, the window label above, and the reset text below.
- Model-family grouping (Antigravity): windows are grouped by `GroupLabel` (Gemini, Claude/GPT) with 5-hour before weekly inside each family. Bar mode shows the family heading once on the group's first row with a divider between families; gauge mode lays each family out as its own row of gauges (`GaugeGroups`) with the same divider between them, and labels every gauge with its family. Ungrouped tools (Claude/Codex/Cursor) keep provider order with no heading.
- If a tool has no windows at all, show a no-data state for that card without breaking.
- Progress bar color steps by usage level (ok / caution / danger). Define colors as theme resources, never hardcoded. The current named thresholds are 70% and 90%, shared with the tray-icon variants and the notification thresholds.
- Always show the percent number, not color alone, for accessibility.
- Update the tray icon to reflect the highest usage level so state is glanceable without opening the popover.
- Follow the Quick Settings panel's generous spacing and low information density. Exact spacing and typography are left for manual tuning; do not over-fix them.

## Localization

Gauge ships Korean, English, and Japanese. The UI language is fixed once at startup; there is no in-app language switch.

- Translations live **in code** (`Localization/Strings.cs`), a `key → string?[]` table, not `.resw`/`.resx`. This is deliberate: an unpackaged WinUI app's MRT/PRI resource path is fragile, the string count is small, and the language is fixed at startup, so a plain dictionary read needs no satellite-assembly build step. Add a key by adding one row with all three columns.
- The array order is the `AppLanguage` enum's integer value — `{ Korean=0, English=1, Japanese=2 }`. **Do not reorder the enum**; the integers index the table columns. A null cell falls back to English, then to the key, so a missing translation is visible rather than blank.
- `Loc` is the facade: `Loc.Initialize(language)` is called once in `App.OnLaunched` before any window or the tray menu is built. `Loc.Get`/`Loc.Format` look up the key; `Format` uses `Loc.Culture` (derived from the language), not the ambient thread culture, for deterministic output. Korean is the default before `Initialize` runs — unit tests rely on this and assert Korean output.
- XAML resolves strings via the `{loc:Localize Key=...}` markup extension, evaluated at parse time. Because the language is fixed before any XAML loads, resolving once is sufficient — there is nothing to re-localize on a switch.
- `LanguageService` resolves the language: a valid persisted code in settings.json wins; otherwise it maps the OS UI culture (`ko`/`ja` → those, everything else → English) and persists the detected choice on first run. Resolution is pure and unit-tested.
- **Invariant-culture rule (important):** `Loc.Initialize` sets `CurrentCulture`/`CurrentUICulture` process-wide to match the language. So anything parsing or formatting **machine-facing** data — provider API timestamps and numbers, JSON, version strings — must pass `CultureInfo.InvariantCulture` explicitly rather than rely on the ambient culture. A naive `double.Parse`/`DateTime.Parse` will silently break under Korean/Japanese culture.
- settings.json is a single shared file; `AppSettingsFile` does read-modify-write and preserves unknown keys via `[JsonExtensionData]`, so the language, tool registration, notifications-enabled, and view-mode stores never clobber each other.

## Code style

- Nullable reference types enabled.
- async/await throughout; never block on async.
- Isolate all network calls and JSON parsing with exception handling and timeouts.
- No hardcoded colors and no magic numbers for thresholds; use theme resources and named constants.

## Comments

- Comments should explain non-obvious invariants, race/concurrency constraints, data-loss risks, platform/SDK quirks, "why this and not the obvious alternative", protocol/JSON-schema facts, magic-number rationale, and security notes.
- Do not add comments that restate nearby code, narrate ordinary control flow, or preserve temporary implementation history ("previously we…", "comes in a later step").
- Prefer short English comments in complete sentences. Avoid decorative banners and numbered step comments unless they genuinely help navigate a long algorithm or a wide span of code.
- When trimming, do not over-simplify: keep the essential content and references (URLs, file paths, header/key names, API names) so future navigation and understanding stay easy. Trim a redundant clause rather than deleting an otherwise-useful comment, and keep XML `<summary>` blocks that describe a type's purpose or contract.
