# winget manifests

DriveForge is published in the Windows Package Manager:

```
winget install ForgeLabsSoft.DriveForge
```

It has been there since July 2026 (first accepted as 4.1.1, then 4.3.0 through 4.3.3). Each new release is a
pull request to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) adding one folder under
`manifests/f/ForgeLabsSoft/DriveForge/<version>/`. The copy submitted for the current version is kept here, in
this repository, so what was sent is always visible beside the code it describes.

```
manifests/ForgeLabsSoft.DriveForge.yaml                 version manifest
manifests/ForgeLabsSoft.DriveForge.installer.yaml       url, SHA-256, architecture
manifests/ForgeLabsSoft.DriveForge.locale.en-US.yaml    name, licence, description, release notes
```

DriveForge is one self-contained executable with no installer, so the package type is **portable**: winget
downloads the exe, checks the SHA-256, and puts a `driveforge` shim on PATH. Nothing is installed, nothing is
written to Program Files, and uninstalling removes both.

The README you are reading lives one level above the manifests on purpose. `winget validate` parses **every**
file in the folder it is given, so a markdown file sitting beside the YAML fails the whole check with a YAML
scanner error pointing at a line of prose.

## Updating for a new release

Three things change, and nothing else:

1. `PackageVersion` in all three files.
2. `InstallerUrl`, `InstallerSha256` and `ReleaseDate` in the installer manifest.
3. `ReleaseNotes` and `ReleaseNotesUrl` in the locale manifest.

**Take the hash from the release's published `SHA256SUMS.txt`, never from a local build.** The released binary
is built by GitHub Actions from the tagged source, so a locally built exe has a different hash and winget would
refuse to install it. The same published binary carries a build-provenance attestation:

```
gh attestation verify DriveForge.exe --repo ForgeLabsSoft/driveforge
```

Check the manifests before sending them:

```
winget validate --manifest packaging\winget\manifests
```

## Submitting the update

The easy way, which fills in the hash and opens the pull request:

```
winget install Microsoft.WingetCreate
wingetcreate update ForgeLabsSoft.DriveForge --version 4.4.0 --urls https://github.com/ForgeLabsSoft/driveforge/releases/download/v4.4.0/DriveForge.exe --submit
```

The first `--submit` asks GitHub for permission: it prints a device code, opens github.com/login/device, and
stores the token afterwards, so later releases need no login. `wingetcreate` writes its own manifests from the
published ones rather than reading this folder, so after submitting, copy what it produced back here — that is
what keeps this copy honest.

By hand: fork `microsoft/winget-pkgs`, copy the three files from `manifests/` into
`manifests/f/ForgeLabsSoft/DriveForge/4.4.0/`, and open a pull request. A bot validates the manifest and
installs the package on a clean virtual machine before a human reviews it.

## Why this matters beyond convenience

DriveForge is not code-signed: the SignPath Foundation's free programme turned the application down in July
2026, because the project had not reached the level of public recognition it asks for. That question is decided
on evidence of use, so it is worth being exact about what can actually be counted.

winget does **not** publish per-package install counts. Microsoft collects telemetry, but its maintainers have
said it has never been vetted for publication, and the acquisition figures a publisher can see in Partner Center
are Microsoft Store figures rather than CLI ones. Third-party sites that show a number next to a winget package
are counting page views of their own listing, not installations.

What can be counted is the GitHub release download count — and because DriveForge is a *portable* package,
winget fetches the exe straight from the release URL, so every winget install is one of those downloads:

```
gh api repos/ForgeLabsSoft/driveforge/releases --jq '.[] | "\(.tag_name) \(.assets[0].download_count)"'
```

It undercounts nothing and overcounts a little: CI, mirrors and our own verification runs land in the same
total. Keeping the package current is still part of the case for being able to sign, not only a nicer way to
install.
