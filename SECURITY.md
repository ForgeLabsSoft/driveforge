# Security Policy

## Reporting a vulnerability

Please report security issues **privately**, not in a public issue.

Use GitHub's private reporting: the repository's **Security** tab → **Report a vulnerability**
(GitHub → Security advisories). This keeps the details private until a fix is available.

Please include:
- what the issue is and its impact,
- steps to reproduce (or a proof of concept),
- the DriveForge version and your Windows version.

We aim to acknowledge reports within a few days. Because DriveForge performs destructive
disk operations, reports about wrong-disk targeting, data loss, or privilege issues are
treated as highest priority.

## Scope notes

- DriveForge runs elevated and performs disk writes by design; "it can erase a disk" is
  expected behaviour, not a vulnerability. Reports about it erasing the **wrong** disk, or
  writing without adequate confirmation, are in scope.
- During a PC clone, DriveForge temporarily disables third-party antivirus **on the newly
  created clone** (offline registry) so first-boot app repair can run, then re-enables it.
  It never disables antivirus on your running PC. This behaviour is documented and intended.

## Supported versions

The latest release is supported. Please reproduce on the current version before reporting.
