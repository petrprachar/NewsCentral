# Solution Structure

## Projects in Solution

```
NewsCentral.sln
├── NewsCentral.Shared\          .NET 9 class library — shared domain models
├── NewsCentral.Shared.Tests\    .NET 9 xUnit test project — ECDSA signing core (9 facts, all passing)
├── NewsCentral\                 .NET 9 MAUI Blazor Hybrid — authoring app
├── NewsService\                 .NET 9 Windows Service — cache sync agent (implemented)
└── NewsViewer\                  .NET 9 WinForms — end-user presentation viewer (Phase 2 complete)
```

## Project References

```
NewsCentral.Shared   (no project references)
       ↑
       ├── NewsCentral
       ├── NewsService
       └── NewsViewer
```

NewsCentral and NewsService both reference **only** `NewsCentral.Shared`. NewsService does **not** reference NewsCentral (a MAUI project cannot be referenced from a non-MAUI project without MAUI build tasks propagating into the referencing project).

## NewsCentral.Shared — Model Layout

```
NewsCentral.Shared\
├── Models\
│   ├── IEntity.cs                     interface IEntity { GetId(); SetId(); }
│   ├── Presentation.cs                implements ISignable
│   ├── Schedule.cs                    implements ISignable
│   ├── Assignment.cs                  implements ISignable
│   ├── SessionTelemetry.cs            cross-component DTO; implements ISignable
│   ├── Team.cs                        Team, TeamsCollection
│   ├── TeamSigningKeys.cs             ECDSA key pair for a team: PrivateKey (PKCS#8), PublicKey, PublicKeyPrevious (SPKI)
│   ├── User.cs                        User, TeamRole, UsersCollection
│   └── IndexFile\
│       ├── TeamIndexFile.cs           root structure for index.json; implements ISignable
│       ├── PublishedAssignmentIndex.cs one entry per published assignment
│       ├── ContentInfo.cs             image path, hash, size, URL
│       ├── DisplayTypeInfo.cs         IsNewsOfWeek, IsWallpaper, IsLogonScreen
│       └── IndexStatistics.cs         summary counts
└── Security\
    ├── ISignable.cs                   interface ISignable { string? Signature { get; set; } }
    ├── HmacOptions.cs                 POCO: SecretKey (Base64 string)
    ├── HmacService.cs                 Sign<T>, Verify<T>, VerifyResult enum
    ├── EcdsaSignatureService.cs       stateless ECDSA P-256/SHA-256 Sign<T>/Verify<T>; IEEE P1363; key-list rotation
    └── SigningKeyTool.cs              GenerateKeyPair, DerivePublicKey, Truncate — key-management helpers
```

All model namespaces are `NewsCentral.Models` and `NewsCentral.Models.IndexFile` — identical to their previous location in the NewsCentral project, so no using-directive changes were required in NewsCentral when the shared library was extracted.

## NewsCentral.Shared — Configuration Layout

```
NewsCentral.Shared\
└── Configuration\
    ├── RegistryConfigurationProvider.cs   IConfigurationProvider/IConfigurationSource + AddRegistryOverrides() extension
    └── TeamConfigurationReader.cs         GetTeams(IConfiguration) helper
```

## NewsService — Service Layout

```
NewsService\
├── Configuration\
│   └── ServiceConfiguration.cs    typed POCOs bound from appsettings.json; includes HmacOptions
├── Models\
│   ├── StatusFile.cs              status.json structure
│   └── ServiceState.cs            servicestate.json structure
├── Services\
│   ├── IRepositoryReader.cs       abstraction over Share / Azure repository
│   ├── LocalShareRepositoryReader.cs  file share implementation (primary)
│   ├── AzureBlobRepositoryReader.cs   Azure implementation — Certificate / ClientSecret auth
│   ├── CacheManager.cs            all local cache I/O; SHA-256 sidecar hashes
│   ├── WallpaperService.cs        IDesktopWallpaper COM + PersonalizationCSP registry
│   ├── TelemetryUploader.cs       deserializes and HMAC-verifies session-*.json; forwards Valid/Unsigned, discards Invalid
│   └── SyncService.cs             orchestrates the poll cycle; HMAC-verifies index.json before caching
├── JsonDefaults.cs                shared JsonSerializerOptions (WriteIndented + CamelCase + CaseInsensitive + enum converter)
├── Worker.cs                      BackgroundService host; reads interval from configuration
├── Program.cs                     DI wiring; registers HmacService; adds registry override source; storage mode resolved from merged config
└── appsettings.json
```

## NewsViewer — Layout

```
NewsViewer\
├── Configuration\
│   └── ViewerConfiguration.cs        typed POCOs bound from appsettings.json; includes BypassShowOnceCheck, BypassImageIntegrityCheck, and HmacOptions
├── Models\
│   └── ViewerState.cs                viewerstate.json structure
│   (SessionTelemetry lives in NewsCentral.Shared — cross-component DTO)
├── Services\
│   ├── PresentationSelector.cs       reads index.json per team, HMAC-verifies, filters active, picks most recent, verifies image SHA-256
│   ├── ViewerStateService.cs         reads/writes viewerstate.json for ShowOnce/ShowNew tracking
│   ├── ShowNewApplicationContext.cs  ApplicationContext subclass; FileSystemWatcher + poll timer for ShowNew mode
│   ├── VirtualDesktopManager.cs      CreateDesktop/SwitchDesktop/SetThreadDesktop wrapper (ShowOnce only)
│   └── TelemetryWriter.cs            HMAC-signs and writes session-{guid}.json to uploads\ on close
├── Forms\
│   ├── ViewerForm.cs                 1810×954 borderless WinForms window; fixed Fluent gray side panel
│   ├── FluentControls.cs             FluentTheme palette + RoundedPanel/RoundedButton (custom-painted, square, hover/press states)
│   └── BackgroundForm.cs             fullscreen solid-colour background for virtual desktop
├── NativeMethods.cs                  Win32 P/Invoke — desktop, thread, process APIs
├── JsonDefaults.cs                   shared JsonSerializerOptions (same standard as NewsService)
├── Program.cs                        entry point; constructs HmacService; passes BypassImageIntegrityCheck to PresentationSelector; startup checks; remote session guard; branches on ShowMode
└── appsettings.json
```

## NewsCentral.Shared.Tests — Layout

```
NewsCentral.Shared.Tests\
└── EcdsaRoundTripTests.cs    round-trip: GenerateKeyPair → Sign → Verify (Valid / Invalid / Unsigned / Disabled / rotation fallback)
```

## JSON Serialization Convention

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
