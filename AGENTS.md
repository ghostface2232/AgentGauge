# AGENTS.md

## Project: Gauge

Gauge is a Windows system-tray app that monitors Claude Code and Codex usage. Clicking the tray icon opens a small popover at the bottom-right screen corner, styled like the Windows 11 Quick Settings panel. It shows 5-hour session usage and weekly usage, switchable. v1 scope is Claude Code and Codex only; Gemini and Antigravity are intentionally excluded.

## Tech stack

- .NET 10 (LTS), target net10.0-windows
- WinUI 3 via Windows App SDK 2.1.x stable
- Deployment: unpackaged win32, self-contained. No MSIX.
- MVVM: CommunityToolkit.Mvvm
- Data: ccusage CLI invoked as a subprocess, parsed from --json output
- Single instance only; second launch exits silently

## Core architecture rule

Separate data collection from UI. This is the most important constraint in the project.

- Every data source implements IUsageProvider with GetSnapshotAsync(CancellationToken).
- All providers normalize their results into one shared UsageSnapshot model.
- The UI and ViewModel depend only on UsageSnapshot, never on a provider's implementation or on ccusage specifics.
- Rationale: v1 reads usage via ccusage; v1.5 may swap to OAuth usage endpoints. That swap must touch provider implementations only, not the model or the UI.

## UsageSnapshot model

- One snapshot represents one tool at one point in time.
- Fields: tool name, and a list of usage windows.
- Each window has: type (e.g. fiveHour, weekly), used ratio (0-1), reset time, display label.
- Windows are a list because tools differ. A tool may expose a 5-hour window, a weekly window, both, or neither. The UI renders only the windows a tool actually has. Do not hardcode the assumption that every tool has exactly a 5-hour and a weekly window.

## Data layer

- Invoke ccusage through one shared subprocess service that accepts executable, args, and a timeout (default 10s).
- Resolve the globally installed ccusage on PATH (via cmd or absolute-path lookup as needed on Windows).
- ClaudeProvider: ccusage blocks --json for the active 5-hour window, ccusage weekly --json for weekly.
- CodexProvider: ccusage codex daily --json and ccusage codex weekly --json. Codex 5-hour granularity via ccusage may be unreliable; if it cannot be obtained cleanly, omit that window and show weekly only, and leave a code comment noting the limitation.
- Never assume the JSON schema from memory. First print the real output of the installed ccusage version, then write parsing against that actual structure.
- The 5-hour ratio from ccusage is an estimate and may differ from the official figure shown by the tool's own usage command. This is accepted in v1.

## Tray icon

- Try H.NotifyIcon.WinUI first.
- If it conflicts with Windows App SDK 2.1.x or fails to build, remove it and implement the tray icon with Win32 Shell_NotifyIcon via CsWin32, using a hidden message window to receive click events. This path has no SDK-version dependency. Record which path was taken.
- The icon must be redrawable at runtime so its color or badge can reflect the current usage level.
- Left-click toggles the popover. Right-click opens a context menu: refresh, start-on-boot toggle, exit.

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
- ccusage may be missing, or return empty for a tool that has never been used. Treat these as normal flows with a clear in-app message, not as crashes.

## UI rules

- A segmented control (or two toggles) switches between the 5-hour session view and the weekly view.
- One card per tool, stacked vertically: tool name, a progress bar for the selected window, a percent number, and time until reset.
- If a tool lacks the selected window, show a no-data state for that card without breaking.
- Progress bar color steps by usage level (ok / caution / danger). Define colors as theme resources, never hardcoded. Extract threshold boundaries (e.g. 75%, 90%) as named constants.
- Always show the percent number, not color alone, for accessibility.
- Update the tray icon to reflect the highest usage level so state is glanceable without opening the popover.
- Follow the Quick Settings panel's generous spacing and low information density. Exact spacing and typography are left for manual tuning; do not over-fix them.

## Code style

- Nullable reference types enabled.
- async/await throughout; never block on async.
- Isolate all subprocess calls and JSON parsing with exception handling and timeouts.
- No hardcoded colors and no magic numbers for thresholds; use theme resources and named constants.
