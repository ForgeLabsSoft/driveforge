using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace DriveForge.Tests;

/// <summary>
/// DriveForge's own correctness contracts — not generic lint. Each rule here encodes a bug class that has actually
/// bitten this codebase more than once: a flag raised and never lowered, a reentrancy guard that is read but never
/// armed, a destructive dialog whose default button erases a disk when you press Enter.
/// </summary>
public class InvariantTests
{
	/// <summary>
	/// Flags that must be lowered in a finally of the same method that raised them. Leaking one of these strands the
	/// UI (buttons dead for the rest of the session), or — for the progress flags — silently changes the maths of
	/// every later operation.
	/// </summary>
	private static readonly string[] PairedFlags =
	{
		"_progressFullRange", "_progressFixedTotal", "_toolOpStarting", "_startInProgress",
		"_cleanBusy", "_analyzerBusy", "_syncingDisk", "_suppressLineProgress",
		"_suppressRecoverSelUpdate", "_sleepReasserting", "_refreshOwnsBusy",
	};

	/// <summary>
	/// Deliberate, documented exceptions. Both are cross-method ownership hand-offs the code explains in a comment;
	/// see the notes on each. Keep this list SHORT — every entry is a place the rule cannot protect.
	/// </summary>
	private static readonly HashSet<(string Method, string Flag)> PairedFlagExemptions = new()
	{
		// Releases with a raw `isBusy = false` immediately before Application.Current.Shutdown(), deliberately
		// bypassing SetBusy so Window_Closing's "operation running?" modal cannot block an unattended run.
		("RunHeadlessCloneAsync", "isBusy"),
		// Re-asserts busy five times purely to repaint the status text; the single release is in the CALLER's
		// finally (ExportVhdx_Click), because busy must persist across the Hyper-V VM offer.
		("ExportBootableVhdxCoreAsync", "isBusy"),
	};

	/// <summary>
	/// RULE 1 — every raise of a paired flag is matched by a lowering in a finally of the same method.
	///
	/// This is the single highest-value structural rule for this app: leaked flags caused a stuck progress bar, a
	/// permanently held keep-awake request, and a toolbar frozen mid-session.
	/// </summary>
	[Fact]
	public void EveryPairedFlagRaiseIsClearedInAFinally()
	{
		var violations = new List<string>();

		foreach (var (file, method, name) in SourceModel.Methods())
		{
			if (method.Body == null && method.ExpressionBody == null) continue;

			foreach (string flag in PairedFlags)
			{
				if (PairedFlagExemptions.Contains((name, flag))) continue;

				AssignmentExpressionSyntax[] assignments = SourceModel.AssignmentsTo(method, flag).ToArray();
				// A raise is any assignment that is not a literal `false` — this deliberately catches
				// `_progressFixedTotal = sizeKnown;`, which a `= true` pattern would miss entirely.
				AssignmentExpressionSyntax[] raises = assignments
					.Where(a => !SourceModel.AssignsFalse(a) && !SourceModel.IsInsideFinally(a, method))
					.ToArray();
				if (raises.Length == 0) continue;

				bool clearedInFinally = assignments.Any(a =>
					SourceModel.AssignsFalse(a) && SourceModel.IsInsideFinally(a, method));

				if (!clearedInFinally)
					violations.Add($"{SourceModel.Where(file, raises[0])}  {name}: raises {flag} but never clears it in a finally");
			}
		}

		Assert.True(violations.Count == 0,
			"Paired flag raised without a guaranteed release:\n  " + string.Join("\n  ", violations));
	}

	/// <summary>
	/// RULE 2 — a handler that CHECKS a reentrancy guard must also ARM it.
	///
	/// Reading `_toolOpStarting` without ever setting it makes the guard inert: the whole pre-write window (file
	/// pickers, action menus, the confirm dialog — all of which pump the message loop) runs with nothing stopping a
	/// second destructive operation from starting on the same disk.
	/// </summary>
	[Fact]
	public void EveryHandlerThatChecksTheReentrancyGuardAlsoArmsIt()
	{
		const string Guard = "_toolOpStarting";
		var violations = new List<string>();

		foreach (var (file, method, name) in SourceModel.Methods())
		{
			bool reads = method.DescendantNodes().OfType<IdentifierNameSyntax>()
				.Any(id => id.Identifier.Text == Guard && id.Parent is not AssignmentExpressionSyntax);
			if (!reads) continue;

			bool arms = SourceModel.AssignmentsTo(method, Guard).Any(SourceModel.AssignsTrue);
			if (!arms)
				violations.Add($"{SourceModel.Where(file, method)}  {name}: checks {Guard} but never sets it — the guard is inert");
		}

		Assert.True(violations.Count == 0,
			"Reentrancy guard read but never armed:\n  " + string.Join("\n  ", violations));
	}

	/// <summary>
	/// RULE 3 — destructive confirm dialogs must pass an explicit safe default button.
	///
	/// Without a 4th argument, MessageBox defaults to the FIRST button, so pressing Enter or Space on a
	/// "this will erase the drive" prompt erases the drive. The codebase already states this contract in a comment
	/// ("default to the SAFE button — Enter/Space must NOT erase the drive"); this makes it enforceable.
	///
	/// Scoped to methods that actually perform destructive work, because the general form has ~27 hits of which only
	/// a handful matter.
	/// </summary>
	[Fact]
	public void DestructiveConfirmDialogsDefaultToTheSafeButton()
	{
		// "Destructive" here means: pressing Enter writes to a disk, discards data, or kills work in progress.
		// The first version of this list missed TestBoot_Click and DownloadIsoAsync — both were then found by hand,
		// which is exactly what this rule exists to prevent. Add to it whenever a new flow touches user data.
		string[] destructiveMethods =
		{
			"WipeDrive_Click", "WipeFreeSpaceFlow", "ShredFiles_Click", "FormatDrive_Click",
			"CapacityTest_Click", "QuickPartitionFlow", "CreatePartitionFlow", "DeletePartitionFlow",
			"ResizePartitionFlow", "SetActiveFlow", "InitializeDiskFlow", "ConvertPartStyleFlow",
			"SsdSecureEraseFlow", "MovePartitionFlow", "MoveGptFlow",
			// Enter here takes the selected disk OFFLINE on the host and boots a guest OS that writes to it.
			"TestBoot_Click",
			// Enter here overwrites an existing, possibly multi-gigabyte, downloaded ISO.
			"DownloadIsoAsync",
			// Enter here aborts a running clone/wipe by killing the diskpart/dism process tree mid-write.
			"Window_Closing",
			// Enter here runs chkdsk /r /x, which force-dismounts the volume and can relocate data into found.000.
			"RunChkdskForSelectedDriveAsync",
			// The raw image write erases the whole target, and until the duplicator was built NONE of its confirms
			// was covered here - not even the one that names the disk it is about to wipe.
			"WriteIsoImageFlowAsync", "WriteIsoImageToManyFlowAsync", "RunImageWriteQueueAsync",
		};

		var violations = new List<string>();

		foreach (var (file, method, name) in SourceModel.Methods())
		{
			if (!destructiveMethods.Contains(name)) continue;

			foreach (InvocationExpressionSyntax call in SourceModel.Calls(method, "Show"))
			{
				string args = call.ArgumentList.ToString();
				bool isChoice = args.Contains("MessageBoxButton.OKCancel") || args.Contains("MessageBoxButton.YesNo");
				if (!isChoice) continue;

				// A destructive method also contains harmless OFFERS ("Hyper-V isn't enabled, open Windows
				// Features?", "Downloaded — show it in the folder?") whose Yes only opens a window. Those must not
				// be flagged, or the rule becomes noise and gets ignored.
				//
				// The two shapes are reliably distinguishable in this codebase: a CONFIRM GATE is written in the
				// bail-out form `if (Show(...) != MessageBoxResult.OK) return;` (or is assigned to a variable and
				// tested later), while an optional offer is the positive form `if (Show(...) == ...Yes) { ... }`.
				// So: skip anything compared with `==`.
				bool isOptionalOffer = call.Ancestors().OfType<BinaryExpressionSyntax>()
					.Any(b => b.IsKind(SyntaxKind.EqualsExpression) && b.Left.DescendantNodesAndSelf().Contains(call));
				if (isOptionalOffer) continue;

				// The default-button argument is the safety-relevant one: passing `MessageBoxResult.Cancel`/`.No`
				// is what stops Enter/Space from triggering the destructive branch.
				bool hasSafeDefault = args.Contains("MessageBoxResult.Cancel") || args.Contains("MessageBoxResult.No");
				if (!hasSafeDefault)
					violations.Add($"{SourceModel.Where(file, call)}  {name}: destructive confirm without an explicit safe default button");
			}
		}

		Assert.True(violations.Count == 0,
			"Destructive confirm dialogs where Enter triggers the destructive action:\n  " + string.Join("\n  ", violations));
	}

	/// <summary>
	/// RULE 4 — every raw disk handle is checked for validity before use.
	///
	/// CreateFile returns INVALID_HANDLE_VALUE rather than throwing. Writing through an unchecked handle is a
	/// silent no-op that reports success; all 24 existing call sites already check, so this is a regression guard.
	/// </summary>
	[Fact]
	public void EveryCreateFileResultIsCheckedForValidity()
	{
		var violations = new List<string>();

		foreach (var (file, root) in SourceModel.Parsed)
			foreach (InvocationExpressionSyntax call in SourceModel.Calls(root, "CreateFile"))
			{
				// Find the enclosing statement, then look at the next few statements for an IsInvalid check.
				StatementSyntax stmt = call.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
				if (stmt == null) continue;
				BlockSyntax block = stmt.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
				if (block == null) continue;

				int idx = block.Statements.IndexOf(stmt);
				string following = string.Join("\n", block.Statements.Skip(idx).Take(5).Select(s => s.ToString()));
				if (!following.Contains("IsInvalid") && !following.Contains("INVALID_HANDLE"))
					violations.Add($"{SourceModel.Where(file, call)}  CreateFile result not checked for IsInvalid within 5 statements");
			}

		Assert.True(violations.Count == 0,
			"Unchecked raw disk handle (writes through it silently do nothing):\n  " + string.Join("\n  ", violations));
	}

	/// <summary>
	/// RULE 6 — clearing the speed WINDOW must also clear the speed DERIVED from it.
	///
	/// <c>_speedWindow</c> holds the samples; <c>progressSpeedMb</c> holds the value computed from them, and nothing
	/// decays it. Clearing only the window leaves the previous operation's throughput in the field, and
	/// UpdateProgressStats divides by it — so the new operation's first "Remaining" is computed from the speed of the
	/// last one.
	///
	/// This rule was first written, seen to report 17 violations, and withdrawn on the assumption that the callers
	/// reset the state one frame up. That was true for two of them and wrong for the rest: seven were Click handlers
	/// wired straight from XAML with no wrapper at all. Checking two cases and generalising from them is how the bug
	/// survived. All 17 now reset it, so the invariant holds everywhere and the rule can be stated plainly.
	/// </summary>
	[Fact]
	public void ClearingTheSpeedWindowAlsoResetsTheSpeed()
	{
		var violations = new List<string>();

		foreach (var (file, root) in SourceModel.Parsed)
			foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				if (call.Expression is not MemberAccessExpressionSyntax ma
					|| ma.Name.Identifier.Text != "Clear"
					|| ma.Expression is not IdentifierNameSyntax w || w.Identifier.Text != "_speedWindow") continue;

				// Quantified per CLEAR SITE, not per method. Checking "does this method assign progressSpeedMb
				// anywhere" passes a method that resets once and then clears the window a SECOND time for a later
				// phase — which is exactly what the clone does when it re-points the bar at the verify pass, and
				// that second site went unnoticed until a reviewer read it.
				StatementSyntax? statement = call.FirstAncestorOrSelf<StatementSyntax>();
				if (statement?.Parent is not BlockSyntax block) continue;

				var statements = block.Statements;
				int at = statements.IndexOf(statement);
				const int Window = 3;   // the reset is conventionally on the same line or immediately around it
				bool resetNearby = Enumerable
					.Range(Math.Max(0, at - Window), Math.Min(statements.Count, at + Window + 1) - Math.Max(0, at - Window))
					.Any(k => SourceModel.AssignmentsTo(statements[k], "progressSpeedMb").Any());

				if (!resetNearby)
					violations.Add($"{SourceModel.Where(file, call)}  clears _speedWindow with no progressSpeedMb reset " +
						"beside it — this phase's first ETA is computed from the previous phase's throughput");
			}

		Assert.True(violations.Count == 0,
			"Speed window cleared without resetting the derived speed:\n  " + string.Join("\n  ", violations));
	}

	/// <summary>
	/// RULE 6b — a flow entered DIRECTLY, with no wrapper above it, must also zero the progress bar itself.
	///
	/// The bar only ever advances, so a flow that does not zero it inherits the previous operation's position — and
	/// an operation that ended at 100% leaves a full bar for the whole next run, with the ETA suppressed on top
	/// (percent >= 99.95 disables it). Unlike the speed rule this one genuinely cannot be stated for every method:
	/// most progress flows are inner methods whose caller zeroes the bar one frame up. So this lists the flows
	/// invoked straight from a dialog branch, with nothing above them. Add an entry when a new one appears.
	/// </summary>
	[Fact]
	public void DirectlyEnteredProgressFlowsZeroTheBar()
	{
		// RunImageWriteQueueAsync is entered straight from two front ends and owns the bar for a whole run of
		// drives; inheriting the previous operation's position would leave a batch of twenty starting at 100%.
		string[] directlyEntered = { "ResumeDeepScanAsync", "RunImageWriteQueueAsync" };
		var violations = new List<string>();

		foreach (string target in directlyEntered)
		{
			var found = SourceModel.Methods().Where(m => m.Name == target).ToList();
			Assert.True(found.Count == 1, $"Expected exactly one {target}; found {found.Count}");
			var (file, method, name) = found[0];

			bool zeroesBar = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
				.Any(a => a.Left is MemberAccessExpressionSyntax ma
					&& ma.Name.Identifier.Text == "Value"
					&& ma.Expression is IdentifierNameSyntax bar && bar.Identifier.Text == "ProgressBar"
					&& a.Right is LiteralExpressionSyntax v && v.Token.ValueText is "0" or "0.0");
			if (!zeroesBar)
				violations.Add($"{SourceModel.Where(file, method)}  {name}: never zeroes ProgressBar.Value — the bar only " +
					"advances, so a resume after a completed operation sits at 100% for the whole scan");
		}

		Assert.True(violations.Count == 0,
			"Directly-entered progress flow that inherits the previous operation's bar:\n  " + string.Join("\n  ", violations));
	}

	/// <summary>
	/// RULE 7 — the "Remaining" estimate is suppressible, and the one path that outlives its operation suppresses it.
	///
	/// SurfaceTest_Click snapshots the stats line and restores it after SetBusy(false) blanks the row, so whatever
	/// "Remaining" says at that moment stays on screen indefinitely. A scan stopped part-way has an honest partial
	/// percentage and a real read speed behind it, so the ETA branch fires and a dead operation ends up advertising a
	/// countdown that never counts down. Both halves are checked: the guard existing in UpdateProgressStats, and
	/// SurfaceTest_Click actually using it.
	/// </summary>
	[Fact]
	public void TheStoppedSurfaceScanDoesNotLeaveALiveCountdown()
	{
		const string Flag = "_progressNoEta";

		var stats = SourceModel.Methods().Single(m => m.Name == "UpdateProgressStats");
		bool guarded = stats.Method.DescendantNodes().OfType<IfStatementSyntax>()
			.Any(i => i.Condition is PrefixUnaryExpressionSyntax neg
				&& neg.IsKind(SyntaxKind.LogicalNotExpression)
				&& neg.Operand is IdentifierNameSyntax id && id.Identifier.Text == Flag);
		Assert.True(guarded,
			$"UpdateProgressStats no longer guards its Remaining computation with !{Flag} — a stopped operation can " +
			"again leave a frozen countdown on screen.");

		var surface = SourceModel.Methods().Single(m => m.Name == "SurfaceTest_Click");
		Assert.True(SourceModel.AssignmentsTo(surface.Method, Flag).Any(SourceModel.AssignsTrue),
			$"SurfaceTest_Click no longer raises {Flag} before the UpdateProgressStats call whose output it snapshots.");
		Assert.True(SourceModel.AssignmentsTo(surface.Method, Flag).Any(SourceModel.AssignsFalse),
			$"SurfaceTest_Click raises {Flag} but never lowers it.");
	}

	/// <summary>
	/// RULE 5 — the source must stay parseable and the files must all be present. A trivial guard, but it turns a
	/// renamed/moved file into one clear failure instead of every other rule silently checking nothing.
	/// </summary>
	[Fact]
	public void AllSourceFilesAreFoundAndParse()
	{
		Assert.True(SourceModel.SourceFiles.Length >= 5,
			$"Expected the app's source files under {Mw.RepoRoot}DriveForge; found {SourceModel.SourceFiles.Length}");

		foreach (var (file, root) in SourceModel.Parsed)
		{
			Diagnostic[] errors = root.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
			Assert.True(errors.Length == 0, $"{System.IO.Path.GetFileName(file)} failed to parse: {errors.FirstOrDefault()}");
		}
	}

	/// <summary>
	/// RULE 6 — nothing may run bcdboot.exe except the one helper that turns its failure into a sentence.
	///
	/// bcdboot reports failure as an exit code plus sixty lines of BFSVC trace. Five flows called it and only
	/// ONE translated that: the other four — including BOTH restore paths, which is what someone runs standing in
	/// front of a PC that no longer starts — surfaced "bcdboot.exe exited with code 1". The failure is not
	/// hypothetical: on a machine whose antivirus refuses to mount a new boot store, bcdboot fails this way every
	/// time, against a freshly formatted partition. The next flow that needs a bootloader will copy whichever
	/// line it finds first, so make the wrong line impossible instead of hoping.
	/// </summary>
	[Fact]
	public void BcdbootIsOnlyEverRunThroughTheHelperThatExplainsItsFailure()
	{
		var offenders = new List<string>();
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				// A process launch naming bcdboot.exe as its executable argument.
				bool launchesBcdboot = call.ArgumentList.Arguments.Any(a =>
					a.Expression is LiteralExpressionSyntax lit
					&& lit.Token.ValueText.Equals("bcdboot.exe", StringComparison.OrdinalIgnoreCase));
				if (!launchesBcdboot) continue;
				string owner = call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "<none>";
				if (owner != "RunBcdbootAsync")
					offenders.Add($"{SourceModel.Where(file, call)} in {owner}()");
			}

		Assert.True(offenders.Count == 0,
			"bcdboot.exe is launched outside RunBcdbootAsync, so this flow will report its failure as a raw exit "
			+ "code and a BFSVC trace instead of saying the drive will not boot and why:" + Environment.NewLine
			+ string.Join(Environment.NewLine, offenders));

		// And the helper itself must still be there to route them through.
		Assert.True(SourceModel.Methods().Any(m => m.Name == "RunBcdbootAsync"),
			"RunBcdbootAsync is gone — every bootloader flow just lost its one explained failure path.");
	}

	/// <summary>
	/// The clone engine is cached in %LOCALAPPDATA%, which anything running as this user can write, and DriveForge
	/// starts it ELEVATED. Its two files are therefore pinned by SHA-256 in MainWindow.cs. This checks the pins
	/// against the archive actually embedded in the app, so that bumping wimlib without re-pinning fails here
	/// rather than on a user's machine, where the only symptom is a clone that refuses to start.
	/// </summary>
	[Fact]
	public void CloneEnginePinsMatchTheEmbeddedArchive()
	{
		string zipPath = System.IO.Path.Combine(Mw.RepoRoot, "DriveForge", "Resources", "wimlib-1.14.4-windows-x86_64-bin.zip");
		Assert.True(System.IO.File.Exists(zipPath), $"The embedded clone engine archive is missing: {zipPath}");

		string source = System.IO.File.ReadAllText(System.IO.Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);

		foreach ((string constant, string entryName) in new[]
		{
			("WimlibImagexSha256", "wimlib-imagex.exe"),
			("WimlibDllSha256", "libwim-15.dll"),
		})
		{
			var match = System.Text.RegularExpressions.Regex.Match(
				source, constant + @"\s*=\s*""([0-9A-Fa-f]{64})""");
			Assert.True(match.Success, $"{constant} is gone from MainWindow.cs, or is no longer a 64-character SHA-256.");

			var entry = archive.GetEntry(entryName);
			Assert.True(entry != null, $"{entryName} is not at the root of the embedded archive any more.");

			string actual;
			using (var stream = entry!.Open())
			using (var sha = System.Security.Cryptography.SHA256.Create())
			{
				actual = Convert.ToHexString(sha.ComputeHash(stream));
			}

			Assert.True(string.Equals(actual, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase),
				$"{constant} does not match {entryName} in the embedded archive." + Environment.NewLine +
				$"  pinned: {match.Groups[1].Value}" + Environment.NewLine +
				$"  actual: {actual}" + Environment.NewLine +
				"If wimlib was deliberately updated, re-pin both constants; never relax the check.");
		}
	}

	/// <summary>
	/// libwim-15.dll must stay pinned alongside the exe. wimlib-imagex.exe imports it, and Windows resolves an
	/// import from the executable's own directory first, so a check covering only the exe leaves an elevated code
	/// path open with nothing visibly wrong: the exe still matches its hash while the library beside it does not.
	/// </summary>
	[Fact]
	public void CloneEngineVerifiesItsLibraryAndNotOnlyItsExecutable()
	{
		string source = System.IO.File.ReadAllText(System.IO.Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		Assert.Contains("WimlibDllSha256", source);
		Assert.Contains("libwim-15.dll", source);
	}

	/// <summary>
	/// The engine must be found at a fixed path, never by scanning the cache folder for anything named
	/// wimlib-imagex.exe. That scan returned the first match in enumeration order, an order an attacker chooses by
	/// picking where to drop the file, and it is how an unprivileged program could get its own binary run as
	/// Administrator. Path.Combine only, so there is exactly one file the hash check can be talking about.
	/// </summary>
	[Fact]
	public void CloneEngineIsNeverLocatedByScanningItsFolder()
	{
		var method = SourceModel.Methods().FirstOrDefault(m => m.Name == "EnsureWimlibAsync");
		Assert.True(method.Method != null, "EnsureWimlibAsync is gone, and the clone engine's integrity check with it.");

		string body = method.Method.ToFullString();
		Assert.False(body.Contains("AllDirectories"),
			"EnsureWimlibAsync is scanning the tool folder again. Locate the engine with Path.Combine on a fixed " +
			"name, or the hash check guards one file while Windows executes another.");
		Assert.False(body.Contains("GetFiles("),
			"EnsureWimlibAsync is enumerating files to find the engine again. Use the fixed path.");
	}

	/// <summary>
	/// Verification has to read the file through a handle that denies writes and deletes, and that handle has to
	/// outlive the check - otherwise there is a gap between hashing the file and Windows executing it, which is the
	/// whole thing the pinning is for. FileShare.Read closes that gap; FileShare.ReadWrite would reopen it.
	/// </summary>
	[Fact]
	public void CloneEngineIsHeldAgainstModificationWhileItIsTrusted()
	{
		var method = SourceModel.Methods().FirstOrDefault(m => m.Name == "TryLockVerifiedWimlib");
		Assert.True(method.Method != null,
			"TryLockVerifiedWimlib is gone, so nothing holds the verified clone engine against replacement.");

		string body = method.Method.ToFullString();
		Assert.Contains("FileShare.Read", body);
		Assert.False(body.Contains("FileShare.ReadWrite") || body.Contains("FileShare.Write"),
			"The verified clone engine is shared with writers, so it can be swapped between the hash check and " +
			"CreateProcess. Only FileShare.Read belongs here.");
	}

	/// <summary>
	/// A drive's health may never be decided from its translated label.
	///
	/// DiskItem exposes two things that look interchangeable: HealthText, a localized sentence for display, and
	/// RawHealth, the status Windows itself reports. Passing the first to anything that DECIDES gave three languages
	/// a red "this drive is failing, replace it" banner over a healthy drive, because the check hunts for English
	/// words. Both still exist and still look alike at a call site, so the rule is enforced here rather than trusted.
	/// </summary>
	[Fact]
	public void DriveHealthIsNeverDecidedFromTheTranslatedLabel()
	{
		string[] deciders = { "IsHealthy", "HealthCardColor", "RecordHealthTrend", "LHealth" };
		List<string> problems = new List<string>();

		foreach ((string file, SyntaxNode root) in SourceModel.Parsed)
			foreach (string decider in deciders)
				foreach (InvocationExpressionSyntax call in SourceModel.Calls(root, decider))
					foreach (ArgumentSyntax arg in call.ArgumentList.Arguments)
					{
						string text = arg.ToString();
						// ToolHealthText / HealthTrendText are UI controls, not the label.
						if (!text.Contains("HealthText")) continue;
						if (text.Contains("ToolHealthText") || text.Contains("HealthTrendText")) continue;
						problems.Add($"{SourceModel.Where(file, call)}: {decider}({text}) decides from the TRANSLATED label - pass RawHealth.");
					}

		Assert.True(problems.Count == 0,
			"Drive health is being decided from a translated string, so the verdict changes with the interface " +
			"language:\n  " + string.Join("\n  ", problems));
	}

	/// <summary>
	/// The task the app opens on, and the sidebar entry highlighted beside it, must be the same task.
	///
	/// Startup sets both by hand, one statement after the other, while every LATER switch derives the highlight from
	/// the selected task through one mapping. So the two can disagree at startup only - the window would open with
	/// one task loaded and a different one lit in the sidebar. This reads the app's own mapping rather than pinning a
	/// particular choice, so changing which task the app opens on stays a one-line change.
	/// </summary>
	[Fact]
	public void TheStartupTaskAndTheHighlightedSidebarEntryAgree()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));

		// The mapping the app uses everywhere else: `i == ModeX ? NavY`, plus the final `: NavZ` fallback.
		Dictionary<string, string> map = Regex.Matches(src, @"i == (\w+) \? (Nav\w+)")
			.Cast<Match>()
			.ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
		Assert.True(map.Count > 0, "Could not find the mode -> sidebar mapping in MainWindow.cs.");

		Match start = Regex.Match(src, @"ModeBox\.SelectedIndex = (\w+);\s*(?:\r?\n\s*)*ShowWorkflowView\(\);\s*(?:\r?\n\s*)*HighlightNav\((Nav\w+)\);");
		Assert.True(start.Success,
			"Could not find the startup pair (ModeBox.SelectedIndex = ...; ShowWorkflowView(); HighlightNav(...);). " +
			"If startup was restructured, update this test to match.");

		string mode = start.Groups[1].Value, nav = start.Groups[2].Value;

		// Several mode constants share a value (ModeCloneCurrentWindows and ModeExperimentalNtfsFullRootUsbClone are
		// both 2), so an unmapped name means the fallback branch - which is NavClonePortable.
		string expected = map.TryGetValue(mode, out string? n) ? n : "NavClonePortable";

		Assert.True(nav == expected,
			$"The app opens on {mode} but lights {nav} in the sidebar; its own mapping says that task is {expected}. " +
			"Startup would show one task with another highlighted.");
	}

	/// <summary>
	/// The "the user is now in control" flag must be the LAST thing start-up does.
	///
	/// It exists to stop the language box's SelectionChanged handler - which SAVES SETTINGS - from firing while
	/// start-up is still restoring them. It used to be raised one line too early, immediately before the selection
	/// was assigned, so every launch saved the theme and accent while they were still the built-in defaults; the
	/// real values were applied a few lines later. The window looked correct for that session and the file on disk
	/// did not, so a user's Light mode, accent and base colour survived exactly one restart.
	///
	/// Measured before and after the fix by writing a non-default appearance into the settings file, starting the
	/// app once and reading the file back: before, all three values came back as the defaults; after, unchanged.
	///
	/// Anything added to the end of that method lands AFTER the flag unless someone moves it, and this fails if
	/// they do.
	/// </summary>
	[Fact]
	public void TheUiCustomizationReadyFlagIsRaisedLast()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "InitializeUiCustomization").ToArray();
		Assert.True(found.Length == 1, $"Expected exactly one InitializeUiCustomization, found {found.Length}.");

		BlockSyntax body = found[0].Method.Body;
		Assert.True(body != null, "InitializeUiCustomization has no block body to inspect.");
		Assert.True(body.Statements.Count > 0, "InitializeUiCustomization is empty.");

		string last = body.Statements[body.Statements.Count - 1].ToString();
		Assert.True(last.Contains("uiCustomizationReady") && last.Contains("true"),
			"uiCustomizationReady must be raised by the LAST statement of InitializeUiCustomization: until it is up, "
			+ "the language box's SelectionChanged handler is suppressed, and that handler saves settings. Raise it "
			+ "any earlier and start-up writes the defaults over the user's saved theme and accent, which then "
			+ "survive exactly one restart.\n  The last statement is instead: " + last);
	}

	/// <summary>
	/// Changing language must not wipe the Drive tools card.
	///
	/// ApplyLanguage walks every string key, calls FindName(key) and writes the static text into whatever control
	/// has that name. Seven controls on that card — ToolHealthText, ToolDriveTitleText, ToolSerialText,
	/// ToolFirmwareText, ToolInterfaceText, ToolSizeText, ToolRecommendationDetailText — are BOTH control names and
	/// string keys, so the loop replaced the selected drive's real readings with the placeholders. Measured: health
	/// read "Good", switching language turned it into "Unknown", and it stayed Unknown, because the card only
	/// re-renders when the SELECTED DISK changes and it had not.
	///
	/// The repair is to clear that guard and re-render. This test exists because the same trap has now caught three
	/// different controls (the Pause buttons, CleanRunButton, and this card), and each was found only by a person
	/// noticing a wrong caption.
	/// </summary>
	[Fact]
	public void ChangingLanguageReRendersTheDriveToolsCard()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "ApplyLanguage").ToArray();
		Assert.True(found.Length == 1, $"Expected exactly one ApplyLanguage, found {found.Length}.");

		string source = found[0].Method.ToString();
		int loop = source.IndexOf("FindName(key)", StringComparison.Ordinal);
		Assert.True(loop >= 0, "ApplyLanguage no longer looks like the FindName loop this rule reasons about.");

		int guard = source.IndexOf("_lastOverviewDiskKey", StringComparison.Ordinal);
		int rerender = source.IndexOf("UpdateDriveToolOverview()", StringComparison.Ordinal);

		Assert.True(guard > loop && rerender > loop,
			"ApplyLanguage must clear _lastOverviewDiskKey and call UpdateDriveToolOverview() AFTER the FindName "
			+ "loop. Without it the loop leaves the Drive tools card showing \"Unknown\" for a drive whose health "
			+ "it just read, and the card will not correct itself until a different disk is selected.");
		Assert.True(rerender > guard,
			"The guard must be cleared BEFORE UpdateDriveToolOverview() is called, or the overview sees the same "
			+ "disk key as last time and returns without re-rendering anything.");
	}

	/// <summary>
	/// The version the app shows must come from the assembly, never from a literal.
	///
	/// It used to be typed into the XAML, and drifted exactly as you would expect: a 4.4.0 build told every user it
	/// was "DriveForge 4.3.3". Nothing failed - the number is just a label - which is why it survived a release, and
	/// why a bug report quoting it would have sent someone to the wrong source.
	///
	/// This is cheap to keep right: AssemblyInfo is already the single source CI checks the release tag against.
	/// </summary>
	[Fact]
	public void TheVersionOnScreenComesFromTheAssembly()
	{
		string xaml = File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml"));

		Match declared = Regex.Match(xaml, @"<TextBlock[^>]*\bName=""AboutVersionText""[^>]*>");
		Assert.True(declared.Success, "AboutVersionText is gone from MainWindow.xaml - update this test with it.");

		Match literal = Regex.Match(declared.Value, @"Text=""([^""]*)""");
		if (literal.Success)
			Assert.False(Regex.IsMatch(literal.Groups[1].Value, @"\d+\.\d+"),
				"The About line has a version number written into the XAML (" + literal.Groups[1].Value + "). It will "
				+ "be wrong the next time a release goes out, and nothing will fail to say so. Assign it from "
				+ "AppVersionString() instead.");

		string ui = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "UiCustomization.cs"));
		string mw = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		Assert.True(Regex.IsMatch(ui + mw, @"AboutVersionText\.Text\s*=[^;]*AppVersionString\(\)"),
			"Nothing assigns AboutVersionText from AppVersionString(), so the About line shows whatever the XAML "
			+ "happens to say.");
	}

	/// <summary>
	/// Applying a dark base colour must not leave the app in Light mode.
	///
	/// The base presets repaint the window and panels only; the TEXT colour belongs to the light/dark mode. Clicking
	/// one while in Light mode therefore painted near-black panels under Light mode's near-black text — measured at
	/// 1.01:1 contrast — and "Reset to default" set the panels to #0F172A, the exact colour Light mode uses for
	/// text: 1.00:1, a window still running and completely invisible, recoverable only by restarting.
	///
	/// Start-up has always refused to restore a dark base in Light mode for this reason. The click handlers did not,
	/// and nothing noticed, because colour contrast is not something the compiler or any other test here can see.
	/// </summary>
	[Fact]
	public void ApplyingADarkBaseNeverLeavesTheAppInLightMode()
	{
		string[] handlers = { "BaseTheme_Click", "ResetTheme_Click" };
		List<string> problems = new List<string>();

		foreach (string name in handlers)
		{
			var found = SourceModel.Methods().Where(m => m.Name == name).ToArray();
			if (found.Length != 1) { problems.Add($"{name}: expected one definition, found {found.Length}."); continue; }

			string src = found[0].Method.ToString();
			// The CALL, not the word: the comment explaining this guard names ApplyBaseTheme too, and matching that
			// put "apply" before "guard" and failed on correct code.
			if (!src.Contains("ApplyBaseTheme(", StringComparison.Ordinal)) continue;   // no longer applies a base

			int guard = src.IndexOf("ApplyAppTheme(", StringComparison.Ordinal);
			int apply = src.IndexOf("ApplyBaseTheme(", StringComparison.Ordinal);
			if (guard < 0 || guard > apply)
				problems.Add($"{name} applies a dark base without first settling the light/dark mode "
					+ "(call ApplyAppTheme before ApplyBaseTheme).");
		}

		Assert.True(problems.Count == 0,
			"A dark base colour can be applied while Light mode is still on, which paints the window's panels in "
			+ "the same colour as its text:\n  " + string.Join("\n  ", problems));
	}

	/// <summary>
	/// The result lines on Clean and Recover must be re-derived when the language changes.
	///
	/// Both are written once, when the work finishes, and then sit on screen - so they stayed in whatever language
	/// they were written in. Measured by driving the app: "About 2.0 GB can be freed." and "20 deleted files" were
	/// still English under an otherwise Romanian window, beside a Clean button that HAD followed the language,
	/// because that one already had a repair.
	///
	/// Neither is remembered as a sentence; both are recomputed from live state, which is why this only has to check
	/// that the recomputation is still wired into ApplyLanguage.
	/// </summary>
	[Fact]
	public void ChangingLanguageReRendersTheComputedResultLines()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "ApplyLanguage").ToArray();
		Assert.True(found.Length == 1, $"Expected exactly one ApplyLanguage, found {found.Length}.");

		string src = found[0].Method.ToString();
		int loop = src.IndexOf("FindName(key)", StringComparison.Ordinal);
		Assert.True(loop >= 0, "ApplyLanguage no longer looks like the FindName loop this rule reasons about.");

		// The cleaner's line is no longer formatted here. ApplyLanguage used to always rebuild ANALYZE's
		// sentence, which summed hidden categories and the -1 sentinel of unmeasured ones, and replaced a
		// finished run's "Done - freed ..." with a different claim instead of translating it. Both lines
		// are now re-derived by the method that owns each, and BOTH are required: dropping either loses a
		// line to the language it was written in.
		(string Needle, string What)[] required =
		{
			("UpdateRecoverSelectionInfo()", "the Recover result line (\"20 deleted files\")"),
			("CleanRunResultLine()", "the finished Clean run's line (\"Done - freed 1.2 GB\")"),
			("UpdateCleanAnalyzeLine(", "the Clean analysis line (\"About 2.0 GB can be freed.\")"),
		};

		List<string> missing = new List<string>();
		foreach ((string needle, string what) in required)
		{
			int at = src.IndexOf(needle, StringComparison.Ordinal);
			if (at < 0 || at < loop) missing.Add($"{what} - expected {needle} after the loop.");
		}

		Assert.True(missing.Count == 0,
			"ApplyLanguage no longer refreshes a computed result line, so it will stay in the language it was "
			+ "written in:\n  " + string.Join("\n  ", missing));
	}

	/// <summary>
	/// A just-cleaned disk must never be handed to diskpart's `convert`.
	///
	/// `convert gpt` converts an EMPTY MBR disk. After `clean` there is no partition table at all, so there is
	/// nothing to convert. Most Windows builds wave it through; build 26300 answers "The disk you specified is not
	/// MBR formatted", which aborts the whole script — a user's SSD erase stopped there and left the drive wiped
	/// and uninitialised (reported against 4.4.2).
	///
	/// `noerr` is not an escape hatch here, and that is the point of this rule. Measured: with no convert, the
	/// following `create partition` initialises the disk as MBR — so a tolerated `convert gpt` failure would hand a
	/// 4 TB SSD a 2 TB table and call it success. A GPT table must be obtained and CHECKED
	/// (EnsureDiskPartitionStyleAsync), never asked of `convert`.
	///
	/// `convert mbr` is allowed with `noerr`, and only with it: there the fallback IS the wanted layout.
	/// </summary>
	[Fact]
	public void ACleanedDiskIsNeverHandedToDiskpartConvert()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		List<string> problems = new List<string>();

		// The style is matched as gpt, mbr OR an interpolated hole like {style}. The first version of this rule
		// looked for literal styles only, and three live flows wrote `convert {style}` - which is `convert gpt`
		// every time the user picks GPT. They passed this test while carrying the exact defect it exists to stop.
		// Restricting the alternation to styles and holes is also what keeps qemu-img's `convert -O vmdk` out.
		foreach (Match m in Regex.Matches(src, @"convert\s+(gpt|mbr|\{[A-Za-z_][A-Za-z0-9_]*\})(\s+noerr)?", RegexOptions.IgnoreCase))
		{
			string style = m.Groups[1].Value.ToLowerInvariant();
			bool noerr = m.Groups[2].Success;

			// Only inside a diskpart script: the word also appears in prose above these builders.
			int lineStart = src.LastIndexOf('\n', m.Index) + 1;
			string line = src.Substring(lineStart, src.IndexOf('\n', m.Index) - lineStart);
			// Skip C# comments AND diskpart `rem` lines: one script carries a rem that spells out why it does NOT
			// convert, and matching prose about the rule is not the same as breaking it.
			string trimmed = line.TrimStart().TrimStart('"');
			if (trimmed.StartsWith("//") || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)) continue;

			if (style.StartsWith("{"))
				problems.Add($"`{m.Value.Trim()}` at offset {m.Index}: the style is decided at runtime, so this is "
					+ "`convert gpt` for every caller that picks GPT. Obtain the table with EnsureDiskPartitionStyleAsync "
					+ "and check the result, rather than asking convert for it.");
			else if (style == "gpt")
				problems.Add($"`{m.Value.Trim()}` at offset {m.Index}: a GPT table must be obtained and verified "
					+ "(EnsureDiskPartitionStyleAsync), not asked of convert — on a cleaned disk convert has nothing "
					+ "to convert, and tolerating the failure silently yields MBR.");
			else if (!noerr)
				problems.Add($"`{m.Value.Trim()}` at offset {m.Index}: needs `noerr`, or it aborts the whole script "
					+ "on a build where convert refuses a just-cleaned disk.");
		}

		Assert.True(problems.Count == 0,
			"diskpart is being asked to convert a disk that may have no partition table:\n  "
			+ string.Join("\n  ", problems));
	}

	/// <summary>
	/// RULE - building one drive does not rewrite the HOST machine's disk policy.
	///
	/// SanPolicy (HKLM\...\Services\partmgr\Parameters) decides what Windows does with a disk it has just
	/// discovered. This app has a strong opinion about it and writes it into every image it builds: 4,
	/// "keep host disks offline", for a portable Windows, so the stick cannot auto-mount the internal disks
	/// of whatever PC it is plugged into; 1 for a normal internal install. It re-asserts the 4 in
	/// unattend.xml at every specialize pass and reports it to the person as a feature.
	///
	/// And the first line of BuildRealNtfsUsbLayoutDiskpartScript was `san policy=OnlineAll` - the same
	/// setting, value 1, written to the RUNNING machine, persistently, by three flows (the experimental
	/// full-root clone and two restore paths). Two things made that worse than untidy:
	///
	///   - it never did the job it looked like it was doing. The SAN policy governs disks discovered
	///     LATER; it does not bring the already-selected disk online. The script's own `assign letter=`
	///     lines were doing that work.
	///   - measured on the machine this was found on, which boots Windows from a USB SSD: SanPolicy=4 and
	///     the internal NVMe reported IsOffline=True, IsReadOnly=True. Building a stick would have set it
	///     to 1, and that portable Windows would then have auto-mounted the next PC's internal disks
	///     read-write - the exact hazard its own SanPolicy=4 exists to prevent, undone by the app that
	///     wrote it.
	///
	/// So: no diskpart script in this app sets the SAN policy, every SanPolicy write goes into a hive
	/// mounted from the TARGET (or into the target's unattend.xml), and the layout script brings its own
	/// disk online explicitly instead - which is what the other six places in the file already do.
	/// </summary>
	[Fact]
	public void BuildingOneDriveDoesNotRewriteTheHostsDiskPolicy()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		var problems = new List<string>();
		
		// Same comment/`rem` stripping as the convert rule above: this file carries prose that explains why
		// the policy must NOT be used, and naming the rule is not the same as breaking it.
		static bool IsProse(string src, int index)
		{
			int lineStart = src.LastIndexOf('\n', index) + 1;
			int lineEnd = src.IndexOf('\n', index);
			string line = src.Substring(lineStart, (lineEnd < 0 ? src.Length : lineEnd) - lineStart);
			string trimmed = line.TrimStart().TrimStart('"');
			return trimmed.StartsWith("//") || trimmed.StartsWith("///")
				|| trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase);
		}
		
		// --- no diskpart script sets the machine's SAN policy ---------------------------------------
		foreach (Match m in Regex.Matches(src, @"san\s+policy\s*=", RegexOptions.IgnoreCase))
		{
			if (IsProse(src, m.Index)) continue;
			problems.Add($"`{m.Value.Trim()}` at offset {m.Index}: this writes the PERSISTENT machine-wide "
				+ "SanPolicy of the host, which outlives the run and the reboot, and it does not bring the "
				+ "selected disk online either - the policy only governs disks discovered later. Use `online "
				+ "disk noerr` on the selected disk, as the rest of this file does.");
		}
		
		// --- every SanPolicy WRITE targets the TARGET, never the running machine ---------------------
		// A write, not a mention: this file also names SanPolicy=4 in a log line and in a report, and the
		// first version of this rule flagged both. The two forms that actually set it are a `reg.exe add
		// ... /v SanPolicy` and the <SanPolicy> element of the answer file; a read for the report is a third
		// form and is nobody's problem.
		int writes = 0;
		foreach (Match m in Regex.Matches(src, @"/v\s+SanPolicy"))
		{
			if (IsProse(src, m.Index)) continue;
			int lineStart = src.LastIndexOf('\n', m.Index) + 1;
			int lineEnd = src.IndexOf('\n', m.Index);
			string line = src.Substring(lineStart, (lineEnd < 0 ? src.Length : lineEnd) - lineStart).Trim();
			// The path has to come from a hive mounted off the target. Written against a live HKLM path it
			// changes what the HOST does with every disk it meets from then on.
			if (line.Contains("hiveRoot", StringComparison.Ordinal)) { writes++; continue; }
			problems.Add($"`{Trim120(line)}` at offset {m.Index}: a SanPolicy write must address a hive "
				+ "mounted from the target. Written against the live registry it changes what the HOST does "
				+ "with every disk it meets from then on.");
		}
		writes += Regex.Matches(src, @"<SanPolicy>\d+</SanPolicy>").Count;   // the target's answer file
		Assert.True(writes >= 4,
			$"Only {writes} SanPolicy write(s) into a target; there were four - the two hive branches "
			+ "(portable 4, internal install 1), the WinToGo re-assert, and the unattend.xml element. "
			+ "Re-read this rule rather than leaving it passing over a scope that has moved.");
		
		// --- and the layout script onlines its own disk, so dropping the policy loses nothing --------
		var layout = SourceModel.Methods()
			.Where(m => m.Name == "BuildRealNtfsUsbLayoutDiskpartScript").ToArray();
		Assert.True(layout.Length == 1,
			$"expected one BuildRealNtfsUsbLayoutDiskpartScript, found {layout.Length}");
		string layoutText = layout[0].Method.ToString();
		Assert.True(Regex.IsMatch(layoutText, @"""online disk noerr"""),
			"The layout script no longer brings its selected disk online. That is the per-disk replacement "
			+ "for the machine-wide SAN policy this rule forbids; without it, a target left offline by an "
			+ "OfflineAll host silently fails every line after `select`.");
		
		Assert.True(problems.Count == 0,
			"Something in this app writes the host machine's disk policy:\n  " + string.Join("\n  ", problems));
	}

	/// <summary>
	/// RULE - a Stop is not a failure, on every surface that reports how an operation ended.
	///
	/// Measured on hardware: Stop pressed 75 seconds into a real Windows-To-Go write. Three of the four
	/// surfaces told the truth - the confirmation ("Stop the current operation? The USB may be unfinished
	/// and need to be formatted again."), the dialog after it ("Stopped before the copy finished, so what
	/// is on the target is incomplete."), and the status line ("Stopped."). The fourth, the verdict line
	/// under the progress bar, read `Elapsed: 00:01:36 | Failed` - and it is the only one of the four that
	/// STAYS on screen once the dialog is dismissed. It also survives a Refresh, which this file already
	/// made sure of for the opposite reason.
	///
	/// Both sites that write it carry a comment saying a user Stop is the most common way to reach them
	/// ("the most common way to reach cloneFailed IS the user's Stop"), and then print the word "Failed".
	/// The knowledge was already written down next to the line that contradicted it. ProgStopped now
	/// exists in all seventeen tables, composed from each language's own ProgFailed prefix and its own
	/// SxStopped word - no invented translations.
	///
	/// The same bool was answering two questions in NotifyOperationDone: which sound to play, and whether
	/// a task finished well enough to ask the person for support. So a Stop in the two write flows played
	/// the Windows ERROR sound (SoundOnFinishCheck is ticked out of the factory), while five flows that
	/// passed !stopRequested played the SUCCESS chime for a cancelled task and then asked for a donation.
	/// Both wrong, in opposite directions, from one missing third state - now read in one place, so all
	/// thirty call sites are honest at once.
	///
	/// SaveLogToDesktop already learned this (it refuses to drop an 850 KB file on the Desktop for a Stop
	/// nobody asked a log for) and is checked here so the three halves of one claim stay together.
	/// </summary>
	[Fact]
	public void AStopIsNotAFailure()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		var problems = new List<string>();
		
		// --- the verdict line -----------------------------------------------------------------------
		// Every write of the failure verdict must choose its word from stopRequested. Matched on the
		// statement, not the file, so a second site added later without the choice is caught.
		int verdicts = 0;
		foreach (Match m in Regex.Matches(src, @"ProgressStatsText\.Text = string\.Format\(L\(([^)]*)\)"))
		{
			string chosen = m.Groups[1].Value;
			if (!chosen.Contains("ProgFailed", StringComparison.Ordinal)) continue;
			verdicts++;
			if (chosen.Contains("stopRequested", StringComparison.Ordinal)
				&& chosen.Contains("ProgStopped", StringComparison.Ordinal)) continue;
			problems.Add($"`{Trim120(m.Value)}` at offset {m.Index}: this prints \"Failed\" for a Stop the "
				+ "person pressed on purpose, and it is the one surface that stays on screen after the dialog "
				+ "is dismissed. Choose the word from stopRequested, as the status line next to it does.");
		}
		Assert.True(verdicts >= 2,
			$"Only {verdicts} verdict-line write(s); there were two (the clone and the image install). "
			+ "Re-read this rule rather than leaving it passing over a scope that has moved.");
		
		// --- the sound and the request for support --------------------------------------------------
		var notify = SourceModel.Methods().Where(m => m.Name == "NotifyOperationDone").ToArray();
		Assert.True(notify.Length == 1, $"expected one NotifyOperationDone, found {notify.Length}");
		string body = notify[0].Method.ToString();
		Assert.True(Regex.IsMatch(body, @"bool\s+stopped\s*=\s*stopRequested"),
			"NotifyOperationDone no longer reads stopRequested. Its bool answers two questions - which "
			+ "sound, and whether to ask for support - and a Stop is neither ending, so without this the "
			+ "error sound plays for a button the person pressed on purpose.");
		// The guard can sit on the statement itself or on the `if` just above it, and the comment that
		// explains why the support prompt exists names it too - so: the first line that is not a comment,
		// read together with the one before it.
		string[] bodyLines = body.Split('\n');
		foreach (var (what, needle) in new[] { ("the sound", ".Play()"), ("the support prompt", "MaybeOfferDonation(") })
		{
			int at = -1;
			for (int i = 0; i < bodyLines.Length; i++)
			{
				if (bodyLines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
				if (!bodyLines[i].Contains(needle, StringComparison.Ordinal)) continue;
				at = i;
				break;
			}
			Assert.True(at >= 0, $"NotifyOperationDone no longer contains {needle} outside a comment.");
			string pair = (at > 0 ? bodyLines[at - 1] : "") + " " + bodyLines[at];
			if (!pair.Contains("!stopped", StringComparison.Ordinal))
				problems.Add($"{what} is not held back on a Stop: {Trim120(bodyLines[at].Trim())}");
		}
		
		// --- and the log file nobody asked for ------------------------------------------------------
		var saver = SourceModel.Methods().Where(m => m.Name == "SaveLogToDesktop").ToArray();
		Assert.True(saver.Length == 1, $"expected one SaveLogToDesktop, found {saver.Length}");
		Assert.True(Regex.IsMatch(saver[0].Method.ToString(), @"!userAsked\s*&&\s*stopRequested"),
			"SaveLogToDesktop lost its Stop guard. Nine of its ten callers are catch blocks, which a Stop "
			+ "reaches by design, so without the guard every Stop drops a log file on the Desktop with "
			+ "nothing on screen to explain it.");
		
		// --- the word exists to choose ---------------------------------------------------------------
		var en = ((Dictionary<string, Dictionary<string, string>>)Mw.Field("Strings"))["en"];
		Assert.True(en.ContainsKey("ProgStopped") && en["ProgStopped"].Contains("{0}", StringComparison.Ordinal),
			"ProgStopped is gone from the English table, so there is no honest word for the verdict line to "
			+ "use. LocalizationTests covers the other sixteen.");
		
		Assert.True(problems.Count == 0,
			"Something tells the person their own Stop was a failure:\n  " + string.Join("\n  ", problems));
	}

	/// <summary>
	/// RULE - a chooser does not offer an option the run will silently replace.
	///
	/// The export's size chooser offered Auto / 128 / 256 / 512 / 1024 GB, and the core then computed
	/// Math.Max(the choice, EstimateExportVhdxMaximumMb()) - so on any machine with more than ~88 GB of
	/// data, "128 GB" was a button that did nothing it said. Measured on the machine this was found on:
	/// 442.6 GB of data, "128 GB" chosen, and Get-Disk reported the created virtual disk at 483.5 GB.
	/// The only record of the substitution was a log line - on the diagnostics page, inside a collapsed
	/// expander, which is not where someone is looking while they pick a size.
	///
	/// So the list is built from the same estimate the core enforces, and the impossible entries are not
	/// offered. Checked by POSITION: the estimate must be read before the chooser is shown, and no fixed
	/// size may be passed to it as a literal - a literal cannot have been filtered.
	/// </summary>
	[Fact]
	public void AChooserDoesNotOfferWhatTheRunWillReplace()
	{
		var core = SourceModel.Methods().Where(m => m.Name == "ExportBootableVhdxCoreAsync").ToArray();
		Assert.True(core.Length == 1, $"expected one ExportBootableVhdxCoreAsync, found {core.Length}");
		Assert.True(Regex.IsMatch(core[0].Method.ToString(),
			@"Math\.Max\(\s*maxMbOverride\s*,\s*EstimateExportVhdxMaximumMb\(\)\s*\)"),
			"The core no longer raises the chosen size to its own estimate. That raise is the reason this "
			+ "rule exists; if it is gone, re-read the rule rather than leaving it passing over a scope that "
			+ "has moved.");
		
		var click = SourceModel.Methods().Where(m => m.Name == "ExportVhdx_Click").ToArray();
		Assert.True(click.Length == 1, $"expected one ExportVhdx_Click, found {click.Length}");
		var chooser = click[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => i.Expression.ToString().EndsWith("ShowChooserDialog", StringComparison.Ordinal)).ToArray();
		Assert.True(chooser.Length == 1,
			$"ExportVhdx_Click should show exactly one chooser; found {chooser.Length}.");
		
		// The estimate has to be read BEFORE the list is shown, or the list cannot have been filtered by it.
		var estimates = click[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => i.Expression.ToString().EndsWith("EstimateExportVhdxMaximumMb", StringComparison.Ordinal))
			.Where(i => i.SpanStart < chooser[0].SpanStart).ToArray();
		Assert.True(estimates.Length >= 1,
			"ExportVhdx_Click shows the size chooser without reading EstimateExportVhdxMaximumMb first, so "
			+ "it is offering sizes the core will raise. Build the list from the estimate.");
		
		// ...and no fixed size is handed to the chooser as a literal: a literal was never filtered.
		var literals = chooser[0].ArgumentList.DescendantNodes().OfType<LiteralExpressionSyntax>()
			.Where(l => Regex.IsMatch(l.Token.ValueText ?? "", @"^\s*\d+\s*(GB|MB|TB)\s*$", RegexOptions.IgnoreCase))
			.Select(l => l.Token.ValueText).ToArray();
		Assert.True(literals.Length == 0,
			"These sizes are offered as literals, so nothing can have checked whether the run would honour "
			+ "them:\n  " + string.Join("\n  ", literals));
	}

	/// <summary>
	/// RULE - how much room an image needs is one question with one answer, and the answer is not in a
	/// unit that excludes the drive people buy.
	///
	/// Met on hardware: a 60 GiB target refused with "The selected drive is too small for this. Needed:
	/// 64.0 GB / Selected drive: 60.0 GB". The refusal itself was exemplary - it ran before anything was
	/// touched and said so - but the number was wrong twice over.
	///
	/// Wrong as arithmetic: this app's "GB" is GiB, so 64 GB means 68.7e9 bytes, while a nominal 64 GB
	/// stick holds 64.0e9 = 59.6 GiB. Every 64 GB drive on the market was refused outright for a
	/// Windows-To-Go build - the commonest size bought for exactly this.
	///
	/// Wrong as a fact: measured the same afternoon, Windows 11 Pro 26H2 applied with Compact occupies
	/// 13.57 GB on the stick. 64 GiB is Microsoft's figure for system storage on a normal install.
	///
	/// And the flow already held a second, better answer to the same question - max(40 GiB, image * 3 +
	/// 8 GiB), with its reason written beside it - used a few lines later to size the Windows partition
	/// when a data partition is asked for. Two answers, 24 GiB apart, in one run. There is one now.
	/// </summary>
	[Fact]
	public void HowMuchRoomAnImageNeedsHasOneAnswer()
	{
		var est = SourceModel.Methods().Where(m => m.Name == "EstimateImagePayloadBytes").ToArray();
		Assert.True(est.Length == 1,
			$"expected one EstimateImagePayloadBytes - the single answer this rule is about - found {est.Length}.");
		
		// The floor has its own name and its own owner: it answers "how much room does an installed
		// Windows need to RUN", which is a different question from "how much does this image need to
		// land". Mixing them under one name is how the gate ended up quoting an install-media figure.
		var floorOwner = SourceModel.Methods().Where(m => m.Name == "MinimumWindowsTargetBytes").ToArray();
		Assert.True(floorOwner.Length == 1,
			$"expected one MinimumWindowsTargetBytes, found {floorOwner.Length}.");
		
		// Everyone who asks either question asks one of those two. All three of these answered it
		// themselves, and the third was found by this rule rather than by reading the flow.
		foreach (string asker in new[] { "EstimateRequiredBytes", "CreateWindowsToGoFromImageAsync",
				"RunExperimentalFullRootUsbCloneAsync" })
		{
			var m = SourceModel.Methods().Where(x => x.Name == asker).ToArray();
			Assert.True(m.Length == 1, $"expected one {asker}, found {m.Length}");
			// An APEL, not a text match: Method.ToString() carries the comments too, and the comment beside
			// each of these call sites names the helper - so a text check passed on prose while the call had
			// been replaced by the arithmetic it exists to own. Found by mutation.
			var asked = m[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
				.Select(i => i.Expression.ToString())
				.Where(n => n.EndsWith("EstimateImagePayloadBytes", StringComparison.Ordinal)
					|| n.EndsWith("MinimumWindowsTargetBytes", StringComparison.Ordinal))
				.ToArray();
			Assert.True(asked.Length >= 1,
				$"{asker} works out how much room a Windows needs on its own again. That is how the gate came "
					+ "to demand 64 GiB while the partition sizer asked for 40 GiB in the same run.");
		}
		
		// No capacity requirement is a flat 64 GiB any more: in GiB that is 68.7e9 bytes, which no nominal
		// 64 GB drive can satisfy. Matched on the literal shape this file writes GiB with.
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		var flats = new List<string>();
		foreach (Match m in Regex.Matches(src, @"64L?\s*\*\s*1024L?\s*\*\s*1024L?\s*\*\s*1024L?"))
		{
			int lineStart = src.LastIndexOf('\n', m.Index) + 1;
			int lineEnd = src.IndexOf('\n', m.Index);
			string line = src.Substring(lineStart, (lineEnd < 0 ? src.Length : lineEnd) - lineStart);
			if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;   // prose about the rule
			flats.Add($"offset {m.Index}: {Trim120(line.Trim())}");
		}
		Assert.True(flats.Count == 0,
			"A capacity requirement is written as a flat 64 GiB. A nominal 64 GB drive is 59.6 GiB, so this "
			+ "refuses the drive size people buy for this job, and the number it prints is not what the image "
			+ "writes (measured: 13.57 GB):\n  " + string.Join("\n  ", flats));
	}

	/// <summary>The SSD erase must obtain its GPT table explicitly, and stop if it does not get it.</summary>
	[Fact]
	public void TheSsdEraseVerifiesItGotAGptTable()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "SsdSecureEraseFlow").ToArray();
		Assert.True(found.Length == 1, $"Expected one SsdSecureEraseFlow, found {found.Length}.");

		string src = found[0].Method.ToString();
		Assert.True(src.Contains("EnsureDiskPartitionStyleAsync", StringComparison.Ordinal),
			"SsdSecureEraseFlow no longer obtains the partition style explicitly; a cleaned disk would be left "
			+ "uninitialised, or silently formatted as MBR.");
		Assert.True(Regex.IsMatch(src, @"if\s*\(\s*!\s*await\s+EnsureDiskPartitionStyleAsync"),
			"The result of EnsureDiskPartitionStyleAsync is not checked — the erase would carry on and build a "
			+ "volume on whatever table it happened to get.");
	}

	/// <summary>
	/// Every flow that asks for a partition style must look at the answer.
	///
	/// EnsureDiskPartitionStyleAsync returns bool because Windows can refuse. A discarded bool is the same
	/// shape of mistake as diskpart's `noerr`: the flow carries on and builds on whatever table it happened
	/// to get, which on an uninitialised disk is MBR - so a 4 TB drive comes back as 2 TB, reported as a
	/// success. The sibling rule above pins the SSD erase by name; this one covers every caller, including
	/// the ones added later.
	/// </summary>
	[Fact]
	public void EveryPartitionStyleRequestIsChecked()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		MatchCollection calls = Regex.Matches(src, @"await\s+EnsureDiskPartitionStyleAsync\s*\(");
		Assert.True(calls.Count >= 6,
			$"Only {calls.Count} flows obtain the partition style explicitly. Every flow that cleans a disk and "
			+ "then builds on it needs to, so this is either a removed call or a new flow that skipped it.");

		List<string> unguarded = new List<string>();
		foreach (Match m in calls)
		{
			// The only acceptable shape is `if (!await EnsureDiskPartitionStyleAsync(...))`.
			int from = Math.Max(0, m.Index - 16);
			string before = src.Substring(from, m.Index - from);
			if (!Regex.IsMatch(before, @"if\s*\(\s*!\s*$"))
				unguarded.Add($"call at offset {m.Index}");
		}

		Assert.True(unguarded.Count == 0,
			"EnsureDiskPartitionStyleAsync is called without checking what it returns, so the flow would carry "
			+ "on with the wrong partition table:\n  " + string.Join("\n  ", unguarded));
	}

	/// <summary>
	/// The erase must ASK the device whether it accepts TRIM, not infer it from the bus.
	///
	/// This decides which of two sentences the user reads: that the old data was discarded, or that it may
	/// still be physically present. Measured on a USB-bridged SSD, the bus rule was wrong - a ReTrim had
	/// discarded 236 of 238 GB while the message said it might all still be there. Measured again on a
	/// spinning disk in a USB enclosure, the device correctly answers no, and on a cheap flash drive it does
	/// not answer at all - which is why the unanswered case must fall back to the old conservative guess
	/// rather than become a claim.
	/// </summary>
	[Fact]
	public void TheEraseAsksTheDeviceBeforeClaimingTheDataIsGone()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "SsdSecureEraseFlow").ToArray();
		Assert.True(found.Length == 1, $"Expected one SsdSecureEraseFlow, found {found.Length}.");
		string src = found[0].Method.ToString();

		Assert.True(src.Contains("DeviceReportsTrimSupport", StringComparison.Ordinal),
			"The erase no longer asks the device whether it accepts TRIM, so the claim about the old data is "
			+ "back to being inferred from the model name and the bus - which was measured wrong in both "
			+ "directions.");

		// The unanswered case must stay conservative: `??` keeps the old guess, it does not claim anything.
		Assert.True(Regex.IsMatch(src, @"deviceTrim\s*\?\?"),
			"The device answer is used without a fallback for the case where the device will not answer. A "
			+ "cheap flash drive returns no answer at all, and no answer must never read as 'the data is gone'.");
	}

	/// <summary>
	/// Only one place in the app may empty a disk.
	///
	/// `clean` is the most destructive line DriveForge writes, and measured on a plain 4 GB USB flash drive
	/// it is REFUSED about four times in five while the stick has a mounted volume - after the partition
	/// table is already gone. A canary file written before a refused clean was unreadable after it, with
	/// zero partitions left on the disk. So every flow that issued its own clean had the same failure
	/// available to it: report a hex error code and walk away from a drive it had just emptied. Seven
	/// flows did, two of them with the clean inside a longer diskpart script, where the refusal also took
	/// every line after it down - so the stick was left blank with nothing created on it.
	///
	/// The rule is structural rather than a count: the clean lives in CleanDiskAsync, which checks the
	/// disk and escalates, and nowhere else. A flow written later cannot bring the defect back by copying
	/// a line, which is exactly how it spread to seven.
	/// </summary>
	[Fact]
	public void OnlyOnePlaceEmptiesADisk()
	{
		List<string> offenders = new List<string>();
		foreach (var (file, method, name) in SourceModel.Methods())
		{
			string body = method.ToString();
			bool inScript = Regex.IsMatch(body, @"\\r\\nclean\\r\\n");   // a clean inside a diskpart script literal
			bool asLine = Regex.IsMatch(body, @"""clean""\s*,");       // ...or one line of a script built as a list
			if ((inScript || asLine) && name != "CleanDiskAsync")
				offenders.Add($"{name} ({Path.GetFileName(file)})");
		}

		Assert.True(offenders.Count == 0,
			"These methods issue their own diskpart `clean`. A refused clean empties the disk and THEN fails, so "
			+ "each of them can leave a user with a blank drive and an error code. Go through CleanDiskAsync:\n  "
			+ string.Join("\n  ", offenders.Distinct()));
	}

	/// <summary>
	/// CleanDiskAsync must CHECK the disk after every attempt, not read diskpart's exit code.
	///
	/// Both halves were measured, and they fail in opposite directions. diskpart answered
	/// ERROR_ACCESS_DENIED on a disk it had in fact emptied, so a failure is no evidence that the data
	/// survived; and diskpart exits 0 having declined to do anything, so a success is no evidence either.
	/// The partition count read back afterwards is the only evidence there is - once per attempt, or an
	/// attempt is being taken on trust.
	/// </summary>
	[Fact]
	public void TheCleanIsCheckedAfterEveryAttempt()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "CleanDiskAsync").ToArray();
		Assert.True(found.Length == 1, $"Expected one CleanDiskAsync, found {found.Length}.");
		// Code only, never the prose: this method's own documentation names each step, and the first version
		// of this rule counted its own words as attempts. The `convert` rule next door skips comments too.
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		int cleans = Regex.Matches(code, @"RunDiskpartAsync\(script\)").Count;
		bool clearDisk = code.Contains("Clear-Disk -Number", StringComparison.Ordinal);
		int attempts = cleans + (clearDisk ? 1 : 0);
		int checks = Regex.Matches(code, @"PartitionCountAsync\s*\(").Count;

		Assert.True(cleans >= 2 && clearDisk,
			$"CleanDiskAsync runs {cleans} clean(s) and {(clearDisk ? "does" : "does not")} try Clear-Disk. The measured way through a refused clean is Clear-Disk, then a second clean; one attempt "
			+ "is the behaviour that left the drive empty and the job undone.");
		Assert.True(checks >= attempts,
			$"{attempts} attempts but only {checks} readings of the partition count: at least one attempt is being "
			+ "believed on its exit code alone. diskpart has answered ACCESS_DENIED on a disk it had emptied, and "
			+ "exits 0 having declined to act, so the count read back afterwards is the only evidence there is.");

		Assert.True(code.Contains("ErrCleanDisk", StringComparison.Ordinal),
			"A clean that could not be completed has to say so in the user's own language, and say that nothing "
			+ "new was created, instead of surfacing a hex error code.");
	}

	/// <summary>Every flow that empties a disk goes through the checked clean. There are eleven.</summary>
	[Fact]
	public void EveryFlowThatEmptiesADiskUsesTheCheckedClean()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		int calls = Regex.Matches(src, @"await\s+CleanDiskAsync\s*\(").Count;
		Assert.True(calls >= 11,
			$"Only {calls} flows empty a disk through CleanDiskAsync, and there were eleven: the SSD erase, Initialize, Convert, Quick partition, Format, the overwrite Wipe, writing an ISO, and the four "
			+ "USB-layout scripts. A missing call is either a flow that is no longer destructive or one that empties a disk some other way - find out which.");
	}

	/// <summary>
	/// A disk that has just been emptied must still get the partition table it was asked for.
	///
	/// An emptied disk does not have to read RAW. Measured on a plain USB flash drive: after a clean that
	/// removed every partition, Get-Disk still answered MBR with zero partitions, and the host storage
	/// cache refresh agreed - so it is the disk's answer, not a stale one. Initialize-Disk refuses such a
	/// disk ("the disk has already been initialized"), and this method gave up there and told the user
	/// Windows would not give the drive a GPT table - on a drive that was blank by then. Which is the same
	/// thing that happens to a person as the bug this release is about, one step further along.
	///
	/// Two parts are held here. Set-Disk, because it is the one call measured to write the table on an
	/// already-initialised disk. And the gate: it runs only when the disk has NO partitions, so it can
	/// never rewrite the table of a disk that still holds something. The rescan is held too - a
	/// create+format issued straight after the conversion failed with ERROR_INVALID_PARAMETER and left an
	/// unformatted volume behind.
	/// </summary>
	[Fact]
	public void AnEmptiedDiskStillGetsTheStyleItWasAskedFor()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "EnsureDiskPartitionStyleAsync").ToArray();
		Assert.True(found.Length == 1, $"Expected one EnsureDiskPartitionStyleAsync, found {found.Length}.");
		// Code only: the prose above names every call, and matching prose is not following it.
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		// The COMMAND, not the word. Matching "Set-Disk" alone passed a mutation that removed the call and
		// left only the log line that names it in its failure message - a green rule proving nothing, which
		// is the exact mistake 4.4.3 shipped behind.
		Assert.True(Regex.IsMatch(code, @"Set-Disk\s+-Number\s+\{diskNumber\}\s+-PartitionStyle"),
			"An emptied disk that reports another partition style now has no way to get the one it was asked "
			+ "for: Initialize-Disk refuses it, and the flow would stop on a drive it has already emptied.");

		Assert.True(Regex.IsMatch(code, @"PartitionCountAsync\s*\(\s*diskNumber\s*\)\s*==\s*0"),
			"The table is written without first checking that the disk has no partitions. That gate is what "
			+ "keeps this from being a way to rewrite the partition table of a disk that still holds data.");

		Assert.True(code.Contains("rescan", StringComparison.Ordinal),
			"No rescan after the table is rewritten. Measured: the create+format that follows then fails with "
			+ "ERROR_INVALID_PARAMETER and leaves an unformatted volume with a drive letter on it.");
	}

	/// <summary>
	/// A drive that will not accept a TRIM must not turn a finished erase into a failure.
	///
	/// Measured by driving the app on a plain USB flash drive, three times out of three: the disk was
	/// cleaned, given a GPT table, partitioned, formatted, mounted and was writable - and the operation
	/// then reported "SSD erase failed", because the last step, Optimize-Volume -ReTrim, answered "the
	/// volume optimization operation requested is not supported by the hardware backing the volume" and
	/// powershell exited 1.
	///
	/// A drive that cannot TRIM is not an error. It is the exact case SsdDoneNoTrim exists for - the
	/// warning that the old data may still be physically present, which steers the person to the
	/// full-overwrite Wipe. Letting the refusal throw replaced that warning with a stack trace, on the
	/// very class of drive the person who reported the original bug had.
	/// </summary>
	[Fact]
	public void ADriveThatRefusesTrimDoesNotFailTheErase()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "SsdSecureEraseFlow").ToArray();
		Assert.True(found.Length == 1, $"Expected one SsdSecureEraseFlow, found {found.Length}.");

		// The syntax tree, not the text: what matters is whether the call is INSIDE a try, and no amount of
		// line matching answers that honestly.
		InvocationExpressionSyntax retrim = found[0].Method.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.FirstOrDefault(i => i.ToString().Contains("Optimize-Volume", StringComparison.Ordinal));
		Assert.True(retrim != null,
			"The erase no longer issues a ReTrim at all, so it cannot discard anything on a drive that does "
			+ "support it - and the message would still be deciding what to claim.");

		// Not just "inside some try": the whole flow already sits in one, whose catch is what turns every
		// failure into "SSD erase failed". The refusal has to be caught NEXT TO the call, so the honest
		// message still runs afterwards - and the way to say that is that the nearest try does not also
		// contain the final message.
		TryStatementSyntax guard = retrim.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault();
		Assert.True(guard != null,
			"The ReTrim is not inside a try at all. A drive that refuses it makes powershell exit non-zero, "
			+ "which throws, which reports the whole erase as failed - on a disk that was by then cleaned, "
			+ "formatted and perfectly usable. Measured on a plain USB flash drive, three times out of three.");

		Assert.True(!guard.ToString().Contains("MessageBox.Show", StringComparison.Ordinal),
			"The nearest try around the ReTrim is the flow's own outer try - the one whose catch reports "
			+ "\"SSD erase failed\". A refused ReTrim would still take the whole operation down with it, and "
			+ "the SsdDoneNoTrim warning that exists for exactly that drive would never be shown.");
	}

	/// <summary>
	/// A partition that was created but could not be formatted must be finished, not abandoned.
	///
	/// Measured by driving the app on a plain USB flash drive, about one run in three and only ever in a
	/// run where the partition style had to be rewritten first: `create partition primary` succeeds and
	/// the `format` on the next line answers "There is no volume selected". The volume for that partition
	/// exists 0.2 s later. What the person is left holding is a drive with a volume that has no file
	/// system, a drive letter on it, and a hex code.
	///
	/// Two things are held. That the repair WAITS for the volume rather than sleeping for a guessed
	/// interval - a tuned sleep is a guess dressed as a fix. And that it never addresses the partition as
	/// number 1: on a disk initialised as GPT, partition 1 is the 15 MB Microsoft Reserved partition, so
	/// a hardcoded 1 would format the wrong thing on every disk that has one.
	/// </summary>
	[Fact]
	public void ACreatedPartitionIsNotLeftUnformatted()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "CreateAndFormatAsync").ToArray();
		Assert.True(found.Length == 1, $"Expected one CreateAndFormatAsync, found {found.Length}.");
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		Assert.True(code.Contains("WaitForFormattablePartitionAsync", StringComparison.Ordinal),
			"The repair no longer waits for the volume to exist. The only thing that was missing when the "
			+ "format failed was the volume, and a sleep tuned to todays hardware is a guess, not a fix.");

		Assert.True(!Regex.IsMatch(code, @"select partition 1\b"),
			"The repair addresses the partition as number 1. On a disk initialised as GPT that is the 15 MB "
			+ "Microsoft Reserved partition - it would format the wrong one. Use the number Windows reports.");

		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		int callers = Regex.Matches(src, @"await\s+CreateAndFormatAsync\s*\(").Count;
		Assert.True(callers >= 3,
			$"Only {callers} flows lay out a single partition through the checked step; there were three: the "
			+ "SSD erase, Initialize, and Format.");
	}

	/// <summary>
	/// The wipe is confirmed AFTER the method is chosen, never before.
	///
	/// The other way round, the confirmation promises something the next step can take back. It read
	/// "Every sector will be OVERWRITTEN. Data will NOT be recoverable. This cannot be undone." - and
	/// the user was then offered the method, whose first entry is "Quick - clear partition table only
	/// (fast, recoverable)", which does neither of those things. One of the six choices made the
	/// sentence they had just agreed to false.
	///
	/// The ending had already been fixed to say which of the two actually happened, and the comment
	/// there spells the problem out - but the promise itself was left in place, and the promise is the
	/// half the person is asked to agree to. Choosing first also means cancelling at the method list
	/// costs nothing, instead of coming after a full-screen warning about unrecoverable data.
	/// </summary>
	[Fact]
	public void TheWipeIsConfirmedAfterTheMethodIsChosen()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "WipeDrive_Click").ToArray();
		Assert.True(found.Length == 1, $"Expected one WipeDrive_Click, found {found.Length}.");
		// Code only: the prose above the flow quotes both strings, in the order that was wrong.
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		int method = code.IndexOf("AmWipeMethodTitle", StringComparison.Ordinal);
		int confirm = code.IndexOf("MbWipeConfirm", StringComparison.Ordinal);
		Assert.True(method >= 0 && confirm >= 0,
			"The wipe flow no longer shows a method list or no longer confirms - one of the two is gone, and "
			+ "this rule is about the order between them.");

		Assert.True(method < confirm,
			"The drive is confirmed BEFORE the method is chosen, so the confirmation is promising something "
			+ "the next step can take back: Quick clears the partition table and overwrites nothing, after the "
			+ "person has agreed that every sector will be overwritten and nothing will be recoverable.");

		Assert.True(code.Contains("MbWipeConfirmQuick", StringComparison.Ordinal),
			"Quick and the overwrite methods share one confirmation again. They cannot: one of them writes "
			+ "over every sector and the other writes nothing at all, and the drive's owner is agreeing to "
			+ "whichever sentence is on screen.");
	}

	/// <summary>
	/// A recovery that was stopped must not read like one that finished.
	///
	/// The loop breaks out on Stop, and the files past that point are never attempted - they are not
	/// counted as recovered, partial or failed. The message then said "Recovered 3 of 50 files" and
	/// nothing more, and a person reads that as a verdict on the other 47. It is not one: they were
	/// never tested. On a dying drive that is the difference between trying again and giving up on it.
	///
	/// The deep scan one step earlier already refuses to let a stopped run look complete, and says why
	/// in its own comment - "the user reads '12 files found' as 'that's all there is' and stops
	/// looking for the file that IS there". This is the same rule for the step after it.
	/// </summary>
	[Fact]
	public void AStoppedRecoveryIsNotReportedAsAFinishedOne()
	{
		// Every flow that hands a list of files to RecoverPickedToDir, not one method by name: a third
		// recovery path added later must not be able to land without the same sentence.
		var callers = SourceModel.Methods()
			.Where(m => m.Name != "RecoverPickedToDir" && m.Method.ToString().Contains("RecoverPickedToDir(", StringComparison.Ordinal))
			.ToArray();
		Assert.True(callers.Length >= 2,
			$"Only {callers.Length} flow(s) recover picked files; there were two - into a folder and into a zip.");

		List<string> problems = new List<string>();
		foreach (var c in callers)
		{
			// Code only: the comments in these flows quote the message this rule exists to prevent.
			string code = string.Join("\n", c.Method.ToString().Split('\n')
				.Where(l => !l.TrimStart().StartsWith("//")));
			if (!code.Contains("RfRecStoppedNote", StringComparison.Ordinal))
				problems.Add($"{c.Name}: says nothing about the files a Stop never reached, so the count of "
					+ "recovered files reads as a verdict on all of them");
			else if (!Regex.IsMatch(code, @"stopped\s*&&"))
				problems.Add($"{c.Name}: the note is not tied to the run having been stopped");
			else if (!Regex.IsMatch(code, @"picked\.Count\s*-\s*\(\s*rr\.Ok"))
				problems.Add($"{c.Name}: the number of untouched files is not derived from what was actually "
					+ "attempted");
		}

		Assert.True(problems.Count == 0,
			"A stopped recovery reads like a finished one. The files past the Stop were never tested, and on "
			+ "a dying drive that is the difference between trying again and giving up on the drive:\n  "
			+ string.Join("\n  ", problems));
	}

	/// <summary>Clean traces must not report a run the person stopped as a finished one.</summary>
	[Fact]
	public void AStoppedCleanIsNotReportedAsAFinishedOne()
	{
		// CleanRun_Click records the run and CleanRunResultLine turns it into a sentence, so the wording
		// lives in one place and a language change can re-derive it. Both are read here: taking the
		// stopped branch out of either one brings the defect back.
		string[] owners = { "CleanRun_Click", "CleanRunResultLine" };
		var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string owner in owners)
		{
			var hit = SourceModel.Methods().Where(m => m.Name == owner).ToArray();
			Assert.True(hit.Length == 1, $"Expected one {owner}, found {hit.Length}.");
			bodies[owner] = string.Join("\n", hit[0].Method.ToString().Split('\n')
				.Where(l => !l.TrimStart().StartsWith("//")));
		}
		string code = bodies["CleanRun_Click"];

		Assert.True(bodies["CleanRunResultLine"].Contains("CleanStopped", StringComparison.Ordinal),
			"A stopped clean still says \"Done\", while the categories it never reached sit in the list "
			+ "still ticked, looking as though they had been dealt with.");
		Assert.True(Regex.IsMatch(code, @"stopRequested\s*\)\s*;|,\s*stopRequested\s*\)"),
			"CleanRun_Click no longer tells the line whether the run was stopped, so the stopped wording "
			+ "above can never be reached.");
		Assert.True(Regex.IsMatch(code, @"if\s*\(\s*!stopRequested\s*\)\s*ProgressBar\.Value\s*=\s*100"),
			"The bar is driven to 100% even when the run was stopped, which is the same claim in the one "
			+ "place a person looks first.");
	}

	/// <summary>
	/// Secure delete must not claim a secure deletion it did not perform.
	///
	/// On an SSD, or on a compressed / sparse / encrypted file, an overwrite lands on NEW clusters and
	/// leaves the original data where it was - so the flow correctly decides not to overwrite, and the
	/// dialog beforehand says so. The line left on screen afterwards still read "File securely
	/// deleted." The warning is read while deciding; the ending is what is remembered.
	/// </summary>
	[Fact]
	public void SecureDeleteOnlyClaimsWhatItDid()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "CleanSecureDelete_Click").ToArray();
		Assert.True(found.Length == 1, $"Expected one CleanSecureDelete_Click, found {found.Length}.");
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		Assert.True(Regex.IsMatch(code, @"L\(\s*overwrite\s*\?"),
			"The ending does not depend on whether anything was actually overwritten, so a plain delete on "
			+ "an SSD is announced as a secure erase - the exact claim the dialog above declined to make.");
	}

	/// <summary>
	/// A flow that reports its own ending while polling Stop must have something to say about a stop.
	///
	/// Three flows were found reporting a stopped run as a finished one - the clean, the recovery into
	/// a folder and the recovery into a zip - while the wipe, the free-space wipe, the shredder, the
	/// imager and the deep scan all handled it. The convention existed; these had simply missed it.
	///
	/// The list below is the honest part. The rule was dry-run over the whole app before being written,
	/// and eight further flows matched the shape. All eight have since been READ, one at a time: six of
	/// them cannot reach their ending after a Stop at all - the stop throws, or is caught, or the result
	/// is verified on the disk afterwards and fails - and the other two could, and now say so. Each name
	/// that stays carries the reason it stays. A name with no reason is a rule looking away.
	///
	/// Nothing NEW may join the list. Adding a name means doing the reading first.
	/// </summary>
	[Fact]
	public void AStoppedRunDoesNotAnnounceItselfAsFinished()
	{
		// Read 2026-10-11. Each of these reports an ending and polls Stop, and in each the ending is
		// unreachable once a Stop has happened:
		HashSet<string> stopCannotReachTheEnding = new HashSet<string>(StringComparer.Ordinal)
		{
			"RunSpeedTestAsync",                      // MeasureDiskSpeed throws OperationCanceledException
			"StartButton_Click",                      // a stop lands in the "aborted before any write" branch
			"BackupThisPcToImageAsync",               // the capture is verified, and `ok` needs the file on disk
			"RunExperimentalFullRootUsbCloneAsync",   // throws on stop; `ok` also requires !rawIncomplete
			"ExportBootableVhdxCoreAsync",            // throws on stop before the ending
			"MultiBootFlowAsync",                     // a stop kills Ventoy, then the disk is checked and throws
		};

		List<string> fresh = new List<string>();
		foreach (var m in SourceModel.Methods())
		{
			string code = string.Join("\n", m.Method.ToString().Split('\n')
				.Where(l => !l.TrimStart().StartsWith("//")));
			if (!code.Contains("stopRequested", StringComparison.Ordinal)) continue;
			if (!Regex.IsMatch(code, @"L\(""\w*(?:Done|Finished|Complete)\w*""\)")) continue;        // does not report its own ending
			if (Regex.IsMatch(code, @"L\(""\w*Stop\w*""\)")) continue;         // has something to say about a stop
			if (stopCannotReachTheEnding.Contains(m.Name)) continue;
			fresh.Add(m.Name);
		}

		Assert.True(fresh.Count == 0,
			"These flows announce an ending while polling Stop, and have no wording for a stopped run, so a "
			+ "run the person cut short reads as one that finished:\n  " + string.Join("\n  ", fresh.Distinct()));
	}

	/// <summary>
	/// The capacity test must not pronounce a drive genuine beyond what it actually wrote.
	///
	/// It can only write the FREE space, so on a drive with files on it most of the claimed capacity is
	/// never touched - and a counterfeit can sit in the untouched part. The confirmation says so
	/// ("for a complete check, run it on an empty drive"); the verdict used to retract it with "the
	/// capacity is real" whatever fraction had been covered. The last message is the one remembered,
	/// and this is the one tool whose entire purpose is catching a drive that lies about its size.
	/// </summary>
	[Fact]
	public void TheCapacityVerdictMatchesWhatWasActuallyTested()
	{
		var found = SourceModel.Methods().Where(m => m.Name == "CapacityTest_Click").ToArray();
		Assert.True(found.Length == 1, $"Expected one CapacityTest_Click, found {found.Length}.");
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		Assert.True(code.Contains("CapGenuinePartial", StringComparison.Ordinal),
			"A drive is called genuine on one verdict however little of it was reached. On a drive that is "
			+ "mostly full that is a few gigabytes of evidence standing in for the whole claimed size.");

		// A COMPARISON, not a co-occurrence. The first version of this rule matched verifiedOk and
		// disk.Size appearing on one line - which the CapGenuinePartial format call does, so replacing the
		// coverage test with `verifiedOk >= 0` left it green while every drive got the full verdict again.
		Assert.True(Regex.IsMatch(code, @"verifiedOk\s*[<>]=?[^;\r\n]*disk\.Size"),
			"What was verified is never compared against what the drive claims, so the choice between the "
			+ "two verdicts cannot be about coverage at all.");
	}

	/// <summary>
	/// An operation-time estimate says whether it measured this drive.
	///
	/// Two different numbers reach the same sentence. One is the speed test's, taken over 80 MB and then
	/// multiplied out over a whole clone, which assumes a drive holds its opening speed for hundreds of
	/// times as long. The other is a flat 60 MB/s written into the code for when no test has been run.
	/// They used to produce identical wording, so a guess and a measurement were indistinguishable in
	/// the one dialog that stands between the user and an erase.
	///
	/// The fix is a sentence, not a better number, so it is a sentence this guards: both endings exist,
	/// and the one that is used comes from whether a measurement was actually found.
	/// </summary>
	[Fact]
	public void AnEstimateSaysWhetherItMeasuredTheDrive()
	{
		string code = SourceModel.Methods().Single(m => m.Name == "EstimateOperationTime")
			.Method.ToString();

		Assert.True(code.Contains("\"EstFromBurst\"") && code.Contains("\"EstNoTest\""),
			"The estimate must have both endings - one for a figure measured over the speed test's"
				+ " 80 MB, one for the flat default - or a guess reads exactly like a measurement:\n" + code);

		// And the choice has to come from whether a measurement was found, not from anything else: a
		// `bool measured = true` keeps both keys in the file and says the wrong one every time.
		Assert.True(Regex.IsMatch(code, @"bool\s+measured\s*=[^;]*\bmbps\s*>"),
			"Which ending the estimate uses must be decided by the measured speed itself:\n" + code);
	}

	/// <summary>
	/// A verdict is never a constant.
	///
	/// Create partition and Delete volume each wrote <c>bool ok = true;</c> and then chose between a
	/// Done and a Failed message with it. PtCreateFailed exists in all seventeen languages - "Could not
	/// create a partition - there may be no unallocated space on the disk" - and could not be reached, so
	/// a Create on a disk with no unallocated space answered "Done. A new partition was created".
	/// Delete was worse: there was no PtDeleteFailed at all, and its unreachable branch pointed at
	/// PtResizeFailed, so a failed deletion would have said diskpart could not RESIZE the volume. Dead
	/// code is where a wrong message hides.
	///
	/// The comment defending the constant was half right: diskpart's output is localized, so the word
	/// "successfully" cannot be parsed in sixteen of seventeen languages. The conclusion does not
	/// follow. The Resize flow in the same screen had already answered it by measuring the volume before
	/// and after, and these now count partitions the same way.
	///
	/// What this looks for is the mechanism, not the two sites: a bool local initialised to a literal,
	/// used as the condition of a conditional whose branches look up text. Deriving the flag from
	/// something measured is what makes a verdict a verdict.
	///
	/// It does NOT check that the measurement is compared the right way round - inverting one of these
	/// (Create looking for FEWER partitions) passes, and was run to confirm it. That is deliberate: the
	/// direction is semantic, asserting it here would be copying the implementation into the test, and an
	/// inverted comparison announces itself on the first run by failing every success. A constant does the
	/// opposite - it never fails, so nobody ever sees the branch behind it.
	/// </summary>
	[Fact]
	public void AVerdictIsNeverAConstant()
	{
		var offenders = new List<string>();
		foreach (var (file, method, name) in SourceModel.Methods())
		{
			// bool locals that start life as a literal true/false.
			var constants = new HashSet<string>(StringComparer.Ordinal);
			foreach (var v in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
			{
				if ((v.Parent as VariableDeclarationSyntax)?.Type.ToString() != "bool") continue;
				if (v.Initializer?.Value is LiteralExpressionSyntax lit
					&& (lit.Token.IsKind(SyntaxKind.TrueKeyword) || lit.Token.IsKind(SyntaxKind.FalseKeyword)))
					constants.Add(v.Identifier.Text);
			}
			if (constants.Count == 0) continue;
			
			// A later assignment from something real is the flag being decided, which is the point.
			foreach (var asg in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
				if (asg.Left is IdentifierNameSyntax lid && asg.Right is not LiteralExpressionSyntax)
					constants.Remove(lid.Identifier.Text);
			
			foreach (var cond in method.DescendantNodes().OfType<ConditionalExpressionSyntax>())
			{
				if (cond.Condition is not IdentifierNameSyntax flag
					|| !constants.Contains(flag.Identifier.Text)) continue;
				// Only when the two branches are TEXT the user reads - a constant flag picking between two
				// code paths is an ordinary switch, not a verdict.
				var keys = Regex.Matches(cond.ToString(), "L\\(\\s*\"([A-Za-z0-9_]+)\"")
					.Select(m => m.Groups[1].Value).ToList();
				if (keys.Count < 2) continue;
				string between = string.Join(" and ", keys);
				offenders.Add($"{SourceModel.Where(file, cond)} in {name}: {flag.Identifier.Text}"
					+ $" is a literal, choosing between {between}");
			}
		}
		Assert.True(offenders.Count == 0,
			"A success/failure message is being chosen by a flag that cannot be false, which makes the other"
				+ " message dead code:\n  " + string.Join("\n  ", offenders));
	}

	/// <summary>
	/// A disk's new state is read back before it is announced - and read back for the thing claimed.
	///
	/// <summary>
	/// A disk's state is read back because the exit code is the only other thing there is.
	///
	/// Said carefully, because two earlier versions of this comment said it too strongly. Measured on
	/// real hardware, diskpart 10.0.26100.1150, every refusal that could be produced came back
	/// NON-zero and so threw before reaching any Done message: no usable space 0x8004240F,
	/// uninitialised disk 0x80042417, a fifth primary partition on an MBR table 0x80070057. There is
	/// no measured case on this build of diskpart declining something and reporting 0 - and the one
	/// cited for it in MainWindow.cs, `format fs=fat32` above 32 GB, turned out to SUCCEED when it
	/// was finally run.
	///
	/// What stands: the exit code is the only signal these flows get, the text that would say more is
	/// localized in sixteen of the seventeen languages, and CreateAndFormatAsync deliberately
	/// catches a diskpart failure and retries - so a flow can reach its ending having swallowed one.
	/// The Resize flow got there first and said it best: "the only locale-safe way to know whether
	/// anything actually changed is to compare the real volume size before/after".
	///
	/// So this rule is defence in depth, not the repair of a reproduced false success. Worth having
	/// at one query per destructive operation; worth describing accurately either way.
	[Fact]
	public void ADiskStateIsReadBackBeforeItIsAnnounced()
	{
		string[] drivers = { "RunDiskpartAsync", "CleanDiskAsync", "CreateAndFormatAsync" };
		
		// Each announcement, and the reading that has to stand behind it.
		var evidenceFor = new Dictionary<string, string[]>(StringComparer.Ordinal)
		{
			["PtResizeDone"] = new[] { "VolumeSizeBytesAsync" },
			["PtCreateDone"] = new[] { "PartitionCountAsync" },
			["PtDeleteDone"] = new[] { "PartitionCountAsync" },
			["PtConvertDone"] = new[] { "EnsureDiskPartitionStyleAsync" },
			["PtInitDone"] = new[] { "FileSystemsOnDiskAsync" },
			["MbFormatDone"] = new[] { "FileSystemsOnDiskAsync" },
			["PtQuickDone"] = new[] { "PartitionCountAsync", "FileSystemsOnDiskAsync" },
			["PtActiveDone"] = new[] { "PartitionIsActiveAsync" },
		};
		// Backed by a measurement of the work rather than a reading of the disk - see above.
		var measuredAsWork = new HashSet<string>(StringComparer.Ordinal)
			{ "MvDone", "SsdDone" };
		
		// The English block, read the way LocalizationTests reads it: the runtime object, not the source.
		var en = ((Dictionary<string, Dictionary<string, string>>)Mw.Field("Strings"))["en"];
		var missing = new List<string>();
		var unclassified = new List<string>();
		foreach (var (file, method, name) in SourceModel.Methods())
		{
			string code = method.ToString();
			if (name is "RunDiskpartAsync" or "CleanDiskAsync" or "CreateAndFormatAsync") continue;
			if (!drivers.Any(d => code.Contains(d + "("))) continue;
			
			var keys = Regex.Matches(code, @"L\(\s*""([A-Za-z0-9_]+)""")
				.Select(m => m.Groups[1].Value).Distinct().ToList();
			
			// Completeness: an announcement opening with "Done" has to be classified one way or the other, so a
			// new one cannot arrive unguarded.
			foreach (string key in keys)
			{
				if (!en.TryGetValue(key, out string? text)) continue;
				if (!text.TrimStart('\u2713', ' ').StartsWith("Done", StringComparison.Ordinal)) continue;
				if (evidenceFor.ContainsKey(key) || measuredAsWork.Contains(key)) continue;
				unclassified.Add($"{name} -> {key}: \"{text}\"");
			}
			
			foreach (string key in keys)
			{
				if (!evidenceFor.TryGetValue(key, out string[]? needed)) continue;
				string[] absent = needed.Where(r => !code.Contains(r + "(")).ToArray();
				if (absent.Length == 0) continue;
				missing.Add($"{SourceModel.Where(file, method)} {name}: announces {key} without "
					+ string.Join(" or ", absent));
			}
		}
		
		Assert.True(unclassified.Count == 0,
			"A diskpart flow announces something new. Say which it is - a state diskpart was asked for (add it"
				+ " to evidenceFor with the reading that proves it) or work the flow measured itself (add it to"
				+ " measuredAsWork, with the reason):\n  " + string.Join("\n  ", unclassified));
		Assert.True(missing.Count == 0,
			"diskpart's exit code does not reliably say whether it did the thing, so each of these"
				+ " announces a disk state without the reading that would show it happened:\n  "
				+ string.Join("\n  ", missing));
	}

	/// <summary>
	/// What counts as a read of a checkbox, in BOTH access forms: `X.IsChecked` is a
	/// MemberAccessExpression, `X?.IsChecked` is a ConditionalAccessExpression wrapping a
	/// MemberBindingExpression. Looking only at the first form has already cost this file one green rule
	/// over a live defect - the export and the clone were both written with the null-conditional form -
	/// and the lesson was then written down TWICE, in two rules with a copy of the test each. This is the
	/// one copy. Blinding it has to turn every rule that depends on it red.
	/// </summary>
	private static bool IsOptionRead(SyntaxNode n, out string box)
	{
		if (n is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "IsChecked")
		{ box = ma.Expression.ToString(); return true; }
		if (n is ConditionalAccessExpressionSyntax ca
			&& ca.WhenNotNull is MemberBindingExpressionSyntax mb && mb.Name.Identifier.Text == "IsChecked")
		{ box = ca.Expression.ToString(); return true; }
		box = "";
		return false;
	}

	/// <summary>Highest {N} in a format string, plus one. 0 when it has no placeholders.</summary>
	private static int HolesIn(string text)
	{
		int highest = -1;
		foreach (Match m in Regex.Matches(text, @"\{(\d+)"))
			highest = Math.Max(highest, int.Parse(m.Groups[1].Value));
		return highest + 1;
	}

	/// <summary>The English text of a key, or null when there is no such key.</summary>
	private static string? EnglishOf(string key)
	{
		var en = ((Dictionary<string, Dictionary<string, string>>)Mw.Field("Strings"))["en"];
		return en.TryGetValue(key, out string? v) ? v : null;
	}

	/// <summary>
	/// A formatted string gets exactly the arguments its placeholders need.
	///
	/// Too few and string.Format throws FormatException at the moment the message is built - which is
	/// the moment an operation is reporting its result, so the app throws instead of telling the user
	/// what happened. Nothing in the compiler sees this: the key is a string and the count lives in a
	/// dictionary. Too many is harmless to run but means the text lost a placeholder it was written for,
	/// so it is reported separately rather than ignored.
	///
	/// Only calls whose key is a plain literal can be checked; L(cond ? "A" : "B") is counted against
	/// BOTH keys, which is correct - the call has one argument list and either key may be the one used.
	/// </summary>
	[Fact]
	public void EveryFormattedKeyGetsTheArgumentsItNeeds()
	{
		var tooFew = new List<string>();
		var tooMany = new List<string>();
		foreach (var (file, root) in SourceModel.Parsed)
		{
			foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				// string.Format(...) or String.Format(...)
				if (call.Expression is not MemberAccessExpressionSyntax ma
					|| ma.Name.Identifier.Text != "Format"
					|| ma.Expression.ToString() is not ("string" or "String")) continue;
				var args = call.ArgumentList.Arguments;
				if (args.Count == 0) continue;
				
				// The first argument has to be an L(...) lookup for this to be checkable.
				if (args[0].Expression is not InvocationExpressionSyntax look
					|| (look.Expression as IdentifierNameSyntax)?.Identifier.Text != "L") continue;
				var keys = Regex.Matches(look.ToString(), "\"([A-Za-z0-9_]+)\"")
					.Select(m => m.Groups[1].Value).Distinct().ToList();
				if (keys.Count == 0) continue;
				
				int given = args.Count - 1;
				foreach (string key in keys)
				{
					string? text = EnglishOf(key);
					if (text == null) continue;   // EveryLiteralKeyReferencedInCodeExists reports a missing key
					int needed = HolesIn(text);
					if (given < needed)
						tooFew.Add($"{SourceModel.Where(file, call)} {key}: needs {needed}, given {given}");
					else if (given > needed)
						tooMany.Add($"{SourceModel.Where(file, call)} {key}: needs {needed}, given {given}");
				}
			}
		}
		Assert.True(tooFew.Count == 0,
			"string.Format would throw FormatException here - the app would fall over while reporting a"
				+ $" result:\n  " + string.Join("\n  ", tooFew));
		Assert.True(tooMany.Count == 0,
			"These pass more arguments than the text has placeholders, so something the code meant to show is"
				+ $" not being shown:\n  " + string.Join("\n  ", tooMany));
	}

	/// <summary>
	/// A key with placeholders is never shown without being formatted.
	///
	/// L("Key") on a key whose text contains {0} puts the braces on screen. It cannot throw and it cannot
	/// fail a build; it just reads as a bug to whoever sees it. The companion rule above catches the
	/// opposite mistake.
	///
	/// A bare L() whose result is handed to string.Format, or concatenated into one, is fine - that is
	/// the formatting happening one step later - so a lookup sitting inside a string.Format call anywhere
	/// up its ancestry is not counted.
	/// </summary>
	[Fact]
	public void AKeyWithPlaceholdersIsNeverShownRaw()
	{
		var raw = new List<string>();
		foreach (var (file, root) in SourceModel.Parsed)
		{
			foreach (var look in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				if ((look.Expression as IdentifierNameSyntax)?.Identifier.Text != "L") continue;
				var keys = Regex.Matches(look.ToString(), "\"([A-Za-z0-9_]+)\"")
					.Select(m => m.Groups[1].Value).Distinct().ToList();
				if (keys.Count == 0) continue;
				
				// Inside a string.Format anywhere above? Then it is being formatted.
				bool formatted = false;
				for (SyntaxNode? p = look.Parent; p != null; p = p.Parent)
				{
					if (p is InvocationExpressionSyntax inv
						&& inv.Expression is MemberAccessExpressionSyntax m2
						&& m2.Name.Identifier.Text == "Format"
						&& m2.Expression.ToString() is "string" or "String")
					{ formatted = true; break; }
				}
				if (formatted) continue;
				
				foreach (string key in keys)
				{
					string? text = EnglishOf(key);
					if (text == null || HolesIn(text) == 0) continue;
					int holes = HolesIn(text);
					raw.Add($"{SourceModel.Where(file, look)} {key}: {holes} placeholder(s), shown unformatted"
						+ $" - \"{text}\"");
				}
			}
		}
		Assert.True(raw.Count == 0,
			"These put a placeholder on screen instead of a value:\n  " + string.Join("\n  ", raw));
	}

	/// <summary>
	/// No shipped member is left with nobody calling it.
	///
	/// C# warns about an unused private field (CS0169) and says nothing at all about an unused private
	/// method, so dead code accumulates silently. One sweep found eighteen members across 225 lines,
	/// among them BuildCreateVhdxDiskpartScript - a builder of a destructive `create partition / format`
	/// script that nothing ran - and three tool-selector click handlers whose buttons had been taken out
	/// of the XAML. Removing five of them orphaned five more, so the sweep had to be repeated until a
	/// pass came up empty.
	///
	/// It matters beyond tidiness, and today is the evidence: both partition findings were unreachable
	/// branches. PtCreateFailed existed in seventeen languages and could not be reached, and the delete
	/// flow's unreachable branch pointed at PtResizeFailed - a message about RESIZING - for a failed
	/// deletion. Nobody had ever seen either, because nobody could.
	///
	/// A name mentioned anywhere else counts as used, including in a comment and including XAML
	/// (Click="Handler"), so a wired handler or a reflected helper is never flagged. That is deliberately
	/// loose: this rule should never be the reason a live member is deleted.
	/// </summary>
	[Fact]
	public void NoShippedMemberIsLeftWithNobodyCallingIt()
	{
		// Everything the project can refer to a name from.
		var blobs = new List<string>();
		foreach (string path in Directory.EnumerateFiles(Mw.RepoRoot, "*.*", SearchOption.AllDirectories))
		{
			string ext = Path.GetExtension(path);
			if (ext != ".cs" && ext != ".xaml") continue;
			string rel = Path.GetRelativePath(Mw.RepoRoot, path).Replace('\\', '/');
			if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("bin/")
				|| rel.StartsWith("obj/")) continue;
			blobs.Add(File.ReadAllText(path));
		}
		Assert.True(blobs.Count > 5,
			$"only {blobs.Count} source files found - the scan is not looking where it should.");
		
		var dead = new List<string>();
		foreach (var (file, method, name) in SourceModel.Methods())
		{
			if (name == "<anonymous>") continue;
			var mods = method.Modifiers.Select(m => m.Text).ToList();
			if (!mods.Contains("private") && !mods.Contains("internal")) continue;
			// A constructor is called by `new Type(...)`, which does not carry the member's name.
			if (method is ConstructorDeclarationSyntax) continue;
			
			int uses = blobs.Sum(b => Regex.Matches(b, @"\b" + Regex.Escape(name) + @"\b").Count);
			if (uses > 1) continue;
			dead.Add($"{SourceModel.Where(file, method)} {name}");
		}
		Assert.True(dead.Count == 0,
			"Nothing refers to these. Dead code is where a wrong thing hides unnoticed - delete them, then"
				+ " check again, because removing one orphans whatever only it called:\n  "
				+ string.Join("\n  ", dead));
	}

	/// <summary>
	/// Every tool the Start button can run is selectable from a button on screen.
	///
	/// Three were not. ToolStartButton_Click dispatches on selectedDriveTool and has always had cases
	/// for ToolSmart, ToolRepair and ToolKit - and selectedDriveTool could only become one of those
	/// three inside the very handlers that switch reaches. Circular, so unreachable: git shows
	/// MainWindow.xaml never referenced their handlers in the repo's history. Three implemented
	/// features, with code, a dispatcher case and seventeen languages of strings, and no way in. The
	/// app even said "Recommended next step: press Repair."
	///
	/// Nothing caught it. The dead-code rule could not: those handlers ARE referenced - by the
	/// dispatcher - so they counted as used. Being referenced is not being reachable, and that is the
	/// gap this closes.
	///
	/// A tool counts as selectable when some method that calls SelectDriveTool(ThatTool, ...) is wired
	/// to a Click in the XAML. One hop only - a chain of code-only callers would still be unreachable
	/// by the same argument, so one hop is the honest test.
	/// </summary>
	[Fact]
	public void EveryToolTheStartButtonRunsIsSelectableOnScreen()
	{
		string code = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		string xaml = File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml"));
		
		// The constants the dispatcher switches on.
		var tools = Regex.Matches(code, @"private const int (Tool[A-Za-z]+) = \d+;")
			.Select(m => m.Groups[1].Value).Distinct().ToList();
		Assert.True(tools.Count >= 5, $"only {tools.Count} Tool* constants found - the scan is wrong.");
		
		// Click="Handler" in the XAML.
		var wired = Regex.Matches(xaml, @"Click=""([A-Za-z0-9_]+)""")
			.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
		
		var unreachable = new List<string>();
		foreach (string tool in tools)
		{
			// Methods whose body selects this tool.
			var setters = SourceModel.Methods()
				.Where(m => Regex.IsMatch(m.Method.ToString(), @"SelectDriveTool\(\s*" + tool + @"\b"))
				.Select(m => m.Name).Distinct().ToList();
			if (setters.Count == 0)
			{
				unreachable.Add($"{tool}: nothing calls SelectDriveTool({tool}, ...)");
				continue;
			}
			if (setters.Any(wired.Contains)) continue;
			unreachable.Add($"{tool}: selected only by {string.Join(", ", setters)}, and no Click in the"
				+ " XAML reaches any of them");
		}
		Assert.True(unreachable.Count == 0,
			"The Start button dispatches to these, and the user has no way to select them:\n  "
				+ string.Join("\n  ", unreachable));
	}

	/// <summary>The sentences the app is allowed to throw in English, by the method that throws them.
	///
	/// Every one of these is the output, the exit code or the missing artefact of an external tool -
	/// DISM, wimlib, VSS, bcdboot, Ventoy, the GPT writer, a child process - which is a diagnostic for a
	/// bug report, not advice a person can act on. ShowError now labels them in the user's own language
	/// ("Technical detail:") instead of letting them look like the app speaking.
	///
	/// Everything a person CAN act on went into the dictionary: the thirty keys this list is the
	/// complement of. A new English sentence in any other method, or one more in one of these, fails.
	/// </summary>
	private static readonly Dictionary<string, int> DiagnosticThrowers = new(StringComparer.Ordinal)
	{
		// An external tool's own diagnostic - its exit code, its stderr, or the artefact it failed to produce.
		// Translating DISM's or wimlib's output would be translating a label on top of English anyway, and the
		// text that matters for a bug report is the tool's, verbatim.
		["CreateWindowsToGoFromImageAsync"] = 1,
		["CreateShadowCopyAsync"] = 1,
		["GetDosDeviceTarget"] = 1,
		["EnsureVentoyAsync"] = 3,
		["MultiBootFlowAsync"] = 1,
		["RunVentoyAsync"] = 1,
		["StreamCloneWithWimlibAsync"] = 4,
		["ExportBootableVhdxCoreAsync"] = 6,
		["RestoreWimToDriveAsync"] = 2,
		["RestoreVhdxToDriveAsync"] = 3,
		["RunExperimentalFullRootUsbCloneAsync"] = 2,
		["RunProcessAsync"] = 1,
		["RunProcessCaptureAsync"] = 1,
		["RunProcessWithArgumentListAsync"] = 1,

		// Raw on-disk structures: an LBA, a VCN gap, a short read, a Win32 error from CreateFile. Nobody can act
		// on these except by sending them in, and the vocabulary ($DATA, $ATTRIBUTE_LIST, GPT entry table) has no
		// translation worth having. W is WriteGpt's local write helper.
		["ReadFull"] = 1,
		["ReadGpt"] = 1,
		["WriteGpt"] = 2,
		["W"] = 1,
		["RawWriteImageToDiskAsync"] = 2,
		["VerifyRawWrite"] = 2,
		["CopyStreamViaApi"] = 2,
		["WriteFileWithMeta"] = 3,
		["WriteNonResident"] = 2,
		["AttachReadOnly"] = 1,

		// Never reaches a dialog. EnableBitLockerAsync's two sentences go to the log - both call sites catch
		// it and the end-of-run report carries a translated warning. If that ever becomes something a user
		// reads, this list is where it shows up.
		//
		// RecoverOne used to be here on the same grounds - its refusal was counted as one failed file among
		// many - and that was the reason to rescue the sentence rather than record it: it carries the only
		// advice that can fix the row it came from. It throws NoRecoveryPayloadException(L("RfStNoPayload"))
		// now and the recovery writes it into that row's status. This rule is what noticed the entry had gone
		// stale, on the first run after the change.
		["EnableBitLockerAsync"] = 2,
	};

	/// <summary>
	/// A thrown message is either translated or a recorded diagnostic - never a stray English sentence.
	///
	/// ShowError prints ex.Message verbatim under a translated title, so a thrown English sentence lands
	/// on screen in English however the app is set. Eighty-four did, and nothing here could see them:
	/// every localisation rule in this suite matches text assigned to a widget or handed to MessageBox,
	/// and a thrown message takes neither route. Tracing (not counting) was the only way to tell the
	/// ones a user reads from the ones a nearer catch swallows.
	/// </summary>
	[Fact]
	public void EveryThrownSentenceIsTranslatedOrARecordedDiagnostic()
	{
		var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (ObjectCreationExpressionSyntax oc in root.DescendantNodes()
					.OfType<ObjectCreationExpressionSyntax>())
			{
				// `throw new X(...)` as a statement AND as an expression (`?? throw new IOException(...)`),
				// and an exception that is never thrown at all but handed straight to ShowError, which prints
				// its Message exactly the same way. The first version of this rule checked only the throws and
				// walked past `ShowError(L("ErrDownload"), new Exception("The download stalled - no data
				// arrived for 60 seconds..."))`, which is as user-facing as text gets.
				if (oc.Parent is not ThrowStatementSyntax && oc.Parent is not ThrowExpressionSyntax
						&& !IsShowErrorArgument(oc)) continue;
				if (!oc.Type.ToString().EndsWith("Exception", StringComparison.Ordinal)) continue;
				ExpressionSyntax? first = oc.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
				if (first is null) continue;
				// A conditional is examined BRANCH BY BRANCH. LiteralPartsOf had no case for one, so it fell to
				// `_ => ""`, ReadsAsASentence("") is false, and `throw new X(cond ? "..." : "...")` was skipped in
				// silence. Exactly one such site existed in the app - on the backup screen, carrying two English
				// sentences, one of which reached a dialog - and this rule was passing over it the whole time.
				// Branch by branch also stops a translated branch from vouching for an English one, which a single
				// Calls(first, "L") over the whole conditional would have done.
				foreach (ExpressionSyntax alt in MessageAlternatives(first))
				{
					// A message built from L(...) is translated by construction, whatever else is concatenated on.
					if (SourceModel.Calls(alt, "L").Any()) continue;
					string literal = LiteralPartsOf(alt);
					if (!ReadsAsASentence(literal)) continue;
					string owner = OwningMethodName(oc);
					if (!found.TryGetValue(owner, out List<string> list))
						found[owner] = list = new List<string>();
					list.Add($"{SourceModel.Where(file, oc)}  {Trim120(literal)}");
				}
			}

		var complaints = new List<string>();
		foreach (var pair in found.OrderBy(p => p.Key, StringComparer.Ordinal))
		{
			int allowed = DiagnosticThrowers.TryGetValue(pair.Key, out int n) ? n : 0;
			if (pair.Value.Count == allowed) continue;
			complaints.Add(allowed == 0
				? $"{pair.Key} throws {pair.Value.Count} English sentence(s) and is not a recorded diagnostic "
					+ $"thrower:\n      " + string.Join("\n      ", pair.Value)
				: $"{pair.Key} throws {pair.Value.Count} English sentence(s), {allowed} recorded:\n      "
					+ string.Join("\n      ", pair.Value));
		}
		foreach (string gone in DiagnosticThrowers.Keys.Where(k => !found.ContainsKey(k))
				.OrderBy(k => k, StringComparer.Ordinal))
			complaints.Add($"{gone} is recorded as a diagnostic thrower but throws no English sentence any "
				+ "more - take it off the list");

		Assert.True(complaints.Count == 0,
			"ShowError prints ex.Message verbatim, so these land on screen in English under a translated "
				+ "title. Use L(\"Key\") for anything a person can act on; add the method to "
				+ "DiagnosticThrowers only when the text is a tool's own diagnostic.\n  "
				+ string.Join("\n  ", complaints));
	}

	/// <summary>The alternative messages one argument can produce: a conditional yields BOTH branches,
	/// recursively, and anything else yields itself. One exception object can carry two different sentences,
	/// and each has to answer for itself.</summary>
	private static IEnumerable<ExpressionSyntax> MessageAlternatives(ExpressionSyntax e) => e switch
	{
		ParenthesizedExpressionSyntax p => MessageAlternatives(p.Expression),
		ConditionalExpressionSyntax c => MessageAlternatives(c.WhenTrue).Concat(MessageAlternatives(c.WhenFalse)),
		_ => new[] { e },
	};

	/// <summary>The string-literal text in an expression: a literal, an interpolated string's text runs,
	/// or either side of a concatenation. Interpolation holes contribute nothing.</summary>
	private static string LiteralPartsOf(ExpressionSyntax e) => e switch
	{
		// Reached only for a conditional NESTED inside something else - `"a" + (c ? "b" : "d")` - because
		// MessageAlternatives flattens a top-level one before this is called.
		ConditionalExpressionSyntax cond => LiteralPartsOf(cond.WhenTrue) + " " + LiteralPartsOf(cond.WhenFalse),
		LiteralExpressionSyntax lit when lit.Token.IsKind(SyntaxKind.StringLiteralToken) => lit.Token.ValueText,
		InterpolatedStringExpressionSyntax interp => string.Concat(interp.Contents
			.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
		BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression) =>
			LiteralPartsOf(bin.Left) + " " + LiteralPartsOf(bin.Right),
		ParenthesizedExpressionSyntax p => LiteralPartsOf(p.Expression),
		_ => "",
	};

	/// <summary>Two runs of two or more letters, separated by something that is not a letter. A bare
	/// token ("NTFS", "RAW") is not a sentence and is not worth a translation key.</summary>
	private static bool ReadsAsASentence(string text) =>
		Regex.IsMatch(text, @"[A-Za-z]{2,}[^A-Za-z]+[A-Za-z]{2,}");

	private static string OwningMethodName(SyntaxNode node)
	{
		for (SyntaxNode n = node; n != null; n = n.Parent)
		{
			if (n is MethodDeclarationSyntax md) return md.Identifier.Text;
			if (n is LocalFunctionStatementSyntax lf) return lf.Identifier.Text;
			if (n is ConstructorDeclarationSyntax cd) return cd.Identifier.Text;
			if (n is AccessorDeclarationSyntax ad) return ad.Keyword.Text;
			// An expression-bodied PROPERTY is a method-like body too, and leaving it out answered
			// "<file scope>" for every expression in one — which is both wrong and, for a rule that keys
			// an allow-list off this name, quietly wrong. Found when the option-owner properties below
			// came back unattributed.
			if (n is PropertyDeclarationSyntax pd) return pd.Identifier.Text;
		}
		return "<file scope>";
	}

	/// <summary>Is this `new SomeException(...)` an argument of a ShowError call? ShowError prints
	/// ex.Message whether the exception was thrown or built on the spot.</summary>
	private static bool IsShowErrorArgument(SyntaxNode node)
	{
		for (SyntaxNode n = node; n != null; n = n.Parent)
		{
			if (n is InvocationExpressionSyntax inv)
				return (inv.Expression as IdentifierNameSyntax)?.Identifier.Text == "ShowError";
			if (n is BlockSyntax || n is StatementSyntax && n is not ExpressionStatementSyntax) return false;
		}
		return false;
	}

	private static string Trim120(string s)
	{
		string t = Regex.Replace(s, @"\s+", " ").Trim();
		return t.Length > 120 ? t.Substring(0, 120) : t;
	}

	/// <summary>
	/// The cleaner decides what is on screen in one place, and everything that acts on the categories
	/// asks it.
	///
	/// It used to be decided in three places and they disagreed. The run cleaned what was ticked AND
	/// visible; the total on the Clean button summed everything ticked, visible or not; Analyze's
	/// "About X can be freed." summed every category there was. Tick Show advanced, tick the Windows
	/// Update cache, untick Show advanced: the button read "Clean 4.1 GB", the run freed 0.3 GB, and
	/// the line afterwards said "Done - freed 0.3 GB". No single line was wrong. There were three.
	///
	/// Two halves, because the defect had two shapes. The button's was an OMISSION - it mentioned
	/// neither _cleanAdvanced nor Risk, so a rule hunting stray copies of the predicate would have
	/// sailed straight past it, and the first version of this rule did.
	///
	///   (a) anything that enumerates _cleanCats either owns the whole list for a reason recorded
	///       below, or asks IsCleanVisible / SelectedCleanCategories;
	///   (b) _cleanAdvanced is read only by the predicate and written only by its own toggle, and
	///       `Risk == 0` is compared only by the predicate and by Recommended - which uses the same
	///       number to mean something else on purpose (recommended = the green ones).
	/// </summary>
	[Fact]
	public void TheCleanerDecidesWhatIsOnScreenInOnePlace()
	{
		// Methods that legitimately walk EVERY category, whatever is on screen:
		//   EnsureCleanCategories  - builds the list and wires its change events
		//   RefreshCleanLabels     - re-localises every row on a language change
		//   CleanRecommended_Click - ticks the green ones and UNTICKS the rest, hidden ones included
		//   CleanAnalyze_Click     - sizes every row, so the numbers are ready when Advanced is turned on
		var ownsTheWholeList = new HashSet<string>(StringComparer.Ordinal)
		{
			"EnsureCleanCategories", "RefreshCleanLabels", "CleanRecommended_Click", "CleanAnalyze_Click",
		};
		var advancedAllowed = new HashSet<string>(StringComparer.Ordinal)
			{ "IsCleanVisible", "CleanAdvancedToggle_Click" };
		var greenAllowed = new HashSet<string>(StringComparer.Ordinal)
			{ "IsCleanVisible", "CleanRecommended_Click" };

		var complaints = new List<string>();
		var advancedIn = new SortedSet<string>(StringComparer.Ordinal);
		var greenIn = new SortedSet<string>(StringComparer.Ordinal);
		int users = 0;
		foreach (var (file, method, name) in SourceModel.Methods())
		{
			string text = method.ToString();
			if (Regex.IsMatch(text, @"\b_cleanAdvanced\b")) advancedIn.Add(name);
			// `Risk == 0`, `Risk != 0`, `0 == Risk` - any comparison of the risk badge against green.
			if (Regex.IsMatch(text, @"\bRisk\s*[!=]=\s*0\b|\b0\s*[!=]=\s*[A-Za-z_.]*Risk\b"))
				greenIn.Add(name);

			// Enumerating the list: `_cleanCats.Where(...)`, `_cleanCats.ToList()`, `in _cleanCats`.
			// A null check (`_cleanCats == null`) is not an enumeration and is not counted.
			if (!Regex.IsMatch(text, @"_cleanCats\s*[.)]|\bin\s+_cleanCats\b")) continue;
			users++;
			if (ownsTheWholeList.Contains(name)) continue;
			if (Regex.IsMatch(text, @"\bIsCleanVisible\b|\bSelectedCleanCategories\b")) continue;
			complaints.Add($"{name} ({SourceModel.Where(file, method)}) walks the category list without asking "
				+ "IsCleanVisible or SelectedCleanCategories, so whatever it counts or acts on is its own idea of "
				+ "what is on screen");
		}
		Assert.True(users >= 4, $"only {users} methods enumerate _cleanCats - the scan is wrong, not the code.");
		Assert.True(advancedIn.Count > 0, "nothing reads _cleanAdvanced - the scan is wrong, not the code.");
		Assert.True(greenIn.Count > 0, "nothing compares Risk against 0 - the scan is wrong, not the code.");

		foreach (string m in advancedIn.Where(m => !advancedAllowed.Contains(m)))
			complaints.Add($"{m} reads _cleanAdvanced itself instead of asking IsCleanVisible");
		foreach (string m in greenIn.Where(m => !greenAllowed.Contains(m)))
			complaints.Add($"{m} compares Risk against 0 itself instead of asking IsCleanVisible");
		foreach (string m in advancedAllowed.Where(m => !advancedIn.Contains(m)))
			complaints.Add($"{m} no longer reads _cleanAdvanced - if that moved, move this list with it");
		foreach (string m in greenAllowed.Where(m => !greenIn.Contains(m)))
			complaints.Add($"{m} no longer compares Risk against 0 - if that moved, move this list with it");

		Assert.True(complaints.Count == 0,
			"The cleaner must decide what is on screen in ONE place. Every other copy of that decision, and "
				+ "every list walked without it, is a number this screen can promise and the run will not keep:"
				+ "\n  " + string.Join("\n  ", complaints));
	}

	/// <summary>The recovery's verdict reaches the row it belongs to, not only a counter.</summary>
	[Fact]
	public void TheRecoveryWritesItsVerdictOnEveryRowItTouched()
	{
		var hit = SourceModel.Methods().Where(m => m.Name == "RecoverPickedToDir").ToArray();
		Assert.True(hit.Length == 1, $"Expected one RecoverPickedToDir, found {hit.Length}.");
		string code = string.Join("\n", hit[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		List<string> problems = new List<string>();
		// One sentence for every outcome it counts: ok, partial and fail are the three ways a file can come
		// out, empty is a fourth answer laid over them, and RecoverOne's own refusal is a fifth.
		foreach (var (key, what) in new[]
		{
			("RfStDone", "a file that came out whole"),
			("RfStPartialOut", "a file only part of which could be read"),
			("RfStFailOut", "a file that could not be read at all"),
			("RfStEmptyOut", "a file written at full length with nothing in it"),
			("RfStNoPayload", "a row that carries no recovery data"),
		})
			if (!code.Contains(key, StringComparison.Ordinal))
				problems.Add($"nothing is written on the row for {what} ({key})");

		if (!Regex.IsMatch(code, @"\bf\.StatusKey\s*="))
			problems.Add("no verdict is written on the row at all - the four counters the caller prints "
				+ "cannot name one, and this column still showed the estimate from before the run");
		if (!code.Contains("NoRecoveryPayloadException", StringComparison.Ordinal))
			problems.Add("RecoverOne's refusal is not told apart from a read failure, so its advice - "
				+ "scan the drive again - goes back into the failure count it spent its life in");

		// Only a CLEAN recovery may untick a row. Whatever is still ticked when the summary closes is what
		// RfRecStoppedNote tells the user to run again, and a partial or an empty file is exactly that.
		if (!Regex.IsMatch(code, @"Selected\s*=\s*false"))
			problems.Add("nothing unticks a recovered row, so a second run repeats the files that already "
				+ "came out and RfRecStoppedNote's own advice cannot be followed");
		if (!Regex.IsMatch(code, @"clean\s*=\s*whole\s*&&\s*!allZero"))
			problems.Add("the test for a clean recovery is no longer 'came out whole and was not empty'");
		if (!Regex.IsMatch(code, @"if\s*\(clean\)\s*recovered\.Add"))
			problems.Add("rows are not gathered by that test before being unticked");
		if (!Regex.IsMatch(code, @"_suppressRecoverSelUpdate\s*=\s*true"))
			problems.Add("the bulk untick is not suppressed, and each untick walks the whole scan three times");

		// And both summaries say where the per-file reason now is.
		foreach (var caller in SourceModel.Methods()
			.Where(m => m.Name != "RecoverPickedToDir"
				&& m.Method.ToString().Contains("RecoverPickedToDir(", StringComparison.Ordinal)))
		{
			string cc = string.Join("\n", caller.Method.ToString().Split('\n')
				.Where(l => !l.TrimStart().StartsWith("//")));
			if (!cc.Contains("RfRecRetryNote", StringComparison.Ordinal))
				problems.Add($"{caller.Name}: the summary never says the rows carry the per-file reason");
		}

		Assert.True(problems.Count == 0,
			"Three came back empty, five could not be read - a count cannot say WHICH, and on a 2,000-row "
				+ "deep scan that is the only thing the user needs:\n  " + string.Join("\n  ", problems));
	}

	/// <summary>One answer to what the recovery will act on, in one place.</summary>
	[Fact]
	public void OneAnswerToWhatTheRecoveryWillActOn()
	{
		// The predicate itself: one copy, in the helper that owns it.
		List<string> owners = new List<string>();
		foreach (var m in SourceModel.Methods())
			if (Regex.IsMatch(m.Method.ToString(), @"Selected\s*&&\s*\w*\.?Recoverable"))
				owners.Add($"{m.Name} ({SourceModel.Where(m.File, m.Method)})");
		Assert.True(owners.Count == 1 && owners[0].StartsWith("PickedRecoverFiles", StringComparison.Ordinal),
			"Selected && Recoverable is the answer to what the recovery will act on, and it belongs to "
				+ "PickedRecoverFiles alone. Found in: " + string.Join(", ", owners));

		// And everything that answers the question asks that helper - or asks Recoverable itself, where it is
		// deciding about the VISIBLE rows rather than the picked ones.
		string[] consumers = { "RecoverButton_Click", "RecoverZip_Click", 
			"UpdateRecoverSelectionInfo", "RecoverGallery_Click", "RecoverSelectAll_Click" };
		List<string> problems = new List<string>();
		foreach (string name in consumers)
		{
			var hit = SourceModel.Methods().Where(m => m.Name == name).ToArray();
			Assert.True(hit.Length == 1, $"Expected one {name}, found {hit.Length}.");
			string code = string.Join("\n", hit[0].Method.ToString().Split('\n')
				.Where(l => !l.TrimStart().StartsWith("//")));
			if (!code.Contains("PickedRecoverFiles()", StringComparison.Ordinal)
					&& !code.Contains("Recoverable", StringComparison.Ordinal))
				problems.Add($"{name}: decides what can be recovered without asking whether the data is "
					+ "still there");
		}
		Assert.True(problems.Count == 0,
			"The gallery used to be this screen's fifth answer to that question: its tiles offered to recover "
				+ "rows the recovery would never look at, so ticking one lit the tile's border up and moved no "
				+ "number anywhere.\n  " + string.Join("\n  ", problems));
	}

	/// <summary>A row's status is a key the language can re-render, never a stored sentence.</summary>
	[Fact]
	public void ARowsStatusIsAKeyTheLanguageCanReRender()
	{
		List<string> problems = new List<string>();

		// 1. Status is computed. A setter is all it takes for a scan to bake its own language back in.
		var decls = SourceModel.Parsed
			.SelectMany(p => p.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
				.Where(d => d.Identifier.Text == "Status" && d.Parent is ClassDeclarationSyntax c
					&& c.Identifier.Text == "DeletedFile")
				.Select(d => (p.File, Decl: d)))
			.ToArray();
		Assert.True(decls.Length == 1, $"Expected one DeletedFile.Status, found {decls.Length}.");
		var (file, status) = decls[0];
		if (status.AccessorList != null && status.AccessorList.Accessors.Any(a => a.Keyword.Text == "set"))
			problems.Add($"{SourceModel.Where(file, status)}: Status is settable again, so a scan can store "
				+ "a finished sentence in it");
		if (!status.ToString().Contains("L(", StringComparison.Ordinal))
			problems.Add("DeletedFile.Status does not render its key, so it cannot follow the language");

		// 2. And nobody smuggles a rendered sentence in through the key.
		foreach (var (f, root) in SourceModel.Parsed)
			foreach (var a in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
			{
				if (!a.Left.ToString().EndsWith("StatusKey", StringComparison.Ordinal)) continue;
				if (a.Right.ToString().Contains("L(", StringComparison.Ordinal))
					problems.Add($"{SourceModel.Where(f, a)}: puts a rendered sentence in StatusKey");
			}

		// 3. The language change re-reads the rows. Without this the column keeps whatever language the scan
		//    ran in - which was the whole defect: the HEADER translated and the rows under it did not.
		var apply = SourceModel.Methods().Where(m => m.Name == "ApplyLanguage").ToArray();
		Assert.True(apply.Length == 1, $"Expected one ApplyLanguage, found {apply.Length}.");
		string applyCode = string.Join("\n", apply[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));
		if (!applyCode.Contains("RaiseRendered()", StringComparison.Ordinal))
			problems.Add("ApplyLanguage does not re-read the Recover rows, so the Status column stays in "
				+ "the language the scan ran in while its own header translates");

		// 4. And re-reading means all three rendered members: the sentence, the dot and the percentage.
		var raise = SourceModel.Methods().Where(m => m.Name == "RaiseRendered").ToArray();
		Assert.True(raise.Length == 1, $"Expected one RaiseRendered, found {raise.Length}.");
		foreach (string member in new[] { "Status", "StatusKind", "PercentText" })
			if (!raise[0].Method.ToString().Contains($"nameof({member})", StringComparison.Ordinal))
				problems.Add($"RaiseRendered does not raise {member}, so that part of the pill stays stale");

		Assert.True(problems.Count == 0,
			"The Status column was written at scan time and pinned to the language of the scan. Nothing could "
				+ "re-render it, and nothing could change it after the recovery had run:\n  "
				+ string.Join("\n  ", problems));
	}

	/// <summary>The one line that survives every repaint tells the whole truth about the result.</summary>
	[Fact]
	public void TheRecoverResultLineNamesEveryReasonItIsIncomplete()
	{
		// UpdateRecoverSelectionInfo runs on every selection change AND in each scan's finally, so it is the
		// only place a warning survives; a one-shot message set right after a scan is overwritten here.
		var hit = SourceModel.Methods().Where(m => m.Name == "UpdateRecoverSelectionInfo").ToArray();
		Assert.True(hit.Length == 1, $"Expected one UpdateRecoverSelectionInfo, found {hit.Length}.");
		string code = string.Join("\n", hit[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		List<string> problems = new List<string>();
		foreach (var (needle, what) in new[]
		{
			("RfDeepPartialSuffix", "a deep scan that was stopped before the end"),
			("RfCappedSuffix", "a list that filled up at the entry cap"),
			("RfFilterShown", "a filter hiding most of the rows"),
		})
			if (!code.Contains(needle, StringComparison.Ordinal))
				problems.Add($"says nothing about {what} ({needle})");

		// The cap has to be derived, not a flag a new scanner can forget to raise.
		var trunc = SourceModel.Parsed
			.SelectMany(p => p.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
				.Where(d => d.Identifier.Text == "Truncated"))
			.ToArray();
		Assert.True(trunc.Length == 1, $"Expected one Truncated property, found {trunc.Length}.");
		if (!trunc[0].ToString().Contains("MaxRecoverEntries", StringComparison.Ordinal))
			problems.Add("Truncated no longer derives from the entry cap, so a scanner that stops adding "
				+ "can report a full list as a complete one");

		Assert.True(problems.Count == 0,
			"A result the user reads as complete when it is not is the difference between trying again and "
				+ "giving up on the drive:\n  " + string.Join("\n  ", problems));
	}

	[Fact]
	// MEASURED ON HARDWARE (2026-10-11, eight deleted files on a throwaway 2 GB VHD): five came back whole
	// and three came back as zeros. The three rows stayed ticked, which is the whole design - what is still
	// ticked is what is worth another attempt - and the summary said "8 of 8 recovered" with not one word
	// about why anything was still ticked. The hint was gated on `ok`, and `ok` counts a write as OK when it
	// is WHOLE, zeros included; the ticks come from `recovered`, the clean ones. Two facts, one of them wrong.
	//
	// Same defect class as the rest of this screen: a derived answer read off the nearest counter instead of
	// off the thing it is actually about.
	public void TheRetryHintAgreesWithWhatIsStillTicked()
	{
		var worker = SourceModel.Methods().Where(m => m.Name == "RecoverPickedToDir").ToArray();
		Assert.True(worker.Length == 1, $"Expected one RecoverPickedToDir, found {worker.Length}.");
		string wcode = worker[0].Method.ToString();

		List<string> problems = new List<string>();

		// The worker has to publish the CLEAN count, or a caller has nothing honest to gate the hint on.
		if (!Regex.IsMatch(wcode, @"int\s+Clean"))
			problems.Add("RecoverPickedToDir no longer returns a Clean count, so the only numbers a caller has "
				+ "are Ok/Partial/Fail/Empty - and an all-zero write is counted as Ok");
		if (!Regex.IsMatch(wcode, @"return\s*\([^)]*recovered\.Count"))
			problems.Add("the Clean count no longer comes from `recovered` - the very list the unticking walks - "
				+ "so the hint and the ticks can disagree again");

		// Both summaries - the folder recovery and the ZIP - gate the hint, and neither may use Ok for it.
		var notes = SourceModel.Parsed
			.SelectMany(f => f.Root.DescendantNodes().OfType<ConditionalExpressionSyntax>())
			.Where(c => c.ToString().Contains("RfRecRetryNote", StringComparison.Ordinal))
			.ToArray();
		Assert.True(notes.Length == 2,
			$"Expected the retry hint on both summaries (folder + ZIP), found {notes.Length}.");
		foreach (var n in notes)
		{
			string cond = n.Condition.ToString();
			if (!cond.Contains("Clean", StringComparison.Ordinal))
				problems.Add($"the retry hint is gated on `{cond}`, which is not the clean count: a file that "
					+ "was written whole but came back as zeros counts there and still stays ticked");
		}

		Assert.True(problems.Count == 0,
			"Rows left ticked with no sentence explaining them read as a bug in the program rather than "
				+ "as the invitation to try again that they are:" + "\n  " + string.Join("\n  ", problems));
	}

	/// <summary>
	/// A run the person stopped is never called a failure on the status line.
	///
	/// The DIALOG has been right for a while: ShowError types a StoppedException as information and prints its
	/// own sentence. The status line is written literally at each site, and it is what stays on screen after
	/// the dialog is dismissed — so a Stop read "Clone failed." / "Backup failed." / "Failed." in three of the
	/// four places that write one, under a dialog that had just said "Stopped at your request."
	///
	/// Measured 2026-10-12, behind one button: of the seven write paths StartButton_Click dispatches, four had
	/// nothing to say about a Stop at all. A Stop reaches every one of them the same way — StopButton_Click
	/// kills the child, the child exits non-zero, RunProcessAsync turns that into an exception carrying its
	/// exit code — so "wimlib-imagex.exe exited with code -1" was the whole of the answer, with no word about
	/// the target, which each of those paths cleans and formats BEFORE it writes. The clone and the .vhdx
	/// restore threw StoppedException themselves; the .wim restore, the .ffu restore and the Windows-USB build
	/// did not. The same key press, the same screen, two different answers depending on the file picked.
	///
	/// Scope is derived from the assignment itself, so a new flow is in it the moment it writes one. The one
	/// site that legitimately has no stop to report is the disk rescan, and it says so in its own guard:
	/// _refreshOwnsBusy is the app's existing name for "busy, but not an operation the user started" —
	/// ShowError consults the same field for the same reason, two methods away.
	/// </summary>
	[Fact]
	public void AStoppedRunIsNotCalledAFailureOnTheStatusLine()
	{
		var sites = new List<(string Where, string Line, bool AsksAboutStop)>();
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (AssignmentExpressionSyntax a in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
			{
				if (a.Left is not MemberAccessExpressionSyntax target
						|| target.Name.Identifier.Text != "Text"
						|| (target.Expression as IdentifierNameSyntax)?.Identifier.Text != "StatusText") continue;
				string rhs = a.Right.ToString();
				// Only a key that NAMES a failure. A status line that reports progress, or a stage, has no
				// stop to distinguish; the defect is specifically calling a cancellation a failure.
				if (!Regex.IsMatch(rhs, @"L\(.*(?:Fail|Err)")) continue;
				// A rescan is not an operation the person started, so it has no Stop of its own. The guard it is
				// already written under says exactly that, and nothing else in the app reads that field. Read
				// from the enclosing `if` CONDITIONS, not from the assignment's own statement - the assignment
				// is its own statement and the guard sits on the `if` above it.
				if (a.Ancestors().OfType<IfStatementSyntax>()
						.Any(i => i.Condition.ToString().Contains("_refreshOwnsBusy", StringComparison.Ordinal)))
					continue;
				sites.Add((SourceModel.Where(file, a), a.ToString(),
					rhs.Contains("stopRequested", StringComparison.Ordinal)));
			}

		// The premise: if nothing writes a failure into the status line any more, this rule guards nothing.
		Assert.True(sites.Count >= 3,
			$"Only {sites.Count} place(s) write a failure into the status line; there were three. Re-read this "
				+ "rule rather than leaving it passing over a scope that has moved.");

		string[] silent = sites.Where(s => !s.AsksAboutStop).Select(s => $"{s.Where}  {s.Line}").ToArray();
		Assert.True(silent.Length == 0,
			"These write a failure into the status line without asking whether the person pressed Stop, so a "
				+ "run they cut short short reads as one that broke. Ask in the assignment: "
				+ "L(stopRequested ? \"SxStopped\" : \"Sx...Failed\").\n  " + string.Join("\n  ", silent));
	}

	/// <summary>
	/// The disk rescan speaks on the status line only when it has the floor.
	///
	/// This is the companion to the rule above, and it exists because that rule's fix was INVISIBLE without
	/// it. After a stopped backup the catch wrote "Stopped." and the screen still read "Ready." — twice, on
	/// two hardware runs, before the reason turned up. The rescan is provoked by the app's OWN teardown: the
	/// backup deletes its VSS snapshot, a volume disappears, the device-change debounce fires moments after
	/// the operation ended, nothing is holding busy any more, so the rescan claims it and announces
	/// "Scanning disks…" — over the verdict. Then its own tidy-up at the end replaced that announcement with
	/// "Ready.", and the verdict was gone about two seconds after being written.
	///
	/// A background activity that overwrites a foreground verdict is the defect class, and the fix is the
	/// same shape as the campaign's others: the rescan asks whether the line is its to write. So no write in
	/// here is unconditional. Each one sits under `silent` (the device-change rescan is passive by design
	/// everywhere else too), under `_refreshOwnsBusy` plus its scan ticket (this invocation holds busy), or
	/// under the comparison against _operationStatusText (the line is still the rescan's own text) — and the
	/// one that reports a failed scan is allowed to speak over anything, because an empty picker with no
	/// explanation beside it is worse.
	/// </summary>
	[Fact]
	public void TheDiskRescanDoesNotSpeakOverAnOperationsVerdict()
	{
		var refresh = SourceModel.Methods().Where(m => m.Name == "RefreshDisksAsync").ToArray();
		Assert.True(refresh.Length == 1, $"expected one RefreshDisksAsync, found {refresh.Length}");
		SyntaxNode body = refresh[0].Method;

		// Every way this method can put text on the status line: a direct assignment, or SetBusy with a
		// status argument that is not plainly null.
		var writes = new List<(SyntaxNode Node, string Text)>();
		foreach (AssignmentExpressionSyntax a in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
			if (a.Left is MemberAccessExpressionSyntax t && t.Name.Identifier.Text == "Text"
					&& (t.Expression as IdentifierNameSyntax)?.Identifier.Text == "StatusText")
				writes.Add((a, a.ToString()));
		foreach (InvocationExpressionSyntax inv in SourceModel.Calls(body, "SetBusy"))
		{
			ArgumentSyntax status = inv.ArgumentList.Arguments.FirstOrDefault(
				x => x.NameColon?.Name.Identifier.Text == "status")
				?? inv.ArgumentList.Arguments.Skip(1).FirstOrDefault();
			if (status is null) continue;
			if (status.Expression.IsKind(SyntaxKind.NullLiteralExpression)) continue;
			writes.Add((inv, inv.ToString()));
		}

		Assert.True(writes.Count >= 3,
			$"Only {writes.Count} status-line write(s) found in RefreshDisksAsync; there were four. Re-read "
				+ "this rule rather than leaving it passing over a scope that has moved.");

		var unguarded = new List<string>();
		foreach (var (node, text) in writes)
		{
			// The guard may be the enclosing `if`, or a condition written into the value itself
			// (`silent ? null : L("BzScanDisks")`), which is how the claim at the top of the method asks.
			string conditions = string.Join(" ", node.Ancestors().TakeWhile(n => n != body)
				.OfType<IfStatementSyntax>().Select(i => i.Condition.ToString()));
			string inValue = string.Join(" ", node.DescendantNodes().OfType<ConditionalExpressionSyntax>()
				.Select(c => c.Condition.ToString()));
			string asked = conditions + " " + inValue;
			bool hasFloor = asked.Contains("silent", StringComparison.Ordinal)
				|| asked.Contains("_refreshOwnsBusy", StringComparison.Ordinal)
				|| asked.Contains("_operationStatusText", StringComparison.Ordinal);
			// A failed scan beside an emptied picker speaks over anything.
			if (text.Contains("ErrDiskScan", StringComparison.Ordinal)) continue;
			if (!hasFloor) unguarded.Add($"{SourceModel.Where(refresh[0].File, node)}  {Trim120(text)}");
		}

		Assert.True(unguarded.Count == 0,
			"A rescan writes the status line here without asking whether the line is its to write, so it can "
				+ "paint over the verdict an operation just left there. Gate it on `silent`, on "
				+ "`_refreshOwnsBusy && _refreshBusyScan == myScan`, or on the line still being the rescan's "
				+ "own text.\n  " + string.Join("\n  ", unguarded));
	}

	/// <summary>
	/// A Stop does not leave a file on the person's Desktop.
	///
	/// SaveLogToDesktop writes the session log there — the thing the issue template calls the most useful
	/// attachment — and nine of its ten callers are catch blocks. A Stop reaches a catch by design: killing
	/// the child process throws. So every Stop dropped an ~850 KB DriveForge-log-… file on the Desktop with
	/// nothing on screen to explain it. Found by looking for residues after a hardware run, not by reading
	/// the code: three stopped backups, three files.
	///
	/// ShowError already makes exactly this distinction one method away, suppressing the bug-report offer
	/// when stopRequested, on the grounds that asking for a bug report about a deliberate cancellation is
	/// noise — and the log is the attachment that offer would have asked for. So the test belongs in the one
	/// place that writes, not at ten call sites, and the one caller that is a REQUEST rather than a reaction
	/// says so at the call.
	///
	/// This rule pins both halves: the writer asks, and exactly one caller overrides it. Without the second
	/// half, the obvious "fix" to a Stop that left no log would be to pass userAsked at whichever call site
	/// was being looked at.
	/// </summary>
	[Fact]
	public void AStopLeavesNoFileOnTheDesktop()
	{
		var writer = SourceModel.Methods().Where(m => m.Name == "SaveLogToDesktop").ToArray();
		Assert.True(writer.Length == 1, $"expected one SaveLogToDesktop, found {writer.Length}");
		string body = writer[0].Method.ToString();
		Assert.True(body.Contains("stopRequested", StringComparison.Ordinal),
			"SaveLogToDesktop writes a file to the person's Desktop and does not ask whether they pressed "
				+ "Stop. Nine of its ten callers are catch blocks, and a Stop reaches a catch by design, so a "
				+ "cancellation leaves an unexplained ~850 KB file behind.");
		Assert.True(Regex.IsMatch(body, @"userAsked\s*&&|!\s*userAsked"),
			"The Stop test must let the one deliberate caller through — the 'save the log' menu item, which "
				+ "is a request and not a reaction, and which someone reaches for precisely after a Stop.");

		var overrides = new List<string>();
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (InvocationExpressionSyntax inv in SourceModel.Calls(root, "SaveLogToDesktop"))
				if (inv.ArgumentList.Arguments.Count > 0)
					overrides.Add($"{SourceModel.Where(file, inv)}  {Trim120(inv.ToString())}");

		Assert.True(overrides.Count == 1,
			$"Exactly one caller may override the Stop test — SaveLog_Click, the menu item. Found "
				+ $"{overrides.Count}:\n  " + string.Join("\n  ", overrides));
		Assert.True(overrides[0].Contains("userAsked", StringComparison.Ordinal),
			"The override must be written by name (userAsked: true), so the call says what it is claiming: "
				+ overrides[0]);
	}

	/// <summary>
	/// Every option the confirmation promised is accounted for when the run ends.
	///
	/// ConfirmOperationSummary is the last dialog before the disk is erased and it lists the options that
	/// are about to be applied. Three of them can then quietly not happen, and two of the three say so:
	/// drivers that were asked for and not added (MbDriversNone), registry edits a security suite blocked
	/// (HiveBlockedDone). The data partition did not. The run checks whether enough space is left over
	/// after Windows, and when it is not it writes "Data partition requested but skipped" to the LOG and
	/// carries on — so the confirmation said "Create an extra data partition", the drive came back with one
	/// partition, and the completion dialog mentioned neither.
	///
	/// The pattern that works is already here: a field carries the outcome out of the run, a Build…Summary
	/// renders it, and the completion dialog concatenates every one of them. So the rule is derived from
	/// that pattern rather than from a list of options: whatever is reset in ConfirmOperationSummary's
	/// per-operation reset is an outcome the dialog owes an answer about, and every Build…Summary that
	/// exists has to reach the dialog. Add a fourth outcome field and the first half fails until something
	/// reports it; write a reporter and forget to call it and the second half fails.
	/// </summary>
	[Fact]
	public void EveryPromisedOptionIsAccountedForWhenTheRunEnds()
	{
		var confirm = SourceModel.Methods().Where(m => m.Name == "ConfirmOperationSummary").ToArray();
		Assert.True(confirm.Length == 1, $"expected one ConfirmOperationSummary, found {confirm.Length}");

		// The per-operation reset: the fields this method clears before an operation starts are exactly the
		// ones whose value at the end is this operation's own.
		var outcomes = new HashSet<string>(StringComparer.Ordinal);
		foreach (AssignmentExpressionSyntax a in confirm[0].Method.DescendantNodes()
				.OfType<AssignmentExpressionSyntax>())
			if (a.Left is IdentifierNameSyntax id && id.Identifier.Text.StartsWith("_", StringComparison.Ordinal))
				outcomes.Add(id.Identifier.Text);
		foreach (InvocationExpressionSyntax inv in SourceModel.Calls(confirm[0].Method, "Clear"))
			if (inv.Expression is MemberAccessExpressionSyntax ma
					&& ma.Expression is IdentifierNameSyntax list
					&& list.Identifier.Text.StartsWith("_", StringComparison.Ordinal))
				outcomes.Add(list.Identifier.Text);

		Assert.True(outcomes.Count >= 4,
			$"Only {outcomes.Count} per-operation outcome field(s) found in ConfirmOperationSummary; there "
				+ "were four. Re-read this rule rather than leaving it passing over a scope that has moved: "
				+ string.Join(", ", outcomes.OrderBy(o => o, StringComparer.Ordinal)));

		// Every Build…Summary in the app, and what each one reads.
		var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var m in SourceModel.Methods())
			if (m.Name.StartsWith("Build", StringComparison.Ordinal)
					&& m.Name.EndsWith("Summary", StringComparison.Ordinal))
				summaries[m.Name] = m.Method.ToString();

		string[] unreported = outcomes
			.Where(f => !summaries.Values.Any(body => body.Contains(f, StringComparison.Ordinal)))
			.OrderBy(f => f, StringComparer.Ordinal).ToArray();
		Assert.True(unreported.Length == 0,
			"These outcomes are reset per operation — so they are this run's own answer about an option the "
				+ "confirmation listed — and no Build…Summary reads them, which means the completion dialog "
				+ "cannot mention them. An option promised and silently dropped reads as a program that did "
				+ "something else:\n  " + string.Join("\n  ", unreported));

		// ...and each reporter has to actually reach the dialog.
		var start = SourceModel.Methods().Where(m => m.Name == "StartButton_Click").ToArray();
		Assert.True(start.Length == 1, $"expected one StartButton_Click, found {start.Length}");
		string dispatch = start[0].Method.ToString();
		string[] uncalled = summaries.Keys.Where(n => !dispatch.Contains(n + "()", StringComparison.Ordinal))
			.OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(uncalled.Length == 0,
			"These build a line for the completion dialog and the completion dialog never asks for it:\n  "
				+ string.Join("\n  ", uncalled));
	}

	/// <summary>
	/// No entry in the ISO catalog hard-codes a version.
	///
	/// The catalog says so about itself — "Distros whose latest ISO is resolved live from a stable source
	/// (durable — no hard-coded versions)" — and five of its six entries were: Mint walks
	/// linuxmint/stable/ for the newest version directory, Debian uses current-live/, Arch uses iso/latest/.
	/// The Ubuntu entry pinned 24.04 TWICE, in the directory it indexed AND in the filename pattern, so
	/// "Get latest" could not return anything newer than a 24.04 point release. Measured 2026-10-12 by
	/// running the app's own ResolveCatalog over the real indexes: releases.ubuntu.com was already serving
	/// 26.04.1 LTS while Mint and Debian resolved to 22.3 and 13.7.0, both current. Two LTS generations
	/// behind, on the distro most people would pick, under a comment promising the opposite.
	///
	/// A version is a digit group, a dot, and another digit group. That is all this looks for, and no
	/// stripping of character classes is needed first — which was measured, not assumed, by writing the
	/// stripping and then checking it was load-bearing: it was not. In a regex the digits that mean "any
	/// digit" always sit inside a class, so the character immediately before the dot is "]" or a
	/// quantifier, never a bare digit: "[0-9]+(?:\.[0-9]+)?/" and "ubuntu-[0-9.]+-desktop-amd64\.iso"
	/// both pass untouched, while "ubuntu-24\.04" does not. The architecture strings ("64-bit", "amd64",
	/// "x86_64") carry digits with no dot between them at all.
	///
	/// Checked against every field including the LABEL, because "Ubuntu 24.04 LTS" is the same pin wearing
	/// a different hat: the label is what the person reads, and it went stale with the URL.
	/// </summary>
	[Fact]
	public void NoIsoCatalogEntryHardCodesAVersion()
	{
		var catalog = SourceModel.Parsed
			.SelectMany(p => p.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
			.Where(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "IsoCatalog"))
			.ToArray();
		Assert.True(catalog.Length == 1, $"expected one IsoCatalog field, found {catalog.Length}");

		// Every string literal in the initialiser: the labels and the three URL/pattern fields.
		string[] literals = catalog[0].DescendantNodes().OfType<LiteralExpressionSyntax>()
			.Where(l => l.Token.IsKind(SyntaxKind.StringLiteralToken))
			.Select(l => l.Token.ValueText).ToArray();
		Assert.True(literals.Length >= 12,
			$"Only {literals.Length} literal(s) found in IsoCatalog; six entries carry at least two each. "
				+ "Re-read this rule rather than leaving it passing over a scope that has moved.");

		var pinned = new List<string>();
		foreach (string lit in literals)
		{
			Match v = Regex.Match(lit, @"\d+\\?\.\d+");
			if (v.Success) pinned.Add($"\"{Trim120(lit)}\"  pins \"{v.Value}\"");
		}

		Assert.True(pinned.Count == 0,
			"The ISO catalog promises it has no hard-coded versions, and these pin one. An entry that names "
				+ "a version serves that version for ever: \"Get latest\" on Ubuntu was two LTS generations "
				+ "behind. Resolve the version from the server — Kind.TwoStep walks a directory index and "
				+ "NaturalSortKey picks the newest:\n  " + string.Join("\n  ", pinned));
	}

	/// <summary>
	/// The checksum check is offered where an ISO arrives, and nothing claims the app cannot do it.
	///
	/// The hashing loop used to live inside VerifyIsoButton_Click, so it could only ever hash
	/// <c>sourcePath</c> — the file picked on the Create-USB or write-ISO screen. The Download-an-ISO
	/// screen collapses the panel that button sits in, so the one place a multi-gigabyte OS image actually
	/// arrives had nothing to offer; and DlHttpWarn told the person so, in every language: "its contents can
	/// be altered in transit **and there is no checksum check**". The app had the check the whole time, one
	/// screen away.
	///
	/// This is a narrow regression pin, and worth saying so: the first half is derived — the hashing owner
	/// must be reached from both the button and the download — but the second half is a literal guard on one
	/// sentence, because "no string claims a capability the app lacks" is not something a rule can derive.
	/// It is still the half that would have caught the original defect, because the sentence is what made
	/// the gap a lie rather than merely a gap.
	/// </summary>
	[Fact]
	public void TheChecksumCheckIsOfferedWhereAnIsoArrives()
	{
		var owner = SourceModel.Methods().Where(m => m.Name == "HashFileForChecksumAsync").ToArray();
		Assert.True(owner.Length == 1,
			$"Expected one HashFileForChecksumAsync, found {owner.Length}. The user-facing checksum has one "
				+ "owner so that every screen where a file arrives can offer it, not just the screen that "
				+ "happens to own sourcePath.");

		foreach (string caller in new[] { "VerifyIsoButton_Click", "DownloadIsoAsync" })
		{
			var m = SourceModel.Methods().Where(x => x.Name == caller).ToArray();
			Assert.True(m.Length == 1, $"expected one {caller}, found {m.Length}");
			Assert.True(SourceModel.Calls(m[0].Method, "HashFileForChecksumAsync").Any(),
				$"{caller} does not offer the checksum check. A freshly downloaded OS image, about to be "
					+ "written to a disk and booted, is the moment the check means something.");
		}

		// ...and no sentence tells the person the app cannot do what it just did. The table is read here
		// rather than in LocalizationTests so the two halves of one claim stay in one place: the code must
		// offer the check AND the text must not deny it.
		var strings = (Dictionary<string, Dictionary<string, string>>)Mw.Field("Strings");
		var liars = new List<string>();
		foreach (string lang in strings.Keys)
			foreach (var (key, value) in strings[lang])
				if (Regex.IsMatch(value, @"no checksum|without a checksum|checksum check\b.{0,12}\bnone",
						RegexOptions.IgnoreCase))
					liars.Add($"{key} [{lang}]: {Trim120(value)}");
		Assert.True(liars.Count == 0,
			"These say the app does no checksum check, and it does — offered on the Create-USB screen, the "
				+ "write-ISO screen, and after a download:\n  " + string.Join("\n  ", liars));
	}

	/// <summary>
	/// A tool the app runs ELEVATED is never located by searching a tree for its name.
	///
	/// Both downloaded engines live under %LOCALAPPDATA%\DriveForge\Tools — which every program running as
	/// this user can write to — and both are then started elevated. EnsureWimlibAsync says why that matters,
	/// about itself: "at fixed paths rather than by searching the tree for anything named wimlib-imagex.exe
	/// — that search returned the first match in enumeration order, which an attacker chooses."
	///
	/// FindVentoyExe was still doing exactly that: GetFiles(root, "Ventoy2Disk.exe", AllDirectories)
	/// .FirstOrDefault(). A planted copy only has to sort earlier than the real one. The lesson had been
	/// learnt, written down, and applied to one of the two siblings.
	///
	/// So: any recursive search for an executable must reject an ambiguous answer rather than pick one. The
	/// scope is derived from what the search is FOR — a GetFiles call whose pattern names a .exe with
	/// SearchOption.AllDirectories — so a third engine added tomorrow is in it the moment it is written.
	/// </summary>
	[Fact]
	public void AnElevatedToolIsNeverTheFirstMatchOfATreeSearch()
	{
		var searches = new List<(string Where, InvocationExpressionSyntax Call)>();
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (InvocationExpressionSyntax inv in SourceModel.Calls(root, "GetFiles"))
			{
				string text = inv.ToString();
				if (!text.Contains("AllDirectories", StringComparison.Ordinal)) continue;
				if (!Regex.IsMatch(text, @"""[^""]*\.exe""", RegexOptions.IgnoreCase)) continue;
				searches.Add((SourceModel.Where(file, inv), inv));
			}

		Assert.True(searches.Count >= 1,
			"No recursive search for an executable found at all. This rule exists because one was used to "
				+ "locate a binary that is then run elevated; if none is left, re-read it rather than "
				+ "leaving it passing over nothing.");

		var picked = new List<string>();
		foreach (var (where, call) in searches)
		{
			// The statement the search feeds into: taking "the first" of an ambiguous answer is the defect,
			// and counting the matches first is the fix.
			SyntaxNode scope = call.FirstAncestorOrSelf<BlockSyntax>() ?? call.Parent ?? call;
			string around = scope.ToString();
			bool takesFirst = Regex.IsMatch(call.Parent?.ToString() ?? "",
				@"\.\s*(?:FirstOrDefault|First|Single)\s*\(\s*\)");
			// EXACTLY one. A bare "is there at least one" comparison is the same defect with a count in
			// front of it: `found.Length >= 1 ? found[0] : null` still trusts whichever copy enumeration
			// returned. Found by asking what the mutation for this rule would have to be.
			bool countsFirst = Regex.IsMatch(around, @"\.\s*Length\s*==\s*1\b")
				|| Regex.IsMatch(around, @"\.\s*Count\(\)\s*==\s*1\b");
			if (takesFirst || !countsFirst)
				picked.Add($"{where}  {Trim120(call.ToString())}");
		}

		Assert.True(picked.Count == 0,
			"This locates an executable by searching a tree and then trusts whichever copy enumeration "
				+ "returned. The folder is writable by every program running as this user and the binary is "
				+ "started elevated, so a planted copy only has to sort earlier. Count the matches and refuse "
				+ "an ambiguous answer — the caller re-extracts from the official download:\n  "
				+ string.Join("\n  ", picked));
	}

	/// <summary>
	/// An option nobody can see is neither applied nor announced.
	///
	/// ModeBox_SelectionChanged decides each option's Visibility from the selected task, and a flow that
	/// reads <c>SomeCheck.IsChecked</c> while its own task collapses that checkbox is reading whatever the
	/// person last left ticked on another screen — or in an earlier session, because the setting is saved and
	/// restored. The file names this defect twice and fixes it twice, in its own words: EjectWhenDoneCheck
	/// ("the write flow used to read a checkbox nobody could see") and BitLockerCheck ("Clear the option when
	/// it cannot apply, so every downstream read is honest by construction").
	///
	/// Measured 2026-10-12 across all twelve conditionally-visible option checkboxes: eleven are read only
	/// where their own task displays them. The two bypasses are read by FOUR flows that cannot show them —
	/// the portable clone, both restores, and the VHDX export, whose panel collapses the entire left column.
	/// The sharpest part is where the repair already was: ConfirmOperationSummary's own comment says
	/// "Visibility is the honest test: it is exactly 'could the user see and tick this for what they are
	/// about to do'" — and it sits directly BELOW the two lines that used `!isClone` instead, and above the
	/// three that use it. The lesson was written down between the broken lines and the fixed ones.
	///
	/// Two halves. The first is narrow and exact, and says why: these are the only two options with
	/// cross-screen readers, so they are the only two that need an owner, and nothing may bypass it. The
	/// second generalises to the place the next option will be added: anything the confirmation LISTS is
	/// either visibility-tested there or cleared when it cannot apply — those being the two disciplines this
	/// file already uses, and between them they are what makes a read honest.
	/// </summary>
	[Fact]
	public void AnOptionNobodyCanSeeIsNeitherAppliedNorAnnounced()
	{
		// The one owner, and the fact that it asks about the screen at all.
		var offered = SourceModel.Methods().Where(m => m.Name == "Offered").ToArray();
		Assert.True(offered.Length == 1, $"expected one Offered helper, found {offered.Length}");
		Assert.True(offered[0].Method.ToString().Contains("IsVisible", StringComparison.Ordinal),
			"Offered must ask whether the checkbox is ON SCREEN, not only whether it is ticked. IsVisible and "
				+ "not Visibility: the panel views collapse the whole left column and never touch ModeBox, so "
				+ "only the ancestor-aware test answers for them.");

		foreach (string owner in new[] { "BypassRequirementsWanted", "BypassAccountWanted" })
		{
			var found = SourceModel.Parsed
				.SelectMany(p => p.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
				.Where(d => d.Identifier.Text == owner).ToArray();
			Assert.True(found.Length == 1, $"expected one {owner} property, found {found.Length}");
			Assert.True(found[0].ToString().Contains("Offered(", StringComparison.Ordinal),
				$"{owner} must go through Offered rather than asking its own way — one owner, so a change to "
					+ "what 'offered' means reaches every reader.");
		}

		// Nothing reads those two checkboxes behind the owners' backs — except persistence, which stores
		// what the person set; "was this offered for THIS task" is a question for the read, not for the file.
		// Nothing reads those two checkboxes behind the owners' backs - except persistence, which stores
		// what the person set; "was this offered for THIS task" is a question for the read, not for the file.
		// Both access forms, via the one IsOptionRead above: this rule used to carry its own copy of that
		// test, and a second copy of the comment explaining why both forms matter.
		var reads = new List<(SyntaxNode Node, string Target, string File)>();
		foreach (var (file, root) in SourceModel.Parsed)
		{
			foreach (SyntaxNode n in root.DescendantNodes())
				if (IsOptionRead(n, out string box)) reads.Add((n, box, file));
		}

		var behind = new List<string>();
		foreach (var (node, rawTarget, file) in reads)
			{
				string target = rawTarget.Trim();
				if (target is not ("BypassRequirementsCheck" or "BypassAccountCheck")) continue;
				// An assignment is persistence restoring the saved value.
				if (node.Parent is AssignmentExpressionSyntax a && a.Left == node) continue;
				SyntaxNode ma = node;
				string method = OwningMethodName(ma);
				if (method is "SaveUserSettings" or "BypassRequirementsWanted" or "BypassAccountWanted") continue;
				if (method.StartsWith("Load", StringComparison.Ordinal)
						|| method.StartsWith("Apply", StringComparison.Ordinal) && method.Contains("Settings", StringComparison.Ordinal))
					continue;
				behind.Add($"{SourceModel.Where(file, ma)}  in {method}");
			}
		Assert.True(behind.Count == 0,
			"These read a cross-screen option directly instead of through its owner, so a tick left on the "
				+ "Create-USB screen reaches a clone, a restore or an export that never offered it:\n  "
				+ string.Join("\n  ", behind));

		// ...and every option the confirmation lists is either visibility-tested there, or cleared when it
		// cannot apply. Those are the two disciplines this file uses; a third option added without either is
		// the defect this rule exists for.
		var confirm = SourceModel.Methods().Where(m => m.Name == "ConfirmOperationSummary").ToArray();
		Assert.True(confirm.Length == 1, $"expected one ConfirmOperationSummary, found {confirm.Length}");
		string allCode = string.Concat(SourceModel.Parsed.Select(p => p.Root.ToString()));

		var announced = new List<string>();
		int listed = 0;
		foreach (IfStatementSyntax ifs in confirm[0].Method.DescendantNodes().OfType<IfStatementSyntax>())
		{
			string cond = ifs.Condition.ToString();
			Match box = Regex.Match(cond, @"\b([A-Za-z0-9_]+Check)\b");
			if (!box.Success) continue;
			listed++;
			string name = box.Groups[1].Value;
			// One test for all of them. The five hand-written mode conditions this replaced had to be kept
			// in step with Visibility lines two hundred lines away, and two of the five were not.
			if (!Regex.IsMatch(cond, @"\bOffered\s*\(")) announced.Add($"{name}: {Trim120(cond)}");
		}

		Assert.True(listed >= 8,
			$"Only {listed} option(s) read in ConfirmOperationSummary's conditions; there were ten. Re-read "
				+ "this rule rather than leaving it passing over a scope that has moved.");
		Assert.True(announced.Count == 0,
			"The last dialog before the disk is erased lists these without asking whether the person could "
				+ "see and tick them for THIS task, so it promises work the run will not do. Ask Offered(...) "
				+ "like every other option here:\n  " + string.Join("\n  ", announced));
	}

	/// <summary>
	/// RULE — an option the run drops is named where the person looks.
	///
	/// Every option in the last dialog before the disk is erased is a promise, and a run can withdraw one for a
	/// reason the person cannot see: too little space left over, a partition layout out of primaries, an engine
	/// that verifies something other than what it wrote. _dataPartitionSkipped existed for exactly this and was
	/// carried out to the completion dialog — by ONE of the two flows that can withdraw it. The clone announces
	/// four options, can withdraw three, and had neither the flag nor the call, so all three went to the log and
	/// the dialog said "Finished."
	///
	/// Three parts. The first is the shape that caused it: the leftover-space rule existed in two copies and the
	/// answer to the person grew in only one of them. The second is the dialogs: a flow that can name a skipped
	/// hive edit can name a withdrawn option — the two answer the same question and are cleared by the same line.
	/// The third points forward: the announced set is a table here, so a new option in the confirmation fails
	/// this test until someone has decided where its withdrawal is reported.
	/// </summary>
	[Fact]
	public void AnOptionTheRunDropsIsNamedWhereThePersonLooks()
	{
		// --- one owner for the leftover-space decision, and for its refusal ---------------------------
		// Named for what it is, not for the number: 8 GiB is also the headroom in the Windows-size estimate
		// two methods away, so keying on the constant alone made this rule fire on arithmetic it has no
		// opinion about. What must be unique is the quantity — a leftover a caller works out for itself is
		// a second copy of the decision, which is how this defect existed. A SUBTRACTION, because the name
		// alone collides with the disk-cleanup page's "what is left of this category", which this rule has
		// no opinion about either.
		var ownLeftover = new List<string>();
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (VariableDeclaratorSyntax v in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
				if (v.Identifier.Text == "leftover"
						&& v.Initializer?.Value is BinaryExpressionSyntax sub
						&& sub.OperatorToken.IsKind(SyntaxKind.MinusToken)
						&& OwningMethodName(v) != "DataPartitionFitsInLeftover")
					ownLeftover.Add(OwningMethodName(v));
		Assert.True(ownLeftover.Count == 0,
			"Nobody works out what is left over after Windows for themselves — they ask "
				+ "DataPartitionFitsInLeftover, which hands it back. A caller that subtracts its own has a second "
				+ "copy of the decision, and that is how the install grew an answer for the person while the "
				+ "clone, 660 lines away, kept only a log line. Found in: "
				+ string.Join(", ", ownLeftover.Distinct()));

		var floors = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("leftover >= 8L * 1024 * 1024 * 1024", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(floors.Length == 1 && floors[0] == "DataPartitionFitsInLeftover",
			"...and only it holds the floor that decides. Found in: " + string.Join(", ", floors));

		var refusals = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("NotePromiseWithheld(L(\"MbDataPartSkipped\"))", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(refusals.Length == 1 && refusals[0] == "DataPartitionFitsInLeftover",
			"...and so does the refusal that goes with it. Found in: " + string.Join(", ", refusals));

		// Nobody keeps their own tally. One list, one way in, one way out — a second bool is how this started.
		var touch = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("_promisesWithheld", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		var allowed = new[] { "BuildWithheldPromiseSummary", "ConfirmOperationSummary",
			"CreateWindowsToGoFromImageAsync", "NotePromiseWithheld" };
		Assert.True(touch.SequenceEqual(allowed),
			"_promisesWithheld is named outside its own accessors and the two per-run resets; record a withheld "
				+ "promise through NotePromiseWithheld so every dialog that asks gets all of them:\n  expected: "
				+ string.Join(", ", allowed) + "\n  found:    " + string.Join(", ", touch));

		// --- a dialog that can name a skipped hive edit can name a withdrawn option -------------------
		var silent = new List<string>();
		int hiveDialogs = 0;
		foreach (var (file, root) in SourceModel.Parsed)
		{
			foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
				if (ma.Name.Identifier.Text != "Show") continue;
				if (!ma.Expression.ToString().EndsWith("MessageBox", StringComparison.Ordinal)) continue;
				string args = inv.ArgumentList.ToString();
				if (!args.Contains("BuildSkippedHiveSummary()", StringComparison.Ordinal)) continue;
				hiveDialogs++;
				if (!args.Contains("BuildWithheldPromiseSummary()", StringComparison.Ordinal))
					silent.Add($"{SourceModel.Where(file, inv)}  in {OwningMethodName(inv)}");
			}
		}
		Assert.True(hiveDialogs == 2,
			$"Expected the two completion dialogs that report a skipped hive edit; found {hiveDialogs}. Re-read "
				+ "this rule rather than leaving it passing over a scope that has moved.");
		Assert.True(silent.Count == 0,
			"These dialogs name the registry edits the run could not make and say nothing about the options it "
				+ "dropped, which is the same question with the same answer. Add BuildWithheldPromiseSummary():\n  "
				+ string.Join("\n  ", silent));


		// Each withheld promise is said once, and only through the owner: the keys exist for this and for
		// nothing else, so a sentence used as bare dialog text somewhere would be a promise answered in one
		// place and not the other. This is also what pins the two halves nobody else can see - the layout
		// running out of primaries, and the verification the Microsoft engine does not do.
		foreach (string key in new[] { "MbDataPartSkipped", "MbExtraPartsCapped", "MbVerifySkippedDism" })
		{
			var uses = new List<string>();
			foreach (var (file, root) in SourceModel.Parsed)
				foreach (InvocationExpressionSyntax inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
				{
					if (inv.Expression.ToString() != "L") continue;
					if (inv.ArgumentList.ToString() != $"(\"{key}\")") continue;
					SyntaxNode? note = inv.Ancestors().OfType<InvocationExpressionSyntax>()
						.FirstOrDefault(a => a.Expression.ToString() == "NotePromiseWithheld");
					uses.Add(note == null ? $"{SourceModel.Where(file, inv)} NOT through NotePromiseWithheld"
										  : $"{SourceModel.Where(file, inv)} ok");
				}
			Assert.True(uses.Count == 1 && uses[0].EndsWith("ok", StringComparison.Ordinal),
				$"{key} is the answer to one withdrawn promise and belongs in exactly one "
					+ "NotePromiseWithheld call. Found:\n  " + string.Join("\n  ", uses));
		}

		// --- what the confirmation promises, somebody has decided how to withdraw ----------------------
		var confirm = SourceModel.Methods().Where(m => m.Name == "ConfirmOperationSummary").ToArray();
		Assert.True(confirm.Length == 1, $"expected one ConfirmOperationSummary, found {confirm.Length}");

		var announced = new SortedSet<string>(StringComparer.Ordinal);
		foreach (InvocationExpressionSyntax inv in confirm[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>())
		{
			if (inv.Expression.ToString() != "opts.Add") continue;
			Match k = Regex.Match(inv.ArgumentList.ToString(), @"L\(""([A-Za-z0-9_]+)""\)");
			Assert.True(k.Success, $"an announced option that is not a key: {Trim120(inv.ToString())}");
			announced.Add(k.Groups[1].Value);
		}

		// Each one, and where its withdrawal is reported. The right-hand side is the point of the table: an
		// option whose answer is "nothing can stop it" has to be SAID to be that, once, here.
		var accountedFor = new SortedSet<string>(StringComparer.Ordinal)
		{
			"CoOptBitLocker",    // manage-bde refuses -> MbCloneBitlockerFailed / MbUsbBitlockerFailed
			"CoOptDataPart",     // no room left over  -> MbDataPartSkipped
			"CoOptOtherParts",   // layout out of primaries -> MbExtraPartsCapped
			"CoOptWholeDisk",    // the same cap, against the internal clone's flat promise
			"CoOptVerify",       // the Microsoft engine checks its capture, not the write -> MbVerifySkippedDism
			// These four are edits to a hive mounted from the target, or to the image itself. They cannot be
			// quietly dropped: a hive that would not open is already named by BuildSkippedHiveSummary, and
			// anything else throws and ends the run as a failure.
			"CoOptBypassReq", "CoOptLocalAcct", "DebloatCheck", "CoOptCompact",
			// Driver injection reports the COUNT it managed, in the same dialog, via BuildDriverDebloatSummary.
			"AddAllDriversCheck", "AddNetworkDriversCheck",
		};
		Assert.True(announced.SetEquals(accountedFor),
			"The confirmation's promises and the promises this file knows how to withdraw have drifted apart. "
				+ "Decide where the new one's withdrawal is reported (NotePromiseWithheld) before listing it, or "
				+ "say here that nothing can withdraw it:\n  announced:    " + string.Join(", ", announced)
				+ "\n  accounted for: " + string.Join(", ", accountedFor));
	}

	/// <summary>
	/// RULE — a run does not change its own parameters halfway through.
	///
	/// SetBusy disables four buttons. It does not disable the Options panel or the task list, so every checkbox
	/// stays live for the whole run — and a clone runs for an hour. RunExperimentalFullRootUsbCloneAsync is the
	/// one flow that answers this: it snapshots every option it depends on before any work starts, with seven
	/// lines of comment saying why ("a clone that began as portable USB could finish applying internal-disk
	/// registry settings"). One line still went back to the live control afterwards, and it was the engine
	/// choice — the comment's own example of the most consequential read of the lot — 115 lines and a VSS
	/// snapshot later, after the antivirus prompt had already been answered for the other engine. And one more
	/// escaped by being one method down: EnableBitLockerAsync asked the screen itself, at 96%.
	///
	/// WIDENED 2026-10-13 to CreateWindowsToGoFromImageAsync, which had six live reads and was the worse of
	/// the two: measured against the source order, CompactImageCheck was read AFTER diskpart had already
	/// partitioned the disk, and both driver boxes and both BitLocker boxes AFTER dism had finished applying
	/// the image. Ticking "Add all drivers" while watching the progress bar injected drivers the confirmation
	/// never promised; unticking BitLocker dropped encryption it did.
	///
	/// WIDENED again 2026-10-14 to WriteIsoImageToManyFlowAsync, which was not reading its options late -
	/// it was reading them TWICE: once to build the option list its confirmation shows, once again as
	/// arguments to RunImageWriteQueueAsync, with a duplicator window and a per-target identity re-check in
	/// between. One read now serves both.
	///
	/// SCOPE, measured and not tallied, so this is not mistaken for a guarantee about the app: two write
	/// flows have no snapshot block, and the measurement says they need none - every option read in them
	/// happens before the work starts, which is the only thing a block is for. What this rule checks for
	/// them is therefore the POSITION of each read, at the end of this test.
	///
	/// What that replaced, and why it mattered: a plain `\bIsChecked\b` count over each method's text
	/// (5 / 3 / 4). A count says nothing about position, and it misread all three flows it named.
	/// StartButton_Click's "five, the latest 211 lines in" is four reads in the interactive phase before
	/// anything is written, plus EjectWhenDoneCheck read after the run has already finished - and ejecting
	/// is not one of the promises ConfirmOperationSummary lists, so the current intent is the right one to
	/// read there. WriteIsoImageFlowAsync passes both of its boxes INTO the queue call as arguments, which
	/// is a snapshot taken at the call instead of at the top. Obeying the count would have produced three
	/// snapshot blocks nobody needed, and silenced the rule while the measurement stayed wrong.
	/// </summary>
	[Fact]
	public void ARunDoesNotChangeItsOwnParametersHalfwayThrough()
	{

		// Each covered flow with the floor its own block must clear. The floor is what stops the rule being
		// blinded: every snapshot line is written with `?.`, so disarming the null-conditional arm below drops
		// the count to zero and fails here rather than passing over nothing.
		var covered = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			["RunExperimentalFullRootUsbCloneAsync"] = 8,
			["CreateWindowsToGoFromImageAsync"] = 6,
			["WriteIsoImageToManyFlowAsync"] = 2,
		};
		foreach (var (flowName, floor) in covered.Select(c => (c.Key, c.Value)))
		{
		var clone = SourceModel.Methods().Where(m => m.Name == flowName).ToArray();
		Assert.True(clone.Length == 1, $"expected one {flowName}, found {clone.Length}");

		var live = new List<string>();
		int snapshotted = 0;
		foreach (SyntaxNode n in clone[0].Method.DescendantNodes())
		{
			if (!IsOptionRead(n, out string box)) continue;

			// The snapshot: the initializer of a local called opt-something. Anything else is a read of the
			// screen while the operation the person approved is already running.
			VariableDeclaratorSyntax? v = n.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
			if (v != null && v.Identifier.Text.StartsWith("opt", StringComparison.Ordinal)) { snapshotted++; continue; }
			live.Add($"{box}.IsChecked — {Trim120(n.Parent?.ToString() ?? n.ToString())}");
		}
		// This floor is what stops the rule being blinded: every line of the snapshot block is written with
		// `?.`, so disarming the null-conditional arm above drops this count to zero and fails here. Measured
		// — that mutation goes red on this line, which is the property last session's version of this check
		// did not have, where a blind arm simply passed.
		Assert.True(snapshotted >= floor,
			$"Only {snapshotted} option(s) snapshotted in {flowName}; there were {floor}. Re-read this rule "
				+ "rather than leaving it passing over a scope that has moved.");
		Assert.True(live.Count == 0,
			$"{flowName} reads these off the screen after its snapshot block, so the operation can change the "
				+ "parameters the person approved while it runs. Snapshot it at the top with the others:\n  "
				+ string.Join("\n  ", live));
		}

		// ...and nothing it calls asks the screen on its behalf. EnableBitLockerAsync is the one that did: it
		// runs at 96% of the clone and read the "let me remove the drive early" box itself, which is how an
		// option escapes a snapshot block without appearing in the method that has one.
		var blk = SourceModel.Methods().Where(m => m.Name == "EnableBitLockerAsync").ToArray();
		Assert.True(blk.Length == 1, $"expected one EnableBitLockerAsync, found {blk.Length}");
		Assert.True(!blk[0].Method.ToString().Contains("IsChecked", StringComparison.Ordinal),
			"EnableBitLockerAsync runs at the very end of a clone and must take its options as arguments; "
				+ "reading a checkbox here puts an hour between the person's choice and its use.");

		// The scope, measured. Two write flows have no snapshot block and need none: every option read in
		// them happens before the work starts. So what is checked here is each read's POSITION, with a floor
		// on how many were found - the floor is what fails if OptionRead above is ever blinded.

		// WriteIsoImageFlowAsync hands its writing to RunImageWriteQueueAsync, so a read is in time if it is
		// lexically before that call OR is one of its arguments - an argument is evaluated before the callee
		// runs, which is exactly what a snapshot is.
		var queue = SourceModel.Methods().Where(m => m.Name == "WriteIsoImageFlowAsync").ToArray();
		Assert.True(queue.Length == 1, $"expected one WriteIsoImageFlowAsync, found {queue.Length}");
		var queueCall = queue[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => i.Expression.ToString().EndsWith("RunImageWriteQueueAsync", StringComparison.Ordinal)).ToArray();
		Assert.True(queueCall.Length == 1,
			$"WriteIsoImageFlowAsync should hand its writing to exactly one RunImageWriteQueueAsync call; "
				+ $"found {queueCall.Length}.");
		var afterWorkStarts = new List<string>();
		int beforeWorkStarts = 0;
		foreach (SyntaxNode n in queue[0].Method.DescendantNodes())
		{
			if (!IsOptionRead(n, out string box)) continue;
			if (n.SpanStart < queueCall[0].SpanStart || queueCall[0].Span.Contains(n.Span))
			{ beforeWorkStarts++; continue; }
			afterWorkStarts.Add($"{box}.IsChecked - {Trim120(n.Parent?.ToString() ?? n.ToString())}");
		}
		Assert.True(beforeWorkStarts >= 3,
			$"Only {beforeWorkStarts} option read(s) reach the queue call in WriteIsoImageFlowAsync; there were "
				+ "three. Re-read this rule rather than leaving it passing over a scope that has moved.");
		Assert.True(afterWorkStarts.Count == 0,
			"WriteIsoImageFlowAsync reads these after RunImageWriteQueueAsync has begun writing, so the run can "
				+ "change the parameters the person approved while it runs:\n  "
				+ string.Join("\n  ", afterWorkStarts));

		// StartButton_Click reads everything in the pre-write phase, which ends at the last confirmation -
		// except EjectWhenDoneCheck, read when the run is already over. That one name is the WHOLE exception,
		// so a second read creeping into the success tail fails here.
		var starter = SourceModel.Methods().Where(m => m.Name == "StartButton_Click").ToArray();
		Assert.True(starter.Length == 1, $"expected one StartButton_Click, found {starter.Length}");
		var confirms = starter[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => i.Expression.ToString().EndsWith("ConfirmOperationSummary", StringComparison.Ordinal)).ToArray();
		Assert.True(confirms.Length == 2,
			$"StartButton_Click confirms once per branch - two calls; found {confirms.Length}.");
		int preWriteEnds = confirms.Max(c => c.Span.End);
		var afterTheRun = new List<string>();
		int inPreWrite = 0;
		foreach (SyntaxNode n in starter[0].Method.DescendantNodes())
		{
			if (!IsOptionRead(n, out string box)) continue;
			if (n.SpanStart < preWriteEnds) { inPreWrite++; continue; }
			afterTheRun.Add(box);
		}
		Assert.True(inPreWrite >= 4,
			$"Only {inPreWrite} option read(s) in StartButton_Click's pre-write phase; there were four. "
				+ "Re-read this rule rather than leaving it passing over a scope that has moved.");
		Assert.True(afterTheRun.SequenceEqual(new[] { "EjectWhenDoneCheck" }),
			"StartButton_Click reads these after the run has finished. EjectWhenDoneCheck is allowed there: "
				+ "ejecting is a post-run action the confirmation never promises, so the current intent is the "
				+ "right one to read. Anything else belongs before the confirmation:\n  "
				+ string.Join("\n  ", afterTheRun));
	}

	/// <summary>
	/// RULE — what the run records about a drive reaches its report.
	///
	/// The image writer is the most carefully built flow in the app: typed outcomes instead of re-reading
	/// stopRequested, a per-drive core that never throws so one dead stick cannot abandon the queue behind it,
	/// a read-back opened with FILE_FLAG_NO_BUFFERING so a fake-capacity drive cannot verify out of the OS
	/// cache. Everything it learns about a drive it writes down. Three things it wrote down never reached the
	/// person:
	///
	///   - DriveWriteOutcome.Rejected and DriveWriteResult.Reason were declared for the drives the run refuses,
	///     and BOTH report paths already rendered them — nothing ever produced one. So eight ticked drives with
	///     two refused ended as "6 of 6 drives written", and the two were in no list the person could still see.
	///   - LeftOffline ("both online attempts failed, so this disk really is still offline — record it instead
	///     so the run can say which one") reached the multi-drive report and not the one-drive dialog, which is
	///     the common case: "Done… plug it into the target PC", information icon, over a drive Windows hides.
	///   - ImageTargetRejection says it exists so the rule cannot drift, and the DiskBox target was vetted by a
	///     copy of two of its five tests. The test the copy lacked was the ambiguity refusal, which greys out
	///     two indistinguishable drives in the picker while the one chosen above them was written to by NUMBER.
	///
	/// So: every outcome is produced by someone and named by the reporter; everything the multi-drive report
	/// reads off a result, the one-drive dialog reads too; and the target rule has exactly one owner.
	/// </summary>
	[Fact]
	public void WhatTheRunRecordsAboutADriveReachesItsReport()
	{
		var reporter = SourceModel.Methods().Where(m => m.Name == "ShowImageWriteResult").ToArray();
		Assert.True(reporter.Length == 1, $"expected one ShowImageWriteResult, found {reporter.Length}");
		string reporterText = reporter[0].Method.ToString();

		// --- every outcome is produced by someone, and named by the reporter -------------------------
		var outcomeEnum = SourceModel.Parsed.SelectMany(p => p.Root.DescendantNodes().OfType<EnumDeclarationSyntax>())
			.Where(e => e.Identifier.Text == "DriveWriteOutcome").ToArray();
		Assert.True(outcomeEnum.Length == 1, $"expected one DriveWriteOutcome enum, found {outcomeEnum.Length}");
		var values = outcomeEnum[0].Members.Select(m => m.Identifier.Text).ToArray();
		Assert.True(values.Length >= 9,
			$"Only {values.Length} outcome(s); there were nine. Re-read this rule rather than leaving it passing "
				+ "over a scope that has moved.");

		// PRODUCED means something BUILDS a result with it: the value passed as an argument to a call or to a
		// `new DriveWriteResult(...)`. Not merely named — `Wrote` and `Ok` list four of these outcomes in their
		// own predicates, so "appears in some member other than the reporter" called every one of them produced
		// and the rule went green on an outcome whose only producer had just been taken away. Found by mutation.
		// Keyed on the two things that BUILD a result - the local `Result(...)` helper and `new DriveWriteResult`
		// - and then any outcome named anywhere inside one of those, because the argument is often a
		// ConditionalExpression: `Result(stopRequested ? NotAttempted : IdentityChanged)`. Matching the
		// argument's own text called IdentityChanged unproduced, which is this file's oldest blind spot
		// (see LiteralPartsOf) showing up in a third rule.
		var built = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var (file, method, name) in SourceModel.Methods())
		{
			if (name == "ShowImageWriteResult") continue;
			foreach (SyntaxNode maker in method.DescendantNodes())
			{
				bool isMaker = maker switch
				{
					InvocationExpressionSyntax inv => inv.Expression.ToString() == "Result",
					ObjectCreationExpressionSyntax oc => oc.Type.ToString() == "DriveWriteResult",
					_ => false
				};
				if (!isMaker) continue;
				foreach (MemberAccessExpressionSyntax ma in maker.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
					if (ma.Expression.ToString() == "DriveWriteOutcome") built.Add(ma.Name.Identifier.Text);
			}
		}
		var unproduced = values.Where(v => !built.Contains(v)).ToList();
		var unreported = values.Where(v => !reporterText.Contains("DriveWriteOutcome." + v, StringComparison.Ordinal)).ToList();
		Assert.True(unproduced.Count == 0,
			"These outcomes are declared and rendered and nothing ever produces one, so the case they describe "
				+ "is silently missing from the report: " + string.Join(", ", unproduced));
		Assert.True(unreported.Count == 0,
			"These outcomes can happen to a drive and the report has no words for them: "
				+ string.Join(", ", unreported));

		// --- the one-drive dialog reads what the multi-drive report reads ----------------------------
		// The reporter's two halves are split by `DriveWriteResult r = results[0];`: above it the loop over `x`,
		// below it the single dialog over `r`. Compare the two sets of members by name.
		var split = reporter[0].Method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
			.Where(v => v.Identifier.Text == "r").ToArray();
		Assert.True(split.Length == 1, $"expected one `r` in the reporter, found {split.Length}");
		int boundary = split[0].SpanStart;

		var multi = new SortedSet<string>(StringComparer.Ordinal);
		var single = new SortedSet<string>(StringComparer.Ordinal);
		foreach (MemberAccessExpressionSyntax ma in reporter[0].Method.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
		{
			string who = ma.Expression.ToString();
			if (who == "x" && ma.SpanStart < boundary) multi.Add(ma.Name.Identifier.Text);
			else if (who == "r" && ma.SpanStart > boundary) single.Add(ma.Name.Identifier.Text);
		}
		Assert.True(multi.Count >= 5,
			$"Only {multi.Count} member(s) read in the enumerating report; there were seven. Re-read this rule.");

		// What a ROW needs and a dialog does not: the row has to name its drive, because eight of them scroll
		// past; the one-drive dialog is about the drive the person just picked and already names it in its own
		// sentence. Everything else is something the run LEARNED, and belongs in both.
		var rowOnly = new SortedSet<string>(StringComparer.Ordinal)
		{ "DiskNumber", "FriendlyName", "Size", "Identity", "Ok" };
		var dropped = multi.Where(m => !single.Contains(m) && !rowOnly.Contains(m)).ToArray();
		Assert.True(dropped.Length == 0,
			"The enumerating report tells the person these and the one-drive dialog does not, so the commonest "
				+ "case is the one that says least: " + string.Join(", ", dropped)
				+ "\n  enumerating report: " + string.Join(", ", multi)
				+ "\n  one-drive dialog:   " + string.Join(", ", single));

		// --- one owner for "can this disk take this image" -------------------------------------------
		var sizeRule = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("(isoSize + 4095) / 4096 * 4096 > disk.Size", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(sizeRule.Length == 1 && sizeRule[0] == "ImageTargetRejection",
			"The padded-size test belongs to ImageTargetRejection and nowhere else - a second copy is how the "
				+ "DiskBox target came to be vetted by two of its five tests. Found in: " + string.Join(", ", sizeRule));

		var srcRule = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("PhysicalDiskOfPath(src) == disk.Number", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(srcRule.Length == 1 && srcRule[0] == "ImageTargetRejection",
			"...and so does the source-on-target test. Found in: " + string.Join(", ", srcRule));

		// The fail-closed ambiguity refusal is reached THROUGH the owner, so a caller cannot forget to chain it.
		var ambig = SourceModel.Methods()
			.Where(m => m.Name != "AmbiguousTargetReason")
			.Where(m => m.Method.ToString().Contains("AmbiguousTargetReason(", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(ambig.Length == 1 && ambig[0] == "ImageTargetRejection",
			"AmbiguousTargetReason is called from ImageTargetRejection and nowhere else: as a separate call every "
				+ "caller had to remember to chain it, and the one drive the app always writes to went through "
				+ "neither. Called from: " + string.Join(", ", ambig));

		// ...and the flow that writes to one drive asks the owner rather than its own questions.
		var front = SourceModel.Methods().Where(m => m.Name == "WriteIsoImageFlowAsync").ToArray();
		Assert.True(front.Length == 1, $"expected one WriteIsoImageFlowAsync, found {front.Length}");
		Assert.True(front[0].Method.ToString().Contains("ImageTargetRejection(disk, src, isoSize)", StringComparison.Ordinal),
			"The single-drive flow must vet its target through ImageTargetRejection; its own inline copy of two "
				+ "of those tests is the defect this rule exists for.");
	}

	/// <summary>
	/// RULE — a finished run shows its own result before anything else asks for anything.
	///
	/// Found by driving the duplicator: after the write finished, an owned window titled "Susține DriveForge"
	/// appeared and the status line never reached Ready, for the whole eight and a half minutes the harness
	/// waited. The run's own report was behind the support prompt, because MaybeOfferDonation put its
	/// MessageBox up inside NotifyOperationDone — which every one of fifteen flows calls immediately BEFORE
	/// showing its result. The two flows that also asked AFTER their dialog say what the intent was; their
	/// second ask is also why _successCount, which is SAVED, advanced two per run, so a prompt meant for
	/// "the 1st and every 5th" arrived on every other run.
	///
	/// And one of my own, from this screen's other fix: the refused drives were appended to `results` after
	/// the chime read it, so a run that turned a ticked drive away still rang the success sound.
	/// </summary>
	[Fact]
	public void AFinishedRunShowsItsOwnResultFirst()
	{
		// --- the queue counts the refused drives before anything reads the outcome ---------------------
		var queue = SourceModel.Methods().Where(m => m.Name == "RunImageWriteQueueAsync").ToArray();
		Assert.True(queue.Length == 1, $"expected one RunImageWriteQueueAsync, found {queue.Length}");

		var appends = queue[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => i.ToString().Contains("results.AddRange(refused)", StringComparison.Ordinal)).ToArray();
		Assert.True(appends.Length == 1, $"expected one results.AddRange(refused), found {appends.Length}");
		var readers = queue[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => i.Expression.ToString() is "NotifyOperationDone" or "ShowImageWriteResult").ToArray();
		Assert.True(readers.Length >= 2,
			$"Only {readers.Length} reader(s) of the outcome in the queue; there were three. Re-read this rule.");
		var tooLate = readers.Where(r => r.SpanStart < appends[0].SpanStart)
			.Select(r => Trim120(r.ToString())).ToArray();
		Assert.True(tooLate.Length == 0,
			"These read the run's outcome before the drives it refused were added to it, so a run that turned a "
				+ "ticked drive away still counts as a clean success:\n  " + string.Join("\n  ", tooLate));

		// --- the support prompt is armed here and shown somewhere else ---------------------------------
		var offer = SourceModel.Methods().Where(m => m.Name == "MaybeOfferDonation").ToArray();
		Assert.True(offer.Length == 1, $"expected one MaybeOfferDonation, found {offer.Length}");
		Assert.True(!offer[0].Method.ToString().Contains("MessageBox.Show", StringComparison.Ordinal),
			"MaybeOfferDonation must not show its dialog itself: every flow asks it immediately BEFORE showing "
				+ "its own result, so a MessageBox here stands in front of the outcome of the run.");

		// Asked from ONE place. Two flows used to ask again after their dialog, which is what advanced the
		// saved counter two per run.
		var askers = SourceModel.Methods()
			.Where(m => m.Name != "MaybeOfferDonation")
			.Where(m => m.Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
				.Any(i => i.Expression.ToString() == "MaybeOfferDonation"))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(askers.SequenceEqual(new[] { "NotifyOperationDone" }),
			"The support offer is asked for in one place, because it is throttled on a counter that is saved to "
				+ "disk: a second ask in the same run advances it twice. Asked from: " + string.Join(", ", askers));

		// ...and only that one counter write.
		var counters = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("_successCount++", StringComparison.Ordinal))
			.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(counters.SequenceEqual(new[] { "MaybeOfferDonation" }),
			"_successCount is advanced in one place. Found in: " + string.Join(", ", counters));

		// --- and the deferred shower asks whether one of ours is up, first ----------------------------
		var shower = SourceModel.Methods().Where(m => m.Name == "ShowSupportOfferWhenIdleAsync").ToArray();
		Assert.True(shower.Length == 1, $"expected one ShowSupportOfferWhenIdleAsync, found {shower.Length}");
		string body = shower[0].Method.ToString();
		Assert.True(body.Contains("MbSupportAsk", StringComparison.Ordinal),
			"the deferred shower is where the prompt is shown");
		Assert.True(body.Contains("OwnWindowIsUp(", StringComparison.Ordinal),
			"ShowSupportOfferWhenIdleAsync must ask OwnWindowIsUp before showing: without it the prompt lands on "
				+ "top of the report again, which is the defect this rule exists for.");

		// Both kinds of window, because the app shows both: a WPF ShowDialog pushes a modal frame, a Win32
		// MessageBox does not, and the duplicator's report is the first kind while most reports are the second.
		var probe = SourceModel.Methods().Where(m => m.Name == "OwnWindowIsUp").ToArray();
		Assert.True(probe.Length == 1, $"expected one OwnWindowIsUp, found {probe.Length}");
		string probeBody = probe[0].Method.ToString();
		foreach (string half in new[] { "IsThreadModal", "GetActiveWindow" })
			Assert.True(probeBody.Contains(half, StringComparison.Ordinal),
				$"OwnWindowIsUp must test {half}: one of the two kinds of window this app shows is invisible to "
					+ "the other test.");
	}

	/// <summary>
	/// RULE — an ISO catalog pattern can express a point release with any number of groups.
	///
	/// The sibling rule above forbids a PINNED version. This one forbids a pattern that cannot
	/// EXPRESS one, which is the same bug arriving by the other door and is how half of a real user
	/// report on 2026-10-13 was still true after the pin was gone.
	///
	/// Shipped v4.4.5 indexed https://releases.ubuntu.com/24.04/ with
	/// "ubuntu-24\.04(?:\.\d+)?-desktop-amd64\.iso". That index CONTAINS
	/// ubuntu-24.04.5.1-desktop-amd64.iso — measured over the real index — but "(?:\.\d+)?" admits ONE
	/// trailing group, so a four-group version cannot match it, and the app handed out 24.04.4 from a
	/// directory that already held .5.1. Nothing was pinned to .4; the pattern simply had no way to say it.
	///
	/// So the test is on the QUANTIFIER, not on any digits: a group that contains an escaped dot is a
	/// version-component group, and it must repeat without a ceiling — "*" or "+". A bare group ("exactly
	/// one") and "?" ("at most one") are both ceilings, and a ceiling is a version the catalog cannot reach.
	/// The C fields need no group at all: "ubuntu-[0-9.]+-desktop-amd64\.iso" is unbounded by construction,
	/// and passes this rule vacuously, which is the right answer.
	/// </summary>
	[Fact]
	public void AnIsoPatternCanExpressAnyPointRelease()
	{
		var catalog = SourceModel.Parsed
			.SelectMany(p => p.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
			.Where(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "IsoCatalog"))
			.ToArray();
		Assert.True(catalog.Length == 1, $"expected one IsoCatalog field, found {catalog.Length}");

		string[] literals = catalog[0].DescendantNodes().OfType<LiteralExpressionSyntax>()
			.Where(l => l.Token.IsKind(SyntaxKind.StringLiteralToken))
			.Select(l => l.Token.ValueText).ToArray();

		int groupsSeen = 0;
		var bounded = new List<string>();
		foreach (string lit in literals)
			foreach (Match g in Regex.Matches(lit, @"\(\?:[^()]*\\\.[^()]*\)([*+?]|\{[^}]*\})?"))
			{
				groupsSeen++;
				string q = g.Groups[1].Value;
				if (q != "*" && q != "+")
					bounded.Add($"\"{Trim120(lit)}\"  caps its version at \"{g.Value}\""
						+ (q.Length == 0 ? " (no quantifier — exactly one group)" : $" (quantifier \"{q}\")"));
			}

		// A floor, so the rule notices when it has been blinded rather than reporting a clean sweep of
		// nothing: the Ubuntu directory pattern and both Mint directory patterns each carry one such group.
		Assert.True(groupsSeen >= 3,
			$"Only {groupsSeen} version-component group(s) found in IsoCatalog; there were three. Either the "
				+ "catalog shrank or this rule stopped recognising the shape it exists for — re-read it rather "
				+ "than leaving it passing over nothing.");

		Assert.True(bounded.Count == 0,
			"These catalog patterns put a ceiling on how many groups a version may have, so a point release "
				+ "with one more group is invisible even when it is sitting in the index the entry already "
				+ "reads. Shipped v4.4.5 served ubuntu-24.04.4 out of a directory that held 24.04.5.1 for "
				+ "exactly this reason. Repeat the group with \"*\":\n  " + string.Join("\n  ", bounded));
	}

	/// <summary>
	/// RULE — on the tools screen, a result is shown for the drive it was taken from, or not at all.
	///
	/// UpdateDriveToolOverview rewrites thirteen things for the newly selected drive — title, health,
	/// temperature, firmware, serial, interface, size, recommendation, SMART rows, trend text, trend box,
	/// card colour, partition map — and its own comments say why each one must not be left behind. Three
	/// result surfaces were left behind anyway.
	///
	/// The speed panel had ONE writer, UpdateSpeedVisuals, reachable only when a cached result exists, so
	/// after the first test the panel could never return to idle: selecting an untested drive left the
	/// previous drive's MB/s and its verdict standing under the new drive's name — directly below the
	/// title, serial, interface and size, which had all just been rewritten for the new drive. The
	/// dangerous direction is the flattering one: a fast NVMe's "Good for Windows To Go" reading, left
	/// over a cheap stick that cannot take it.
	///
	/// (The Speed summary CARD is not a second opinion here, as a first draft of this rule claimed: that
	/// whole Grid is Visibility="Collapsed" — "info now lives in the Overview tab" — so nobody sees it.
	/// UpdateDiskSummary keeps writing to it, which is deliberate and documented in the XAML.)
	///
	/// The idle texts were there the whole time — XAML defaults, translated into all 17 languages under
	/// their control names — just unreachable. So the panel gets an owner, ShowSpeedFor, and every exit
	/// from the overview goes through it.
	///
	/// The transcript box and status line are cleared on a change of drive, and the trap is in HOW: seven
	/// tools call RefreshDisksAsync immediately after writing their output, and that rebuild changes the
	/// selection. Clearing on "the selection changed" would erase the result the tool had just produced, so
	/// the clear is keyed on the drive the output was WRITTEN for. That half is pinned here too, because
	/// the obvious simplification is the bug.
	/// </summary>
	[Fact]
	public void AToolResultBelongsToTheDriveItCameFrom()
	{
		string[] panel = { "SpeedVisualText", "SpeedAdviceText", "SequentialSpeedBar", "RandomSpeedBar" };

		// --- the speed panel is written in two places, and one of them is the idle state ----------------
		var writers = SourceModel.Methods()
			.Where(m => m.Method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
				.Any(a => panel.Any(c => a.Left.ToString().StartsWith(c + ".", StringComparison.Ordinal))))
			.Select(m => m.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray();
		Assert.True(writers.SequenceEqual(new[] { "ShowSpeedFor", "UpdateSpeedVisuals" }),
			"The speed panel must be written only by its owner and by the renderer it delegates to, so that "
				+ "\"this drive has no result\" is a state the code can reach. Written by: "
				+ string.Join(", ", writers));

		var owner = SourceModel.Methods().Where(m => m.Name == "ShowSpeedFor").ToArray();
		Assert.True(owner.Length == 1, $"expected one ShowSpeedFor, found {owner.Length}");
		string ownerBody = owner[0].Method.ToString();
		foreach (string idle in new[] { "L(\"SpeedVisualText\")", "L(\"SpeedAdviceText\")" })
			Assert.True(ownerBody.Contains(idle, StringComparison.Ordinal),
				$"ShowSpeedFor must restore {idle}: the idle text is the translated XAML default, and without "
					+ "it the panel can never stop describing the previous drive.");

		// --- the overview never renders a speed result itself ------------------------------------------
		var overview = SourceModel.Methods().Where(m => m.Name == "UpdateDriveToolOverview").ToArray();
		Assert.True(overview.Length == 1, $"expected one UpdateDriveToolOverview, found {overview.Length}");
		var calls = overview[0].Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Select(i => i.Expression.ToString()).ToArray();
		Assert.True(!calls.Contains("UpdateSpeedVisuals"),
			"UpdateDriveToolOverview must go through ShowSpeedFor, never render a cached result directly: "
				+ "a direct call is reachable only when a result exists, which is exactly how the panel lost "
				+ "its idle state.");
		int through = calls.Count(c => c == "ShowSpeedFor");
		Assert.True(through >= 3,
			$"Only {through} ShowSpeedFor call(s) in UpdateDriveToolOverview; it has three exits that each "
				+ "settle what the panel shows (no drive, cached report, no report yet). An exit that skips "
				+ "the owner leaves the previous drive's reading on screen.");

		// --- the transcript is cleared by WHOSE it is, not by "something changed" ----------------------
		var clears = overview[0].Method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.Where(a => a.Left.ToString() == "ToolOutputBox.Text").ToArray();
		Assert.True(clears.Length == 1,
			$"Expected exactly one place where the overview clears the transcript, found {clears.Length}.");
		// Tied to the GUARD, not to the method merely mentioning the field: a rule keyed on a name stays
		// green when the name survives and the condition behind it does not.
		var guard = clears[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault();
		Assert.True(guard != null && guard.Condition.ToString()
				.Contains("_toolOutputDiskKey", StringComparison.Ordinal),
			"The transcript may only be cleared under a test on _toolOutputDiskKey — the drive the output was "
				+ "written FOR. Clearing because \"the selection changed\" erases the result the tool just "
				+ "produced, because seven tools call RefreshDisksAsync immediately after writing it. Guard "
				+ "found: " + (guard == null ? "(none)" : Trim120(guard.Condition.ToString())));

		// ...and the rebuild is recognised by the flag that means ONLY that. _syncingDisk looks like the
		// same thing and is not: DiagDiskBox_SelectionChanged raises it on every selection the person makes,
		// because mirroring onto DiskBox fires DiskBox_SelectionChanged synchronously. Measured on hardware:
		// with _syncingDisk here, changing drive stopped clearing the transcript at all. Both directions of
		// this are wrong in opposite ways, which is why both are pinned.
		string cond = guard!.Condition.ToString();
		Assert.True(cond.Contains("_rebuildingDiskList", StringComparison.Ordinal),
			"The guard must exempt a disk-list REBUILD, or a tool's own RefreshDisksAsync wipes the output it "
				+ "just wrote. Guard: " + Trim120(cond));
		Assert.True(!cond.Contains("_syncingDisk", StringComparison.Ordinal),
			"_syncingDisk is not the rebuild flag — it is raised for every selection made in the diagnostics "
				+ "combo as well, so it suppresses the clear always. Guard: " + Trim120(cond));

		var raised = SourceModel.Methods()
			.SelectMany(m => m.Method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
			.Count(a => a.Left.ToString() == "_rebuildingDiskList" && a.Right.ToString() == "true");
		Assert.True(raised == 2,
			$"_rebuildingDiskList is raised {raised} time(s); the disk list is rebuilt in two places and both "
				+ "must be covered, or a refresh down one of them still erases the transcript.");

		var setter = SourceModel.Methods().Where(m => m.Name == "SetToolOutput").ToArray();
		Assert.True(setter.Length == 1, $"expected one SetToolOutput, found {setter.Length}");
		Assert.True(setter[0].Method.ToString().Contains("_toolOutputDiskKey =", StringComparison.Ordinal),
			"SetToolOutput must record which drive the transcript is about. Without it the clear can only be "
				+ "keyed on \"the selection changed\" — and seven tools refresh the disk list immediately "
				+ "after writing their output, so that would erase the result they had just produced.");
	}
}
