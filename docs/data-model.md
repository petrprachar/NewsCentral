# Data Model

All models are defined in `NewsCentral.Shared` and implement `IEntity`.

## IEntity

```csharp
public interface IEntity
{
    string GetId();
    void SetId(string id);
}
```

## Signing Infrastructure

`ISignable` (in `NewsCentral.Security`) marks any type whose JSON payload is covered by a signature. Two signing services exist; both live in `NewsCentral.Shared/Security/`.

```csharp
public interface ISignable { string? Signature { get; set; } }

public enum VerifyResult { Disabled, Unsigned, Valid, Invalid }
```

**Canonical serialization** (identical across both services): non-indented camelCase JSON with `JsonStringEnumConverter`; `Signature` temporarily nulled before serialization so it is excluded from its own payload.

**Verification behavior** (shared semantics):

| Result | Meaning | Action taken |
|---|---|---|
| `Disabled` | No key configured | Accept and pass through |
| `Unsigned` | `Signature` is null | Accept with a warning log |
| `Valid` | Signature matches | Accept |
| `Invalid` | Signature present but does not match | Reject — content discarded / team skipped |

### HmacService

Symmetric shared-key signing; used for session telemetry (`session-*.json`).

```csharp
public class HmacOptions { public string SecretKey { get; set; } = string.Empty; }

public sealed class HmacService
{
    public bool IsEnabled { get; }                    // false when SecretKey is empty
    public string? Sign<T>(T entity) where T : ISignable;
    public VerifyResult Verify<T>(T entity) where T : ISignable;
}
```

`SecretKey` is a Base64-encoded 32-byte key in `Hmac:SecretKey`. An empty key disables HMAC — `Sign` returns `null`, `Verify` returns `Disabled`. Timing-safe comparison via `CryptographicOperations.FixedTimeEquals`.

### EcdsaSignatureService

Stateless asymmetric signing; foundation for per-team `index.json` signing. Key material is passed per call.

```csharp
public sealed class EcdsaSignatureService
{
    // Returns Base64 IEEE P1363 (64-byte r‖s). Does not modify entity.Signature.
    public string Sign<T>(T entity, string privateKeyBase64Pkcs8) where T : ISignable;

    // Pass [PublicKey, PublicKeyPrevious] for rotation-window support.
    // No non-empty keys → Disabled. Null Signature → Unsigned. Any key match → Valid. All fail → Invalid.
    public VerifyResult Verify<T>(T entity, params string?[] publicKeysSpkiBase64) where T : ISignable;
}
```

Algorithm: ECDSA nistP256, SHA-256, IEEE P1363 fixed-field concatenation (64-byte r‖s, Base64). A key attempt that throws is treated as that key failing.

### SigningKeyTool

Static helpers for key management UI and offline tooling.

```csharp
public static class SigningKeyTool
{
    public static (string PrivateKeyBase64, string PublicKeyBase64) GenerateKeyPair();
    public static string DerivePublicKey(string privateKeyInput);   // PEM or Base64 PKCS#8; throws ArgumentException on bad input
    public static string Truncate(string? key, int head = 12, int tail = 6);
}
```

## Presentation

The core content unit distributed to NewsViewer.

```csharp
public class Presentation : IEntity, ISignable
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

    public string? Signature { get; set; }
}
```

## Schedule

```csharp
public class Schedule : IEntity, ISignable
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

    public string? Signature { get; set; }
}
```

`DisplayMode.ShowOnce` — show once per calendar day (first start or first unlock).  
`DisplayMode.ShowNew` — same as ShowOnce, and also when new content arrives.

## Assignment

```csharp
public class Assignment : IEntity, ISignable
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

    public string? Signature { get; set; }
}

public enum AssignmentStatus
{
    Draft, PendingApproval, Approved, Published, Cancelled, Rejected
}
```

## Team / User

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

## TeamSigningKeys

Stored at `{teamFolderName}/team-signing.json` on the authoring tier only. Must never be written through `IBlobDistributionService` or synced to client machines.

```csharp
public sealed class TeamSigningKeys
{
    public string? PrivateKey { get; set; }         // Base64 PKCS#8 — authoring side only
    public string? PublicKey { get; set; }           // Base64 SubjectPublicKeyInfo
    public string? PublicKeyPrevious { get; set; }   // Base64 SPKI; optional; retained for the rotation window
}
```

Trust assumption: team content authors are trusted, and their processes are granted access to the team private key by design. `PublicKey` and `PublicKeyPrevious` are distributed to verifying components (NewsService, NewsViewer); `PrivateKey` stays on the authoring tier.

## TeamIndexFile (index.json per team)

The `index.json` written to each team folder by NewsCentral and consumed by NewsService and NewsViewer.

```csharp
public class TeamIndexFile : ISignable, IDeliveredKeyCarrier
{
    public string TeamFolderName { get; set; }
    public string TeamName { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string IndexHash { get; set; }               // SHA256 of content for change detection
    public List<PublishedAssignmentIndex> PublishedAssignments { get; set; }
    public IndexStatistics Statistics { get; set; }
    public string? Signature { get; set; }              // ECDSA P-256 per-team (Phases B1+C complete)

    // Phase 3a — key-with-content: the team's public key (Base64 SubjectPublicKeyInfo, the same
    // form as the registry PublicKey) travels with the index for dynamic (Entra-resolved) teams.
    // [JsonIgnore(WhenWritingNull)] + null-and-restore in EcdsaSignatureService.Canonicalize
    // EXCLUDE it from the canonical signing input exactly like Signature, so the signature does
    // not cover this field and a null value leaves the canonical bytes bit-identical to the
    // pre-key form (every existing static-team signature still verifies).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SigningPublicKey { get; set; }
}

public class PublishedAssignmentIndex
{
    public string AssignmentId { get; set; }
    public string PresentationId { get; set; }
    public string ScheduleId { get; set; }
    public string PresentationName { get; set; }
    public string PresentationDescription { get; set; }
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
    public string SourceTeamName { get; set; }

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
    public DateTime ImageLastModified { get; set; }     // From Presentation.LastModified
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

## status.json (NewsService → NewsViewer)

Written by NewsService to `%programdata%\NewsCentral\` after each poll cycle.

```json
{
  "LastSyncTime": "2025-05-23T08:12:00Z",
  "IsOnline": true,
  "SyncSource": "Share"
}
```

`SyncSource` values: `Azure`, `Share`, `None`

## viewerstate.json (NewsViewer internal)

Location: `%localappdata%\NewsCentral\viewerstate.json` (per-user, not machine-level).

```json
{
  "LastShownDate": "2025-05-23",
  "LastShownPresentationId": "abc-123"
}
```

## servicestate.json (NewsService internal)

```json
{
  "LastWallpaperPresentationId": "abc-123",
  "LastLockscreenPresentationId": "def-456"
}
```

## ResolvedTeams (resolved-teams.json)

Model: `NewsCentral.Models.ResolvedTeamsFile` (in `NewsCentral.Shared`). Written by NewsService to `{CacheRootPath}\resolved-teams.json` each poll cycle; read by NewsViewer, which **unions** these entries with the registry static team list.

Lists **only** dynamic teams resolved from the device's Entra `extensionAttributes` (see `EntraTeamNameResolver`). It is a **local-tier file**, **unsigned**, and protected by cache ACLs. It carries no signature fields because dynamic-team `index.json` verification uses a public key **delivered with the team content** (key-with-content model) — the full key-delivery and verification mechanics land in Phases 2–3.

```json
{
  "GeneratedUtc": "2026-06-12T08:12:00Z",
  "Teams": [
    {
      "TeamFolderName": "cz-prague-its",
      "LastConfirmedUtc": "2026-06-12T08:12:00Z",
      "State": "Active"
    }
  ]
}
```

`State` values: `Active`, `Grace`. Serializes with the existing camelCase + `JsonStringEnumConverter` options used across NewsService/NewsViewer; no new serializer options are introduced.

## Session Telemetry (NewsViewer → uploads folder)

Model: `NewsCentral.Models.SessionTelemetry` (in `NewsCentral.Shared`; implements `ISignable`). Shared so both NewsViewer (writer) and NewsService (verifier) can deserialize and verify without model duplication.

```json
{
  "SessionId": "guid",
  "PresentationId": "abc-123",
  "TeamId": "cz-its",
  "SessionStartTime": "2025-05-23T08:15:00Z",
  "SessionEndTime": "2025-05-23T08:15:34Z",
  "CloseReason": "Timeout",
  "Signature": "base64-hmac-sha256"
}
```

`CloseReason` values: `Timeout`, `UserClose`, `UrlLaunch`

`Signature` is set by `TelemetryWriter` in NewsViewer immediately before writing the file. `TelemetryUploader` in NewsService verifies it before forwarding to the repository; files with `Invalid` signatures are discarded and logged.

## Cache Folder Structure

```
%programdata%\NewsCentral\              ← machine-level; shared across all users
├── cz-its\
│   ├── index.json                     TeamIndexFile
│   └── images\generated\              downloaded presentation images
├── de-prod\
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
