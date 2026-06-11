# Configuration Model

## Registry Hive

All registry-configurable values reside under:

```
HKLM\Software\[Company]\[NewsCentral]\
```

The `[Company]` and `[NewsCentral]` placeholder strings are defined in `appsettings.json` and are **not** overridable via registry (they define the registry path itself).

## Precedence Rule

Registry values override `appsettings.json` values. Override is implemented via `RegistryConfigurationProvider` (in `NewsCentral.Shared`) wired into all components through `IConfigurationBuilder.AddRegistryOverrides(company, "NewsCentral", componentName)`. Any `appsettings.json` key can be overridden by mirroring the JSON section hierarchy as registry subkeys.

## Registry Layout

Each component reads from its own subkey. Values set for one component do not affect another.

**NewsService**
```
HKLM\Software\[Company]\NewsCentral\NewsService\
├── Service\
│       PollIntervalSeconds   DWORD     (poll interval in seconds)
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
├── Hmac\
│       SecretKey   REG_SZ    (Base64-encoded 32-byte key; empty = HMAC disabled)
├── Signing\
│       RequireSignedIndex  REG_SZ    "true" or "false"  (default false; when true, Unsigned/Disabled indexes are rejected)
│       <teamFolderName>\
│           PublicKey          REG_SZ   (Base64 SubjectPublicKeyInfo — ECDSA index verification)
│           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional — rotation window)
│       (one subtree per team; surfaced as Signing:{teamFolderName}:PublicKey via RegistryConfigurationProvider)
└── teams\
        (one REG_SZ value per team; value name = team folder name incl. team- prefix; data = "")
        e.g.  team-cz-its   REG_SZ   ""
              team-de-prod  REG_SZ   ""
```

**NewsViewer**
```
HKLM\Software\[Company]\NewsCentral\NewsViewer\
│   CacheRootPath                REG_SZ    (overrides ViewerConfiguration.CacheRootPath)
│   BypassShowOnceCheck          DWORD     (1 = skip once-per-day guard at startup)
│   BypassImageIntegrityCheck    DWORD     (1 = skip image SHA-256 verification)
├── Hmac\
│       SecretKey   REG_SZ
├── Signing\
│       RequireSignedIndex  REG_SZ    "true" or "false"  (default false; when true, Unsigned/Disabled indexes are rejected)
│       <teamFolderName>\
│           PublicKey          REG_SZ   (Base64 SubjectPublicKeyInfo — ECDSA index verification)
│           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional — rotation window)
│       (one subtree per team; surfaced as Signing:{teamFolderName}:PublicKey via RegistryConfigurationProvider)
└── teams\
        (one REG_SZ value per team; value name = full folder name incl. team- prefix)
```

**NewsCentral** (MAUI authoring app)
```
HKLM\Software\[Company]\NewsCentral\NewsCentral\
│   DataPath               REG_SZ    (root for IStorageService)
│   LockExpirationMinutes  DWORD
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

**`teams\` sub-hive:** team folder names are the value *names* (not the value data). Value names must use the full generated folder name including the `team-` prefix (e.g. `team-cz-its`, not `CZ_ITS`). The provider exposes them as `teams:0`, `teams:1`, … so `IConfiguration.GetSection("teams").GetChildren()` returns one entry per team. Use `TeamConfigurationReader.GetTeams(configuration)` to read them.

**`Signing\` sub-hive:** contains `RequireSignedIndex` (REG_SZ `"true"`/`"false"`, default `"false"`) plus one subkey per team. Each team subkey holds `PublicKey` and (optionally) `PublicKeyPrevious` as `REG_SZ` values containing Base64-encoded SubjectPublicKeyInfo. `RegistryConfigurationProvider` surfaces all values via its recursive `WalkKey` traversal: `Signing:RequireSignedIndex`, `Signing:{teamFolderName}:PublicKey`, and `Signing:{teamFolderName}:PublicKeyPrevious`. Use `SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)` to read the key list; use `configuration.GetValue<bool>("Signing:RequireSignedIndex")` to read the flag. When `RequireSignedIndex` is `true`, `SignatureGate.ShouldReject` rejects Unsigned and Disabled indexes in addition to Invalid — enabling fail-closed enforcement. NewsService and NewsViewer each maintain their own separate `Signing\` subtrees.

## appsettings.json — NewsCentral

```json
{
  "Company": "Contoso",
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
    "ContainerName": "newscentral"
  },
  "Hmac": {
    "SecretKey": ""
  },
  "Signing": {
    "RequireSignedIndex": false
  }
}
```

## appsettings.json — NewsViewer

```json
{
  "Company": "MyCompany",
  "ApplicationName": "NewsCentral",
  "CacheRootPath": "C:\\ProgramData\\NewsCentral",
  "BypassShowOnceCheck": false,
  "BypassImageIntegrityCheck": false,
  "Hmac": {
    "SecretKey": ""
  },
  "Signing": {
    "RequireSignedIndex": false
  }
}
```

## Azure Authentication Modes — NewsService only

NewsService runs as an unattended Windows Service with no interactive user. It authenticates to Azure Blob using one of two modes selected by `AzureBlob:AuthMode`:

| Mode | Credential type | Required keys |
|---|---|---|
| `Certificate` | `ClientCertificateCredential(tenantId, clientId, cert)` | `TenantId`, `ClientId`, `CertificateThumbprint` |
| `ClientSecret` | `ClientSecretCredential(tenantId, clientId, secret)` | `TenantId`, `ClientId`, `ClientSecret` |

**Certificate mode** — the certificate is loaded from `Cert:\LocalMachine\My` by thumbprint (`X509Store(StoreName.My, StoreLocation.LocalMachine)`). Local System has access to `LocalMachine\My` by default; no additional key permission grants are required when the service runs as Local System. Preferred for production.

**ClientSecret mode** — uses a plain client secret string. Simpler to configure for development and testing.

NewsCentral does **not** use these modes. It authenticates via an interactive MSAL user session.
