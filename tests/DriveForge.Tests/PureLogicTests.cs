using System;
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
	public void IsHealthy_Regression_UnhealthyIsNotHealthy(string status, bool expected) =>
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
}
