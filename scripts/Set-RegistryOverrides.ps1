#Requires -RunAsAdministrator
# ============================================================================
#  SCOPE — DEVELOPER AND PILOT MACHINES ONLY
#
#  This script provisions a SUBSET of the registry surface, for dev boxes and
#  pilot machines. It is NOT the fleet provisioning mechanism: in production,
#  Group Policy owns everything under HKLM\Software\<Company>\NewsCentral\
#  (see docs/packaging.md — the installer must not write configuration).
#
#  AUTHORITATIVE REFERENCES — this script does not supersede either:
#    docs/configuration.md          full registry surface, per component
#    docs/production-deployment.md  ordered deployment sequence
#
#  NOT WRITTEN by this script (write by hand or by GPO):
#    NewsCentral component keys (DataPath, LockExpirationMinutes, Storage\, ...)
#    NewsTester component keys (reserved; not yet defined)
#
#  WARNING — numeric values must NEVER be written as -Type DWord.
#  RegistryConfigurationProvider coerces REG_DWORD 0 -> "False" and 1 -> "True",
#  after which the configuration binder throws converting "False"/"True" to int
#  and crashes the component at startup. Write every int-valued key
#  (PollIntervalSeconds, GracePeriodMinutes, LockExpirationMinutes,
#  LogicalDayStartHour, MaxDynamicTeams, ...) as -Type String. Genuine booleans
#  as DWord are fine — the coercion exists for them.
# ============================================================================
<#
.SYNOPSIS
    Configures registry overrides for a single NewsCentral component.

.DESCRIPTION
    Writes values under HKLM\Software\<Company>\NewsCentral\<ComponentName>\ using
    the subkey layout expected by RegistryConfigurationProvider.  Any value not
    supplied is left unchanged (or omitted when creating the key for the first
    time).  Run with -WhatIf to preview without writing.

    Registry layout written by this script (NewsService example)
    ─────────────────────────────────────────────────────────────
    HKLM\Software\<Company>\NewsCentral\NewsService\
    ├── Service\
    │       PollIntervalSeconds   REG_SZ   (int poll interval in seconds — REG_SZ, never DWORD)
    │       CacheRootPath         REG_SZ   (overrides Service:CacheRootPath)
    ├── Repository\
    │       StorageMode   REG_SZ   ("Share" or "Azure")
    │       SharePath     REG_SZ   (UNC or local path to the file-share repository)
    ├── AzureBlob\
    │       AuthMode               REG_SZ   ("Certificate", "ClientSecret", or "ClientSecretEnv")
    │       TenantId               REG_SZ
    │       ClientId               REG_SZ
    │       AccountName            REG_SZ   (storage account name, no .blob.core.windows.net)
    │       ContainerName          REG_SZ
    │       CertificateThumbprint  REG_SZ   (AuthMode=Certificate)
    │       ClientSecret           REG_SZ   (AuthMode=ClientSecret)
    │       UseWinHttpProxy        REG_SZ   "true" or "false"
    ├── Signing\
    │       RequireSignedIndex     REG_SZ   "true" or "false"
    │       <teamFolderName>\
    │           PublicKey          REG_SZ   (Base64 SPKI)
    │           PublicKeyPrevious  REG_SZ   (Base64 SPKI; optional)
    ├── Entra\
    │       Enabled                REG_SZ   "true" or "false"
    │       GracePeriodMinutes     REG_SZ
    │       MaxDynamicTeams        REG_SZ
    │       AttributeSchemes\<scheme>\Selector, Mappings\<value>
    │       GroupTeams\ExclusionGroup, Instances\<label>\InclusionGroup, ExclusionGroup
    ├── Delivery\
    │       DefaultLockScreenPath  REG_SZ
    │       PublishedImagePath     REG_SZ   (protected folder for applied display images; must not be user-writable)
    ├── Telemetry\
    │       UploadEnabled          DWORD
    ├── Logging\
    │       LogLevel\Default               REG_SZ
    │       EventLog\LogLevel\Default      REG_SZ
    ├── Hmac\
    │       SecretKey   REG_SZ   (Base64-encoded 32-byte HMAC key; empty string = HMAC disabled)
    └── teams\
            <teamFolderName>   REG_SZ ""   (one value per team; name = folder name; data ignored)

    HKLM\Software\<Company>\NewsCentral\NewsViewer\
    │   CacheRootPath                REG_SZ    (overrides ViewerConfiguration.CacheRootPath)
    │   Active                       DWORD     (0 = NewsViewer exits at startup with no action; default 1)
    │   BypassDailyGate             DWORD     (1 = skip once-per-day gate at startup)
    │   BypassImageIntegrityCheck    DWORD     (1 = skip image SHA-256 verification)
    ├── Display\
    │       LogicalDayStartHour     REG_SZ
    ├── Ui\
    │       Theme                   REG_SZ   "Dark" or "Light"
    ├── Delivery\
    │       DefaultWallpaperPath        REG_SZ
    │       WallpaperStyle              REG_SZ
    │       WallpaperBackgroundColor    REG_SZ
    ├── Signing\
    │       RequireSignedIndex      REG_SZ   "true" or "false"
    │       <teamFolderName>\PublicKey, PublicKeyPrevious
    ├── Hmac\
    │       SecretKey   REG_SZ
    └── teams\
            <teamFolderName>   REG_SZ ""

    Each component reads only its own subkey; values set for one component do
    not affect another.  Company must match Directory.Build.props (SolutionConstants.Company)
    exactly — it defines the registry path and is never registry-overridable.

.PARAMETER Company
    Mandatory, no default. Must match Directory.Build.props (SolutionConstants.Company) exactly —
    it defines the registry hive path, is NOT registry-overridable, and a mismatch fails silently
    (OpenSubKey returns null), quietly discarding every override.

.PARAMETER ComponentName
    The component whose registry subkey to write.
    Must be one of: NewsCentral, NewsService, NewsViewer, NewsTester.
    Only NewsService and NewsViewer accept the parameters added for the fleet registry surface
    (Signing, Entra, Delivery, Display, Ui, Telemetry, Logging, AzureBlob:UseWinHttpProxy) — binding
    any of them against NewsCentral or NewsTester is a terminating error.

.PARAMETER Teams
    Array of team folder names to register.  Existing teams not in this list
    are removed from the teams\ subkey.

.PARAMETER StorageMode
    Repository storage backend: Share or Azure. NewsService only.

.PARAMETER SharePath
    UNC or local path to the file-share repository (StorageMode=Share). NewsService only.

.PARAMETER PollIntervalSeconds
    NewsService polling interval in seconds. NewsService only.

.PARAMETER CacheRootPath
    Local cache root used by both NewsService and NewsViewer.
    Default: C:\ProgramData\NewsCentral

.PARAMETER Active
    Per-machine master switch. Default true. When $false, NewsViewer exits at startup with no
    action taken: no poster, no wallpaper apply, no viewerstate write, no telemetry. The
    last-applied wallpaper is left as-is (not reverted). NewsViewer only.

.PARAMETER BypassDailyGate
    When $true, NewsViewer skips the once-per-logical-day display gate.
    Useful for repeated test runs. NewsViewer only.

.PARAMETER BypassImageIntegrityCheck
    When $true, NewsViewer skips SHA-256 verification of the cached image file.
    Useful when testing with manually replaced images. NewsViewer only.

.PARAMETER HmacSecretKey
    Base64-encoded 32-byte HMAC-SHA256 key shared across all components.
    An empty string disables HMAC verification system-wide (phased rollout default).
    Generate with: [Convert]::ToBase64String((1..32 | ForEach-Object { [byte](Get-Random -Max 256) }))

.PARAMETER AzureTenantId
    Azure AD tenant ID (StorageMode=Azure).

.PARAMETER AzureClientId
    Azure AD app/client ID (StorageMode=Azure).

.PARAMETER AzureAccountName
    Azure Storage account name without the .blob.core.windows.net suffix.

.PARAMETER AzureContainerName
    Azure Blob container name. Default: newscentral

.PARAMETER AzureAuthMode
    Certificate, ClientSecret, or ClientSecretEnv.
    ClientSecretEnv reads the secret from the machine-scope environment variable
    NEWSSERVICE_AZURE_CLIENTSECRET — this script does not and must not set it.

.PARAMETER AzureCertificateThumbprint
    Certificate thumbprint in Cert:\LocalMachine\My (AzureAuthMode=Certificate).

.PARAMETER AzureClientSecret
    Client secret string (AzureAuthMode=ClientSecret).

.PARAMETER AzureUseWinHttpProxy
    When $true, routes Azure Blob and Microsoft Graph traffic through the machine
    WinHTTP proxy (AzureProxyTransportFactory) instead of the default per-user WinINet
    resolution — use when NewsService cannot reach Azure/Graph under Local System.

.PARAMETER RequireSignedIndex
    When $true, index.json verification rejects Unsigned and Disabled results in
    addition to Invalid (fail-closed). Default false. NewsService and NewsViewer.

.PARAMETER TeamPublicKeys
    Hashtable of team folder name -> Base64 SubjectPublicKeyInfo. Authoritatively
    replaces every per-team subkey under Signing\ — a team omitted from this
    hashtable when the parameter is bound loses its pinned key. RequireSignedIndex
    (a sibling value at Signing\'s root) is preserved across the replace.

.PARAMETER TeamPreviousPublicKeys
    Hashtable of team folder name -> Base64 SubjectPublicKeyInfo, written as
    PublicKeyPrevious for each team's rotation window.

.PARAMETER EntraEnabled
    Gates the whole Entra dynamic-team feature. Default false. NewsService only.

.PARAMETER EntraGracePeriodMinutes
    Minutes a dynamic team's last resolved state is retained while Graph is
    transiently unreachable. 0 = no grace (deliberate and valid). NewsService only.

.PARAMETER EntraMaxDynamicTeams
    Cap on total dynamic teams written across both sources. 0 = no cap. NewsService only.

.PARAMETER EntraGlobalExclusionGroup
    Fleet-wide group display name; membership suppresses every group-team instance.
    Fails closed if unresolvable. NewsService only.

.PARAMETER EntraGroupInstances
    Hashtable of instance label -> @{ InclusionGroup = "..."; ExclusionGroup = "..." }
    (ExclusionGroup optional). Authoritatively replaces every subkey under
    Entra\GroupTeams\Instances\. Entra\GroupTeams\ExclusionGroup (the fleet-wide
    kill switch, a sibling value) is preserved across the replace. NewsService only.

.PARAMETER EntraAttributeSchemes
    Hashtable of scheme name -> @{ Selector = "extensionAttributeN"; Mappings = @{ value = "rule" } }.
    Authoritatively replaces every subkey under Entra\AttributeSchemes\. NewsService only.

.PARAMETER DefaultLockScreenPath
    Absolute, SYSTEM-readable path to a default lock-screen image; "" = no default
    (sticky). NewsService only.

.PARAMETER PublishedImagePath
    Absolute path to the protected folder ImagePublisher copies applied display images into
    before PersonalizationCSP is pointed at them; must not be user-writable, or the protection
    this provides is void. Default C:\Windows\Web\NewsCentral. NewsService only.

.PARAMETER DefaultWallpaperPath
    Absolute path to a default wallpaper; "" = leave current wallpaper (sticky).
    NewsViewer only.

.PARAMETER WallpaperStyle
    Fill, Fit, Stretch, Center, or Tile. Default Fit. NewsViewer only.

.PARAMETER WallpaperBackgroundColor
    "R G B" desktop background color for Fit letterbox bars. Default "0 0 0". NewsViewer only.

.PARAMETER LogicalDayStartHour
    Hour (0-23, local time) at which the logical day for the once-per-day poster
    gate rolls over. Default 0. NewsViewer only.

.PARAMETER Theme
    Dark or Light. Default Dark. NewsViewer only.

.PARAMETER TelemetryUploadEnabled
    When bound, sets Telemetry:UploadEnabled — $false stops forwarding session
    telemetry to the repository (the local 30-day retention sweep still runs).
    Default true. NewsService only.

.PARAMETER LogLevel
    Logging:LogLevel:Default — Trace, Debug, Information, Warning, Error, Critical, or None.
    NewsService only.

.PARAMETER EventLogLevel
    Logging:EventLog:LogLevel:Default — same values, EventLog provider only. NewsService only.

.EXAMPLE
    # NewsService on a dev box — file share, two teams
    .\Set-RegistryOverrides.ps1 `
        -Company "Contoso" `
        -ComponentName NewsService `
        -Teams "cz-its","de-prod" `
        -StorageMode Share `
        -SharePath "\\fileserver\newscentral" `
        -PollIntervalSeconds 60

.EXAMPLE
    # NewsViewer on the same dev box — same teams, gates bypassed for repeat runs
    .\Set-RegistryOverrides.ps1 `
        -Company "Contoso" `
        -ComponentName NewsViewer `
        -Teams "cz-its","de-prod" `
        -BypassDailyGate `
        -BypassImageIntegrityCheck

.EXAMPLE
    # NewsService against Azure, certificate auth — preview only
    .\Set-RegistryOverrides.ps1 -WhatIf `
        -Company "Contoso" `
        -ComponentName NewsService `
        -Teams "cz-its" `
        -StorageMode Azure `
        -AzureTenantId "00000000-0000-0000-0000-000000000000" `
        -AzureClientId "00000000-0000-0000-0000-000000000000" `
        -AzureAccountName "stgnewscentral" `
        -AzureAuthMode Certificate `
        -AzureCertificateThumbprint "ABCDEF1234567890ABCDEF1234567890ABCDEF12"

.EXAMPLE
    # NewsService on a pilot box — Entra group-team dynamic resolution, one instance
    .\Set-RegistryOverrides.ps1 `
        -Company "Contoso" `
        -ComponentName NewsService `
        -EntraEnabled $true `
        -EntraGracePeriodMinutes 240 `
        -EntraMaxDynamicTeams 16 `
        -EntraGroupInstances @{
            "prague-its" = @{ InclusionGroup = "NewsCentral Prague ITS" }
        }

.EXAMPLE
    # NewsViewer on a pilot box — pinned team signing keys plus display/wallpaper settings
    .\Set-RegistryOverrides.ps1 `
        -Company "Contoso" `
        -ComponentName NewsViewer `
        -Teams "cz-its","de-prod" `
        -RequireSignedIndex $true `
        -TeamPublicKeys @{
            "cz-its"  = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEreplace-with-real-spki-bytes"
            "de-prod" = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEreplace-with-real-spki-bytes"
        } `
        -LogicalDayStartHour 5 `
        -Theme Dark `
        -Active $true
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    # ── Registry path identity ────────────────────────────────────────────────
    # Company must match Directory.Build.props exactly — it defines the registry hive path, a
    # mismatch fails silently (OpenSubKey returns null), and it is NOT registry-overridable.
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Company,

    [Parameter(Mandatory)]
    [ValidateSet("NewsCentral","NewsService","NewsViewer","NewsTester")]
    [string] $ComponentName,

    # ── Teams ─────────────────────────────────────────────────────────────────
    [string[]] $Teams,

    # ── Storage ───────────────────────────────────────────────────────────────
    [ValidateSet("Share", "Azure")]
    [string] $StorageMode,

    [string] $SharePath,

    # ── NewsService ───────────────────────────────────────────────────────────
    [ValidateRange(10, 86400)]
    [Nullable[int]] $PollIntervalSeconds,

    [string] $CacheRootPath,

    # ── NewsViewer ────────────────────────────────────────────────────────────
    [switch] $BypassDailyGate,
    [switch] $BypassImageIntegrityCheck,

    # ── HMAC ──────────────────────────────────────────────────────────────────
    [string] $HmacSecretKey,

    # ── Azure Blob ────────────────────────────────────────────────────────────
    [string] $AzureTenantId,
    [string] $AzureClientId,
    [string] $AzureAccountName,
    [string] $AzureContainerName,

    [ValidateSet("Certificate", "ClientSecret", "ClientSecretEnv")]
    [string] $AzureAuthMode,

    [string] $AzureCertificateThumbprint,
    [string] $AzureClientSecret,

    [Nullable[bool]] $AzureUseWinHttpProxy,

    # ── Signing (NewsService, NewsViewer) ────────────────────────────────────────
    [Nullable[bool]] $RequireSignedIndex,
    [hashtable] $TeamPublicKeys,
    [hashtable] $TeamPreviousPublicKeys,

    # ── Entra (NewsService only) ──────────────────────────────────────────────────
    [Nullable[bool]] $EntraEnabled,

    [ValidateRange(0, 10080)]
    [Nullable[int]] $EntraGracePeriodMinutes,

    [ValidateRange(0, 1024)]
    [Nullable[int]] $EntraMaxDynamicTeams,

    [string] $EntraGlobalExclusionGroup,
    [hashtable] $EntraGroupInstances,
    [hashtable] $EntraAttributeSchemes,

    # ── Delivery ──────────────────────────────────────────────────────────────────
    [string] $DefaultLockScreenPath,
    [string] $PublishedImagePath,
    [string] $DefaultWallpaperPath,

    [ValidateSet("Fill","Fit","Stretch","Center","Tile")]
    [string] $WallpaperStyle,

    [string] $WallpaperBackgroundColor,

    # ── Display / Ui (NewsViewer only) ────────────────────────────────────────────
    [ValidateRange(0, 23)]
    [Nullable[int]] $LogicalDayStartHour,

    [ValidateSet("Dark","Light")]
    [string] $Theme,

    [Nullable[bool]] $Active,

    # ── Telemetry / Logging (NewsService only) ────────────────────────────────────
    [switch] $TelemetryUploadEnabled,

    [ValidateSet("Trace","Debug","Information","Warning","Error","Critical","None")]
    [string] $LogLevel,

    [ValidateSet("Trace","Debug","Information","Warning","Error","Critical","None")]
    [string] $EventLogLevel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$base = "HKLM:\Software\$Company\NewsCentral\$ComponentName"

# ── Component-aware parameter validation — runs before any write ─────────────────
# Parameters introduced by this change. NewsCentral and NewsTester remain entirely
# out of scope for them; only NewsService and NewsViewer may bind any of these.
$newParameterNames = @(
    "RequireSignedIndex", "TeamPublicKeys", "TeamPreviousPublicKeys",
    "EntraEnabled", "EntraGracePeriodMinutes", "EntraMaxDynamicTeams",
    "EntraGlobalExclusionGroup", "EntraGroupInstances", "EntraAttributeSchemes",
    "DefaultLockScreenPath", "PublishedImagePath", "DefaultWallpaperPath", "WallpaperStyle", "WallpaperBackgroundColor",
    "LogicalDayStartHour", "Theme", "TelemetryUploadEnabled", "LogLevel", "EventLogLevel",
    "AzureUseWinHttpProxy", "Active"
)

# Full parameter -> allowed-component map for the two fleet components. Covers every
# parameter this script can write to a fleet component's subtree (old and new alike) —
# binding a parameter against the wrong fleet component is rejected before any write.
$componentAllowedParams = @{
    NewsService = @(
        "Teams", "CacheRootPath", "HmacSecretKey", "RequireSignedIndex",
        "TeamPublicKeys", "TeamPreviousPublicKeys",
        "AzureTenantId", "AzureClientId", "AzureAccountName", "AzureContainerName",
        "AzureAuthMode", "AzureCertificateThumbprint", "AzureClientSecret", "AzureUseWinHttpProxy",
        "EntraEnabled", "EntraGracePeriodMinutes", "EntraMaxDynamicTeams",
        "EntraGlobalExclusionGroup", "EntraGroupInstances", "EntraAttributeSchemes",
        "DefaultLockScreenPath", "PublishedImagePath", "TelemetryUploadEnabled", "LogLevel", "EventLogLevel",
        "PollIntervalSeconds", "StorageMode", "SharePath"
    )
    NewsViewer = @(
        "Teams", "CacheRootPath", "HmacSecretKey", "RequireSignedIndex",
        "TeamPublicKeys", "TeamPreviousPublicKeys",
        "AzureTenantId", "AzureClientId", "AzureAccountName", "AzureContainerName",
        "AzureAuthMode", "AzureCertificateThumbprint", "AzureClientSecret", "AzureUseWinHttpProxy",
        "BypassDailyGate", "BypassImageIntegrityCheck", "LogicalDayStartHour", "Theme",
        "DefaultWallpaperPath", "WallpaperStyle", "WallpaperBackgroundColor", "Active"
    )
}

if ($ComponentName -in @("NewsCentral", "NewsTester")) {
    $offending = $newParameterNames | Where-Object { $PSBoundParameters.ContainsKey($_) }
    if ($offending) {
        throw "Parameter(s) $($offending -join ', ') are not valid for -ComponentName $ComponentName. " +
            "NewsCentral and NewsTester are out of scope for this script's fleet registry surface. See docs/configuration.md."
    }
}
else {
    $allowed = $componentAllowedParams[$ComponentName]
    $allGatedParams = ($componentAllowedParams.NewsService + $componentAllowedParams.NewsViewer) | Select-Object -Unique
    $offending = @()
    foreach ($paramName in $allGatedParams) {
        if ($PSBoundParameters.ContainsKey($paramName) -and ($paramName -notin $allowed)) {
            $validFor = $componentAllowedParams.Keys | Where-Object { $paramName -in $componentAllowedParams[$_] }
            $offending += "-$paramName (valid for: $($validFor -join ', '))"
        }
    }
    if ($offending) {
        throw "The following parameter(s) are not valid for -ComponentName ${ComponentName}: $($offending -join '; ')"
    }
}

# ── Hashtable shape validation — runs before any write ───────────────────────────
function Test-Base64String {
    param([string]$Value)
    try {
        [Convert]::FromBase64String($Value) | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Assert-TeamKeyHashtable {
    param([hashtable]$Table, [string]$ParamName)
    foreach ($key in $Table.Keys) {
        if ([string]::IsNullOrWhiteSpace($key)) {
            throw "-$ParamName contains an empty team folder name key."
        }
        $value = $Table[$key]
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "-$ParamName['$key'] is empty; expected a Base64-encoded SubjectPublicKeyInfo key."
        }
        if (-not (Test-Base64String -Value $value)) {
            throw "-$ParamName['$key'] is not valid Base64: '$value'"
        }
    }
}

if ($PSBoundParameters.ContainsKey("TeamPublicKeys")) {
    Assert-TeamKeyHashtable -Table $TeamPublicKeys -ParamName "TeamPublicKeys"
}
if ($PSBoundParameters.ContainsKey("TeamPreviousPublicKeys")) {
    Assert-TeamKeyHashtable -Table $TeamPreviousPublicKeys -ParamName "TeamPreviousPublicKeys"
}

if ($PSBoundParameters.ContainsKey("TeamPublicKeys") -and $PSBoundParameters.ContainsKey("Teams")) {
    foreach ($key in $TeamPublicKeys.Keys) {
        if ($key -notin $Teams) {
            Write-Warning "-TeamPublicKeys contains '$key', which is not present in -Teams. Valid for a dynamic (Entra-resolved) team with a pinned registry key, otherwise likely a typo."
        }
    }
}

if ($PSBoundParameters.ContainsKey("EntraGroupInstances")) {
    $knownInstanceKeys = @("InclusionGroup", "ExclusionGroup")
    foreach ($label in $EntraGroupInstances.Keys) {
        if ([string]::IsNullOrWhiteSpace($label)) {
            throw "-EntraGroupInstances contains an empty instance label key."
        }
        $instance = $EntraGroupInstances[$label]
        if ($instance -isnot [hashtable]) {
            throw "-EntraGroupInstances['$label'] must be a hashtable containing InclusionGroup."
        }
        $unknownKeys = $instance.Keys | Where-Object { $_ -notin $knownInstanceKeys }
        if ($unknownKeys) {
            throw "-EntraGroupInstances['$label'] contains unknown key(s): $($unknownKeys -join ', '). Allowed: $($knownInstanceKeys -join ', ')."
        }
        if (-not $instance.ContainsKey("InclusionGroup") -or [string]::IsNullOrWhiteSpace($instance["InclusionGroup"])) {
            throw "-EntraGroupInstances['$label'] is missing a non-empty InclusionGroup."
        }
    }
}

if ($PSBoundParameters.ContainsKey("EntraAttributeSchemes")) {
    $knownSchemeKeys = @("Selector", "Mappings")
    foreach ($scheme in $EntraAttributeSchemes.Keys) {
        if ([string]::IsNullOrWhiteSpace($scheme)) {
            throw "-EntraAttributeSchemes contains an empty scheme name key."
        }
        $def = $EntraAttributeSchemes[$scheme]
        if ($def -isnot [hashtable]) {
            throw "-EntraAttributeSchemes['$scheme'] must be a hashtable containing Selector and Mappings."
        }
        $unknownKeys = $def.Keys | Where-Object { $_ -notin $knownSchemeKeys }
        if ($unknownKeys) {
            throw "-EntraAttributeSchemes['$scheme'] contains unknown key(s): $($unknownKeys -join ', '). Allowed: $($knownSchemeKeys -join ', ')."
        }
        if (-not $def.ContainsKey("Selector") -or [string]::IsNullOrWhiteSpace($def["Selector"])) {
            throw "-EntraAttributeSchemes['$scheme'] is missing a non-empty Selector."
        }
        if (-not $def.ContainsKey("Mappings") -or $def["Mappings"] -isnot [hashtable] -or $def["Mappings"].Count -eq 0) {
            throw "-EntraAttributeSchemes['$scheme'] is missing a non-empty Mappings hashtable."
        }
        foreach ($mapKey in $def["Mappings"].Keys) {
            if ([string]::IsNullOrWhiteSpace($mapKey) -or [string]::IsNullOrWhiteSpace($def["Mappings"][$mapKey])) {
                throw "-EntraAttributeSchemes['$scheme'].Mappings contains an empty key or value."
            }
        }
    }
}

function Set-RegValue {
    param([string]$Path, [string]$Name, $Value, [string]$Type)
    if ($PSCmdlet.ShouldProcess("$Path\$Name", "Set registry value ($Type) = '$Value'")) {
        if (-not (Test-Path $Path)) {
            New-Item -Path $Path -Force | Out-Null
        }
        New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    }
}

function Remove-RegValue {
    param([string]$Path, [string]$Name)
    if ((Test-Path $Path) -and (Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess("$Path\$Name", "Remove registry value")) {
            Remove-ItemProperty -Path $Path -Name $Name -Force
        }
    }
}

function Set-AuthoritativeChildKeys {
    # Replaces the full set of child KEYS under $ParentPath, leaving any VALUE written
    # directly on $ParentPath itself untouched. Used for Signing\<team>\ subkeys and
    # Entra\GroupTeams\Instances\ / Entra\AttributeSchemes\<scheme>\, each of which sits
    # alongside a sibling value (RequireSignedIndex, GroupTeams\ExclusionGroup) that must
    # survive the replace.
    param([string]$ParentPath)
    if (Test-Path $ParentPath) {
        foreach ($child in Get-ChildItem -Path $ParentPath -ErrorAction SilentlyContinue) {
            if ($PSCmdlet.ShouldProcess($child.PSPath, "Remove stale subkey")) {
                Remove-Item -Path $child.PSPath -Recurse -Force
            }
        }
    }
    elseif ($PSCmdlet.ShouldProcess($ParentPath, "Create registry key")) {
        New-Item -Path $ParentPath -Force | Out-Null
    }
}

# ── Ensure base key exists ────────────────────────────────────────────────────
if (-not (Test-Path $base)) {
    if ($PSCmdlet.ShouldProcess($base, "Create registry key")) {
        New-Item -Path $base -Force | Out-Null
    }
}

# ── Root-level values (NewsViewer bindings) ───────────────────────────────────
if ($PSBoundParameters.ContainsKey("CacheRootPath") -and $ComponentName -eq "NewsViewer") {
    Set-RegValue -Path $base -Name "CacheRootPath" -Value $CacheRootPath -Type String
}

if ($PSBoundParameters.ContainsKey("Active")) {
    $dword = if ($Active) { 1 } else { 0 }
    Set-RegValue -Path $base -Name "Active" -Value $dword -Type DWord
}

if ($PSBoundParameters.ContainsKey("BypassDailyGate")) {
    $dword = if ($BypassDailyGate) { 1 } else { 0 }
    Set-RegValue -Path $base -Name "BypassDailyGate" -Value $dword -Type DWord
}

if ($PSBoundParameters.ContainsKey("BypassImageIntegrityCheck")) {
    $dword = if ($BypassImageIntegrityCheck) { 1 } else { 0 }
    Set-RegValue -Path $base -Name "BypassImageIntegrityCheck" -Value $dword -Type DWord
}

# ── Hmac\ ────────────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("HmacSecretKey")) {
    Set-RegValue -Path "$base\Hmac" -Name "SecretKey" -Value $HmacSecretKey -Type String
}

# ── Service\ ─────────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("PollIntervalSeconds")) {
    Set-RegValue -Path "$base\Service" -Name "PollIntervalSeconds" -Value $PollIntervalSeconds -Type String
}
if ($PSBoundParameters.ContainsKey("CacheRootPath") -and $ComponentName -eq "NewsService") {
    Set-RegValue -Path "$base\Service" -Name "CacheRootPath" -Value $CacheRootPath -Type String
}

# ── Repository\ ──────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("StorageMode")) {
    Set-RegValue -Path "$base\Repository" -Name "StorageMode" -Value $StorageMode -Type String
}
if ($PSBoundParameters.ContainsKey("SharePath")) {
    Set-RegValue -Path "$base\Repository" -Name "SharePath" -Value $SharePath -Type String
}

# ── AzureBlob\ ───────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("AzureAuthMode")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "AuthMode" -Value $AzureAuthMode -Type String
}
if ($PSBoundParameters.ContainsKey("AzureTenantId")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "TenantId" -Value $AzureTenantId -Type String
}
if ($PSBoundParameters.ContainsKey("AzureClientId")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "ClientId" -Value $AzureClientId -Type String
}
if ($PSBoundParameters.ContainsKey("AzureAccountName")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "AccountName" -Value $AzureAccountName -Type String
}
if ($PSBoundParameters.ContainsKey("AzureContainerName")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "ContainerName" -Value $AzureContainerName -Type String
}
if ($PSBoundParameters.ContainsKey("AzureCertificateThumbprint")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "CertificateThumbprint" -Value $AzureCertificateThumbprint -Type String
}
if ($PSBoundParameters.ContainsKey("AzureClientSecret")) {
    Set-RegValue -Path "$base\AzureBlob" -Name "ClientSecret" -Value $AzureClientSecret -Type String
}
if ($PSBoundParameters.ContainsKey("AzureUseWinHttpProxy")) {
    $val = if ($AzureUseWinHttpProxy) { "true" } else { "false" }
    Set-RegValue -Path "$base\AzureBlob" -Name "UseWinHttpProxy" -Value $val -Type String
}

# ── Signing\ ─────────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("RequireSignedIndex")) {
    $val = if ($RequireSignedIndex) { "true" } else { "false" }
    Set-RegValue -Path "$base\Signing" -Name "RequireSignedIndex" -Value $val -Type String
}

if ($PSBoundParameters.ContainsKey("TeamPublicKeys")) {
    $signingPath = "$base\Signing"
    Set-AuthoritativeChildKeys -ParentPath $signingPath
    foreach ($team in $TeamPublicKeys.Keys) {
        Set-RegValue -Path "$signingPath\$team" -Name "PublicKey" -Value $TeamPublicKeys[$team] -Type String
        if ($PSBoundParameters.ContainsKey("TeamPreviousPublicKeys") -and $TeamPreviousPublicKeys.ContainsKey($team)) {
            Set-RegValue -Path "$signingPath\$team" -Name "PublicKeyPrevious" -Value $TeamPreviousPublicKeys[$team] -Type String
        }
    }
    if ($PSBoundParameters.ContainsKey("TeamPreviousPublicKeys")) {
        foreach ($team in $TeamPreviousPublicKeys.Keys) {
            if (-not $TeamPublicKeys.ContainsKey($team)) {
                Set-RegValue -Path "$signingPath\$team" -Name "PublicKeyPrevious" -Value $TeamPreviousPublicKeys[$team] -Type String
            }
        }
    }
}
elseif ($PSBoundParameters.ContainsKey("TeamPreviousPublicKeys")) {
    # TeamPreviousPublicKeys bound without TeamPublicKeys — write alongside whatever
    # per-team subkeys already exist, without an authoritative replace.
    foreach ($team in $TeamPreviousPublicKeys.Keys) {
        Set-RegValue -Path "$base\Signing\$team" -Name "PublicKeyPrevious" -Value $TeamPreviousPublicKeys[$team] -Type String
    }
}

# ── Entra\ ───────────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("EntraEnabled")) {
    $val = if ($EntraEnabled) { "true" } else { "false" }
    Set-RegValue -Path "$base\Entra" -Name "Enabled" -Value $val -Type String
}
if ($PSBoundParameters.ContainsKey("EntraGracePeriodMinutes")) {
    Set-RegValue -Path "$base\Entra" -Name "GracePeriodMinutes" -Value $EntraGracePeriodMinutes -Type String
}
if ($PSBoundParameters.ContainsKey("EntraMaxDynamicTeams")) {
    Set-RegValue -Path "$base\Entra" -Name "MaxDynamicTeams" -Value $EntraMaxDynamicTeams -Type String
}
if ($PSBoundParameters.ContainsKey("EntraGlobalExclusionGroup")) {
    Set-RegValue -Path "$base\Entra\GroupTeams" -Name "ExclusionGroup" -Value $EntraGlobalExclusionGroup -Type String
}

if ($PSBoundParameters.ContainsKey("EntraGroupInstances")) {
    $instancesPath = "$base\Entra\GroupTeams\Instances"
    Set-AuthoritativeChildKeys -ParentPath $instancesPath
    foreach ($label in $EntraGroupInstances.Keys) {
        $instance = $EntraGroupInstances[$label]
        Set-RegValue -Path "$instancesPath\$label" -Name "InclusionGroup" -Value $instance["InclusionGroup"] -Type String
        if ($instance.ContainsKey("ExclusionGroup") -and -not [string]::IsNullOrWhiteSpace($instance["ExclusionGroup"])) {
            Set-RegValue -Path "$instancesPath\$label" -Name "ExclusionGroup" -Value $instance["ExclusionGroup"] -Type String
        }
    }
}

if ($PSBoundParameters.ContainsKey("EntraAttributeSchemes")) {
    $schemesPath = "$base\Entra\AttributeSchemes"
    Set-AuthoritativeChildKeys -ParentPath $schemesPath
    foreach ($scheme in $EntraAttributeSchemes.Keys) {
        $def = $EntraAttributeSchemes[$scheme]
        Set-RegValue -Path "$schemesPath\$scheme" -Name "Selector" -Value $def["Selector"] -Type String
        foreach ($mapKey in $def["Mappings"].Keys) {
            Set-RegValue -Path "$schemesPath\$scheme\Mappings" -Name $mapKey -Value $def["Mappings"][$mapKey] -Type String
        }
    }
}

# ── Delivery\ ────────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("DefaultLockScreenPath")) {
    Set-RegValue -Path "$base\Delivery" -Name "DefaultLockScreenPath" -Value $DefaultLockScreenPath -Type String
}
if ($PSBoundParameters.ContainsKey("PublishedImagePath")) {
    Set-RegValue -Path "$base\Delivery" -Name "PublishedImagePath" -Value $PublishedImagePath -Type String
}
if ($PSBoundParameters.ContainsKey("DefaultWallpaperPath")) {
    Set-RegValue -Path "$base\Delivery" -Name "DefaultWallpaperPath" -Value $DefaultWallpaperPath -Type String
}
if ($PSBoundParameters.ContainsKey("WallpaperStyle")) {
    Set-RegValue -Path "$base\Delivery" -Name "WallpaperStyle" -Value $WallpaperStyle -Type String
}
if ($PSBoundParameters.ContainsKey("WallpaperBackgroundColor")) {
    Set-RegValue -Path "$base\Delivery" -Name "WallpaperBackgroundColor" -Value $WallpaperBackgroundColor -Type String
}

# ── Display\ / Ui\ ─────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("LogicalDayStartHour")) {
    Set-RegValue -Path "$base\Display" -Name "LogicalDayStartHour" -Value $LogicalDayStartHour -Type String
}
if ($PSBoundParameters.ContainsKey("Theme")) {
    Set-RegValue -Path "$base\Ui" -Name "Theme" -Value $Theme -Type String
}

# ── Telemetry\ ───────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("TelemetryUploadEnabled")) {
    $dword = if ($TelemetryUploadEnabled) { 1 } else { 0 }
    Set-RegValue -Path "$base\Telemetry" -Name "UploadEnabled" -Value $dword -Type DWord
}

# ── Logging\ ─────────────────────────────────────────────────────────────────
if ($PSBoundParameters.ContainsKey("LogLevel")) {
    Set-RegValue -Path "$base\Logging\LogLevel" -Name "Default" -Value $LogLevel -Type String
}
if ($PSBoundParameters.ContainsKey("EventLogLevel")) {
    Set-RegValue -Path "$base\Logging\EventLog\LogLevel" -Name "Default" -Value $EventLogLevel -Type String
}

# ── teams\ — replace entire subkey so the list stays authoritative ────────────
if ($PSBoundParameters.ContainsKey("Teams")) {
    $teamsPath = "$base\teams"
    if ($PSCmdlet.ShouldProcess($teamsPath, "Recreate teams subkey")) {
        if (Test-Path $teamsPath) {
            Remove-Item -Path $teamsPath -Recurse -Force
        }
        New-Item -Path $teamsPath -Force | Out-Null
    }
    foreach ($team in $Teams) {
        if (-not [string]::IsNullOrWhiteSpace($team)) {
            Set-RegValue -Path $teamsPath -Name $team -Value "" -Type String
        }
    }
}

# ── Remove stale root-level flat values from the old format ──────────────────
@("teams", "StorageMode", "PollIntervalSeconds") | ForEach-Object {
    Remove-RegValue -Path $base -Name $_
}

# ── Verify — dump the final state ─────────────────────────────────────────────
Write-Host ""
Write-Host "Registry state under $($base -replace 'HKLM:\\','HKLM\'):" -ForegroundColor Cyan

function Show-RegKey {
    param([string]$Path, [string]$Indent = "")
    if (-not (Test-Path $Path)) { return }
    $item = Get-Item $Path
    foreach ($name in $item.GetValueNames()) {
        $val  = $item.GetValue($name)
        $kind = $item.GetValueKind($name)
        Write-Host "$Indent  $name  ($kind)  =  $val"
    }
    foreach ($child in Get-ChildItem $Path -ErrorAction SilentlyContinue) {
        Write-Host "$Indent  [$($child.PSChildName)]" -ForegroundColor Yellow
        Show-RegKey -Path $child.PSPath -Indent "$Indent    "
    }
}

Show-RegKey -Path $base
Write-Host ""
