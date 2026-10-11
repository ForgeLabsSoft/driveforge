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
		var found = SourceModel.Methods().Where(m => m.Name == "CleanRun_Click").ToArray();
		Assert.True(found.Length == 1, $"Expected one CleanRun_Click, found {found.Length}.");
		string code = string.Join("\n", found[0].Method.ToString().Split('\n')
			.Where(l => !l.TrimStart().StartsWith("//")));

		Assert.True(code.Contains("CleanStopped", StringComparison.Ordinal),
			"A stopped clean still says \"Done\", while the categories it never reached sit in the list "
			+ "still ticked, looking as though they had been dealt with.");
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
}
