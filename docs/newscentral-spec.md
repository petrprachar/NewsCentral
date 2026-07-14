# NewsCentral — Component Specification

**Type:** .NET 9 MAUI Blazor Hybrid desktop application  
**Status:** Active development. Admin page, assignments, publishing workflow, Azure blob distribution, UseVirtualDesktop/VirtualDesktopBackgroundColor UI, and Phase B2 Key Management page implemented.

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
| `IndexGenerationService` | Generates and writes `index.json` for each team; signs the index via `EcdsaSignatureService` using the team's private key from `team-signing.json`; missing key → published unsigned with a warning. Public methods: `LoadTeamSigningKeysAsync`, `SaveTeamSigningKeysAsync` (used by `KeyManagement.razor`). |
| `EcdsaSignatureService` | Signs `TeamIndexFile` with per-team ECDSA P-256; stateless singleton; index is normalized through `GetIndexJsonOptions` before signing |
| `HmacService` | Registered singleton; no longer used for index signing in NewsCentral — session telemetry signing is done by NewsViewer |
| `LocalStorageService` / `IStorageService` | File I/O abstraction |
| `LocalBlobDistributionService` / `AzureBlobDistributionService` | Distribution backends |
| `TeamContextService` | Current team scope for the session |
| `AuthenticationService` | Login, UPN detection, role resolution |
| `DataSeederService` | Seeds default admin/team on first run |

## Key UI Pages

| Page | Path | Purpose |
|---|---|---|
| `EditPresentation.razor` | `/presentations/edit/{id}` | Edit name, description, URL, display types; set `UseVirtualDesktop` checkbox (defaults to `true` for new presentations) and `VirtualDesktopBackgroundColor` color picker; optionally generate a poster |
| `CreateAssignment.razor` | `/presentations/{id}/assign` | Set schedule dates (default start = today, all 7 days selected), select target teams, set approval requirement. Display-type badges (News-of-Week / Wallpaper / Logon Screen) are shown **read-only** here — display types are set on the presentation (see `EditPresentation.razor`), never on the assignment. |
| `KeyManagement.razor` | `/key-management` | TeamAdmin-gated (SystemAdmin passes automatically). View current public key (full, copyable for GPO/registry deployment) and truncated private key hint. Generate a new ECDSA P-256 key pair (held in page state). Apply — sets `PublicKeyPrevious = old PublicKey` for rotation-window continuity and writes `team-signing.json` via `IStorageService` (never through `IBlobDistributionService`). Republish — separate deliberate action: re-signs and saves `index.json` via `GenerateAndSaveIndexAsync` (self-verify guard runs). Nav item hidden when no team is selected or user lacks TeamAdmin. Import-from-authority (paste/derive) deferred. |

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
- `team-signing.json` — ECDSA P-256 key pair (`TeamSigningKeys`); authoring tier only; never distributed via `IBlobDistributionService`

---

# NewsCentral.Shared — Component Specification

**Type:** .NET 9 class library  
**Status:** Implemented.

Contains all shared domain models. Referenced by NewsCentral, NewsService, and (future) NewsViewer and NewsTester. No platform dependencies — targets plain `net9.0` so it is compatible with any .NET 9 project regardless of platform target.

See `docs/solution-structure.md` for the full file layout and `docs/data-model.md` for all model definitions.
