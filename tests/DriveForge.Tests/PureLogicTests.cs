using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace DriveForge.Tests;

/// <summary>
/// Regression tests for the pure helpers. Every case marked REGRESSION reproduces a bug that actually shipped —
/// if one of these ever fails again, that exact defect is back.
/// All expected values were measured against the real implementation, not assumed.
/// </summary>
public class PureLogicTests
{
	// ---------------------------------------------------------------- NaturalSortKey

	[Theory]
	[InlineData("22", "00000022")]
	[InlineData("22.1", "00000022.00000001")]
	[InlineData("abc", "abc")]
	[InlineData("", "")]
	[InlineData("v4.2.0", "v00000004.00000002.00000000")]
	public void NaturalSortKey_ZeroPadsDigitRuns(string input, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("NaturalSortKey", input));

	/// <summary>
	/// REGRESSION — "Download an ISO" picked the OLDER Linux Mint release.
	///
	/// The catalog scraper sorted directory names with the trailing '/' still attached. Ordinal comparison puts
	/// '/' (0x2F) AFTER '.' (0x2E), so "22/" sorted after "22.1/" and .Last() returned the base release instead of
	/// the newest point release. The fix trims the slash BEFORE sorting. This test pins both halves: trimmed-first
	/// gives the right answer, and the untrimmed form is still wrong — so nobody "simplifies" the trim back out.
	/// </summary>
	[Fact]
	public void NaturalSortKey_Regression_TrailingSlashMustBeTrimmedBeforeSorting()
	{
		string[] dirs = { "22/", "22.1/" };

		string newestTrimmedFirst = dirs
			.Select(d => d.Trim('/'))
			.OrderBy(d => Mw.Call<string>("NaturalSortKey", d), StringComparer.Ordinal)
			.Last();
		Assert.Equal("22.1", newestTrimmedFirst);

		string newestUntrimmed = dirs
			.OrderBy(d => Mw.Call<string>("NaturalSortKey", d), StringComparer.Ordinal)
			.Last().Trim('/');
		Assert.Equal("22", newestUntrimmed);   // documents the defect: the base release wins
		Assert.NotEqual(newestTrimmedFirst, newestUntrimmed);
	}

	// ---------------------------------------------------------------- FormatDuration

	[Theory]
	[InlineData(0, "00:00:00")]
	[InlineData(5, "00:00:05")]
	[InlineData(5400, "01:30:00")]
	public void FormatDuration_FormatsClock(int seconds, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("FormatDuration", TimeSpan.FromSeconds(seconds)));

	/// <summary>
	/// REGRESSION — elapsed/remaining silently dropped whole days.
	///
	/// `hh\:mm\:ss` renders the hours COMPONENT (0-23), not total hours, so a 30-hour multi-pass wipe displayed
	/// "06:00:00" and the elapsed clock wrapped to zero every 24 h. Reachable with a 7-pass wipe on a large HDD.
	/// </summary>
	[Theory]
	[InlineData(30, "1.06:00:00")]
	[InlineData(200, "8.08:00:00")]
	public void FormatDuration_Regression_KeepsDaysPast24h(int hours, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("FormatDuration", TimeSpan.FromHours(hours)));

	[Fact]
	public void FormatDuration_ClampsNegativeToZero() =>
		Assert.Equal("00:00:00", Mw.Call<string>("FormatDuration", TimeSpan.FromSeconds(-5)));

	// ---------------------------------------------------------------- FormatBytes

	[Theory]
	[InlineData(0L, "0.0 B")]
	[InlineData(1023L, "1023.0 B")]
	[InlineData(1024L, "1.0 KB")]
	[InlineData(1048576L, "1.0 MB")]
	[InlineData(1073741824L, "1.0 GB")]
	[InlineData(512110190592L, "476.9 GB")]   // the 512 GB SSD used in hardware testing
	public void FormatBytes_ScalesUnits(long bytes, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("FormatBytes", bytes));

	// ---------------------------------------------------------------- ExtractJsonPayload

	[Theory]
	[InlineData("[{\"a\":1}]", "[{\"a\":1}]")]
	[InlineData("{\"a\":1}", "{\"a\":1}")]                       // single object, not an array
	[InlineData("WARNING: noisy line\n[{\"a\":1}]", "[{\"a\":1}]")] // skips a pre-JSON warning
	public void ExtractJsonPayload_FindsPayload(string raw, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("ExtractJsonPayload", raw));

	/// <summary>
	/// REGRESSION — "0 disks, Ready, no error" on a machine that could not enumerate disks at all.
	///
	/// PowerShell exits 0 when a cmdlet in a NON-final statement is missing, so on WinPE without StorageWMI
	/// `Get-Disk` failed non-terminatingly, nothing reached stdout, and this helper returned its "[]" sentinel.
	/// The caller could not tell that apart from a genuine empty result and reported a perfectly successful scan.
	/// GetDisksAsync now distinguishes the two by ALSO checking the raw output — this pins the sentinel behaviour
	/// that check depends on.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("Get-Disk : The term 'Get-Disk' is not recognized...")]
	[InlineData("null")]
	public void ExtractJsonPayload_Regression_ReturnsSentinelWhenNoJson(string raw) =>
		Assert.Equal("[]", Mw.Call<string>("ExtractJsonPayload", raw));

	// ---------------------------------------------------------------- Crc32 (GPT headers)

	/// <summary>
	/// Known-answer test. This is CRC-32/ISO-HDLC (the zlib/PNG/GPT variant); its documented check value for the
	/// ASCII string "123456789" is 0xCBF43926. GPT header and partition-entry CRCs are computed with this — a wrong
	/// CRC makes a disk unbootable and makes the partition-table backup unrestorable, so it is worth pinning exactly.
	/// </summary>
	[Fact]
	public void Crc32_MatchesIsoHdlcCheckValue() =>
		Assert.Equal(0xCBF43926u, Mw.Call<uint>("Crc32", Encoding.ASCII.GetBytes("123456789"), 0, 9));

	[Fact]
	public void Crc32_EmptyRangeIsZero() =>
		Assert.Equal(0u, Mw.Call<uint>("Crc32", new byte[8], 0, 0));

	[Fact]
	public void Crc32_HonoursOffsetAndLength()
	{
		byte[] padded = new byte[] { 0xFF, 0xFF }.Concat(Encoding.ASCII.GetBytes("123456789")).Concat(new byte[] { 0xFF }).ToArray();
		Assert.Equal(0xCBF43926u, Mw.Call<uint>("Crc32", padded, 2, 9));
	}

	// ---------------------------------------------------------------- GPT header round-trip

	/// <summary>
	/// FixGptHeaderCrc must produce a header that VerifyGptHeader accepts. This pairing is what makes the
	/// partition-table backup written before a GPT partition move actually restorable — a stale CRC there means the
	/// backup fails its own self-check exactly when someone needs it.
	/// </summary>
	[Fact]
	public void GptHeader_FixThenVerify_RoundTrips()
	{
		byte[] entries = new byte[128 * 128];
		new Random(1234).NextBytes(entries);

		byte[] header = new byte[512];
		Encoding.ASCII.GetBytes("EFI PART").CopyTo(header, 0);       // signature
		Mw.Call("WriteUInt32", header, 8, 0x00010000u);              // revision 1.0
		Mw.Call("WriteUInt32", header, 12, 92u);                     // header size
		Mw.Call("WriteInt64", header, 24, 1L);                       // MyLBA
		Mw.Call("WriteUInt32", header, 80, 128u);                    // NumberOfPartitionEntries
		Mw.Call("WriteUInt32", header, 84, 128u);                    // SizeOfPartitionEntry

		Assert.False(Mw.Call<bool>("VerifyGptHeader", header, entries, 92));  // CRCs not written yet
		Mw.Call("FixGptHeaderCrc", header, entries, 92);
		Assert.True(Mw.Call<bool>("VerifyGptHeader", header, entries, 92));

		entries[0] ^= 0xFF;                                          // corrupt one entry byte
		Assert.False(Mw.Call<bool>("VerifyGptHeader", header, entries, 92));
	}

	// ---------------------------------------------------------------- Health verdict

	/// <summary>
	/// REGRESSION — a failed drive was reported as healthy.
	///
	/// The gate used to be a substring test, and "Unhealthy" CONTAINS "healthy" — so the one status that means the
	/// media has failed read as fine. Any implementation that reintroduces a naive Contains fails this.
	/// </summary>
	[Theory]
	[InlineData("Healthy", true)]
	[InlineData("OK", true)]
	[InlineData("Unhealthy", false)]
	[InlineData("Warning", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void IsHealthy_Regression_UnhealthyIsNotHealthy(string? status, bool expected) =>
		Assert.Equal(expected, Mw.Call<bool>("IsHealthy", status));

	// ---------------------------------------------------------------- Process-argument quoting

	[Theory]
	[InlineData("plain", "\"plain\"")]
	[InlineData("has space", "\"has space\"")]
	[InlineData("", "\"\"")]
	public void QuoteArgument_AlwaysQuotes(string input, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("QuoteArgument", input));

	/// <summary>
	/// Command-line injection guard: a path containing a quote, or ending in a backslash, must not be able to break
	/// out of its argument. These arguments reach diskpart/dism/wimlib, which act on whole disks.
	/// </summary>
	[Theory]
	[InlineData("quote\"inside", "\"quote\\\"inside\"")]
	[InlineData("trail\\", "\"trail\\\\\"")]
	public void QuoteArgument_EscapesQuotesAndTrailingBackslash(string input, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("QuoteArgument", input));

	// ---------------------------------------------------------------- Misc pure helpers

	[Theory]
	[InlineData((byte)0x07, "NTFS/exFAT")]
	[InlineData((byte)0x0B, "FAT32")]
	[InlineData((byte)0xEE, "type 0xEE")]
	public void PartTypeName_NamesKnownTypes(byte type, string expected) =>
		Assert.Equal(expected, Mw.Call<string>("PartTypeName", type));

	[Theory]
	[InlineData("1024", "MiB", 1.0)]
	[InlineData("2", "GiB", 2.0)]
	[InlineData("512", "MiB", 0.5)]
	public void ConvertToGiB_Converts(string value, string unit, double expected) =>
		Assert.Equal(expected, Mw.Call<double>("ConvertToGiB", value, unit), 6);

	/// <summary>
	/// Progress parsing must be culture-invariant. A comma-decimal locale (ro-RO, de-DE) previously broke the
	/// speed/ETA readout entirely; this pins the invariant parse.
	/// </summary>
	[Fact]
	public void ConvertToGiB_IsCultureInvariant()
	{
		var prior = System.Globalization.CultureInfo.CurrentCulture;
		try
		{
			System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ro-RO");
			Assert.Equal(1.5, Mw.Call<double>("ConvertToGiB", "1536", "MiB"), 6);
		}
		finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
	}

	[Theory]
	[InlineData("abc", true)]
	[InlineData("ABC123 -_", true)]
	[InlineData("café", false)]
	[InlineData("日本", false)]
	public void IsPureAscii_DetectsNonAscii(string s, bool expected) =>
		Assert.Equal(expected, Mw.Call<bool>("IsPureAscii", s));

	/// <summary>
	/// Recovered files are written using names taken from a damaged filesystem. A name like "CON" or "COM1" is a
	/// reserved DOS device and cannot be created on Windows — recovery must rename rather than fail or, worse,
	/// write to the device.
	/// </summary>
	[Theory]
	[InlineData("CON")]
	[InlineData("com1")]
	[InlineData("AUX")]
	[InlineData("NUL")]
	public void MakeNonReservedFileName_RewritesReservedDeviceNames(string name) =>
		Assert.NotEqual(name, Mw.Call<string>("MakeNonReservedFileName", name));

	[Theory]
	[InlineData("normal.txt")]
	[InlineData("console.log")]   // merely CONTAINS a device name — must be left alone
	public void MakeNonReservedFileName_LeavesOrdinaryNames(string name) =>
		Assert.Equal(name, Mw.Call<string>("MakeNonReservedFileName", name));

	/// <summary>
	/// Path-traversal guard: a filename harvested from a damaged filesystem must never be able to escape the
	/// recovery output folder.
	/// </summary>
	[Theory]
	[InlineData("..\\..\\Windows\\System32")]
	[InlineData("C:\\Windows")]
	[InlineData("/etc/passwd")]
	public void SanitizeRelativeDir_NeverEscapesTheOutputFolder(string input)
	{
		string result = Mw.Call<string>("SanitizeRelativeDir", input);
		Assert.DoesNotContain("..", result);
		Assert.False(System.IO.Path.IsPathRooted(result), $"'{result}' is rooted — it would escape the output folder");
	}

	// ------------------------------------------------- DiskItem.IsLikelyUsbOrExternal

	// The disk pickers are sorted so the DEFAULT selection is a drive it is safe to erase, and this property is
	// what decides. Reached by reflection because DiskItem is a private nested record - see Mw's note on why the
	// production code is not widened for tests.
	private static bool IsExternal(string friendlyName, string busType)
	{
		Type t = typeof(DriveForge.MainWindow).GetNestedType("DiskItem",
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
			?? throw new MissingMemberException("MainWindow.DiskItem not found");
		object disk = Activator.CreateInstance(t, 0, friendlyName, busType, "SSD", "Healthy", "Online",
			1_000_000_000L, "GPT", false, new System.Collections.Generic.List<char>())!;
		return (bool)t.GetProperty("IsLikelyUsbOrExternal")!.GetValue(disk)!;
	}

	/// <summary>
	/// REGRESSION: the machine's own internal drive must never rank as "external".
	///
	/// The test used to answer yes for anything on SATA and for any model name containing "SSD" - which is how
	/// internal drives describe themselves. On the machine this was found on, that put the internal 954 GB NVMe
	/// at the top of the list and pre-selected it for Create Windows USB, Clone, Restore, Format and Wipe.
	/// </summary>
	[Theory]
	[InlineData("INTEL SSDPEKNU010TZ", "NVMe", false)]          // the measured case
	[InlineData("Samsung SSD 970 EVO Plus", "NVMe", false)]
	[InlineData("CT500MX500SSD1", "SATA", false)]
	[InlineData("WDC WDS100T2B0A", "SATA", false)]
	[InlineData("Msft Virtual Disk", "File Backed Virtual", false)]
	[InlineData("SanDisk Extreme 55DD", "USB", true)]
	[InlineData("SSK Port able SSD 256", "USB", true)]
	[InlineData("Generic MassStorageClass", "SD", true)]
	[InlineData("WD My Passport 25E2", "USB", true)]
	[InlineData("Seagate External Drive", "SATA", true)]        // the name says so for itself
	public void IsLikelyUsbOrExternal_OnlyCountsDrivesAttachedByCable(string name, string bus, bool expected) =>
		Assert.Equal(expected, IsExternal(name, bus));

	// ---------------------------------------------------------------- the clean sweep's reach

	/// <summary>
	/// REGRESSION (reported from the field, reproduced on this machine): closing DriveForge after cleaning
	/// traces raised "Dll was not found." out of a mixed-mode module's CRT teardown.
	///
	/// The release exe is PublishSingleFile + IncludeNativeLibrariesForSelfExtract, so at startup it unpacks its
	/// native libraries into %TEMP%\.net\&lt;app&gt;\&lt;hash&gt;\ and RUNS them from there - and the
	/// "Temporary files" category walks Path.GetTempPath() recursively. Measured on the published build: 12 of
	/// the 15 extracted files were deletable under the live process, vcruntime140_cor3.dll among them, and the
	/// crash log that followed matched the user's byte for byte.
	///
	/// Both guards are measured here, because they fire on different paths: the extraction base is normally
	/// reached as a CHILD of the temp folder (the reported case), while a direct call on the base itself is the
	/// other way in. A test that only did one of them would leave the reported path uncovered.
	/// </summary>
	[Fact]
	public void TheCleanSweepDoesNotCarryOffARunningProgramsOwnCode()
	{
		string temp = Path.GetTempPath();
		string baza = Path.Combine(temp, ".net");   // the single-file extraction base
		string tag = "dftest-" + Guid.NewGuid().ToString("N");
		string inCod = Path.Combine(baza, tag);       // stands in for a running app's unpacked code
		string inGunoi = Path.Combine(temp, tag);     // ordinary temp rubbish, fair game
		string fCod = Path.Combine(inCod, "vcruntime140_cor3.dll");
		// A file sitting DIRECTLY in the base. Without it the descent guard hides whether the entry guard
		// exists at all: the base holds only sub-folders, and those are skipped on the way down anyway -
		// so "nothing came back" would be true either way. Measured with a mutation, not assumed.
		string fInBaza = Path.Combine(baza, tag + ".probe");
		string fGunoi = Path.Combine(inGunoi, "leftover.tmp");
		try
		{
			Directory.CreateDirectory(inCod);
			Directory.CreateDirectory(inGunoi);
			File.WriteAllText(fCod, "x");
			File.WriteAllText(fGunoi, "x");
			File.WriteAllText(fInBaza, "x");
			
			// The sweep, exactly as the Temp category runs it.
			var swept = new HashSet<string>(
				Mw.Call<IEnumerable<string>>("SafeFiles", temp, "*", true), StringComparer.OrdinalIgnoreCase);
			
			Assert.True(swept.Contains(fGunoi),
				"The sweep no longer picks up ordinary temp files, so this test is proving nothing. "
					+ "Re-read it rather than leaving it passing over a scope that has moved.");
			Assert.False(swept.Contains(fCod),
				$"The clean sweep still reaches into {baza} - the folder a single-file .NET app unpacks itself "
					+ "into and runs from. Deleting that under a live process is what made a user's DriveForge throw "
					+ "DllNotFoundException on exit, and it would do the same to any other single-file app running.");
			
			// And the other way in: handed the base directly, it must refuse to enumerate it at all.
			var direct = new HashSet<string>(
				Mw.Call<IEnumerable<string>>("SafeFiles", baza, "*", true), StringComparer.OrdinalIgnoreCase);
			Assert.False(direct.Contains(fInBaza),
				$"Called straight on {baza}, the sweep handed back a file lying in it - the entry guard is missing, "
					+ "so any category naming that folder itself would empty a running app's code.");
		}
		finally
		{
			try { Directory.Delete(inCod, true); } catch { }
			try { Directory.Delete(inGunoi, true); } catch { }
			try { File.Delete(fInBaza); } catch { }
		}
	}

	// ---------------------------------------------------------------- the disk-space analyzer

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr sec);

	/// <summary>
	/// Runs the analyzer's REAL scan engine over a folder and hands back what it found.
	///
	/// AnalyzeScanCore is an instance method (it reads the stop flag), so <see cref="Mw"/> - which only reaches
	/// static members - cannot call it. An uninitialized MainWindow is enough and needs no UI thread: the core
	/// touches no control and no dispatcher, only static helpers and that one bool, which defaults to false.
	/// </summary>
	private static (int Files, long Total, int RedundantCount, long RedundantBytes, List<(int Group, string Path)> Dupes) Scan(string root)
	{
		var mw = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
		GC.SuppressFinalize(mw);
		MethodInfo mi = typeof(MainWindow).GetMethod("AnalyzeScanCore", BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new MissingMethodException("MainWindow.AnalyzeScanCore (private instance) not found");
		object res = mi.Invoke(mw, new object[] { root, new Progress<string>(_ => { }), new Progress<double>(_ => { }) })!;
		Type t = res.GetType();
		object Fld(string n) => t.GetField(n)!.GetValue(res)!;
		var dupes = new List<(int, string)>();
		foreach (object d in (System.Collections.IEnumerable)Fld("Dupes"))
		{
			Type dt = d.GetType();
			dupes.Add(((int)dt.GetProperty("Group")!.GetValue(d)!, (string)dt.GetProperty("FullPath")!.GetValue(d)!));
		}
		return ((int)Fld("FileCount"), (long)Fld("TotalSize"), (int)Fld("RedundantCount"), (long)Fld("RedundantBytes"), dupes);
	}

	private static void Bytes(string path, byte b, int n)
	{
		var buf = new byte[n];
		for (int i = 0; i < n; i++) buf[i] = b;
		File.WriteAllBytes(path, buf);
	}

	/// <summary>
	/// REGRESSION - the duplicate finder offered a hard-linked pair as "2 copies" and added the whole file size
	/// to the reclaimable total. Deleting either name frees nothing: the bytes are held by the name that stays.
	/// Measured on a real tree - 28,675 reclaimable claimed where 16,387 was the truth.
	/// </summary>
	[Fact]
	public void TwoNamesForOneFileAreNotOfferedAsTwoCopies()
	{
		string root = Path.Combine(Path.GetTempPath(), "df-an-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			Directory.CreateDirectory(Path.Combine(root, "a"));
			Directory.CreateDirectory(Path.Combine(root, "b"));
			// Two genuine copies - the positive control. If these stop being found the rest proves nothing.
			string c1 = Path.Combine(root, "a", "copie.bin"), c2 = Path.Combine(root, "b", "copie.bin");
			Bytes(c1, 0x41, 8192);
			File.Copy(c1, c2);
			// One file, two names.
			string l1 = Path.Combine(root, "legatura1.bin"), l2 = Path.Combine(root, "legatura2.bin");
			Bytes(l1, 0x5A, 12288);
			bool linked = CreateHardLinkW(l2, l1, IntPtr.Zero);
			Assert.True(linked, $"Could not create a hard link in {Path.GetTempPath()} (Win32 {Marshal.GetLastWin32Error()}). "
				+ "This test measures nothing without one - it needs an NTFS temp folder.");

			var scan = Scan(root);

			var real = scan.Dupes.Where(d => d.Path.Equals(c1, StringComparison.OrdinalIgnoreCase)
				|| d.Path.Equals(c2, StringComparison.OrdinalIgnoreCase)).ToList();
			Assert.True(real.Count == 2 && real[0].Group == real[1].Group,
				"The two genuine copies are no longer reported as one duplicate set, so this test is proving "
				+ "nothing about hard links. Re-read it rather than leaving it passing over a scope that has moved.");

			Assert.DoesNotContain(scan.Dupes, d => d.Path.Equals(l1, StringComparison.OrdinalIgnoreCase)
				|| d.Path.Equals(l2, StringComparison.OrdinalIgnoreCase));
			Assert.True(scan.RedundantBytes == 8192,
				$"The analyzer says {scan.RedundantBytes} bytes are reclaimable; only 8192 are. Two names for one "
				+ "file were counted as a second copy, and deleting one of them frees nothing at all.");
			Assert.True(scan.RedundantCount == 1, $"Expected 1 extra copy, the analyzer counted {scan.RedundantCount}.");
		}
		finally { try { Directory.Delete(root, true); } catch { } }
	}

	/// <summary>
	/// The funnel (same size -> same head+tail -> same SHA-256) must separate files that only LOOK alike, find
	/// the tiny ones, ignore the empty ones, and never walk through a junction. All five measured together on
	/// one real tree, because they are one promise: a row in that grid is a file you can delete.
	/// </summary>
	[Fact]
	public void TheDuplicateFunnelOnlyGroupsFilesThatReallyMatch()
	{
		string root = Path.Combine(Path.GetTempPath(), "df-an-" + Guid.NewGuid().ToString("N")[..8]);
		string outside = Path.Combine(Path.GetTempPath(), "df-afara-" + Guid.NewGuid().ToString("N")[..8]);
		string junction = Path.Combine(root, "jonctiune");
		try
		{
			Directory.CreateDirectory(root);
			Directory.CreateDirectory(outside);
			// Same size, same first 16KB AND same last 16KB, different in the middle: only a FULL hash can tell
			// these apart, so this is what stops the cheap pre-filter from being trusted as an answer.
			foreach ((string name, byte mid) in new[] { ("mijloc-A.bin", (byte)0x41), ("mijloc-B.bin", (byte)0x42) })
			{
				var buf = new byte[40960];
				for (int i = 16384; i < 16384 + 8192; i++) buf[i] = mid;
				File.WriteAllBytes(Path.Combine(root, name), buf);
			}
			Bytes(Path.Combine(root, "egal1.bin"), 0x01, 5000);   // same size, different from the first byte
			Bytes(Path.Combine(root, "egal2.bin"), 0x02, 5000);
			File.WriteAllText(Path.Combine(root, "mic1.txt"), "abc");  // 3 bytes - the floor is 1, so these count
			File.WriteAllText(Path.Combine(root, "mic2.txt"), "abc");
			File.WriteAllBytes(Path.Combine(root, "gol1.dat"), Array.Empty<byte>());
			File.WriteAllBytes(Path.Combine(root, "gol2.dat"), Array.Empty<byte>());
			// A junction holding a third copy: walking into it would double-count files that are not in this tree.
			File.WriteAllText(Path.Combine(outside, "mic3.txt"), "abc");
			var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
				$"/c mklink /J \"{junction}\" \"{outside}\"") { UseShellExecute = false, CreateNoWindow = true });
			mk!.WaitForExit();
			Assert.True(Directory.Exists(junction), "Could not create the junction, so the walk is unmeasured here.");

			var scan = Scan(root);
			var paths = scan.Dupes.Select(d => Path.GetFileName(d.Path)).ToList();

			Assert.True(paths.Count(p => p == "mic1.txt" || p == "mic2.txt") == 2,
				"The two 3-byte duplicates were not found, so nothing below is being measured. The size floor is 1 "
				+ "byte on purpose: a tiny file is still a duplicate.");
			Assert.DoesNotContain("mijloc-A.bin", paths);
			Assert.DoesNotContain("mijloc-B.bin", paths);
			Assert.DoesNotContain("egal1.bin", paths);
			Assert.DoesNotContain("egal2.bin", paths);
			Assert.DoesNotContain("gol1.dat", paths);
			Assert.DoesNotContain("mic3.txt", paths);
			Assert.True(scan.Files == 8, $"Expected the 8 files of this tree, the scan counted {scan.Files} - "
				+ "a junction is a door into another folder, not part of this one.");
		}
		finally
		{
			try { Directory.Delete(junction); } catch { }
			try { Directory.Delete(root, true); } catch { }
			try { Directory.Delete(outside, true); } catch { }
		}
	}
}
