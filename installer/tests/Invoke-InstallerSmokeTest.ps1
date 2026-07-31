<#
.SYNOPSIS
    Installs the PowerLease MSI silently, asserts the installed state, then uninstalls and
    asserts what survives.

.DESCRIPTION
    This is the only place the installer's behaviour is actually verified. The development
    machine for this project is macOS, so every claim the MSI makes about services, PATH and
    ProgramData is unproven until this script runs on Windows.

    Assertions are deliberately specific. "The service exists" would pass even if the startup
    type, the failure actions or the account were wrong, and each of those is a decision with
    a stated reason in Package.wxs.

.NOTES
    Safe to run on a CI runner: PowerLease as installed here never calls a power-transition
    API, and this version of the product contains no such code path at all, so nothing here
    can put the runner to sleep. The service is also deliberately NOT started by the MSI.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MsiPath,

    # Kept overridable so a developer can smoke-test a locally built MSI in a VM.
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

    if ($result) { Write-Host "  ok    $Description" }
    else { $script:Failures.Add($Description); Write-Host "  FAIL  $Description" }
}

function Invoke-Msi {
    param([string[]] $MsiArguments, [string] $LogName)

    $log = Join-Path $env:RUNNER_TEMP "$LogName.log"
    if (-not $env:RUNNER_TEMP) { $log = Join-Path $env:TEMP "$LogName.log" }

    $all = $MsiArguments + @('/quiet', '/norestart', '/l*v', $log)
    Write-Host "msiexec $($all -join ' ')"
    $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $all -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        Write-Host "--- msiexec log tail ---"
        if (Test-Path $log) { Get-Content $log -Tail 60 | ForEach-Object { Write-Host $_ } }
        throw "msiexec failed with exit code $($p.ExitCode)"
    }
}

Write-Host "=== Preconditions ==="
Test-That 'service is not already installed' { -not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }
Test-That 'MSI exists' { Test-Path -LiteralPath $MsiPath }

Write-Host "`n=== Install ==="
Invoke-Msi -MsiArguments @('/i', "`"$MsiPath`"") -LogName 'powerlease-install'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
Test-That 'service is registered' { $null -ne $svc }

# The MSI deliberately does not start the service: ServiceControl has no Condition attribute
# and StartServices runs before InstallFinalize, so starting it would risk a new service
# migrating data that a rolled-back install then leaves behind older binaries for.
Test-That 'service is NOT running after install (by design)' { $svc.Status -eq 'Stopped' }

# Win32_Service.StartMode reports 'Auto' for both plain and delayed auto start, so the
# delayed flag has to be read from the registry where ServiceConfig writes it.
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Test-That 'startup type is Auto' {
    (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartMode -eq 'Auto'
}
Test-That 'DelayedAutostart flag is set' {
    (Get-ItemProperty -Path $svcKey -Name 'DelayedAutostart' -ErrorAction Stop).DelayedAutostart -eq 1
}
Test-That 'runs as LocalSystem' {
    (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartName -eq 'LocalSystem'
}

# Failure actions are what keep a crash from turning into a wrongful sleep: when the process
# dies the OS drops the power request, so the service has to come back on its own.
$scFailure = & sc.exe qfailure $ServiceName 2>&1 | Out-String
Write-Host "sc qfailure output:`n$scFailure"
Test-That 'failure actions restart the service' { $scFailure -match 'RESTART' }
Test-That 'first restart has no delay' { $scFailure -match 'RESTART -- Delay = 0 milliseconds' }

Write-Host "`n=== Layout ==="
Test-That 'service executable installed under Service\' { Test-Path "$InstallDir\Service\PowerLease.Service.exe" }
Test-That 'CLI installed under Cli\' { Test-Path "$InstallDir\Cli\powerlease.exe" }
# Both trees are self-contained publishes, so each must carry its own runtime. If these
# collapsed into one directory the identically-named runtime files would collide.
Test-That 'service tree has its own runtime' { Test-Path "$InstallDir\Service\hostfxr.dll" }
Test-That 'CLI tree has its own runtime' { Test-Path "$InstallDir\Cli\hostfxr.dll" }

Write-Host "`n=== PATH ==="
# MSI resolves a directory property with a trailing backslash, so the entry PATH actually
# receives is "...\Cli\". That is legal and works, so entries are compared with trailing
# separators trimmed rather than changing the package to produce a tidier-looking string.
function Get-MachinePathEntries {
    ([Environment]::GetEnvironmentVariable('Path', 'Machine') -split ';') |
        Where-Object { $_ } |
        ForEach-Object { $_.TrimEnd('\') }
}
$pathEntries = Get-MachinePathEntries
Write-Host "PowerLease entries on PATH: $(($pathEntries | Where-Object { $_ -like '*PowerLease*' }) -join ' | ')"
Test-That 'Cli directory is on the machine PATH' { $pathEntries -contains "$InstallDir\Cli" }
Test-That 'Service directory is NOT on PATH' { -not ($pathEntries -contains "$InstallDir\Service") }

# The runner process inherited its PATH before the install, so invoke by full path first and
# only then rebuild the environment to check the bare command resolves.
Test-That 'CLI runs and reports a version (full path)' {
    (& "$InstallDir\Cli\powerlease.exe" --version) -match '^PowerLease CLI '
}
$env:Path = ([Environment]::GetEnvironmentVariable('Path', 'Machine')) + ';' +
            ([Environment]::GetEnvironmentVariable('Path', 'User'))
Test-That 'CLI resolves as a bare command after PATH refresh' {
    (& powerlease --version) -match '^PowerLease CLI '
}

Write-Host "`n=== Service start and crash recovery ==="
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
Test-That 'service starts' { (Get-Service -Name $ServiceName).Status -eq 'Running' }

$before = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
Test-That 'service has a process id' { $before -gt 0 }

Stop-Process -Id $before -Force
$deadline = (Get-Date).AddSeconds(60)
$after = 0
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $after = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
    if ($after -gt 0 -and $after -ne $before) { break }
}
Write-Host "process id before kill: $before, after: $after"
Test-That 'SCM restarts the service after a forced kill' { $after -gt 0 -and $after -ne $before }

Stop-Service -Name $ServiceName -Force

Write-Host "`n=== Data directory ==="
Test-That 'ProgramData root was created by the MSI' { Test-Path $DataDir }

Write-Host "`n=== Uninstall ==="
Invoke-Msi -MsiArguments @('/x', "`"$MsiPath`"") -LogName 'powerlease-uninstall'

Test-That 'service is deregistered' { -not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) }
Test-That 'install directory is gone' { -not (Test-Path "$InstallDir\Service\PowerLease.Service.exe") }
# The whole point of keeping config/db/logs out of the MSI: an uninstall must not take the
# user's configuration and history with it.
Test-That 'ProgramData survives uninstall' { Test-Path $DataDir }
Test-That 'Cli directory removed from PATH' {
    -not ((Get-MachinePathEntries) -contains "$InstallDir\Cli")
}

Write-Host "`n=== Result ==="
if ($script:Failures.Count -gt 0) {
    Write-Host "$($script:Failures.Count) assertion(s) failed:"
    $script:Failures | ForEach-Object { Write-Host "  - $_" }
    exit 1
}
Write-Host 'All installer smoke assertions passed.'
exit 0
