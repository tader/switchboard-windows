# Windows release setup

Public release publication requires Azure Artifact Signing with a validated Public Trust identity and certificate profile. No paid Azure resources are created by repository tooling. The owner must complete subscription setup, identity validation and certificate profile creation in Azure.

Create a GitHub environment named `windows-release`. Add environment variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, and `ARTIFACT_SIGNING_PROFILE`. Configure the Azure application federated credential for this repository's `windows-release` environment, and grant only Certificate Profile Signer access to the selected profile. Workflows authenticate with OIDC, not a saved Azure password.

`UPDATE_FEED_PRIVATE_KEY` is a `windows-release` environment secret containing the P-256 private key corresponding to the public PEM embedded in the app. The initial pair is already provisioned; no private key is committed. Preserve its public/private pairing across releases. Key changes require a deliberately planned trust transition for installed apps.

Release Please updates version.txt and maintains the release PR. Release Please creates a draft release and its tag. After merge, the signed release job checks signing prerequisites, builds self-contained x64/ARM64 apps, signs via Azure, verifies the installer signature, signs the architecture-specific update feed and uploads release assets. The release becomes public only after both signed architecture jobs succeed. A missing signing configuration stops publication; there is no unsigned-public-release fallback. An unsigned ZIP attached only as a CI artifact is a development build.

Before the first public release: publish core Windows runtimes, run Windows UI/lifecycle checks on both architectures, install each signed installer, verify Windows signatures, exercise a second signed version through the real updater, check cancellation and server shutdown, and confirm settings/data survive uninstall or update. Signing does not guarantee immediate SmartScreen reputation.
