# Windows manager implementation

The app follows the desktop manager settings hierarchy with native Windows controls. It uses a contained, owned Node supervisor over private pipes, independent server/app update preferences, Windows Hello recovery and per-user NTFS storage. The activation pipe accepts only a fixed show-settings message and is restricted to the current Windows user. Operational and recovery commands remain on inherited supervisor pipes.

Verification completed during implementation:

- .NET 10 compilation of app and smoke harness with no warnings or errors.
- Native x64 and ARM64 builds and smoke checks: settings/path validation, ownership contention, stale-lock archival, verified relocation, recovery authorization denial, update-feed tamper rejection, light/dark rendering at wide/narrow widths, and actual C# manager start/stop/restart against a pinned core checkout.
- Real executable entry-point checks verify that closing Settings leaves the tray app alive and another launch activates it.
- Unsigned development installer packaging has passed on both architecture runners. These packages are CI artifacts, never unsigned public releases.
- Visual review of Windows renders caught and corrected transparent surfaces and a missing Server update panel; a visual-tree assertion now requires both update panels.
- A transient Windows cleanup sharing violation in one duplicate run led to bounded private-staging cleanup retries. Persistent errors still fail.

The core dependency supplies native prepared runtimes, private graceful shutdown, Windows browser discovery and pipe ownership. Its native browser tests cover the installed browser through direct pipes and through its watchdog; no debugging TCP endpoint is used. Core changes and fixtures remain generic.

## Release prerequisites still requiring owner configuration

Azure Artifact Signing is not configured: the windows-release environment has no Azure variables yet. The update feed key is provisioned as an environment secret, paired with the embedded public key. The release environment accepts only main and version tags. Public releases remain draft until both signed architecture jobs succeed. No Azure resources were purchased or created.

Before the first public release, publish a compatible core release containing Windows runtime assets, configure and validate the Azure Public Trust signing profile and OIDC identity, and exercise signed install/update flows and live Windows Hello recovery on a suitable Windows machine. Automated tests do not simulate a successful OS verification as proof of actual Windows Hello behavior. High contrast and increased text scaling also need an interactive final acceptance pass.
