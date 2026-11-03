# Release bundles and draft releases

Tailwatch currently ships **unsigned preview binaries**. Windows Authenticode signing and Apple Developer ID signing/notarization are not configured. SHA-256 checksums detect changed archive bytes; they do not establish publisher identity. Review the source build, asset provenance and platform trust prompts before using or publishing a bundle.

## What CI produces

`Build and test` builds the Rust agent on Windows x64, Ubuntu 22.04 x64 and macOS 15 Apple silicon, and the self-contained .NET 10 Windows x64 viewer. Existing Rust and viewer tests use controlled fixtures without Tailscale credentials. The packaging job waits for both agent and viewer builds, runs package validation tests, and combines their artifacts into:

| Bundle | Exact contents |
| --- | --- |
| `Tailwatch-v<VERSION>-windows-x64.zip` | `Tailwatch.exe`, `tailwatch-agent.exe`, `install-windows.ps1`, `uninstall-windows.ps1`, `README.md`, `LICENSE` |
| `Tailwatch-v<VERSION>-linux-x64.zip` | `tailwatch-agent`, `install-linux.sh`, `README.md`, `LICENSE` |
| `Tailwatch-v<VERSION>-macos-arm64.zip` | `tailwatch-agent`, `install-macos.sh`, `README.md`, `LICENSE` |

`SHA256SUMS.txt` accompanies the three archives in the `release-bundles` CI artifact. Viewer PDBs remain available in the original viewer CI artifact and are deliberately excluded from release bundles. Any other unexpected file inside an input artifact causes packaging to fail. PE x64, little-endian ELF x64 and Mach-O arm64 headers are checked before packaging, then every archive entry is decompressed and hashed against its source. Unix agents and shell scripts carry executable archive permissions, although some ZIP extractors discard them.

Native smoke jobs download these completed bundles, verify checksums, extract them, execute the packaged agent's `--version`, and parse their platform installers without installing anything. On the isolated Windows runner, the normal packaged viewer is also launched with the existing UI test executable supplying a synthetic Tailscale CLI identity. The test checks that the viewer stays alive and its packaged local-only agent serves fresh, advancing snapshots on loopback, then stops that viewer's process tree. No Tailscale credentials or service installation are needed. The workflow succeeds only after these checks pass. Native checks do not test service registration, physical sleep/reboot, hardware sensor availability, or OS trust prompts; those remain manual checks on suitable machines.

## Reproduce locally

Use PowerShell 7.2 or newer. Download all four CI artifacts into separate directories named `windows-x64-agent`, `windows-x64-viewer`, `linux-x64-agent`, and `macos-arm64-agent`. Use a checkout at the **same commit** as that build. The version must be valid SemVer without a leading `v` and must match both `agent/Cargo.toml` and `viewer/Tailwatch.App/Tailwatch.App.csproj`.

```powershell
pwsh -NoProfile -File tests/package-release.tests.ps1
node --test tests/package-workflow.tests.cjs
pwsh -NoProfile -File scripts/package-release.ps1 `
  -ArtifactRoot ./artifacts -OutputDirectory ./release-bundles -Version 0.2.0
```

Choose a new output directory each time. Missing inputs, unexpected files, mismatched architecture, invalid/mismatched versions and an existing output directory all fail before output creation. The script never deletes an existing directory. An I/O failure after validation can leave partial output for inspection; retry with a fresh destination. The tests use header-only synthetic fixtures, so also run the script against real CI artifacts before changing release behavior.

On Windows, compare `Get-FileHash <archive> -Algorithm SHA256` to `SHA256SUMS.txt`. On Linux use `sha256sum -c SHA256SUMS.txt`; on macOS use `shasum -a 256 -c SHA256SUMS.txt`. After extraction, Unix users can restore executable permission with `chmod +x tailwatch-agent`. Follow the included README and installers for setup.

## Create a draft prerelease

The manual `Create draft prerelease` workflow must first exist on the repository's default branch for GitHub to expose its dispatch UI. It accepts a successful `Build and test` **run ID** and the source **version** without `v`. For a new version, update both source manifests before building. Existing published versions are never overwritten.

The read-only validation job checks that the selected run belongs to this repository, uses `.github/workflows/build.yml`, was triggered by a push or manual dispatch, and completed successfully. Pull request runs are rejected. It fetches both version manifests at the run's exact commit SHA, then downloads that run's unexpired `release-bundles` artifact. It verifies the four expected filenames and every archive checksum before promoting the files to the draft job.

Only the draft job has `contents: write`. It performs no checkout and executes no code from the selected build or downloaded files. It checks the promoted filenames/checksums again, refuses any existing tag or release with the requested version, and creates a **draft prerelease** targeting the validated commit SHA. It uploads the three archives and checksum file. Nothing is published automatically, and no existing tag or release is edited. If upload fails midway, inspect the partial draft manually; repeated dispatch refuses to overwrite it.

Review the draft's target commit, complete asset list, checksums, unsigned status and native/manual test evidence before deciding to publish in GitHub. Workflow actions are pinned to official upstream commits; update those pins deliberately.

## Future signing credentials

No signing credentials are currently available or requested by these workflows. Signing must be added as an explicit release step before hashes and archives are finalized, followed by signature verification on native runners. Do not claim signing or notarization until these steps have completed with real credentials.

For Windows Authenticode, configure one of these release-signing arrangements:

- An Azure Artifact Signing account (formerly Trusted Signing), completed identity validation, a certificate profile, its service endpoint, and an authorized Azure identity. An OIDC setup needs the Azure tenant ID, client ID, a federated credential scoped to the repository's protected release environment, and the Artifact Signing Certificate Profile Signer role. The protected signing job needs `id-token: write` and must use the configured account/profile names. See Microsoft's [account and identity setup](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) and [signing integrations](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations).
- A code-signing certificate and its matching private key accessible through the certificate provider/HSM on the signing runner, with the provider authentication/PIN or service credentials stored in a protected environment. The certificate alone is insufficient. Configure a trusted timestamp service and verify the resulting Authenticode chain and timestamp for each Windows executable and signed PowerShell installer.

For macOS Developer ID signing and notarization, provide the Apple Developer Program team ID, a Developer ID Application certificate **with its private key** (typically a password-protected `.p12`) and its import password. Notarization also needs an App Store Connect API private key (`.p8`), key ID and issuer ID authorized for that team, or an Apple ID with an app-specific password and team ID. Import the identity into a temporary keychain on the macOS signing runner, sign with the required runtime options, submit the distribution archive to Apple's notary service, wait for acceptance, and verify the result using Apple's tools. Stapling support depends on the final distribution format; a raw command-line Mach-O binary cannot itself be stapled. Choose an appropriate signed/stapleable container before promising offline notarization behavior. Follow Apple's [notarization guide](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution) and [custom workflow instructions](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow).

Keep these credentials in protected release environments, require the intended reviewer policy, and do not expose them to pull request builds. Signing enrollment, paid accounts, public publishing and platform security bypasses are outside the current automation.
