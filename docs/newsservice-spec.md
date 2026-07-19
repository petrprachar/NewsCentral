# NewsService — Component Specification

**Type:** .NET 9 Windows Service (`Microsoft.NET.Sdk.Worker`)  
**Target:** `net9.0-windows10.0.19041.0`, `win-x64`  
**Status:** Implemented — full sync cycle operational in Share mode; Azure Blob mode implemented with Certificate / ClientSecret / ClientSecretEnv auth.

## Configuration Resolution

`RegistryConfigurationProvider` (from `NewsCentral.Shared`) is added to `IConfigurationBuilder` in `Program.cs` via `AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService")`. It reads from `HKLM\Software\{Company}\NewsCentral\NewsService\` and merges registry values on top of `appsettings.json`. Any appsettings.json key can be overridden; see `docs/configuration.md` for the full registry layout.

Key registry values for NewsService:

| Registry path | Type | Effect |
|---|---|---|
| `Repository\StorageMode` | `REG_SZ` | `Share` (default) or `Azure` |
| `Service\PollIntervalSeconds` | `REG_SZ` | Overrides `Service:PollIntervalSeconds` (int; MUST be REG_SZ — DWORD 0/1 coerce to `"False"`/`"True"` and the int binder throws) |
| `teams\{teamFolderName}` | `REG_SZ` | Each value name is a team folder name |
| `Signing\{teamFolderName}\PublicKey` | `REG_SZ` | Base64 SPKI for ECDSA `index.json` verification |
| `Signing\{teamFolderName}\PublicKeyPrevious` | `REG_SZ` | Base64 SPKI — rotation window (optional) |

`Company` is the build-time constant `SolutionConstants.Company` (authored in `Directory.Build.props`, generated into `NewsCentral.Shared`) — **not** an appsettings value — and is not registry-overridable (it defines the hive path; a mismatch fails silently as `OpenSubKey` returns `null`). The solution segment is likewise the fixed constant `SolutionConstants.SolutionName`, not a config value.

## Poll Cycle — `SyncService.RunCycleAsync`

Executed by `Worker` on every interval tick:

**Step 1 — Index sync (per team)**
- Reads `{teamFolder}/index.json` from the repository
- **ECDSA verification** — calls `EcdsaSignatureService.Verify(remoteIndex, keys)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`: `Invalid` → logs error and skips the team entirely; `Unsigned` → logs warning and continues; `Valid` / `Disabled` → continues. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Compares `IndexHash` with the cached copy
- If unchanged: skips the team entirely (O(1) check, no I/O)
- If changed: for each `PublishedAssignmentIndex`, checks the locally stored SHA-256 sidecar (`{imagePath}.hash`) against `Content.ImageHash`; downloads only changed or missing images
- Writes the new `index.json` to cache only after all images are safely written

**Step 2 — Lock screen** (lock-screen only — desktop wallpaper is applied by NewsViewer in the user session)
- Filters each team's cached index to *active* assignments: `ScheduleStart ≤ now ≤ ScheduleEnd` and today's day number (1=Mon … 7=Sun) is in `DaysOfWeek`
- Selects the most recently modified active assignment with `IsLogonScreen = true` (the **winner**)
- **Registry-driven, stateless apply** — the live `PersonalizationCSP\LockScreenImagePath` value is the single source of truth; there is no `servicestate.json`. Computes the **intended** path, then applies only when it differs from the current registry value:
  - Winner exists → intended = the winner's cached image path
  - No winner and `Delivery:DefaultLockScreenPath` is configured and the file exists → intended = the default path
  - No winner and no usable default (unset, or configured-but-missing → warning logged) → intended = `null`
  - `current = ILockScreenService.GetCurrentLockScreenPath()`; both sides normalized via `Path.GetFullPath` and compared `OrdinalIgnoreCase`
  - `intended == null` → leave the current lock screen untouched (sticky); `intended == current` → skip; otherwise call `SetLockScreen(intended)` — log Information on success, Error on failure
- A failed write is **not** recorded as applied: the live value still won't match the intended one, so the next cycle re-evaluates and retries naturally. This self-heals the drift class where a failed apply was previously recorded as success. The decision is unit-tested via `SyncService.ApplyIntendedLockScreen` with an `ILockScreenService` test double.

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
| `AzureBlobRepositoryReader` | `Azure` | Certificate, ClientSecret, or ClientSecretEnv auth; reads index.json and images from blob container |

## CacheManager — Hash Sidecar Pattern

When an image is written to cache, `CacheManager.WriteBytesAsync` also writes `{imagePath}.hash` containing `sha256:{hex}`. On the next cycle, `ReadStoredHash` reads this file instead of re-hashing the image, making per-image change detection O(1).

## Lock screen — `LockScreenService`

NewsService applies the **lock screen only**. Desktop wallpaper is owned by NewsViewer (applied per-user via `SystemParametersInfo` + HKCU; see `docs/newsviewer-spec.md`), and no `IDesktopWallpaper`/COM code remains in NewsService.

| Target | API | Session constraint |
|---|---|---|
| Lock screen | `PersonalizationCSP` registry keys (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`) | Works from SYSTEM — no desktop access needed. NewsService runs as LocalSystem. Enterprise/MDM-grade mechanism used by Intune. |

`ILockScreenService` exposes `bool SetLockScreen(string)` (writes the three CSP values; returns `false` on a missing image or a caught write failure) and `string? GetCurrentLockScreenPath()` (reads the live `LockScreenImagePath`, or `null` if absent/unreadable). SyncService owns the Information-level "applied" log; `SetLockScreen` logs its own write at Debug.

A configurable **default lock-screen image** is applied when no lock-screen content is active. It is set via `Delivery:DefaultLockScreenPath` (absolute, SYSTEM-readable path; default `""` = no default). Empty leaves the current lock screen in place (sticky). See Step 2 of the poll cycle for the registry-gated apply.

## Azure Authentication

- Machine certificate from local machine certificate store
- Proactive token refresh; exponential backoff on transient failures; cert-expiry logging
- Handles weeks-long uptime without restart

**ClientSecretEnv mode (NewsService only).** A third `AzureBlob:AuthMode` alongside Certificate and ClientSecret: `AzureCredentialFactory` builds the same `ClientSecretCredential`, but the secret comes from the **machine-scope environment variable `NEWSSERVICE_AZURE_CLIENTSECRET`** (constant `SolutionConstants.NewsServiceAzureClientSecretEnvVar`) instead of `AzureBlob:ClientSecret`. **Fail-closed:** a missing or empty variable throws `InvalidOperationException` (message names the variable) and the callers log at Error — no fallback to the registry secret, no unauthenticated run. Read behaviour: the code reads with `EnvironmentVariableTarget.Machine`, which reads the value **live from the registry**, so a running service picks up a newly set or changed value on its next authentication attempt — a restart is not strictly required. A process-inherited (non-Machine-scope) read, by contrast, would see only the environment captured at service start and would require a restart (the general Windows expectation for env-var changes); restarting after setting the variable therefore remains the recommended, unambiguous practice, robust if the read behaviour ever changes. **Convenience, not security** — a machine env var is clear text readable by any SYSTEM process, exactly like the registry `REG_SZ` secret; Azure Key Vault remains the intended secure path. NewsCentral is excluded by design (interactive per-user app, no LocalSystem context) and its manifest does not offer the mode. Hermetic unit tests inject the env read through the factory's `readEnv` seam (`NewsService.Tests/AzureCredentialFactoryTests.cs`).

**Optional WinHTTP transport (`AzureProxyTransportFactory`).** A singleton, gated by `AzureBlob:UseWinHttpProxy` (default `false`). When on, it builds one shared `WinHttpHandler`-backed transport for Azure.Core (`HttpClientTransport`, used by the credential + blob reader) and one Graph `HttpClient` (`GraphClientFactory.Create(finalHandler: WinHttpHandler{UseWinHttpProxy})`, preserving Graph's retry/redirect/throttling middleware, used by the device + group clients) — so all blob + Graph traffic rides the machine WinHTTP proxy under Local System (see `docs/configuration.md` / `docs/azure-setup.md`). When off, both members are null, `WinHttpHandler` is never instantiated, and every SDK is constructed exactly as before. Only transport construction is affected — credential selection, resolvers, `checkMemberGroups`, and team selection are unchanged.
