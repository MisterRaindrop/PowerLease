# PowerLease v0.1.0 real-machine release gate

This is the one-pass procedure for the seven numbered physical-hardware scenarios. A passing CI
run is not a substitute: CI excludes every test whose category is `RealMachine`. Record the date,
machine model, Windows build, PowerLease build/version, active power-scheme GUID, and the operator
name with the results.

PowerLease and these tests never request sleep, hibernation, shutdown, or any other power
transition. They only add and remove a `PowerRequestSystemRequired` request. After protection is
removed, leave the machine idle and wait for Windows to apply its own active power plan. Do not use
a sleep button, close a laptop lid, run a sleep command, or otherwise force the result.

The complete run takes about 35 minutes plus build time. Run the scenarios one at a time, in order.
Scenarios 1, 2, 4, and 5 temporarily set the active plan's **AC sleep timeout** to two minutes; each
test restores the exact prior value in cleanup. The manual recovery commands near the end are the
fallback if the test host is terminated before cleanup runs.

## Result record

Create a copy of this table in the release evidence:

| Scenario | Result | Evidence to paste |
| --- | --- | --- |
| 1 | PASS / FAIL / MACHINE BLOCKED | Test result and five-minute timestamps |
| 2 | PASS / FAIL / MACHINE BLOCKED | Actual sleep latency, Kernel-Power sleep/wake, wake reason |
| 3 | PASS / FAIL / MACHINE BLOCKED | SSH lease ID, `status`, SYSTEM request |
| 4 | PASS / FAIL / MACHINE BLOCKED | Remaining lease, expiry, sleep/wake events |
| 5 | PASS / FAIL / MACHINE BLOCKED | Protected `cmd:<pid>` inhibitor and build result |
| 6 | PASS / FAIL / MACHINE BLOCKED | Observed absence and kill-to-restored milliseconds, old/new PID |
| 7 | PASS / FAIL / MACHINE BLOCKED | Complete `powercfg /requests` output |

Use **MACHINE BLOCKED**, not FAIL, when the test's live PowerLease assertions pass but this machine's
power policy, firmware, wake support, event-log availability, or another program prevents the
sleep/wake observation. The scenario-specific diagnosis sections say which is which.

## Preconditions and preparation

### Hardware, Windows, and access

Use a physical Windows 10 or Windows 11 machine on AC power. A VM cannot establish the hardware
sleep facts. Keep a local keyboard/power button available even when using wake-on-LAN. Open a local
PowerShell window with **Run as administrator**, change to the repository root, and keep this same
window open for the entire run.

Confirm elevation:

```powershell
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
```

Expected: `True`. If it is `False`, stop and reopen PowerShell as administrator. An access-denied
test result from an unelevated shell is not a product failure.

Confirm that this is the intended installed build and that the service is running:

```powershell
Get-Command powerlease.exe
powerlease.exe --version
Get-Service PowerLease
sc.exe qfailure PowerLease
```

Expected:

- `Get-Command` resolves the installed CLI, normally under `C:\Program Files\PowerLease\Cli`.
- `Get-Service` reports `Running`.
- `sc.exe qfailure` shows `RESTART -- Delay = 0 milliseconds` for at least the first failure.

If the service is stopped, run `Start-Service PowerLease`. If the failure action is not immediate
restart, scenario 6 cannot test the release build as specified; reinstall the intended artifact.
Do not change the SCM recovery configuration just to make the test pass.

Confirm that Windows offers an automatic sleep state and that PowerLease can read the active policy:

```powershell
powercfg.exe /a
powerlease.exe wake-status
```

Expected: `powercfg /a` lists Standby (S3) or Standby (S0 Low Power Idle), `Running on battery` is
`no`, and `Keep-awake honoured on mains` is `yes`. If standby is unavailable, or the mains answer is
`no`/`unknown`, record **MACHINE BLOCKED**. `PowerCapabilityProbe` reports this condition because a
successful API call is not proof that the active plan or hardware will honour it.

Confirm access to the persistent sleep/wake evidence:

```powershell
wevtutil.exe qe System /q:"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=107 or EventID=506 or EventID=507)]]" /c:1 /rd:true /f:text
wevtutil.exe qe System /q:"*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]" /c:1 /rd:true /f:text
```

Expected: both commands return without `Access is denied` or `The specified channel could not be
found`. An empty result is acceptable if the machine has not slept recently.

### OpenSSH and the second host

PowerLease must observe the real Windows OpenSSH server; the tests never install, enable, or
reconfigure it. On the Windows target, run:

```powershell
Get-Service sshd
Get-NetTCPConnection -State Listen -LocalPort 22
wevtutil.exe gl OpenSSH/Operational
```

Expected: `sshd` is `Running`, port 22 has a listening socket, and the channel output contains
`enabled: true`. If not, stop. Prepare OpenSSH through the machine owner's normal administration
process, then restart this procedure; do not change SSH configuration as part of the test.

From a second host on the same reachable network, check TCP reachability without logging in yet:

```powershell
Test-NetConnection <TARGET-IP-OR-NAME> -Port 22
```

Expected: `TcpTestSucceeded : True`. Replace the angle-bracket value with the actual target. Wait 20
seconds after this probe so its short TCP connection has disappeared before taking the baseline.

Scenarios 2 and 4 need a wake that does not create a new SSH session. Either have a person press the
target's power button after the stated wait, or use already-configured wake-on-LAN. Do not change BIOS,
NIC, or security settings for this run. To check an existing Windows wake-on-LAN setup:

```powershell
Get-NetAdapter | Format-Table Name, Status, MacAddress
powercfg.exe /devicequery wake_armed
```

Expected when wake-on-LAN will be used: the active network adapter is listed by `wake_armed`, and its
MAC address is known to the second-host operator. From a Windows second host, the following sends a
standard magic packet; fill in the target MAC address first:

```powershell
$targetMac = 'AA-BB-CC-DD-EE-FF'
$macBytes = [Convert]::FromHexString($targetMac.Replace('-', ''))
$packet = [byte[]](,0xFF * 6 + $macBytes * 16)
$udp = [Net.Sockets.UdpClient]::new()
$udp.EnableBroadcast = $true
[void]$udp.Send($packet, $packet.Length, '255.255.255.255', 9)
$udp.Dispose()
```

This only sends a network packet. If wake-on-LAN was not already configured and verified, use the
physical power button instead. In either case, do not log in over SSH until scenario 3 asks for it.

### Preserve the exact power-plan value

The tests read and restore this value themselves. Record it separately so it can be restored even if
the test host is killed:

```powershell
$activeScheme = [regex]::Match((powercfg.exe /getactivescheme), '[0-9a-fA-F-]{36}').Value
$standbyQuery = powercfg.exe /query $activeScheme SUB_SLEEP STANDBYIDLE
$standbyValues = [regex]::Matches(($standbyQuery -join "`n"), '\b0x[0-9a-fA-F]{8}\b')
$originalAcSleepSeconds = [Convert]::ToUInt32($standbyValues[0].Value.Substring(2), 16)
$activeScheme
$originalAcSleepSeconds
```

Expected: a scheme GUID and a decimal number of seconds. Write both in the result record. Do not
continue if `$standbyValues.Count` is less than 2.

### Install the controlled test configuration

The controlled configuration disables activity rules and schedules, sets restart/resume grace to
zero, makes an SSH hold last three minutes, and names `cmd` as the protected process. This makes every
release observable and lets scenario 5 use a `cmd.exe` wrapper that performs real repeated builds.
Run the scenarios from PowerShell, not Command Prompt, and close unrelated `cmd.exe` windows.

Back up the owner's configuration first:

```powershell
$configPath = Join-Path $env:ProgramData 'PowerLease\config.json'
$configBackup = Join-Path $env:ProgramData 'PowerLease\config.m7-backup.json'
if (Test-Path $configBackup) { throw "Backup already exists at $configBackup; resolve the earlier run first." }
$configExisted = Test-Path $configPath
if ($configExisted) { Copy-Item $configPath $configBackup }
```

Install the test configuration and restart the service:

```powershell
Stop-Service PowerLease
@'
{
  "schemaVersion": 1,
  "general": {
    "language": "en-US",
    "resumeGracePeriodMinutes": 0,
    "serviceRecoveryGracePeriodMinutes": 0
  },
  "ssh": {
    "enabled": true,
    "ports": [22],
    "minimumConnectionSeconds": 15,
    "defaultHoldMinutes": 3,
    "autoRenew": true,
    "eventLogEnabled": true,
    "fileLogPath": null,
    "tcpOnlyConfirmed": false
  },
  "idleRules": {
    "cpu": { "enabled": false, "thresholdPercent": 10, "durationMinutes": 20 },
    "memory": { "enabled": false, "thresholdPercent": 70, "durationMinutes": 20 },
    "disk": { "enabled": false, "thresholdBytesPerSecond": 1048576, "durationMinutes": 15 },
    "network": { "enabled": false, "thresholdBytesPerSecond": 131072, "durationMinutes": 15 }
  },
  "protectedProcesses": {
    "names": ["cmd"],
    "commandLinePatterns": [],
    "lockFile": null
  },
  "schedules": [],
  "energy": { "source": "Auto", "idleBaselineWatts": null, "electricityPricePerKwh": null, "currency": "CNY" },
  "retention": { "rawHours": 24, "minuteDays": 30, "eventDays": 365 },
  "logging": { "minimumLevel": "Information", "retentionDays": 30 }
}
'@ | Set-Content -LiteralPath $configPath -Encoding utf8
Start-Service PowerLease
Start-Sleep -Seconds 40
powerlease.exe status
powerlease.exe list
powercfg.exe /requests
```

Expected baseline:

- `status` says `State: Released`, no faults, no distrusted/stale sources, and no grace period.
- `list` says `No holds.`
- `powercfg /requests` has no `PowerLease.Service.exe` entry.

Historical SSH events or an interrupted earlier run may leave known test leases. List them, verify
they are test artifacts, then release each by its printed ID:

```powershell
powerlease.exe release <LEASE-ID>
```

Never release an unexplained owner's hold. If status reports an OpenSSH/event-log fault, fix that
precondition or record **MACHINE BLOCKED**; do not set `tcpOnlyConfirmed` merely to silence it.

Set the deliberate opt-in and build the test project:

```powershell
$env:POWERLEASE_REAL_MACHINE = '1'
dotnet build PowerLease.CrossPlatform.slnf -c Release
dotnet build tests\PowerLease.IntegrationTests -c Release -p:EnableWindowsTargeting=true
```

Expected: both commands report `Build succeeded` with zero warnings and zero errors. The first also
prepares the locked assets used by scenario 5's repeated `--no-restore` builds. Every test command
below uses `--no-build` so the test process itself does not rebuild between physical observations.

## Scenario 1 — A hold keeps the machine awake

Before starting, close remote sessions and leave only the local Administrator PowerShell. Run:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_1_A_hold_keeps_the_machine_awake" --logger "console;verbosity=detailed"
```

Wait: **five minutes**. As soon as the command starts, do not type, move the mouse, connect remotely,
or press a button. The display may turn off; that is not system sleep. Test output can be buffered
until completion, so do not wait for the quiet-window message before becoming idle.

Expected live evidence before and after the wait:

- JSON status is `Protected` because of a `CliLease` created by `hold 1h`.
- `PowerLease.Service.exe` is present under `SYSTEM:` and absent under `DISPLAY:`.
- The test continues after five minutes and finds no Kernel-Power 42/506 sleep-entry event in the
  interval.

The test releases only its own hold and restores the recorded AC timeout in `finally`.

Failure meaning:

- No lease/status/request, or a request under `DISPLAY`: **FAIL — product behavior is wrong**.
- `wake-status` says mains requests are not honoured or unknown: **MACHINE BLOCKED**.
- The SYSTEM entry is correct but an idle sleep event occurs: normally **FAIL**; if the capability
  probe or platform documentation says this hardware/policy ignores requests, attach that evidence
  and classify **MACHINE BLOCKED** rather than blaming request creation.

## Scenario 2 — Releasing lets Windows sleep on its own

Run locally:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_2_Releasing_lets_Windows_sleep_on_its_own" --logger "console;verbosity=detailed"
```

The test creates and verifies a one-hour hold, releases that exact lease, proves status is `Released`
and the SYSTEM request is gone, then waits. Note the wall-clock time when you launch the command and do
nothing to the target. Watch reachability only from the second host. When the target becomes
unreachable, keep it asleep until **at least six minutes and 30 seconds after command launch**, then
wake it with the already-configured magic packet or physical power button. Do not wake it by starting
SSH. The exact release timestamp is printed with the completed result; console test output may be
buffered until then.

Expected after wake:

- Kernel-Power 42 (`System Idle`, data reason 7) plus 107, or Modern Standby 506 plus 507.
- A Power-Troubleshooter event 1 or Modern Standby wake reason.
- The same PowerLease service PID before and after sleep.
- `SCENARIO 2 RECORD` prints the actual sleep latency; copy that number and the event summaries.

The event log is the assertion across sleep: the test process being able to resume is not treated as
proof by itself. No test command initiated the transition.

Failure meaning:

- Request remains after release, or status still says it should hold: **FAIL — product release bug**.
- Event 42 reports Application API, button/lid, or anything other than System Idle: **invalid run**;
  something forced sleep, so repeat without it.
- Request is absent but Windows never sleeps: inspect `powercfg /requests`, `powercfg /a`, the active
  timeout, and `powerlease wake-status`. Another request, disabled sleep, firmware, unattended-sleep
  policy, or ongoing user input is **MACHINE BLOCKED**, not a PowerLease defect.

## Scenario 3 — An SSH login creates a hold

If scenario 2 left the target asleep, wake it without SSH first and wait for the local test window to
return. Start the test locally:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_3_An_SSH_login_creates_a_hold" --logger "console;verbosity=detailed"
```

It waits up to **five minutes**. Immediately after launching the test, connect from the second host
and keep the shell open (do not wait for live test output, which may be buffered):

```powershell
ssh <USER>@<TARGET-IP-OR-NAME>
```

Expected within about 20 seconds:

- `powerlease list --json` gains one new `SshSession` lease with a positive remaining duration.
- `status` reports a live `SshSession` inhibitor and `Protected`.
- `powercfg /requests` attributes the SYSTEM request to `PowerLease.Service.exe`, not DISPLAY.

The passing result prints the lease ID and remaining seconds. **Leave this SSH shell open and start
scenario 4 immediately in the target's local PowerShell.**

Failure meaning:

- SSH itself cannot connect: **MACHINE BLOCKED — network/OpenSSH precondition**.
- Status says the OpenSSH log or TCP table cannot be trusted: **MACHINE BLOCKED** unless the service
  has permission and a functioning channel, in which case investigate the adapter.
- A confirmed live session produces neither inhibitor nor lease, or produces them without a SYSTEM
  request: **FAIL — product behavior is wrong**.

## Scenario 4 — Disconnect keeps the remaining lease, then Windows sleeps

With scenario 3's SSH shell still open and at least 30 seconds remaining on its three-minute lease,
run locally:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_4_A_disconnect_keeps_the_remaining_lease_then_Windows_sleeps" --logger "console;verbosity=detailed"
```

Immediately after launching the test, type this in the second-host SSH shell (within **30 seconds**;
do not wait for test output):

```text
exit
```

Expected live sequence:

1. The live `SshSession` inhibitor disappears.
2. The same SSH lease remains with positive time, and `PowerLease.Service.exe` remains under SYSTEM.
3. The test samples the lease and request until 15 seconds before reported expiry, then makes no
   command calls across the sleep boundary. After wake, it proves the lease and request released.

After disconnect, do not touch the target. Allow up to **nine minutes total** (three-minute remaining
lease, two-minute sleep timeout, test tolerance). Once it is asleep and at least nine minutes have
passed, wake it with the existing magic packet or physical power button, not SSH.

Expected after wake: no sleep entry before the lease's calculated expiry, followed by an idle
Kernel-Power sleep/wake pair after expiry. The test restores the original AC timeout.

Failure meaning:

- Lease or SYSTEM request disappears with the TCP session: **FAIL — dangerous early release**.
- Lease never expires or request remains afterward: **FAIL — over-protection/release bug**, after
  first confirming there is no other lease or inhibitor.
- Lease and request release correctly but Windows does not sleep: **MACHINE BLOCKED — Windows power
  configuration or another request**, not a product defect.

## Scenario 5 — A protected long build keeps the machine awake

Confirm there are no unrelated `cmd.exe` processes (`Get-Process cmd -ErrorAction SilentlyContinue`
should print nothing). The test creates a temporary batch wrapper whose `cmd` process repeatedly runs
the real cross-platform solution build. Run:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_5_A_protected_long_build_keeps_the_machine_awake" --logger "console;verbosity=detailed"
```

Wait: about **four minutes**. For three full minutes after detection, the test asserts on every sample
that the exact `cmd:<pid>` protected-process inhibitor remains, the build process remains alive, the
SYSTEM request remains, DISPLAY has no PowerLease request, and the event log has no sleep entry. It
then stops only its temporary process tree, verifies at least one real `dotnet build` succeeded,
waits for the 15-second process poll to remove that inhibitor, and proves protection releases.

Failure meaning:

- The configured exact PID never appears as `ProtectedProcess`: **FAIL — process rule/adapter bug**.
- It appears but SYSTEM protection drops while the process is alive: **FAIL — dangerous early release**.
- It remains after that PID exits: first check for another `cmd.exe`; contamination is an **invalid
  run**, while the exact exited PID remaining is a **FAIL**.
- The build itself fails: **test/source checkout precondition**, not evidence about PowerLease.

## Scenario 6 — A killed service recovers

This is the only scenario that intentionally terminates a product process. It does not request a
power transition. The test first creates a durable one-hour CLI lease and verifies its SYSTEM request,
then force-kills the service PID. The installed SCM recovery action must restart it.

Run:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_6_A_killed_service_recovers_and_records_the_unprotected_window" --logger "console;verbosity=detailed"
```

Expected in under 30 seconds:

- The sampler observes `PowerLease.Service.exe` disappear from SYSTEM.
- SCM starts a different service PID.
- The durable CLI lease reappears in protected status and the SYSTEM request returns, never under
  DISPLAY.
- `SCENARIO 6 RECORD` prints two measurements: the observed request-absent interval and the
  conservative interval from issuing the kill through the first restored observation. Record both,
  plus the old/new PIDs. The latter is the release-note recovery-window number.

This non-zero window is a documented limitation. Windows destroys a process's request handle when the
process dies; closing the gap would require a kernel driver.

Failure meaning:

- SCM never starts a new PID despite the verified recovery action: **FAIL — installer/service recovery
  contract**, unless machine security policy blocked SCM, which is **MACHINE BLOCKED** with its event.
- New PID starts but the lease or SYSTEM request is not restored: **FAIL — dangerous recovery bug**.
- Sampling never catches absence because recovery is faster than `powercfg` resolution: mark the run
  **inconclusive** and repeat; do not record zero milliseconds.

## Scenario 7 — The request is SYSTEM, never DISPLAY

Run:

```powershell
dotnet test tests\PowerLease.IntegrationTests -c Release --no-build --filter "FullyQualifiedName=PowerLease.IntegrationTests.RealMachineScenarioTests.Scenario_7_The_keep_awake_request_is_SYSTEM_and_never_DISPLAY" --logger "console;verbosity=detailed"
```

Expected:

- Status is protected by the test's ten-minute CLI lease.
- The full `SCENARIO 7 RECORD` has `PowerLease.Service.exe` beneath `SYSTEM:`.
- `PowerLease.Service.exe` does not appear beneath `DISPLAY:`. Other programs' DISPLAY requests do
  not change the product assertion, but close those programs first when possible so the captured
  evidence is unambiguous.

Copy the complete `powercfg /requests` block into the release record. The test releases its own hold.

Failure meaning: a PowerLease entry under DISPLAY, no PowerLease entry under SYSTEM while status wants
protection, or attribution to the wrong executable is **FAIL — product request-type/ownership bug**.
If `wake-status` says the plan refuses SYSTEMREQUIRED, record **MACHINE BLOCKED** separately; it does
not make a DISPLAY request acceptable.

## Undo and verify restoration

Run this even when every test passed. First restore the exact power-plan value recorded during
preparation:

```powershell
powercfg.exe /setacvalueindex $activeScheme SUB_SLEEP STANDBYIDLE $originalAcSleepSeconds
powercfg.exe /setactive $activeScheme
powercfg.exe /query $activeScheme SUB_SLEEP STANDBYIDLE
```

Expected: the first current setting index is the original value (shown in hexadecimal). If the
PowerShell window was lost, substitute the recorded GUID and decimal seconds literally:

```powershell
powercfg.exe /setacvalueindex <RECORDED-SCHEME-GUID> SUB_SLEEP STANDBYIDLE <RECORDED-DECIMAL-SECONDS>
powercfg.exe /setactive <RECORDED-SCHEME-GUID>
```

Restore the owner's PowerLease configuration:

```powershell
Stop-Service PowerLease
if (Test-Path $configBackup) {
    Copy-Item $configBackup $configPath -Force
    Remove-Item $configBackup
} elseif (-not $configExisted) {
    Remove-Item $configPath -ErrorAction SilentlyContinue
} else {
    throw 'The recorded original configuration existed, but its backup is missing. Restore it manually.'
}
Start-Service PowerLease
Start-Sleep -Seconds 40
powerlease.exe status
powerlease.exe list
powercfg.exe /requests
Remove-Item Env:POWERLEASE_REAL_MACHINE -ErrorAction SilentlyContinue
```

If the original configuration did not exist, `$configExisted` is `False` and there should be no
backup; remove the test `config.json`, then start the service so defaults load. If the shell variables
were lost, inspect `C:\ProgramData\PowerLease\config.m7-backup.json`: if present, copy it over
`config.json`, delete only that M7 backup after verifying the copy, and restart the service.

Final checks:

- The AC sleep timeout matches the recorded original value.
- No `M7 scenario` CLI lease remains. Release a known leftover by its printed ID; never release an
  unexplained hold.
- No temporary `PowerLease-M7-*.cmd`, `.log`, or `.succeeded` file remains under the current user's
  temp directory.
- The original configuration is active and the service is running.
- No SSH, hibernation, BIOS, wake-source, NIC, or security-policy setting was changed by this run.

## What the tests assert and what the person supplies

| Scenario | Automatic assertions | Human evidence/action |
| --- | --- | --- |
| 1 | CLI lease/status, SYSTEM not DISPLAY before/after, five-minute Kernel-Power window has no sleep | Leave target completely untouched for five minutes |
| 2 | Hold existed, exact release, request absent, idle sleep and wake events, same service PID, latency | Keep target idle; after six minutes wake without SSH; retain event summaries and latency |
| 3 | New SSH lease, live SSH status inhibitor, SYSTEM not DISPLAY | Establish SSH from the second host and keep it open |
| 4 | Live inhibitor ends, same lease/request remains to expiry, no early sleep, request releases, later idle sleep/wake | Disconnect when prompted; wait up to nine minutes; wake without SSH |
| 5 | Exact build-wrapper PID is protected throughout, SYSTEM not DISPLAY, no sleep entry, real build succeeds, inhibitor then releases | Keep unrelated `cmd.exe` processes closed |
| 6 | Request absence is sampled, PID changes, durable hold and SYSTEM request recover, intervals printed | Record both timing numbers and PIDs |
| 7 | Full request is attributed to service under SYSTEM and not under DISPLAY | Paste complete `powercfg /requests` output |

The person never substitutes an observation for an empty test. Every scenario has a live assertion
that can fail; the manual steps provide only evidence a process cannot create or observe while the
machine is suspended.
