using System;
using System.Collections.Generic;
using System.Linq;
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
}
