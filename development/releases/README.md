# Windows release setup

Windows executables and installers are intentionally unsigned. Azure Artifact Signing, certificates, paid signing services and OIDC credentials are not required. Release assets and titles identify unsigned installers explicitly; Windows may warn about or block them according to the device’s security policy.

Update authenticity remains mandatory. `UPDATE_FEED_PRIVATE_KEY` is a P-256 key stored in the protected `windows-release` environment, paired with the embedded public PEM. The key is already provisioned. No private key is committed. Public release builds verify the signed feed and package SHA-256 hashes before installing, ask for confirmation, and stop the owned server safely. Development builds keep app updating disabled. Preserve the key pairing; changing it requires a trust transition for installed apps.

Release Please creates a draft release and tag. The release job builds self-contained x64/ARM64 installers, signs architecture-specific update metadata, verifies the signature and uploads assets. Both architecture jobs must succeed before publication. The release environment permits only main and version tags. Missing feed credentials stop publication. Installer and portable filenames include `unsigned`; updater package filenames remain those authenticated by the signed feed.

Before the first public release: publish compatible core Windows runtimes, run native lifecycle/UI checks on both architectures, install the generated installers and exercise a second release through the real updater. Confirm cancellation, safe shutdown and preservation of settings/data. Live Windows Hello and interactive accessibility acceptance remain part of first-release validation.
