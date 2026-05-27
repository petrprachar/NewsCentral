# NewsCentral — Solution Specification

**Version:** 2.3  
**Status:** Implementation in progress  
**Scope:** NewsCentral, NewsCentral.Shared, NewsService, NewsViewer, NewsTester

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Solution Architecture](#2-solution-architecture)
3. [Technologies](#3-technologies)
4. [Solution Structure](#4-solution-structure)
5. [Configuration Model](#5-configuration-model)
6. [Data Model](#6-data-model)
7. [Cache Folder Structure](#7-cache-folder-structure)
8. [Component Specifications](#8-component-specifications)
   - 8.1 [NewsCentral](#81-newscentral)
   - 8.2 [NewsCentral.Shared](#82-newscentralshared)
   - 8.3 [NewsService](#83-newsservice)
   - 8.4 [NewsViewer](#84-newsviewer)
   - 8.5 [NewsTester](#85-newstester)
9. [Communication](#9-communication)
10. [Security](#10-security)
11. [Operational Requirements](#11-operational-requirements)
12. [Non-Goals](#12-non-goals)
13. [Future Extensions](#13-future-extensions)

---

## 1. Project Overview

### Purpose

The solution provides a structured communication channel between content author teams and corporate end users. Content is prepared, approved, scheduled, and published by authors using **NewsCentral**. It is distributed to corporate workstations by **NewsService** and presented to users by **NewsViewer**.

### Components at a Glance

| Component | Role | Type |
|---|---|---|
| NewsCentral | Content authoring, approval, scheduling, publishing, user/team management | .NET 9 MAUI Blazor Hybrid desktop app |
| NewsCentral.Shared | Shared domain models referenced by all components | .NET 9 class library |
| NewsService | Cache agent — syncs content from repository to local machine, applies wallpaper/lockscreen, uploads telemetry | .NET 9 Windows Service |
| NewsViewer | End-user presentation layer — displays scheduled content from local cache | .NET 9 WinForms desktop app |
| NewsTester | Content preview tool for authors and approvers | Future — independent desktop app |

---

## 2. Solution Architecture

### Principles

- All components are configurable via `appsettings.json`. Selected values are additionally overridable via Windows registry (HKLM). Registry values take precedence over `appsettings.json`.
- Storage backend (local file share vs. Azure Blob Storage) is switchable via registry without code changes.
- Local/file share mode is the **primary development and testing configuration**. No Azure dependency is required for full functional testing of any component.
- The architecture is prepared for future anti-tamper protection via HMAC signatures.
- A viable migration path to NativeAOT is preserved for NewsViewer.
- All domain models live in **NewsCentral.Shared** and are referenced by every component — no model duplication across projects.

### Data Flow

```
[Network Repository]
  (File Share or Azure Blob)
        |
        | (sync, configurable interval)
        v
[NewsService]
  - Populates %programdata%\NewsCentral\ cache
  - Sets wallpaper / lockscreen
  - Uploads session telemetry from NewsViewer
        |
        | (local file access)
        v
[NewsViewer]
  - Reads content from cache
  - Watches for cache changes
  - Writes session telemetry to cache\uploads\
        |
        | (session JSON)
        v
[NewsService] --> [Network Repository / Azure Blob]
```

---

## 3. Technologies

| Component | Technology | Status |
|---|---|---|
| NewsCentral | C# / .NET 9 MAUI Blazor Hybrid | Active development |
| NewsCentral.Shared | C# / .NET 9 class library | Implemented |
| NewsService | C# / .NET 9 — Windows Service (`Microsoft.NET.Sdk.Worker`) | Implemented |
| NewsViewer | C# / .NET 9 — WinForms; NativeAOT migration path preserved | Phase 2 complete |
| NewsTester | C# — to be decided at design time | Future |
| Data files | JSON throughout (`System.Text.Json`, `WriteIndented = true`, `CamelCase`, `PropertyNameCaseInsensitive`, `JsonStringEnumConverter`) | — |
| Images | Base64-encoded and embedded in presentation JSON | — |
| Azure auth | MSAL (NewsCentral interactive); Machine certificate from local store (NewsService) | — |
| Network auth | MS Azure Storage via certificate (NewsService) | — |

---

## 4. Solution Structure

### Projects in Solution

```
NewsCentral.sln
├── NewsCentral.Shared\          .NET 9 class library — shared domain models
├── NewsCentral\                 .NET 9 MAUI Blazor Hybrid — authoring app
├── NewsService\                 .NET 9 Windows Service — cache sync agent (implemented)
└── NewsViewer\                  .NET 9 WinForms — end-user presentation viewer (Phase 1 implemented)
```

### NewsCentral.Shared — Model Layout

```
NewsCentral.Shared\
└── Models\
    ├── IEntity.cs                     interface IEntity { GetId(); SetId(); }
    ├── Presentation.cs
    ├── Schedule.cs
    ├── Assignment.cs
    ├── Team.cs                        Team, TeamsCollection
    ├── User.cs                        User, TeamRole, UsersCollection
    └── IndexFile\
        ├── TeamIndexFile.cs           root structure for index.json
        ├── PublishedAssignmentIndex.cs one entry per published assignment
        ├── ContentInfo.cs             image path, hash, size, URL
        ├── DisplayTypeInfo.cs         IsNewsOfWeek, IsWallpaper, IsLogonScreen
        └── IndexStatistics.cs         summary counts
```

All model namespaces are `NewsCentral.Models` and `NewsCentral.Models.IndexFile` — identical to their previous location in the NewsCentral project, so no using-directive changes were required in NewsCentral when the shared library was extracted.

### NewsService — Service Layout

```
NewsService\
├── Configuration\
│   ├── ServiceConfiguration.cs    typed POCOs bound from appsettings.json
│   └── RegistryConfiguration.cs   reads HKLM overrides; registry wins over config
├── Models\
│   ├── StatusFile.cs              status.json structure
│   └── ServiceState.cs            servicestate.json structure
├── Services\
│   ├── IRepositoryReader.cs       abstraction over Share / Azure repository
│   ├── LocalShareRepositoryReader.cs  file share implementation (primary)
│   ├── AzureBlobRepositoryReader.cs   Azure implementation — Certificate / ClientSecret auth
│   ├── CacheManager.cs            all local cache I/O; SHA-256 sidecar hashes
│   ├── WallpaperService.cs        IDesktopWallpaper COM + PersonalizationCSP registry
│   ├── TelemetryUploader.cs       copies uploads\session-*.json to repository
│   └── SyncService.cs             orchestrates the five-step poll cycle
├── JsonDefaults.cs                shared JsonSerializerOptions (WriteIndented + CamelCase + CaseInsensitive + enum converter)
├── Worker.cs                      BackgroundService host; reads interval from registry
├── Program.cs                     DI wiring; storage mode resolved from registry at startup
└── appsettings.json
```

### NewsViewer — Layout

```
NewsViewer\
├── Configuration\
│   ├── ViewerConfiguration.cs        typed POCOs bound from appsettings.json
│   └── RegistryConfiguration.cs      reads HKLM 'teams' and 'BypassShowOnceCheck'; same pattern as NewsService
├── Models\
│   ├── ViewerState.cs                viewerstate.json structure
│   └── SessionTelemetry.cs           uploads\session-*.json structure
├── Services\
│   ├── PresentationSelector.cs       reads index.json per team, filters active, picks most recent
│   ├── ViewerStateService.cs         reads/writes viewerstate.json for ShowOnce/ShowNew tracking
│   ├── ShowNewApplicationContext.cs  ApplicationContext subclass; FileSystemWatcher + poll timer for ShowNew mode
│   ├── VirtualDesktopManager.cs      CreateDesktop/SwitchDesktop/SetThreadDesktop wrapper (ShowOnce only)
│   └── TelemetryWriter.cs            writes session-{guid}.json to uploads\ on close
├── Forms\
│   ├── ViewerForm.cs                 1600×900 borderless WinForms window; hover-triggered side panel
│   └── BackgroundForm.cs             fullscreen solid-colour background for virtual desktop
├── NativeMethods.cs                  Win32 P/Invoke — desktop, thread, process APIs
├── JsonDefaults.cs                   shared JsonSerializerOptions (same standard as NewsService)
├── Program.cs                        entry point; startup checks; remote session guard; branches on ShowMode
└── appsettings.json
```

### Project References

```
NewsCentral.Shared   (no project references)
       ↑
       ├── NewsCentral
       ├── NewsService
       └── NewsViewer
```

NewsCentral and NewsService both reference **only** `NewsCentral.Shared`. NewsService does **not** reference NewsCentral (a MAUI project cannot be referenced from a non-MAUI project without MAUI build tasks propagating into the referencing project).

### JSON Serialization Convention

All projects use the same options:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new()
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};
```

---

## 5. Configuration Model

### 5.1 Registry Hive

All registry-configurable values reside under:

```
HKLM\Software\[Company]\[NewsCentral]\
```

The `[Company]` and `[NewsCentral]` placeholder strings are defined in `appsettings.json` and are **not** overridable via registry.

### 5.2 Precedence Rule

Registry values override `appsettings.json` values. If a registry value is absent, the `appsettings.json` value applies.

### 5.3 Registry Values

| Value Name | Type | Description | Default |
|---|---|---|---|
| `teams` | `REG_SZ` | Semicolon-separated list of team identifiers configured for this machine. Example: `"team_xy;team_xz"` | — |
| `StorageMode` | `REG_SZ` | Storage backend: `Share` or `Azure` | `Share` |
| `AzureUploadEnabled` | `DWORD` | Whether NewsService uploads telemetry to Azure Blob | `0` |
| `PollIntervalSeconds` | `DWORD` | NewsService polling interval in seconds | `300` |
| `BypassShowOnceCheck` | `DWORD` | NewsViewer only. Set to `1` to skip the once-per-day guard at startup — allows repeated test runs without waiting for a new day. Does **not** affect the ShowNew watcher's day-boundary logic. | `0` |

### 5.4 appsettings.json — NewsCentral

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
  }
}
```

NewsCentral authenticates to Azure using an **interactive MSAL user session** (`InteractiveBrowserCredential`) — no service credentials are stored in config. Token is persisted in a named cache (`"NewsCentral"`) so subsequent calls are non-interactive. See Section 10 (Security).

### 5.5 appsettings.json — NewsService

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
  }
}
```

### 5.6 Azure Authentication Modes — NewsService only

NewsService runs as an unattended Windows Service with no interactive user. It authenticates to Azure Blob using one of two modes selected by `AzureBlob:AuthMode`:

| Mode | Credential type | Required keys |
|---|---|---|
| `Certificate` | `ClientCertificateCredential(tenantId, clientId, cert)` | `TenantId`, `ClientId`, `CertificateThumbprint` |
| `ClientSecret` | `ClientSecretCredential(tenantId, clientId, secret)` | `TenantId`, `ClientId`, `ClientSecret` |

**Certificate mode** — the certificate is loaded from `Cert:\LocalMachine\My` by thumbprint (`X509Store(StoreName.My, StoreLocation.LocalMachine)`). Local System has access to `LocalMachine\My` by default; no additional key permission grants are required when the service runs as Local System. Preferred for production.

**ClientSecret mode** — uses a plain client secret string. Simpler to configure for development and testing.

NewsCentral does **not** use these modes. It authenticates via an interactive MSAL user session (see Section 10).

---

## 6. Data Model

All models are defined in `NewsCentral.Shared` and implement `IEntity`.

### 6.1 IEntity

```csharp
public interface IEntity
{
    string GetId();
    void SetId(string id);
}
```

### 6.2 Presentation

The core content unit distributed to NewsViewer.

```csharp
public class Presentation : IEntity
{
    public string PresentationID { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }

    public string TeamID { get; set; }
    public string TeamFolderName { get; set; }

    public string OriginalImagePath { get; set; }
    public string? GeneratedImagePath { get; set; }
    public string ImageOriginalName { get; set; }
    public string ImageName { get; set; }

    public bool IsNewsOfWeek { get; set; }
    public bool IsWallpaper { get; set; }
    public bool IsLogonScreen { get; set; }

    public int DisplayDurationSeconds { get; set; }     // Countdown timer; no registry default
    public bool UseVirtualDesktop { get; set; }
    public string VirtualDesktopBackgroundColor { get; set; } = "#000000";

    public string ContentImageBase64 { get; set; }      // Base64-encoded image
    public string? PosterText { get; set; }
    public string MoreUrl { get; set; }

    public string CreatedBy { get; set; }
    public DateTime DateCreated { get; set; }
    public string ModifiedBy { get; set; }
    public DateTime LastModified { get; set; }
    public int Version { get; set; }

    public string? Signature { get; set; }              // Reserved for HMAC
}
```

### 6.3 Schedule

```csharp
public class Schedule : IEntity
{
    public string ScheduleID { get; set; }
    public string PresentationID { get; set; }
    public string Version { get; set; }
    public DateTime ScheduleStart { get; set; }
    public DateTime ScheduleEnd { get; set; }
    public string DaysOfWeek { get; set; } = "1,2,3,4,5,6,7";  // Mon–Sun
    public bool IsActive { get; set; }
    public string CreatedBy { get; set; }
    public DateTime LastModified { get; set; }

    public enum DisplayMode { ShowOnce, ShowNew }
    public DisplayMode ShowMode { get; set; } = DisplayMode.ShowOnce;
}
```

`DisplayMode.ShowOnce` — show once per calendar day (first start or first unlock).  
`DisplayMode.ShowNew` — same as ShowOnce, and also when new content arrives.

### 6.4 Assignment

```csharp
public class Assignment : IEntity
{
    public string AssignmentID { get; set; }
    public string PresentationID { get; set; }
    public string PresentationVersion { get; set; }
    public string ScheduleID { get; set; }
    public string SourceTeam { get; set; }
    public string TargetTeam { get; set; }
    public AssignmentStatus Status { get; set; }        // see enum below
    public bool RequiresApproval { get; set; }

    public bool IsNewsOfWeek { get; set; }
    public bool IsWallpaper { get; set; }
    public bool IsLogonScreen { get; set; }

    // Audit trail
    public string CreatedBy { get; set; }
    public DateTime DateCreated { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public string? ApprovalNotes { get; set; }
    public string? PublishedBy { get; set; }
    public DateTime? PublishedDate { get; set; }
    public List<string> PublishedPaths { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledDate { get; set; }
    public string? CancellationReason { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedDate { get; set; }
    public string? RejectionReason { get; set; }
}

public enum AssignmentStatus
{
    Draft, PendingApproval, Approved, Published, Cancelled, Rejected
}
```

### 6.5 Team / User

```csharp
public class Team : IEntity
{
    public string TeamID { get; set; }
    public string Name { get; set; }
    public string FolderName { get; set; }
    public string Description { get; set; }
    public bool IsActive { get; set; }
    // ...
}
public class TeamsCollection : IEntity { public List<Team> Teams { get; set; } }

public class User : IEntity
{
    public string UserID { get; set; }
    public string Username { get; set; }
    public string Email { get; set; }
    public string? UPN { get; set; }
    public bool IsSystemAdmin { get; set; }
    public List<TeamRole> TeamRoles { get; set; }
    // ...
}
public class TeamRole { public string TeamID; public List<string> Roles; }
public class UsersCollection : IEntity { public List<User> Users { get; set; } }
```

### 6.6 TeamIndexFile (index.json per team)

The `index.json` written to each team folder by NewsCentral and consumed by NewsService and NewsViewer.

```csharp
public class TeamIndexFile
{
    public string TeamFolderName { get; set; }
    public string TeamName { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string IndexHash { get; set; }               // SHA256 of content for change detection
    public List<PublishedAssignmentIndex> PublishedAssignments { get; set; }
    public IndexStatistics Statistics { get; set; }
}

public class PublishedAssignmentIndex
{
    public string AssignmentId { get; set; }
    public string PresentationId { get; set; }
    public string ScheduleId { get; set; }
    public string PresentationName { get; set; }
    public int PresentationVersion { get; set; }
    public DateTime PresentationLastModified { get; set; }
    public DateTime ScheduleStart { get; set; }         // Client local time (no Z suffix)
    public DateTime ScheduleEnd { get; set; }           // Client local time (no Z suffix)
    public string DaysOfWeek { get; set; }
    public DateTime PublishedDate { get; set; }
    public string PublishedBy { get; set; }
    public DisplayTypeInfo DisplayTypes { get; set; }
    public ContentInfo Content { get; set; }
    public string SourceTeamFolderName { get; set; }

    public string? PosterText { get; set; }             // From Presentation.PosterText
    public int DisplayDurationSeconds { get; set; }     // From Presentation.DisplayDurationSeconds
    public Schedule.DisplayMode ShowMode { get; set; } = Schedule.DisplayMode.ShowOnce;
    public bool UseVirtualDesktop { get; set; }
    public string VirtualDesktopBackgroundColor { get; set; } = "#000000";
}

public class ContentInfo
{
    public string ImagePath { get; set; }               // Relative path from team root
    public string? ImageUrl { get; set; }               // Azure Blob URL
    public string ImageHash { get; set; }               // SHA256 for cache invalidation
    public long ImageSizeBytes { get; set; }
    public string MoreInfoUrl { get; set; }
}

public class DisplayTypeInfo
{
    public bool IsNewsOfWeek { get; set; }
    public bool IsWallpaper { get; set; }
    public bool IsLogonScreen { get; set; }
}

public class IndexStatistics
{
    public int TotalPublishedAssignments { get; set; }
    public int ActiveAssignments { get; set; }
    public int UpcomingAssignments { get; set; }
    public int ExpiredAssignments { get; set; }
}
```

### 6.7 status.json (NewsService → NewsViewer)

Written by NewsService to `%programdata%\NewsCentral\` after each poll cycle.

```json
{
  "LastSyncTime": "2025-05-23T08:12:00Z",
  "IsOnline": true,
  "SyncSource": "Share"
}
```

`SyncSource` values: `Azure`, `Share`, `None`

### 6.8 viewerstate.json (NewsViewer internal)

Location: `%localappdata%\NewsCentral\viewerstate.json` (per-user, not machine-level).

```json
{
  "LastShownDate": "2025-05-23",
  "LastShownPresentationId": "abc-123"
}
```

### 6.9 servicestate.json (NewsService internal)

```json
{
  "LastWallpaperPresentationId": "abc-123",
  "LastLockscreenPresentationId": "def-456"
}
```

### 6.10 Session Telemetry (NewsViewer → uploads folder)

```json
{
  "SessionId": "guid",
  "PresentationId": "abc-123",
  "TeamId": "team_xy",
  "SessionStartTime": "2025-05-23T08:15:00Z",
  "SessionEndTime": "2025-05-23T08:15:34Z",
  "CloseReason": "Timeout",
  "Signature": null
}
```

`CloseReason` values: `Timeout`, `UserClose`, `UrlLaunch`

---

## 7. Cache Folder Structure

```
%programdata%\NewsCentral\              ← machine-level; shared across all users
├── team_xy\
│   ├── index.json                     TeamIndexFile
│   └── images\generated\              downloaded presentation images
├── team_xz\
│   └── ...
├── uploads\
│   └── session-{guid}.json            written by NewsViewer, uploaded by NewsService
├── status.json                        sync state — written by NewsService
└── servicestate.json                  wallpaper/lockscreen tracking — written by NewsService

%localappdata%\NewsCentral\             ← per-user; one copy per Windows user account
└── viewerstate.json                   display tracking — written by NewsViewer
```

`viewerstate.json` is intentionally per-user so that in multi-session environments (Citrix RDSH, Windows Server RDS) each user's shown-today state is independent. All other cache files remain machine-level.

Repository / network share mirrors the same team folder structure as the `%programdata%` cache.

---

## 8. Component Specifications

---

### 8.1 NewsCentral

**Type:** .NET 9 MAUI Blazor Hybrid desktop application  
**Status:** Active development. Admin page, assignments, publishing workflow, Azure blob distribution, and ShowMode/UseVirtualDesktop/VirtualDesktopBackgroundColor UI implemented.

#### Responsibilities

- Content creation: Presentations, Schedules, Assignments
- Approval and publishing workflow
- Team, user, and role management
- Index generation (`index.json` per team) via `IndexGenerationService`
- Uploading published content to the network repository (file share or Azure Blob, switchable)

#### Key Services

| Service | Purpose |
|---|---|
| `PresentationService` | CRUD for presentations |
| `AssignmentService` | Assignment lifecycle and approval workflow |
| `PublishingService` | Publishes approved assignments to the repository |
| `IndexGenerationService` | Generates and writes `index.json` for each team |
| `LocalStorageService` / `IStorageService` | File I/O abstraction |
| `LocalBlobDistributionService` / `AzureBlobDistributionService` | Distribution backends |
| `TeamContextService` | Current team scope for the session |
| `AuthenticationService` | Login, UPN detection, role resolution |
| `DataSeederService` | Seeds default admin/team on first run |

#### Key UI Pages

| Page | Path | Purpose |
|---|---|---|
| `EditPresentation.razor` | `/presentations/edit/{id}` | Edit name, description, URL, display types; set `UseVirtualDesktop` checkbox and `VirtualDesktopBackgroundColor` color picker; optionally generate a poster |
| `CreateAssignment.razor` | `/presentations/{id}/assign` | Set schedule dates, select target teams, choose `ShowMode` (ShowOnce / ShowNew), set approval requirement |

#### Storage Backend

- `DistributionMode` in `appsettings.json`: `Local` (file share) or `Azure` (Blob Storage)
- Authentication to Azure: interactive MSAL with token cache for subsequent non-interactive authentication

#### Role Model

- `SystemAdmin` — full access across all teams
- `ContentAuthor` — creates and edits content within their team; edit/delete restricted to creator or SystemAdmin

#### Folder Convention

Each team folder in the repository contains:
- `content\presentations\` — presentation JSON files
- `content\schedules\` — schedule JSON files
- `content\assignments\` — assignment JSON files
- `images\original\` — uploaded source images
- `images\generated\` — AI-generated or processed images (reserved)
- `index.json` — generated team index

---

### 8.2 NewsCentral.Shared

**Type:** .NET 9 class library  
**Status:** Implemented.

Contains all shared domain models. Referenced by NewsCentral, NewsService, and (future) NewsViewer and NewsTester. No platform dependencies — targets plain `net9.0` so it is compatible with any .NET 9 project regardless of platform target.

See [Section 4 — Solution Structure](#4-solution-structure) for the full file layout and [Section 6 — Data Model](#6-data-model) for all model definitions.

---

### 8.3 NewsService

**Type:** .NET 9 Windows Service (`Microsoft.NET.Sdk.Worker`)  
**Target:** `net9.0-windows10.0.19041.0`, `win-x64`  
**Status:** Implemented — full sync cycle operational in Share mode; Azure Blob mode implemented with Certificate / ClientSecret auth.

#### Configuration Resolution

`RegistryConfiguration` reads from `HKLM\Software\{Company}\{ApplicationName}\`. Registry values override `appsettings.json`:

| Registry value | Type | Effect |
|---|---|---|
| `teams` | `REG_SZ` | Semicolon-separated team folder names (e.g. `MY_TEAM;EXP_JP`) |
| `StorageMode` | `REG_SZ` | `Share` (default) or `Azure` |
| `PollIntervalSeconds` | `DWORD` | Overrides `Service:PollIntervalSeconds` |
| `AzureUploadEnabled` | `DWORD` | `1` to enable telemetry upload to Azure Blob |

`Company` and `ApplicationName` keys are read from `appsettings.json` and are not registry-overridable.

#### Poll Cycle — `SyncService.RunCycleAsync`

Executed by `Worker` on every interval tick:

**Step 1 — Index sync (per team)**
- Reads `{teamFolder}/index.json` from the repository
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
- Copies `uploads\session-*.json` from local cache to `{SharePath}\uploads\`
- Deletes the local copy after a successful copy

#### Storage Abstraction

`IRepositoryReader` has two implementations, selected at DI registration time based on effective storage mode:

| Implementation | Mode | Notes |
|---|---|---|
| `LocalShareRepositoryReader` | `Share` | Reads from UNC/local path; `IsAvailable` checks `Directory.Exists` |
| `AzureBlobRepositoryReader` | `Azure` | Certificate or ClientSecret auth (Section 5.6); reads index.json and images from blob container |

#### CacheManager — Hash Sidecar Pattern

When an image is written to cache, `CacheManager.WriteBytesAsync` also writes `{imagePath}.hash` containing `sha256:{hex}`. On the next cycle, `ReadStoredHash` reads this file instead of re-hashing the image, making per-image change detection O(1).

#### Wallpaper — `WallpaperService`

| Target | API | Session constraint |
|---|---|---|
| Desktop wallpaper | `IDesktopWallpaper` COM (`C2CF3110…`) — `SetWallpaper(null, path)` applies to all monitors | Requires desktop access; logs a warning and skips in session 0. Configure the service to run as the interactive user or trigger via Task Scheduler in the user session. |
| Lock screen | `PersonalizationCSP` registry keys (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP`) | Works from SYSTEM — no desktop access needed. Enterprise/MDM-grade mechanism used by Intune. |

#### Azure Authentication

- Machine certificate from local machine certificate store
- Proactive token refresh; exponential backoff on transient failures; cert-expiry logging
- Handles weeks-long uptime without restart

---

### 8.4 NewsViewer

**Type:** WinForms (.NET 9) desktop application  
**Status:** Phase 2 complete. All four Phase 2 features implemented and tested. NativeAOT migration path preserved; Win32 P/Invoke via `DllImport` with simple types — no unsafe code required.

#### Launch Conditions

- Registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for system startup
- Registered in Task Scheduler triggered by Workstation Unlock event
- Optionally launchable from the Windows Start menu

#### Display Mode

| Mode | Behaviour |
|---|---|
| `ShowOnce` | Show once per calendar day — on first system start or first workstation unlock |
| `ShowNew` | Same as `ShowOnce`, and additionally show when new content arrives via NewsService |

#### Presentation Selection

- Reads `index.json` from all team cache folders matching the `teams` registry configuration
- Selects the most recent active presentation by `PresentationLastModified` timestamp
- If no valid presentation found: **exit silently, no window shown**
- If no qualifying monitor (Full HD or better): **do not show the window**

#### Window Layout

```
┌─────────────────────────────────────────────┐
│                                             │
│           [Image — main area]               │  ← 1600×900 frame
│                                             │
│  [Label 1 — single-line comment text]       │
│                                             │  ← Online/Offline indicator (always visible)
│                                      [▶]    │  ← Side panel trigger (hover on right edge)
└─────────────────────────────────────────────┘
```

**Side panel** (revealed on hover over right edge):

| Control | Label | Action |
|---|---|---|
| Button 1 | `Close` | Close window; return to original desktop if on virtual desktop |
| Button 2 | `Click to see more information..` | Switch to original desktop; open `MoreUrl` in browser |
| Label 2 | Countdown: `N seconds` | Counts down from `DisplayDurationSeconds`; closes at zero |

#### Virtual Desktop

When `Presentation.UseVirtualDesktop = true`: creates a new Windows desktop via `CreateDesktop` / `SwitchDesktop` / `SetThreadDesktop`. Taskbar not visible. Background set to `VirtualDesktopBackgroundColor`. Auto-detected and suppressed in RDP / Citrix / VMware Horizon sessions.

#### Side Panel — Hover Trigger (Phase 2)

An 8px transparent `_pnlTrigger` strip is pinned to the right edge of the form. When the mouse enters it, `SlideIn()` sets `_targetX = FormWidth - SidePanelWidth` and starts `_slideTimer` (12 ms interval, 30 px per tick). The panel slides in from off-screen. `OnSidePanelMouseLeave` uses a `PointToClient` + `ClientRectangle.Contains` bounds check — moving between child controls does not falsely trigger slide-out. Mouse leaving the panel area calls `SlideOut()`.

#### ShowNew Mode — `ShowNewApplicationContext` (Phase 2)

When `assignment.ShowMode == Schedule.DisplayMode.ShowNew`, `Program.Main` creates a `ShowNewApplicationContext` and calls `Application.Run(context)` with no `MainForm`, keeping the message pump alive indefinitely. The context:

- Creates one `FileSystemWatcher` per team folder, watching `index.json` for `Changed`, `Created`, and `Renamed` events (covering both in-place saves and editor temp-file-rename patterns). The handler sets `volatile bool _indexChanged = true`.
- A `System.Windows.Forms.Timer` (3-second interval, fires on UI thread) polls the flag:
  1. **New-content check** — if `_indexChanged` was set and the selected `PresentationId` differs from `ViewerStateService.GetLastShownPresentationId()`: show the viewer.
  2. **Day-boundary check** — if `AlreadyShownToday` returns false (new calendar day or new presentation): show the viewer. `BypassShowOnceCheck` does **not** apply here — only applies to the startup gate in `Program.Main`.
- `_activeForm != null` guards against opening a second instance while one is already displayed.
- To terminate a resident ShowNew process: `taskkill /IM NewsViewer.exe /F` or Task Manager → Details → End Task.

#### Remote / Virtual Session Suppression (Phase 2)

Checked in `Program.Main` immediately after `HasQualifyingMonitor`, before any file I/O or window creation:

- **RDP and Citrix ICA** — detected via `SystemInformation.TerminalServerSession` (`GetSystemMetrics(SM_REMOTESESSION)`). Both RDP and Citrix ICA sessions set this flag.
- **VMware Horizon** — detected via the `ViewClient_Machine_Name` environment variable, which Horizon sets in every user session.

If either condition is true the process exits immediately, no window is shown, and no watcher is started.

#### Virtual Desktop (Phase 2 — ShowOnce only)

When `assignment.UseVirtualDesktop = true` and `ShowMode = ShowOnce`:

1. `Program.Main` spawns a **fresh STA thread** (`uiThread`) for all virtual-desktop UI. This is required because `Application.EnableVisualStyles()` and other WinForms startup calls on the main thread create hidden internal windows (the WinForms parking window, etc.). `SetThreadDesktop` silently returns `false` once a thread owns any window handle; using a fresh thread that has never touched WinForms guarantees the call succeeds.
2. On the new thread: `VirtualDesktopManager` is constructed — saves the original desktop handle (`GetThreadDesktop`) and creates a new named desktop (`CreateDesktop("NewsViewer", ...)`).
3. `SwitchToNew()` is called — `SwitchDesktop` makes the new desktop visible; `SetThreadDesktop` binds the new thread to it. **This must happen before any window handle is created on the thread.**
4. `BackgroundForm` (borderless, maximised, `VirtualDesktopBackgroundColor`) is shown, filling the new desktop.
5. `ViewerForm` is shown on top (`TopMost = true`).
6. On `ViewerForm.FormClosed`: `BackgroundForm` is closed first (still on new desktop context), then `SwitchToOriginal()` returns the user to the default desktop.
7. `VirtualDesktopManager.Dispose()` calls `CloseDesktop` to release the handle.
8. The main thread blocks on `uiThread.Join()` until the viewer closes, then the process exits.

**ShowNew + virtual desktop** — not supported. `ShowNewApplicationContext` creates a hidden `System.Windows.Forms.Timer` window before any `SwitchToNew()` call, which would cause `SetThreadDesktop` to fail. ShowNew presentations always display on the current desktop regardless of `UseVirtualDesktop`.

Win32 P/Invoke declarations are in `NativeMethods.cs` (`DllImport`, `CharSet.Unicode`, no unsafe blocks).

#### Session Telemetry

Writes `session-{guid}.json` to `%programdata%\NewsCentral\uploads\` at session end.

---

### 8.5 NewsTester

**Status:** Future requirement. Not yet designed.

- Independent standalone application
- Runs at standard interactive user level (not elevated)
- Shows prepared presentations as they will appear in NewsViewer
- Enables content authors and approvers to evaluate content before publishing

---

## 9. Communication

| Path | Mechanism |
|---|---|
| NewsCentral → Repository | File share write or Azure Blob upload (switchable) |
| NewsService → Repository | File share read/write or Azure Blob read/write (switchable) |
| NewsService → Cache | Local file write to `%programdata%\NewsCentral\` |
| NewsViewer → Cache | Local file read from `%programdata%\NewsCentral\` |
| NewsViewer → NewsService | Via `uploads\` folder (session telemetry JSON files) |
| NewsService → Azure | MS Azure Blob Storage via certificate authentication |

No direct inter-process communication between any components. All coordination is via the shared cache folder structure.

---

## 10. Security

### NewsCentral

- Detects UPN accounts and supports local accounts
- Azure Blob distribution uses `InteractiveBrowserCredential` (MSAL interactive user session); token persisted in named cache `"NewsCentral"` for non-interactive re-authentication. Config: `AzureBlob:{TenantId, ClientId, AccountName}` in appsettings.json.

### NewsService

- Azure Blob access uses `ClientCertificateCredential` or `ClientSecretCredential` (see Section 5.6); mode selected by `AzureBlob:AuthMode`
- Certificate loaded from `Cert:\LocalMachine\My` by thumbprint — Local System has access by default, no extra grants required
- Proactive token refresh via `Azure.Identity`; handles weeks-long uptime without restart

### NewsViewer

- Architecture is prepared for future authentication requirement to access `%programdata%\NewsCentral\` data
- No authentication implemented in this version

### Anti-Tamper (All Components)

- `Signature` field reserved in all JSON data structures (`Presentation`, `Schedule`, `Assignment`, `index.json`, session telemetry)
- Stub HMAC verification hook present in all JSON read paths (no-op in this version)
- Future implementation may extend scope (e.g. binary signing, cache file signing)

---

## 11. Operational Requirements

| Component | Requirement |
|---|---|
| NewsCentral | User-initiated start and shutdown; interactive Azure authentication |
| NewsService | Automatic start as Windows Service; runs unattended; handles weeks-long uptime |
| NewsViewer | Auto-start via HKLM Run and Task Scheduler (Workstation Unlock); optional Start menu entry |
| All | Configurable via `appsettings.json` with registry override; registry under `HKLM\Software\[Company]\[NewsCentral]\` |

---

## 12. Non-Goals

- No third-party UI frameworks for wallpaper or lockscreen management; Microsoft APIs only
- No peer-to-peer communication between components; all coordination via shared file system
- No real-time push from server to client; polling-based model throughout
- No Azure dependency required for local development and testing

---

## 13. Future Extensions

| Item | Notes |
|---|---|
| ~~NewsService Azure mode~~ | Implemented — `AzureBlobRepositoryReader` with Certificate / ClientSecret auth (Section 5.6). |
| ~~NewsCentral Azure distribution~~ | Implemented — `AzureBlobDistributionService` uses `InteractiveBrowserCredential` (MSAL interactive, token cached as `"NewsCentral"`). Config: `AzureBlob:{TenantId,ClientId,AccountName}`. |
| NewsCentral web application | May be rewritten as a web application or replaced by an existing portal |
| AI-assisted content generation | Folder structure (`original\`, `generated\`) already in place |
| NativeAOT for NewsViewer | Migration path preserved; Win32 P/Invoke usage kept compatible |
| NewsTester | Independent preview application for content authors and approvers |
| HMAC anti-tamper | `Signature` fields and verification stubs in place; full implementation deferred |
| Extended presentation selection logic | Current selection (most recent by timestamp) designed as an extensible function |
| NewsViewer authentication | Architecture prepared; not implemented in this version |
