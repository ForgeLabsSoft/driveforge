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

	/// <summary>
	/// Controls that legitimately keep their literal XAML text: the product name, which is a brand rather than a
	/// phrase to translate. Keep this list SHORT — every entry is a string the rule below stops protecting.
	/// </summary>
	private static readonly HashSet<string> LiteralTextIsCorrect = new()
	{
		"AppTitleText", "AboutVersionText",
	};

	/// <summary>
	/// Every named, user-visible control in the window carries text the user can read, so each one must get that
	/// text from somewhere translatable: either its x:Name is a string key (ApplyLanguage's FindName loop rewrites
	/// it), or code assigns it from L(...). A control that satisfies neither keeps its English XAML literal in all
	/// 17 languages, and the only way to notice is to look at the window in another language — which is exactly how
	/// "Use the Microsoft engine (DISM)" and "Fast Clone" were found, sitting in English in a German UI.
	/// </summary>
	[Fact]
	public void EveryNamedVisibleControlGetsItsTextFromSomewhereTranslatable()
	{
		string xaml = File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml"));
		string code = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"))
			+ File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "UiCustomization.cs"));
		HashSet<string> keys = Strings["en"].Keys.ToHashSet();

		var orphans = new List<string>();
		foreach (Match m in Regex.Matches(xaml, @"<(CheckBox|Button|TextBlock|GroupBox|TabItem|Expander)\b(?:[^<>""]|""[^""]*"")*?>"))
		{
			string tag = m.Value;
			Match name = Regex.Match(tag, @"\bName=""([A-Za-z0-9_]+)""");
			Match text = Regex.Match(tag, @"\b(?:Content|Text|Header)=""([^""]*)""");
			if (!name.Success || !text.Success) continue;

			string value = text.Groups[1].Value;
			// Skip glyph fonts and icon code points — those are symbols, not language.
			if (tag.Contains("Segoe MDL2") || value.StartsWith("&#x") || !Regex.IsMatch(value, "[A-Za-z]{3}")) continue;

			string id = name.Groups[1].Value;
			if (keys.Contains(id) || LiteralTextIsCorrect.Contains(id)) continue;
			if (Regex.IsMatch(code, Regex.Escape(id) + @"\s*\.\s*(?:Content|Text|Header)\s*=")) continue;

			orphans.Add($"{id} ({m.Groups[1].Value}) = \"{value}\" — no string key and never assigned in code");
		}

		Assert.True(orphans.Count == 0,
			"Named control whose text can never be translated:\n  " + string.Join("\n  ", orphans));
	}

	/// <summary>
	/// Every sidebar nav button must have a screen-reader name.
	///
	/// Their content is a Grid holding an icon glyph and two TextBlocks, so WPF derives no name from it and a
	/// screen reader announces a bare "button" - the whole sidebar reads as twelve identical buttons. The only
	/// thing that names them is ApplyAccessibilityNames, and NavExportVhdx was the one button missing from that
	/// list: found by reading the live UIA tree, invisible to everything else, and a one-line omission is exactly
	/// the mistake the next button added to the sidebar will repeat.
	/// </summary>
	[Fact]
	public void EverySidebarNavButtonHasAnAccessibilityName()
	{
		string xaml = File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml"));
		string custom = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "UiCustomization.cs"));
		HashSet<string> keys = Strings["en"].Keys.ToHashSet();

		var names = Regex.Matches(xaml, @"<Button\s+Name=""(Nav[A-Za-z0-9_]+)""")
			.Select(m => m.Groups[1].Value).Distinct().ToList();
		Assert.True(names.Count >= 10, $"Expected the sidebar nav buttons in MainWindow.xaml; found {names.Count}.");

		var unnamed = new List<string>();
		foreach (string id in names)
		{
			Match set = Regex.Match(custom, @"Set\(FindName\(""" + Regex.Escape(id) + @"""\)[^;]*?,\s*""([A-Za-z0-9_]+)""\s*\)");
			if (!set.Success) { unnamed.Add(id + " - no AutomationProperties.Name is ever set for it"); continue; }
			if (!keys.Contains(set.Groups[1].Value))
				unnamed.Add(id + " - named from \"" + set.Groups[1].Value + "\", which is not a string key");
		}

		Assert.True(unnamed.Count == 0,
			"Sidebar nav button a screen reader cannot announce:\n  " + string.Join("\n  ", unnamed));
	}

	/// <summary>
	/// No button built in code may carry an English caption typed into the source.
	///
	/// The three hand-built dialogs - the chooser, the action menu and the text prompt - are used about thirty
	/// times between them, in front of picking which disk to erase, which partition operation to run and which
	/// firmware to boot with. Their OK and Cancel were string literals, so in all seventeen languages the two
	/// buttons that decide whether a drive is destroyed stayed in English. XAML captions are covered by the
	/// orphan test above; this covers the ones that never appear in the XAML at all.
	/// </summary>
	[Fact]
	public void NoButtonBuiltInCodeHasAHardCodedCaption()
	{
		string code = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		var offenders = new List<string>();
		foreach (Match m in Regex.Matches(code, @"Content\s*=\s*""([^""]{2,})"""))
		{
			string text = m.Groups[1].Value;
			// Glyph code points and single symbols are not language.
			if (!Regex.IsMatch(text, "[A-Za-z]{2}")) continue;
			int line = code.Take(m.Index).Count(c => c == '\n') + 1;
			offenders.Add($"line {line}: Content = \"{text}\"");
		}
		Assert.True(offenders.Count == 0,
			"Button caption typed into the source instead of coming from a string key:\n  " + string.Join("\n  ", offenders));
	}

	/// <summary>
	/// No status line may be written in English from the code.
	///
	/// The XAML orphan test above only sees text that starts life in the XAML. These are assigned at runtime,
	/// and five of them had been typed straight in: the whole visible state of the ISO downloader ("Downloading
	/// x.iso - 1.2 GB / 4.0 GB (30%)", "Downloading to ...", "Saved: ...") and the line a clone shows for its
	/// first several minutes ("Scanning Windows... 12.3 GiB indexed"). In a Romanian window the body of the
	/// dialog was Romanian and the line underneath it was not.
	/// </summary>
	[Fact]
	public void NoStatusLineIsWrittenInEnglishFromCode()
	{
		string code = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		var offenders = new List<string>();
		// Any UI element whose name ends in Text or Hint, assigned a literal (plain or interpolated).
		foreach (Match m in Regex.Matches(code, @"\b([A-Za-z0-9_]*(?:Text|Hint))\s*\.\s*Text\s*=\s*\$?""([^""]*)"""))
		{
			string value = m.Groups[2].Value;
			// Only the LITERAL parts count. An interpolation hole holds C# identifiers - {diskItem.FriendlyName},
			// {percent:F0} - which are not text anyone reads, and matching them flagged five lines whose visible
			// output is "25-30 \u00b0C (14)" or "87%".
			string literal = Regex.Replace(value, @"\{[^}]*\}", " ");
			// Empty resets and pure punctuation/units are not language.
			if (!Regex.IsMatch(literal, "[A-Za-z]{3}")) continue;
			// An interpolation hole that IS a lookup (e.g. $"{L(\"Key\")} ...") is fine; a bare word is not.
			if (value.Contains("L(\"")) continue;
			int line = code.Take(m.Index).Count(c => c == '\n') + 1;
			offenders.Add($"line {line}: {m.Groups[1].Value}.Text = \"{value}\"");
		}
		Assert.True(offenders.Count == 0,
			"UI text typed into the source instead of coming from a string key:\n  " + string.Join("\n  ", offenders));
	}

	/// <summary>
	/// Every language must be written in its OWN alphabet.
	///
	/// Found by review, then confirmed by sweeping all seventeen blocks: the Polish word <c>rosnąć</c> was
	/// spelled with a CYRILLIC н (U+043D). It compiles, it passes key parity, it renders as a Polish word and it
	/// is not one. Romanian had the mirror problem - six strings typed with the Turkish cedilla ş/ţ while the
	/// other 627 use the comma-below ș/ț Romanian actually takes, several of them mixing both inside one
	/// sentence. Both arrived through tooling that rewrites characters, which is why this is a rule and not a
	/// one-off correction.
	/// </summary>
	[Fact]
	public void EveryLanguageIsWrittenInItsOwnAlphabet()
	{
		string src = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "UiStrings.cs"));
		var blocks = Regex.Matches(src, "\\[\"(\\w\\w)\"\\] = new\\(\\)").Cast<Match>().ToList();
		Assert.True(blocks.Count == 17, $"expected 17 language blocks, found {blocks.Count}");

		// Languages written in the Latin alphabet. A Cyrillic or Greek letter in one of these is a homoglyph.
		var latin = new HashSet<string> { "en", "es", "fr", "de", "it", "pt", "nl", "pl", "tr", "id", "ro" };
		var offenders = new List<string>();

		for (int i = 0; i < blocks.Count; i++)
		{
			string lang = blocks[i].Groups[1].Value;
			int start = blocks[i].Index;
			int end = i + 1 < blocks.Count ? blocks[i + 1].Index : src.Length;
			string block = src.Substring(start, end - start);

			foreach (Match e in Regex.Matches(block, "\\[\"(\\w+)\"\\] = \"([^\"]*)\""))
			{
				string key = e.Groups[1].Value;
				// These values are read from SOURCE, so a C# escape is still two characters. Blank them first:
				// otherwise the 'n' of a \\n glues itself to the next word and every Cyrillic string in the file
				// reads as mixed-script. That false positive fired 128 times while this check was being written.
				string value = Regex.Replace(e.Groups[2].Value, @"\\.", " ");

				if (latin.Contains(lang))
				{
					string wrong = new string(value.Where(c => (c >= '\u0400' && c <= '\u04FF')
													  || (c >= '\u0370' && c <= '\u03FF')).Distinct().ToArray());
					if (wrong.Length > 0)
						offenders.Add($"[{lang}] {key}: Cyrillic/Greek letter(s) '{wrong}' inside a Latin-alphabet language");
				}

				// Romanian takes the comma-below ș/ț, never the Turkish cedilla ş/ţ. They look almost identical
				// in most fonts, which is exactly why this has to be checked rather than seen.
				if (lang == "ro" && value.IndexOfAny(new[] { '\u015f', '\u015e', '\u0163', '\u0162' }) >= 0)
					offenders.Add($"[ro] {key}: Turkish cedilla ş/ţ where Romanian takes comma-below ș/ț");
			}
		}

		Assert.True(offenders.Count == 0,
			"Wrong-alphabet letter(s) in a language block:\n  " + string.Join("\n  ", offenders));
	}

	/// <summary>
	/// The Recover "More" overflow menu lives in a ContextMenu, which is its own XAML namescope, so its items cannot
	/// be found by name and their headers are assigned BY POSITION in ApplyLanguage. That makes the menu silently
	/// order-dependent: insert one item in the XAML and every label below it shifts onto the wrong row, in all
	/// seventeen languages at once, with nothing to see in English because the XAML Header still reads correctly
	/// until the first language switch. (It happened: adding "Create image of the whole drive" moved "Open image",
	/// the separator and both session items down one.)
	///
	/// So this walks the two in lockstep: for every Items[N] the code assigns, the XAML's Nth child must be a
	/// MenuItem, and its hard-coded English Header must be exactly what that key says in English.
	/// </summary>
	[Fact]
	public void OverflowMenuHeadersAreAssignedToTheRightItems()
	{
		string xaml = File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml"));
		string ui = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "UiCustomization.cs"));

		var menu = Regex.Match(xaml, @"RecoverMoreButton.*?<ContextMenu>(.*?)</ContextMenu>", RegexOptions.Singleline);
		Assert.True(menu.Success, "The Recover overflow ContextMenu is gone from MainWindow.xaml.");

		// Children in document order; a Separator counts as a position even though it gets no header.
		var children = Regex.Matches(menu.Groups[1].Value, @"<(MenuItem|Separator)\b([^>]*)>")
			.Select(m => (Tag: m.Groups[1].Value, Attrs: m.Groups[2].Value))
			.ToList();
		Assert.True(children.Count > 0, "The overflow menu has no items.");

		var assignments = Regex.Matches(ui, @"ContextMenu\.Items\[(\d+)\][^;]*?Header = L\(" + @"""" + @"([A-Za-z0-9_]+)" + @"""" + @"\)")
			.Select(m => (Index: int.Parse(m.Groups[1].Value), Key: m.Groups[2].Value))
			.ToList();
		Assert.True(assignments.Count > 0, "Nothing assigns headers to the overflow menu any more.");

		var problems = new List<string>();
		foreach ((int index, string key) in assignments)
		{
			if (index >= children.Count)
			{
				problems.Add($"Items[{index}] -> {key}: the menu only has {children.Count} items.");
				continue;
			}
			if (children[index].Tag != "MenuItem")
			{
				problems.Add($"Items[{index}] -> {key}: position {index} is a {children[index].Tag}, not a MenuItem.");
				continue;
			}
			if (!Strings[Base].TryGetValue(key, out string? english))
			{
				problems.Add($"Items[{index}] -> {key}: no such key in English.");
				continue;
			}
			var header = Regex.Match(children[index].Attrs, @"Header=" + @"""" + @"([^" + @"""" + @"]*)" + @"""");
			if (!header.Success)
			{
				problems.Add($"Items[{index}] -> {key}: the XAML item has no Header to compare.");
				continue;
			}
			if (header.Groups[1].Value != english)
				problems.Add($"Items[{index}] -> {key}: XAML says \"{header.Groups[1].Value}\", English says \"{english}\".");
		}

		// Every MenuItem must get a header from somewhere, or it stays English in every other language.
		for (int i = 0; i < children.Count; i++)
			if (children[i].Tag == "MenuItem" && !assignments.Any(a => a.Index == i))
				problems.Add($"Menu item {i} is never translated - nothing assigns Items[{i}].");

		Assert.True(problems.Count == 0,
			"The overflow menu and its by-index translation have drifted apart:\n  " + string.Join("\n  ", problems));
	}

	// ------------------------------------------------------------------ G. the language dropdown's order
	//
	// The order shown to the user is DERIVED (English pinned first, the rest sorted by the name people read) rather
	// than hand-written, so these tests read the real arrays off the built assembly instead of re-parsing the source.
	//
	// Reflecting on them also forces the static initialiser to run, which is the one way this file can fail
	// catastrophically rather than cosmetically: a derived field declared BEFORE the literal it reads leaves the
	// literal null at init time, and MainWindow then throws TypeInitializationException the first time anything
	// touches it - the whole window, not just the dropdown. C# runs static initialisers in textual order and the
	// compiler does not warn, so only running them catches it.

	private static (string Code, string Display)[] ReadLanguages(string field)
	{
		Array arr = (Array)Mw.Field(field);
		Type option = Mw.Nested("LanguageOption");
		System.Reflection.PropertyInfo code = option.GetProperty("Code")
			?? throw new MissingMemberException("LanguageOption.Code");
		System.Reflection.PropertyInfo display = option.GetProperty("Display")
			?? throw new MissingMemberException("LanguageOption.Display");
		return arr.Cast<object>()
			.Select(o => ((string)code.GetValue(o), (string)display.GetValue(o)))
			.ToArray();
	}

	[Fact]
	public void TheLanguageDropdownPutsEnglishFirstAndSortsTheRest()
	{
		(string Code, string Display)[] shown = ReadLanguages("Languages");

		Assert.Equal("en", shown[0].Code);

		// Ordinal on purpose, matching production: a culture-aware sort would order this differently on a Turkish
		// or Swedish machine, and the dropdown has to be the same list for everyone.
		string[] rest = shown.Skip(1).Select(l => l.Display).ToArray();
		Assert.Equal(rest.OrderBy(d => d, StringComparer.Ordinal).ToArray(), rest);
	}

	[Fact]
	public void TheDropdownOffersEveryLanguageExactlyOnce()
	{
		// The shown list is built from the source list with two predicates. Get either wrong and a language quietly
		// vanishes from the dropdown or appears twice - both still compile and both look fine until someone goes
		// looking for their own language, so count and compare rather than trusting the LINQ.
		(string Code, string Display)[] all = ReadLanguages("AllLanguages");
		(string Code, string Display)[] shown = ReadLanguages("Languages");

		Assert.Equal(all.Length, shown.Length);
		Assert.Equal(shown.Length, shown.Select(l => l.Code).Distinct().Count());
		Assert.Equal(
			all.Select(l => l.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray(),
			shown.Select(l => l.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray());
	}

	[Fact]
	public void EveryLanguageOfferedHasStringsBehindIt()
	{
		string[] orphans = ReadLanguages("Languages")
			.Select(l => l.Code)
			.Where(c => !Strings.ContainsKey(c))
			.ToArray();

		Assert.True(orphans.Length == 0,
			"The dropdown offers languages with no translations behind them: " + string.Join(", ", orphans));
	}

	// ------------------------------------------------------------------ H. drive health must not depend on language
	//
	// A drive's health is a fact about the drive. It was being decided by searching the drive's *translated* health
	// label for the English word "OK", so the three languages that translate that label outright - Chinese
	// ("健康：正常"), Japanese ("状態: 正常") and Hindi ("स्थिति: ठीक") - reported a perfectly healthy drive as failing
	// and advised replacing it. The other fourteen happen to leave "OK" inside the translation, which is the only
	// reason it survived this long, and is exactly why no future translation may be trusted to keep it.

	private static object NewDisk(string healthStatus, string operationalStatus) =>
		Activator.CreateInstance(
			Mw.Nested("DiskItem"),
			4, "SSK Portable SSD 256", "USB", "SSD", healthStatus, operationalStatus,
			238_500_000_000L, "GPT", false, new List<char> { 'X' });

	private static string RawOf(object disk) =>
		(string)Mw.Nested("DiskItem").GetProperty("RawHealth").GetValue(disk);

	private static void UseLanguage(string code) =>
		typeof(MainWindow)
			.GetField("currentLanguage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
			.SetValue(null, code);

	[Fact]
	public void DriveHealthIsJudgedTheSameInEveryLanguage()
	{
		object healthy = NewDisk("Healthy", "OK");
		object failing = NewDisk("Warning", "OK");
		List<string> wrong = new List<string>();

		try
		{
			foreach (string lang in Strings.Keys)
			{
				UseLanguage(lang);
				if (!Mw.Call<bool>("IsHealthy", RawOf(healthy)))
					wrong.Add($"{lang}: a healthy drive is reported as failing");
				if (Mw.Call<bool>("IsHealthy", RawOf(failing)))
					wrong.Add($"{lang}: a drive reporting Warning is reported as fine");
			}
		}
		finally { UseLanguage("en"); }

		Assert.True(wrong.Count == 0,
			"The drive health verdict changes with the interface language:\n  " + string.Join("\n  ", wrong));
	}

	[Fact]
	public void TheRawHealthStatusCarriesNoTranslatedText()
	{
		// Whatever the language, the string the decisions read is Windows' own wording.
		try
		{
			foreach (string lang in Strings.Keys)
			{
				UseLanguage(lang);
				Assert.Equal("Healthy/OK", RawOf(NewDisk("Healthy", "OK")));
				Assert.Equal("Warning/OK", RawOf(NewDisk("Warning", "OK")));
				Assert.Equal("Healthy", RawOf(NewDisk("Healthy", "")));
			}
		}
		finally { UseLanguage("en"); }
	}
}
