# Test coverage notes

The four previously-untestable areas now have small production seams and are covered:

| Area | Seam | Tests |
| --- | --- | --- |
| Update version comparison | `UpdateService` takes an injectable `HttpClient` + current `Version`; `TryParseVersion` is `internal`. | `UpdateServiceTests` |
| Installer execution failure | `IInstallerLauncher` seam (default `ProcessInstallerLauncher`). | `UpdateServiceTests` |
| Multi-monitor positioning | Placement math extracted to the pure `Views\PopoverPlacement` (popover bottom-right + tray menu), consumed by `PopoverWindow.PositionAndResize` and `TrayIconService.RepositionContextMenuAboveTray`. | `PopoverPlacementTests` |
| 429 backoff timing | `ClaudeProvider` takes a `TimeProvider`; the 2→4→8→16→30m escalation lives in the reusable `Services\BackoffPolicy`. | `ClaudeProviderBackoffTests`, `BackoffPolicyTests` |

What else is covered: provider JSON-schema tolerance (Claude/Codex/Cursor/Copilot/
Antigravity), credential parsing and auth expiry, the cold-start half of 429
(propagation + 401/403 → auth), the Claude throttle/cache and account-switch
invalidation (`ProviderCredentialSwitchTests`), coordinator cache merge (cold-start
failure, failure→success, tool purge, debounce), usage-history recording/pruning
(`UsageHistoryStoreTests`), the ETA projection (`UsageEtaClassifierTests`), notification
evaluation and preferences, and tool-registry persistence validation.

Remaining untested-by-design: the WinUI window/tray handlers themselves (thin shells over
the pure helpers above), toast presentation (`ToastContentBuilder.Show`), and the
delegated CLI refresh's real process execution (covered by stubs at the runner seam).
