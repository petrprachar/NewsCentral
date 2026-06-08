# NewsViewer — Component Specification

**Type:** WinForms (.NET 9) desktop application  
**Status:** Phase 2 complete. All four Phase 2 features implemented and tested. NativeAOT migration path preserved; Win32 P/Invoke via `DllImport` with simple types — no unsafe code required.

## Launch Conditions

- Registered in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` for system startup
- Registered in Task Scheduler triggered by Workstation Unlock event
- Optionally launchable from the Windows Start menu
- `appsettings.json` must be declared in `NewsViewer.csproj` as `<Content Include="appsettings.json">` with `CopyToOutputDirectory = PreserveNewest` so it is deployed alongside the executable
- `BuildConfiguration()` uses `optional: false` for both `AddJsonFile` calls — a missing `appsettings.json` is a hard startup failure
- `Main()` validates `Company`, `ApplicationName`, and `CacheRootPath` after config load; if any are empty a `MessageBox` is shown and the process exits
- Registry `teams\` value names must use the full generated folder name including the `team-` prefix (e.g. `team-cz-its`, not `CZ_ITS`)

## Display Mode

| Mode | Behaviour |
|---|---|
| `ShowOnce` | Show once per calendar day — on first system start or first workstation unlock |
| `ShowNew` | Same as `ShowOnce`, and additionally show when new content arrives via NewsService |

## Presentation Selection

- Reads `index.json` from all team cache folders matching the `teams` registry configuration
- **HMAC verification** — calls `HmacService.Verify(index)` on each team's index: `Invalid` → skips the team entirely (no presentations shown from that team); `Unsigned` → logs warning and continues
- Selects the most recent active presentation by `PresentationLastModified` timestamp
- **Image integrity verification** — after the winning assignment is selected, computes SHA-256 of the cached image file and compares it against `Content.ImageHash` from the signed index. Missing hash → warning logged, continues. Mismatch → error logged, returns `(best, null)` so the caller exits silently rather than displaying tampered content. Disabled by `BypassImageIntegrityCheck = true`.
- If no valid presentation found: **exit silently, no window shown**
- If no qualifying monitor (Full HD or better): **do not show the window**

## Window Layout

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

## Virtual Desktop (overview)

When `Presentation.UseVirtualDesktop = true`: creates a new Windows desktop via `CreateDesktop` / `SwitchDesktop` / `SetThreadDesktop`. Taskbar not visible. Background set to `VirtualDesktopBackgroundColor`. Auto-detected and suppressed in RDP / Citrix / VMware Horizon sessions.

## Side Panel — Hover Trigger (Phase 2)

An 8px transparent `_pnlTrigger` strip is pinned to the right edge of the form. When the mouse enters it, `SlideIn()` sets `_targetX = FormWidth - SidePanelWidth` and starts `_slideTimer` (12 ms interval, 30 px per tick). The panel slides in from off-screen. `OnSidePanelMouseLeave` uses a `PointToClient` + `ClientRectangle.Contains` bounds check — moving between child controls does not falsely trigger slide-out. Mouse leaving the panel area calls `SlideOut()`.

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

## Session Telemetry

Writes `session-{guid}.json` to `%programdata%\NewsCentral\uploads\` at session end. The record is a `NewsCentral.Models.SessionTelemetry` instance (defined in `NewsCentral.Shared`); `TelemetryWriter` signs it via `HmacService` before serializing to disk.
