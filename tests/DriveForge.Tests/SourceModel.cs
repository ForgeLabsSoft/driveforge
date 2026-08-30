using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DriveForge.Tests;

/// <summary>
/// Parses the app's own source so the invariant checks can reason about real syntax.
///
/// Roslyn rather than regex/line-scanning, decided by measurement on THIS codebase:
///   - a grep for `SetBusy(busy: true` misses 8 positional `SetBusy(true, ...)` calls, including all five in the one
///     method the paired-flag rule most needs to reason about;
///   - `_progressFixedTotal = sizeKnown;` is invisible to a `= true` pattern;
///   - a line-based scan for confirm dialogs missing a safe default reported 6 where the parser reports 27, because
///     the line also contained `!= MessageBoxResult.OK`;
///   - brace counters desync on the interpolated strings with nested quotes that are pervasive here.
/// Parsing all four files takes about a second, so this is cheap enough to gate a build.
/// </summary>
internal static class SourceModel
{
	internal static readonly string[] SourceFiles =
		new[] { "MainWindow.cs", "NtfsRawClone.cs", "NtfsRecovery.cs", "UiCustomization.cs", "App.cs", "VirtDiskInterop.cs" }
			.Select(f => Path.Combine(Mw.RepoRoot, "DriveForge", f))
			.Where(File.Exists)
			.ToArray();

	private static List<(string File, SyntaxNode Root)> _parsed;

	internal static IReadOnlyList<(string File, SyntaxNode Root)> Parsed =>
		_parsed ??= SourceFiles
			.Select(f => (File: f, Root: CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f).GetRoot()))
			.ToList();

	/// <summary>Every method-like body in the app (methods, local functions, accessors, lambdas' owners).</summary>
	internal static IEnumerable<(string File, BaseMethodDeclarationSyntax Method, string Name)> Methods()
	{
		foreach (var (file, root) in Parsed)
			foreach (BaseMethodDeclarationSyntax m in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
				yield return (file, m, (m as MethodDeclarationSyntax)?.Identifier.Text
					?? (m as ConstructorDeclarationSyntax)?.Identifier.Text
					?? "<anonymous>");
	}

	internal static string Where(string file, SyntaxNode node) =>
		$"{Path.GetFileName(file)}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

	/// <summary>Assignments to a named field anywhere under <paramref name="node"/>.</summary>
	internal static IEnumerable<AssignmentExpressionSyntax> AssignmentsTo(SyntaxNode node, string fieldName) =>
		node.DescendantNodes().OfType<AssignmentExpressionSyntax>()
			.Where(a => a.Left is IdentifierNameSyntax id && id.Identifier.Text == fieldName);

	/// <summary>True when the assignment's right-hand side is the literal <c>true</c>.</summary>
	internal static bool AssignsTrue(AssignmentExpressionSyntax a) =>
		a.Right is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.TrueKeyword);

	internal static bool AssignsFalse(AssignmentExpressionSyntax a) =>
		a.Right is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.FalseKeyword);

	/// <summary>Is this node lexically inside a finally block of the given method?</summary>
	internal static bool IsInsideFinally(SyntaxNode node, SyntaxNode methodRoot)
	{
		for (SyntaxNode n = node; n != null && n != methodRoot; n = n.Parent)
			if (n is FinallyClauseSyntax) return true;
		return false;
	}

	/// <summary>All invocations of a method by simple name under a node.</summary>
	internal static IEnumerable<InvocationExpressionSyntax> Calls(SyntaxNode node, string methodName) =>
		node.DescendantNodes().OfType<InvocationExpressionSyntax>()
			.Where(i => (i.Expression as IdentifierNameSyntax)?.Identifier.Text == methodName
					 || (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == methodName);

	/// <summary>
	/// The single boolean argument of a call, whether written positionally (<c>SetBusy(false)</c>) or by name
	/// (<c>SetBusy(busy: false)</c>). Returns null when it is not a boolean literal.
	/// </summary>
	internal static bool? BoolArg(InvocationExpressionSyntax call)
	{
		foreach (ArgumentSyntax arg in call.ArgumentList.Arguments)
			if (arg.Expression is LiteralExpressionSyntax lit)
			{
				if (lit.Token.IsKind(SyntaxKind.TrueKeyword)) return true;
				if (lit.Token.IsKind(SyntaxKind.FalseKeyword)) return false;
			}
		return null;
	}
}
