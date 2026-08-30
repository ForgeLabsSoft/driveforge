# Verification

DriveForge writes to whole disks. A regression here does not throw an exception — it erases the wrong drive, or
reports success on a clone that is missing files. So the checks below run on every push and every pull request, and
a failure blocks the release step.

## Run it

```powershell
dotnet test tests/DriveForge.Tests/DriveForge.Tests.csproj
```

Takes about a second. **Always pass the project path** — plain `dotnet test` at the repo root picks up
`DriveForge.csproj`, runs zero tests and exits 0, which looks exactly like passing.

Run one group:

```powershell
dotnet test tests/DriveForge.Tests/DriveForge.Tests.csproj --filter "FullyQualifiedName~Localization"
```

## What is covered

| File | Checks |
|---|---|
| `PureLogicTests.cs` | The pure helpers, with a regression test for every logic bug that has shipped: version sorting, duration formatting past 24 h, the JSON-payload sentinel, GPT CRC round-trip, the health-status substring trap, argument quoting, path-traversal in recovered filenames. |
| `LocalizationTests.cs` | All 17 languages: key parity, `{0}`/`{1}` arity per key (a mismatch is a runtime `FormatException` or a silently dropped value), every `L("…")` in the source resolves, no unbalanced braces, no new orphan keys, and multi-paragraph dialogs keeping their structure. |
| `InvariantTests.cs` | DriveForge's own contracts: every paired flag raised is cleared in a `finally`; a reentrancy guard that is read is also armed; destructive confirms pass an explicit safe default button; every raw disk handle is checked for validity. |
| `ApiSurfaceTests.cs` | Pins the helper names the tests reach by reflection, so a rename fails once, clearly, instead of scattering `MissingMethodException` across the suite. |

## What is NOT covered

Anything that needs real hardware: actually erasing a disk, booting a clone, whether a translation *means* the right
thing. That is [`MANUAL-TEST-CHECKLIST.md`](MANUAL-TEST-CHECKLIST.md), and it still has to be worked through by hand
before a release.

## Design notes

**Reflection, not a refactor.** The helpers are `private static` on a 15,000-line partial `MainWindow`. Widening ~200
declarations to `internal`, or extracting them, would be a large diff across hand-verified code purely for test
ergonomics. Reflection gets the same coverage with a zero-line production diff. The cost is that a rename becomes a
runtime failure — `ApiSurfaceTests` turns that into one clear message.

**Roslyn, not regex, for the invariant checks.** Measured on this codebase: a grep for `SetBusy(busy: true` misses
eight positional `SetBusy(true, …)` calls; `_progressFixedTotal = sizeKnown;` is invisible to a `= true` pattern; a
line-based scan for confirm dialogs missing a safe default found 6 where the parser finds 27, because the same line
contained `!= MessageBoxResult.OK`. Brace counters also desync on the interpolated strings with nested quotes that
are common here.

**Ratchets, not absolutes, where debt exists.** `NoNewOrphanKeys` carries a baseline of the 16 keys that are dead
today, so the rule is enforceable now and any *new* orphan fails. Shrink the baseline; never grow it. Same idea for
the one known translation drift in `MultiLineDialogsKeepTheirLineBreaks`.

**The exemption lists are the honest part.** `InvariantTests.PairedFlagExemptions` names the two places where the
rule genuinely cannot apply (documented cross-method ownership hand-offs). Every entry there is a place the machine
is not protecting you — keep the list short.
