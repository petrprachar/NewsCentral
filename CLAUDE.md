# NewsCentral — Solution Specification

**Version:** 1.0  
**Status:** Baseline for implementation  
**Scope:** NewsCentral, NewsService, NewsViewer, NewsTester

---

## Table of Contents

1. [Project Overview](#1-project-overview)
2. [Solution Architecture](#2-solution-architecture)
3. [Technologies](#3-technologies)
4. [Configuration Model](#4-configuration-model)
5. [Data Model](#5-data-model)
6. [Cache Folder Structure](#6-cache-folder-structure)
7. [Component Specifications](#7-component-specifications)
   - 7.1 [NewsCentral](#71-newscentral)
   - 7.2 [NewsService](#72-newsservice)
   - 7.3 [NewsViewer](#73-newsviewer)
   - 7.4 [NewsTester](#74-newstester)
8. [Communication](#8-communication)
9. [Security](#9-security)
10. [Operational Requirements](#10-operational-requirements)
11. [Non-Goals](#11-non-goals)
12. [Future Extensions](#12-future-extensions)

---

## 1. Project Overview

### Purpose

The solution provides a structured communication channel between content author teams and corporate end users. Content is prepared, approved, scheduled, and published by authors using **NewsCentral**. It is distributed to corporate workstations by **NewsService** and presented to users by **NewsViewer**.

### Components at a Glance

| Component | Role | Type |
|---|---|---|
| NewsCentral | Content authoring, approval, scheduling, publishing, user/team management | .NET MAUI Blazor Hybrid desktop app |
| NewsService | Cache agent — syncs content from repository to local machine, applies wallpaper/lockscreen, uploads telemetry | Windows Service |
| NewsViewer | End-user presentation layer — displays scheduled content from local cache | WinForms desktop app |
| NewsTester | Content preview tool for authors and approvers | Future — independent desktop app |

---

## 2. Solution Architecture

### Principles

- All components are configurable via `appsettings.json`. Selected values are additionally overridable via Windows registry (HKLM). Registry values take precedence over `appsettings.json`.
- Storage backend (local file share vs. Azure Blob Storage) is switchable via registry without code changes.
- Local/file share mode is the **primary development and testing configuration**. No Azure dependency is required for full functional testing of any component.
- The architecture is prepared for future anti-tamper protection via HMAC signatures.
- A viable migration path to NativeAOT is preserved for NewsViewer.

### Data Flow

```
[Network Repository]
  (File Share or Azure Blob)
        |
        | (sync, 300s cycle)
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

| Component | Technology |
|---|---|
| NewsCentral | C# / .NET MAUI Blazor Hybrid |
| NewsService | C# / .NET 8 (or .NET 9 if available) — Windows Service |
| NewsViewer | C# / .NET 8 (or .NET 9 if available) — WinForms; NativeAOT migration path preserved |
| NewsTester | C# — to be decided at design time |
| Data files | JSON throughout |
| Images | Encoded and embedded in presentation JSON files (base64 or suitable format) |
| Azure auth | MSAL (NewsCentral interactive); Machine certificate from local store (NewsService) |
| Network auth | MS Azure Storage via certificate (NewsService) |

---

## 4. Configuration Model

### 4.1 Registry Hive

All registry-configurable values reside under:

```
HKLM\Software\[Company]\[NewsCentral]\
```

The `[Company]` and `[NewsCentral]` placeholder strings are defined in `appsettings.json` and are **not** overridable via registry.

### 4.2 Precedence Rule

Registry values override `appsettings.json` values. If a registry value is absent, the `appsettings.json` value applies.

### 4.3 Registry Values

| Value Name | Type | Description | Default |
|---|---|---|---|
| `teams` | `REG_SZ` | Semicolon-separated list of team identifiers configured for this machine. Example: `"team_xy;team_xz"` | — |
| `StorageMode` | `REG_SZ` | Storage backend: `Share` or `Azure` | `Share` |
| `AzureUploadEnabled` | `DWORD` | Whether NewsService uploads telemetry to Azure Blob | `0` |
| `PollIntervalSeconds` | `DWORD` | NewsService polling interval in seconds | `300` |

### 4.4 appsettings.json Values (Examples)

```json
{
  "Company": "Contoso",
  "ApplicationName": "NewsCentral",
  "Repository": {
    "SharePath": "\\\\server\\newscontent"
  },
  "AzureStorage": {
    "AccountName": "",
    "ContainerName": ""
  }
}
```

---

## 5. Data Model

### 5.1 Presentation

The `Presentation` class represents a single content item — an image with accompanying text, a comment, and an optional URL. It is the core unit distributed to NewsViewer.

```csharp
public class Presentation
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string TeamId { get; set; }
    public string ImageData { get; set; }         // Base64-encoded image
    public string CommentText { get; set; }        // Short label shown under image
    public string? Url { get; set; }               // Optional URL for Button 2
    public PresentationStatus Status { get; set; } // Draft, Approved, Published
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string CreatedBy { get; set; }

    // Wallpaper / lockscreen
    public bool IsWallpaper { get; set; }
    public bool IsLogonScreen { get; set; }

    // Viewer display behaviour
    public int DisplayDurationSeconds { get; set; }       // Countdown timer; no registry default
    public bool UseVirtualDesktop { get; set; }           // Show in a new virtual desktop
    public string VirtualDesktopBackgroundColor { get; set; } = "#000000";

    // Anti-tamper — reserved for future HMAC implementation
    public string? Signature { get; set; }
}
```

### 5.2 Schedule

```csharp
public class Schedule
{
    public string Id { get; set; }
    public string PresentationId { get; set; }
    public string TeamId { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public DisplayMode ShowMode { get; set; } = DisplayMode.ShowOnce;
    public string? Signature { get; set; }        // Reserved for HMAC
}

public enum DisplayMode
{
    ShowOnce,   // Show once per day: first system start or first workstation unlock
    ShowNew     // ShowOnce behaviour + also show when new content arrives
}
```

### 5.3 Assignment

```csharp
public class Assignment
{
    public string Id { get; set; }
    public string PresentationId { get; set; }
    public string TeamId { get; set; }
    public string AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; }
    public string? Signature { get; set; }        // Reserved for HMAC
}
```

### 5.4 index.json (per team)

Each team folder in the repository and in the cache contains an `index.json` listing the current state of content for that team.

```json
{
  "TeamId": "team_xy",
  "UpdatedAt": "2025-05-23T08:00:00Z",
  "Presentations": [ { } ],
  "Schedules": [ { } ],
  "Assignments": [ { } ],
  "Signature": null
}
```

### 5.5 status.json (NewsService → NewsViewer)

Written by NewsService to `%programdata%\NewsCentral\` after each poll cycle.

```json
{
  "LastSyncTime": "2025-05-23T08:12:00Z",
  "IsOnline": true,
  "SyncSource": "Share"
}
```

`SyncSource` values: `Azure`, `Share`, `None`

### 5.6 viewerstate.json (NewsViewer internal)

Written and read by NewsViewer to track display history.

```json
{
  "LastShownDate": "2025-05-23",
  "LastShownPresentationId": "abc-123"
}
```

### 5.7 servicestate.json (NewsService internal)

Written and read by NewsService to track last-applied wallpaper and lockscreen.

```json
{
  "LastWallpaperPresentationId": "abc-123",
  "LastLockscreenPresentationId": "def-456"
}
```

### 5.8 Session Telemetry (NewsViewer → uploads folder)

One JSON file per session, written at session end.

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

## 6. Cache Folder Structure

```
%programdata%\NewsCentral\
├── team_xy\
│   ├── index.json
│   └── [presentation assets]
├── team_xz\
│   ├── index.json
│   └── [presentation assets]
├── uploads\
│   └── [session-{guid}.json ...]     ← written by NewsViewer, uploaded by NewsService
├── status.json                        ← sync state, written by NewsService
├── viewerstate.json                   ← display tracking, written by NewsViewer
└── servicestate.json                  ← wallpaper/lockscreen tracking, written by NewsService
```

Repository / network share mirrors the same team folder structure as the cache.

---

## 7. Component Specifications

---

### 7.1 NewsCentral

**Type:** .NET MAUI Blazor Hybrid desktop application  
**Status:** Prototyping phase complete. Serves as origin of data structures, roles, and publishing workflow.

#### Responsibilities

- Content creation: Presentations, Schedules, Assignments
- Approval and publishing workflow
- Team, user, and role management
- Uploading published content to the network repository (file share or Azure Blob, switchable)

#### Storage Backend

- Switchable between local file share and MS Azure Blob Storage via registry `StorageMode` value
- Authentication to Azure: interactive MSAL with token cache for subsequent non-interactive authentication

#### Role Model

- `SystemAdmin` — full access across all teams
- `ContentAuthor` — creates and edits content within their team; all team members see all team presentations; edit/delete restricted to creator or SystemAdmin

#### Folder Convention

Each team folder in the repository contains:
- `original\` — uploaded source images
- `generated\` — reserved for future AI-generated content

#### Future Readiness

- Prepared for replacement by a web application or portal
- Prepared for AI-assisted content generation (folder structure in place)

---

### 7.2 NewsService

**Type:** Windows Service (.NET 8/9)  
**Purpose:** Cache synchronisation agent, wallpaper/lockscreen manager, telemetry forwarder.

#### Startup and Polling

- Runs as a Windows Service, started automatically
- Executes its service routine on a configurable interval (default 300 seconds, registry: `PollIntervalSeconds`)
- On each cycle:
  1. Check `index.json` in repository for each configured team
  2. Retrieve updated content to local cache if changes detected
  3. Apply wallpaper and/or lockscreen if indicated by presentation flags
  4. Write `status.json` to cache root
  5. Process and upload session telemetry files from `uploads\`

#### Storage Abstraction

- Implements `IStorageBackend` interface with two concrete implementations:
  - `LocalShareStorageBackend` — operates on file share paths; fully functional without Azure
  - `AzureBlobStorageBackend` — operates against MS Azure Blob Storage
- Storage mode selected at runtime by registry `StorageMode` value
- **Local/share mode is the primary development and testing configuration.** No Azure credentials, SDK, or connectivity required when in share mode.

#### Azure Authentication

- Uses machine certificate enrolled in the local machine certificate store
- Proactive token refresh before expiry
- Retry with exponential backoff on transient failures
- Logs a warning when the certificate is approaching expiry
- Handles long-uptime scenarios (machine may run weeks without restart): token state maintained in memory with proactive refresh

#### Presentation Selection

- Reads `index.json` from all team folders configured in the `teams` registry value
- Selects the most recent active presentation by `UpdatedAt` timestamp across all teams
- Function is designed for extensibility — additional selection parameters may be added in future versions

#### Wallpaper and Lockscreen Management

- Triggered when a presentation has `IsWallpaper = true` or `IsLogonScreen = true`
- Extracts the image from the presentation JSON
- Applies wallpaper using the `IDesktopWallpaper` COM interface (Microsoft API only, no third-party libraries)
- Applies lockscreen image using the `LockScreen` WinRT API
- Tracks last-applied presentation IDs in `servicestate.json` to avoid reapplying on every poll
- Applied silently; action logged internally

#### Telemetry Upload

- On each poll cycle, processes JSON files present in `%programdata%\NewsCentral\uploads\`
- If `AzureUploadEnabled` registry value is set, uploads processed files to Azure Blob Storage
- In share mode, copies files to the repository uploads path

#### Anti-Tamper Readiness

- All JSON read operations include a stub HMAC verification hook (no-op in this version)
- `Signature` field is present in all data structures; reserved for future implementation

---

### 7.3 NewsViewer

**Type:** WinForms (.NET 8/9) desktop application  
**NativeAOT:** Migration path preserved; Win32 P/Invoke usage kept compatible.

#### Launch Conditions

- Registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for system startup
- Registered in Task Scheduler triggered by Workstation Unlock event
- Optionally launchable from the Windows Start menu

#### Display Mode

Determined by `Schedule.ShowMode` for the selected presentation:

| Mode | Behaviour |
|---|---|
| `ShowOnce` | Show once per calendar day — on first system start or first workstation unlock |
| `ShowNew` | Same as `ShowOnce`, and additionally show when new content arrives via NewsService |

Display history tracked in `viewerstate.json` (`LastShownDate`, `LastShownPresentationId`).

#### Presentation Selection

- Reads `index.json` from all team cache folders matching the `teams` registry configuration
- Selects the most recent active presentation by `UpdatedAt` timestamp
- If no valid presentation found in cache on startup: **exit silently, no window shown**

#### Change Detection

- `FileSystemWatcher` monitors:
  - `index.json` in each configured team cache folder
  - `status.json` in the cache root
- On `index.json` change: re-evaluate presentation selection; if a different (or newer) presentation is selected, switch content **in-place without closing and re-opening the window**; reset the countdown timer
- On `status.json` change: update the offline/online indicator

#### Display Requirements

- Window size: **1600 × 900 points**
- Target monitor: first monitor with a resolution of at least **Full HD (1920 × 1080)**
- If no qualifying monitor is found: **do not show the window**
- Multi-monitor: configurable selection is not required; first qualifying monitor by enumeration order is used

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

**Side panel** (revealed on hover over right edge or border):

| Control | Label | Action |
|---|---|---|
| Button 1 | `Close` | Close window; return to original desktop if on virtual desktop |
| Button 2 | `Click to see more information..` | Switch to original desktop; open browser at presentation URL |
| Label 2 | Countdown: `N seconds` | Counts down from `DisplayDurationSeconds`; window closes at zero |

#### Offline/Online Indicator

- Reads `status.json` from cache root
- Displays a simple visual indicator showing whether content is from a fresh sync (`IsOnline = true`) or from cache only (`IsOnline = false` or `SyncSource = None`)
- Updated dynamically when `status.json` changes

#### Countdown and Auto-Close

- Duration sourced from `Presentation.DisplayDurationSeconds` (no registry default)
- Countdown visible in Label 2 (side panel)
- Window closes automatically when counter reaches zero
- Counter resets to `DisplayDurationSeconds` when a new presentation is loaded in-place

#### Virtual Desktop

Controlled by `Presentation.UseVirtualDesktop`. When enabled:

- NewsViewer creates a new Windows desktop using Win32 APIs: `CreateDesktop`, `SwitchDesktop`, `SetThreadDesktop`
- Switches the current thread to the new desktop before showing the window
- Taskbar is **not visible** on the virtual desktop
- Background color of the new desktop is set to `Presentation.VirtualDesktopBackgroundColor` (hex string, e.g. `"#1A1A2E"`)

**Actions on the virtual desktop:**

| Action | Behaviour |
|---|---|
| Button 1 (Close) | Close window; switch back to original desktop immediately |
| Button 2 (URL) | Switch to original desktop immediately; open browser there |
| Counter reaches zero | Close window; switch back to original desktop immediately |

#### VDI / Citrix / Remote Session Detection

On startup, NewsViewer detects whether it is running in a remote or virtual session:

- `GetSystemMetrics(SM_REMOTESESSION)` — detects RDP / Terminal Services
- Presence of Citrix-specific environment variables or processes (e.g. `CtxSession`, `ICAClient`)
- VMware Horizon: process detection (e.g. `vmware-remotemks`)

If a remote/virtual session is detected: `UseVirtualDesktop` flag is **ignored**; the window is shown on the current desktop. Detection result is logged.

#### Session Telemetry

- At session end, writes a JSON file to `%programdata%\NewsCentral\uploads\`
- File named `session-{guid}.json`
- Contains: `SessionId`, `PresentationId`, `TeamId`, `SessionStartTime`, `SessionEndTime`, `CloseReason`
- `CloseReason` values: `Timeout`, `UserClose`, `UrlLaunch`

---

### 7.4 NewsTester

**Status:** Future requirement. Not yet designed.

**Recorded requirements:**
- Independent standalone application
- Runs at standard interactive user level (not elevated)
- Shows prepared presentations as they will appear in NewsViewer
- Enables content authors and approvers to evaluate content before publishing
- No further specification at this time

---

## 8. Communication

| Path | Mechanism |
|---|---|
| NewsCentral → Repository | File share write or Azure Blob upload (switchable) |
| NewsService → Repository | File share read/write or Azure Blob read/write (switchable) |
| NewsService → Cache | Local file write to `%programdata%\NewsCentral\` |
| NewsViewer → Cache | Local file read from `%programdata%\NewsCentral\` |
| NewsViewer → NewsService | Via `uploads\` folder (session telemetry JSON files) |
| NewsService → Azure | MS Azure Blob Storage via certificate authentication |

No direct inter-process communication between NewsService and NewsViewer. All coordination is via the shared cache folder structure.

---

## 9. Security

### NewsCentral

- Detects UPN accounts and supports local accounts
- Interactive MSAL authentication to Azure with token cache for subsequent non-interactive use

### NewsService

- Authenticates to MS Azure Storage using a certificate enrolled in the local machine certificate store
- Proactive token refresh; exponential backoff on failures; cert-expiry logging
- Handles long-uptime scenarios without requiring machine restart

### NewsViewer

- Architecture is prepared for future authentication requirement to access `%programdata%\NewsCentral\` data
- No authentication implemented in this version

### Anti-Tamper (All Components)

- `Signature` field reserved in all JSON data structures (`Presentation`, `Schedule`, `Assignment`, `index.json`, session telemetry)
- Stub HMAC verification hook present in all JSON read paths (no-op in this version)
- Future implementation may extend scope (e.g. binary signing, cache file signing)

---

## 10. Operational Requirements

| Component | Requirement |
|---|---|
| NewsCentral | User-initiated start and shutdown; interactive Azure authentication |
| NewsService | Automatic start as Windows Service; runs unattended; handles weeks-long uptime |
| NewsViewer | Auto-start via HKLM Run and Task Scheduler (Workstation Unlock); optional Start menu entry |
| All | Configurable via `appsettings.json` with registry override; registry under `HKLM\Software\[Company]\[NewsCentral]\` |

---

## 11. Non-Goals

- No third-party UI frameworks for wallpaper or lockscreen management; Microsoft APIs only
- No peer-to-peer communication between components; all coordination via shared file system
- No real-time push from server to client; polling-based model throughout
- No Azure dependency required for local development and testing

---

## 12. Future Extensions

| Item | Notes |
|---|---|
| NewsCentral web application | NewsCentral may be rewritten as a web application or replaced by an existing portal |
| AI-assisted content generation | Folder structure (`original\`, `generated\`) already in place |
| NativeAOT for NewsViewer | Migration path preserved; Win32 P/Invoke usage kept compatible |
| NewsTester | Independent preview application for content authors and approvers |
| HMAC anti-tamper | `Signature` fields and verification stubs in place; full implementation deferred |
| Extended presentation selection logic | Current selection (most recent by timestamp) designed as an extensible function |
| NewsViewer authentication | Architecture prepared; not implemented in this version |
| Microsoft Entra ID (Azure AD) hybrid authentication | Architectural plan established for NewsCentral; phased migration path defined |
