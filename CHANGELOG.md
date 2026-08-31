# Changelog

All notable changes to DriveForge are documented here. Dates are ISO (YYYY-MM-DD).

## v4.3.1 — 2026-08-31

Re-cut on 2026-08-31. The build first tagged v4.3.1 on 2026-08-30 was replaced before anyone had
downloaded it, so this tag is the only v4.3.1 that ever reached anyone. It carries everything below plus
the original v4.3.1 changes further down this section.

### Fixed — found by a code audit of everything since v4.3.0
- **The progress bar along the bottom of the window is no longer cut off by the window edge.** The bottom bar
  had a fixed height that its own contents outgrew whenever the "pick a drive first" hint was showing, so the
  bar's lower rounded edge was drawn past the bottom of the window and the percentage sat on what looked like a
  truncated control. The bar now keeps a small margin below it, and the bottom strip grows instead of clipping
  when that hint appears or wraps onto a second line in a narrow window. Cosmetic only — nothing about how an
  operation runs has changed.
- **A surface test you stop part-way no longer leaves a countdown that never counts down.** The stopped scan
  keeps its honest partial percentage on screen, as before, but the "Remaining" estimate beside it is now blank
  instead of frozen at whatever it happened to say when you pressed Stop.
- **Resuming a deep scan starts the progress bar where the scan actually resumes.** It used to inherit the bar
  from whatever ran before it, so a resume after any completed operation showed a full bar and "100%" from its
  first second to its last, with no time estimate at all for the whole scan.
- **Six pieces of the window stayed in English after switching language.** The task heading at the top of the
  panel, the administrator badge, and the progress line under the status text kept whatever language was active
  when the app started; the two clone-engine checkboxes ("Use the Microsoft engine (DISM)" and "Fast Clone") had
  no translations at all and were English in all 17 languages. The descriptive paragraph in the Export VHDX
  panel had never been translated either. All are now translated and follow a language switch immediately.
- **Buttons no longer clip their own captions in longer languages.** Nineteen buttons had a fixed width chosen
  for the English text, so German "Laufwerk prüfen" rendered as "Laufwerk prü" and "Sicher entfernen" as
  "Sicher entferne". They now grow to fit their caption and keep the old width as a minimum, so the layout is
  unchanged in English.

### Also in v4.3.1 (from the 2026-08-30 build)

Safety fixes to confirmation dialogs, a translation fix on the recovery warning, and the project's first
automated test suite.

### Safety
- **Pressing Enter on a confirmation dialog no longer triggers the destructive action.** Nine dialogs had no
  explicit default button, so Windows focused the first one — meaning Enter or Space confirmed instead of
  cancelling. Affected: erase free space, capacity test, grow/create partition, set active partition, test boot
  (which takes the disk offline and boots a guest OS that writes to it), overwrite a downloaded ISO, CHKDSK
  repair, and closing the app during a running operation (which killed the running tool mid-write). All of them
  now default to Cancel/No.
- **Fixed a reentrancy hole in Shred and Surface test.** Both checked a "something is already starting" guard but
  never set it, so the guard did nothing. During the file picker and confirmation dialogs — 49 lines of the shred
  flow — a second destructive operation could be started on the same disk. Both now hold the guard for the whole
  flow.
- **The "you are recovering onto the same disk" warning was misleading in 15 languages.** This is an OK/Cancel
  dialog where OK writes onto the very disk being recovered. English and Romanian spelled out what each button
  does; the other 15 languages had been translated as a flat refusal, with no indication that OK proceeds anyway.
  All 15 rewritten to state the risk, the recommended alternative, that you may continue if it is your only disk,
  and what each button does.

### Fixed
- German text on that warning used informal address, out of step with every other German dialog, and called an
  SSD/USB drive a "hard disk".
- Arabic: the button legend on that warning displayed mirrored relative to the actual button positions.
- Turkish text said "the files you rescued" rather than "the files you are trying to rescue".
- Pause buttons lost their state when the language was switched: while an operation was paused, the button that
  resumes it reverted to reading "Pause". The Clean panel's button likewise lost its computed size.
- The engine checkboxes (Microsoft DISM / Fast Clone) no longer appear when installing Windows from an ISO —
  they only ever applied to cloning, and did nothing there.
- Installing from an ISO no longer shows the "you may need to reinstall your antivirus" note. That note is about
  restoring a clone of an existing PC; a fresh install from a Windows ISO has no antivirus on it.
- Removed two unused, half-translated internal strings.

### New — reporting a problem
- **Added a way to report problems**, since until now nothing in the app pointed anywhere: *Settings → Report a
  problem*, plus an offer after a failed operation and a pointer in the crash dialog. It opens either the bug form
  on GitHub or an email to `support@forgelabssoft.com`, with your app and Windows versions already filled in.
- **Nothing is submitted until you send it yourself** — there is still no telemetry and no automatic crash
  reporting. The GitHub link deliberately carries only the version and Windows build: error messages in this app
  quote full file paths, which on Windows contain your account name, and GitHub issues are public. The email
  option does include the error text for you to review first, with your profile path replaced by `%UserProfile%`.
- The offer after a failure is deliberately quiet: it never appears when *you* pressed Stop, never during a
  background disk rescan, never in unattended scheduled runs, and at most once per operation.

### Under the hood
- **Added an automated test suite** (79 tests, runs in about a second) covering the pure logic, all 17
  localizations (key parity, placeholder counts, duplicate keys, structural drift) and the project's own code
  invariants. It runs on every push and blocks a release if it fails. Four of the fixes above were found by it on
  its first run.
- Added a manual hardware-test checklist for the parts that cannot be automated.

## v4.3.0 — 2026-07-26

A large safety and reliability pass across every feature in the app, plus a new keep-awake
behaviour and the removal of the wipe "certificate" feature.

### Removed
- **The Wipe "Certificate of Data Erasure" has been removed.** DriveForge is not an accredited
  certification body, and generating a certificate implied a level of formal assurance the
  project isn't in a position to offer. Wipe itself is unchanged — it still securely overwrites
  the drive — it just no longer offers to produce a certificate afterwards.

### New
- **The PC no longer goes to sleep or hibernates while an operation is running** (wipe, clone,
  backup, scan, download, etc.) — the display can still turn off. This cannot override a sleep
  you initiate yourself (lid close, power button, Start > Sleep), and on a laptop running on
  battery with Modern Standby, Windows still forces sleep about 5 minutes after its own timeout;
  plug in for long jobs.
- Progress reporting is more accurate: several operations (Wipe, raw ISO write + verify,
  capacity test, Shred) could finish with the bar and percentage stuck around 89% and a
  stats line quoting a bogus total instead of showing 100% / the real size.

### Safety fixes (data-loss / false-success prevention)
- **File Analyzer**: duplicate/large-file deletion now goes through Windows' own delete
  confirmation (with the "this can't be undone" warning) instead of silently bypassing the
  Recycle Bin for files too large for it or on drives without one; a protected/master folder
  set *after* a scan is now honored; Undo now actually restores files (it previously failed to
  match filenames with extensions hidden); a file that was both a "largest file" and a
  duplicate-set's last copy can no longer be deleted from both grids at once.
- **Multi-Boot USB (Ventoy)**: re-verifies the target disk right before the wipe (a disk can
  renumber if a drive is unplugged/replugged during the multi-minute download), verifies the
  downloaded Ventoy tool is digitally signed before running it elevated, and no longer reports
  success or failure incorrectly around timing edge cases.
- **Disk erase tools**: Shredding a folder that contains a junction/symlink no longer follows it
  onto another drive; "SSD Secure Erase" no longer claims blocks were discarded on media that
  doesn't actually support TRIM (HDDs, most USB flash drives, some USB-bridged SSDs); FAT32
  formatting over 32 GB (which silently fails) is now blocked up front instead of reporting
  success on an unusable drive; Shred now also overwrites alternate data streams, not just the
  main file content.
- **Partition tools**: an interrupted overlapping partition move could previously corrupt data
  with no way back — there's now an explicit warning plus an automatic backup of the partition
  table before the move; Resize no longer reports success when the resize was actually declined.
- **Diagnostics (Health / SMART / Surface scan / Speed test)**: a failing drive could be reported
  as healthy in several cases (a stale cached report, a matching-substring bug that read
  "Unhealthy" as containing "healthy", a surface scan that stopped on the first bad read and
  called the rest of the drive fine); the Speed test — which writes to free space — now discloses
  that and asks first, since those are exactly the clusters a Recovery scan might need.
- **Clone, Restore, Export VHDX, Backup-to-image**: all four now correctly flag a run as
  incomplete instead of reporting full success when files are skipped or a step fails partway
  (previously only some of these were checked); restoring a backup now validates the saved image
  and checks capacity honestly before wiping the destination; backing up now writes to a
  temporary file and swaps it in only after the new backup is verified, so a failed backup can
  no longer destroy a good existing one.
- **Create Windows USB**: fixed BitLocker failures being silently swallowed and reported as
  success, 32-bit/ARM64 images not booting on UEFI, and a double-click launching two destructive
  operations on the same disk at once.
- **Download ISO / Verify ISO checksum**: fixed picking the wrong (older) release when several
  point releases exist, a truncated download being saved as if complete with no warning, and
  common checksum-paste formats (a filename attached, a `sha256:` prefix, a whole checksum-list
  file) being flagged as "does not match" on a perfectly good image.

### Other fixes and improvements
- Fixed the nav-sidebar "Clone to USB / external drive" label getting cut off mid-word in every
  language.
- Fast Clone is now the default cloning engine (was DISM); its warning text no longer mentions
  antivirus, and cloning now shows a single format-confirmation prompt instead of two.
- The Desktop clone report is now only created when something needs a second look — a clean
  successful clone no longer leaves a folder + text file behind.
- The progress bar now resets properly after pressing Stop on any operation (previously stuck at
  its last position on most of them).
- Selecting Wipe no longer makes the Drive-tools overview card spuriously repaint as if you'd
  clicked Health.
- TestBoot (boot a physical disk in a VM) now protects against targeting the wrong disk, no
  longer strands a disk offline silently on a script error, and recovers automatically if the
  app is closed or crashes mid-session.
- Numerous smaller correctness fixes in the raw NTFS clone/recovery engine shared by Clone,
  Export, Restore and Recover (bounds-checking on malformed filesystem records, alternate data
  streams on hidden/system files and directories, 4Kn sector alignment).

## v4.2.0 — 2026-07-13

Adds faithful backup-image restore and completes the multi-language coverage.

### Backup & restore
- **Restore from a VHDX backup** (made by *Export VHDX*) now writes the image back to a drive *faithfully* — using the same raw engine that makes *Clone This PC* an exact copy (preserving apps, permissions, hardlinks, reparse points and alternate data streams). Verified end to end: the restored drive boots on real hardware.
- The backup image is now attached through the **native Windows Virtual Disk API** instead of scripting an external mount — it is read-only, cleans up any leftover mount automatically, and can never leave the image file locked.
- Restore reliability fixes: correct handling when Windows auto-assigns a drive letter to the mounted image, the right Windows volume is chosen inside multi-partition images, and the target is only formatted **after** the image is confirmed readable.

### Language
- **Full 17-language coverage**: every remaining stage/status message and completion dialog on the backup / clone / restore screens is now translated, so a chosen language shows no leftover English.

### Under the hood
- Renamed the project internals throughout (no functional change).
- Numerous smaller reliability fixes across the imaging paths.

*This release also carries the safety + reliability work listed under v4.1.2 below, which had not yet been published as a download.*

## v4.1.2 — 2026-07-04

A large safety + reliability release. DriveForge writes whole disks, so this focuses on
never doing the wrong thing, and on being clear about what is happening.

### Safety (data-loss prevention)
- Destructive confirmation dialogs now default to the **safe** button (Cancel/No), so pressing Enter never erases a drive by accident.
- Every destructive write now **re-verifies the target disk's identity** (size + serial) immediately before writing — Windows can renumber disks when a drive is unplugged/replugged between the scan and the click. Covers wipe, format, secure-erase, raw ISO write, and the partition tools (Initialize, Convert, Move, Delete, Quick-partition).
- **Refuses to write when the source file is stored on the target disk** (raw ISO write and image restore) — this used to be able to destroy the very file being written.
- **Restore now verifies the backup image's integrity *before* formatting the target** — finding a corrupt backup after erasing the destination (a dead-PC recovery) was the worst possible ordering.
- Pressing **Stop during secure file-shred** no longer deletes the partially-overwritten file (that would lose it *and* leave its data recoverable).
- Scheduled/unattended clone now matches the target drive by **serial number** (not just name+size) and never targets the system disk.

### Cloning a PC
- **Detects your active real-time antivirus** (any vendor, via Windows Security Center) and warns before cloning that it can drastically slow the process — with a clear choice to pause/exclude it. DriveForge never touches your antivirus itself.
- Skips antivirus data/quarantine folders and developer caches (NuGet/npm/pip/cargo) during capture — these are self-protected or huge and slow the imaging engine.
- Clearer live status: distinguishes **"engine busy (high CPU)"** from **"drive stalled"**, and no longer looks frozen during the normal scan phase.
- Disables legacy 8.3 short-name generation on the target for a faster many-file apply.
- Antivirus temporarily disabled on the clone for first-boot app repair is now **re-enabled automatically and reliably**, and is left untouched on the Windows-To-Go path (which has no auto-restore).

### Reliability & correctness
- Added a global crash handler that saves a crash log to `%LocalAppData%\DriveForge\crash.log` and shows a clear message instead of vanishing.
- Progress/speed/ETA parsing is now culture-invariant (was breaking on comma-decimal locales).
- Drive health no longer shows a **Warning** drive as healthy just because its status text also contains "OK".
- Recovery deep-scan bounds its memory like the other scanners; disk read-back verification no longer reports a genuine drive as **FAKE** on a short read.
- Partition-tool success is now reported correctly on non-English Windows (no longer keyed off the English word "successfully").
- Numerous smaller fixes: settings-file robustness, first-run language matching your OS, safe stop/pause flags, resource disposal.

### Build & trust
- Self-contained single-file build is now compressed (smaller download).
- Releases now carry **GitHub build-provenance attestation** — verify with `gh attestation verify DriveForge.exe --repo ForgeLabsSoft/driveforge`.

## v4.1.1 — 2026-06-28

- First open-source (GPL-3.0) release built from public source via GitHub Actions, with SHA-256 checksums.
