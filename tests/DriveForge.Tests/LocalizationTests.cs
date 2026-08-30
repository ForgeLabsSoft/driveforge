using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DriveForge.Tests;

/// <summary>
/// Localization invariants. This is the highest-churn area in the app (1,049 keys x 17 languages, added in batches
/// of 17) and every failure mode here is INVISIBLE to the compiler and only shows up at runtime, in a language the
/// maintainer does not read. Exactly the class of bug that needs a machine watching it.
///
/// The dictionary is read by reflection at runtime rather than by parsing UiStrings.cs — parsing would have to cope
/// with escaped quotes, braces inside literals and 14,000 lines of it, whereas the runtime object is the truth.
/// </summary>
public class LocalizationTests
{
	private static readonly Dictionary<string, Dictionary<string, string>> Strings =
		(Dictionary<string, Dictionary<string, string>>)Mw.Field("Strings");

	private const string Base = "en";

	private static IEnumerable<string> OtherLanguages => Strings.Keys.Where(k => k != Base);

	// ------------------------------------------------------------------ A. shape

	[Fact]
	public void AllSeventeenLanguagesArePresent()
	{
		string[] expected = { "en", "ro", "es", "fr", "de", "it", "pt", "nl", "ru", "pl", "tr", "uk", "zh", "ja", "hi", "id", "ar" };
		Assert.Equal(expected.OrderBy(x => x), Strings.Keys.OrderBy(x => x));
	}

	// ------------------------------------------------------------------ B. key parity

	/// <summary>
	/// Every key English has, every other language must have. A missing key silently falls back to English, so a
	/// half-finished batch of 17 ships as untranslated UI that nobody notices without reading all 17 languages.
	/// </summary>
	[Fact]
	public void EveryLanguageHasEveryEnglishKey()
	{
		var missing = new List<string>();
		foreach (string lang in OtherLanguages)
		{
			string[] gaps = Strings[Base].Keys.Where(k => !Strings[lang].ContainsKey(k)).OrderBy(k => k).ToArray();
			if (gaps.Length > 0) missing.Add($"{lang}: missing {gaps.Length} -> {string.Join(", ", gaps)}");
		}
		Assert.True(missing.Count == 0, "Keys missing from some languages:\n" + string.Join("\n", missing));
	}

	/// <summary>A key in another language but not in English is dead weight — English is the base and the fallback.</summary>
	[Fact]
	public void NoLanguageHasKeysEnglishLacks()
	{
		var extra = new List<string>();
		foreach (string lang in OtherLanguages)
		{
			string[] orphans = Strings[lang].Keys.Where(k => !Strings[Base].ContainsKey(k)).OrderBy(k => k).ToArray();
			if (orphans.Length > 0) extra.Add($"{lang}: {string.Join(", ", orphans)}");
		}
		Assert.True(extra.Count == 0, "Keys present in a translation but not in English:\n" + string.Join("\n", extra));
	}

	/// <summary>
	/// A key declared TWICE in the same language block. This is invisible everywhere else: the dictionary is an
	/// indexer collection initializer, so a duplicate compiles without even a warning and the LAST assignment
	/// silently wins. Every other test here reads the dictionary at RUNTIME, by which point the duplicate has
	/// already collapsed — so this one has to parse the source instead.
	///
	/// It is not hypothetical: a reporting feature was added with an ErrReport key that already existed, and all
	/// 17 of its new values were dead on arrival while the tests stayed green.
	/// </summary>
	[Fact]
	public void NoKeyIsDeclaredTwiceInTheSameLanguageBlock()
	{
		string[] lines = File.ReadAllLines(Path.Combine(Mw.RepoRoot, "DriveForge", "UiStrings.cs"));
		var duplicates = new List<string>();
		string lang = null;
		var seen = new Dictionary<string, int>();

		for (int i = 0; i < lines.Length; i++)
		{
			Match block = Regex.Match(lines[i], @"^		\[""(\w\w)""\] = new\(\)$");
			if (block.Success) { lang = block.Groups[1].Value; seen.Clear(); continue; }
			if (lang == null) continue;
			Match entry = Regex.Match(lines[i], @"^			\[""([A-Za-z0-9_]+)""\] = ");
			if (!entry.Success) continue;
			string key = entry.Groups[1].Value;
			if (seen.TryGetValue(key, out int first))
				duplicates.Add($"{key} [{lang}]: lines {first} and {i + 1} — the later value silently wins");
			else seen[key] = i + 1;
		}

		Assert.True(duplicates.Count == 0,
			"Duplicate localization key(s) — the earlier value is dead code:\n  " + string.Join("\n  ", duplicates));
	}

	// ------------------------------------------------------------------ C. placeholder arity

	private static SortedSet<int> Placeholders(string value)
	{
		var set = new SortedSet<int>();
		// {{ and }} are literal braces, not placeholders — skip them before matching.
		string scrubbed = value.Replace("{{", "").Replace("}}", "");
		foreach (Match m in Regex.Matches(scrubbed, @"\{(\d+)(?::[^}]*)?\}"))
			set.Add(int.Parse(m.Groups[1].Value));
		return set;
	}

	/// <summary>
	/// A translation whose {0}/{1} set differs from English is a latent crash or a silently dropped value:
	/// string.Format throws FormatException if it references an index the caller did not supply, and silently omits
	/// information if it uses fewer. The compiler cannot see this.
	/// </summary>
	[Fact]
	public void PlaceholderSetsMatchEnglishInEveryLanguage()
	{
		var problems = new List<string>();
		foreach (var (key, englishValue) in Strings[Base])
		{
			SortedSet<int> expected = Placeholders(englishValue);
			foreach (string lang in OtherLanguages)
			{
				if (!Strings[lang].TryGetValue(key, out string translated)) continue;   // covered by the parity test
				SortedSet<int> actual = Placeholders(translated);
				if (!expected.SetEquals(actual))
					problems.Add($"{key} [{lang}]: en has {{{string.Join(",", expected)}}}, {lang} has {{{string.Join(",", actual)}}}");
			}
		}
		Assert.True(problems.Count == 0, "Placeholder mismatches (runtime FormatException or dropped values):\n" + string.Join("\n", problems));
	}

	/// <summary>Placeholder indices must start at 0 and be contiguous, or string.Format throws at runtime.</summary>
	[Fact]
	public void PlaceholderIndicesAreContiguousFromZero()
	{
		var problems = new List<string>();
		foreach (string lang in Strings.Keys)
			foreach (var (key, value) in Strings[lang])
			{
				SortedSet<int> p = Placeholders(value);
				if (p.Count > 0 && (p.Min != 0 || p.Max != p.Count - 1))
					problems.Add($"{key} [{lang}]: indices {{{string.Join(",", p)}}}");
			}
		Assert.True(problems.Count == 0, "Non-contiguous placeholder indices:\n" + string.Join("\n", problems));
	}

	// ------------------------------------------------------------------ D. code references

	/// <summary>
	/// Every literal L("Key") in the source must resolve. A typo here shows the raw key name in the UI.
	/// Non-literal call sites (L(c.LabelKey), L(key) in a loop) are skipped — they are table-driven and covered by
	/// <see cref="EveryTableDrivenKeyResolves"/>.
	/// </summary>
	[Fact]
	public void EveryLiteralKeyReferencedInCodeExists()
	{
		var missing = new SortedSet<string>();
		foreach (string file in SourceModel.SourceFiles)
			foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\bL\(\s*""([A-Za-z0-9_]+)""\s*\)"))
				if (!Strings[Base].ContainsKey(m.Groups[1].Value))
					missing.Add($"{m.Groups[1].Value}  ({Path.GetFileName(file)})");
		Assert.True(missing.Count == 0, "L(\"...\") referencing a key that does not exist in English:\n" + string.Join("\n", missing));
	}

	/// <summary>
	/// The Clean panel drives its labels from a table of key NAMES rather than literals, so the checker above cannot
	/// see them. Resolve them the same way the app does.
	/// </summary>
	[Fact]
	public void EveryTableDrivenKeyResolves()
	{
		var missing = new SortedSet<string>();
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		foreach (Match m in Regex.Matches(src, @"\b(?:LabelKey|GroupKey|DescKey)\s*=\s*""([A-Za-z0-9_]+)"""))
			if (!Strings[Base].ContainsKey(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);
		Assert.True(missing.Count == 0, "Table-driven localization key does not exist:\n" + string.Join("\n", missing));
	}

	// ------------------------------------------------------------------ E. escape / structure parity

	/// <summary>
	/// A translation that drops the line breaks of a multi-line dialog is not a formatting nit. The worst live case
	/// was an OK/Cancel warning where OK writes onto the disk being recovered: English spelled out which button does
	/// what on its own line, and 15 languages rendered it as a flat refusal with no mention that OK proceeds anyway.
	/// A structural difference this large means the MEANING drifted.
	///
	/// Enforced against a baseline so the existing debt is visible but does not block; new drift fails immediately.
	/// </summary>
	[Fact]
	public void MultiLineDialogsKeepTheirLineBreaks()
	{
		// Keys already known to have drifted. Shrink this list — never grow it.
		// (RfSameDiskBlocked was here: 15 languages had flattened the recover-to-the-same-disk warning into a plain
		//  refusal, losing the line that says OK proceeds anyway — on a dialog whose OK writes onto the disk being
		//  recovered. Retranslated 2026-07-26, so the rule now guards it like every other key.)
		var known = new HashSet<string>();

		var problems = new List<string>();
		foreach (var (key, englishValue) in Strings[Base])
		{
			int expected = englishValue.Split("\n\n").Length - 1;   // paragraph breaks only
			if (expected == 0 || known.Contains(key)) continue;
			foreach (string lang in OtherLanguages)
			{
				if (!Strings[lang].TryGetValue(key, out string translated)) continue;
				int actual = translated.Split("\n\n").Length - 1;
				if (actual == 0)
					problems.Add($"{key} [{lang}]: English has {expected} paragraph break(s), {lang} has none — the structure was lost");
			}
		}
		Assert.True(problems.Count == 0,
			"Translations that flattened a multi-paragraph dialog (check the MEANING, not just the layout):\n" + string.Join("\n", problems));
	}

	// ------------------------------------------------------------------ F. orphans (ratchet)

	/// <summary>
	/// Keys reachable from nothing: not referenced by L(), not a control Name in the XAML, not table-driven.
	/// They are dead weight and a trap — someone wires one up later and ships a half-translated batch.
	///
	/// Ratcheted, not absolute: the 16 dead keys below are recorded as a baseline so the check is enforceable today,
	/// and any NEW orphan fails the build. Removing one from the baseline is the cleanup path.
	/// </summary>
	[Fact]
	public void NoNewOrphanKeys()
	{
		// Measured baseline. These are dead today — delete the key and its 17 translations, then delete it here.
		var baseline = new HashSet<string>
		{
			"AnSet", "Mb023", "RfSameDrive", "RfZipSameDrive", "Step1Title", "Step1Desc",
			"AnalyzeTreemapHeader", "AnalyzeDeleteConfirm", "PredReasonSectors", "A11yMenu",
			"MvNeedTable", "MvGptUnsupported", "PtLostMounted", "SbSettingsS",
			"InternalDiskCheck", "RepairToolButton",
		};

		string allCode = string.Join("\n", SourceModel.SourceFiles.Select(File.ReadAllText));
		string xaml = File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml"));

		// A key counts as referenced if it appears as ANY string literal in code (covers L(), Set(ctrl,"Key"),
		// ternaries and table entries) or as a control Name/x:Name in the XAML (auto-applied by ApplyLanguage).
		var referenced = new HashSet<string>(
			Regex.Matches(allCode, @"""([A-Za-z0-9_]+)""").Select(m => m.Groups[1].Value));
		foreach (Match m in Regex.Matches(xaml, @"(?:x:)?Name\s*=\s*""([A-Za-z0-9_]+)"""))
			referenced.Add(m.Groups[1].Value);
		// Tooltips are applied as "<ControlName>Tip".
		foreach (string name in referenced.ToArray()) referenced.Add(name + "Tip");

		string[] newOrphans = Strings[Base].Keys
			.Where(k => !referenced.Contains(k) && !baseline.Contains(k))
			.OrderBy(k => k).ToArray();

		Assert.True(newOrphans.Length == 0,
			"New unreferenced localization key(s) — either wire them up or delete them from all 17 languages:\n  "
			+ string.Join("\n  ", newOrphans));
	}

	// ------------------------------------------------------------------ G. content sanity

	[Fact]
	public void NoKeyHasAnEmptyOrWhitespaceValue()
	{
		var empty = new List<string>();
		foreach (string lang in Strings.Keys)
			foreach (var (key, value) in Strings[lang])
				if (string.IsNullOrWhiteSpace(value)) empty.Add($"{key} [{lang}]");
		Assert.True(empty.Count == 0, "Empty localized values:\n" + string.Join("\n", empty));
	}

	/// <summary>A stray unmatched brace makes string.Format throw at runtime, in that language only.</summary>
	[Fact]
	public void NoUnbalancedBracesInAnyValue()
	{
		var bad = new List<string>();
		foreach (string lang in Strings.Keys)
			foreach (var (key, value) in Strings[lang])
			{
				string scrubbed = Regex.Replace(value.Replace("{{", "").Replace("}}", ""), @"\{\d+(?::[^}]*)?\}", "");
				if (scrubbed.Contains('{') || scrubbed.Contains('}')) bad.Add($"{key} [{lang}]: {value}");
			}
		Assert.True(bad.Count == 0, "Unbalanced/among-literal braces (runtime FormatException risk):\n" + string.Join("\n", bad));
	}
}
