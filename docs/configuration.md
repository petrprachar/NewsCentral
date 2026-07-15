# Configuration Model

> For the anti-tamper-relevant subset (signing keys, `RequireSignedIndex`, image-integrity bypass, HMAC key) consolidated into one table with defaults and effects, see `docs/anti-tamper.md`.

## Registry Hive

All registry-configurable values reside under:

```
HKLM\Software\[Company]\[NewsCentral]\
```

`[Company]` is **not configuration** — it is a single **build-time constant** authored once in `Directory.Build.props` (`<Company>`) and surfaced to code as `SolutionConstants.Company` (generated into `NewsCentral.Shared`), identical across all three components. It is **absent from every `appsettings.json`**, has **no runtime default and no fallback**, and an MSBuild target (`Directory.Build.targets`) fails the build if it is empty, so it cannot ship unset. It is **not** registry-overridable: it *defines* the hive path and cannot be read from the path it defines. A `Company` **mismatch fails silently** — `RegistryConfigurationProvider`'s `OpenSubKey` returns `null` with no error, so every registry/GPO override is quietly ignored and the component runs on its shipped defaults. That silent-drift risk is exactly why `Company` is a single source of truth (the packaging MSI reads the same `Directory.Build.props` value for the scheduled-task path `\{Company}\NewsCentral\NewsViewer`). The `[NewsCentral]` solution segment is likewise **not** configuration — it is the fixed constant `SolutionConstants.SolutionName` (in `NewsCentral.Shared`, namespace `NewsCentral.Configuration`).

## Precedence Rule

Registry values override `appsettings.json` values. Override is implemented via `RegistryConfigurationProvider` (in `NewsCentral.Shared`) wired into all components through `IConfigurationBuilder.AddRegistryOverrides(company, "NewsCentral", componentName)`. Any `appsettings.json` key can be overridden by mirroring the JSON section hierarchy as registry subkeys.

## Registry Layout

Each component reads from its own subkey. Values set for one component do not affect another.

**NewsService**
```
HKLM\Software\[Company]\NewsCentral\NewsService\
├── Service\
│       PollIntervalSeconds   REG_SZ    (int poll interval in seconds; MUST be REG_SZ — DWORD 0/1 coerce to "False"/"True" and the int binder throws)
│       CacheRootPath         REG_SZ    (overrides Service:CacheRootPath)
├── Repository\
│       StorageMode   REG_SZ   ("Share" or "Azure")
│       SharePath     REG_SZ   (UNC or local path to the file-share repository)
├── AzureBlob\
│       AuthMode               REG_SZ   ("Certificate" or "ClientSecret")
│       TenantId               REG_SZ
│       ClientId               REG_SZ
│       AccountName            REG_SZ
│       ContainerName          REG_SZ
│       CertificateThumbprint  REG_SZ
│       ClientSecret           REG_SZ
│       UseWinHttpProxy        REG_SZ   "true" or "false"  (default false; route blob+Graph via machine WinHTTP proxy)
├── Hmac\
│       SecretKey   REG_SZ    (Base64-encoded 32-byte key; empty = HMAC disabled)
├── Signing\
│       RequireSignedIndex  REG_SZ    "true" or "false"  (default false; when true, Unsigned/Disabled indexes are rejected)
│       <teamFolderName>\
│           PublicKey          REG_SZ   (Base64 SubjectPublicKeyInfo — ECDSA index verification)
│           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional — rotation window)
│       (one subtree per team; surfaced as Signing:{teamFolderName}:PublicKey via RegistryConfigurationProvider)
├── Entra\
│       Enabled              REG_SZ    "true" or "false"  (default false; gates the whole feature)
│       GracePeriodMinutes   REG_SZ    (int retention window in minutes for the last resolved team while Graph is unreachable; default 240; MUST be REG_SZ — 0 = "no grace" is deliberate, and DWORD 0/1 coerce to "False"/"True" and the int binder throws)
│       Mappings\
│           FAT   REG_SZ   "extensionAttribute2-extensionAttribute5-extensionAttribute4"
│           VDE   REG_SZ   "extensionAttribute3"
│           VDL   REG_SZ   "extensionAttribute6-extensionAttribute2"
│       (selector → rule; surfaced as Entra:Enabled / Entra:GracePeriodMinutes / Entra:Mappings:FAT via the recursive walk)
├── Delivery\
│       DefaultLockScreenPath   REG_SZ   (absolute, SYSTEM-readable path to a default lock-screen image; "" = no default)
└── teams\
        (one REG_SZ value per team; value name = team folder name; data = "")
        e.g.  cz-its   REG_SZ   ""
              de-prod  REG_SZ   ""
```

**NewsViewer**
```
HKLM\Software\[Company]\NewsCentral\NewsViewer\
│   CacheRootPath                REG_SZ    (overrides ViewerConfiguration.CacheRootPath)
│   BypassDailyGate             DWORD     (1 = skip once-per-day gate at startup)
│   BypassImageIntegrityCheck    DWORD     (1 = skip image SHA-256 verification)
├── Display\
│       LogicalDayStartHour  REG_SZ   (int 0..23; default 0 = calendar day — see below; MUST be REG_SZ)
├── Ui\
│       Theme   REG_SZ   ("Dark" | "Light"; default Dark — viewer color theme, resolved once at launch; "light" case-insensitive selects Light, anything else Dark; registry-only, no appsettings key)
├── Hmac\
│       SecretKey   REG_SZ
├── Signing\
│       RequireSignedIndex  REG_SZ    "true" or "false"  (default false; when true, Unsigned/Disabled indexes are rejected)
│       <teamFolderName>\
│           PublicKey          REG_SZ   (Base64 SubjectPublicKeyInfo — ECDSA index verification)
│           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional — rotation window)
│       (one subtree per team; surfaced as Signing:{teamFolderName}:PublicKey via RegistryConfigurationProvider)
├── Delivery\
│       DefaultWallpaperPath      REG_SZ   (absolute path to a default wallpaper; "" = leave current/sticky)
│       WallpaperStyle            REG_SZ   (Fill | Fit | Stretch | Center | Tile; default Fit)
│       WallpaperBackgroundColor  REG_SZ   ("R G B" desktop background for Fit letterbox bars; default "0 0 0")
└── teams\
        (one REG_SZ value per team; value name = full folder name)
```

**NewsCentral** (MAUI authoring app)
```
HKLM\Software\[Company]\NewsCentral\NewsCentral\
│   DataPath               REG_SZ    (root for IStorageService)
│   LockExpirationMinutes  REG_SZ    (int minutes; MUST be REG_SZ — a 1-minute lock is valid, and DWORD 0/1 coerce to "False"/"True" and the int binder throws)
├── Authentication\
│       EnableAutoLogin   DWORD     (dev/test only — not for registry deployment)
│       UseMockUPN        DWORD     (dev/test only — not for registry deployment)
│       MockUPN           REG_SZ    (dev/test only — not for registry deployment)
├── Storage\
│       EnableBlobDistribution   DWORD
│       DistributionMode         REG_SZ   ("Local" or "AzureBlob")
│       LocalDistributionPath    REG_SZ
│       AzureBlobContainerName   REG_SZ
├── AzureBlob\
│       TenantId      REG_SZ
│       ClientId      REG_SZ
│       AccountName   REG_SZ
├── Hmac\
│       SecretKey   REG_SZ
└── teams\
        (one REG_SZ value per team)
```

**NewsTester** (future — stub, no values defined yet)
```
HKLM\Software\[Company]\NewsCentral\NewsTester\
    (reserved; no values written by current tooling)
```

**DWORD mapping:** `0` → `"False"`, `1` → `"True"`, values > 1 → numeric string. The configuration binder selects the correct interpretation from the target POCO property type. **REG_SZ** values are stored as-is. All other registry value types are ignored.

**`teams\` sub-hive:** team folder names are the value *names* (not the value data). Value names must match the generated folder name exactly — the sanitized team name with no `team-` prefix (e.g. `cz-its`, not `CZ_ITS`). The provider exposes them as `teams:0`, `teams:1`, … so `IConfiguration.GetSection("teams").GetChildren()` returns one entry per team. Use `TeamConfigurationReader.GetTeams(configuration)` to read them.

**`Signing\` sub-hive:** contains `RequireSignedIndex` (REG_SZ `"true"`/`"false"`, default `"false"`) plus one subkey per team. Each team subkey holds `PublicKey` and (optionally) `PublicKeyPrevious` as `REG_SZ` values containing Base64-encoded SubjectPublicKeyInfo. `RegistryConfigurationProvider` surfaces all values via its recursive `WalkKey` traversal: `Signing:RequireSignedIndex`, `Signing:{teamFolderName}:PublicKey`, and `Signing:{teamFolderName}:PublicKeyPrevious`. Use `SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)` to read the key list; use `configuration.GetValue<bool>("Signing:RequireSignedIndex")` to read the flag. When `RequireSignedIndex` is `true`, `SignatureGate.ShouldReject` rejects Unsigned and Disabled indexes in addition to Invalid — enabling fail-closed enforcement. NewsService and NewsViewer each maintain their own separate `Signing\` subtrees.

## appsettings.json — NewsCentral

`Company` is deliberately **absent** — it is the build-time constant `SolutionConstants.Company`, not an appsettings key (see the Registry Hive note above).

```json
{
  "DataPath": "C:\\Download\\NewsCentral",
  "Initialization": {
    "DefaultAdminUsername": "admin",
    "DefaultAdminPassword": "admin"
  },
  "LockExpirationMinutes": 15,
  "Authentication": {
    "EnableAutoLogin": false,
    "UseMockUPN": true,
    "MockUPN": "user@company.com"
  },
  "Storage": {
    "EnableBlobDistribution": true,
    "DistributionMode": "Local",
    "LocalDistributionPath": "C:\\Download\\NewsCentralDist",
    "AzureBlobContainerName": "newscentral"
  },
  "AzureBlob": {
    "TenantId": "",
    "ClientId": "",
    "AccountName": ""
  },
  "Hmac": {
    "SecretKey": ""
  }
}
```

NewsCentral authenticates to Azure using an **interactive MSAL user session** (`InteractiveBrowserCredential`) — no service credentials are stored in config. Token is persisted in a named cache (`"NewsCentral"`) so subsequent calls are non-interactive.

## appsettings.json — NewsService

```json
{
  "Service": {
    "PollIntervalSeconds": 60,
    "CacheRootPath": "C:\\ProgramData\\NewsCentral"
  },
  "Repository": {
    "StorageMode": "Share",
    "SharePath": ""
  },
  "AzureBlob": {
    "AuthMode": "Certificate",
    "TenantId": "",
    "ClientId": "",
    "CertificateThumbprint": "",
    "ClientSecret": "",
    "AccountName": "",
    "ContainerName": "newscentral",
    "UseWinHttpProxy": false
  },
  "Hmac": {
    "SecretKey": ""
  },
  "Signing": {
    "RequireSignedIndex": false
  },
  "Entra": {
    "Enabled": false,
    "GracePeriodMinutes": 240,
    "Mappings": {}
  },
  "Delivery": {
    "DefaultLockScreenPath": ""
  }
}
```

`Delivery:DefaultLockScreenPath` — absolute, machine-readable (SYSTEM-readable in the pre-logon context) path to a default lock-screen image applied when no lock-screen content is active. Empty (`""`) means no default: the last-applied lock screen is left in place (sticky). NewsService-only; not shared with other components.

## appsettings.json — NewsViewer

NewsViewer resolves configuration across three layers (registry always wins):

| Layer | File / source | Status |
|---|---|---|
| Base | `appsettings.json` | **Shipped, neutral, committed** — a tracked artifact (never gitignored). Every key explicit; loaded with `optional: false` (a missing file is a hard startup failure). This is the base layer the MSI installs. |
| Dev overlay | `appsettings.Development.json` | **Optional, gitignored, dev-only** — loaded with `optional: true`; its mere presence activates it (no environment variable). **Never published** (`CopyToPublishDirectory=Never`), so nothing in it can reach the fleet. Documented shape: `appsettings.Development.json.example`. |
| Override | Registry (GPO) | **Always wins** — `HKLM\Software\{Company}\NewsCentral\NewsViewer`. |

`appsettings.json` is a committed base artifact, not a hand-maintained local file; GPO sits on top of it. `Company` is **not** present in any layer's file — it is the build-time constant `SolutionConstants.Company` (see the Registry Hive note above).

```json
{
  "CacheRootPath": "C:\\ProgramData\\NewsCentral",
  "BypassDailyGate": false,
  "BypassImageIntegrityCheck": false,
  "Display": {
    "LogicalDayStartHour": 0
  },
  "Hmac": {
    "SecretKey": ""
  },
  "Signing": {
    "RequireSignedIndex": false
  },
  "Delivery": {
    "DefaultWallpaperPath": "",
    "WallpaperStyle": "Fit",
    "WallpaperBackgroundColor": "0 0 0"
  }
}
```

`Display:LogicalDayStartHour` (NewsViewer) — the hour (local time) at which the "logical day" for the once-per-day poster gate rolls over. `int`, default `0` (= calendar day), clamped to `0..23`. Example: `5` makes the day run 05:00 → 04:59 next morning, so a night-shift unlock after midnight is still the same logical day and does not re-trigger the poster. See `docs/newsviewer-spec.md` → The daily gate.

> ⚠️ **`Display:LogicalDayStartHour` MUST be provisioned as `REG_SZ`, never `REG_DWORD`.** `RegistryConfigurationProvider` coerces a `REG_DWORD` of `0` to the string `"False"` and `1` to `"True"` (see the DWORD mapping note above). The configuration binder then tries to convert `"False"`/`"True"` to `int`, throws, and **crashes NewsViewer at startup** — a fleet-wide outage from a one-value mistake. This foot-gun applies to **any** numeric override whose legitimate value range includes `0` or `1` (latent today for `Service:PollIntervalSeconds`). Provision such values as `REG_SZ` (e.g. `LogicalDayStartHour = "5"`).

**Display duration has NO registry or appsettings default.** `DisplayDurationSeconds` is a per-presentation value carried in the content, not configuration. `0` is a **meaningful** value ("unset") resolved in code to `30` by `PresentationDefaults.ResolveDuration` — it is not a missing setting to be filled from config. See `docs/data-model.md` → PresentationDefaults and `docs/newsviewer-spec.md` → Display Duration.

`Delivery` (NewsViewer) — desktop wallpaper applied in the user session via `SystemParametersInfo` + HKCU. `DefaultWallpaperPath` (absolute path; `""` = leave the current wallpaper, sticky) is applied when no active `IsWallpaper` content is present. `WallpaperStyle` is `Fill | Fit | Stretch | Center | Tile` (default `Fit`). `WallpaperBackgroundColor` is `"R G B"` for the Fit letterbox bars (default `"0 0 0"`). NewsViewer-only; the NewsService `Delivery` section is separate (`DefaultLockScreenPath`).

## Azure Authentication Modes — NewsService only

NewsService runs as an unattended Windows Service with no interactive user. It authenticates to Azure Blob using one of two modes selected by `AzureBlob:AuthMode`:

| Mode | Credential type | Required keys |
|---|---|---|
| `Certificate` | `ClientCertificateCredential(tenantId, clientId, cert)` | `TenantId`, `ClientId`, `CertificateThumbprint` |
| `ClientSecret` | `ClientSecretCredential(tenantId, clientId, secret)` | `TenantId`, `ClientId`, `ClientSecret` |

**Certificate mode** — the certificate is loaded from `Cert:\LocalMachine\My` by thumbprint (`X509Store(StoreName.My, StoreLocation.LocalMachine)`). Local System has access to `LocalMachine\My` by default; no additional key permission grants are required when the service runs as Local System. Preferred for production.

**ClientSecret mode** — uses a plain client secret string. Simpler to configure for development and testing.

NewsCentral does **not** use these modes. It authenticates via an interactive MSAL user session.

**`AzureBlob:UseWinHttpProxy`** (bool, default `false`) — when `true`, NewsService routes **all** its cloud SDK traffic (Azure Blob **and** Microsoft Graph) through `WinHttpHandler` with `UseWinHttpProxy`, i.e. the **machine WinHTTP proxy** (`netsh winhttp` / WPAD) that the Intune client and Windows Update use. Default `false` keeps today's behavior — the SDKs use the default .NET HTTP stack, which resolves its proxy via **WinINet** (per-user, unreliable under Local System with no user profile loaded). Enable this when NewsService cannot reach Azure/Graph under SYSTEM but the machine otherwise has working cloud connectivity. Shared transport: `AzureProxyTransportFactory` (one set of handlers per process); no per-app proxy config. Registry override: `AzureBlob\UseWinHttpProxy` REG_SZ `"true"`/`"false"`.

## Entra device team resolution (NewsService)

NewsService can resolve **one dynamic team** per machine from the machine's own Entra (Azure AD) device object each poll cycle, writing `{CacheRootPath}\resolved-teams.json`; NewsService and NewsViewer union it with the static team list and consume it (key-with-content verification — see `docs/security.md`). The feature is gated by `Entra:Enabled` (default `false`).

**appsettings.json (NewsService):**

```json
"Entra": {
  "Enabled": false,
  "GracePeriodMinutes": 240,
  "Mappings": {
    "FAT": "extensionAttribute2-extensionAttribute5-extensionAttribute4",
    "VDE": "extensionAttribute3",
    "VDL": "extensionAttribute6-extensionAttribute2"
  },
  "GroupTeam": {
    "InclusionGroup": "",
    "ExclusionGroup": ""
  }
}
```

**Registry layout** (override; surfaced via the recursive walk in `RegistryConfigurationProvider`):

```
HKLM\Software\[Company]\NewsCentral\NewsService\Entra\
    Enabled              REG_SZ   "true" / "false"
    GracePeriodMinutes   REG_SZ   (int minutes; MUST be REG_SZ — 0 = "no grace" is deliberate; DWORD 0/1 coerce to "False"/"True" and the int binder throws)
    Mappings\
        FAT   REG_SZ   "extensionAttribute2-extensionAttribute5-extensionAttribute4"
        VDE   REG_SZ   "extensionAttribute3"
        VDL   REG_SZ   "extensionAttribute6-extensionAttribute2"
    GroupTeam\
        InclusionGroup   REG_SZ   "NewsCentral Prague ITS"
        ExclusionGroup   REG_SZ   "Excluded Devices"   (optional; empty = no exclusion)
```

`Entra:Mappings:FAT` and `Entra:GroupTeam:InclusionGroup` etc. bind into `EntraOptions` automatically — no provider change.

**Group-membership team (`Entra:GroupTeam`):** a second, independent dynamic-team source. When `InclusionGroup` is set, NewsService resolves the device's transitive membership (one `checkMemberGroups` call) and resolves **one** group team when the device is in the inclusion group and **not** in the (optional) exclusion group; the team folder name is the canonicalized inclusion-group display name (same canonicalization as attribute teams). An **empty `InclusionGroup` disables** the source (and removes any previously resolved group team). It contributes to `resolved-teams.json` tagged `Source: Group`, unioned and verified exactly like attribute teams. Requires the `GroupMember.Read.All` Graph permission (below).

**Rule format:** `extensionAttribute1` on the device is the **selector** and must equal a mapping key (ordinal, case-sensitive — e.g. `FAT`/`VDE`/`VDL`). Its mapped rule is a `'-'`-joined, ordered list of attribute names drawn from `extensionAttribute2`..`extensionAttribute15` (attribute 1 may not appear in a rule; 0 and 16+ are invalid). NewsService reads each referenced attribute, joins the values in **rule order** with `-`, and canonicalizes (lower-case; space/underscore → `-`; strip anything outside `[a-z0-9-]`) into a team folder name — byte-for-byte identical to an authored folder built from the same tokens. Any empty referenced attribute aborts resolution for that cycle.

**Grace (per source, persistent vs transient):** when the device/Graph is **unreachable** (network/timeout/throttling/5xx), each source's last resolved team is retained in `resolved-teams.json` with `State = Grace` for up to `GracePeriodMinutes`, then dropped. An authoritative "no team" answer — device read OK but no/invalid mapping (attribute) or not-in-inclusion/excluded/unresolved group name (group), or device object not found — removes that source's entry **immediately**. A **403** on either the device read or the group check is **persistent**: it removes promptly (logged at Error), never grace. The attribute and group sources grace independently.

**Credential reuse:** the Microsoft Graph reads use the **same `AzureBlob` credential and app registration** as blob access (`AzureCredentialFactory.Create`), built lazily only when Entra is enabled. Because of this, **`AzureBlob:{AuthMode, TenantId, ClientId, …}` must be populated even when `Repository:StorageMode = Share`**.

> Azure app-registration permissions (Graph `Device.Read.All` / `GroupMember.Read.All`, admin consent), storage RBAC, the signing certificate, and device/group provisioning: see `docs/azure-setup.md` — the single source for Azure setup.
