# Security

## Reporting a problem

Please report security issues privately through GitHub: **Security › Report a vulnerability** on this repository.
Don't open a public issue for them.

## What the app does and doesn't do

- **No accounts, no secrets.** The app signs in nowhere and stores no passwords or tokens. It reads public
  pricerunner.se data anonymously and GitHub's public release list anonymously.
- **Local data only.** Your watchlist, price history and settings stay in `%LOCALAPPDATA%\PriceRunnerPanel` on your PC.
- **Links** opened from the app are only `https` URLs, passed to your default browser. Product pages always point
  at pricerunner.se; "View on GitHub" always points at this repository's releases.
- **Startup** uses your per-user Run key (no admin rights); turn it off from the tray menu or Task Manager.

## Updates

- Updates are never installed on their own: you click **Install update**.
- The app downloads only from this repository's release URLs on `github.com`, over HTTPS, with a size limit.
- The zip is checked against the SHA-256 published with the release; on a mismatch nothing is installed.
- The app only replaces its own folder: one named `PriceRunnerPanel` that holds nothing but the files its release
  shipped (listed in `release.json`). Extracted somewhere shared, like Downloads, it refuses and says why.
- The unpacked copy must contain this app at the release's version before it may replace the installed folder.
  The previous version is kept next to it as `<folder>.previous`, and a failed swap restores it.
- Release notes are shown as plain text, never rendered as markup.
- Releases are built by the **Release** workflow from a version tag, with GitHub Actions pinned to commit SHAs, a
  locked NuGet restore, and a signed **build provenance attestation** for each zip. You can check a download with:

  ```bash
  gh attestation verify PriceRunnerPanel-X.Y.Z-win-x64.zip --repo PantaKoda/PriceRunnerPanel
  ```

- **Not code-signed:** the executable has no Authenticode signature, so Windows SmartScreen may warn on first run.
  The checksum and attestation prove a download came from this repository's release workflow; they can't protect
  against someone who controls the repository publishing a malicious release.
