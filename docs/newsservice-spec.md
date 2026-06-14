# NewsService — Component Specification

**Type:** .NET 9 Windows Service (`Microsoft.NET.Sdk.Worker`)  
**Target:** `net9.0-windows10.0.19041.0`, `win-x64`  
**Status:** Implemented — full sync cycle operational in Share mode; Azure Blob mode implemented with Certificate / ClientSecret auth.

## Configuration Resolution

`RegistryConfigurationProvider` (from `NewsCentral.Shared`) is added to `IConfigurationBuilder` in `Program.cs` via `AddRegistryOverrides(company, appName)`. It reads from `HKLM\Software\{Company}\{ApplicationName}\` and merges registry values on top of `appsettings.json`. Any appsettings.json key can be overridden; see `docs/configuration.md` for the full registry layout.

Key registry values for NewsService:

| Registry path | Type | Effect |
|---|---|---|
| `Repository\StorageMode` | `REG_SZ` | `Share` (default) or `Azure` |
| `Service\PollIntervalSeconds` | `DWORD` | Overrides `Service:PollIntervalSeconds` |
| `AzureUploadEnabled` | `DWORD` | `1` to enable telemetry upload to Azure Blob |
| `teams\{teamFolderName}` | `REG_SZ` | Each value name is a team folder name |
| `Signing\{teamFolderName}\PublicKey` | `REG_SZ` | Base64 SPKI for ECDSA `index.json` verification |
| `Signing\{teamFolderName}\PublicKeyPrevious` | `REG_SZ` | Base64 SPKI — rotation window (optional) |

`Company` and `ApplicationName` are read from appsettings.json before the registry provider is added and are not registry-overridable.

## Poll Cycle — `SyncService.RunCycleAsync`

Executed by `Worker` on every interval tick:

**Step 1 — Index sync (per team)**
- Reads `{teamFolder}/index.json` from the repository
- **ECDSA verification** — calls `EcdsaSignatureService.Verify(remoteIndex, keys)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`: `Invalid` → logs error and skips the team entirely; `Unsigned` → logs warning and continues; `Valid` / `Disabled` → continues. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Compares `IndexHash` with the cached copy
- If unchanged: skips the team entirely (O(1) check, no I/O)
- If changed: for each `PublishedAssignmentIndex`, checks the locally stored SHA-256 sidecar (`{imagePath}.hash`) against `Content.ImageHash`; downloads only changed or missing images
- Writes the new `index.json` to cache only after all images are safely written

**Step 2 — Lock screen** (lock-screen only — wallpaper ownership moved to NewsViewer in a later phase)
- Filters each team's cached index to *active* assignments: `ScheduleStart ≤ now ≤ ScheduleEnd` and today's day number (1=Mon … 7=Sun) is in `DaysOfWeek`
- Selects the most recently modified active assignment with `IsLogonScreen = true` (the **winner**)
- Apply-on-change decision (`SyncService.DecideLockScreen`, pure + unit-tested), gated on `state.LastLockscreenPresentationId`:
  - Winner exists and its `PresentationId` differs from the last-applied id → apply the winner's cached image; record its id
  - No winner and `Delivery:DefaultLockScreenPath` is configured and the file exists and the last-applied id is not `__DEFAULT__` → apply the default image; record the `__DEFAULT__` sentinel
  - No winner and no default configured → do nothing (last-applied lock screen stays — sticky)
  - No winner, default configured but file missing → warning logged, no change
  - Winner exists and its id already matches state → no-op (CSP keys never re-asserted)
- The `__DEFAULT__` sentinel is overridden the next time content wins, giving clean winner → default → winner transitions

**Step 3 — status.json**
- Writes `LastSyncTime`, `IsOnline`, `SyncSource` (`Share` / `Azure` / `None`) to cache root

**Step 4 — Telemetry upload**
- Deserializes each `uploads\session-*.json` as `SessionTelemetry` and calls `HmacService.Verify`
- Files with `Invalid` signature are logged and deleted without forwarding
- `Valid` and `Unsigned` files are copied to `{SharePath}\uploads\`; the local copy is deleted after a successful copy

## Storage Abstraction

`IRepositoryReader` has two implementations, selected at DI registration time based on effective storage mode:

| Implementation | Mode | Notes |
|---|---|---|
| `LocalShareRepositoryReader` | `Share` | Reads from UNC/local path; `IsAvailable` checks `Directory.Exists` |
| `AzureBlobRepositoryReader` | `Azure` | Certificate or ClientSecret auth; reads index.json and images from blob container |

## CacheManager — Hash Sidecar Pattern

When an image is written to cache, `CacheManager.WriteBytesAsync` also writes `{imagePath}.hash` containing `sha256:{hex}`. On the next cycle, `ReadStoredHash` reads this file instead of re-hashing the image, making per-image change detection O(1).

## Lock screen — `LockScreenService`

NewsService applies the **lock screen only**. Desktop wallpaper is not handled here — wallpaper ownership moves to NewsViewer in a later phase, and no `IDesktopWallpaper`/COM code remains in NewsService.

| Target | API | Session constraint |
|---|---|---|
| Lock screen | `PersonalizationCSP` registry keys (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`) | Works from SYSTEM — no desktop access needed. NewsService runs as LocalSystem. Enterprise/MDM-grade mechanism used by Intune. |

A configurable **default lock-screen image** is applied when no lock-screen content is active. It is set via `Delivery:DefaultLockScreenPath` (absolute, SYSTEM-readable path; default `""` = no default). Empty leaves the last-applied lock screen in place (sticky). See Step 2 of the poll cycle for the apply-on-change decision and the `__DEFAULT__` sentinel.

## Azure Authentication

- Machine certificate from local machine certificate store
- Proactive token refresh; exponential backoff on transient failures; cert-expiry logging
- Handles weeks-long uptime without restart
