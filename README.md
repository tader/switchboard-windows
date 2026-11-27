# Switchboard for Windows

A native Windows 11 tray manager for a local Switchboard server, for x64 and ARM64 PCs. It uses Windows controls with the same settings organization as the macOS app.

**Development status:** the first release is not published. Downloadable CI artifacts are unsigned development builds. A compatible Windows core runtime must be released before first setup can succeed. Public Windows installers are intentionally unsigned; Windows may show an unknown-publisher warning or block installation under stricter security policies.

## Setup and use

Install the installer matching your PC from GitHub Releases. Choose **Set Up Switchboard**. The app downloads a compatible server runtime, verifies it, and opens initial account setup in your browser. No WSL, Docker, Node, .NET installation or developer tools are needed.

Left-click the tray icon to open a running Switchboard; otherwise it opens Settings. Right-click for Open, Start, Stop, Restart, Settings, Show Logs and Quit. Closing Settings leaves the app running. Quit waits for owned server shutdown and installation cleanup.

General shows the server and account status, startup preferences, and app and server updates. When connections need attention, the tray menu offers **Sign in to connections (N)** and opens Connections in the browser. **Windows app** is the tray manager; **Server** runs Switchboard in your browser. App updates check stable releases daily and ask before downloading and installing. Server installation is manual, preserves data, and starts the updated server automatically. Choose Stable or Development; Development requires published compatible prereleases. Advanced contains folders, networking, optional version pinning, reinstall and lock recovery.

Login startup is off initially. Starting the server when the app opens is a separate preference. Windows may disable a startup entry in Startup Apps; use Windows Settings to re-enable it.

## Data and recovery

Defaults use `%LOCALAPPDATA%\Switchboard\Data` for server data, `Updater` for runtimes, logs and backups, and `settings.json` for manager preferences. The listener defaults to `127.0.0.1:8770`. These folders use Windows ACLs limited to the owning user and SYSTEM. Use separate local NTFS folders. Existing installations must be stopped before adoption.

Password recovery requires a running server and fresh Windows Hello/PIN verification. If verification is unavailable or cancelled, no recovery link is requested. The account password is entered only in your browser. Links expire and are single-use; they are not logged or saved by the app. A Windows account controlling this installation can control its data; Hello verification does not protect against malware running as that account.

Installation holds the core SQLite ownership leases, retains a verified full backup and rehearses database migrations on a copy. Unsupported reparse points are refused. Relocation copies and verifies new folders while retaining originals; select **Copy and verify folders when moving data**. Keep independent backups containing the database, encryption key, extension state and browser profiles together.

If installation reports an unresolved journal, preserve both data and updater folders before making recovery decisions. **Recover Server Lock** archives advisory lock metadata only after proving the ownership leases are free; it does not kill an unrelated process. Never delete lease databases to bypass ownership.

## Build

Use the .NET 10 SDK on Windows: `dotnet publish src/Switchboard/Switchboard.csproj -c Release -r win-x64 --self-contained true` (or `win-arm64`). CI builds both architectures and runs ownership, relocation and WPF-rendering smoke checks. Developer builds do not require release-signing credentials.

See [release setup](development/releases/README.md) for signing prerequisites. App updates authenticate a P-256-signed feed and package hashes; public release packaging signs update metadata using the repository’s protected key, without Azure or a code-signing certificate. Installed app files are separate from accounts, server data and updater state.

### GitHub rate limits

In **Settings → General**, below Updates, optionally save a GitHub personal access token before or after setup. The token is masked while entering and stored for your Windows user in Credential Manager. Choose public repositories only with no additional permissions; see [GitHub token setup](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens). Use **Save Token** to replace it or **Remove Token** to return to anonymous requests.

The app uses it for server release listings and revision verification. An updated server runtime also uses it only after a rate-limit response during plugin installation, including catalog browsing and dependencies. It is not used for plugin updates, plugin update checks, server updates or a GitHub connection. Token changes affect new desktop requests immediately; stop and start the local server to apply changes there. Older server runtimes ignore the installation token.
