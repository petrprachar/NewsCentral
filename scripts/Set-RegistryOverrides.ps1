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
#    Signing\   RequireSignedIndex, per-team PublicKey / PublicKeyPrevious
#               (Phase D — parked indefinitely, see docs/future.md)
#    Entra\     Enabled, GracePeriodMinutes, MaxDynamicTeams,
#               AttributeSchemes\, GroupTeams\
#    Delivery\  DefaultLockScreenPath (NewsService),
#               DefaultWallpaperPath / WallpaperStyle /
#               WallpaperBackgroundColor (NewsViewer)
#    Display\   LogicalDayStartHour
#    Ui\        Theme
#    Telemetry\ UploadEnabled
#    Logging\   LogLevel\Default, EventLog\LogLevel\Default
#    AzureBlob\ UseWinHttpProxy
#    NewsCentral component keys (DataPath, LockExpirationMinutes, Storage\, ...)
#
#  WARNING — numeric values must NEVER be written as -Type DWord.
#  RegistryConfigurationProvider coerces REG_DWORD 0 -> "False" and 1 -> "True",
#  after which the configuration binder throws converting "False"/"True" to int
#  and crashes the component at startup. Write every int-valued key
#  (PollIntervalSeconds, GracePeriodMinutes, LockExpirationMinutes,
#  LogicalDayStartHour, ...) as -Type String. Genuine booleans as DWord are
#  fine — the coercion exists for them.
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
    │       AuthMode               REG_SZ   ("Certificate" or "ClientSecret")
    │       TenantId               REG_SZ
    │       ClientId               REG_SZ
    │       AccountName            REG_SZ   (storage account name, no .blob.core.windows.net)
    │       ContainerName          REG_SZ
    │       CertificateThumbprint  REG_SZ   (AuthMode=Certificate)
    │       ClientSecret           REG_SZ   (AuthMode=ClientSecret)
    ├── Hmac\
    │       SecretKey   REG_SZ   (Base64-encoded 32-byte HMAC key; empty string = HMAC disabled)
    └── teams\
            <teamFolderName>   REG_SZ ""   (one value per team; name = folder name; data ignored)

    HKLM\Software\<Company>\NewsCentral\NewsViewer\
    │   CacheRootPath                REG_SZ    (overrides ViewerConfiguration.CacheRootPath)
    │   BypassDailyGate             DWORD     (1 = skip once-per-day gate at startup)
    │   BypassImageIntegrityCheck    DWORD     (1 = skip image SHA-256 verification)
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

.PARAMETER Teams
    Array of team folder names to register.  Existing teams not in this list
    are removed from the teams\ subkey.

.PARAMETER StorageMode
    Repository storage backend: Share or Azure.

.PARAMETER SharePath
    UNC or local path to the file-share repository (StorageMode=Share).

.PARAMETER PollIntervalSeconds
    NewsService polling interval in seconds.

.PARAMETER CacheRootPath
    Local cache root used by both NewsService and NewsViewer.
    Default: C:\ProgramData\NewsCentral

.PARAMETER BypassDailyGate
    When $true, NewsViewer skips the once-per-logical-day display gate.
    Useful for repeated test runs.

.PARAMETER BypassImageIntegrityCheck
    When $true, NewsViewer skips SHA-256 verification of the cached image file.
    Useful when testing with manually replaced images.

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
    [string] $AzureClientSecret
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$base = "HKLM:\Software\$Company\NewsCentral\$ComponentName"

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
