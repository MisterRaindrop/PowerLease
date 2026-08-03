# PowerLease

Keep-awake tool for remote Windows development machines.

PowerLease tells Windows "I am SSH'd in, or my build is running, so do not sleep right now."
It holds a Windows power request while something is genuinely in use, and releases it when
nothing is.

**PowerLease never puts your machine to sleep.** It calls no power-transition API at all.
Windows keeps sleeping on idle according to your own power plan; PowerLease only prevents that
from happening at the wrong moment. The failure mode this design rules out is a tool that sleeps
a machine you were using; the failure mode it accepts is a machine that stays awake longer than
strictly necessary.

## What it does

```
inhibitor present         ->  hold the power request   ->  machine stays awake
no inhibitor              ->  release it               ->  Windows sleeps per your power plan
cannot prove there is     ->  keep holding             ->  fail safe
no inhibitor
```

An inhibitor is anything Windows itself cannot see: an active SSH session, a manual hold, a
protected process such as a long build, a lock file, a scheduled always-awake window, or
sustained CPU/disk/network activity. Keyboard and mouse activity is deliberately **not** on that
list, because the Windows idle timer already resets on input.

The decision is an OR, not an AND: any one inhibitor is enough to keep the machine awake, and
uncertainty counts as an inhibitor. A rule can only add protection, never remove it.

## Status

Early. This is `v0.x`, which means:

- **No graphical interface.** State is visible through the CLI and the log files.
- **No automatic sleep or hibernate**, by design, not as a missing feature. See
  `Docs/PowerLease-Design.md` if you have it, or the release notes, for why that was separated
  out.
- **Artifacts are not code-signed.** There is no certificate for this project, so SmartScreen
  will warn about an unknown publisher. Verify what you download against `SHA256SUMS.txt` and
  the build provenance attestation attached to each release.
- Releases are marked as prereleases.

### v0.1.0 real-hardware release gate

`v0.1.0` is not complete until all seven scenarios in the
[real-machine runbook](tests/PowerLease.IntegrationTests/RealMachine.md) have passed on physical
Windows hardware. CI deliberately excludes these tests: only a real machine can demonstrate that a
SYSTEM request prevents idle sleep and that, after release, Windows sleeps by itself.

## Install

Download `PowerLease-Setup-X.Y.Z-x64.exe` from the
[releases page](https://github.com/MisterRaindrop/PowerLease/releases) and run it. It requires
administrator rights, because it installs a per-machine Windows service.

Silent install and uninstall:

```powershell
PowerLease-Setup-X.Y.Z-x64.exe /quiet /norestart
PowerLease-Setup-X.Y.Z-x64.exe /uninstall /quiet
```

### The service is stopped right after installing

This is deliberate, and it is the one surprising thing about the installer.

The service is registered as **Automatic (Delayed Start)**, so it comes up on its own at the
next boot. It is not started during installation. To start it immediately:

```powershell
Start-Service PowerLease
```

The reason: MSI runs its `StartServices` action *before* `InstallFinalize`, and WiX's
`ServiceControl` element has no condition attribute, so "start the service now" cannot be made
conditional on the install having committed. Starting early would let a freshly installed
service begin migrating data while the installation could still roll back to the previous
binaries, which would then be facing a newer schema. Leaving it stopped removes that class of
failure entirely, at the cost of one command or one reboot.

### What uninstalling keeps

Uninstalling removes the program files and deregisters the service, and **keeps** everything
under `C:\ProgramData\PowerLease` — configuration, database and logs. That is not a special
case in the uninstaller; those files are simply not owned by the installer, so it has nothing to
remove.

To remove them as well:

```powershell
PowerLease-Setup-X.Y.Z-x64.exe /uninstall /quiet REMOVEUSERDATA=1
```

### Layout

```
C:\Program Files\PowerLease\Service\    the Windows service
C:\Program Files\PowerLease\Cli\        powerlease.exe, added to the system PATH
C:\ProgramData\PowerLease\              configuration, database, logs (survives uninstall)
```

Service and CLI live in separate directories because each is a self-contained .NET publish
carrying its own runtime; one directory would mean two sets of identically-named runtime files.
Only the CLI directory is put on `PATH`.

## Command line

```
powerlease status         current state, active inhibitors, and what is NOT covered
powerlease hold 3h        keep the machine awake for a while
powerlease release        drop your own hold
powerlease list           active holds
powerlease wake-status    wake-on-LAN and scheduled-wake diagnostics
```

There is no `powerlease sleep`. This version does not put the machine to sleep, and holding the
power request does not stop you from doing it yourself:

```powershell
rundll32.exe powrprof.dll,SetSuspendState 0,1,0   # sleep
shutdown /h                                        # hibernate
```

### Check that the inhibit is actually working

`powerlease status` reports whether the power request is being **honoured**, not just whether it
was made. A power plan with `SYSTEMREQUIRED` disabled, or Modern Standby on battery, can make
Windows ignore the request entirely while the API still reports success. When that happens the
state is reported as `Unprotected` and `powerlease status` exits non-zero, so it can be used in
a monitoring check.

## Known limitations

- **There is a short unprotected window if the service crashes.** The OS reclaims the power
  request as soon as the process dies, and Windows is free to sleep the machine until the
  service is restarted. The service is configured to restart immediately on failure and to
  re-establish the inhibit early in startup, but the window cannot be closed entirely without a
  kernel driver, which this project does not use.
- Wake-on-LAN and scheduled wake are diagnostics only in this version.
- Temperature and power-consumption history are not implemented yet.

## Building

Requires the .NET SDK pinned in `global.json`.

```bash
dotnet build PowerLease.CrossPlatform.slnf -c Release   # works on macOS and Linux
dotnet test  PowerLease.CrossPlatform.slnf -c Release
```

`PowerLease.CrossPlatform.slnf` contains only the `net10.0` projects, which is where the
safety-critical logic lives so it can be tested anywhere. The full solution and the WiX
installer need Windows:

```powershell
dotnet build PowerLease.sln -c Release
dotnet test  PowerLease.sln -c Release --filter "Category!=RealMachine"
```

Tests marked `Category=RealMachine` need physical hardware and are excluded from CI.

`dotnet restore` on a `.wixproj` hangs on macOS, so the installer can only be built and verified
on Windows or in CI.

## License

See [LICENSE](LICENSE). Bundled third-party components retain their own licenses.
