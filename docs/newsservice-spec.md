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

`Company` and `ApplicationName` are read from appsettings.json before the registry provider is added and are not registry-overridable.

## Poll Cycle — `SyncService.RunCycleAsync`

Executed by `Worker` on every interval tick:

**Step 1 — Index sync (per team)**
- Reads `{teamFolder}/index.json` from the repository
- **HMAC verification** — calls `HmacService.Verify(remoteIndex)`: `Invalid` → logs error and skips the team entirely; `Unsigned` → logs warning and continues; `Valid` / `Disabled` → continues
- Compares `IndexHash` with the cached copy
- If unchanged: skips the team entirely (O(1) check, no I/O)
- If changed: for each `PublishedAssignmentIndex`, checks the locally stored SHA-256 sidecar (`{imagePath}.hash`) against `Content.ImageHash`; downloads only changed or missing images
- Writes the new `index.json` to cache only after all images are safely written

**Step 2 — Wallpaper / lock screen**
- Filters each team's cached index to *active* assignments: `ScheduleStart ≤ now ≤ ScheduleEnd` and today's day number (1=Mon … 7=Sun) is in `DaysOfWeek`
- Selects the most recently modified active assignment with `IsWallpaper = true` / `IsLogonScreen = true`
- Skips if `PresentationId` matches the last-applied ID in `servicestate.json`

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

## Wallpaper — `WallpaperService`

| Target | API | Session constraint |
|---|---|---|
| Desktop wallpaper | `IDesktopWallpaper` COM (`C2CF3110…`) — `SetWallpaper(null, path)` applies to all monitors | Requires desktop access; logs a warning and skips in session 0. Configure the service to run as the interactive user or trigger via Task Scheduler in the user session. |
| Lock screen | `PersonalizationCSP` registry keys (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`) | Works from SYSTEM — no desktop access needed. Enterprise/MDM-grade mechanism used by Intune. |

## Azure Authentication

- Machine certificate from local machine certificate store
- Proactive token refresh; exponential backoff on transient failures; cert-expiry logging
- Handles weeks-long uptime without restart
