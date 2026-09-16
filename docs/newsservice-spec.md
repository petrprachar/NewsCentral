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
| `Logging\LogLevel\Default` | `REG_SZ` | Overrides `Logging:LogLevel:Default` (`Trace` \| `Debug` \| `Information` \| `Warning` \| `Error` \| `Critical` \| `None`) |
| `Logging\EventLog\LogLevel\Default` | `REG_SZ` | Overrides `Logging:EventLog:LogLevel:Default` (same values; EventLog provider only) |

`Company` is the build-time constant `SolutionConstants.Company` (authored in `Directory.Build.props`, generated into `NewsCentral.Shared`) — **not** an appsettings value — and is not registry-overridable (it defines the hive path; a mismatch fails silently as `OpenSubKey` returns `null`). The solution segment is likewise the fixed constant `SolutionConstants.SolutionName`, not a config value.

## Poll Cycle — `SyncService.RunCycleAsync`

Executed by `Worker` on every interval tick:

**Step 1 — Index sync (per team)**
- Reads `{teamFolder}/index.json` from the repository
- **ECDSA verification** — calls `SignatureGate.VerifyWithPrecedence(remoteIndex, keys, isDynamic)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`, then `SignatureGate.ShouldReject`. Rejection → **always logs Error** and skips the team entirely (security outcomes are never demoted or gated on change). Acceptance follows the cycle-wide "log on change, stay quiet otherwise" rule: it logs at its natural level (`Unsigned` → Warning, otherwise Information) only when the index changed **or** the verification result changed since the previous cycle for that team, and at Debug otherwise. The per-team last result is held by the singleton `SyncService`, so every team's verification state is reported at least once per service start; the verification itself still runs before the cached index is read and before any content is trusted — only the logging is positioned after the hash comparison. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Compares `IndexHash` with the cached copy
- If unchanged: skips the team entirely (O(1) check, no I/O)
- If changed: for each `PublishedAssignmentIndex`, checks the locally stored SHA-256 sidecar (`{imagePath}.hash`) against `Content.ImageHash`; downloads only changed or missing images
- Writes the new `index.json` to cache only after all images are safely written

**Step 2 — Lock screen** (lock-screen only — desktop wallpaper is applied by NewsViewer in the user session)

`Delivery:LockScreenEnabled` (bool, default `true`) gates the **entire** step and must be the first thing evaluated: when `false`, nothing below happens — no CSP read, no sweep, no publish, no write, no clear. Logged once per cycle at Debug. **This is a per-surface opt-out, not a revert** — see "Enable toggles" below.

When enabled:
- **Stale-file sweep** — reads the live `PersonalizationCSP\LockScreenImagePath` value and calls `IImagePublisher.SweepExcept` to delete every other previously-published `lockscreen-*` file from `Delivery:PublishedImagePath`. This runs at the **start** of the step, before anything is published this cycle — never in the same cycle a file was just written, since Windows may still hold it open from the apply that just ran. (`SweepExcept(null)` — no file to keep — deletes every published file; this is a legitimate steady state once teardown, below, is in play.)
- Filters each team's cached index to *active* assignments: `ScheduleStart ≤ now ≤ ScheduleEnd` and today's day number (1=Mon … 7=Sun) is in `DaysOfWeek`
- Selects the most recently modified active assignment with `IsLogonScreen = true` (the **winner**)
- **Publish, then apply.** The intended *source* image and its expected hash are computed exactly as before:
  - Winner exists → source = the winner's cached image path, expected hash = `Content.ImageHash` from the signed index
  - No winner and `Delivery:DefaultLockScreenPath` is configured and the file exists → source = the default path; the default carries no index hash, so its own SHA-256 is computed and passed as the expected hash — a self-consistent no-op verification, not a skipped check
  - No winner and no usable default (unset, or configured-but-missing → warning logged) → source = `null`

  A non-null source is then re-verified and copied into the protected `Delivery:PublishedImagePath` folder by `IImagePublisher.Publish` — SHA-256 re-checked against the expected hash, target file named `lockscreen-{hash16}.ext` (content-derived, so the name changes exactly when the bytes change), written via a temp-file-then-move so a torn copy can never be referenced. A publish failure (hash mismatch, missing source, I/O error) logs a Warning and is treated the same as "no source" below — it is **not** a skipped check and it is **not** automatically sticky (see the three-state dispatch).
- **Three-state, registry-driven, stateless apply** — the live `PersonalizationCSP\LockScreenImagePath` value is the single source of truth; there is no `servicestate.json`. `current = ILockScreenService.GetCurrentLockScreenPath()`; comparisons below use fully-normalized absolute paths (`Path.GetFullPath`, `OrdinalIgnoreCase`), never a raw string prefix match. Dispatches on the pair (published path `intended`, live value `current`):

  | `intended` | `current` | Action |
  |---|---|---|
  | non-null | equals `intended` | No-op — steady state |
  | non-null | differs (or absent) | Write the trio (`SetLockScreen`) — log Information on success, Error on failure |
  | `null` | absent | No-op, silent |
  | `null` | present, **inside** `Delivery:PublishedImagePath` (i.e. one **we** published) | **Clear** the trio (`ClearLockScreen`) — log Information |
  | `null` | present, **outside** `Delivery:PublishedImagePath` (foreign — GPO, Intune, a manual admin change) | Left alone — log Debug |

  The "inside `PublishedImagePath`" ownership test is the safety mechanism for the clear branch: a misconfigured or unresolvable `Delivery:PublishedImagePath` (empty, invalid characters, `Path.GetFullPath` throws) makes the test fail **closed** — every live value is then treated as foreign and is **never** cleared.
- A failed write or clear is **not** recorded as applied: the live value still won't match, so the next cycle re-evaluates and retries naturally. This self-heals the drift class where a failed apply was previously recorded as success. Because the published file name is content-derived, the write-branch compare-and-apply logic is unchanged from before this step existed — the path it compares simply points at the protected folder instead of the cache. The decision is unit-tested via `SyncService.ApplyIntendedLockScreen` and the `LockScreenEnabled` gate via `SyncService.ApplyLockScreenAsync`, both with an `ILockScreenService` test double (`LockScreenApplyTests`); the publish step is unit-tested separately via `ImagePublisherTests`.

> **This replaces the earlier sticky-on-null behavior.** Before this milestone, `intended == null` always left the current lock screen untouched, unconditionally — described in more than one place in this document (and in `docs/configuration.md`) as "sticky." That is no longer universally true: a value NewsService itself previously published is now **cleared** when there is nothing left to show, returning the machine to Windows' own default lock screen at the next lock rather than displaying stale content indefinitely. Only a **foreign** value (or a disabled `LockScreenEnabled`) is still left untouched — genuinely sticky, because NewsCentral never manages a value it did not write.

> **Prerequisite — Windows Enterprise.** Personalization CSP (the mechanism behind the lock-screen apply) is documented by Microsoft as supported on Windows Enterprise and Education SKUs, and on Pro only under Shared PC / Cloud Config (BootToCloud) configurations. The raw registry writes this service performs are widely observed to work on Pro outside those configurations too, but that is undocumented behavior — do not rely on it for a production fleet running Pro.

**Enable toggles — `Delivery:LockScreenEnabled` / `Delivery:WallpaperEnabled`.** Both `bool`, default `true`. `LockScreenEnabled = false` disables Step 2 entirely, as above. `WallpaperEnabled` is **reserved** — defined, validated, and provisionable now so a GPO baseline can set it alongside `LockScreenEnabled` ahead of time, but nothing in NewsService reads it in this release; desktop wallpaper is still owned entirely by NewsViewer (`docs/newsviewer-spec.md`).

**Neither toggle is a revert.** Setting `LockScreenEnabled = false` does not clear whatever lock screen is currently applied — it freezes it in place, since the surface is no longer touched at all. Provision the toggle in the GPO baseline **before** the machine's first sync cycle for any machine where the surface should never be managed; flipping it off after content has already been applied requires clearing the value by hand (or flipping it back on so the ordinary teardown path can do it).

**Session-host advisory.** At startup, `Program.cs` logs a `Warning` when this machine looks like a Windows Server with Remote Desktop connections allowed (`fDenyTSConnections == 0`) and either toggle is still at its default `true` — a best-effort, conservative heuristic (never fires on a client Windows workstation) described in full in the code comment on `WarnIfSessionHostSurfaceUnmanaged`. **RDS session hosts, VDI templates, and RemoteApp hosts should set both toggles to `0`**: one machine-wide registry value cannot correctly serve many concurrent sessions, RDS policy can suppress the desktop background outright, and non-persistent VDI rebuilds every boot, making any applied state meaningless past the next reboot.

**Step 3 — status.json**
- Writes `LastSyncTime`, `IsOnline`, `SyncSource` (`Share` / `Azure` / `None`) to cache root

**Step 4 — Telemetry upload & local retention**
- **Upload gate:** `Telemetry:UploadEnabled` (bool, default `true`; registry `Telemetry\UploadEnabled` DWORD). When `false`, nothing is forwarded to the repository (logged once per cycle at Debug) — this expresses a read-only deployment that consumes content without writing anything back. NewsViewer is deliberately unaffected: it always writes session files, and NewsService alone decides what becomes of them (a single point of control) — re-enabling upload immediately flushes whatever is still inside the retention window.
- Deserializes each `uploads\session-*.json` as `SessionTelemetry` and calls `HmacService.Verify`
- Files with `Invalid` signature are logged and deleted without forwarding
- `Valid` and `Unsigned` files are copied to `{SharePath}\uploads\`; the local copy is deleted after a successful copy
- **Unconditional retention sweep** — after the upload stage, in the same enumeration pass, remaining local files older than `TelemetryDefaults.RetentionDays` (**30 days**, fixed) are deleted, judged by the file's `LastWriteTime` (no deserialization and no HMAC check just to age a file — the files are written locally and never moved, so the filesystem timestamp is reliable). The sweep runs on **every** path — upload disabled, `SharePath` unconfigured, unreachable destination folder, failed copies — precisely the cases where files previously accumulated without bound. A failed delete logs Warning and continues; one locked file never aborts the sweep. Logging follows the log-on-change rule: Information with the count and window when files were deleted, Debug otherwise. The rationale is **data hygiene, not disk space**: volume is tiny (roughly one small file per user per logical day), but the records describe what a specific user saw and when, and should not persist indefinitely on a workstation. The window is deliberately a **constant, not a setting**: there is no operational need to tune it at this volume, and a configurable `0` would be dangerously ambiguous between delete-everything and keep-forever.

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

`ILockScreenService` exposes `bool SetLockScreen(string)` (writes the three CSP values; returns `false` on a missing image or a caught write failure), `string? GetCurrentLockScreenPath()` (reads the live `LockScreenImagePath`, or `null` if absent/unreadable), and `void ClearLockScreen()` (deletes the three values if present, tolerating their absence; never removes the `PersonalizationCSP` key itself, since other CSP settings may live there; any failure is a Warning, never a throw). SyncService owns the Information-level "applied"/"cleared" logs; `SetLockScreen` and `ClearLockScreen` log their own writes at Debug. `SetLockScreen` is always called with a path already published under `Delivery:PublishedImagePath` by `ImagePublisher` — CSP never points directly into the `%ProgramData%` cache; see `IImagePublisher` below and `docs/anti-tamper.md`.

A configurable **default lock-screen image** is applied when no lock-screen content is active. It is set via `Delivery:DefaultLockScreenPath` (absolute, SYSTEM-readable path; default `""` = no default). Empty falls through to the same three-state dispatch as "no active content" — a value NewsService previously published is **cleared**, a foreign value is left alone. See Step 2 of the poll cycle for the full dispatch table.

## `IImagePublisher` — protected-folder publish before apply

`ImagePublisher` re-verifies and copies the winning lock-screen image (or the configured default) into `Delivery:PublishedImagePath` (default `C:\Windows\Web\NewsCentral`) before `SetLockScreen` is ever called, closing two gaps that existed when CSP pointed directly at the cache:

1. **No re-verification at apply.** The image's SHA-256 was checked once, at download time, against the signed index — never again when the path was actually handed to the OS. `Publish` re-checks it every time.
2. **A writable cache folder.** `%ProgramData%\NewsCentral\` inherits `C:\ProgramData`'s ACL, under which a standard user can pre-create a team's `images\generated\` folder and, as `CREATOR OWNER`, retain delete-child rights over everything later written into it. The lock screen is a machine-wide, pre-authentication surface, so that exposure matters here specifically.

`Publish(sourcePath, expectedHash, prefix)` hashes `sourcePath`, compares it (accepting both the bare-hex and `sha256:`-prefixed forms, case-insensitively) against `expectedHash`, and on a match copies it into the publish folder as `{prefix}-{hash16}{ext}` — a content-derived name, so an existing target of that name is known-correct and is never re-copied, and the CSP path changes exactly when the image content changes. A mismatch, a missing source, or an I/O error logs and returns `null`; the caller must treat that as "no usable image," never as success. The publish root is created (inheriting its parent's ACL) if absent; no explicit DACL is ever set — inheriting from `C:\Windows` (Users read-only, no writable-down inheritance) is the entire point.

`SweepExcept(keepFileName)` deletes every other previously-published `lockscreen-*` file. `SyncService` calls it at the **start** of the lock-screen step — reading the live CSP value first — so a file is never removed in the same cycle it was published, when Windows may still hold it open.

A startup check (`ImagePublisher.CheckPublishFolderAcl`, called once from `Program.cs` after the host is built) logs a `Warning` if the configured publish folder already exists and grants `Write`/`Modify`/`FullControl` to `Users` or `Authenticated Users` — a security warning, not a functional failure; `Publish` still succeeds against a writable folder, it simply no longer has the ACL protection this design relies on. Separately, an **empty or unresolvable** `Delivery:PublishedImagePath` makes every `Publish` call fail *and* makes the ownership test in Step 2's three-state apply fail closed — the net effect is that nothing is ever cleared, because nothing can be identified as one of ours (genuinely sticky, in that specific misconfiguration). Neither case is fatal to the service. See `docs/anti-tamper.md` for the full threat model and `docs/configuration.md` for `Delivery:PublishedImagePath`.

## Azure Authentication

- Machine certificate from local machine certificate store
- Proactive token refresh; exponential backoff on transient failures; cert-expiry logging
- Handles weeks-long uptime without restart

**ClientSecretEnv mode (NewsService only).** A third `AzureBlob:AuthMode` alongside Certificate and ClientSecret: `AzureCredentialFactory` builds the same `ClientSecretCredential`, but the secret comes from the **machine-scope environment variable `NEWSSERVICE_AZURE_CLIENTSECRET`** (constant `SolutionConstants.NewsServiceAzureClientSecretEnvVar`) instead of `AzureBlob:ClientSecret`. **Fail-closed:** a missing or empty variable throws `InvalidOperationException` (message names the variable) and the callers log at Error — no fallback to the registry secret, no unauthenticated run. Read behaviour: the code reads with `EnvironmentVariableTarget.Machine`, which reads the value **live from the registry**, so a running service picks up a newly set or changed value on its next authentication attempt — a restart is not strictly required. A process-inherited (non-Machine-scope) read, by contrast, would see only the environment captured at service start and would require a restart (the general Windows expectation for env-var changes); restarting after setting the variable therefore remains the recommended, unambiguous practice, robust if the read behaviour ever changes. **Convenience, not security** — a machine env var is clear text readable by any SYSTEM process, exactly like the registry `REG_SZ` secret; Azure Key Vault remains the intended secure path. NewsCentral is excluded by design (interactive per-user app, no LocalSystem context) and its manifest does not offer the mode. Hermetic unit tests inject the env read through the factory's `readEnv` seam (`NewsService.Tests/AzureCredentialFactoryTests.cs`).

**Optional WinHTTP transport (`AzureProxyTransportFactory`).** A singleton, gated by `AzureBlob:UseWinHttpProxy` (default `false`). When on, it builds one shared `WinHttpHandler`-backed transport for Azure.Core (`HttpClientTransport`, used by the credential + blob reader) and one Graph `HttpClient` (`GraphClientFactory.Create(finalHandler: WinHttpHandler{UseWinHttpProxy})`, preserving Graph's retry/redirect/throttling middleware, used by the device + group clients) — so all blob + Graph traffic rides the machine WinHTTP proxy under Local System (see `docs/configuration.md` / `docs/azure-setup.md`). When off, both members are null, `WinHttpHandler` is never instantiated, and every SDK is constructed exactly as before. Only transport construction is affected — credential selection, resolvers, `checkMemberGroups`, and team selection are unchanged.
