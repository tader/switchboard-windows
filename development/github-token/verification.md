# GitHub token implementation

- Stores optional credentials with Windows Credential Manager, per user and computer. No Settings schema change.
- GitHub API metadata requests use a dedicated no-redirect client; public asset and app-update clients never receive this credential.
- Server startup includes only the restricted plugin-install environment value over the private pipe. Changes require a server restart.
- Smoke coverage includes isolated native credential save/reload/replace/remove, token validation, origin guards, response errors, startup environment and serialization, plus masked UI presence.
- Local validation: `git diff --check` passed. This macOS host has no .NET SDK or Windows UI runtime; builds, native credential tests and WPF renders require the existing Windows CI matrix.
