# Changelog

All notable changes to DriveForge are documented here. Dates are ISO (YYYY-MM-DD).

## Unreleased

### Fixed
- **The same fault was in three more places, and one of them was the tool for recovering from it.** 4.4.3
  said the problem was fixed everywhere. It was not. Quick partition, Convert MBR/GPT and "Initialize a
  blank / RAW disk" all emptied the disk and then asked diskpart to convert it — the same sequence that
  fails on Windows 11 26H2 and leaves a drive wiped and uninitialised.

  The third one is the worst of the three. It is where the error message, the release notes and the reply to
  the user who reported this all send people to put their drive back. On the build where the original bug
  bites, that tool would have failed in exactly the same way.

  All three now obtain the partition table with the call meant for an uninitialised disk and check that they
  got it, as the SSD erase already did. The message names the style that was refused, so it reads correctly
  for MBR as well as GPT, in all seventeen languages.

- **Why the test did not catch them.** The rule added in 4.4.3 searched for the literal text `convert gpt`.
  These three wrote `convert {style}` — a variable filled in at run time, which is `convert gpt` every time
  the user picks GPT. They passed the rule while carrying the exact defect it exists to stop. It now matches
  the interpolated form as well and rejects it outright: nothing in the source can prove the variable is
  never "gpt".

  A second rule covers the shape underneath. The helper that obtains a partition style returns whether it
  succeeded, and every caller must look at that — a discarded result is the same silent-wrong-answer shape
  as diskpart's `noerr`. Both rules were confirmed by breaking the code five different ways; none survived.
## v4.4.3 — 2026-10-10

### Fixed
- **SSD erase could wipe a drive and then stop, leaving it unusable.** Reported by a user on Windows 11 26H2
  (build 26300): the drive was emptied, then *"The disk you specified is not MBR formatted"* and nothing else — no
  partition, no volume, no explanation beyond a raw error code.

  DriveForge asked diskpart to `convert gpt` straight after `clean`. But `convert` turns an **empty MBR** disk into
  GPT, and a disk that has just been cleaned has no partition table at all, so there is nothing to convert. Most
  Windows builds wave that through; 26H2 does not, and diskpart abandons the rest of the script at the first error
  — which is why the drive was left wiped and uninitialised.

  It no longer converts. The partition table is now created with the call meant for an uninitialised disk, and
  then checked; if Windows will not give it one, DriveForge says so in your own language and tells you the drive is
  blank rather than broken, instead of failing with a hex code. Because nothing is converted any more, this does
  not depend on how forgiving a particular Windows build happens to be.

- **The same fault, found in two more places while fixing the first.** Creating a bootable VHDX hit it identically
  — measured, same error code — because a freshly made virtual disk is also uninitialised. And the Windows-To-Go
  layout would have aborted for the same reason, though there the fallback happens to be the layout it wanted.

  Worth recording why the obvious one-word fix was rejected: diskpart's `noerr` would have let the failure pass,
  and the next command then initialises the disk as **MBR**. A 4 TB SSD would have come back formatted to 2 TB and
  reported as a success. That is worse than the bug, so the partition style is now obtained and verified wherever
  GPT is actually required.

  A dead code path carrying the same pattern was removed rather than fixed — nothing called it, and leaving a
  broken example in the tree invites someone to copy it.

  Two tests now hold the rule: no cleaned disk may be handed to `convert`, and the SSD erase must verify it got
  the table it asked for. Both were confirmed by breaking the code on purpose.

## v4.4.2 — 2026-10-06

### Fixed
- **Sentences that point you at another task now use that task's name in your own language.** A few descriptions
  and confirmations say things like *"...you can restore it later with 'Restore image'"*. The name was written into
  the sentence, so the sentence was translated and the name was not: in Romanian the text sent you to *Restore
  image* while the button beside it read *Restaurează imagine*, and in Japanese to a label that simply was not on
  screen. Five strings, all sixteen languages.

  They now carry a reference to the task rather than its name, filled in from the same string the sidebar draws its
  label from. That is the point of doing it this way instead of translating the names by hand: the two cannot
  disagree again, including after a rename, in any language.

  Three tests hold it — every reference resolves to a real key, the resolved text carries that language's own label
  with no reference left showing, and no translation may name a task in English when that language renames it. The
  last one is the rule that found this, and it now passes over the whole table, so there are no others.

## v4.4.1 — 2026-10-04

### Added
- **Image a whole drive, not just one partition of it — which makes DriveForge a duplicator.** *Recover → ⋯
  → Create image of the whole drive…* reads a physical drive end to end, first sector to last, into a `.img`.
  The image that comes out goes straight back in through *Create bootable USB from a disk image* with *Write
  this image to several drives* ticked, so one stick becomes a batch of identical copies without any duplicator
  hardware.

  The existing *Create disk image* was never going to do this, and the difference is not obvious from the
  outside: it images a **volume**, which is right for recovery — you freeze one partition and carve from the
  copy instead of the drive — but a volume image holds that partition's bytes and nothing else. Write it to a
  stick and you get the files back and a drive that will not boot, because the partition table and the boot
  sector were never in the image. Reading the physical drive takes those too.

  It reuses the machinery that was already there rather than adding a second copy of it: the same reader, which
  already aligns every access to 4096 bytes as a raw device demands; the same response to a bad block, which
  retries it sector by sector, zero-fills only the sectors that genuinely fail, and reports how many — so an
  image that is partly fiction says so, on a stopped run as well as a finished one.

  Two checks are stricter than the volume version's, because the mistakes are worse. The size comes from the
  **device**, not from the volume: a stick with one 8 GB partition on a 16 GB device would otherwise be imaged
  half-way and every copy truncated mid-filesystem. And the refusal to write the image onto the drive being
  imaged compares **physical disks**, not drive letters — the letter check cannot see that `D:` and `E:` are
  two partitions of the same stick, and that mistake grows the file into its own source.

### Changed
- **The app opens on *Create Windows USB* instead of a clone task.** It is the first task in the sidebar, the one
  most people arrive for, and the only one that touches nothing until a file is chosen. The window used to open on
  *Clone to USB / external drive*, which put "the selected disk will be formatted" under the cursor before anyone
  had asked for it.

  The sidebar order did not change - *Create Windows USB* was already first in the list, only the selection landed
  elsewhere. Reordering the tasks would have moved the clone entry to the top, which is the opposite of the point,
  and would have disturbed the task indices a good deal of logic keys off.

  Startup sets the loaded task and the sidebar highlight as two separate statements, so they can disagree there and
  nowhere else - every later switch derives the highlight from the task through one mapping. A test now reads that
  mapping and requires the two startup statements to agree, so changing which task the app opens on stays a one-line
  change that cannot silently leave the wrong entry lit.

  *Remember the last task*, in Settings, still overrides this for anyone who turns it on.

- **Two options now say what they do, instead of what one would like them to do.** Both were measured on real
  media before being rewritten: a Windows 11 26H2 install USB was built from Microsoft's own ISO (build 26300,
  SHA-256 checked against Microsoft's published table) and the resulting image's registry hives were read back.
  Every value both options claim to write was there. The problem was never that they do nothing; it is that what
  they do is narrower than what they said.

  *Remove bloatware (Copilot, Teams, ads, telemetry)* is now **Apply Microsoft's privacy policies (ads,
  suggestions, telemetry)**. It writes eight group-policy values and removes no packages, so it was never going
  to remove Copilot or Teams; and Microsoft documents several of those values as honoured only on Enterprise and
  Education. The image written in that test came out as `EditionID=Core` - Home - which is the edition most of
  these USBs become, and on it the advertising-ID and Start-web-search values apply while diagnostic data falls
  back to *Required* rather than off. The Copilot value is a policy Microsoft has deprecated and does not govern
  the packaged Copilot app. The new tooltip says all of that plainly rather than burying it.

  *Bypass Windows 11 system requirements* keeps its name, because the name is accurate, but its tooltip no longer
  claims it is what lets the drive run on an old PC. It said "Only matters for a fresh ISO install", which is
  wrong twice over: this flow runs no Windows Setup at all (it applies `install.wim` directly with DISM), and an
  already-installed Windows does not re-check TPM, Secure Boot or the CPU at boot - so the drive starts on
  unsupported hardware whether the box is ticked or not. What the written `LabConfig` values are genuinely good
  for is a later in-place upgrade of that Windows, which Setup *does* appraise. The tooltip now says that.

  The progress line and the completion message moved with them: "Removing bloatware (Copilot, Teams, ads,
  telemetry)" and "Bloatware & telemetry removal applied" became "Applying privacy policies" and "Privacy policy
  settings written (several apply only to Enterprise and Education editions)". Five strings, seventeen languages.
  Nothing about the behaviour changed - the same values are written as before.

- **The language list is in an order again.** It had grown by appending, so after seventeen languages it was in
  no order at all: English, Romana, Deutsch, Francais, Espanol, Italiano, Portugues, Nederlands, Russkij,
  Polski, Turkce, Ukrayinska, then the non-Latin names, with Indonesia and العربية last because they were
  added last.
  English stays pinned at the top, where someone who cannot read the current language can find it; everything
  else is now alphabetical by the name shown in the list, so people look for their language under the name they
  know it by rather than under its English name or its code.

  The order is computed rather than typed out, which is the point: the old list was hand-ordered, and every
  language added over the years went to the end because that is the path of least resistance. An eighteenth
  language will now land in its place on its own.

  Sorting is ordinal and deliberately not culture-aware. A culture-aware sort reads the machine's own locale, so
  the same build would show the list in a different order on a Turkish or a Swedish PC; the list is the same for
  everyone instead. The cost is that the names not written in the Latin alphabet - العربية, हिन्दी, 中文, 日本語 -
  collate after the ones that are, which is at least a predictable place to keep them.

  Nobody's language changes on update, and this was checked rather than assumed: the saved setting stores the
  language *code* and the selection is recomputed from it, so the order is free to move. Three tests now hold
  this - the rendered order, that every language is offered exactly once, and that none is offered without
  translations behind it - and each was confirmed to fail when the implementation is broken on purpose.

### Fixed
- **Two result lines stayed in the language they were written in.** After an analysis, *Clean* says how much can
  be freed and *Recover* says how many deleted files it found. Both are written once, when the work finishes, and
  then simply sit there - so changing language left "About 2.0 GB can be freed." and "20 deleted files" in English
  under an otherwise translated window, next to a *Clean* button that did follow along, because that one had been
  repaired years earlier.

  Neither is remembered as a sentence now; both are recomputed from what is on screen, so there is nothing left to
  go stale. They are only refreshed while idle: during a run those same labels carry progress, and replacing that
  with a finished-looking total would be a worse lie than the wrong language.

- **Choosing a base colour while in Light mode made the window unreadable.** The base-theme presets repaint the
  window and its panels; the colour of the *text* belongs to Light or Dark. So picking one in Light mode put near-
  black panels under Light mode's near-black text, and *Reset to default* set the panels to exactly the colour the
  text was already using. The program was still running and could not be read, with no way back but restarting.

  Clicking a dark colour is a request for the dark look, so it now switches to it rather than refusing or doing
  something halfway; *Reset to default* restores the whole default appearance, mode included. Start-up had always
  refused to restore a dark base in Light mode for this reason — the buttons simply never got the same treatment. A
  test now holds both of them to it.

- **The About box had been showing the wrong version since 4.4.0 shipped.** It read "DriveForge 4.3.3" because the
  number was typed into the window by hand. It now comes from the build itself, so it cannot disagree again — and
  a bug report quoting it will no longer point at the wrong source. A test rejects a version number written into
  the window.

- **The drive-health trend line stayed in the previous language.** After a language change the whole Drive tools
  card is redrawn — but the redraw deliberately does not record a new health check, and the sentence under the
  drive's name ("checked 4× since …, status stable") was only ever written while recording one. It now carries the
  identity of the drive it describes and is redrawn with it, so it follows the language without inventing a check.

- **Changing language blanked the Drive tools card.** Open *Drive tools* and the selected drive's health reads
  "Good". Switch language, come back, and it reads "Unknown" — for the same drive, with nothing else changed. It
  stayed that way, too: the card only redraws when a *different* disk is picked, so the drive's health, name,
  serial, firmware, interface, size and recommendation all stayed blank until you selected another drive or ran a
  report.

  The cause is a trap this codebase has now fallen into three times. Translation works by walking every string key,
  looking for a control with that name, and writing the text into it. That is right for a fixed label and wrong for
  anything showing a live reading — and seven controls on that card are both a control name and a string key, so
  each was overwritten with its "Unknown" placeholder. The Pause buttons and the *Clean now* button had already
  been fixed for exactly this; the card had not.

  Changing language now redraws the card, which also brings back the cached SMART report and speed result in the
  new language.

  A test requires that redraw to happen after the translation pass, and to clear the "same disk as last time" guard
  first — without which the redraw is skipped and the fix silently does nothing. All three ways of breaking it were
  tried, including that one.

- **Light mode, your accent colour and your base colour survived exactly one restart.** Pick them, and the window
  looks right for the rest of the session — but the settings file on disk had already been overwritten with the
  stock dark blue. The next launch came up with the defaults and no sign of why.

  Start-up raises a flag meaning "the user is in control now", which exists to stop the language box's
  selection-changed handler from running while saved settings are still being restored — because that handler saves
  settings. The flag was being raised one line too early, immediately before the language was selected. So the
  handler ran, saved, and read the theme and accent while they were still at their built-in values; the saved ones
  were applied a few lines further down. The window therefore looked correct and the file did not, which is why
  this never looked like a bug in the session where it happened.

  The flag now goes up last, and the language is applied explicitly rather than as a side effect of a handler that
  start-up ought to be suppressing.

  Measured rather than reasoned: a non-default appearance (light theme, red accent, custom base) plus a Japanese
  interface were written to the settings file, the app was started once and nothing was touched. Before the fix all
  three appearance values came back as the defaults; after it, the file was unchanged and the window came up in
  Japanese — the latter being the regression the fix could have caused, since the language used to be applied by
  that same handler.

  A test now requires the flag to be raised by the last statement of start-up, so anything added later cannot slip
  in behind it.

- **Sixteen languages were still describing an older, smaller version of the recovery tool.** *Recover deleted
  files* grew from NTFS-only to NTFS, exFAT and FAT, learned to read disk images, to list existing files as well as
  deleted ones, and to preview pictures — and stopped being merely "fast", since a deep scan is not. The English
  text was updated. The other sixteen were not, so they still read "recover files from a drive (NTFS)" and promised
  a "fast, read-only scan".

  The effect was to understate the program to almost everyone who uses it: someone running the Romanian, German or
  Japanese interface with an exFAT stick was told by the app itself that it was not supported. Both strings — the
  sidebar subtitle and the panel description — are now rewritten in all sixteen.

  Nothing caught this, and that is the more interesting half. Every existing check passes on a stale translation:
  the key is present, non-empty, placeholder-compatible and in the right alphabet. So a new test looks for the one
  thing a translation cannot change — the identifiers that are never translated in any language (NTFS, exFAT,
  BitLocker, DISM, VHDX, TPM and so on). If the English names one and the translation does not, the translation
  almost certainly predates an edit to the English. It checks 864 values across 54 keys today.

  The list deliberately leaves out *Secure Boot*, *Windows To Go* and *Trusted Platform Module*: those really are
  translated (sicherer Start, démarrage sécurisé, セキュア ブート), and a rule that cries wolf gets ignored. The
  comparison is case-sensitive, which is what tells S.M.A.R.T. apart from the ordinary word in "Smart clean".

- **A healthy drive was reported as failing — but only in Chinese, Japanese and Hindi.** The same SSD, reporting
  the same status, showed a green "good for a portable Windows drive" in English and a red "this drive reports
  health problems — back up its data and consider replacing it" in those three. The drive was fine.

  The cause: health was decided by searching the drive's *translated* health label for the English word "OK".
  Fourteen languages leave "OK" inside the translation ("Stare: OK", "Zustand: OK"), so the search happened to
  work. Exactly three translate it outright — 健康：正常, 状態: 正常, स्थिति: ठीक — and in those the search found
  nothing and concluded the drive was bad. It was never a translation mistake; those three are the correct ones.

  Health is now read from the status Windows itself reports, which is English in every locale, and the translated
  label is used only for display. That also fixes everything else fed from the same source: the health card in
  *Drive tools*, the SMART table's Health row, the replacement advice, and the failure prediction.

  One of these reached disk. The health history that powers the "stable since / changed on" trend was storing the
  translated label, so checking a drive in English and again in Japanese compared "Health: OK" against "状態: 正常"
  and announced the drive had *degraded*, with a date. It now records the raw status. Existing history files stay
  readable and produce no false alarm.

  Worth stating plainly: nothing was ever blocked or put at risk. The gate that runs before a destructive
  operation reads the raw status and always did, so no clone was prevented and no data was in danger — the damage
  was bad advice, including advice to replace a healthy drive. Direction matters too: a genuinely failing drive
  was still caught in all seventeen languages, because the raw status is interpolated into that label.

  Found from a screenshot, held by tests: a sweep asserting the verdict is identical in all seventeen languages,
  and a rule that fails the build if any decision is fed the translated label again. Six deliberate breakages were
  tried against them, including restoring the original bug; all six were caught.

- **Closed a way for an ordinary program to get its code run as Administrator.** DriveForge caches its clone
  engine, wimlib, under `%LocalAppData%\DriveForge\Tools\wimlib`, and anything running as the signed-in user
  can write there without any elevation at all. DriveForge itself is `requireAdministrator`, so when a clone or
  a backup starts, it launches `wimlib-imagex.exe` out of that folder **elevated**. The integrity check that was
  supposed to cover this ran once: it hashed the downloaded archive the first time the engine was unpacked, and
  on every run after that the code simply took whatever executable it found in the folder and started it. A
  program with no privileges could drop its own binary there, wait for the owner to clone a disk, and be running
  as Administrator - and nothing would look wrong, because the check that passed had been about a file from
  weeks earlier.

  Hashing the executable on each run would not have been enough either. `wimlib-imagex.exe` imports
  `libwim-15.dll`, and Windows resolves that import from the executable's own directory before it looks anywhere
  else, so replacing the library alone buys exactly the same elevated execution while the executable still
  matches its hash perfectly. Both files are now pinned by SHA-256 and both are verified **every** time the
  engine is used.

  Two further changes make that verification mean something. The engine is located at a fixed path instead of by
  searching the folder tree for anything called `wimlib-imagex.exe` - that search returned the first match in
  enumeration order, which is an order an attacker picks by choosing where to drop the file, so the check could
  have been reading one executable while Windows started another. And each file is read through a handle opened
  with `FileShare.Read`, which denies writes and deletes to everyone else and is held for the rest of the
  session, closing the gap between "we hashed it" and "Windows executed it". That combination was measured, not
  assumed: holding both handles that way does not stop the executable running or the library loading.

  Anything that fails is not repaired in place. The folder is deleted whole and written again from the copy of
  wimlib embedded in DriveForge itself, because a directory someone has tampered with may hold more than the two
  files worth checking. Nothing about this is visible in normal use - an untouched installation verifies and
  proceeds exactly as before.

  Four tests cover it, each one confirmed to fail when the thing it guards is undone: the pinned hashes are
  checked against the archive actually embedded in the app, so updating wimlib without re-pinning fails the
  build rather than a user's clone; the library stays pinned beside the executable; the folder search cannot be
  reinstated; and the share mode cannot be widened back to one that lets a writer in.

- **BitLocker now says it cannot work on Windows Home *before* the clone starts, instead of at 96%.** Home
  carries no BitLocker management: `manage-bde` refuses with `0x8031005A`, *FVE_E_NO_FEATURE_LICENSE* — "this
  version of Windows does not support this feature of BitLocker Drive Encryption". Nothing in DriveForge had
  ever looked at the Windows edition, so the tick was offered to everyone; a Home owner chose a folder for the
  recovery key, waited out an entire clone, and was told at the end only that `manage-bde` had exited with a
  code. The warning now appears in the confirmation dialog, beside the one that already checks whether a
  security suite will block the offline registry — same lesson, same place, while the disk is still theirs.

  It **warns and never blocks**. The whole test is one registry string, `EditionID`, and a string lookup cannot
  know about an edition released after it was written, so unreadable, empty or unrecognised all mean *let it
  try*. Only the Home family is recognised as unable — `Core`, `CoreN`, `CoreSingleLanguage` and
  `CoreCountrySpecific`, which is all of it, and nothing outside that family begins with `Core`. Refusing to
  run because a lookup failed would be a worse failure than the late one this replaces.

  Translated into all seventeen languages, like every other message.

### Under the hood
- **Corrected a comment that was true for only one of the two ways the BitLocker step can fail.** It said a
  recovery-key file had already been written. That holds when `-on` fails, but the Home failure happens one call
  earlier, at `-protectors -add`, which throws before the file is created — the difference between an orphaned
  key file for an unencrypted disk and no file at all. Nothing a user sees, but the next person to read that
  catch block would have believed it.

- **The release build now refuses to run when the tag and `AssemblyInfo.cs` disagree about the version.** The
  version is written by hand in `Properties/AssemblyInfo.cs` - `GenerateAssemblyInfo` is `False`, so the SDK
  generates none of it - and nothing ever checked it against the tag being built. Tagging `v4.5.0` without
  editing that file would have published an executable whose properties still read `4.4.0.0`, which is the
  number Explorer shows, the one winget and Scoop report, and the one every scanner prints in its file-version
  block: wrong everywhere at once, and not correctable without cutting the release again.

  The check compares the tag against `AssemblyFileVersion`, `AssemblyVersion` and
  `AssemblyInformationalVersion`, and runs immediately after checkout - before the SDK is installed, before the
  tests - on the same reasoning that already put the test step ahead of the build: the job stops at the first
  red step, so a mistyped tag costs seconds instead of a published release. Three-part tags are padded to four
  before the numeric comparison, and a pre-release suffix is stripped for it but required of the informational
  version, so `v4.5.0-rc1` cannot ship as plain `4.5.0`. It only runs on a version tag; ordinary pushes and
  pull requests have no tag to contradict.


## v4.4.0 — 2026-09-30

### Added
- **Write one image to several drives, one after another.** Tick *Write this image to several drives* under
  the target list and pick the rest from the drives on screen. Each one is erased, written, checked and put
  back before the next begins, so at no point is more than one disk offline. The confirmation lists every
  drive by name, size and what is currently on it — not "8 drives selected" — with the erase warning pinned
  above the list where it cannot be scrolled out of sight, and it will not start until you tick a box that
  names the number of drives. Stop ends the whole run; Pause holds it between drives as well as inside one.
- **A result for each drive at the end, not one verdict for the batch.** The report gives every drive its own
  line — written, checked, read back different, skipped, never reached — because with eight sticks a single
  "done" is a statement about seven drives nobody looked at. The heading is a count, and a drive Windows
  could not bring back online is named, which the old code silently swallowed behind a green dialog.
- **Drives that report no serial number are told apart by the port they are plugged into.** Sticks out of one
  bulk pack share their model, their size and, very often, an empty serial — so DriveForge now reads the
  device path as well, and refuses a target it genuinely cannot identify rather than guessing. This also
  strengthens the identity check that stands in front of every destructive operation in the app, not just
  this one.

### Changed
- **The read-back check is now a box you tick before the write, not a question asked after it.** The old
  dialog appeared once the write was already over — by which time an unattended run had nobody at the
  keyboard, so the question sat there and the verification never ran. It is on by default; untick it to skip.
  This also removes the last English-only dialog on that task.
- **"Eject the drive when finished" is now visible when writing an image.** It was being read on that task
  while living in a section the task hides, so the drive ejected, or did not, according to whatever was left
  ticked during some other job.

### Fixed
- **A disk left offline by a crash during an image write is brought back at the next launch.** If the app was
  killed, or the power went, between taking the disk offline and putting it back, Windows simply kept hiding
  the drive with nothing on screen to explain it. The drive is now recorded by device path — not by number,
  which points somewhere else entirely after an interrupted run — and re-onlined on the next start.

### Fixed — second pass, from running every task against real hardware

- **A security suite that blocks offline registry access no longer wastes the drive first and explains nothing.**
  Everything that personalises an image after it is written — the portable-Windows flags, the Windows 11
  requirement bypass, the local-account bypass, bloatware removal — needs Windows to mount a registry hive out
  of the target, and `bcdboot` needs it to create the boot record. Some security products refuse that
  machine-wide, and `reg` reports the refusal as *"The filename or extension is too long"*, which is true of
  nothing involved. DriveForge now asks the question **before** the confirmation that erases the disk, in the
  same dialog, and says what will be missing if you carry on. During the run the optional settings are skipped
  rather than fatal, and the completion message names what did not get applied instead of only saying
  "finished". Measured: the task used to die at 86%, after a four-minute image apply and before a single boot
  file was written, leaving a drive carrying a complete Windows that could not start.
- **When Windows will not write the boot record, the message says so in words.** `bcdboot` reported its refusal
  as *"exited with code 183"* followed by sixty lines of trace with the one useful line buried in the middle.
  The known cause is now named, along with the fact that the drive will not boot; the trace stays in the log.
- **The disk pickers no longer put this PC's own internal drive first.** The list is sorted to leave a drive
  that is safe to erase at the top, but the test for "external" counted anything on SATA, and any model name
  containing "SSD" — which is how internal drives describe themselves. On the machine this was found on, the
  pre-selected target for *Create Windows USB*, *Clone*, *Restore*, *Format* and *Wipe* was the internal 954 GB
  NVMe. Only the bus a drive is attached by counts now.
- **A recovery that comes back empty says so.** Nine deleted files were recovered with the right names, the
  right sizes and not one byte of content: the drive had already erased the blocks, as an SSD does within
  seconds of a delete. DriveForge reported "Recovered 9 of 9 files" and left the user to find out by opening
  them. Files that come back entirely empty are now counted and named in the result — separately from partial
  ones, because a full-length file of nothing is not a partial recovery.
- **The time remaining is no longer quoted from a speed that no longer exists.** A 74-minute clone ended by
  announcing *"Remaining: 06:47:35"* two minutes before it finished: the copy was over and the run was applying
  permissions, which moves no data, so the trailing average had decayed and the arithmetic did the rest. When
  nothing has moved for a whole window there is no current speed, and the estimate now falls back to elapsed
  time against progress — six minutes for that same sample — or says nothing at all.
- **A finished download no longer claims to still be downloading.** The status line kept reading
  "Downloading … (100%)" for as long as the window stayed open; measured fourteen minutes after the file was
  complete and its size verified.
- **Stop and Pause are back on the three screens that hid them.** *Download an ISO*, *Multi-boot USB* and
  *Export to VHDX* all run work that checks for Stop the whole way through — a multi-gigabyte download, the
  Ventoy fetch, a whole-PC export — and all three collapsed the buttons, leaving killing the app as the only
  way out. (*Clean traces* had already hit this and un-hidden the button for itself; the other three were
  missed.)
- **Twenty-one pieces of the interface that never translated.** The **OK** and **Cancel** buttons of the three
  hand-built dialogs — used about thirty times between them, in front of choosing which disk to erase, which
  partition operation to run and which firmware to boot with — were English string literals in all seventeen
  languages. So were fourteen dialog titles, nine of which already had a translated key sitting unused in the
  string table. So were five live status lines, including the whole visible state of the ISO downloader and the
  line a clone shows for its first several minutes.
- **Three more things a screen reader could not read.** *Export to VHDX* was the one sidebar button with no
  name at all, and the rows of every action menu in the app — including the one that picks which disk to erase
  — had none either, because their content is a layout of text blocks rather than text.
- **Two claims in the README that were not true.** The system disk is not hidden from the target list; it is
  listed last, marked *RUNNING WINDOWS (no format/erase)*, and refused by every task that erases. And the task
  is chosen from the sidebar, not from a dropdown that has been collapsed for some time.

### Fixed — third pass, from the partition tools and a read of the flows hardware could not reach

- **A Stop during the multi-boot engine download was accepted on screen and then thrown away — and the drive
  was erased anyway.** The download is not a child process, so Stop took the no-process path, said
  *"Stopping…"* and set the flag; the setup code then cleared that flag on its way past and handed the whole
  disk to Ventoy. The user's abort was acknowledged and their data erased in the same breath. Honoured now,
  before anything is touched.
- **A recovery read that fails part-way keeps what it rescued.** A short read kept its partial file — the code
  says why, and it is right — but an *exception* deleted every byte already written and reported the file as
  unrecoverable. Those are the same situation, a dying drive, and it is the one this tool exists for.
- **Verification no longer excuses a target it could not read.** The content check reads the source and the
  freshly written file; when either throws it was filed as "unverifiable protected source file", which the
  caller explicitly treats as *not* an error. So a bad block on the drive just written — the precise failure
  the option exists to catch — came back as a clean clone. Which side failed is now established before the
  verdict.
- **The saved clone report no longer grades itself more kindly than the app did.** It computed its own verdict
  from eight checks while the app weighed thirteen, omitting a failed BitLocker request, a raw copy that
  dropped files, regions zero-filled from unreadable source sectors, and both kinds of stop. A clone the app
  had just called *needs review* was written to the Desktop as **"Result: pass"** — and the report is the part
  that outlives the dialog.
- **The same report no longer claims every file was byte-compared.** It said so, and then concluded "no silent
  corruption detected". What actually happens is a presence-and-size check on every file plus a byte-compare
  of the boot-critical ones and one in eight of the rest. It now says that, and the verdict claims only what
  those checks can support.
- **A stopped search for lost partitions says it was stopped.** The scan exits early on a Stop and on its
  128-find cap, and returned the partial list either way — so pressing Stop on a four-terabyte disk produced
  *"No partition signatures were found"*, the most load-bearing negative answer this tool gives, about a disk
  that had been read to three percent.
- **A large but nearly-empty .vhdx is no longer refused as too big.** The space gate measured the image
  *file*, and a fixed-size VHDX's file is its full provisioned size. The restore code says exactly this in its
  own comment and defers the check to after the attach, where the real used size is known; the gate that ran
  first made that unreachable.
- **A restore no longer reports driver injection and debloat done by an earlier run.** Those counters were
  reset only inside the USB-creation flow, but the completion dialog that reads them is shared with all three
  restore paths — so restoring an image after building a USB in the same session announced *"Drivers from this
  PC: 12 package(s) added"* about a run that injected nothing.
- **Closing the window mid-run no longer leaves the machine holding the pieces.** A backup, a clone and a VHDX
  export each take a VSS snapshot and map it to a drive letter, and the backup stages into a `.wim.dfnew`.
  Closing the window is allowed and kills the work — but it also skips the cleanup, so the snapshot stayed
  registered, the letter stayed mapped (Explorer showing a phantom second copy of Windows), and a partial file
  of up to half the used space sat on the backup drive that nothing would ever resume or remove. All three are
  now undone on the way out.
- **Two partition tools spoke for a different tool.** *Delete a partition* on a disk with no volumes answered
  "This disk has no formatted volumes **to check**" — the file-system checker's sentence, shared with *Resize*
  and *Set active*. And *Grow* with no free space after the partition ran DiskPart and surfaced *"The system
  cannot find the file specified"*, then offered to file a bug report; it now says there is no unallocated
  space immediately after that partition, and why that is the only space it could use.
- **The confirmation lists the two options it was leaving out.** *Remove bloatware* and *Add this PC's drivers*
  both ran and both change the image — one removes apps, the other injects driver packages — and neither
  appeared in the summary shown immediately before the disk is erased.
- **Three more pieces of English in the interface**: the drive summary and the drive-tools title said
  "Disk N" in every language, and the health report left the previous operation's progress row on screen —
  measured reading "Progress: 100.0% (5.7 / 5.7 GiB)" underneath a health check that had copied nothing.

### Fixed — fourth pass, from restoring a real 88.6 GB backup of this PC

- **A restore that cannot write the boot record now says so in words.** Five flows make a drive bootable and
  all five end at `bcdboot`, whose failure is an exit code followed by sixty lines of BFSVC trace. Exactly one
  of them — Windows To Go — translated that into a sentence; the other four, **including both restore paths**,
  showed `bcdboot.exe exited with code 1` and the trace. Restore is the flow someone runs standing in front of
  a PC that no longer starts, and the failure is not hypothetical: where security software refuses to mount a
  new boot store, `bcdboot` fails this way every time, against a freshly formatted partition. All five now give
  the same sentence — the drive will not boot, everything else was copied — and name the antivirus when that is
  what happened. A rule in the test suite fails the build if a new flow ever calls `bcdboot` directly again.

- **A finished operation no longer follows you around the app.** The progress row describes the operation that
  wrote it, not the page you are looking at — but leaving a page carried the last run's finished bar along.
  Measured: after securely shredding seven files, *Create Windows USB* opened with a full bar reading
  **"Progress: 100.0% (1.0 / 1.0 GiB)"**, about work that page had never done, beside an elapsed time of
  `00:00:00`. Eighteen flows cleared the row themselves and the shredder did not, so it is now cleared at the one
  point every navigation passes through — which retires the whole class rather than one flow's share of it. An
  operation that is still running keeps its row: navigating away mid-run is allowed, and its progress follows.
- **The shredder stops claiming it overwrote a gigabyte.** Its progress total was floored at 1 GiB — a sensible
  default for whole-disk work, wrong for a handful of files — and completion pins the counter to the total, so
  erasing 2.9 MB ended on *"100.0% (1.0 / 1.0 GiB)"*. From a tool whose entire job is overwriting, that is the
  one number it must not invent. It now uses the real size, and because the size is only shown above 0.5 GB, a
  small shred simply omits it; the bar is pinned directly at the end so it still reads 100% when it finishes.
- **The progress row follows the language.** Seen on screen in Arabic: the whole window had mirrored and every
  label had changed script, while the row underneath still read *"Progress: … | Elapsed: … | Remaining: …"* in
  English. The translation existed in all seventeen languages — the row is written from code, and the routine
  that repaints code-written text after a language change did not know about it.

- **A restore no longer ends by announcing "USB creation finished."** An 88.6 GB backup of this PC was
  restored onto a drive, and the completion dialog said a USB stick had been made. The dialog is shared by
  every job the Start button dispatches and already varies by mode elsewhere; the headline simply never did.
  Someone who has just restored a dead machine's backup should not be told they made a USB stick.
- **The progress bar stops walking backwards at the end of a long copy.** The copy phase advances through the
  40–82% band while each stage change sets the bar to a fixed value — and the stage that follows the copy was
  pinned at 80%, below the band it was leaving. Measured on a 49-minute restore: 82% → 80%. Every other flow's
  post-copy stages already sat at 84, 86, 88, 90 and 95; the clone and the restore were the two exceptions.
- **A restore's remaining time is computed from the image, not from a multiplier.** The total was the
  *compressed* file's size × 1.8 — 159.5 GiB for an image that wrote 141.2 GB — so the bar could never reach
  the top of its band and the countdown swung between "8:05:00" and "1:37" during one copy. The image's own
  uncompressed content size is read six lines earlier for the capacity check; it is now used here too.

### Fixed — fifth pass, from an adversarial review of everything the testing changed

Every change made during the testing above was then reviewed by eight independent readers, one per area, and
each finding was put to three more readers whose job was to refute it. Thirty-two claims were raised; ten
survived, and they reduce to these. Three of them were defects introduced by the fixes themselves.

- **Polish told users a partition could `rosнąć`.** The word is *rosnąć* — the third letter was a Cyrillic **н**
  (U+043D), which renders as a Latin *n* and is not one. One character, in one string, invisible to the compiler
  and to every key-parity check.
- **Six Romanian strings were written in the wrong alphabet.** They used the Turkish cedilla *ş/ţ* where the
  other 629 Romanian strings use the comma-below *ș/ț* the language actually takes — several mixing both inside
  one sentence — and one misspelled the pluperfect *ștersese*. Both of these now have a rule: every language
  block is checked against its own alphabet, and the build fails on a letter that does not belong to it.
- **Changing the language no longer erases a failed run's verdict.** Three flows deliberately leave their result
  in the progress row after the run ends — *"Failed after 12:04"*, or a stopped surface test's partial bar that
  the dialog beside it refers to — and each takes care that nothing later can wipe it. The repaint added for
  Arabic blanked all of it to *"0.0% | 00:00:00"*, turning a failed run into an idle one. A running operation
  still follows the language; an idle row is left alone.
- **A clone no longer leaves the window's close handler working against dead state.** The clone records its
  shadow copy so that closing mid-run can still undo it, and its cleanup deletes the snapshot but never said so
  — the other two flows that do this were paired correctly, the clone was not. Closing the window any time after
  a clone then spent up to twenty seconds blocking on a snapshot that had been gone for hours.
- **The last dialog before a disk is erased no longer promises work that will not happen.** *Remove bloatware*
  and the two driver-injection options are hidden and disabled when the mode changes, but never unticked — so
  ticking them to build a Windows USB and then starting a **restore** listed them in the final confirmation,
  about a flow that never applies them. The summary now lists an option only when the mode can actually run it.
- **A finished export reports the size it wrote.** The progress total is an estimate — the used space of `C:` —
  and the engine skips the pagefile, the hibernation file and the temp tree, so on a real run it read 418.7 GiB
  for an image holding 219.7 GiB. Measuring beats estimating once the copy has stopped, and the measurement was
  there all along.

### Fixed — sixth pass, from installing the multi-boot engine for real

- **The multi-boot drive picker now says which of the disks it lists is not a removable drive.** It asks
  *"Choose the USB drive to use as a multi-boot drive"* and then offers every disk that is not the running
  Windows — on the machine this was found on, that meant the internal 954 GB NVMe appeared in the list, and on
  an ordinary PC it means the second internal drive with the user's data on it. Two things were already right:
  removable drives sort first, so the default selection is safe, and the confirmation that follows names the
  disk, its size and its contents before anything is erased. What was missing was any way to tell, *in the list
  itself*, that one of those lines is an internal disk. Each one that is not removable now says so.

### Added

- **The app can point at its own source.** DriveForge is GPLv3 and had exactly two outbound links, Ko-fi
  and the bug form; neither led to the code. *Settings → About & support* now offers **View the source on
  GitHub**, so anyone running it can reach the repository, the licence and every release without being
  told where to look. Translated into all seventeen languages, like every other button.
- **The winget manifest for this release, kept in `packaging/winget/`** beside the code it describes.
  DriveForge has been installable with `winget install ForgeLabsSoft.DriveForge` since July 2026; each
  release is a pull request adding one version folder, and the copy that was submitted now lives in this
  repository instead of only in Microsoft's. It validates cleanly, and its hash was checked against the
  binary the release build actually published rather than a local one.

### Under the hood
- The write, verify, re-online and eject sequence now exists in exactly one place, and the parts that must
  happen once per run rather than once per drive — the busy state, the stopwatch, the progress total, the
  completion chime — are owned by the code that runs the queue. A second copy of that sequence is what the
  previous release spent a day removing from seventeen other flows.
- The two checks that erase a disk on this task are now covered by the invariant suite, which had never
  looked at this flow at all. 21 new strings in all 17 languages; one dead string removed.
- Four new checks, each one written after the defect it catches: every sidebar button must carry a
  screen-reader name, no button built in code may have a caption typed into the source, no status line may
  be written in English from the code, and the machine's own internal drive must never rank as "external".
  27 further strings in all 17 languages.

## v4.3.3 — 2026-09-28

### Fixed
- **A failed operation no longer leaves the progress bar claiming it is still working.** When one ended with an
  error, the bar kept its last percentage and the line beneath it went on advertising a "Remaining" time that
  would never move again — directly above the dialog explaining the failure, with the Windows taskbar still
  showing a green progress bar underneath. The row is now cleared the moment the failure is known, before the
  dialog appears, and the elapsed time it reports is how long the work actually ran rather than how long the
  dialog was left on screen. This covers recovering files, recovering into a zip, imaging a drive, the surface
  test, exporting a bootable VHDX, moving a partition on both MBR and GPT disks, scanning for lost partitions,
  and setting up multi-boot.
- **A failed multi-boot setup no longer leaves "Setting up multi-boot engine on Disk N..." on screen** — including
  when it stops for a reason that is not an error at all: the target disk changing identity while the engine is
  still downloading.
- **An operation that keeps its progress bar after a failure no longer quotes a "Remaining" time beside it.**
  Wiping, shredding, backing up, writing an image and the capacity test deliberately leave the bar where the
  failure found it — on a destructive operation that number is the only thing on screen saying how far the write
  got — but the estimate next to it was still being calculated, and froze into a countdown that would never move.
  The bar stays; the estimate goes.
- **Two exits that are not errors no longer leave the app saying it is working.** Writing an image and formatting
  both stop before they start if the target drive turns out to have changed identity in the meantime; the status
  line survived that, and for formatting it survived a completed run too.

### Under the hood
- **Every build warning is gone — 299 of them, down to zero, with nothing about the app changed.** Most (288)
  were nullable annotations sitting in files that had no nullable context; the project now switches that context on
  without switching on the nullable warnings, which makes the existing annotations legal without moving a single line
  of code. The rest were genuine leftovers: the WindowsDesktop SDK reference that has not been needed for years, two
  failure flags that were set and then never read, a progress-throttle field that had lost its reader, an always-zero
  addend in the file-carving size table, and a deliberately fire-and-forget screen update that now says so out loud.
  Each site was first examined for a missing check hiding behind the warning, rather than simply silenced — and the two
  real defects that search turned up are left for their own fix, since they change behaviour.

## v4.3.2 — 2026-09-17

### Fixed — a regression in v4.3.1
- **Secure shred stopped erasing OneDrive, deduplicated and CompactOS-compressed files.** The v4.3.1 fix that
  stopped shred following symbolic links tested the wrong thing: the "reparse point" attribute marks a whole
  family of files, not just links, and OneDrive Files On-Demand placeholders, NTFS-deduplicated files and
  CompactOS-compressed files all carry it while being perfectly ordinary files. Shredding a folder of them
  reported "Nothing selected"; in a mixed folder they were left out of every count and then deleted without
  being overwritten, while the summary said the folder had been securely erased. Only genuine symbolic links
  and junctions are skipped now, and one picked directly is reported as skipped rather than silently dropped.
- **Stop no longer hangs the streaming clone.** Stopping it broke the pipe between the two copy processes and
  left one of them waiting forever, so the operation never ended.
- **Stop works when the step it was going to interrupt has just finished.** It previously did nothing at all in
  that case — the operation carried on with no sign the button had been pressed.
- Pausing the clone no longer makes the app warn that the drive has stalled.
- A disk image you stop part-way now tells you if it also had to skip unreadable sectors.
- The progress bar during a FAT32 / exFAT recovery scan no longer jumps to 100% on the first folder.
- Switching language no longer wipes the result line left by a stopped surface test.

### New
- **The "bootable USB from an image" task now accepts raw disk images**, not just ISOs: `.img`, `.bin`,
  `.raw` and `.dd` can be selected in the file picker or dropped onto the window, and dropping one switches
  to that task automatically. The write engine always could do this — it copies bytes straight to the drive
  without looking at the file — the file picker simply never offered them. Suggested by a user.
- The same picker no longer offers `.wim` / `.esd` for that task. They are file-level archives: written to a
  drive raw they produce nothing bootable, so offering them there could only waste someone's time.

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
