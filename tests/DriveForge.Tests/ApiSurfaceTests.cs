using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace DriveForge.Tests;

/// <summary>
/// The tests reach production helpers by reflection, which trades a compile error for a runtime one when something
/// is renamed. This file is the mitigation: renaming a tested helper fails HERE, once, with the name printed —
/// instead of producing a wall of confusing MissingMethodExceptions across the rest of the suite.
///
/// If a rename was intentional, update the list below and the call sites together.
/// </summary>
public class ApiSurfaceTests
{
	[Fact]
	public void EveryReflectedHelperStillExists()
	{
		string[] required =
		{
			"NaturalSortKey", "FormatDuration", "FormatBytes", "ExtractJsonPayload", "QuoteArgument",
			"Crc32", "WriteUInt32", "WriteInt64", "VerifyGptHeader", "FixGptHeaderCrc",
			"IsHealthy", "PartTypeName", "ConvertToGiB", "IsPureAscii",
			"MakeNonReservedFileName", "SanitizeRelativeDir",
		};

		var missing = new List<string>();
		foreach (string name in required)
		{
			try { Mw.Method(name); }
			catch (Exception ex) { missing.Add($"{name}  ({ex.GetType().Name})"); }
		}

		Assert.True(missing.Count == 0,
			"Helper(s) renamed, removed, or now overloaded — update the reflection call sites too:\n  "
			+ string.Join("\n  ", missing));
	}

	[Fact]
	public void TheLocalizationDictionaryIsStillReachable()
	{
		var strings = Mw.Field("Strings") as Dictionary<string, Dictionary<string, string>>;
		Assert.NotNull(strings);
		Assert.True(strings.Count >= 17, $"Expected 17 languages, found {strings.Count}");
		Assert.True(strings["en"].Count > 900, $"Expected ~1000 English keys, found {strings["en"].Count}");
	}

	[Fact]
	public void RepoRootResolvesToTheCheckout()
	{
		Assert.True(System.IO.Directory.Exists(Mw.RepoRoot), $"RepoRoot does not exist: {Mw.RepoRoot}");
		Assert.True(System.IO.File.Exists(System.IO.Path.Combine(Mw.RepoRoot, "DriveForge.csproj")),
			$"RepoRoot does not look like the repo: {Mw.RepoRoot}");
	}
}
