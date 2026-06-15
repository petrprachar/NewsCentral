# Azure Configuration Reference — NewsCentral

This document lists every Azure entity the solution depends on, the settings to configure on each, and
the solution configuration keys that point at them: the two Azure identities (authoring vs. agent), the
storage account, the Entra device data used by the dynamic-team features (extensionAttributes and group
membership), and the optional signing certificate.

---

## Which components touch Azure

| Component | Azure interaction | Identity / credential |
|---|---|---|
| **NewsCentral** (authoring) | **Writes** published content to Blob | `InteractiveBrowserCredential` — interactive **user** sign-in |
| **NewsService** (agent) | **Reads** content from Blob; **reads** this machine's Entra **device** (extensionAttributes + group membership) via Graph | `ClientCertificateCredential` / `ClientSecretCredential` — **app-only** |
| **NewsViewer** (display) | **None** — reads only the local `%programdata%` cache | n/a |

Only NewsCentral (write) and NewsService (read + Graph) authenticate to Azure, with separate identities.

---

## Entities at a glance

| # | Azure entity | Purpose | Required when |
|---|---|---|---|
| 1 | App registration — **agent** (app-only) | NewsService blob read + Graph device/group read | `StorageMode=Azure`, or any time `Entra:Enabled=true` |
| 2 | App registration — **authoring** (interactive) | NewsCentral blob write | `DistributionMode=AzureBlob` |
| 3 | **Storage account** + container | The distribution tier (blob) | `DistributionMode=AzureBlob` / `StorageMode=Azure` |
| 4 | **Entra device data** (extensionAttributes / group membership) | Drive dynamic-team resolution | `Entra:Enabled=true` |
| 5 | **Entra groups** (inclusion / exclusion) | Drive the group-membership team | `Entra:GroupTeam` configured |
| 6 | **Certificate** | Agent app-only credential (preferred) | `AzureBlob:AuthMode=Certificate` |

Entities 1 and 2 may be combined into one app registration (configured as both public and confidential
client); keeping them separate is cleaner and assumed below.

---

## 1. App registration — NewsService agent (app-only / confidential client)

The unattended identity for blob reads and the Graph device/group reads.

| Azure value | Where | Solution key (NewsService) |
|---|---|---|
| Application (client) ID | Overview | `AzureBlob:ClientId` |
| Directory (tenant) ID | Overview | `AzureBlob:TenantId` |

**Credential** (one of):
- **Certificate (preferred):** upload the public `.cer` (Certificates & secrets); deploy the private-key
  cert to each agent machine's `LocalMachine\My` (see §6). → `AzureBlob:AuthMode = Certificate`,
  `AzureBlob:CertificateThumbprint`.
- **Client secret:** → `AzureBlob:AuthMode = ClientSecret`, `AzureBlob:ClientSecret`.

**API permissions (Microsoft Graph), all Application + admin consent:**
- `Device.Read.All` — read the device object and its extensionAttributes. Needed for any Entra dynamic
  team; omit if `Entra:Enabled=false`.
- `GroupMember.Read.All` — resolve group display names and check the device's transitive group
  membership. Needed **only** for the group-membership team (`Entra:GroupTeam`). `Directory.Read.All` is a
  broad fallback if `GroupMember.Read.All` proves insufficient at runtime (which surfaces as a `403`).
- No Azure Storage API permission is required for app-only access — storage is governed by RBAC (§3).

**RBAC:** assign the agent service principal a storage role — see §3.

---

## 2. App registration — NewsCentral authoring (interactive / public client)

Authentication is delegated to the signed-in user.

| Azure value | Where | Solution key (NewsCentral) |
|---|---|---|
| Application (client) ID | Overview | `AzureBlob:ClientId` |
| Directory (tenant) ID | Overview | `AzureBlob:TenantId` (blank = `organizations`) |

- Authentication → Mobile and desktop applications → redirect URI `http://localhost`; **Allow public
  client flows = Yes**; no secret.
- API permissions → **Azure Storage** `user_impersonation` (Delegated).
- **RBAC:** the authors (user/group) need a write role — see §3.

---

## 3. Storage account + container

| Azure value | Notes | Solution key |
|---|---|---|
| Storage account name | **Without** the `.blob.core.windows.net` suffix | NewsService & NewsCentral `AzureBlob:AccountName` |
| Container name | Default `newscentral`; auto-created by NewsCentral on first publish | NewsService `AzureBlob:ContainerName` · NewsCentral `Storage:AzureBlobContainerName` |

**RBAC role assignments** (IAM on the account or container):

| Principal | Role | Reason |
|---|---|---|
| Authors (NewsCentral user/group) | **Storage Blob Data Contributor** | Publish content |
| Agent service principal (§1) | **Storage Blob Data Reader** | Sync content to cache |

> If the agent also uploads session telemetry to the same account, grant it **Storage Blob Data
> Contributor** instead of Reader.

**Networking:** NewsService runs as Local System and reaches blob over the same default .NET HTTP stack
(and corporate proxy) as the rest of its traffic — no app-specific proxy. Permit the agent machines/proxy
egress if the account uses a firewall or private endpoint.

---

## 4. Entra device data — dynamic-team features

Required only when `Entra:Enabled=true`. Each feature is **inert** until the device data is provisioned.
Devices must be **Entra-joined or Hybrid-joined**.

### 4a. extensionAttributes (attribute-based team)

Per device, set `extensionAttribute1` = the machine-type selector (`FAT`/`VDE`/`VDL`, matching a key in
`Entra:Mappings`), plus the attributes (`extensionAttribute2`..`15`) named by that selector's rule. Setting
these requires `Device.ReadWrite.All` for the **admin** doing the provisioning:

```powershell
Connect-MgGraph -Scopes "Device.ReadWrite.All"
$obj = Get-MgDevice -Filter "deviceId eq '<DeviceId from dsregcmd /status>'"
Update-MgDevice -DeviceId $obj.Id -AdditionalProperties @{
    extensionAttributes = @{ extensionAttribute1 = "FAT"; extensionAttribute2 = "CZ";
                             extensionAttribute4 = "ITS"; extensionAttribute5 = "Prague" } }
```

### 4b. Group membership (group-based team)

See §5 for the groups. Per the group rule, a device gets a team named after the **inclusion** group when
it is a transitive member of it and not of the **exclusion** group. Provisioning is simply **adding the
target devices as members** (direct or nested) of the appropriate groups.

---

## 5. Entra groups — inclusion / exclusion (group-membership team only)

Required only when `Entra:GroupTeam` is configured.

- Create the **inclusion** group and (optionally) the **exclusion** group in Entra.
- Add target devices as members — membership is evaluated **transitively** (nested groups count).
- The inclusion group's **display name** becomes the team folder name, canonicalized (lower-case;
  space/underscore → `-`; strip non-`[a-z0-9-]`; e.g. `NewsCentral Prague ITS` → `newscentral-prague-its`).
  Publish content under that exact folder name.
- **Display names must be unique** — an ambiguous inclusion name (more than one matching group) produces
  no team and is logged. Use a uniquely-named group.

| Solution key (NewsService) | Value |
|---|---|
| `Entra:GroupTeam:InclusionGroup` | inclusion group display name (activates the feature) |
| `Entra:GroupTeam:ExclusionGroup` | exclusion group display name (optional) |

---

## 6. Certificate (only when `AzureBlob:AuthMode=Certificate`)

1. Generate a certificate. 2. Upload the public `.cer` to app registration §1. 3. Deploy the private-key
cert to each agent machine's `LocalMachine\My` (via GPO; Local System reads this store by default).
4. Record the thumbprint → `AzureBlob:CertificateThumbprint`.

---

## Configuration mapping (NewsService `AzureBlob` / `Entra` / `Repository`)

| Key | Value | Source |
|---|---|---|
| `Repository:StorageMode` | `Azure` or `Share` | choice |
| `AzureBlob:AuthMode` | `Certificate` / `ClientSecret` | choice |
| `AzureBlob:TenantId` / `ClientId` | tenant / client ID | §1 |
| `AzureBlob:CertificateThumbprint` / `ClientSecret` | credential | §1 / §6 |
| `AzureBlob:AccountName` / `ContainerName` | storage account / container | §3 |
| `Entra:Enabled` | `true` to enable dynamic teams | choice |
| `Entra:GracePeriodMinutes` | grace window (default 240) | choice |
| `Entra:Mappings:{FAT,VDE,VDL}` | selector → attribute rule | §4a |
| `Entra:GroupTeam:InclusionGroup` | inclusion group display name | §5 |
| `Entra:GroupTeam:ExclusionGroup` | exclusion group display name (optional) | §5 |

> When `Entra:Enabled=true`, the `AzureBlob` credential fields must be populated **even if
> `StorageMode=Share`** — the Graph reads reuse the blob credential.

NewsCentral keys: `Storage:EnableBlobDistribution=true`, `Storage:DistributionMode=AzureBlob`,
`Storage:AzureBlobContainerName`, `AzureBlob:TenantId/ClientId/AccountName` (§2/§3).

---

## Ordered setup checklist

1. **Storage:** create the account; note the name; create (or let NewsCentral create) the container.
2. **Agent app reg (§1):** register; add a certificate or secret; add Graph `Device.Read.All`, plus
   `GroupMember.Read.All` for the group-membership team — **grant admin consent** for each.
3. **Authoring app reg (§2):** register; `http://localhost` redirect; allow public client flows; add
   Azure Storage `user_impersonation`.
4. **RBAC:** authors → Storage Blob Data Contributor; agent SP → Storage Blob Data Reader.
5. **Certificate (§6, if Certificate mode):** upload public cert; deploy private cert to agent machines.
6. **Device data:** (a) stamp extensionAttributes (§4a); (b) create the inclusion/exclusion groups and add
   target devices as members (§5).
7. **Configure** NewsCentral and NewsService per the mapping; deploy fleet values via registry/GPO.

---

## Gotchas

- **Graph permissions need admin consent** — a missing `Device.Read.All` or `GroupMember.Read.All` consent
  surfaces as a `403`, which the agent treats as a clean "no team" with an **error** log (not a silent
  grace), so the cause is visible.
- **Blob credential is reused for Graph** — populate `AzureBlob:*` on NewsService even in `Share` mode when
  Entra is enabled.
- **`AccountName` excludes the suffix** — `mystorageaccount`, not the full host.
- **Attribute selector is case-sensitive**; group **display names must be unique** (ambiguous → no team).
- **Group membership is transitive** — nested groups count.
- **Devices must be Entra/Hybrid-joined**, and each feature is inert until its device data (attributes or
  group membership) is provisioned.
- **No app-specific proxy** — Graph and Blob share the same default HTTP stack.
