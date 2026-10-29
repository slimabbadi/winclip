# Code signing policy

WinClip releases are currently unsigned. No signing provider is configured. The release workflow rejects tag publication unless the installer and WinClip binaries have valid Authenticode signatures from the same publisher. Manual packaging and lifecycle verification remain available without signing.

## Provider choice

The selected route is SignPath Foundation. The application was submitted on 29 October 2026 with the maintainer's approval. After refreshing a stalled form and restoring the approved details, the site confirmed: “Form submitted” and “Thank you, we'll be in touch soon.” Optional marketing was disabled. Foundation approval is pending; submission confirmation is not signing approval.

The maintainer publishes as an individual in Germany. Microsoft's [Artifact Signing prerequisites](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) currently limit public-trust individual enrollment to the US and Canada, so that service is not available for this identity.

[SignPath Foundation](https://signpath.org/terms) offers signing for qualifying open-source projects. Acceptance is discretionary and requires a verifiable reputation, published software, a public signing policy, MFA, verifiable hosted builds, and manual approval of signing requests. The certificate identifies SignPath Foundation as publisher. WinClip has not been approved.

If acceptance or the Foundation publisher identity is unsuitable, use a paid public-trust certificate provider that accepts German individuals. Confirm eligibility, identity requirements, price, and secure CI signing support before purchasing. Do not use a self-signed certificate for public distribution.

## Application draft for SignPath Foundation

- Project: WinClip — keyboard-first clipboard history for Windows.
- Repository: https://github.com/slimabbadi/winclip
- License: MIT. The seven resolved NuGet runtime dependencies declare MIT or Apache-2.0 licenses: Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core, SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3, SQLitePCLRaw.provider.e_sqlite3, and Velopack. This metadata check does not replace license-notice obligations for the bundled runtime and upstream components.
- Published release: https://github.com/slimabbadi/winclip/releases/tag/v0.2.0
- Build system: GitHub-hosted Windows Actions runners, with build and test steps and versioned source tags.
- Requested artifacts: WinClip's own executable and managed assemblies, the Velopack installer, portable zip, and update package. Third-party binaries retain their upstream signatures; they must not be signed as WinClip's own code.
- Proposed author, reviewer, and signing approver: slimabbadi. Confirm that the single-maintainer role arrangement meets Foundation requirements.
- Privacy: see [the privacy policy](../PRIVACY.md). Clipboard content is encrypted locally using Windows DPAPI and is not uploaded by WinClip. Optional automatic update checks and manual update requests contact GitHub and its download hosts. Support links open the user's browser.
- Requested workflow: sign WinClip binaries before packaging, sign the installer, verify signatures and package consistency, regenerate checksums, then publish the release.

This is an application draft, not a claim that signing is provided. Add the Foundation's required attribution only after approval.

## Roles and approval

- Committer and reviewer: [Slim Abbadi](https://github.com/slimabbadi).
- Signing approver: [Slim Abbadi](https://github.com/slimabbadi).
- Once signing is configured, each release requires manual approval under the provider's signing policy. GitHub and SignPath accounts must use MFA.
- Only WinClip's own binaries may be signed as this project. Upstream components keep their own identities and signatures.

The project has no established reputation yet. The application states that explicitly and links to the public source, releases, and successful hosted lifecycle checks. Acceptance remains SignPath Foundation's decision. No claim that free signing is currently provided is made.

## Integration after approval

1. Complete the provider's enrollment, enable MFA, and approve the project and signing policy.
2. Configure narrowly scoped signing access. If an API token is required, provide it through the private secret-entry flow and store it as a GitHub Actions secret; never commit it or paste it into chat.
3. Sign WinClip.exe, WinClip.dll, and WinClip.Core.dll before running `vpk pack`. Sign the generated setup executable through Velopack's signing integration. Signing only the outer installer leaves the installed app unsigned.
4. Regenerate SHA-256 checksums after signing. Verify that the portable zip and update package contain identical signed WinClip binaries.
5. Run `scripts/verify-signatures.ps1 -PackageDirectory <packages-folder>` and the lifecycle workflow before publishing a new version. Never replace the already published 0.2.0 binaries in place.

The [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github) requires artifacts uploaded by GitHub Actions and, for Foundation projects, GitHub-hosted jobs. The integration and a successfully signed release remain pending provider approval.
