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

		(string Needle, string What)[] required =
		{
			("UpdateRecoverSelectionInfo()", "the Recover result line (\"20 deleted files\")"),
			("CleanAnalyzeResult", "the Clean result line (\"About 2.0 GB can be freed.\")"),
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
}
