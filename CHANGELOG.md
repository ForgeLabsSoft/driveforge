# Changelog

All notable changes to DriveForge are documented here. Dates are ISO (YYYY-MM-DD).

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
