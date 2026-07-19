# Configuration Review Page — Specification

**Component:** NewsCentral (MAUI Blazor Hybrid authoring app)
**Status:** Specified — not started.
**Depends on:** `SolutionConstants.SolutionName` and the `Initialization:*` admin-cred fix (both committed as `0fe00a2`). The manifest below reflects current config: `Company` is the build-time constant `SolutionConstants.Company` (authored in `Directory.Build.props`, not an appsettings key).

## 1. Purpose & Scope

A **read-only** diagnostic page giving full transparency over how every configuration value on the local machine resolves across the two live layers — the shipped `appsettings.json` and the registry hive — for all three components (NewsCentral, NewsService, NewsViewer) as installed on that machine.

The page answers, per value: *what is the code default, what does the file say, what does the registry say, where is the registry subkey, and can the registry override this at all?*

**Read-only for v1.** No editing of any kind, including the discovered install directories. "Suitable controls" means type-appropriate **rendering for comprehension** (a toggle communicates a bool better than the string `True`; a dropdown shows an enum's valid set), not editability.

**Two layers only.** The earlier `%PROGRAMDATA%` external-`appsettings.json` middle layer was dropped. Precedence is `appsettings.json` (base) → registry (override).

**Four value inputs, strictly.** Each row shows: value name · code default · `appsettings.json` value · registry value. No "effective/resolved" column — the reader applies precedence themselves, and the *overridable* flag tells them when registry-wins even applies. Omitting the effective column is deliberate: it keeps the page from asserting a live/effective value it cannot guarantee (see §10, disk-vs-live).

## 2. Placement & Gating

- New Blazor page `ConfigurationReview.razor`, route `/configuration-review`.
- **Distinct from the existing `Settings.razor` (`/settings`)**, which is user-profile settings. Do not extend that page; this is a separate SystemAdmin surface. *(Naming decision: if you prefer the label "Settings" in the nav, use a sub-label like "Configuration Review" to avoid collision — flagged, your call.)*
- **SystemAdmin-gated**, mirroring Index Management. Add a NavMenu entry under the Admin section, visible only when `currentUser.IsSystemAdmin`.
- Localizable via `IStringLocalizer<ConfigurationReview>`, consistent with existing pages. Resx population can follow the parked batched-localization approach; not a blocker for the page.

## 3. Page Layout

Three module sections stacked vertically in fixed order: **NewsCentral → NewsService → NewsViewer**. Each section is independent and self-contained:

```
┌─ Module: NewsService ───────────────────────────────────┐
│  [Header block]  install dir · appsettings path · hive  │
│  [Config table]  rows of name·subkey·default·json·reg·ovr│
│  [Structural]    teams · Signing (NewsService/Viewer only)│
└─────────────────────────────────────────────────────────┘
```

All three sections re-resolve on every page load (§8). Nothing is cached or persisted (session-only).

## 4. Module Header Block

Above each config table, a small metadata block describing *where the module lives* — separate from *what its config says*. Install dir is module metadata, not a config value, so it does not appear as a table row.

| Field | Source | Rendering |
|---|---|---|
| Install directory | Discovered (§4.1) | Read-only text + discovery source + exists indicator; `(not discovered)` on failure, never blank |
| `appsettings.json` path | `{InstallDir}\appsettings.json` | Read-only text + exists indicator |
| Registry hive | `HKLM\Software\{Company}\NewsCentral\{Component}` | Full path + **found / not found** badge |

The **found/not-found** badge on the hive is load-bearing: a `Company` mismatch makes `OpenSubKey` return null, and this badge surfaces that as a diagnostic signal rather than a silently empty registry column (§10).

**Install-directory override (session-only).** Auto-discovery (§4.1) is the default, but on a dev box the components are rarely installed where the service registration / `Run` key point, so discovery yields `(not discovered)` and the NewsService/NewsViewer sections come up empty. Each section therefore exposes **one** editable field — the install directory — as an override:

- The field is an **override**, not a replacement: blank = auto-discovery exactly as today; non-empty = the resolver uses the typed directory and **skips discovery**.
- The `appsettings.json` path is **derived** from it as `{override}\appsettings.json` — there is no second editable field.
- Applying is an **explicit per-section action** (an *Apply / Reload* button, or field blur), not a keystroke-reactive watcher. On apply the section **re-resolves** end to end: install dir → derived appsettings path → both layers → rows + structural blocks, and the header badges (exists / hive found-or-not) reflect the new path.
- A non-existent override path falls back to the normal `(not discovered)` / not-found messaging via the exists badges — it never throws.
- **Session-only**: the override lives in page component state; it is **never** persisted and is lost on reload/restart (the developer re-enters it). It is NewsCentral's own in-memory diagnostic pointer — nothing is written to the registry or to any component's `appsettings.json`.

The NewsCentral section already self-discovers via `AppContext.BaseDirectory`; the field is present there for consistency but is not needed.

### 4.1 Install Directory Discovery

| Component | Source | Notes |
|---|---|---|
| NewsCentral | `AppContext.BaseDirectory` | Self — the page runs in this process |
| NewsService | `HKLM\SYSTEM\CurrentControlSet\Services\NewsService\ImagePath` | Command line — parse (§4.2) |
| NewsViewer | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` → `NewsViewer` value | Command line — parse (§4.2) |

### 4.2 Command-Line → Directory Parsing

`ImagePath` and `Run` values are command lines, not bare paths. Parse:

1. If the value starts with `"`, the exe path is the substring between the first and second `"`.
2. Otherwise, take the substring up to and including the last case-insensitive `.exe` occurrence (handles unquoted paths; args follow).
3. `InstallDir = Path.GetDirectoryName(exePath)`.
4. Validate the directory exists; if not, treat discovery as failed → `(not discovered)`.

Pure managed string handling — NativeAOT-safe, no COM.

## 5. Config Row Model

Columns per row:

| Column | Content |
|---|---|
| Value name | `DisplayName` + a small **ⓘ** affordance carrying the `ValueHint` tooltip (§7). The **ⓘ** is a visible title-based (native) tooltip — a styled `ⓘ` span with `title={ValueHint}`, rendered only when `ValueHint` is non-empty. Canonical colon-key shown as secondary/monospace text. |
| Subkey path | Registry subkey relative to the hive, e.g. `AzureBlob\ClientSecret`. For path-defining keys: **N/A — defines the registry path**. |
| Default | Code default from the manifest. Render `""` as `(empty)`. |
| appsettings.json | Live read of the file value; `(not set)` if absent. Redacted if secret & present (§5.1). |
| Registry | Live read of the registry value; `(not set)` if absent. Redacted if secret & present. |
| Overridable | Badge: **Registry-overridable** / **Not overridable** / **Registry-only** (§5.2). |

### 5.1 Secret Redaction

Driven by the manifest `IsSecret` flag (keys under `Hmac`, `*Secret`, `*Password`, `ClientSecret`). Redact the **value** in both the appsettings and registry columns, but **preserve presence and source**: render `••• (set)` vs `(not set)`. Whether a secret is set, and in which layer, is transparency-relevant and safe to show; the value is not. Signing **public** keys are **not** secret and are shown copyable (§6).

### 5.2 Overridable-State (tri-state)

| State | Meaning | Subkey column |
|---|---|---|
| Registry-overridable | appsettings key with a registry mapping | shows the subkey path |
| Not overridable | defines the hive path (`Company`) | **N/A — defines the registry path** |
| Registry-only | no appsettings counterpart; settable only via registry | shows the subkey path |
| Environment-sourced | machine environment variable; not in appsettings, not registry-overridable | **N/A — machine environment variable** |

This disambiguates a blank appsettings column (`Company` blank because it is a build-time constant, `SolutionConstants.Company`, and *can't* be in any appsettings file, vs a registry-only key blank in appsettings because no such JSON key exists).

Path-defining rows (`Company`) render their Default cell as `Contoso (build constant)` — it is a build-time constant from `Directory.Build.props`, not a default anything else could replace. Environment-sourced rows (currently only `NEWSSERVICE_AZURE_CLIENTSECRET`, read machine-scope by `AzureCredentialFactory` when `AzureBlob:AuthMode = ClientSecretEnv`) show **presence only** — `(set)` / `(not set)` in place of the two layer columns; the value is never displayed.

## 6. Structural Hives

Non-scalar registry subtrees, rendered as sub-blocks below the config table. **NewsService and NewsViewer only** — NewsCentral has none (its teams come from the data store, its signing keys from `team-signing.json`).

- **`teams\`** — list of team folder names (surfaced by `RegistryConfigurationProvider` as `teams:0…N`). Registry-only.
- **`Signing\`** — `RequireSignedIndex` renders as a normal scalar row in the config table; the per-team keys render here as a sub-table (`Team` · `PublicKey` · `PublicKeyPrevious`). Public keys are **not secret** — show truncated but copyable, as Key Management already does. Each component maintains its own separate `Signing\` subtree.
- **`Entra\Mappings\`** (NewsService only) — dictionary; sub-table of `Selector` · `Rule`.

## 7. The Config Manifest

A hand-authored, per-module manifest in `NewsCentral.Shared` is the single source of truth for the page. The JSON and registry providers only surface *present* keys; the manifest supplies the full known surface, including absent-but-defaulted keys.

### 7.1 Per-key schema

`CanonicalKey` · `DisplayName` · `RegistrySubkeyPath` (relative; null for path-defining keys) · `RegistryType` (`REG_SZ` / `DWORD` / `n/a`) · `Default` (code default) · `ControlKind` (`text`/`toggle`/`number`/`dropdown`/`path`/`redacted`/`structural`) · `Options` (`string[]` for dropdowns) · `IsSecret` · `OverridableState` (`OV`/`PATH`/`RO`) · `ValueHint`.

`ValueHint` is the **ⓘ** tooltip text, type-driven: enum → allowed set; bool → `true | false` + registry encoding; int → range + unit; path → constraint; formatted string → the format (GUID, 40 hex, `"R G B" 0–255`, Base64 32-byte). It is also where the bool `REG_SZ`-vs-`DWORD` split is made self-documenting.

### 7.2 NewsService manifest

Hive: `…\NewsCentral\NewsService\`. `RegType`: S=REG_SZ, D=DWORD. `Ovr`: OV/PATH/RO.

| Canonical key | Default | Subkey | RegType | Ovr | Control | Secret | ValueHint |
|---|---|---|---|---|---|---|---|
| `Company` | `Contoso` | — | — | PATH | text | | build-time constant `SolutionConstants.Company`; defines hive path; not in appsettings |
| `Service:PollIntervalSeconds` | `60` | `Service\PollIntervalSeconds` | S | OV | number | | integer seconds, 10–86400; MUST be REG_SZ (DWORD 0/1 coerce to `False`/`True` → int binder throws) |
| `Service:CacheRootPath` | `C:\ProgramData\NewsCentral` | `Service\CacheRootPath` | S | OV | path | | absolute path |
| `Repository:StorageMode` | `Share` | `Repository\StorageMode` | S | OV | dropdown | | `Share \| Azure` |
| `Repository:SharePath` | `""` | `Repository\SharePath` | S | OV | path | | UNC or local path; warning: empty REG_SZ is a PRESENT value — overrides appsettings with empty and disables the share repository |
| `AzureBlob:AuthMode` | `Certificate` | `AzureBlob\AuthMode` | S | OV | dropdown | | `Certificate \| ClientSecret \| ClientSecretEnv` |
| `AzureBlob:TenantId` | `""` | `AzureBlob\TenantId` | S | OV | text | | GUID |
| `AzureBlob:ClientId` | `""` | `AzureBlob\ClientId` | S | OV | text | | GUID |
| `AzureBlob:CertificateThumbprint` | `""` | `AzureBlob\CertificateThumbprint` | S | OV | text | | 40 hex chars |
| `AzureBlob:ClientSecret` | `""` | `AzureBlob\ClientSecret` | S | OV | redacted | ✔ | client secret |
| `NEWSSERVICE_AZURE_CLIENTSECRET` | — | — | — | ENV | redacted | ✔ | machine-scope environment variable (`EnvironmentVariableTarget.Machine`) read when `AzureBlob:AuthMode = ClientSecretEnv`; presence only — the value is never displayed |
| `AzureBlob:AccountName` | `""` | `AzureBlob\AccountName` | S | OV | text | | storage account, no suffix |
| `AzureBlob:ContainerName` | `newscentral` | `AzureBlob\ContainerName` | S | OV | text | | container name |
| `AzureBlob:UseWinHttpProxy` | `false` | `AzureBlob\UseWinHttpProxy` | S | OV | toggle | | `true \| false` (registry: REG_SZ) |
| `Hmac:SecretKey` | `""` | `Hmac\SecretKey` | S | OV | redacted | ✔ | Base64, 32 bytes; warning: empty REG_SZ is a PRESENT value — overrides appsettings with empty and disables HMAC |
| `Signing:RequireSignedIndex` | `false` | `Signing\RequireSignedIndex` | S | OV | toggle | | `true \| false` (registry: REG_SZ); ad-hoc read, not on POCO |
| `Entra:Enabled` | `false` | `Entra\Enabled` | S | OV | toggle | | `true \| false` (registry: REG_SZ) |
| `Entra:GracePeriodMinutes` | `240` | `Entra\GracePeriodMinutes` | S | OV | number | | integer minutes; MUST be REG_SZ (0 = "no grace" is valid; DWORD 0/1 coerce to `False`/`True` → int binder throws) |
| `Entra:GroupTeam:InclusionGroup` | `""` | `Entra\GroupTeam\InclusionGroup` | S | OV | text | | Entra group id/name |
| `Entra:GroupTeam:ExclusionGroup` | `""` | `Entra\GroupTeam\ExclusionGroup` | S | OV | text | | Entra group id/name |
| `Delivery:DefaultLockScreenPath` | `""` | `Delivery\DefaultLockScreenPath` | S | OV | path | | absolute; SYSTEM-readable; warning: empty REG_SZ is a PRESENT value — overrides appsettings with empty and disables the default lock screen (sticky) |
| `Telemetry:UploadEnabled` | `true` | `Telemetry\UploadEnabled` | D | OV | toggle | | `true \| false` (registry: DWORD 0/1 — genuine bool, DWORD is safe); false = session telemetry is not forwarded to the repository; the fixed 30-day local retention sweep still runs |
| `Logging:LogLevel:Default` | `Information` | `Logging\LogLevel\Default` | S | OV | dropdown | | `Trace \| Debug \| Information \| Warning \| Error \| Critical \| None` (registry: REG_SZ); standard .NET logging key, honoured by the generic host |
| `Logging:EventLog:LogLevel:Default` | `Information` | `Logging\EventLog\LogLevel\Default` | S | OV | dropdown | | `Trace \| Debug \| Information \| Warning \| Error \| Critical \| None` (registry: REG_SZ); standard .NET logging key, honoured by the generic host |

Structural: `Entra\Mappings\{selector}` (dict); `Signing\{team}\PublicKey` + `PublicKeyPrevious` (RO, **not secret**); `teams\{team}` (RO).

### 7.3 NewsViewer manifest

Hive: `…\NewsCentral\NewsViewer\`.

| Canonical key | Default | Subkey | RegType | Ovr | Control | Secret | ValueHint |
|---|---|---|---|---|---|---|---|
| `Company` | `Contoso` | — | — | PATH | text | | build-time constant `SolutionConstants.Company`; defines hive path; not in appsettings |
| `CacheRootPath` | `C:\ProgramData\NewsCentral` | `CacheRootPath` (hive root) | S | OV | path | | absolute path |
| `BypassDailyGate` | `false` | `BypassDailyGate` | D | OV | toggle | | `true \| false` (registry: DWORD 0/1) |
| `BypassImageIntegrityCheck` | `false` | `BypassImageIntegrityCheck` | D | OV | toggle | | `true \| false` (registry: DWORD 0/1) |
| `Hmac:SecretKey` | `""` | `Hmac\SecretKey` | S | OV | redacted | ✔ | Base64, 32 bytes; warning: empty REG_SZ is a PRESENT value — overrides appsettings with empty and disables HMAC |
| `Signing:RequireSignedIndex` | `false` | `Signing\RequireSignedIndex` | S | OV | toggle | | `true \| false` (registry: REG_SZ); ad-hoc read, not on POCO |
| `Delivery:DefaultWallpaperPath` | `""` | `Delivery\DefaultWallpaperPath` | S | OV | path | | absolute; `""` = sticky; warning: empty REG_SZ is a PRESENT value — overrides appsettings with empty and disables the default wallpaper (sticky) |
| `Delivery:WallpaperStyle` | `Fit` | `Delivery\WallpaperStyle` | S | OV | dropdown | | `Fill \| Fit \| Stretch \| Center \| Tile` |
| `Delivery:WallpaperBackgroundColor` | `0 0 0` | `Delivery\WallpaperBackgroundColor` | S | OV | text | | `"R G B"`, each 0–255 |

Structural: `Signing\{team}\PublicKey` + `PublicKeyPrevious` (RO, **not secret**, own subtree); `teams\{team}` (RO).

### 7.4 NewsCentral manifest

Hive: `…\NewsCentral\NewsCentral\`. **No bindable POCO** — every value is an ad-hoc `IConfiguration` read; entirely hand-tracked. **No structural hives.**

| Canonical key | Default | Subkey | RegType | Ovr | Control | Secret | ValueHint |
|---|---|---|---|---|---|---|---|
| `Company` | `Contoso` | — | — | PATH | text | | build-time constant `SolutionConstants.Company` (single `Directory.Build.props` value, same for all components); defines hive path; not in appsettings |
| `DataPath` | `""` | `DataPath` | S | OV | path | | authoring data root |
| `Initialization:DefaultAdminUsername` | `admin` | `Initialization\DefaultAdminUsername` | S | OV | text | | seed admin username |
| `Initialization:DefaultAdminPassword` | `admin` | `Initialization\DefaultAdminPassword` | S | OV | redacted | ✔ | seed admin password |
| `LockExpirationMinutes` | `15` | `LockExpirationMinutes` | S | OV | number | | integer minutes; MUST be REG_SZ (a 1-minute lock is valid; DWORD 0/1 coerce to `False`/`True` → int binder throws) |
| `Authentication:EnableAutoLogin` | `false` | `Authentication\EnableAutoLogin` | S | OV | toggle | | `true \| false` (registry: REG_SZ) |
| `Authentication:UseMockUPN` | `false` | `Authentication\UseMockUPN` | S | OV | toggle | | `true \| false` (registry: REG_SZ) |
| `Authentication:MockUPN` | `""` | `Authentication\MockUPN` | S | OV | text | | UPN string |
| `Storage:EnableBlobDistribution` | `false` | `Storage\EnableBlobDistribution` | S | OV | toggle | | `true \| false` (registry: REG_SZ) |
| `Storage:DistributionMode` | `Local` | `Storage\DistributionMode` | S | OV | dropdown | | `Local \| AzureBlob` |
| `Storage:LocalDistributionPath` | `""` | `Storage\LocalDistributionPath` | S | OV | path | | absolute; must differ from DataPath |
| `Storage:AzureBlobContainerName` | `newscentral` | `Storage\AzureBlobContainerName` | S | OV | text | | container name |
| `AzureBlob:TenantId` | `""` | `AzureBlob\TenantId` | S | OV | text | | GUID |
| `AzureBlob:ClientId` | `""` | `AzureBlob\ClientId` | S | OV | text | | GUID |
| `AzureBlob:AccountName` | `""` | `AzureBlob\AccountName` | S | OV | text | | storage account, no suffix |
| `Hmac:SecretKey` | `""` | `Hmac\SecretKey` | S | OV | redacted | ✔ | Base64, 32 bytes; warning: empty REG_SZ is a PRESENT value — overrides appsettings with empty and disables HMAC |

## 8. Data Resolution

The page must show the two layers **separately**, not merged — so it does **not** use the normal merged builder. For each component:

1. **Discover** install dir (§4.1) and derive `{InstallDir}\appsettings.json`.
2. **appsettings layer:** `new ConfigurationBuilder().AddJsonFile(appsettingsPath, optional: true).Build()` → query per manifest key. `null` → `(not set)`.
3. **Company for the hive:** `Company` is the compile-time constant `SolutionConstants.Company` (a single `Directory.Build.props` value, identical for all three components) — **not** read from any appsettings layer. This is what each component itself uses to locate its hive; a component built under a different `Company` → hive not-found.
4. **registry layer:** `new ConfigurationBuilder().AddRegistryOverrides(company, SolutionConstants.SolutionName, component).Build()` → query per manifest key. `null` → `(not set)`. This reuses the shipped `RegistryConfigurationSource`, so `teams:*`, `Signing:{team}:*`, and `Entra:Mappings:*` surface automatically for the structural sub-blocks.
5. **Render** each manifest row from (default, appsettings-value, registry-value), applying redaction and overridable-state.

Factor steps 1–4 into a shared `EffectiveConfigResolver` (name retained from prior design though it now returns the two layers *separately*, not an effective merge) in `NewsCentral.Shared`, so the page and any future consumer resolve identically.

## 9. Coverage Test

A drift guard, one-directional (manifest is deliberately a superset — it carries registry-only keys and structural hives with no POCO property):

- For `ServiceConfiguration` and `ViewerConfiguration`: reflect over public settable scalar properties (walking nested section objects) and assert **every** one has a manifest entry (POCO ⊆ manifest).
- **NewsCentral is exempt** — `AppConfiguration` is a get-only `IConfiguration` wrapper (not reflection-bindable), and its `Authentication:*` keys aren't even on it. Fully hand-tracked.
- **Ad-hoc keys** (`Signing:RequireSignedIndex`, `Authentication:*`) are the hand-tracked tail — not reflection-coverable; documented as such.

## 10. Caveats & Known Constraints

- **Disk-vs-live.** The page shows the `appsettings.json` on disk and the registry as it reads *now*. It makes no claim about a running service's snapshot — NewsService binds config at startup (`reloadOnChange: false`), so if config changed after it started, the live service differs until restart. With no effective column, the page never has to label this; include a one-line page note: *"Shows configuration sources on disk/registry now; a running service may hold an earlier snapshot until restart."*
- **Permissions.** `HKLM` + `Program Files` `appsettings.json` are readable by a standard user by default. If the reviewer is non-admin and any hive/file is ACL-hardened, reads fail — render **not readable (insufficient rights)**, not `(not set)`.
- **Company mismatch.** A component deployed under a different `Company` → `OpenSubKey` null → hive **not-found** badge (§4). Never a silent blank registry column.
- **MAUI → Blazor migration.** This page depends on local file + registry access from the native Hybrid process. A migration to remote-hosted Blazor breaks that; the capability would need a local agent/endpoint. Documented lifespan constraint — do not assume it survives the migration unchanged.

## 11. Out of Scope

- Any editing (v1 is read-only throughout, including install dir).
- The dropped `%PROGRAMDATA%` external `appsettings.json` layer.
- `Logging:*` beyond the two NewsService `LogLevel:Default` keys in §7.2 (NewsViewer and NewsCentral have no logging framework — rows there would display controls that do nothing).
- Resolving the NewsViewer `appsettings.Development.json` overlay as a layer — when the file exists, the page shows a notice that an un-displayed overlay is in effect; its values are not rendered.
- `Storage:DefaultStorageType` — phantom key (MauiProgram fallback only); excluded.
- Refreshing stale test-count figures in other docs (separate cleanup).
- De-duplicating the two default-admin seed paths (separate cleanup).

## 12. Implementation Notes

- **NativeAOT-safe:** registry via `Microsoft.Win32.Registry`; command-line parsing pure managed; no COM.
- **Localization:** `IStringLocalizer<ConfigurationReview>`; column headers/labels localizable; resx population may follow the parked batched approach.
- **Windows guards:** registry/`ImagePath` reads under `OperatingSystem.IsWindows()` where the Shared resolver is `net9.0`.
