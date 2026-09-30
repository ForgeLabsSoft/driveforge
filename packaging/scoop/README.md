# Scoop manifest

[Scoop](https://scoop.sh) is a command-line installer for Windows. Once the manifest below is
accepted into the **Extras** bucket, DriveForge installs with:

```
scoop bucket add extras
scoop install driveforge
```

`driveforge.json` is the manifest as submitted, kept here so what was sent is visible beside the
code it describes — the same arrangement as `packaging/winget/`.

## Why Extras, and not our own bucket

A personal bucket looks tempting and is nearly useless: `scoop search` falls back only to a small
set of well-known buckets, so an app in a private bucket is invisible from the command line. It
would only show up on scoop.sh and in the Scoop Directory. Extras is where a portable single-exe
Windows app belongs, and it is the bucket almost everyone already has added.

## What the manifest says, and why

- **`architecture.64bit` only.** DriveForge ships one x64 build; there is no 32-bit or ARM64 exe to
  point at.
- **`bin` with an alias.** The shim is `driveforge`, matching the `Commands:` entry in the winget
  manifest so the command is the same however you installed it.
- **No `persist`.** Scoop prefers portable configuration, but `persist` can only keep files that
  live inside the app directory, and DriveForge keeps its settings, drive-health history and logs
  in `%LocalAppData%\DriveForge`. The `notes` say so plainly, including that `scoop uninstall`
  leaves that folder behind.
- **`notes` mention elevation and the missing signature.** The app is `requireAdministrator`, so it
  prompts every launch, and SmartScreen warns because there is no certificate. Someone installing
  from a command line deserves to know both before the first UAC dialog, not after.
- **`checkver` + `autoupdate`.** Required for the bucket to stay current without a human. The hash
  comes from `$baseurl/SHA256SUMS.txt`, the checksum file published beside every release — so an
  update takes the hash from the binary GitHub actually published, never from a local build.

## Updating for a new release

Nothing by hand. Excavator (the bucket's bot) reads `checkver`, sees the new GitHub tag, rewrites
`version`, `url` and `hash` from `SHA256SUMS.txt`, and opens the update itself. Copy the result back
into this folder so this copy stays honest.

If you ever do need to do it manually, verify first:

```
scoop install .\driveforge.json     # installs straight from the file
scoop uninstall driveforge
```

## Submitting

One manifest per pull request. Title it exactly:

```
driveforge: Add version 4.4.0
```

After opening it, add a comment containing `/verify` — that runs the bucket's automatic checks
(hash, URL, format, autoupdate) and is expected of every submission.

Contributing rules live in
[ScoopInstaller/.github](https://github.com/ScoopInstaller/.github/blob/main/.github/CONTRIBUTING.md):
field order, four-space indent, an SPDX licence identifier, and a working autoupdate.
