using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace DriveForge.Tests;

/// <summary>
/// Reflection gateway to MainWindow's private static helpers.
///
/// Why reflection rather than widening `private` to `internal` (or extracting the helpers into their own class):
/// the app is a single 15,000-line partial god-class that has been hand-verified. Making ~200 declarations internal,
/// or moving them, is a large diff across working code purely for test ergonomics. Reflection buys the same coverage
/// with a zero-line production diff.
///
/// The honest trade-off: a rename in production becomes a runtime MissingMethodException here instead of a compile
/// error. <see cref="ApiSurfaceTests"/> pins the names so that shows up as ONE clear failure, not forty confusing ones.
/// </summary>
internal static class Mw
{
	private static readonly Type T = typeof(MainWindow);
	private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

	/// <summary>Repo root, injected by the .csproj so source-scanning tests never hardcode a path.</summary>
	internal static string RepoRoot =>
		Path.GetFullPath(Assembly.GetExecutingAssembly()
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Single(a => a.Key == "RepoRoot").Value);

	internal static MethodInfo Method(string name)
	{
		MethodInfo[] found = T.GetMethods(Flags).Where(m => m.Name == name).ToArray();
		if (found.Length == 0) throw new MissingMethodException($"MainWindow.{name} (private static) not found");
		if (found.Length > 1) throw new AmbiguousMatchException($"MainWindow.{name} has {found.Length} overloads — use Method(name, paramTypes)");
		return found[0];
	}

	internal static MethodInfo Method(string name, params Type[] paramTypes)
	{
		MethodInfo m = T.GetMethod(name, Flags, binder: null, types: paramTypes, modifiers: null);
		if (m == null) throw new MissingMethodException($"MainWindow.{name}({string.Join(", ", paramTypes.Select(p => p.Name))}) not found");
		return m;
	}

	/// <summary>Private nested types (DiskItem, Sig, IsoEntry, ...).</summary>
	internal static Type Nested(string name) =>
		T.GetNestedType(name, BindingFlags.NonPublic | BindingFlags.Public)
		?? throw new MissingMemberException($"MainWindow+{name} not found");

	internal static object Field(string name) =>
		(T.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
		 ?? throw new MissingFieldException($"MainWindow.{name} (static field) not found")).GetValue(null);

	/// <summary>
	/// Invoke a private static method. Rethrows the ORIGINAL exception rather than TargetInvocationException, so a
	/// test asserting on a thrown type sees what production would actually throw.
	/// </summary>
	internal static object Call(string name, params object[] args)
	{
		try { return Method(name).Invoke(null, args); }
		catch (TargetInvocationException ex) when (ex.InnerException != null)
		{
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw; // unreachable
		}
	}

	internal static T2 Call<T2>(string name, params object[] args) => (T2)Call(name, args);

	internal static object CallOn(MethodInfo m, params object[] args)
	{
		try { return m.Invoke(null, args); }
		catch (TargetInvocationException ex) when (ex.InnerException != null)
		{
			ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
			throw;
		}
	}
}
