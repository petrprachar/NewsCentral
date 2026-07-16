# NewsCentral — Packaging & Installation Specification

## Purpose and scope

This document specifies what the installer(s) for the NewsCentral fleet components must do. It is
written for a packaging team producing an .msi and/or Intune (.intunewin) package. It describes
required actions and their parameters, not a specific packaging technology.

In scope — the two fleet components:

- **NewsService** — a Windows Service that syncs content to each machine.
- **NewsViewer** — a per-user desktop application that displays content.

Out of scope:

- **NewsCentral** — the authoring application. Installed separately on content-author machines only;
  not part of the fleet package.
- **NewsTester** — a planned preview tool, not yet built. Not in this package.

The two components may be delivered as one package with two feature groups, or two separate
packages. Two separate packages is recommended: the components install differently (machine-wide
service vs. per-user launch plumbing) and may be serviced independently.

## Runtime prerequisite (critical)

Both components are published **framework-dependent**. They do not contain the .NET runtime.

Every target machine must have the **.NET 9 Desktop Runtime (x64)** installed before these components
run. The Desktop runtime is required (not merely the base runtime) because NewsViewer is a WinForms
application; the Desktop runtime includes the base runtime that NewsService needs.

This is declared a **deployment prerequisite**, satisfied by the fleet's baseline image or a separate
managed runtime deployment. The package does not bundle or chain the runtime installer.

If the runtime is absent, failure is not graceful: NewsViewer shows a "runtime not found" dialog at
launch; NewsService fails to start. The packaging team should treat the runtime as a hard dependency
in whatever dependency mechanism their tooling offers.

Note for the support team: because the runtime is shared and machine-wide, a Windows/.NET security
update can service it without the components being repackaged. This is normally beneficial, but it
means runtime behaviour can change without a component redeploy.

## The Company value

The registry hive path and the scheduled-task path both embed a Company name:

- Registry: `HKLM\Software\{Company}\NewsCentral\{Component}`
- Scheduled task: `\{Company}\NewsCentral\NewsViewer`

`Company` is fixed at build time from `Directory.Build.props` in the source tree (surfaced in code as
`SolutionConstants.Company`). The packaging team must build from source so this value is available,
and must use the same value for the task-folder path. It is not a runtime setting and must not be
prompted for or defaulted independently — a mismatch causes silent failure (see Troubleshooting).

## Install location

Both components install to:

```
%ProgramFiles%\{Company}\NewsCentral\{Component}\
```

e.g. `C:\Program Files\Contoso\NewsCentral\NewsService\` and `...\NewsViewer\`.

## NewsService — installation

**Files:** deploy the framework-dependent publish output to
`%ProgramFiles%\{Company}\NewsCentral\NewsService\`. `appsettings.json` is included and must sit beside
the executable.

**Service registration:**

- Service name: `NewsService`
- Display name: `NewsCentral News Service`
- Account: LocalSystem
- Start type: Automatic (Delayed Start)

Delayed start keeps the service from racing network/proxy availability at boot.

**Recovery actions** — restart on unexpected termination (an unhandled exception, OOM, or host kill).
These do NOT fire for network changes; the service handles a changed proxy/network context at
runtime by retrying on its next poll cycle.

- First failure: restart after 1 minute
- Second failure: restart after 1 minute
- Subsequent failures: restart after 2 minutes
- Reset failure count: after 1 day

**EventLog source:** NewsService writes to the Windows Application log using source name `NewsService`.
The installer must register this EventLog source (creation requires the elevated installer context;
the service running as LocalSystem can write to it but must not be relied on to create it).
Log name: Application.

Start the service at the end of installation.

**NewsService — no configuration actions:** the installer does NOT write the component's runtime
configuration (team lists, signing public keys, Azure/proxy settings). That is provisioned
separately by Group Policy into `HKLM\Software\{Company}\NewsCentral\NewsService\`. The installer lays
down binaries and the service; GPO owns configuration. Keep this separation.

## NewsViewer — installation

**Files:** deploy the framework-dependent publish output to
`%ProgramFiles%\{Company}\NewsCentral\NewsViewer\`. `appsettings.json` is included and must sit beside
the executable. `appsettings.Development.json` is a developer-only file, excluded from the publish
output, and must never appear in the package.

NewsViewer runs per-user, in the interactive session, non-elevated. It is launched by two
complementary mechanisms.

1. **HKLM Run value** — fires once at each interactive logon, in the logging-on user's session:

   ```
   Key:   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run
   Name:  NewsViewer
   Value: "%ProgramFiles%\{Company}\NewsCentral\NewsViewer\NewsViewer.exe"
   ```

2. **Scheduled task** — fires on workstation unlock:

   - Task path: `\{Company}\NewsCentral\NewsViewer`
   - Trigger: on workstation unlock (session state change → SessionUnlock), any user
   - Principal: well-known SID `S-1-5-32-545` (the built-in Users group). MUST be the SID, not the
     string "Users", which is localized (e.g. `Uzivatele`) and fails to resolve at import on
     non-English Windows.
   - Logon type: interactive token (runs in the unlocking user's session)
   - Run level: least privilege — NOT "highest privileges"; elevation would detach the task from the
     user's desktop and break wallpaper application.
   - Repeating trigger: none in this release.

The two mechanisms do not overlap: Run fires at logon, the task fires at unlock. Together they give
a re-check at each natural session boundary. There is deliberately no repeating/timer trigger —
guaranteeing timely delivery of new content is the service's responsibility (it syncs content into
the cache); the viewer only needs to re-evaluate at logon and unlock.

**Task definition file:** the most robust way to create this task is to import a task-definition XML
(`schtasks /create /xml ...`). `schtasks` has historically been sensitive to file encoding — UTF-16 LE
with BOM is the safe choice.

NewsViewer writes no EventLog source and requires none.

## Uninstall

Both components — remove:

- All installed files under `%ProgramFiles%\{Company}\NewsCentral\{Component}\`.
- NewsService: stop and delete the `NewsService` Windows Service.
- NewsViewer: delete the `HKLM\...\Run\NewsViewer` value and the scheduled task
  `\{Company}\NewsCentral\NewsViewer`.
- The machine content cache under `%ProgramData%\NewsCentral\` (and its per-team subfolders).

Deliberately left in place (do NOT remove):

- **GPO-provisioned registry configuration** under `HKLM\Software\{Company}\NewsCentral\`. Group Policy
  owns this; it is removed or re-applied on GPO's own lifecycle. The installer must not touch it.
- **Per-user state** — `viewerstate.json` under each user's `%LOCALAPPDATA%\NewsCentral\`. An installer
  running as SYSTEM cannot cleanly reach every user profile, and leaving it is harmless: a reinstall
  picks it up without issue.
- **The NewsService EventLog source.** Removing an EventLog source is fussy and unnecessary; an orphaned
  source is conventional and harmless.

Scheduled-task folder: deleting the task leaves an empty `\{Company}\NewsCentral` task folder. Removing
it is optional; leaving it is harmless.

Task-delete must be tolerant: `schtasks /delete` returns non-zero if the task is already gone. The
uninstall action must not treat that as a failure.

## Upgrade (critical ordering requirement)

If a future version is delivered as an in-place upgrade, the old version's uninstall actions must
complete **before** the new version's install actions begin. In MSI terms: schedule
`RemoveExistingProducts` early (immediately after `InstallInitialize`).

The failure this prevents is silent: if the old product is removed after the new files and task are
laid down, the old uninstall's "delete scheduled task" action runs after the new install created
that task — deleting the task the new version just installed. NewsViewer would then have only the Run
value and no unlock trigger, with no error to indicate why. (For an Intune-only delivery the same
principle holds — fully remove before install — even though the `RemoveExistingProducts` term is
MSI-specific.)

## Field diagnostics (limitation in this release)

NewsViewer emits no runtime diagnostic logging in this release. When a user reports "I don't see any
news," the available troubleshooting is:

- verify the machine's GPO registry configuration under
  `HKLM\Software\{Company}\NewsCentral\NewsViewer\` (teams configured, signing public keys present,
  `Company` matches);
- clear the per-user gate by deleting `%LOCALAPPDATA%\NewsCentral\viewerstate.json` (forces a
  re-display on next launch).

EventLog logging for NewsViewer is a planned follow-up. When added, the installer will gain one
action: registering a `NewsViewer` EventLog source. The package structure should leave room for that.

## Troubleshooting — silent Company mismatch

`Company` defines the registry hive path and is not registry-overridable. If the value the components
were built with does not match the hive GPO wrote to, the lookup returns nothing with NO error: every
GPO override is silently ignored and the component runs on shipped defaults (NewsService finds no
teams and does nothing; NewsViewer shows nothing). If a freshly deployed machine behaves as though it
has no configuration, verify that the built-in `Company` value and the GPO hive path
`HKLM\Software\{Company}\NewsCentral\` use the identical company name.

## Summary of installer actions

**NewsService:** files → register service (LocalSystem, delayed-auto) → set recovery actions →
register EventLog source → start service.

**NewsViewer:** files → HKLM Run value → import scheduled task (unlock trigger, Users SID, least
privilege).

**Uninstall:** remove files, service, Run value, task, ProgramData cache; leave GPO config, per-user
state, and EventLog source.
