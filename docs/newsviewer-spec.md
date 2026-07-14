# NewsViewer — Component Specification

**Type:** WinForms (.NET 9) desktop application  
**Status:** Phase 2 complete. All Phase 2 features implemented and tested; the side panel was reworked from a hover-reveal to a fixed Fluent gray panel in the v2.7 UI pass. Desktop wallpaper application added (user-session, `SystemParametersInfo` + HKCU; see Wallpaper Application). NativeAOT migration path preserved; Win32 P/Invoke via `DllImport` with simple types — no unsafe code required.

NewsViewer is a **one-shot process**: launch → evaluate the daily gate → render the poster (or not) → apply the wallpaper → exit. There is no resident process, no `FileSystemWatcher`, and no held-open message pump. Each display decision is made afresh at launch.

## Launch Conditions

- Registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for system startup
- Registered in Task Scheduler triggered by Workstation Unlock event
- Optionally launchable from the Windows Start menu
- `appsettings.json` must be declared in `NewsViewer.csproj` as `<Content Include="appsettings.json">` with `CopyToOutputDirectory = PreserveNewest` so it is deployed alongside the executable
- `BuildConfiguration()` uses `optional: false` for both `AddJsonFile` calls — a missing `appsettings.json` is a hard startup failure
- `Main()` validates `Company` and `CacheRootPath` after config load; if any are empty a `MessageBox` is shown and the process exits
- Registry `teams\` value names must match the generated folder name exactly — the sanitized team name with no `team-` prefix (e.g. `cz-its`, not `CZ_ITS`)

### Launch surface — two complementary triggers

The two triggers are **complementary, not redundant**:

- **HKLM `…\Run`** fires once at **session start** (logon). It does **not** fire on workstation unlock.
- **Task Scheduler / Workstation Unlock** fires on every **unlock** thereafter. It does **not** fire at logon.

On always-on machines that are locked rather than logged off, **Unlock is the only trigger** for every logical day after the first. There is deliberately **no repeating/timer trigger** in this release — re-evaluation is event-driven (logon or unlock), and the daily gate below decides whether each launch actually shows anything.

## The daily gate

The gate is **date-only** and evaluated once per launch. `ViewerStateService.AlreadyShownToday()` takes **no presentation id** — it disturbs the user at most once per **logical day**, regardless of which presentation is active.

A **logical day** runs from `Display:LogicalDayStartHour` (default `0` = calendar day) to the same hour the next day, in **local** time. `AlreadyShownToday()` compares the stored `LastShownDate` against the current logical day; `RecordShown` stamps the logical day after a display.

**Rationale.** A night-shift worker who starts at 22:00 and unlocks again at 01:00 is still inside the *same* logical day and must not be shown the poster twice. Worked example — `LogicalDayStartHour = 5` (05:00 boundary) cleanly covers morning, afternoon, and night shifts: everything from 05:00 through 04:59 the next morning counts as one day, so a night-shift unlock after midnight does not re-trigger.

### Deliberate gap — no same-day delivery (design decision)

Content published **after** the day's poster has already shown **waits for the next logical day**. There is **no same-day delivery path** in this release, and this is **intended** ("do not disturb users much") — it is a design decision, not an omission or a bug to be fixed. The reserved `Priority` field (see `docs/data-model.md`) is the future, author-controlled escape hatch for urgent same-day content.

## Presentation Selection

- Reads `index.json` from all team cache folders matching the `teams` registry configuration
- **ECDSA verification** — calls `EcdsaSignatureService.Verify(index, keys)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`: `Invalid` → skips the team entirely (no presentations shown from that team); `Unsigned` / `Disabled` / `Valid` → all logged distinctly, team accepted. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Selects the most recent active **News-of-the-Week** presentation by `PresentationLastModified` timestamp. Only `DisplayTypes.IsNewsOfWeek` assignments are eligible for the poster — a wallpaper-only / lock-screen-only assignment is never shown as a full-screen poster.
- Active-window/day filtering and the "newest by `PresentationLastModified`" pick are the shared pure helper `NewsCentral.Models.IndexFile.ActiveAssignmentSelector` (`IsActive` + `PickNewestActive(assignments, now, predicate)`), unit-tested in `NewsCentral.Shared.Tests`. `SelectActive` uses predicate `a => a.DisplayTypes.IsNewsOfWeek`; `SelectActiveWallpaper` uses `a => a.DisplayTypes.IsWallpaper`. The two selections are independent (an assignment flagged both shows a poster **and** sets the wallpaper). Both read from the same signature-verified index enumeration — neither bypasses verification.
- **Image integrity verification** — after the winning assignment is selected, computes SHA-256 of the cached image file and compares it against `Content.ImageHash` from the signed index. Missing hash → warning logged, continues. Mismatch → error logged, returns `(best, null)` so the caller declines to display/apply tampered content. Disabled by `BypassImageIntegrityCheck = true`.
- If no valid presentation found: **the poster is skipped** (but the wallpaper step still runs — see Wallpaper Application)
- If no qualifying monitor (Full HD or better): **do not show the poster window** (the wallpaper step is not gated by this)

## Window Layout

Borderless, top-most, centered frame sized **1810×954** = image area (1600 × 900)
+ 200px side panel + 44px caption bar, wrapped in a 5px solid gray frame.

┌─ 5px gray frame ─────────────────────────────┬──────────┐
│                                              │ ● Online │
│            Image — fixed size                │ [Close]  │
│            (Zoom, #606060 stage mat)         │ [More..] │
│                                              │ [card]   │
├──────────────────────────────────────────────┤          │
│  Caption bar — PosterText / PresentationName │          │
└──────────────────────────────────────────────┴──────────┘

The side panel is a **fixed, always-visible column** (the earlier hover-reveal /
slide-in trigger and the 8px trigger strip have been removed). Top to bottom:
Online/Offline indicator, `Close`, `Click to see more information..` (opens
`Content.MoreInfoUrl`), and the auto-close card.

### Auto-close card

A checkbox ("Form closes in") + countdown number + "seconds" label + a
progress bar, laid out in a `TableLayoutPanel` with auto-sizing rows (rows cannot
overlap regardless of font/DPI). The checkbox is checked by default; unchecking
**stops** the countdown and grays the number, unit, and bar; re-checking resumes
from the current value. Counts down from the **resolved** `DisplayDurationSeconds`
(see Display Duration below), closes at zero with reason `Timeout`.

## Display Duration

`DisplayDurationSeconds` carries three meanings via a sentinel model:

| Value | Meaning |
|---|---|
| `-1` | **Never auto-close** — RESERVED. No authoring UI can set it yet. |
| `0` | Unset → resolves to `PresentationDefaults.DisplayDurationSeconds` (**30**). |
| `> 0` | That many seconds. |

Resolution happens **once**, in `Program.cs`, via `PresentationDefaults.ResolveDuration`. `ViewerForm` performs **no** resolution of its own — it receives an already-resolved value. Any **negative value other than `-1`** is treated as unset (→ 30), **not** as never-close (the `NeverAutoClose` check precedes the unset check in `ResolveDuration`).

> **`-1` = never auto-close.** On a virtual desktop this is a modal takeover with the Close button as the sole exit. Any future UI exposing `-1` must address the VD interaction.

### Styling — FluentControls.cs

`NewsViewer/Forms/FluentControls.cs` defines `FluentTheme` (Windows light-gray
palette: #F0F0F0 surfaces, #E1E1E1 button faces, #ADADAD borders, #0078D7 accent,
black text) plus two custom-painted controls: `RoundedPanel` and `RoundedButton`
(`Radius = 0` → square; buttons have hover/press fill and a blue hover/press
border). All public properties carry
`[DesignerSerializationVisibility(Hidden)]` to satisfy analyzer WFO1000.

## Virtual Desktop (overview)

When `Presentation.UseVirtualDesktop = true`: creates a new Windows desktop via `CreateDesktop` / `SwitchDesktop` / `SetThreadDesktop`. Taskbar not visible. Background set to `VirtualDesktopBackgroundColor`. Auto-detected and suppressed in RDP / Citrix / VMware Horizon sessions.

## Remote / Virtual Session Suppression (Phase 2)

Checked in `Program.Main` immediately after `HasQualifyingMonitor`, before any file I/O or window creation:

- **RDP and Citrix ICA** — detected via `SystemInformation.TerminalServerSession` (`GetSystemMetrics(SM_REMOTESESSION)`). Both RDP and Citrix ICA sessions set this flag.
- **VMware Horizon** — detected via the `ViewClient_Machine_Name` environment variable, which Horizon sets in every user session.

If either condition is true the process exits immediately, no window is shown, and no watcher is started.

## Virtual Desktop — Full Details (Phase 2)

Virtual Desktop governs **how the poster is presented** — it does not gate or exclude the independent wallpaper and lock-screen applies. When `assignment.UseVirtualDesktop = true`:

1. `Program.Main` spawns a **fresh STA thread** (`uiThread`) for all virtual-desktop UI. This is required because `Application.EnableVisualStyles()` and other WinForms startup calls on the main thread create hidden internal windows (the WinForms parking window, etc.). `SetThreadDesktop` silently returns `false` once a thread owns any window handle; using a fresh thread that has never touched WinForms guarantees the call succeeds.
2. On the new thread: `VirtualDesktopManager` is constructed — saves the original desktop handle (`GetThreadDesktop`) and creates a new named desktop (`CreateDesktop("NewsViewer", ...)`).
3. `SwitchToNew()` is called — `SwitchDesktop` makes the new desktop visible; `SetThreadDesktop` binds the new thread to it. **This must happen before any window handle is created on the thread.**
4. `BackgroundForm` (borderless, maximised, `VirtualDesktopBackgroundColor`) is shown, filling the new desktop.
5. `ViewerForm` is shown on top (`TopMost = true`).
6. On `ViewerForm.FormClosed`: `BackgroundForm` is closed first (still on new desktop context), then `SwitchToOriginal()` returns the user to the default desktop.
7. `VirtualDesktopManager.Dispose()` calls `CloseDesktop` to release the handle.
8. The main thread blocks on `uiThread.Join()` until the viewer closes, then the process exits.

Win32 P/Invoke declarations are in `NativeMethods.cs` (`DllImport`, `CharSet.Unicode`, no unsafe blocks).

## Wallpaper Application

NewsViewer applies the **desktop wallpaper** in the user session (lock-screen application stays with NewsService). `NewsViewer/Services/WallpaperService.cs` is a thin, AOT-clean shell — **`SystemParametersInfo` + HKCU only, no COM/`IDesktopWallpaper`** (`DllImport` with simple blittable types + `Microsoft.Win32.Registry`, no extra packages, no unsafe code; matching `NativeMethods.cs`).

`bool SetWallpaper(string imagePath)`:
- `!File.Exists` → warning, returns `false`.
- Writes `HKCU\Control Panel\Desktop`: `WallpaperStyle`/`TileWallpaper` from the configured style (default **Fit** → `WallpaperStyle="6"`, `TileWallpaper="0"`; also Fill `10`/`0`, Stretch `2`/`0`, Center `0`/`0`, Tile `0`/`1`).
- Sets a uniform desktop background colour so Fit letterbox bars are even: `HKCU\Control Panel\Colors\Background = "R G B"` plus `SetSysColors(COLOR_DESKTOP)` (configurable, default `"0 0 0"`).
- `SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)`; returns its result.

**Selection** — `PresentationSelector.SelectActiveWallpaper(teams)` picks the newest active `DisplayTypes.IsWallpaper` assignment from the same signature-verified index read (never bypasses verification), returning `(assignment, resolvedImagePath)` or `(null, null)`.

**Control flow (terminal step, re-asserted every run, stateless — no viewerstate):**
- Skipped entirely on remote/virtual sessions (`IsRemoteOrVirtualSession`); otherwise runs on any local interactive session and is **not** gated by `HasQualifyingMonitor`.
- A "no active display assignment" (or no qualifying monitor) case does **not** exit the process — the poster is skipped and the wallpaper step still runs, then the process exits.
- Runs as the **terminal** step, after `ViewerForm` closes and the virtual desktop (if used) is switched back and destroyed — on the main thread / original desktop, never on the temporary VD.
- Decision: `intended` = wallpaper winner path; else `Delivery:DefaultWallpaperPath` if set & `File.Exists`; else `null`. `intended != null` → `SetWallpaper(intended)` (Information log `Wallpaper applied: {source} -> {path}` with source `presentation {id}, team {team}` or `default`; Error on `false`). `intended == null` → leave the current wallpaper untouched (sticky), Debug log. If the winner's image fails integrity verification (`wpPath == null`), it falls back to the default rather than applying unverified content.
- `Delivery:DefaultWallpaperPath` is applied on **every run** where no active `IsWallpaper` assignment exists; an **empty** value leaves the current wallpaper in place (**sticky**).

> **Test ritual — stale wallpaper impersonates fresh behaviour.** The wallpaper lives in **HKCU**, so it **survives a `%ProgramData%` cache wipe** — a wallpaper left over from an earlier test will look like fresh output of the run under test. Reset the wallpaper to a known neutral image **before each end-to-end run**.

Config (`Delivery` section, NewsViewer): `DefaultWallpaperPath` (default `""`), `WallpaperStyle` (default `Fit`), `WallpaperBackgroundColor` (default `"0 0 0"`). See `docs/configuration.md`.

## Session Telemetry

Writes `session-{guid}.json` to `%programdata%\NewsCentral\uploads\` at session end. The record is a `NewsCentral.Models.SessionTelemetry` instance (defined in `NewsCentral.Shared`); `TelemetryWriter` signs it via `HmacService` before serializing to disk.
