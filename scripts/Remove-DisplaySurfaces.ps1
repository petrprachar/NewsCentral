#Requires -RunAsAdministrator
# ============================================================================
#  SCOPE — DECOMMISSIONING / UNINSTALL REMEDIATION
#
#  Clears the two PersonalizationCSP display surfaces NewsService manages (lock
#  screen + desktop wallpaper) and removes the published-image folder. Run this
#  as part of uninstalling NewsCentral — see docs/packaging.md ->
#  "NewsService — display-surface cleanup" for the full rationale and the rules
#  this script implements.
#
#  This script does NOT stop or remove the NewsService Windows Service. See
#  .NOTES below for why, and run it only after NewsService has been stopped
#  (or uninstalled) — otherwise the next poll cycle may simply re-apply what
#  this script just cleared.
#
#  AUTHORITATIVE REFERENCES:
#    docs/packaging.md      "NewsService — display-surface cleanup"
#    docs/anti-tamper.md    "Teardown — clearing a stale value, per surface"
#    docs/configuration.md  Delivery:PublishedImagePath
#
#  Ownership rule (never touch a value this product did not write): mirrors
#  NewsService/Services/SyncService.cs -> IsUnderPublishRoot exactly — a fully
#  normalized, case-insensitive, directory-prefix comparison against the
#  resolved publish root. An empty or unresolvable publish root fails CLOSED:
#  nothing is ever cleared.
# ============================================================================
<#
.SYNOPSIS
    Clears the NewsService-managed PersonalizationCSP display surfaces (lock screen and desktop
    wallpaper) and removes the published-image folder, as part of uninstalling NewsCentral.

.DESCRIPTION
    Uninstalling NewsCentral leaves C:\Windows\Web\NewsCentral (or wherever
    Delivery:PublishedImagePath points) populated, and the six PersonalizationCSP registry values
    pointing into it — the machine keeps an enforced wallpaper AND lock screen permanently, with
    Personalization greyed out in Windows Settings, and no software left on the box that could ever
    clear them. This script performs that cleanup.

    For each surface (lock screen, wallpaper) it:
      1. Resolves the publish root — HKLM\Software\<Company>\NewsCentral\NewsService\Delivery\
         PublishedImagePath, falling back to the default (C:\Windows\Web\NewsCentral) — or the
         -PublishedImagePath override, for when the GPO that set a custom path is already gone.
      2. Validates the resolved root (non-empty, an absolute path GetFullPath can resolve). An
         invalid root aborts with an error and clears NOTHING — fail closed.
      3. Reads the live *ImagePath value for that surface. Clears the three CSP values for that
         surface ONLY if the live value resolves to a path INSIDE the publish root (the same
         ownership test SyncService.IsUnderPublishRoot applies every poll cycle). A value pointing
         elsewhere is left alone — it belongs to another management system (GPO, Intune, a manual
         admin change) — with a warning naming the foreign path. An absent value is reported and
         skipped, not treated as an error.
      4. Never removes the PersonalizationCSP key itself — other CSP settings may live there.

    It then deletes every lockscreen-* and wallpaper-* file from the publish root (the only files
    this product ever writes there) and removes the folder itself only if it is now empty — never a
    recursive delete of a registry-supplied path.

    Every destructive step is gated behind ShouldProcess, so -WhatIf reports the full plan (which
    values would be cleared, which files would be deleted, whether the folder would be removed)
    without touching anything.

.PARAMETER Company
    The Company value NewsCentral was built with (Directory.Build.props <Company> /
    SolutionConstants.Company). Used to locate
    HKLM\Software\<Company>\NewsCentral\NewsService\Delivery\PublishedImagePath. Required even when
    -PublishedImagePath is also supplied, for consistency with the rest of the script family.

.PARAMETER PublishedImagePath
    Overrides the publish-root resolution — read this literal path instead of the registry. Use this
    when the GPO that provisioned a CUSTOM Delivery:PublishedImagePath has already been retired (so
    it can no longer be read from the registry) and the admin knows the actual folder that was in use.

.EXAMPLE
    .\Remove-DisplaySurfaces.ps1 -Company "Contoso" -WhatIf

    Previews the full cleanup plan for a machine provisioned under Contoso — which of the six CSP
    values would be cleared, which files would be deleted, whether the folder would be removed —
    without changing anything.

.EXAMPLE
    .\Remove-DisplaySurfaces.ps1 -Company "Contoso"

    Performs the cleanup: clears any NewsService-owned lock-screen/wallpaper CSP values, deletes the
    published image files, and removes the publish folder if it is left empty.

.EXAMPLE
    .\Remove-DisplaySurfaces.ps1 -Company "Contoso" -PublishedImagePath "D:\Custom\NewsCentral"

    Performs the cleanup against an explicit publish root, for a machine whose GPO (and therefore the
    registry value that would normally supply this path) has already been removed.

.NOTES
    Does NOT stop or uninstall the NewsService Windows Service — a running service cannot reliably
    clean up after its own removal, and "clear on shutdown" would wrongly fire on every ordinary
    service stop/restart, not just an uninstall. Stop or uninstall NewsService BEFORE running this
    script; otherwise its next poll cycle may simply re-apply the surfaces this script just cleared.

    Uninstall before retiring the GPO that set a custom Delivery:PublishedImagePath — once that GPO
    is gone the registry value is unreadable, resolution falls back to the default path, and the
    actual (custom) folder is left behind untouched. Use -PublishedImagePath to recover from that
    situation after the fact.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Company,

    [string] $PublishedImagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$DefaultPublishedImagePath = "C:\Windows\Web\NewsCentral"
$CspKeyPath = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP"

# Order matters only for output readability; each surface is evaluated independently.
$Surfaces = @(
    [pscustomobject]@{
        Name   = "Lock screen"
        Prefix = "lockscreen"
        Values = @("LockScreenImagePath", "LockScreenImageUrl", "LockScreenImageStatus")
    },
    [pscustomobject]@{
        Name   = "Wallpaper"
        Prefix = "wallpaper"
        Values = @("DesktopImagePath", "DesktopImageUrl", "DesktopImageStatus")
    }
)

function Resolve-RawPublishedImagePath {
    param(
        [string] $Company,
        [bool] $OverrideBound,
        [string] $OverrideValue
    )

    if ($OverrideBound) {
        return $OverrideValue
    }

    $deliveryPath = "HKLM:\Software\$Company\NewsCentral\NewsService\Delivery"
    try {
        if (Test-Path $deliveryPath) {
            $prop = Get-ItemProperty -Path $deliveryPath -Name "PublishedImagePath" -ErrorAction SilentlyContinue
            if ($prop) {
                return $prop.PublishedImagePath
            }
        }
    }
    catch {
        Write-Warning "Could not read Delivery\PublishedImagePath from the registry — falling back to the default. $_"
    }

    return $DefaultPublishedImagePath
}

function Test-UnderPublishRoot {
    # Mirrors NewsService/Services/SyncService.cs -> IsUnderPublishRoot exactly: fully-normalized
    # absolute-path comparison, case-insensitive, directory-prefix (never a raw string prefix on the
    # unnormalized value). Any resolution failure fails closed (treated as foreign).
    param([string] $Path, [string] $Root)

    try {
        $normalizedRoot = ([System.IO.Path]::GetFullPath($Root)).TrimEnd('\', '/')
        $normalizedPath = [System.IO.Path]::GetFullPath($Path)
        return $normalizedPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase) `
            -or $normalizedPath.StartsWith("$normalizedRoot\", [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Remove-RegValue {
    param([string] $Path, [string] $Name)
    if ((Test-Path $Path) -and (Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue)) {
        if ($PSCmdlet.ShouldProcess("$Path\$Name", "Remove registry value")) {
            Remove-ItemProperty -Path $Path -Name $Name -Force
        }
    }
}

# ── Step 1/2 — resolve and validate the publish root ───────────────────────────────────────────

$rawPath = Resolve-RawPublishedImagePath -Company $Company `
    -OverrideBound $PSBoundParameters.ContainsKey('PublishedImagePath') -OverrideValue $PublishedImagePath

$publishRoot = $null
if (-not [string]::IsNullOrWhiteSpace($rawPath)) {
    try {
        if ([System.IO.Path]::IsPathRooted($rawPath)) {
            $publishRoot = [System.IO.Path]::GetFullPath($rawPath)
        }
    }
    catch {
        $publishRoot = $null
    }
}

if (-not $publishRoot) {
    Write-Error "Delivery:PublishedImagePath ('$rawPath') is empty or not a usable absolute path. Clearing NOTHING — fail closed. Pass -PublishedImagePath explicitly to override."
    return
}

Write-Host "Publish root resolved to: $publishRoot" -ForegroundColor Cyan
Write-Host ""

# ── Step 3 — per-surface CSP clear, ownership-checked ───────────────────────────────────────────

$clearedSurfaces = [System.Collections.Generic.List[string]]::new()
$skippedForeign  = [System.Collections.Generic.List[string]]::new()
$absentSurfaces  = [System.Collections.Generic.List[string]]::new()

foreach ($surface in $Surfaces) {
    $pathValueName = $surface.Values[0]

    $current = $null
    if (Test-Path $CspKeyPath) {
        $prop = Get-ItemProperty -Path $CspKeyPath -Name $pathValueName -ErrorAction SilentlyContinue
        if ($prop) {
            $current = $prop.$pathValueName
        }
    }

    if ([string]::IsNullOrWhiteSpace($current)) {
        Write-Host "$($surface.Name) — no live value; nothing to clear." -ForegroundColor DarkGray
        $absentSurfaces.Add($surface.Name)
        continue
    }

    if (-not (Test-UnderPublishRoot -Path $current -Root $publishRoot)) {
        Write-Warning "$($surface.Name) — live value '$current' does NOT resolve inside the publish root; leaving all three values untouched (foreign — owned by another management system)."
        $skippedForeign.Add($surface.Name)
        continue
    }

    foreach ($valueName in $surface.Values) {
        Remove-RegValue -Path $CspKeyPath -Name $valueName
    }
    Write-Host "$($surface.Name) — cleared (was: $current)" -ForegroundColor Green
    $clearedSurfaces.Add($surface.Name)
}

Write-Host ""

# ── Step 4 — delete published files, then remove the folder if empty ───────────────────────────

$deletedFiles = 0
$folderOutcome = "did not exist"

if (Test-Path $publishRoot) {
    foreach ($surface in $Surfaces) {
        $files = Get-ChildItem -Path $publishRoot -Filter "$($surface.Prefix)-*" -File -ErrorAction SilentlyContinue
        foreach ($file in $files) {
            if ($PSCmdlet.ShouldProcess($file.FullName, "Delete published image")) {
                Remove-Item -Path $file.FullName -Force
            }
            $deletedFiles++
        }
    }

    $remaining = Get-ChildItem -Path $publishRoot -Force -ErrorAction SilentlyContinue
    if (-not $remaining -or $remaining.Count -eq 0) {
        if ($PSCmdlet.ShouldProcess($publishRoot, "Remove empty publish folder")) {
            Remove-Item -Path $publishRoot -Force
        }
        $folderOutcome = "removed (was empty)"
    }
    else {
        Write-Host "Publish folder still contains $($remaining.Count) other item(s) — left in place (never a recursive delete)." -ForegroundColor DarkGray
        $folderOutcome = "left in place (not empty)"
    }
}

# ── Step 5 — summary ─────────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
Write-Host "-------"
Write-Host "Publish root:      $publishRoot"
Write-Host "Surfaces cleared:  $(if ($clearedSurfaces.Count -gt 0) { $clearedSurfaces -join ', ' } else { '(none)' })"
Write-Host "Surfaces skipped (foreign): $(if ($skippedForeign.Count -gt 0) { $skippedForeign -join ', ' } else { '(none)' })"
Write-Host "Surfaces absent:   $(if ($absentSurfaces.Count -gt 0) { $absentSurfaces -join ', ' } else { '(none)' })"
Write-Host "Published files deleted: $deletedFiles"
Write-Host "Publish folder:    $folderOutcome"
