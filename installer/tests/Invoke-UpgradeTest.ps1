<#
.SYNOPSIS
    Verifies that upgrading from an older package preserves the user's data and leaves exactly
    one product registered.

.DESCRIPTION
    The MSI keeps config.json, the database and logs out of its component list precisely so an
    upgrade cannot take them with it. That is a claim about MSI behaviour, not about our code,
    so it has to be executed to be believed.

    Two packages built from the same sources with different versions are enough to exercise the
    real MajorUpgrade path; this does not need a previously published release.

.NOTES
    A marker file stands in for the service's own state. The service does not yet create
    config.json (that arrives with the configuration store in a later milestone), so asserting
    on a file it does not write would be asserting nothing.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $OldPackage,
    [Parameter(Mandatory = $true)] [string] $NewPackage,
    [string] $ServiceName = 'PowerLease',
    [string] $InstallDir = "$env:ProgramFiles\PowerLease",
    [string] $DataDir = "$env:ProgramData\PowerLease"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = New-Object System.Collections.Generic.List[string]

function Test-That {
    param([string] $Description, [scriptblock] $Condition)
    $result = $false
    try { $result = [bool](& $Condition) }
    catch { $script:Failures.Add("$Description -- threw: $($_.Exception.Message)"); Write-Host "  FAIL  $Description (threw)"; return }
    if ($result) { Write-Host "  ok    $Description" } else { $script:Failures.Add($Description); Write-Host "  FAIL  $Description" }
}

function Invoke-Package {
    param([string] $Path, [string[]] $Arguments, [string] $LogName)

    $dir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
    $log = Join-Path $dir "$LogName.log"
    $all = $Arguments + @('/quiet', '/norestart', '/log', $log)
    Write-Host "$Path $($all -join ' ')"
    $p = Start-Process -FilePath $Path -ArgumentList $all -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        if (Test-Path $log) { Get-Content $log -Tail 60 | ForEach-Object { Write-Host $_ } }
        throw "$LogName failed with exit code $($p.ExitCode)"
    }
}

function Get-InstalledPowerLease {
    # Only the bundle should register in Programs and Features. The inner MSI hides itself with
    # ARPSYSTEMCOMPONENT=1; Visible="no" on the bundle's MsiPackage does not do that.
    #
    # Plenty of uninstall keys have no DisplayName at all, and Set-StrictMode turns reading a
    # missing property into a terminating error, so presence is checked before the comparison.
    try {
        @(Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
            Where-Object {
                $_ -and
                ($_.PSObject.Properties.Name -contains 'DisplayName') -and
                $_.DisplayName -eq 'PowerLease'
            })
    }
    catch {
        # Registry keys can vanish mid-enumeration right after an uninstall.
        Write-Host "  (registry enumeration raised: $($_.Exception.Message))"
        @()
    }
}

function Get-RegistryValue {
    param($Entry, [string] $Name)
    if ($Entry.PSObject.Properties.Name -contains $Name) { $Entry.$Name } else { $null }
}

# What "registered" means to a user is what Programs and Features shows, and that list hides
# entries flagged SystemComponent=1. The inner MSI sets that flag deliberately, so counting raw
# registry keys would report two products for one installation.
function Get-VisiblePowerLease {
    # @() at the call site is not redundant: PowerShell unrolls an empty array returned from a
    # function into $null, and Set-StrictMode then makes $null.Count a terminating error.
    @(@(Get-InstalledPowerLease) | Where-Object { (Get-RegistryValue $_ 'SystemComponent') -ne 1 })
}

function Write-PowerLeaseRegistrations {
    param([string] $When)
    $all = @(Get-InstalledPowerLease)
    $visible = @(Get-VisiblePowerLease)
    Write-Host "ARP keys named PowerLease $When : $($all.Count) total, $($visible.Count) visible"
    foreach ($e in $all) {
        $ver = Get-RegistryValue $e 'DisplayVersion'
        $sys = Get-RegistryValue $e 'SystemComponent'
        $un = Get-RegistryValue $e 'UninstallString'
        Write-Host "    version=$(if ($ver) { $ver } else { '<none>' }) systemComponent=$(if ($null -ne $sys) { $sys } else { '<unset>' })"
        Write-Host "    uninstall=$(if ($un) { $un } else { '<none>' })"
    }
}

function Get-InstalledPowerLeaseCount { @(Get-VisiblePowerLease).Count }

function Get-InstalledPowerLeaseVersion {
    $entries = @(Get-VisiblePowerLease)
    if ($entries.Count -ne 1) { return $null }
    Get-RegistryValue $entries[0] 'DisplayVersion'
}

Write-Host "=== Preconditions ==="
Test-That 'no PowerLease service present' { -not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }
Test-That 'old package exists' { Test-Path -LiteralPath $OldPackage }
Test-That 'new package exists' { Test-Path -LiteralPath $NewPackage }

Write-Host "`n=== Install old version ==="
Invoke-Package -Path $OldPackage -Arguments @() -LogName 'upgrade-install-old'
Test-That 'service registered by old version' { $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }
Write-PowerLeaseRegistrations 'after installing the old version'
$oldCount = Get-InstalledPowerLeaseCount
$versionBefore = Get-InstalledPowerLeaseVersion
Test-That 'exactly one product registered' { $oldCount -eq 1 }
Test-That 'old version reports a version' { -not [string]::IsNullOrWhiteSpace($versionBefore) }

# Stand-in for whatever the service will later write here. The point is that the upgrade must
# not touch anything under ProgramData that the package does not own.
$marker = Join-Path $DataDir 'upgrade-marker.json'
$markerContent = '{"writtenBy":"upgrade-test","mustSurvive":true}'
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Set-Content -LiteralPath $marker -Value $markerContent -NoNewline
$logDir = Join-Path $DataDir 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Set-Content -LiteralPath (Join-Path $logDir 'pretend.log') -Value 'old log line' -NoNewline
Test-That 'marker written before upgrade' { Test-Path -LiteralPath $marker }

Write-Host "`n=== Upgrade in place ==="
Invoke-Package -Path $NewPackage -Arguments @() -LogName 'upgrade-install-new'

Test-That 'service still registered after upgrade' { $null -ne (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }
Test-That 'service executable still present' { Test-Path "$InstallDir\Service\PowerLease.Service.exe" }
Test-That 'CLI still present' { Test-Path "$InstallDir\Cli\powerlease.exe" }

Write-PowerLeaseRegistrations 'after the upgrade'
$newCount = Get-InstalledPowerLeaseCount
# MajorUpgrade scheduled afterInstallInitialize must have removed the old product. Two entries
# would mean the upgrade installed alongside instead of replacing, which is what a changed
# UpgradeCode looks like from the outside.
Test-That 'still exactly one product registered (old one was replaced)' { $newCount -eq 1 }

Test-That 'user data survived the upgrade' { Test-Path -LiteralPath $marker }
Test-That 'user data content is unchanged' {
    (Get-Content -LiteralPath $marker -Raw) -eq $markerContent
}
Test-That 'log directory survived the upgrade' { Test-Path (Join-Path $logDir 'pretend.log') }

# Without this the whole test could silently be comparing a package against itself: an
# incremental build that skips relinking leaves the older content behind under a filename that
# still claims the new version.
$versionAfter = Get-InstalledPowerLeaseVersion
Write-Host "registered version before upgrade: $versionBefore, after: $versionAfter"
Test-That 'registered version actually changed' {
    -not [string]::IsNullOrWhiteSpace($versionAfter) -and $versionAfter -ne $versionBefore
}

Write-Host "`n=== Uninstall the upgraded product ==="
Invoke-Package -Path $NewPackage -Arguments @('/uninstall') -LogName 'upgrade-uninstall'
Test-That 'service deregistered' { -not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }
Test-That 'no product left registered' { (Get-InstalledPowerLeaseCount) -eq 0 }
Test-That 'user data still survives uninstall' { Test-Path -LiteralPath $marker }

Write-Host "`n=== Explicit data removal ==="
# REMOVEUSERDATA is the opt-in escape hatch. It has to be passed to the MSI, so it goes through
# the bundle's MSI argument passthrough.
Invoke-Package -Path $NewPackage -Arguments @('/uninstall', 'REMOVEUSERDATA=1') -LogName 'upgrade-uninstall-purge'
Write-Host "DataDir present after purge attempt: $(Test-Path $DataDir)"

Write-Host "`n=== Result ==="
if ($script:Failures.Count -gt 0) {
    Write-Host "$($script:Failures.Count) assertion(s) failed:"
    $script:Failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host 'All upgrade assertions passed.'
exit 0
