# Test coverage notes

This suite is intentionally tests-only — it adds no production seams. The areas below
are **not** unit-tested because their logic is currently unreachable from a test without
a small production change. Each is listed with the one change that would unblock it, so
the follow-up is obvious if we choose to do it later.

| Area | Why it can't be tested today | Unblocking change |
| --- | --- | --- |
| Update version comparison | `UpdateService.TryParseVersion` is `private static`; `CheckAsync` uses an internal, non-injectable `HttpClient` and hits real GitHub. | Inject `HttpClient` (and the current `Version`); expose `TryParseVersion` as `internal` + `[InternalsVisibleTo("Gauge.Tests")]`. |
| Installer execution failure | `DownloadAndLaunchAsync` calls `Process.Start` and the internal `HttpClient` directly. | Add an `IInstallerLauncher` (or `Func<ProcessStartInfo,bool>`) seam and inject `HttpClient`, so download/launch failure → `false` is assertable. |
| Multi-monitor positioning | Placement math is inline in WinUI handlers (`PopoverWindow.CaptureTargetMonitor`/`PositionAndResize`, `TrayIconService.RepositionContextMenuAboveTray`), coupled to `DisplayArea`/`AppWindow`/Win32. | Extract a pure `static` function `(workArea, dpiScale, contentHeight, margins) → rect` and unit-test it. |
| 429 backoff timing | The exponential escalation (2→4→8→16→30m) and cooldown-expiry depend on `Environment.TickCount64`, which is not injectable. | Inject `TimeProvider` into `ClaudeProvider` to replace `Environment.TickCount64`. |

What **is** covered: provider JSON-schema tolerance (Claude/Codex/Cursor), credential
parsing and auth expiry, the cold-start half of 429 (propagation + 401/403 → auth), the
Claude throttle/cache and account-switch invalidation (in `ProviderCredentialSwitchTests`),
coordinator cache merge (cold-start failure, failure→success, tool purge, debounce),
and tool-registry persistence validation.
