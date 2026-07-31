PowerLease {{VERSION}}

Artifacts are {{SIGNED}}. There is no code-signing certificate for this project yet, so
SmartScreen will warn about an unknown publisher. Verify downloads against `SHA256SUMS.txt`
and the build provenance attestation attached to this release.

**This version keeps the machine awake; it never puts it to sleep.** Windows continues to
sleep on idle according to your own power plan. There is no automatic sleep and no graphical
interface in this version.

After installing, the service is registered but **stopped**. It starts on the next boot
(Automatic, Delayed Start), or immediately with:

    Start-Service PowerLease

This is deliberate: MSI runs its `StartServices` action before `InstallFinalize` and WiX's
`ServiceControl` has no condition attribute, so "start it now" cannot be made conditional on
the install having committed. Leaving it stopped removes the risk of a new service migrating
data that a rolled-back install would then leave older binaries facing. See the README.

## Install

    PowerLease-Setup-{{VERSION}}-x64.exe /quiet /norestart

Uninstalling keeps everything under `C:\ProgramData\PowerLease` (configuration, database,
logs). To remove that too:

    PowerLease-Setup-{{VERSION}}-x64.exe /uninstall /quiet REMOVEUSERDATA=1
