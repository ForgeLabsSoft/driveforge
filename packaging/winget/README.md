# winget manifests

These three files are the Windows Package Manager manifest for DriveForge. They are kept here, in this
repository, so the version that was submitted is always visible next to the code it describes.

```
ForgeLabsSoft.DriveForge.yaml                 version manifest
ForgeLabsSoft.DriveForge.installer.yaml       url, SHA-256, elevation
ForgeLabsSoft.DriveForge.locale.en-US.yaml    name, licence, description, tags
```

DriveForge is one self-contained `.exe` with no installer, so the manifest type is **portable**: winget
downloads the exe, checks the SHA-256, and puts a `driveforge` shim on PATH. Nothing is installed, nothing is
written to Program Files, and uninstalling removes the shim and the file.

Once accepted, installing is:

```
winget install ForgeLabsSoft.DriveForge
```

## Submitting

The manifests live in Microsoft's repository, so publishing means opening a pull request there.

The easy way, which fills in the hash and opens the PR for you:

```
winget install Microsoft.WingetCreate
wingetcreate update ForgeLabsSoft.DriveForge --version 4.4.0 --urls https://github.com/ForgeLabsSoft/driveforge/releases/download/v4.4.0/DriveForge.exe --submit
```

By hand: fork `microsoft/winget-pkgs`, copy these three files to
`manifests/f/ForgeLabsSoft/DriveForge/4.4.0/`, and open a pull request. A bot validates the manifest and
installs the package on a clean VM before a human reviews it.

Before submitting either way, check the manifest locally:

```
winget validate --manifest packaging\winget
```

## Updating for a new release

Three values change, and nothing else:

1. `PackageVersion` in all three files.
2. `InstallerUrl` and `InstallerSha256` in the installer manifest. **Take the hash from the release's
   published `SHA256SUMS.txt`, not from a local build** — the released binary is built by GitHub Actions from
   the tagged source, so a locally built exe has a different hash and winget would refuse to install it.
3. `ReleaseDate`, `ReleaseNotes` and `ReleaseNotesUrl`.

## Why this matters beyond convenience

DriveForge is not code-signed: the SignPath Foundation's free programme turned the application down in July
2026 because the project had not reached the level of public recognition it asks for. winget publishes install
counts, which is the kind of evidence that question is decided on — so being in winget is a step towards being
able to sign, not only a nicer way to install.
