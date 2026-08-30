# Manual hardware test checklist

The automated suite (`tests/DriveForge.Tests`) covers pure logic, all 17 localizations and the code invariants.
It cannot cover what this app actually does: write to real disks and boot real machines.

Everything below needs hardware. Work through the sections for whatever you touched — the whole list only before a
release.

**Before you start**
- Use a spare disk. Every destructive item here really erases it.
- Run on mains power, not battery. On a Modern Standby laptop Windows force-sleeps on battery ~5 min after its own
  timeout no matter what the app requests.
- Keep a second machine or a phone handy to check `powercfg /requests` output and to look things up.

---

## 0. Smoke — after any change

- [ ] App starts, no crash dialog, disk list populates.
- [ ] Switch language to Romanian and back. No untranslated strings on the visible page, no key names showing raw.
- [ ] `Refresh disks` works; unplug a USB stick and confirm the list updates on its own within ~1 s.

## 1. Keep-awake (cannot be automated — needs a real idle timer)

- [ ] Start a long operation (surface scan or a multi-pass wipe on a spare disk).
- [ ] From an **elevated** prompt: `powercfg /requests`
  - [ ] `SYSTEM:` lists DriveForge with the reason string.
  - [ ] `EXECUTION:` lists DriveForge.
  - [ ] `DISPLAY:` says `None.` — this is the proof the screen is still allowed to sleep.
- [ ] Leave it running past the configured sleep timeout. Machine must NOT sleep. Screen may go dark.
- [ ] Let the operation finish, then `powercfg /requests` again → both sections back to `None.`
- [ ] Press Stop mid-operation → same, request released.
- [ ] Close the app mid-operation (answer Yes) → request released.

## 2. Progress reporting

- [ ] Bar reaches a true **100%** at the end of: wipe, raw ISO write, ISO verify, capacity test, shred.
- [ ] The stats line's total matches the real drive/file size (not ~12% larger).
- [ ] Press **Stop** on each of the above → bar, `NN%` label and the stats line all reset together. No widget
      disagreeing with another.
- [ ] Force a failure (e.g. unplug mid-operation) → the bar does not jump forward, and the error is honest.

## 3. Create Windows USB (from ISO)

- [ ] Engine checkboxes (DISM / Fast Clone) are **not** shown — they only apply to cloning.
- [ ] Completion dialog does **not** mention reinstalling a third-party antivirus (nothing was cloned).
- [ ] Verify ISO checksum: paste the hash from the download page bare, then again as `<hash>  filename`, then with a
      `sha256:` prefix, then paste the whole SHA256SUMS file. All four must report a match.
- [ ] Paste an MD5 → must say "not a SHA-256", NOT "does not match / do not use".
- [ ] **Boot the result on real hardware.** UEFI and, if you can, Legacy/CSM.

## 4. Clone this PC

- [ ] Exactly **one** format-confirmation prompt (not two).
- [ ] Fast Clone is the default engine.
- [ ] On a clean success: **no** report folder or .txt left on the Desktop.
- [ ] Force a failure → report IS written, and `Open Report` points at it.
- [ ] **Boot the clone on a different PC.** Check: apps present, permissions intact, network works.

## 5. Wipe / Shred / Format / Secure erase

- [ ] Wipe: no certificate is offered (feature removed).
- [ ] Wipe (Quick): finishes, bar at 100%, "Use Format to make it usable again".
- [ ] While the shred **file picker** is open, click Wipe → must be refused ("an operation is running"). This is the
      reentrancy guard; it was inert before and is now armed.
- [ ] Same test with the surface-test confirm open.
- [ ] Every destructive confirm: press **Enter** immediately → must CANCEL, never erase.
- [ ] FAT32 on a >32 GB drive → refused up front, steered to exFAT.
- [ ] SSD Secure Erase on a USB flash drive → honest "TRIM not supported" warning, not a green "blocks discarded".

## 6. Partition tools

- [ ] Move a partition on an MBR disk and on a GPT disk. Both must write a partition-table backup to the Desktop
      first, and warn on an overlapping move.
- [ ] Restore from that backup on a spare disk and confirm the disk still reads.
- [ ] Resize (grow and shrink) → reported result matches the actual new size.

## 7. Recover deleted files

- [ ] Quick scan and deep scan on a spare drive with known-deleted files.
- [ ] Recovering **to the same disk** you are scanning → warning appears, and it clearly says what OK does.
      (In every language — this text drifted once and 15 languages lost the OK/Cancel explanation.)
- [ ] Recover to a different drive → files open correctly afterwards.

## 8. Multi-boot USB

- [ ] Install Ventoy to a spare stick, drop two ISOs on it, boot and pick each from the menu.
- [ ] Unplug/replug the stick during the download phase → must not target the wrong disk.

## 9. Diagnostics

- [ ] Health / SMART on a healthy drive and, if you have one, a failing drive.
- [ ] Surface scan: press Stop halfway → verdict says it was stopped, NOT "the drive may be failing".
- [ ] Speed test → asks for consent first (it writes to free space).
- [ ] chkdsk scan and repair on a spare volume → the panel and the completion dialog agree with each other.

## 10. Localization spot-check

Automated tests cover key parity and placeholder arity. What they cannot judge is whether a translation *means* the
right thing.

- [ ] For each language you can read: open the destructive confirms and check the OK/Cancel wording actually
      describes what each button does.
- [ ] Right-to-left (Arabic): dialogs readable, nothing clipped.
- [ ] Long-word languages (German): sidebar titles wrap instead of clipping.

---

## After a release build

- [ ] Download the exe from the GitHub release (not your local build).
- [ ] `gh attestation verify DriveForge.exe --repo ForgeLabsSoft/driveforge`
- [ ] Compare the SHA-256 against `SHA256SUMS.txt`.
- [ ] Run it on a machine that has never seen DriveForge — confirm SmartScreen behaviour is what you expect for an
      unsigned build.
