# .NET 10 migration — pre-migration baseline

> **Temporary working document. Deleted at the end of the net10 migration (Phase 3).**
> Provenance of the canonical JSON fixture is preserved separately in
> `NewsCentral.Shared.Tests/Fixtures/CanonicalJson/net9-signed-index.meta.json`.

Captured at the Phase 1 commit (tagged `pre-net10`), on 2026-10-10.

## Toolchain

`dotnet --version`: `10.0.401`

`dotnet --list-sdks`:

```
9.0.318  [C:\Program Files\dotnet\sdk]
10.0.401 [C:\Program Files\dotnet\sdk]
```

`dotnet --list-runtimes` (Microsoft.NETCore.App / Microsoft.AspNetCore.App / Microsoft.WindowsDesktop.App
all carry the same three versions):

| Runtime set | Versions |
|---|---|
| Microsoft.NETCore.App | 8.0.31, 9.0.20, 10.0.12 |
| Microsoft.AspNetCore.App | 8.0.31, 9.0.20, 10.0.12 |
| Microsoft.WindowsDesktop.App | 8.0.31, 9.0.20, 10.0.12 |

`global.json`: **none** — the newest installed SDK (10.0.401) is resolved. The net9.0 projects therefore
build with the 10.0.401 SDK and run on the 9.0.20 runtime. The canonical JSON fixture was generated on
runtime 9.0.20 under this SDK (the generator asserts `Environment.Version.Major == 9`).

## Target frameworks

| Project | TargetFramework |
|---|---|
| NewsCentral | `net9.0-windows10.0.19041.0` (via `TargetFrameworks`, Windows only; other MAUI TFMs commented out) |
| NewsCentral.Shared | `net9.0` |
| NewsCentral.Shared.Tests | `net9.0` |
| NewsService | `net9.0-windows10.0.19041.0` |
| NewsService.Tests | `net9.0-windows10.0.19041.0` |
| NewsViewer | `net9.0-windows` |

`Directory.Build.props` / `Directory.Build.targets` set no TargetFramework or package versions.
No central package management (`Directory.Packages.props` absent).

## PackageReferences

### NewsCentral (`UseMaui` = true; MAUI Controls version comes from the SDK workload)

| Package | Version |
|---|---|
| Azure.Identity | 1.21.0 |
| Azure.Storage.Blobs | 12.28.0 |
| BCrypt.Net-Next | 4.2.0 |
| Microsoft.Extensions.Localization | 9.0.5 |
| Microsoft.Extensions.Logging.Debug | 9.0.5 |
| Microsoft.AspNetCore.Components.WebView.Maui (`Update`) | 10.0.60 |

(`Microsoft.Maui.Controls` 10.0.60 is present only as a commented-out reference.)

### NewsCentral.Shared

| Package | Version |
|---|---|
| Microsoft.Extensions.Configuration | 9.0.9 |
| Microsoft.Extensions.Configuration.Json | 9.0.9 |

### NewsCentral.Shared.Tests

| Package | Version |
|---|---|
| coverlet.collector | 6.0.2 |
| Microsoft.NET.Test.Sdk | 17.12.0 |
| xunit | 2.9.2 |
| xunit.runner.visualstudio | 2.8.2 |

### NewsService

| Package | Version |
|---|---|
| Azure.Identity | 1.21.0 |
| Azure.Storage.Blobs | 12.28.0 |
| Microsoft.Extensions.Hosting | 9.0.9 |
| Microsoft.Extensions.Hosting.WindowsServices | 9.0.9 |
| Microsoft.Extensions.Logging.EventLog | 9.0.9 |
| Microsoft.Graph | 6.2.0 |
| System.Net.Http.WinHttpHandler | 9.0.9 |

### NewsService.Tests

| Package | Version |
|---|---|
| coverlet.collector | 6.0.2 |
| Microsoft.NET.Test.Sdk | 17.12.0 |
| xunit | 2.9.2 |
| xunit.runner.visualstudio | 2.8.2 |

### NewsViewer

| Package | Version |
|---|---|
| Microsoft.Extensions.Configuration.Binder | 9.0.9 |
| Microsoft.Extensions.Configuration.Json | 9.0.9 |

## Test baseline

- `NewsCentral.Shared.Tests`: 394 passed, 1 skipped (the fixture generator), 0 failed.
- `NewsService.Tests`: 144 passed, 0 failed.
