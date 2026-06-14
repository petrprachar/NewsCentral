# NewsViewer — Component Specification

**Type:** WinForms (.NET 9) desktop application  
**Status:** Phase 2 complete. All Phase 2 features implemented and tested; the side panel was reworked from a hover-reveal to a fixed Fluent gray panel in the v2.7 UI pass. Desktop wallpaper application added (user-session, `SystemParametersInfo` + HKCU; see Wallpaper Application). NativeAOT migration path preserved; Win32 P/Invoke via `DllImport` with simple types — no unsafe code required.

## Launch Conditions

- Registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for system startup
- Registered in Task Scheduler triggered by Workstation Unlock event
- Optionally launchable from the Windows Start menu
- `appsettings.json` must be declared in `NewsViewer.csproj` as `<Content Include="appsettings.json">` with `CopyToOutputDirectory = PreserveNewest` so it is deployed alongside the executable
- `BuildConfiguration()` uses `optional: false` for both `AddJsonFile` calls — a missing `appsettings.json` is a hard startup failure
- `Main()` validates `Company`, `ApplicationName`, and `CacheRootPath` after config load; if any are empty a `MessageBox` is shown and the process exits
- Registry `teams\` value names must match the generated folder name exactly — the sanitized team name with no `team-` prefix (e.g. `cz-its`, not `CZ_ITS`)

## Display Mode

| Mode | Behaviour |
|---|---|
| `ShowOnce` | Show once per calendar day — on first system start or first workstation unlock |
| `ShowNew` | Same as `ShowOnce`, and additionally show when new content arrives via NewsService |

## Presentation Selection

- Reads `index.json` from all team cache folders matching the `teams` registry configuration
- **ECDSA verification** — calls `EcdsaSignatureService.Verify(index, keys)` where `keys = SigningKeyConfigurationReader.GetPublicKeys(configuration, teamFolderName)`: `Invalid` → skips the team entirely (no presentations shown from that team); `Unsigned` / `Disabled` / `Valid` → all logged distinctly, team accepted. Index is deserialized with standard ISO timestamp parsing (no `DateTime` converter) so values match the signed, persisted form.
- Selects the most recent active presentation by `PresentationLastModified` timestamp
- Active-window/day filtering and the "newest by `PresentationLastModified`" pick are the shared pure helper `NewsCentral.Models.IndexFile.ActiveAssignmentSelector` (`IsActive` + `PickNewestActive(assignments, now, predicate)`), unit-tested in `NewsCentral.Shared.Tests`. `SelectActive` uses predicate `_ => true`; `SelectActiveWallpaper` uses `a => a.DisplayTypes.IsWallpaper`. Both read from the same signature-verified index enumeration — wallpaper selection never bypasses verification.
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
from the current value. Counts down from `DisplayDurationSeconds` (default 60),
closes at zero with reason `Timeout`.

### Styling — FluentControls.cs

`NewsViewer/Forms/FluentControls.cs` defines `FluentTheme` (Windows light-gray
palette: #F0F0F0 surfaces, #E1E1E1 button faces, #ADADAD borders, #0078D7 accent,
black text) plus two custom-painted controls: `RoundedPanel` and `RoundedButton`
(`Radius = 0` → square; buttons have hover/press fill and a blue hover/press
border). All public properties carry
`[DesignerSerializationVisibility(Hidden)]` to satisfy analyzer WFO1000.

## Virtual Desktop (overview)

When `Presentation.UseVirtualDesktop = true`: creates a new Windows desktop via `CreateDesktop` / `SwitchDesktop` / `SetThreadDesktop`. Taskbar not visible. Background set to `VirtualDesktopBackgroundColor`. Auto-detected and suppressed in RDP / Citrix / VMware Horizon sessions.

## ShowNew Mode — `ShowNewApplicationContext` (Phase 2)

When `assignment.ShowMode == Schedule.DisplayMode.ShowNew`, `Program.Main` creates a `ShowNewApplicationContext` and calls `Application.Run(context)` with no `MainForm`, keeping the message pump alive indefinitely. The context:

- Creates one `FileSystemWatcher` per team folder, watching `index.json` for `Changed`, `Created`, and `Renamed` events (covering both in-place saves and editor temp-file-rename patterns). The handler sets `volatile bool _indexChanged = true`.
- A `System.Windows.Forms.Timer` (3-second interval, fires on UI thread) polls the flag:
  1. **New-content check** — if `_indexChanged` was set and the selected `PresentationId` differs from `ViewerStateService.GetLastShownPresentationId()`: show the viewer.
  2. **Day-boundary check** — if `AlreadyShownToday` returns false (new calendar day or new presentation): show the viewer. `BypassShowOnceCheck` does **not** apply here — only applies to the startup gate in `Program.Main`.
- `_activeForm != null` guards against opening a second instance while one is already displayed.
- To terminate a resident ShowNew process: `taskkill /IM NewsViewer.exe /F` or Task Manager → Details → End Task.

## Remote / Virtual Session Suppression (Phase 2)

Checked in `Program.Main` immediately after `HasQualifyingMonitor`, before any file I/O or window creation:

- **RDP and Citrix ICA** — detected via `SystemInformation.TerminalServerSession` (`GetSystemMetrics(SM_REMOTESESSION)`). Both RDP and Citrix ICA sessions set this flag.
- **VMware Horizon** — detected via the `ViewClient_Machine_Name` environment variable, which Horizon sets in every user session.

If either condition is true the process exits immediately, no window is shown, and no watcher is started.

## Virtual Desktop — Full Details (Phase 2 — ShowOnce only)

When `assignment.UseVirtualDesktop = true` and `ShowMode = ShowOnce`:

1. `Program.Main` spawns a **fresh STA thread** (`uiThread`) for all virtual-desktop UI. This is required because `Application.EnableVisualStyles()` and other WinForms startup calls on the main thread create hidden internal windows (the WinForms parking window, etc.). `SetThreadDesktop` silently returns `false` once a thread owns any window handle; using a fresh thread that has never touched WinForms guarantees the call succeeds.
2. On the new thread: `VirtualDesktopManager` is constructed — saves the original desktop handle (`GetThreadDesktop`) and creates a new named desktop (`CreateDesktop("NewsViewer", ...)`).
3. `SwitchToNew()` is called — `SwitchDesktop` makes the new desktop visible; `SetThreadDesktop` binds the new thread to it. **This must happen before any window handle is created on the thread.**
4. `BackgroundForm` (borderless, maximised, `VirtualDesktopBackgroundColor`) is shown, filling the new desktop.
5. `ViewerForm` is shown on top (`TopMost = true`).
6. On `ViewerForm.FormClosed`: `BackgroundForm` is closed first (still on new desktop context), then `SwitchToOriginal()` returns the user to the default desktop.
7. `VirtualDesktopManager.Dispose()` calls `CloseDesktop` to release the handle.
8. The main thread blocks on `uiThread.Join()` until the viewer closes, then the process exits.

**ShowNew + virtual desktop** — not supported. `ShowNewApplicationContext` creates a hidden `System.Windows.Forms.Timer` window before any `SwitchToNew()` call, which would cause `SetThreadDesktop` to fail. ShowNew presentations always display on the current desktop regardless of `UseVirtualDesktop`. The `CreateAssignment` UI enforces this constraint: the ShowNew radio button is disabled (with an explanatory hint) when the selected presentation has `UseVirtualDesktop = true`.

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
- Runs as the **terminal** step: **ShowOnce** after `ViewerForm` closes and the virtual desktop (if used) is switched back and destroyed — on the main thread / original desktop, never on the temporary VD; **ShowNew** once, immediately **before** `Application.Run(showNewContext)` blocks (ShowNew is incompatible with VD, so the current desktop is correct — wallpaper is never driven from inside the resident context).
- Decision: `intended` = wallpaper winner path; else `Delivery:DefaultWallpaperPath` if set & `File.Exists`; else `null`. `intended != null` → `SetWallpaper(intended)` (Information log `Wallpaper applied: {source} -> {path}` with source `presentation {id}, team {team}` or `default`; Error on `false`). `intended == null` → leave the current wallpaper untouched (sticky), Debug log. If the winner's image fails integrity verification (`wpPath == null`), it falls back to the default rather than applying unverified content.

Config (`Delivery` section, NewsViewer): `DefaultWallpaperPath` (default `""`), `WallpaperStyle` (default `Fit`), `WallpaperBackgroundColor` (default `"0 0 0"`). See `docs/configuration.md`.

## Session Telemetry

Writes `session-{guid}.json` to `%programdata%\NewsCentral\uploads\` at session end. The record is a `NewsCentral.Models.SessionTelemetry` instance (defined in `NewsCentral.Shared`); `TelemetryWriter` signs it via `HmacService` before serializing to disk.
