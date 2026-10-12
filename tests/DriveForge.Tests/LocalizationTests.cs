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
		// The baseline is empty, and that is the point: it held sixteen keys with the instruction to
		// delete them, and they were deleted (272 translations with them). A key nothing shows is text
		// that gets translated, reviewed and carried forever while reaching nobody - and it reads as live
		// to the next person who greps for a message and finds it. Keep this empty: wire a new key up or
		// delete it, rather than recording it here.
		var baseline = new HashSet<string>();

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
		// ...and the other three doors onto the same TextBlock. The rule above matches
		// `<Name>Text.Text = "literal"`, which is one of FOUR ways the app writes the status line: SetBusy
		// forwards its `status` argument to StatusText.Text, SetStage assigns it outright, and SetToolStatus
		// writes the tools card's own line. Measured 2026-10-12: 147 such calls, exactly ONE with English
		// typed into it — the multi-boot install, whose own finally comment quoted the string it left behind.
		// One is all it takes, and no rule could see it.
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (string call in new[] { "SetBusy", "SetStage", "SetToolStatus" })
				foreach (InvocationExpressionSyntax inv in SourceModel.Calls(root, call))
				{
					var args = inv.ArgumentList.Arguments;
					// SetBusy's status is its second argument (named or positional); the other two take it first.
					ArgumentSyntax status = call == "SetBusy"
						? args.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "status")
							?? args.Skip(1).FirstOrDefault()
						: args.FirstOrDefault();
					if (status is null || !IsTypedProse(status.Expression)) continue;
					offenders.Add($"{SourceModel.Where(file, inv)}  {call}(…, {Trim80(status.ToString())})");
				}

		Assert.True(offenders.Count == 0,
			"UI text typed into the source instead of coming from a string key:\n  " + string.Join("\n  ", offenders));
	}

	/// <summary>
	/// A dialog's words come from the table — its body AND its title.
	///
	/// The campaign has audited the two other channels user-facing text travels down: thrown sentences
	/// (ShowError prints ex.Message verbatim — 84 of them) and widget text (the rule above, and
	/// EveryNamedVisibleControlGetsItsTextFromSomewhereTranslatable). A MessageBox built from literals is
	/// the third, and nothing watched it. Measured 2026-10-12 over 243 MessageBox.Show calls: FOUR were
	/// English end to end and one more had an English title over a translated body.
	///
	/// They were not minor ones. The drive-health gate — the dialog that says the drive you are about to
	/// ERASE is showing SMART warnings, shown by the clone, the Windows-USB build and all three restore
	/// paths — was English, frame and warning items both, and the items were hand-typed copies of SmHealth
	/// / SmWear / SmReadTotal / SmReadUnc / SmWriteTotal / SmWriteUnc, which have existed in all seventeen
	/// languages the whole time. The scheduling dialog that sets up an unattended, recurring whole-disk
	/// clone was English. And the support prompt, shown after EVERY completed task, was the most-seen
	/// English dialog in the app — beside a Settings page whose own Ko-fi button was translated.
	///
	/// The filter that matters is the one for a key lookup, and it has to strip the WHOLE call: `L(cond ?
	/// "KeyA" : "KeyB")` is how eight of this rule's twelve first hits were written, and a filter that only
	/// understood `L("Literal")` called every one of them English because it could see the key names inside.
	/// Two words of three letters or more is prose; "DriveForge" alone is the brand.
	/// </summary>
	[Fact]
	public void EveryDialogTakesItsWordsFromTheTable()
	{
		var offenders = new List<string>();
		int dialogs = 0;
		foreach (var (file, root) in SourceModel.Parsed)
			foreach (InvocationExpressionSyntax inv in SourceModel.Calls(root, "Show"))
			{
				if (inv.Expression is not MemberAccessExpressionSyntax ma
						|| (ma.Expression as IdentifierNameSyntax)?.Identifier.Text != "MessageBox") continue;
				var args = inv.ArgumentList.Arguments;
				if (args.Count == 0) continue;
				dialogs++;
				foreach (var (index, what) in new[] { (0, "body"), (1, "title") })
				{
					if (index >= args.Count) continue;
					if (!IsTypedProse(args[index].Expression)) continue;
					offenders.Add($"{SourceModel.Where(file, inv)}  {what}: {Trim80(args[index].ToString())}");
				}
			}

		Assert.True(dialogs > 200,
			$"Only {dialogs} MessageBox.Show call(s) found; there were 243. Re-read this rule rather than "
				+ "leaving it passing over a scope that has moved.");
		Assert.True(offenders.Count == 0,
			"A dialog's words are typed into the source, so sixteen of seventeen languages read it in "
				+ "English:\n  " + string.Join("\n  ", offenders));
	}

	/// <summary>
	/// Is this argument prose somebody reads, rather than a key lookup or a value substituted into one?
	///
	/// Walked as syntax rather than scrubbed as text, because text-scrubbing got it wrong twice in two
	/// different ways and both were measured:
	///   - stripping `L\(([^()]*)\)` misses `L(DetectWipeMedia(disk) == WipeMedia.Flash ? "A" : "B")`,
	///     whose own parentheses defeat the pattern, and then reports the key names inside it as English;
	///   - and string.Format's ARGUMENTS are not text. `string.Format(L("MbBootTestConfirm"), …,
	///     (uefi ? "UEFI" : "BIOS"), …)` put two perfectly good words in reach of any literal scan — and
	///     they are technical tokens the app keeps identical in all seventeen languages on purpose.
	/// Only the format TEMPLATE is the sentence; the rest are values.
	///
	/// A conditional counts as prose if EITHER branch does, for the same reason the thrown-sentence rule
	/// examines branches one at a time: a translated branch must not vouch for an English one.
	/// </summary>
	private static bool IsTypedProse(ExpressionSyntax e) => e switch
	{
		ParenthesizedExpressionSyntax p => IsTypedProse(p.Expression),
		ConditionalExpressionSyntax c => IsTypedProse(c.WhenTrue) || IsTypedProse(c.WhenFalse),
		BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) =>
			IsTypedProse(b.Left) || IsTypedProse(b.Right),
		// This arm is what gives the rule its REACH, not its quiet: without it a
		// `string.Format("English template {0}.", x)` body is never looked at (mutation-checked both ways).
		InvocationExpressionSyntax f when IsStringFormat(f) =>
			f.ArgumentList.Arguments.Count > 0 && IsTypedProse(f.ArgumentList.Arguments[0].Expression),
		// Any L(...) is translated by construction, however its key is chosen. This arm is DOCUMENTATION:
		// the default below already answers false for it, as mutation-checking showed. It stays so that a
		// reader does not conclude an L(...) inside a concatenation is unhandled.
		InvocationExpressionSyntax l when (l.Expression as IdentifierNameSyntax)?.Identifier.Text == "L" => false,
		LiteralExpressionSyntax lit when lit.Token.IsKind(SyntaxKind.StringLiteralToken) =>
			ReadsAsProse(lit.Token.ValueText),
		InterpolatedStringExpressionSyntax interp => ReadsAsProse(string.Concat(
			interp.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText))),
		// Anything this does not understand is NOT reported. The rule is deliberately quiet rather than
		// noisy: a dialog assembled by a helper is out of reach, and a false alarm in a rule that gates
		// every build is worse than a gap a later screen can close.
		_ => false,
	};

	private static bool IsStringFormat(InvocationExpressionSyntax inv) =>
		inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Format"
			&& ma.Expression.ToString() is "string" or "String";

	/// <summary>Two words of three letters or more. One token is a brand ("DriveForge"), a unit or an
	/// identifier; two is a sentence.</summary>
	private static bool ReadsAsProse(string text) =>
		Regex.Matches(Regex.Replace(text, @"\{[^}]*\}", " "), "[A-Za-z]{3,}").Count >= 2;

	/// <summary>
	/// The verdict a diagnostic tool ends on comes from the dictionary, never from the source.
	///
	/// The two rules above match a literal assigned straight to a widget, and that is not how a verdict is
	/// written: it is built into a local over several branches and assigned at the end. The surface test's
	/// five endings were typed in English that way and no rule here could see them - while the confirmation
	/// that opens the same tool, its title, its progress line and its error message were all translated. So
	/// the one sentence in the app that says a drive may be dying reached sixteen of seventeen languages in
	/// a language the reader may not have, under a window that was otherwise entirely in theirs.
	///
	/// This follows the assignment back: the locals it names in the same method, and one step into a helper
	/// it calls (which is how the health card's verdict is built). A literal counts as text somebody reads
	/// when it holds two words - that is what separates a sentence from the property names the predictive
	/// verdict looks up ("ReadErrorsUncorrected") and from punctuation and units.
	///
	/// The report pane underneath (SetToolOutput) is deliberately NOT covered: it carries raw diskpart,
	/// CHKDSK and PowerShell output verbatim on twenty other screens and is English everywhere. The verdict
	/// is the sentence the user acts on; the report is the evidence under it.
	/// </summary>
	[Fact]
	public void TheVerdictOnTheToolsCardIsNeverTypedInEnglish()
	{
		var offenders = new List<string>();
		foreach (var (file, root) in SourceModel.Parsed)
		{
			foreach (AssignmentExpressionSyntax a in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
			{
				if (a.Left is not MemberAccessExpressionSyntax ma || ma.Name.Identifier.Text != "Text") continue;
				if (ma.Expression is not IdentifierNameSyntax widget
					|| widget.Identifier.Text != "ToolRecommendationDetailText") continue;
				var method = a.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
				if (method == null) continue;

				// Follow the assignment back until the chain stops growing. ONE hop is not enough: the coverage line
				// is a string declared fifty lines above the verdict, so it is named inside `verdict` and not inside
				// `verdict.Replace(...)`. Measured - the first draft of this rule did not reach it.
				//
				// Only STRING locals and string-returning helpers. Following every name reached into
				// RunSurfaceScanCore and flagged its "Could not open the disk for reading." - an exception message,
				// not a verdict, and a false positive here is how a rule gets loosened later.
				var sources = new List<SyntaxNode>();
				var pending = new Queue<SyntaxNode>();
				var walked = new HashSet<string>(StringComparer.Ordinal) { "L" };
				pending.Enqueue(a.Right);
				while (pending.Count > 0)
				{
					SyntaxNode here = pending.Dequeue();
					sources.Add(here);
					foreach (string name in here.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
							.Select(i => i.Identifier.Text).Distinct().ToList())
					{
						if (!walked.Add(name)) continue;
						foreach (var v in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
							if (v.Identifier.Text == name && v.Initializer != null && IsString(
									(v.Parent as VariableDeclarationSyntax)?.Type.ToString()))
								pending.Enqueue(v.Initializer.Value);
						foreach (var asg in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
							if (asg.Left is IdentifierNameSyntax lid && lid.Identifier.Text == name)
								pending.Enqueue(asg.Right);
						// Into a helper that hands back text, so moving the wording into a method does not hide it.
						foreach (var (_, r) in SourceModel.Parsed)
							foreach (var m in r.DescendantNodes().OfType<MethodDeclarationSyntax>())
								if (m.Identifier.Text == name && m.ReturnType.ToString().Contains("string"))
									sources.Add(m);
					}
				}

				foreach (SyntaxNode node in sources)
					foreach (string sentence in SentencesTypedIn(node))
						offenders.Add($"{SourceModel.Where(file, a)}: \"{sentence}\"");
			}
		}
		Assert.True(offenders.Count == 0,
			"A tool verdict is written in English in the source instead of coming from a string key"
				+ " (this is the sentence the user reads):\n  " + string.Join("\n  ", offenders.Distinct()));
	}

	/// <summary>
	/// The amount the speed test says it writes is the amount it writes.
	///
	/// Four sentences quote that size, and they quote it for a reason: it is why a Good result is a
	/// narrow claim ("tested over 80 MB"), why the recommendation says the test is too short to see
	/// whether the drive holds its speed, and why the time estimate says where its number came from.
	/// It checks the number and not the unit, because four of the seventeen write the megabyte in
	/// their own alphabet and the file is already mixed on which to use.
	///
	/// The size itself is not a constant anywhere - it is two loop bounds multiplied by two buffer
	/// sizes. Change one loop from 64 to 256 and all four sentences are wrong in seventeen languages,
	/// with nothing in the diff to see it by. The one place that already quoted a size before this
	/// (MbSpeedWritesWarn, "writes about 80 MB into this drive's free space") was unguarded too.
	/// </summary>
	[Fact]
	public void WhatTheSpeedTestSaysItWritesIsWhatItWrites()
	{
		var method = SourceModel.Methods().Single(m => m.Name == "MeasureDiskSpeed").Method;
		var loops = method.DescendantNodes().OfType<ForStatementSyntax>().ToList();
		Assert.True(loops.Count == 2,
			$"MeasureDiskSpeed has {loops.Count} write loops, not the two this rule knows how to read.");

		long written = 0;
		foreach (ForStatementSyntax loop in loops)
		{
			// `for (int i = 0; i < 64; i++)` - the rounds.
			var bound = Assert.IsType<BinaryExpressionSyntax>(loop.Condition);
			int rounds = int.Parse(Assert.IsType<LiteralExpressionSyntax>(bound.Right).Token.ValueText);
			// ...writing THIS buffer: `stream.Write(buf, 0, buf.Length)`.
			string buffer = loop.DescendantNodes().OfType<InvocationExpressionSyntax>()
				.Where(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "Write")
				.Select(i => (i.ArgumentList.Arguments[0].Expression as IdentifierNameSyntax)?.Identifier.Text)
				.First(n => n != null)!;
			// ...which is this big: `byte[] buf = new byte[1048576];`.
			int size = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
				.Where(v => v.Identifier.Text == buffer).Select(v => v.Initializer?.Value)
				.OfType<ArrayCreationExpressionSyntax>()
				.Select(ac => int.Parse(((LiteralExpressionSyntax)ac.Type.RankSpecifiers[0].Sizes[0])
					.Token.ValueText)).Single();
			written += (long)rounds * size;
		}
		long mb = written / (1024 * 1024);

		string[] quoted = { "MbSpeedWritesWarn", "DSpdMsgGood", "DSpdRecGood", "EstFromBurst" };
		var wrong = new List<string>();
		foreach (string key in quoted)
			foreach (string lang in Strings.Keys)
			{
				if (!Strings[lang].TryGetValue(key, out string? value))
					wrong.Add($"{lang}/{key}: missing");
				// The NUMBER, not the unit: four languages localise the megabyte (fr "80 Mo", ru and uk
				// "80 МБ", ar "80 ميغابايت"), the file is already mixed on it, and which word a language uses
				// for a megabyte is not this rule's business. The digit boundaries stop 80 matching 800.
				else if (!Regex.IsMatch(value, $@"(?<![0-9]){mb}(?![0-9])"))
					wrong.Add($"{lang}/{key}: {value}");
			}
		Assert.True(wrong.Count == 0, $"The speed test writes {mb} MB, and these sentences quote a"
			+ $" different size, or none:\n  " + string.Join("\n  ", wrong));
	}

	/// <summary>
	/// A flow that recycles never reports a size as freed.
	///
	/// A recycled file is still on the volume - Windows moves it into $Recycle.Bin on the same clusters -
	/// so free space does not change by a byte. File.Exists goes false, which is what made this easy to
	/// get wrong: the disk-space analyzer counted every vanished file into a total it called "freed", in
	/// all seventeen languages, on the one screen whose entire purpose is reclaiming space. The same
	/// message sat beside an Undo button, and one dialog earlier the app had said the files could be
	/// restored from the bin - a claim that cannot hold at the same time as the other.
	///
	/// The app had the answer already. The Clean screen, in the same tab control, branches on whether it
	/// recycled or deleted and has a string for it: "no space is freed until you empty it". So this is a
	/// rule and not a correction - two flows recycle, and they disagreed.
	///
	/// It reads the ENGLISH value, like its siblings here: every key in this file is written in English
	/// first and translated in a batch of seventeen, and PlaceholderSetsMatchEnglishInEveryLanguage
	/// already holds the shapes together. A translation that invented the claim on its own would get
	/// past this; a key added to a recycling flow would not.
	/// </summary>
	[Fact]
	public void RecyclingIsNeverReportedAsFreeingSpace()
	{
		// FOF_ALLOWUNDO (SHFileOperation) and RecycleOption.SendToRecycleBin are the two ways the app asks
		// for the bin. Everything else deletes for real and may say freed.
		var recycling = SourceModel.Methods()
			.Where(m => m.Method.ToString().Contains("FOF_ALLOWUNDO")
				|| m.Method.ToString().Contains("SendToRecycleBin"))
			.ToList();
		Assert.True(recycling.Count > 0, "No recycling flow found - this rule has stopped looking at anything.");
		
		// The key may be used in a helper the flow calls (the undo toast is one), so take one step in.
		var bodies = new List<(string Where, string Code)>();
		foreach (var (file, method, name) in recycling)
		{
			bodies.Add((name, method.ToString()));
			foreach (string called in Regex.Matches(method.ToString(), @"\b([A-Z][A-Za-z0-9_]*)\s*\(")
				.Select(m => m.Groups[1].Value).Distinct())
				foreach (var (_, m2, n2) in SourceModel.Methods())
					if (n2 == called) bodies.Add(($"{name} -> {n2}", m2.ToString()));
		}
		
		var offenders = new List<string>();
		foreach (var (where, code) in bodies)
			foreach (Match use in Regex.Matches(code, @"L\(\s*""([A-Za-z0-9_]+)"""))
			{
				string key = use.Groups[1].Value;
				if (!Strings[Base].TryGetValue(key, out string? en)) continue;
				// A SIZE claimed as freed - the placeholder next to the word is what makes it a claim about
				// this operation. CleanDoneRecycled's own "no space is freed until you empty it" says the
				// word and is the opposite of the defect, which is why adjacency and not the word is the test.
				if (Regex.IsMatch(en, @"freed\s*\{\d\}|\{\d\}\s*(?:of\s+)?freed", RegexOptions.IgnoreCase))
					offenders.Add($"{where} -> {key}: \"{en}\"");
			}
		Assert.True(offenders.Count == 0,
			"These flows send files to the Recycle Bin, where those files keep using the same space,"
				+ " and then report that space as freed:\n  "
				+ string.Join("\n  ", offenders.Distinct()));
	}

	/// <summary>
	/// How much of a clone gets byte-compared on screen is how much gets byte-compared.
	///
	/// "Verify cloned data after writing" promised, in all seventeen languages, to "re-read every file
	/// from the drive and compare it to the original to catch a bad USB stick". It does not. Every file
	/// is checked for presence and exact SIZE - metadata, no read-back - and the byte-for-byte compare
	/// runs on boot-critical paths plus a rotating 1-in-N sample of the rest. Seven files in eight are
	/// never re-read, and a stick that flips a byte inside a large file has exactly the right size, so
	/// the failure the tooltip names is the failure the method cannot see.
	///
	/// This had already been found once. BuildFullRootUsbCloneReportText spells the method out and says
	/// so in a comment: the old wording "promised a byte-compare of every file, and the verdict below
	/// then claimed no silent corruption exists - a guarantee a size check cannot give". That correction
	/// was made to the English report and never reached the sentence the user reads before ticking the
	/// box, which is the only place the promise is made in their own language.
	///
	/// The tooltip now carries the number, so this ties it to the constant. One assertion covers both
	/// ways it can go wrong: a tooltip that drops the number (the old wording had none) and a constant
	/// that moves away from the tooltip.
	/// </summary>
	[Fact]
	public void TheShareOfACloneThatIsByteComparedIsTheShareOnScreen()
	{
		string code = File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", "MainWindow.cs"));
		var decl = Regex.Match(code, @"const\s+int\s+ContentVerifySampleEvery\s*=\s*(\d+)");
		Assert.True(decl.Success,
			"ContentVerifySampleEvery is gone - this rule no longer knows what the verifier samples.");
		string every = decl.Groups[1].Value;
		
		var wrong = new List<string>();
		foreach (string lang in Strings.Keys)
		{
			if (!Strings[lang].TryGetValue("VerifyContentCheckTip", out string? tip))
			{
				wrong.Add($"{lang}: missing");
				continue;
			}
			// The number, with digit boundaries so 8 does not match 18 or 80. Which words a language puts
			// around it is its own business; that it tells the user the share is not.
			if (!Regex.IsMatch(tip, $@"(?<![0-9]){every}(?![0-9])"))
				wrong.Add($"{lang}: {tip}");
		}
		Assert.True(wrong.Count == 0,
			$"The verifier byte-compares boot files plus 1 in {every} of the rest. The checkbox that offers"
				+ " it must say that share, and these do not:\n  " + string.Join("\n  ", wrong));
	}

	/// <summary>A declaration that holds text. Nothing else is followed back.</summary>
	private static bool IsString(string? declaredType) =>
		declaredType is "string" or "string?" or "String" or "String?";

	/// <summary>Literals under <paramref name="node"/> that read as a sentence rather than as a lookup
	/// name, a unit or punctuation. A key inside L("...") is not one.</summary>
	private static IEnumerable<string> SentencesTypedIn(SyntaxNode node)
	{
		foreach (SyntaxNode n in node.DescendantNodesAndSelf())
		{
			string text;
			// The runs of one $"..." are ONE sentence and are judged together. Taken apart, the coverage line
			// ($"Read {x} of {y} ({z}%)") is "Read ", " of ", " (" and "%)" - four single words, and it walked
			// straight through the two-word test below. Measured: the first draft of this rule did not see it.
			if (n is InterpolatedStringExpressionSyntax istr)
				text = string.Join(" ", istr.Contents.OfType<InterpolatedStringTextSyntax>()
					.Select(t => t.TextToken.ValueText));
			else if (n is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.StringLiteralToken))
			{
				// The argument of L("Key") is the name of a string, not a string anybody reads.
				if (n.Parent is ArgumentSyntax arg && arg.Parent?.Parent is InvocationExpressionSyntax inv
					&& (inv.Expression as IdentifierNameSyntax)?.Identifier.Text == "L") continue;
				text = lit.Token.ValueText;
			}
			else continue;

			// Two words of two or more letters. One word is a property name or a unit; no words is punctuation.
			if (Regex.IsMatch(text, @"[A-Za-z]{2,}[^A-Za-z]+[A-Za-z]{2,}"))
				yield return Regex.Replace(text.Trim(), @"\s+", " ");
		}
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

	// ------------------------------------------------------------------ I. translations that fell behind the English

	/// <summary>
	/// A translation may be worded freely, but it cannot quietly describe an OLDER version of the feature.
	///
	/// This is the failure that produced it: the recovery tool grew from NTFS-only to NTFS/exFAT/FAT, gained disk
	/// images, existing files and image preview, and stopped being merely "fast". The English said so. All sixteen
	/// other languages still advertised "recover files from a drive (NTFS)" - so a Romanian user with an exFAT stick
	/// was told, by the app, that it was not supported. Nothing caught it: the key existed, was non-empty and had no
	/// placeholder mismatch, which is all the other tests here check.
	///
	/// The detector: some names are identifiers, not words, and are never translated in any language. If the English
	/// value names one and the translation does not, the translation almost certainly predates an edit to the English.
	///
	/// Deliberately NOT listed: "Secure Boot", "Windows To Go", "Trusted Platform Module". Those genuinely are
	/// translated - sicherer Start, demarrage securise, (Japanese katakana) - and flagging them would teach everyone
	/// to ignore this test, which is how a rule stops being a rule.
	///
	/// Comparison is case-SENSITIVE, which is what separates S.M.A.R.T. from the ordinary word in "Smart clean".
	/// </summary>
	[Fact]
	public void TranslationsKeepTheTechnicalNamesTheEnglishUses()
	{
		string[] identifiers = { "NTFS", "exFAT", "FAT32", "BitLocker", "DISM", "VHDX", "Hyper-V", "TPM", "ReFS", "WinPE" };
		List<string> stale = new List<string>();

		foreach (KeyValuePair<string, string> entry in Strings[Base])
		{
			string[] named = identifiers.Where(id => entry.Value.Contains(id, StringComparison.Ordinal)).ToArray();
			if (named.Length == 0) continue;

			foreach (string lang in OtherLanguages)
			{
				// A missing key is a different defect, and AllKeysExistInEveryLanguage already reports it.
				if (!Strings[lang].TryGetValue(entry.Key, out string? translated)) continue;

				string[] lost = named.Where(id => !translated.Contains(id, StringComparison.Ordinal)).ToArray();
				if (lost.Length > 0)
					stale.Add($"{lang}/{entry.Key}: the English names {string.Join(", ", lost)}, the translation does not."
						+ $"\n      en: {entry.Value}\n      {lang}: {translated}");
			}
		}

		Assert.True(stale.Count == 0,
			"These translations look like they describe an older version of the feature than the English does "
			+ "(if a term here is genuinely translated in that language, add it to the exclusions above rather "
			+ "than loosening the rule):\n  " + string.Join("\n  ", stale));
	}

	// ------------------------------------------------------------------ J. sentences that name another task
	//
	// A few strings send the reader somewhere else - "...use 'Restore image' instead". The name used to be written
	// into the sentence, so the sentence was translated and the name was not: sixteen languages pointed at a button
	// whose label reads something different ("Restaurează imagine", "イメージを復元"). They now carry a reference,
	// [[SbRestoreT]], resolved at display time from the same key the sidebar draws its label from.

	// Names that are the same word in every language: a sentence carrying one of these is not quoting a
	// label, it is using the word. Without this, "der Download hat zu fruh aufgehort" reads as German prose
	// naming an English button.
	private static readonly HashSet<string> UntranslatedNames = new(StringComparer.Ordinal)
	{
		"DriveForge", "Windows", "BitLocker", "Hyper-V", "NTFS", "exFAT", "FAT32", "USB", "SSD", "HDD",
		"ISO", "WIM", "ESD", "VHDX", "VHD", "SMART", "TRIM", "CHKDSK", "DISM", "MBR", "GPT", "UEFI",
		"BIOS", "Linux", "Ventoy", "Rufus", "Explorer", "Microsoft", "GitHub", "ZIP", "PowerShell",
	};

	/// <summary>
	/// What makes a string a LABEL - something a sentence could point a reader at - rather than a list of
	/// which labels anyone remembered to check. Every clause below removed a measured false positive:
	///
	/// * length: a one-word key is usually a word, and a paragraph is never a button.
	/// * no placeholder and no newline: a format string or a dialog body is not a label.
	/// * TWO WORDS at least. This is the one that matters. A single English word inside German, Italian or
	///   Dutch prose is a loanword - "Der Download hat zu fruh aufgehort", "SMART-Attribute" - and one
	///   message points at VirtualBox's own Settings menu, not ours.
	/// * not a name that is identical in every language (see UntranslatedNames).
	/// </summary>
	private static bool IsLabelLike(string english) =>
		english.Length >= 9 && english.Length <= 46
		&& !english.Contains('{') && !english.Contains("\\n") && !english.Contains("[[")
		&& english.Trim().Contains(' ')
		&& !UntranslatedNames.Contains(english.Trim());

	/// <summary>The label text, and the same without a trailing parenthetical: a sentence quotes
	/// "RUNNING WINDOWS", not "RUNNING WINDOWS (no format/erase)".</summary>
	private static IEnumerable<string> LabelForms(string label)
	{
		string t = label.Trim();
		if (t.Length >= 9) yield return t;
		string trimmed = Regex.Replace(t, @"\s*\([^)]*\)\s*$", "").Trim();
		if (trimmed.Length >= 9 && trimmed != t) yield return trimmed;
	}

	/// <summary>`needle` inside `hay` on word boundaries, so "DriveForge - format" does not match inside
	/// "DriveForge - formatare".</summary>
	private static bool NamesIt(string hay, string needle)
	{
		for (int i = hay.IndexOf(needle, StringComparison.Ordinal); i >= 0;
			 i = hay.IndexOf(needle, i + 1, StringComparison.Ordinal))
		{
			char before = i > 0 ? hay[i - 1] : ' ';
			int j = i + needle.Length;
			char after = j < hay.Length ? hay[j] : ' ';
			if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after)) return true;
		}
		return false;
	}

	[Fact]
	public void EveryTaskReferenceResolvesToARealKey()
	{
		// An unknown reference does not crash - it renders as the key's own name - which is exactly why it would
		// reach a user unnoticed.
		List<string> bad = new List<string>();
		foreach (string lang in Strings.Keys)
			foreach (KeyValuePair<string, string> entry in Strings[lang])
				foreach (Match m in Regex.Matches(entry.Value, @"\[\[([A-Za-z0-9_]+)\]\]"))
					if (!Strings[Base].ContainsKey(m.Groups[1].Value))
						bad.Add($"{lang}/{entry.Key} refers to [[{m.Groups[1].Value}]], which is not a string key.");

		Assert.True(bad.Count == 0, "A sentence refers to a key that does not exist:\n  " + string.Join("\n  ", bad));
	}

	[Fact]
	public void EverySentenceWithAReferenceShowsThatLanguagesLabel()
	{
		// End to end, through L() itself, for EVERY sentence that points at a control - not one hand-picked key,
		// which is what this checked before while two hundred sentences elsewhere wrote their names out by hand.
		//
		// This is the half its sibling above cannot see. That one catches a sentence that kept the ENGLISH name;
		// this one catches a sentence that names the control SOMETHING ELSE ENTIRELY - the Romanian multi-boot
		// message said "Test de pornire" for a button whose Romanian label reads "Test boot", and no search for
		// English text could ever have found it.
		//
		// ENGLISH declares the pointers. The first version of this test looped over the strings that still
		// CONTAIN a reference, and was green under all eight mutations: deleting the reference from a
		// translation takes that string out of the loop, so the single edit the rule exists to catch was the
		// one edit it could not see. A rule must be told what to expect by something the mutation cannot erase.
		var declared = Strings[Base]
			.SelectMany(e => Regex.Matches(e.Value, @"\[\[([A-Za-z0-9_]+)\]\]")
				.Select(m => (Key: e.Key, Name: m.Groups[1].Value)))
			.Distinct()
			.ToArray();
		Assert.True(declared.Length >= 10,
			$"Only {declared.Length} English sentences declare a reference - the pointers have gone missing.");

		List<string> bad = new List<string>();
		int checksRun = 0;
		try
		{
			foreach (string lang in Strings.Keys)
			{
				UseLanguage(lang);
				foreach (var (key, nameKey) in declared)
				{
					// A key missing in this language falls back to English inside L(); AllKeysExistInEveryLanguage
					// reports that, and repeating it here would only add noise.
					if (!Strings[lang].ContainsKey(key)) continue;
					if (!Strings[lang].TryGetValue(nameKey, out string? label)) continue;
					string shown = Mw.Call<string>("L", key);
					if (shown.Contains("[["))
						bad.Add($"{lang}/{key}: an unexpanded reference reached the text - {shown}");
					checksRun++;
					if (!shown.Contains(label, StringComparison.Ordinal))
						bad.Add($"{lang}/{key} does not show this language's label for {nameKey} "
							+ $"(expected \"{label}\"): {shown}");
				}
			}
		}
		finally { UseLanguage("en"); }

		// A table that silently emptied would make this test pass by checking nothing.
		Assert.True(checksRun >= 150, $"Only {checksRun} reference renders were checked - the sentences that name a control by reference have gone missing.");
		Assert.True(bad.Count == 0, "References are not resolving to this language's label:\n  " + string.Join("\n  ", bad));
	}

	[Fact]
	public void NoTranslationNamesAControlInEnglishWhenItRenamesIt()
	{
		// The rule that found this. If a language renames a control, no other string in that language may still
		// carry the English name - the reader would be sent to a button that is not there under that label.
		//
		// This used to run over THIRTEEN HAND-WRITTEN sidebar task names, and was green while 212 sites were
		// broken: DkSystemDisk, OpenImageButton, TbExportVhdx, StartClone, AnalyzeKeepFirstButton,
		// DiagDriveToTestLabel, TbTestBoot and ChkCleanRecycle were all outside the list. Four of the thirteen
		// names did not exist either (SbClonePortableT for SbClonePT, and three more), and TryGetValue skipped
		// them in silence - a third of the reach aimed at nothing. A predicate has no names to get wrong.
		List<string> bad = new List<string>();

		foreach (KeyValuePair<string, string> label in Strings[Base])
		{
			string nameKey = label.Key;
			string english = label.Value;
			if (!IsLabelLike(english)) continue;

			foreach (string lang in OtherLanguages)
			{
				// Only when this language actually uses a different label.
				if (!Strings[lang].TryGetValue(nameKey, out string? local) || local == english) continue;
				// ...and not when this language's own label still carries the English name inside it, which is
				// how Indonesian writes "Recycle Bin (semua drive)". Then the sentence matches the screen.
				if (LabelForms(english).Any(f => local.Contains(f, StringComparison.Ordinal))) continue;

				foreach (KeyValuePair<string, string> entry in Strings[lang])
				{
					if (entry.Key == nameKey) continue;                       // the name itself
					if (entry.Value.Contains("[[", StringComparison.Ordinal)) continue;   // already a reference
					if (!LabelForms(english).Any(f => NamesIt(entry.Value, f))) continue;
					bad.Add($"{lang}/{entry.Key} names the control in English (\"{english}\"), but this language "
						+ $"calls it \"{local}\". Use [[{nameKey}]] so it follows the label.");
				}
			}
		}

		Assert.True(bad.Count == 0,
			"Translations send the reader to a button by its English name:\n  " + string.Join("\n  ", bad));
	}

	/// <summary>
	/// Both renderers have to expand references, not just L().
	///
	/// MEASURED ON SCREEN, in Romanian, after the two rules above were green: the duplicate-files hint read
	/// 'apasă „[[AnalyzeKeepFirstButton]]”' — the raw markup, on a panel, to a user. ApplyLanguage writes the
	/// dictionary value STRAIGHT into every control whose Name matches a key, so references only ever worked for
	/// the keys that happen to be rendered through L() in code, and nothing anywhere recorded which keys those
	/// were. The suite could not see it: its own checks call L().
	///
	/// A string does not know which renderer will pick it up, so every renderer has to behave the same way.
	/// </summary>
	[Fact]
	public void EveryRendererExpandsReferences()
	{
		var problems = new List<string>();
		foreach (string name in new[] { "ApplyLanguage", "ApplyToolTips" })
		{
			var found = SourceModel.Methods().Where(m => m.Name == name).ToArray();
			if (found.Length != 1)
			{
				problems.Add($"expected one {name}, found {found.Length}");
				continue;
			}
			string code = found[0].Method.ToString();
			bool expands = Regex.IsMatch(code, @"TaskRefPattern\s*\.\s*Replace")
				|| Regex.IsMatch(code, @"\bExpandTaskRefs\s*\(")
				|| Regex.IsMatch(code, @"=\s*L\(");
			if (!expands)
				problems.Add($"{name} hands text to controls without expanding [[Key]], so a sentence that names "
					+ "another control reaches the screen as literal markup");
		}
		Assert.True(problems.Count == 0,
			"A renderer that does not expand references:\n  " + string.Join("\n  ", problems));
	}

	/// <summary>
	/// A sentence never names a control nobody can see.
	///
	/// The reference sweep fixed the drift where a sentence copies a control's name and the copy goes stale.
	/// This is its MIRROR: a sentence that names something nothing on screen is called. MbBackupCreated — the
	/// backup's one instruction on how to get the data back — said: choose "Advanced: restore full disk image".
	/// That is an item of ModeBox, the ComboBox that used to be the task selector and now sits inside a Border
	/// with Visibility="Collapsed" which nothing ever shows; the sidebar replaced it. All seventeen languages
	/// pointed at it. Thirteen kept the English phrase and four translated it, so those four named something
	/// that exists in no language at all.
	///
	/// Scope is DERIVED. XAML says which named controls sit under a collapsed element; the code says which of
	/// those elements can be shown again (only a NAMED one can — nothing else holds a handle on it); whatever
	/// is left is unreachable, because a collapsed parent hides its whole subtree whatever the children say.
	/// Its captions are then read from both places a caption comes from: the XAML attribute and the code that
	/// assigns one. A rule over a hand-written list of phrases would have gone stale the first time the
	/// ComboBox changed — which is exactly how the rule next door came to have four dead entries in it.
	///
	/// Two filters, each of which earned its place by removing a measured false positive out of 185,895 pairs:
	///   - EQUALITY is fine, containment is not. A caption that IS a value is that control's own key, and
	///     ApplyLanguage writing a translation into a hidden control harms nobody. The defect is a caption
	///     sitting INSIDE a longer sentence, which is a sentence sending the reader to find it.
	///   - 18 characters. "Select a disk" (RecommendationText, 13) appears inside PtNoDisk in English and
	///     inside HintDisk, RfPickDrive, DToolSelectBegin and five more in ro/fr/it/pt/nl/ru/pl/uk/hi/id/ar —
	///     31 hits, every one of them ordinary prose. At 18 all six ModeBox items (33–54), CreateKitButton's
	///     caption (30) and Step5Title (18) are still covered.
	///
	/// What it does NOT catch: a translator who INVENTS a name for the hidden control, as ro/fr/de/nl had.
	/// That half is held by EverySentenceWithAReferenceShowsThatLanguagesLabel — once the sentence carries
	/// [[SbRestoreT]], every language's copy of it has to contain that language's own sidebar label.
	/// </summary>
	[Fact]
	public void NoSentenceNamesAControlNobodyCanSee()
	{
		var (xaml, code) = WindowSources();

		var captions = new HashSet<string>(StringComparer.Ordinal);
		var unreachable = new List<string>();
		foreach (System.Xml.Linq.XElement root in UnreachableSubtrees(xaml, code))
			foreach (System.Xml.Linq.XElement el in root.DescendantsAndSelf())
			{
				foreach (string attr in new[] { "Text", "Content", "Header" })
				{
					string v = (string)el.Attribute(attr) ?? "";
					if (v.Length > 0 && !v.StartsWith("{", StringComparison.Ordinal) && Regex.IsMatch(v, "[A-Za-z]{3}"))
						captions.Add(v);
				}
				string name = NameOf(el);
				if (name.Length == 0) continue;
				unreachable.Add(name);
				foreach (Match m in Regex.Matches(code,
						Regex.Escape(name) + @"\s*\.\s*(?:Items\.Add\(|Content\s*=|Text\s*=|Header\s*=)\s*""([^""]+)"""))
					captions.Add(m.Groups[1].Value);
			}

		// The premise is asserted, not assumed: if the window ever stops hiding anything, this rule has stopped
		// protecting anything and should be read again rather than left passing.
		Assert.True(unreachable.Count > 0,
			"No named control is unreachable any more, so this rule now checks nothing — re-read it.");

		string[] names = captions.Where(c => c.Trim().Length >= 18 && c.Trim().Contains(' ')).ToArray();
		Assert.True(names.Length >= 6,
			$"Only {names.Length} caption(s) of an unreachable control are long enough to protect; the six "
				+ "ModeBox items alone used to make it six. Has the caption scan stopped finding them?");

		var found = new List<string>();
		foreach (string lang in Strings.Keys)
			foreach (var (key, value) in Strings[lang])
				foreach (string name in names)
					if (!string.Equals(value, name, StringComparison.Ordinal)
							&& value.Contains(name, StringComparison.Ordinal))
						found.Add($"{key} [{lang}] names \"{name}\"");

		Assert.True(found.Count == 0,
			"These sentences send the reader to a control that is not on screen. Name the control that really "
				+ "does it, as [[ItsKey]], so the text follows the label instead of a copy of it:\n  "
				+ string.Join("\n  ", found));
	}

	private static string NameOf(System.Xml.Linq.XElement el) =>
		(string)el.Attribute("Name")
			?? (string)el.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
			?? "";

	/// <summary>
	/// The XAML subtrees nothing in the app can bring back on screen: a Visibility="Collapsed" element whose
	/// own .Visibility is never assigned in code. Only a NAMED element can be, because nothing else holds a
	/// handle on it, and WPF hides every descendant with its collapsed parent whatever the children say.
	///
	/// One owner, asked by both rules that need it. It was written out twice first, and the copies were
	/// identical for about ten minutes - which is the same mistake as a sentence copying a control's name.
	/// </summary>
	private static IEnumerable<System.Xml.Linq.XElement> UnreachableSubtrees(string xaml, string code)
	{
		foreach (System.Xml.Linq.XElement root in System.Xml.Linq.XDocument.Parse(xaml).Descendants())
		{
			if ((string)root.Attribute("Visibility") != "Collapsed") continue;
			string rootName = NameOf(root);
			if (rootName.Length > 0
					&& Regex.IsMatch(code, @"\b" + Regex.Escape(rootName) + @"\s*\.\s*Visibility\s*=")) continue;
			yield return root;
		}
	}

	/// <summary>The window's XAML and the code that renders into it — the two inputs both reachability
	/// rules read.</summary>
	private static (string Xaml, string Code) WindowSources() => (
		File.ReadAllText(Path.Combine(Mw.RepoRoot, "MainWindow.xaml")),
		string.Concat(new[] { "MainWindow.cs", "UiCustomization.cs" }
			.Select(f => File.ReadAllText(Path.Combine(Mw.RepoRoot, "DriveForge", f)))));

	/// <summary>A leading "2. " / "3) " — the shape of a numbered step heading.</summary>
	private static readonly Regex LeadingEnumerator = new(@"^\s*\d{1,2}[.)]\s", RegexOptions.Compiled);

	/// <summary>
	/// The panel's step numbering is the same in every language, and the headings a person can see are not
	/// numbered at all.
	///
	/// Read off the screen 2026-10-12. In ENGLISH the Create-USB panel reads "Windows source / Target drive
	/// (USB / SSD / HDD) / Options" — no numbers — and five separate messages pointed at numbers anyway:
	/// both orange readiness hints ("Choose a Windows ISO / WIM / ESD in step 2.") and the three
	/// too-early refusals ("Select the target drive first (step 3)."). In Romanian the same headings read
	/// "2. Sursă Windows / 3. Țintă / 4. Opțiuni" and those five messages matched — so English, the language
	/// the author reads and every translation's reference, was the inconsistent one.
	///
	/// And the numbering could not be rescued. Slot 2's heading is swapped for Step2ImageFile or
	/// Step2IsoImage when the task changes, and those carry no number in ANY language, so the Romanian screen
	/// then read "Fișier imagine / 3. Țintă / 4. Opțiuni". The sidebar replaced the old step-1 task picker and
	/// Step5Section is collapsed, so the sequence started at 2 and ended at 4 with neither 1 nor 5 on screen.
	///
	/// Two halves. The first needs no vocabulary and is the real guard: ENGLISH decides. A rule that looked
	/// for the WORD for "step" beside a digit would need a list of that word in seventeen languages — the
	/// hand-written list this campaign keeps finding behind a green rule — and measuring the obvious
	/// alternative (the same numbers in every language) gave 30 false positives out of 19,408 pairs, all of
	/// them honest translation: Arabic writes "one pass" as a word, Japanese adds a counter to "4 GB per
	/// file". Whether a value STARTS with an enumerator is not a translator's choice, so that one is exact.
	///
	/// The second says what the screen's design now is, so renumbering every language at once cannot quietly
	/// pass the first. Its scope is derived the same way NoSentenceNamesAControlNobodyCanSee derives its
	/// own — which is what keeps Step5Title, numbered in all seventeen and collapsed out of sight, from
	/// having to be named in an exception list.
	/// </summary>
	[Fact]
	public void TheStepNumberingAgreesWithEnglishAndNoVisibleHeadingIsNumbered()
	{
		var disagree = new List<string>();
		int pairs = 0;
		foreach (var (key, enValue) in Strings[Base])
		{
			bool enNumbered = LeadingEnumerator.IsMatch(enValue);
			foreach (string lang in Strings.Keys.Where(l => l != Base))
			{
				if (!Strings[lang].TryGetValue(key, out string value)) continue;
				pairs++;
				if (LeadingEnumerator.IsMatch(value) != enNumbered)
					disagree.Add($"{key} [{lang}] {(enNumbered ? "drops" : "adds")} a leading number: "
						+ $"[en] \"{Trim80(enValue)}\" vs [{lang}] \"{Trim80(value)}\"");
			}
		}
		Assert.True(pairs > 15000, $"Only {pairs} pairs compared; the table should give far more.");
		Assert.True(disagree.Count == 0,
			"A heading is numbered in one language and not in another, so the panel reads as a numbered "
				+ "sequence for some people and not for others — and any sentence pointing at a step number "
				+ "is then right for some and wrong for the rest:\n  " + string.Join("\n  ", disagree));

		// The headings a person can actually see. Reachability is derived, not listed, by the same owner
		// NoSentenceNamesAControlNobodyCanSee asks — which is what keeps Step5Title, numbered in all
		// seventeen languages and collapsed out of sight, from needing a name in an exception list.
		var (xaml, code) = WindowSources();
		var unreachable = new HashSet<string>(StringComparer.Ordinal);
		foreach (System.Xml.Linq.XElement root in UnreachableSubtrees(xaml, code))
			foreach (System.Xml.Linq.XElement el in root.DescendantsAndSelf())
				if (NameOf(el).Length > 0) unreachable.Add(NameOf(el));

		var headings = new List<string>();
		foreach (Match m in Regex.Matches(xaml, @"\bName=""(Step\d+(?:Title|Desc))"""))
			if (!unreachable.Contains(m.Groups[1].Value)) headings.Add(m.Groups[1].Value);

		Assert.True(headings.Count >= 3,
			$"Only {headings.Count} reachable step heading(s) found; the panel has three plus a description. "
				+ "Re-read this rule rather than leaving it passing over a scope that has moved.");

		var numbered = new List<string>();
		foreach (string name in headings)
			foreach (string lang in Strings.Keys)
				if (Strings[lang].TryGetValue(name, out string value) && LeadingEnumerator.IsMatch(value))
					numbered.Add($"{name} [{lang}] = \"{Trim80(value)}\"");

		Assert.True(numbered.Count == 0,
			"A heading a person can see carries a step number. The panel is not a numbered sequence any "
				+ "more: the sidebar replaced step 1, step 5 is collapsed, and slot 2's own heading changes "
				+ "with the task. If numbering comes back it has to come back everywhere, including the two "
				+ "alternate titles for slot 2:\n  " + string.Join("\n  ", numbered));
	}

	private static string Trim80(string s)
	{
		string t = Regex.Replace(s, @"\s+", " ").Trim();
		return t.Length > 80 ? t.Substring(0, 80) + "…" : t;
	}
}
