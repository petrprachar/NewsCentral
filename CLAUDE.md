# NewsCentral — Solution Specification

**Version:** 2.7  
**Status:** Implementation in progress  
**Scope:** NewsCentral, NewsCentral.Shared, NewsService, NewsViewer, NewsTester

---

## Components at a Glance

| Component | Role | Type |
|---|---|---|
| NewsCentral | Content authoring, approval, scheduling, publishing, user/team management | .NET 9 MAUI Blazor Hybrid desktop app |
| NewsCentral.Shared | Shared domain models referenced by all components | .NET 9 class library |
| NewsService | Cache agent — syncs content from repository to local machine, applies the lock screen (with a configurable default image), uploads telemetry | .NET 9 Windows Service |
| NewsViewer | End-user presentation layer — displays scheduled content from local cache; applies the desktop wallpaper in the user session (`SystemParametersInfo` + HKCU) | .NET 9 WinForms desktop app |
| NewsTester | Content preview tool for authors and approvers | Future — independent desktop app |

---

## Architecture Principles

- All components are configurable via `appsettings.json`. Registry values (`HKLM\Software\[Company]\NewsCentral\`) override `appsettings.json`. Each component has its own subkey.
- Storage backend (local file share vs. Azure Blob Storage) is switchable via registry without code changes.
- Local/file share mode is the **primary development and testing configuration**. No Azure dependency is required for full functional testing.
- HMAC-SHA256 anti-tamper for session telemetry — NewsViewer signs, NewsService verifies; `index.json` signing moved to per-team ECDSA (see below).
- ECDSA P-256 per-team `index.json` signing — Phases A, B1 & C complete: signing core in `NewsCentral.Shared`, `IndexGenerationService` signs with team key, `SyncService` and `PresentationSelector` verify via `SigningKeyConfigurationReader`. B2 (Key Management page) complete; D (registry scripts) parked indefinitely; see `docs/security.md`.
- All domain models live in **NewsCentral.Shared** — no model duplication across projects.
- NativeAOT migration path is preserved for NewsViewer.
- **Entra dynamic-team resolution (NewsService-only, behind `Entra:Enabled`; default off)** — resolves dynamic teams from **two independent sources**: the device's Entra `extensionAttributes` (attribute source) and group membership (group source, when `Entra:GroupTeam:InclusionGroup` is set), unioning them with the static registry team list on both consuming tiers. Each source is tagged in `resolved-teams.json` (`Source: Attribute|Group`) and graces independently. End-to-end complete (Phases 1–3b + group G1/G2). See the **Entra Device Team Resolution** section below.

### Data Flow

```
[Network Repository] → [NewsService] → [%programdata%\NewsCentral\ cache] → [NewsViewer]
                                ↑                                                    |
                                └──────────── uploads\session-*.json ───────────────┘
```

---

## Entra Device Team Resolution

NewsService-only feature, gated by `Entra:Enabled` (default `false`). Each poll cycle NewsService reads **this machine's** Entra device via Microsoft Graph and resolves dynamic teams from **two independent sources** — the device `extensionAttributes` (attribute source) and group membership (group source) — then writes `{CacheRootPath}\resolved-teams.json` (each entry tagged `Source: Attribute|Group`). Both NewsService and NewsViewer **union** that set with their static (registry) team list and consume it (NewsViewer ignores `Source`). End-to-end complete (Phases 1–3b + group G1/G2). Pure logic lives in `NewsCentral.Shared` and is unit-tested.

**Resolution — attribute source.** `extensionAttribute1 ∈ {FAT, VDE, VDL}` selects a mapping rule naming `extensionAttribute2..15`; the referenced values are `'-'`-joined, lowercased, prefix-free → folder name. An empty referenced attribute, or `extensionAttribute1` appearing in a rule, aborts (no team). One attribute team max per device. (→ `docs/configuration.md`)

**Resolution — group source.** Active only when `Entra:GroupTeam:InclusionGroup` is set. One `checkMemberGroups` call evaluates the device's transitive membership; **inclusion ∧ ¬exclusion** → one group team named after the canonicalized inclusion-group display name (same canonicalization as the attribute path). Empty inclusion **disables** the source and removes any prior group team (`GroupTeamDecision` + `GroupOutcomeMapper`, pure/unit-tested).

**Auth / Graph.** Single app registration shared with Azure Blob — same credential via `AzureCredentialFactory`; `AzureBlob:*` must be populated even when `StorageMode=Share`. Admin-consented application permissions: `Device.Read.All` (device read) and, for the group source, `GroupMember.Read.All` (`Directory.Read.All` is a broad fallback). DeviceId is read locally from `HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo\<thumbprint>` (`dsregcmd /status` fallback) and must be a GUID or Graph is skipped. `Microsoft.Graph` v6 fluent SDK; the v1.0 Device model has no typed `extensionAttributes`, so it is read from `AdditionalData` as a Kiota `UntypedObject` (documented v1.0 pattern; beta deliberately avoided). Graph rides the same default .NET HTTP stack as blob — no app-specific proxy. **Optional:** `AzureBlob:UseWinHttpProxy` (default off) routes blob **and** Graph through the machine WinHTTP proxy (Intune / Windows Update path) via the shared `AzureProxyTransportFactory`, for reliable proxying under Local System; off = no behavior change.

**Grace (per source, persistent vs transient).** `GraphFailureClassifier` maps failures: a **403 is persistent** on either read (clean removal + Error, never grace); a clean answer — unknown/no selector, device-not-found, not-in-inclusion/excluded/unresolved group name — removes that source's team **promptly**; only transient "couldn't reach Graph" (`Unreachable`: network/timeout/throttling/5xx) rides grace. The attribute and group sources grace **independently**, each `GracePeriodMinutes` from its own last confirmation; `resolved-teams.json` carries `State` `Active|Grace` per entry.

**Trust model — Option A (key-with-content).** The team signing **public** key is embedded in `index.json` (`SigningPublicKey`, excluded from the canonical signing input) for **all** teams. Consumers decide trust by precedence (`SignatureGate.VerifyWithPrecedence`): registry key wins → else delivered key (**dynamic teams only**) → else unsigned per `RequireSignedIndex`. Anti-downgrade: the delivered key is never used for static teams and never overrides a registry key. Dynamic-team authenticity rests on **distribution-tier RBAC**; static teams stay registry-anchored. `resolved-teams.json` is unsigned local state protected by cache ACLs. Dynamic teams are **rotation-free** (the key travels with the content); static teams rotate via the registry dual-key pattern. (→ `docs/security.md`)

**Durable learnings.** static-vs-dynamic is a property of the **consuming machine**, not the team — so the public key is embedded in *every* index and is inert for static consumers. `resolved-teams.json` lists dynamic teams only; both components union it with their registry list. NewsViewer is a **read-only** consumer. The resolver, grace merger, attribute mapper, and precedence are pure and unit-tested (`NewsCentral.Shared` / `NewsService.Tests`), leaving only **two** environment-bound seams: the DeviceId read and the live Graph round-trip.

**Status.** Structurally complete and offline-tested, shipping behind `Entra:Enabled=false`. The only unproven path is live validation on an Entra-joined box (pending) — the environment-bound seams are the DeviceId read, the device fetch HTTP, and `EntraGroupClient`'s name→id + `checkMemberGroups` calls; everything else (classifier, group/outcome mappers, dual-source orchestrator) is fake-tested. Operational prerequisites before enabling: `Device.Read.All` consent (plus `GroupMember.Read.All` for the group source), device `extensionAttributes` / group membership provisioned across the fleet, verified cache ACLs, and GPO deployment of the Entra registry config.

---

## Technologies

| Component | Technology |
|---|---|
| NewsCentral | C# / .NET 9 MAUI Blazor Hybrid |
| NewsCentral.Shared | C# / .NET 9 class library |
| NewsService | C# / .NET 9 Windows Service (`Microsoft.NET.Sdk.Worker`) |
| NewsViewer | C# / .NET 9 WinForms; NativeAOT migration path preserved |
| Data files | JSON (`System.Text.Json`); see Critical Rules for the two option sets used across components |
| Images | Base64-encoded and embedded in presentation JSON |
| Azure auth | MSAL interactive (NewsCentral); machine certificate from local store (NewsService) |

---

## Critical Rules

**JSON serialization** — two option sets; use the right one for the context.

*NewsService / NewsViewer* — general cache and telemetry I/O:
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
}
```

*NewsCentral* — index file generation and signing (`JsonConfiguration.GetIndexJsonOptions()`):
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(), new SmartDateTimeConverter() }
}
```
`SmartDateTimeConverter` writes UTC `DateTime` with a `Z` suffix and `Unspecified` (schedule times) without one, and truncates to whole seconds. **This truncation is what keeps the ECDSA signature byte-consistent with the persisted file** — without it, sub-second precision present in memory is stripped on write, causing every verifier to return `Invalid`. Verifiers (NewsService, NewsViewer) deserialize with standard ISO parsing, which faithfully round-trips whole-second timestamps.

**Assignments are immutable after creation.** There is no edit path. To change a scheduled assignment, delete it and create a new one.

**ShowNew + UseVirtualDesktop are mutually exclusive.** `ShowNewApplicationContext` creates a hidden timer window before `SwitchToNew()` is called, which causes `SetThreadDesktop` to fail. The `CreateAssignment` UI enforces this: ShowNew is disabled (greyed out with a hint) when the presentation has `UseVirtualDesktop = true`. Wallpaper and LogonScreen badges are also dimmed when `UseVirtualDesktop` is set.

**Registry `teams\` naming** — value names must match the generated folder name exactly, i.e. the sanitized team name (e.g. `cz-its`, not `CZ_ITS`). Folder names carry no `team-` prefix.

**NewsViewer startup** — `appsettings.json` is required (`optional: false`). Missing file = hard startup failure. `Main()` validates `Company`, `CacheRootPath` and exits with `MessageBox` if any are empty.

---

## Communication

| Path | Mechanism |
|---|---|
| NewsCentral → Repository | File share write or Azure Blob upload (switchable) |
| NewsService → Repository | File share read/write or Azure Blob read/write (switchable) |
| NewsService → Cache | Local file write to `%programdata%\NewsCentral\` |
| NewsViewer → Cache | Local file read from `%programdata%\NewsCentral\` |
| NewsViewer → NewsService | Via `uploads\` folder (session telemetry JSON files) |

No direct inter-process communication. All coordination is via the shared cache folder structure.

---

## Operational Requirements

| Component | Requirement |
|---|---|
| NewsCentral | User-initiated start and shutdown; interactive Azure authentication |
| NewsService | Automatic start as Windows Service; runs unattended; handles weeks-long uptime |
| NewsViewer | Auto-start via HKLM Run and Task Scheduler (Workstation Unlock); optional Start menu entry |
| All | Configurable via `appsettings.json` with registry override under `HKLM\Software\[Company]\[NewsCentral]\` |

---

## Non-Goals

- No third-party UI frameworks for wallpaper or lockscreen management; Microsoft APIs only
- No peer-to-peer communication between components; all coordination via shared file system
- No real-time push from server to client; polling-based model throughout
- No Azure dependency required for local development and testing

---

## Reference Documents

@docs/solution-structure.md
@docs/configuration.md
@docs/azure-setup.md
@docs/data-model.md
@docs/newscentral-spec.md
@docs/newsservice-spec.md
@docs/entra-group-team.md
@docs/newsviewer-spec.md
@docs/security.md
@docs/anti-tamper.md
@docs/future.md
