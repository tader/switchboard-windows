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

## Release prerequisites

The owner chose to skip Windows code signing after Azure eligibility was checked. This supersedes the earlier Azure-signing plan. There are no Azure resources, billing or signing-profile prerequisites. Installer files and release titles explicitly identify unsigned builds; public update authenticity continues to rely on the P-256-signed feed and package hashes. The feed key is provisioned in the protected release environment.

A compatible core release with Windows assets must precede first setup. Signed-feed update installation across two real release versions, live Windows Hello verification, and final interactive accessibility acceptance still need first-release validation. Automated denial/tamper tests are not represented as proof of successful live Windows Hello or a completed public update.
