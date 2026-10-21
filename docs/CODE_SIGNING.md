# Code signing

Playnest releases are built by GitHub Actions (`.github/workflows/release.yml`) and are meant to be signed through
[SignPath Foundation](https://signpath.org), which provides free code signing certificates for open-source projects.
Until SignPath is set up, the same workflow still produces releases, just unsigned, with a SHA-256 checksum.

## Code signing policy

> This section is the policy SignPath Foundation asks projects to publish. Keep it up to date.

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- **Committers and reviewers:** [Random5386](https://github.com/Random5386)
- **Approvers:** [Random5386](https://github.com/Random5386)

Only `Playnest.exe` built by the release workflow from a tag in this repository is signed. Every signing request is
approved by hand in SignPath. Nothing built on a personal computer is ever signed.

**Privacy:** Playnest doesn't transfer any information to other networked systems unless you ask it to, with these
exceptions, all of which can be turned off in Settings: game names are sent to Steam / Epic (and SteamGridDB, if you add
a key) to find artwork, and once a day it asks GitHub for the latest release number. There's no telemetry.
See "Security & privacy" in the [README](../README.md).

## One-time setup

1. **Open-source licence:** done. Playnest is under the [MIT License](../LICENSE), which is OSI-approved as SignPath
   Foundation requires. Keep every part of the project open source (no proprietary components).
2. **Make sure the repository is public** and has at least one published release (SignPath requires the project to be
   released already in the form that will be signed; an unsigned release made by the workflow below counts).
3. **Apply** at <https://signpath.org/apply>. Give the repository URL, say releases are built by GitHub Actions, and link
   to this file as the code signing policy.
4. **After you're accepted**, in SignPath:
   - Connect the GitHub repository as a *trusted build system* (SignPath's onboarding walks you through it).
   - Create the artifact configuration: *Project → Artifact configurations → New*, paste
     [`.signpath/artifact-configuration.xml`](../.signpath/artifact-configuration.xml).
   - Create an API token for a CI user that is allowed to submit requests for the signing policy.
5. **In GitHub**, *Settings → Secrets and variables → Actions*:
   - Secret `SIGNPATH_API_TOKEN`: the API token.
   - Variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`
     (usually `release-signing`) and `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`: copy them from SignPath.

## Making a release

1. Raise `<Version>` in `src/Playnest/Playnest.csproj` (e.g. `1.2.0`) and commit.
2. Tag and push: `git tag v1.2.0` then `git push origin v1.2.0`.
3. The release workflow tests, builds the native exe and sends it to SignPath. Approve the request in SignPath
   (you get an email; the workflow waits up to an hour).
4. A **draft** release appears on GitHub with the signed `Playnest.exe` and `SHA256SUMS.txt`. Write what changed and publish it.

The workflow refuses to run if the tag doesn't match `<Version>`, and it checks the signature on the exe SignPath sends back.

Windows SmartScreen may still warn for a while after the first signed releases; that goes away as the certificate
builds up reputation with downloads.
