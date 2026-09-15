# Test coverage notes

These previously-untestable areas now have small production seams and are covered:

| Area | Seam | Tests |
| --- | --- | --- |
| Update version comparison | `UpdateService` takes an injectable `HttpClient` + current `Version`; `TryParseVersion` is `internal`. | `UpdateServiceTests` |
| Installer execution failure | `IInstallerLauncher` seam (default `ProcessInstallerLauncher`). | `UpdateServiceTests` |
| Multi-monitor positioning | Placement math extracted to the pure `Views\PopoverPlacement` (popover bottom-right + tray menu), consumed by `PopoverWindow.PositionAndResize` and `TrayIconService.RepositionContextMenuAboveTray`. | `PopoverPlacementTests` |
| 429 backoff timing | `ClaudeProvider` takes a `TimeProvider`; the 2→4→8→16→30m escalation lives in the reusable `Services\BackoffPolicy`. | `ClaudeProviderBackoffTests`, `BackoffPolicyTests` |
| Shared retry/cooldown layer | The HTTP providers share `UsageProviderBase` (credential read, delegated 401 retry, cache serving) and `Services\RateLimitGate` (retryable-status set, Retry-After, extend-only cooldown, user-initiated bypass), both on injected `TimeProvider`s. | `RateLimitGateTests`, `CodexProviderBackoffTests` |
| Refresh timing gates | `UsageCoordinator` takes a `TimeProvider` and reads both its gates — the 10s forced-refresh debounce and the per-provider cost floor — off monotonic timestamps. | `UsageCoordinatorTests` |
| Transient credential-read retry | `CliCredentialSource` takes an injectable wait, so the retry that rides out a CLI's token rotation is exercised without spending its duration. | `CredentialSourceTests` |
| Drag-reorder index math | The gesture's index math extracted to the pure `Views\ReorderPlan` (shift layout, snapshot-validity, commit bounds), consumed by `PopoverWindow.ReorderSurface`. | `ReorderPlanTests` |
| Foreground-lock restore | `IForegroundLockTimeout` isolates the system setting. A locked/corrupt settings file, a failed baseline write, or an unreadable timeout prevents zeroing; hard-kill recovery and reuse of an existing persisted baseline are covered. | `ForegroundLockGuardTests` |
| Tool visibility save failures | Locked settings leave visibility and polling unchanged. The settings card rolls back both switch directions, reports one failure, and retries successfully after unlocking. | `ToolVisibilityTests`, `SettingsViewModelTests` |

What else is covered: provider JSON-schema tolerance (Claude/Codex/Cursor/Copilot/
Antigravity), credential parsing and auth expiry, the cold-start half of 429
(propagation + 401/403 → auth), the Claude throttle/cache and account-switch
invalidation (`ProviderCredentialSwitchTests`), coordinator cache merge (cold-start
failure, failure→success, tool purge, debounce, the popover-open cost floor), the
per-window label key that keeps GitHub Copilot's three billing-cycle quotas distinct in
both the rehydrated cache and the toast titles, usage-history recording/pruning
(`UsageHistoryStoreTests`), the ETA projection (`UsageEtaClassifierTests`), notification
evaluation and preferences, tool-registry persistence validation, and the shared
settings.json read-modify-write — that no write ever replaces a document it could not read,
that the explicit recovery replaces only bytes that are not JSON at all (never valid JSON
that merely fails to bind) and keeps a copy a later corruption cannot overwrite, and that
reading alone never touches the file (`AppSettingsFileTests`).

Remaining untested-by-design: the WinUI window/tray handlers themselves (thin shells over
the pure helpers above), toast presentation (`ToastContentBuilder.Show`), and the
delegated CLI refresh's real process execution (covered by stubs at the runner seam).
