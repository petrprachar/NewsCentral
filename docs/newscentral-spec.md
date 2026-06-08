# NewsCentral — Component Specification

**Type:** .NET 9 MAUI Blazor Hybrid desktop application  
**Status:** Active development. Admin page, assignments, publishing workflow, Azure blob distribution, and ShowMode/UseVirtualDesktop/VirtualDesktopBackgroundColor UI implemented.

## Responsibilities

- Content creation: Presentations, Schedules, Assignments
- Approval and publishing workflow
- Team, user, and role management
- Index generation (`index.json` per team) via `IndexGenerationService`
- Uploading published content to the network repository (file share or Azure Blob, switchable)

## Key Services

| Service | Purpose |
|---|---|
| `PresentationService` | CRUD for presentations |
| `AssignmentService` | Assignment lifecycle and approval workflow |
| `PublishingService` | Publishes approved assignments to the repository |
| `IndexGenerationService` | Generates and writes `index.json` for each team; signs the index via `HmacService` |
| `HmacService` | Signs `TeamIndexFile` after `IndexHash` is set; singleton wired from `AppConfiguration.HmacSecretKey` |
| `LocalStorageService` / `IStorageService` | File I/O abstraction |
| `LocalBlobDistributionService` / `AzureBlobDistributionService` | Distribution backends |
| `TeamContextService` | Current team scope for the session |
| `AuthenticationService` | Login, UPN detection, role resolution |
| `DataSeederService` | Seeds default admin/team on first run |

## Key UI Pages

| Page | Path | Purpose |
|---|---|---|
| `EditPresentation.razor` | `/presentations/edit/{id}` | Edit name, description, URL, display types; set `UseVirtualDesktop` checkbox (defaults to `true` for new presentations) and `VirtualDesktopBackgroundColor` color picker; optionally generate a poster |
| `CreateAssignment.razor` | `/presentations/{id}/assign` | Set schedule dates (default start = today, all 7 days selected), select target teams, choose `ShowMode`, set approval requirement. ShowNew option is disabled (greyed out with hint) when the presentation has `UseVirtualDesktop = true`. Wallpaper and Logon Screen display-type badges are dimmed with a hint when `UseVirtualDesktop` is set, because those modes are incompatible with Virtual Desktop. |

## Storage Backend

- `DistributionMode` in `appsettings.json`: `Local` (file share) or `Azure` (Blob Storage)
- Authentication to Azure: interactive MSAL with token cache for subsequent non-interactive authentication

## Role Model

- `SystemAdmin` — full access across all teams
- `ContentAuthor` — creates and edits content within their team; edit/delete restricted to creator or SystemAdmin

**Assignments are immutable after creation** — there is no edit path. To change a scheduled assignment, delete it and create a new one.

## Folder Convention

Each team folder in the repository contains:
- `content\presentations\` — presentation JSON files
- `content\schedules\` — schedule JSON files
- `content\assignments\` — assignment JSON files
- `images\original\` — uploaded source images
- `images\generated\` — AI-generated or processed images (reserved)
- `index.json` — generated team index

---

# NewsCentral.Shared — Component Specification

**Type:** .NET 9 class library  
**Status:** Implemented.

Contains all shared domain models. Referenced by NewsCentral, NewsService, and (future) NewsViewer and NewsTester. No platform dependencies — targets plain `net9.0` so it is compatible with any .NET 9 project regardless of platform target.

See `docs/solution-structure.md` for the full file layout and `docs/data-model.md` for all model definitions.
