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
- HMAC-SHA256 anti-tamper protection is implemented end-to-end: NewsCentral signs `index.json`; NewsViewer signs session telemetry; both NewsService and NewsViewer verify before using content.
- All domain models live in **NewsCentral.Shared** — no model duplication across projects.
- NativeAOT migration path is preserved for NewsViewer.

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
| Data files | JSON (`System.Text.Json`, `WriteIndented`, `CamelCase`, `CaseInsensitive`, `JsonStringEnumConverter`) |
| Images | Base64-encoded and embedded in presentation JSON |
| Azure auth | MSAL interactive (NewsCentral); machine certificate from local store (NewsService) |

---

## Critical Rules

**JSON serialization** — all projects use identical options:
```csharp
new JsonSerializerOptions {
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
}
```

**Assignments are immutable after creation.** There is no edit path. To change a scheduled assignment, delete it and create a new one.

**ShowNew + UseVirtualDesktop are mutually exclusive.** `ShowNewApplicationContext` creates a hidden timer window before `SwitchToNew()` is called, which causes `SetThreadDesktop` to fail. The `CreateAssignment` UI enforces this: ShowNew is disabled (greyed out with a hint) when the presentation has `UseVirtualDesktop = true`. Wallpaper and LogonScreen badges are also dimmed when `UseVirtualDesktop` is set.

**Registry `teams\` naming** — value names must use the full generated folder name including the `team-` prefix (e.g. `team-cz-its`, not `CZ_ITS`).

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
