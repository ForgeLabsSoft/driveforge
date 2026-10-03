# Scoop manifest

[Scoop](https://scoop.sh) is a command-line installer for Windows. `driveforge.json` is the manifest
for DriveForge, kept beside the code it describes - the same arrangement as `packaging/winget/`.

## Where this stands

The Extras bucket declined it. [#18897](https://github.com/ScoopInstaller/Extras/pull/18897) was
opened on 2026-10-02 and closed on 2026-10-03 by `aliesbelik`, a maintainer, who labelled it
`not-meet-criteria`. The event log is the whole story:

| when (UTC) | who | what |
| --- | --- | --- |
| 10-02 23:32 | `coderabbitai[bot]` | label `package-request-needed` |
| 10-02 23:38 | `github-actions[bot]` | validation passed 7/7, label `review-needed` |
| 10-03 13:15 | `aliesbelik` | label `not-meet-criteria` |
| 10-03 13:16 | `aliesbelik` | closed |

The manifest was never the problem. The bucket's own CI passed Lint, Description, License, Hashes,
Checkver, Autoupdate and Autoupdate Hash Extraction. What failed is the first criterion in the
package-request template:

> *Reasonably well-known and widely used (e.g. if it's a GitHub project, it should have at least
> **100 stars and/or 50 forks**)*

DriveForge has 2 stars and 0 forks. That is a recognition threshold, not a quality one, and no amount
of work on the manifest moves it. The closing comment offered the alternatives: contribute to a
community bucket, create our own, or file a package request first and return once the threshold is
met.

## Our own bucket, and why it is less of a consolation prize than it sounds

The obvious objection to a personal bucket is that nobody can find it. That is half true, and the
half that is true is worth stating exactly.

**The command line will not find it.** Verified in
[`libexec/scoop-search.ps1`](https://github.com/ScoopInstaller/Scoop/blob/master/libexec/scoop-search.ps1):
`scoop search` scans the buckets already added locally, and only when that returns nothing does it
fall back to `search_remotes`, which iterates the ten buckets listed in
[`buckets.json`](https://github.com/ScoopInstaller/Scoop/blob/master/buckets.json). A third-party
bucket is not in that list, so `scoop search driveforge` finds nothing until someone adds the bucket.

**The website will find it.** `scoopinstaller.github.io`, described by its own repository as the
"ScoopInstaller homepage and search engine", answers with a 301 to `https://scoop.sh` - they are one
site, so this is the official app search and not a third-party mirror. Its
[indexer](https://github.com/ScoopInstaller/scoopinstaller.github.io-indexer) discovers buckets by
GitHub search for `topic:scoop-bucket`, re-runs every two hours, and carries a repository's star
count as a value on the bucket rather than as a gate - there is no popularity filter in the discovery
path. The official bucket template says it outright among its setup steps:

> *If you'd like your bucket to be indexed on `https://scoop.sh`, add the topic `scoop-bucket` to
> your repository.*

So the app search people actually browse indexes us on a two-hour timer, with no human approval and
no star threshold. What Extras would have bought is the single line a user does not have to type
first. That is a real loss, and it is a smaller one than being absent.

## Setting it up

Start from [ScoopInstaller/BucketTemplate](https://github.com/ScoopInstaller/BucketTemplate) with
"Use this template" rather than assembling the plumbing by hand. It ships the same Excavator action
the official buckets run, on a four-hour cron, which reads `checkver`, notices a new release and
commits the new `version`, `url` and `hash` by itself - so a personal bucket stays current without a
human, exactly as Extras would have. It also brings the CI that validates manifests on every push.
The manifest in this folder needs no changes to work there.

The template's own README lists the steps. The ones that matter here: allow Actions, give workflows
write permission to contents, copy `driveforge.json` into `bucket/`, and **add the `scoop-bucket`
topic** - that last one is what puts it on scoop.sh.

Installation then reads:

```
scoop bucket add forgelabssoft https://github.com/ForgeLabsSoft/scoop-bucket
scoop install forgelabssoft/driveforge
```

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
- **`checkver` + `autoupdate`.** What lets the bucket stay current without a human. The hash comes
  from `$baseurl/SHA256SUMS.txt`, the checksum file published beside every release - so an update
  takes the hash from the binary GitHub actually published, never from a local build.

## Updating for a new release

Nothing by hand, in either home: Excavator reads `checkver`, sees the new tag and rewrites `version`,
`url` and `hash` from `SHA256SUMS.txt`. In a personal bucket it commits directly; in an official one
it opens a pull request. Either way, copy the result back into this folder so this copy stays honest.

If you ever do need to do it manually, verify first:

```
scoop install .\driveforge.json     # installs straight from the file
scoop uninstall driveforge
```

## If Extras is worth retrying

Not with another pull request. The order asked for is: reach 100 stars or 50 forks, open a package
request issue, wait for it to be accepted, and only then submit the manifest. Until then the
criterion is unmet and a second PR would close the same way - which is a fair outcome for a threshold
that exists to keep a shared bucket's review burden finite. 52 other open pull requests carried the
same label when ours was closed.

The submission rules, for that day: one manifest per pull request, titled exactly
`driveforge: Add version <x.y.z>`, with a `/verify` comment afterwards to trigger the automatic
checks. Field order, four-space indent, an SPDX licence identifier and a working autoupdate are
required by
[ScoopInstaller/.github](https://github.com/ScoopInstaller/.github/blob/main/.github/CONTRIBUTING.md).
