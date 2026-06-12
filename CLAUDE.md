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
| NewsService | Cache agent — syncs content from repository to local machine, applies wallpaper/lockscreen, uploads telemetry | .NET 9 Windows Service |
| NewsViewer | End-user presentation layer — displays scheduled content from local cache | .NET 9 WinForms desktop app |
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
- **Entra device team resolution (NewsService, behind `Entra:Enabled`)** — each poll cycle NewsService reads its own Entra device object and resolves at most one dynamic team from `extensionAttributes` (selector `extensionAttribute1` → mapped rule over `extensionAttribute2..15` → canonical folder name), unioned with the static team list. Phase 1 (pure `EntraTeamNameResolver` + `ResolvedTeams` DTOs) and Phase 2 (device read, grace, `resolved-teams.json`) are complete; **Phase 2 only produces `{CacheRootPath}\resolved-teams.json` — consuming it (dynamic-team content sync, NewsViewer display, key-with-content verification) is Phase 3.** Key learnings: DeviceId comes from `HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo\<thumbprint>` (dsregcmd `/status` fallback); the **grace window applies to transient unreachability only** — an authoritative "no team" removes the entry promptly; Graph reuses the **single AzureBlob app registration/credential** (`AzureCredentialFactory`, requires `Device.Read.All` admin consent, and `AzureBlob:*` must be set even when `StorageMode=Share`); the Graph SDK rides the **same default .NET HTTP stack as blob**, so no app-specific proxy config. The v1.0 Graph SDK Device model has no typed `extensionAttributes`, so it is read from `Device.AdditionalData` as a Kiota `UntypedObject` — this is **intentional and Microsoft's documented v1.0 pattern** (v1.0 stable; `Microsoft.Graph.Beta` deliberately avoided for production). The `UntypedObject → JsonElement` projection in `EntraDeviceClient` is the single environment-bound line; everything downstream is the pure, unit-tested `EntraExtensionAttributeMapper`, and the orchestrator is tested offline through the `IDeviceIdentityProvider` / `IEntraDeviceClient` seams (`NewsService.Tests`). See `docs/configuration.md`.
  - **Phase 3a — key-with-content for dynamic teams (implemented, tested; consumption is 3b):** `index.json` now carries `TeamIndexFile.SigningPublicKey`; NewsCentral emits the team public key at publish for **all** teams. The field is **excluded from the canonical signed payload** via the same null-and-restore mechanism as `Signature` (`IDeliveredKeyCarrier` + `[JsonIgnore(WhenWritingNull)]`), so a null value keeps canonical bytes bit-identical and every existing static-team signature still verifies (hard regression gate). `SignatureGate.VerifyWithPrecedence(index, registryPublicKeys, isDynamicTeam)` decides the key: **registry key always wins** → delivered key (dynamic only) → `Unsigned`; a **static** team **never** consults the delivered key (anti-downgrade). Dynamic teams are **rotation-free** — the key travels with the content, so readers need no `PublicKeyPrevious` handling. Trust rests on distribution RBAC; not protective against channel-write or local-admin compromise (the `%programdata%` leg is the future anchored-root upgrade point). Shared `ResolvedTeamsReader` + `EffectiveTeams` helpers added for 3b. See `docs/security.md`. **3a does NOT wire NewsService sync or NewsViewer consumption.**

### Data Flow

```
[Network Repository] → [NewsService] → [%programdata%\NewsCentral\ cache] → [NewsViewer]
                                ↑                                                    |
                                └──────────── uploads\session-*.json ───────────────┘
```

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

**NewsViewer startup** — `appsettings.json` is required (`optional: false`). Missing file = hard startup failure. `Main()` validates `Company`, `ApplicationName`, `CacheRootPath` and exits with `MessageBox` if any are empty.

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
@docs/data-model.md
@docs/newscentral-spec.md
@docs/newsservice-spec.md
@docs/newsviewer-spec.md
@docs/security.md
@docs/future.md
