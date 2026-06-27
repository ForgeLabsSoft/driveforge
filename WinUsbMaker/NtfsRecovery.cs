using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using Forms = System.Windows.Forms;

namespace WinUsbMaker;

// File "undelete" for NTFS and exFAT volumes. Scans the file-system metadata for deleted file records, lists
// them with their ORIGINAL name, path, size and date, lets the user pick which to recover, and rebuilds each
// file by reading its data clusters straight off the raw volume. No third-party engine; no network access.
// Recovers files whose data clusters have not yet been overwritten (the normal undelete case).
public partial class MainWindow
{
	public sealed class DeletedFile : INotifyPropertyChanged
	{
		private bool _selected;
		public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } } }
		public bool Deleted { get; set; } = true;
		public string Name { get; set; } = "";
		public string Path { get; set; } = "";
		public long Size { get; set; }
		public string SizeText { get; set; } = "";
		public DateTime? ModifiedUtc { get; set; }
		public string ModifiedText { get; set; } = "";
		public string Status { get; set; } = "";
		public bool Recoverable { get; set; }
		// Honest recoverability pill: StatusKind drives the colour dot (good/warn/bad/info), RecoverPercent the % text.
		// We never assert 100% on a carved or undelete result — the data could be partially overwritten.
		public string StatusKind { get; set; } = "info";
		public int RecoverPercent { get; set; } = -1;
		public string PercentText => RecoverPercent >= 0 ? RecoverPercent + "%" : "";
		// Recycle Bin recoveries: the data still exists as a $R file — recover = copy this path (zero risk).
		public string SourcePath = "";
		// NTFS payload:
		public bool Resident;
		public byte[]? ResidentData;
		public List<(long Lcn, long Count)> Runs = new();
		// exFAT payload:
		public bool ExFat;
		public long FirstCluster;
		public bool Contiguous;
		// Deep-scan (carving) payload: an absolute byte offset on the volume.
		public bool Carved;
		public long ByteOffset;
		public event PropertyChangedEventHandler? PropertyChanged;
	}

	private sealed class NtfsScanResult
	{
		public char Letter;
		public string ImagePath = "";  // when set, the source is a disk-image file instead of a live volume
		public bool IsExFat;
		public int ClusterSize;
		public int BytesPerSector;
		public long DataAreaOffset;   // exFAT: byte offset of cluster #2
		public long FatOffset;        // exFAT: byte offset of the FAT
		public List<DeletedFile> Files = new();
		// Deep-scan checkpoint: byte offset where a (paused/stopped) carving scan left off, so it can resume later.
		public long ResumeOffset;
		public bool DeepPartial;      // true if a deep scan was stopped before reaching the end
	}

	private NtfsScanResult? _lastScan;

	// Safety cap so listing existing + deleted files on a very large drive can't exhaust memory / freeze the grid.
	private const int MaxRecoverEntries = 200000;

	// ---- Save / resume scan session (.dfscan) ----
	// Persists a whole scan (source + every file's recovery payload) so the user can close DriveForge and reopen
	// the results later, then recover without re-scanning — the source volume/image is reopened on demand.
	private sealed class SessionFileDto
	{
		public bool Deleted { get; set; }
		public string Name { get; set; } = "";
		public string Path { get; set; } = "";
		public long Size { get; set; }
		public string SizeText { get; set; } = "";
		public string ModifiedUtc { get; set; } = "";
		public string ModifiedText { get; set; } = "";
		public string Status { get; set; } = "";
		public bool Recoverable { get; set; }
		public bool Resident { get; set; }
		public string ResidentData { get; set; } = "";
		public List<long[]> Runs { get; set; } = new();
		public bool ExFat { get; set; }
		public long FirstCluster { get; set; }
		public bool Contiguous { get; set; }
		public bool Carved { get; set; }
		public long ByteOffset { get; set; }
	}

	private sealed class SessionDto
	{
		public int Version { get; set; } = 1;
		public string Letter { get; set; } = "";
		public string ImagePath { get; set; } = "";
		public bool IsExFat { get; set; }
		public int ClusterSize { get; set; }
		public int BytesPerSector { get; set; }
		public long DataAreaOffset { get; set; }
		public long FatOffset { get; set; }
		public long ResumeOffset { get; set; }
		public bool DeepPartial { get; set; }
		public List<SessionFileDto> Files { get; set; } = new();
	}

	private void SaveSession(string path)
	{
		if (_lastScan == null) return;
		var dto = new SessionDto
		{
			Letter = _lastScan.Letter == '\0' ? "" : _lastScan.Letter.ToString(),
			ImagePath = _lastScan.ImagePath, IsExFat = _lastScan.IsExFat,
			ClusterSize = _lastScan.ClusterSize, BytesPerSector = _lastScan.BytesPerSector,
			DataAreaOffset = _lastScan.DataAreaOffset, FatOffset = _lastScan.FatOffset,
			ResumeOffset = _lastScan.ResumeOffset, DeepPartial = _lastScan.DeepPartial,
		};
		foreach (var f in _lastScan.Files)
			dto.Files.Add(new SessionFileDto
			{
				Deleted = f.Deleted, Name = f.Name, Path = f.Path, Size = f.Size, SizeText = f.SizeText,
				ModifiedUtc = f.ModifiedUtc?.ToString("o") ?? "", ModifiedText = f.ModifiedText, Status = f.Status,
				Recoverable = f.Recoverable, Resident = f.Resident,
				ResidentData = f.ResidentData != null ? Convert.ToBase64String(f.ResidentData) : "",
				Runs = f.Runs.Select(r => new[] { r.Lcn, r.Count }).ToList(),
				ExFat = f.ExFat, FirstCluster = f.FirstCluster, Contiguous = f.Contiguous, Carved = f.Carved, ByteOffset = f.ByteOffset,
			});
		File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(dto));
	}

	private NtfsScanResult LoadSession(string path)
	{
		var dto = System.Text.Json.JsonSerializer.Deserialize<SessionDto>(File.ReadAllText(path)) ?? throw new IOException("Empty or invalid session file.");
		if (dto.Version != 1) throw new IOException($"This .dfscan was made by a newer DriveForge (format v{dto.Version}). Update DriveForge to open it.");
		var scan = new NtfsScanResult
		{
			Letter = string.IsNullOrEmpty(dto.Letter) ? '\0' : dto.Letter[0],
			ImagePath = dto.ImagePath ?? "", IsExFat = dto.IsExFat,
			ClusterSize = dto.ClusterSize, BytesPerSector = dto.BytesPerSector,
			DataAreaOffset = dto.DataAreaOffset, FatOffset = dto.FatOffset,
			ResumeOffset = dto.ResumeOffset, DeepPartial = dto.DeepPartial,
		};
		foreach (var d in dto.Files)
		{
			var f = new DeletedFile
			{
				Deleted = d.Deleted, Name = d.Name, Path = d.Path, Size = d.Size, SizeText = d.SizeText,
				ModifiedUtc = DateTime.TryParse(d.ModifiedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null,
				ModifiedText = d.ModifiedText, Status = d.Status, Recoverable = d.Recoverable, Resident = d.Resident,
				ResidentData = string.IsNullOrEmpty(d.ResidentData) ? null : Convert.FromBase64String(d.ResidentData),
				ExFat = d.ExFat, FirstCluster = d.FirstCluster, Contiguous = d.Contiguous, Carved = d.Carved, ByteOffset = d.ByteOffset,
			};
			if (d.Runs != null) foreach (var r in d.Runs) if (r.Length >= 2) f.Runs.Add((r[0], r[1]));
			scan.Files.Add(f);
		}
		return scan;
	}

	// Raw, sector-aligned reader over a volume handle (\\.\X:).
	private sealed class VolumeReader : IDisposable
	{
		private readonly FileStream _fs;
		public VolumeReader(SafeFileHandle handle) { _fs = new FileStream(handle, FileAccess.Read); }
		public VolumeReader(string imagePath) { _fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20); }
		public byte[] Read(long offset, int length) => Read(offset, length, out _);
		// Like Read, but reports how many real bytes were available (the tail is still zero-padded). Callers that
		// must not invent data past end-of-device / a bad sector use `got` to stop instead of trusting the buffer.
		public byte[] Read(long offset, int length, out int got)
		{
			const int sector = 512;
			long alignedStart = offset - (offset % sector);
			int pad = (int)(offset - alignedStart);
			int toRead = ((pad + length + sector - 1) / sector) * sector;
			byte[] raw = new byte[toRead];
			_fs.Seek(alignedStart, SeekOrigin.Begin);
			int read = 0;
			while (read < toRead) { int r = _fs.Read(raw, read, toRead - read); if (r <= 0) break; read += r; }
			byte[] result = new byte[length];
			got = Math.Min(length, Math.Max(0, read - pad));
			Array.Copy(raw, pad, result, 0, got);
			return result;
		}
		// Reads straight into a caller buffer at a sector-aligned offset (used for fast bulk MFT reads).
		public int ReadInto(long alignedOffset, byte[] buffer, int count)
		{
			_fs.Seek(alignedOffset, SeekOrigin.Begin);
			int got = 0;
			while (got < count) { int r = _fs.Read(buffer, got, count - got); if (r <= 0) break; got += r; }
			return got;
		}
		public void Dispose() => _fs.Dispose();
	}

	private VolumeReader OpenVolume(char letter)
	{
		var h = CreateFile($"\\\\.\\{char.ToUpperInvariant(letter)}:", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException($"Could not open volume {letter}: for reading (run as administrator).");
		return new VolumeReader(h);
	}

	// Opens whichever source a scan came from: a live volume, or a disk-image file.
	private VolumeReader OpenSource(NtfsScanResult g) => string.IsNullOrEmpty(g.ImagePath) ? OpenVolume(g.Letter) : new VolumeReader(g.ImagePath);

	private static void ApplyFixup(byte[] rec, int baseOff, int recSize, int bytesPerSector)
	{
		int usaOff = BitConverter.ToUInt16(rec, baseOff + 0x04);
		int usaCount = BitConverter.ToUInt16(rec, baseOff + 0x06);
		if (usaCount < 1) return;
		ushort usn = BitConverter.ToUInt16(rec, baseOff + usaOff);
		for (int i = 1; i < usaCount; i++)
		{
			int sectorEnd = baseOff + i * bytesPerSector - 2;
			int usaEntry = baseOff + usaOff + i * 2;
			if (sectorEnd + 2 > baseOff + recSize || usaEntry + 2 > rec.Length || sectorEnd + 2 > rec.Length) break;
			if (BitConverter.ToUInt16(rec, sectorEnd) == usn)
			{
				rec[sectorEnd] = rec[usaEntry];
				rec[sectorEnd + 1] = rec[usaEntry + 1];
			}
		}
	}

	private static List<(long Lcn, long Count)> DecodeRuns(byte[] rec, int pos, int end)
	{
		var runs = new List<(long, long)>();
		long lcn = 0;
		while (pos < end && pos < rec.Length)
		{
			byte header = rec[pos++];
			if (header == 0) break;
			int lenBytes = header & 0x0F, offBytes = (header >> 4) & 0x0F;
			if (lenBytes == 0 || pos + lenBytes + offBytes > rec.Length) break;
			long count = 0;
			for (int i = 0; i < lenBytes; i++) count |= (long)rec[pos + i] << (8 * i);
			pos += lenBytes;
			if (offBytes == 0) { runs.Add((-1, count)); }
			else
			{
				long delta = 0;
				for (int i = 0; i < offBytes; i++) delta |= (long)rec[pos + i] << (8 * i);
				if ((rec[pos + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes);
				pos += offBytes; lcn += delta;
				runs.Add((lcn, count));
			}
		}
		return runs;
	}

	private static List<(long Lcn, long Count)> ReadMftRuns(VolumeReader vr, long mftByteOffset, int mftRecordSize, int bytesPerSector)
	{
		byte[] rec = vr.Read(mftByteOffset, mftRecordSize);
		ApplyFixup(rec, 0, mftRecordSize, bytesPerSector);
		int attrOff = BitConverter.ToUInt16(rec, 0x14);
		int guard = 0;
		while (attrOff + 8 <= rec.Length && guard++ < 64)
		{
			uint type = BitConverter.ToUInt32(rec, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(rec, attrOff + 0x04);
			if (attrLen <= 0) break;
			if (type == 0x80 && rec[attrOff + 0x08] != 0)
			{
				int runOff = BitConverter.ToUInt16(rec, attrOff + 0x20);
				return DecodeRuns(rec, attrOff + runOff, attrOff + attrLen);
			}
			attrOff += attrLen;
		}
		return new List<(long, long)>();
	}

	private static DateTime? FileTimeToDate(long ft)
	{
		try { return ft > 0 ? DateTime.FromFileTimeUtc(ft) : (DateTime?)null; } catch { return null; }
	}

	// ---- NTFS scan (bulk-read for speed; resolves paths and dates) ----
	private NtfsScanResult ScanNtfs(char letter, VolumeReader vr, byte[] boot, CancellationToken ct, Action<int> progress)
	{
		int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
		int sectorsPerCluster = boot[0x0D];
		if (bytesPerSector == 0 || sectorsPerCluster == 0) throw new IOException("Unreadable NTFS boot sector.");
		int clusterSize = bytesPerSector * sectorsPerCluster;
		long mftStartCluster = BitConverter.ToInt64(boot, 0x30);
		sbyte cpr = unchecked((sbyte)boot[0x40]);
		int recSize = cpr > 0 ? cpr * clusterSize : 1 << (-cpr);
		if (recSize <= 0 || recSize > 1 << 20) recSize = 1024;

		long mftByteOffset = mftStartCluster * (long)clusterSize;
		var mftRuns = ReadMftRuns(vr, mftByteOffset, recSize, bytesPerSector);
		if (mftRuns.Count == 0) mftRuns.Add((mftStartCluster, 64)); // fallback

		var result = new NtfsScanResult { Letter = letter, ClusterSize = clusterSize, BytesPerSector = bytesPerSector };
		var deleted = new List<(DeletedFile f, long parent)>();
		var dirNames = new Dictionary<long, (string name, long parent)>();

		long totalClusters = mftRuns.Where(r => r.Lcn >= 0).Sum(r => r.Count);
		long doneClusters = 0;
		int recsPerCluster = Math.Max(1, clusterSize / recSize);
		long globalIndex = 0;
		// Read the MFT run by run, in big chunks (fast sequential I/O instead of per-record seeks).
		const int chunkClusters = 256;
		foreach (var (lcn, count) in mftRuns)
		{
			if (ct.IsCancellationRequested) break;
			if (lcn < 0) { globalIndex += count * recsPerCluster; continue; }
			long c = 0;
			while (c < count)
			{
				if (ct.IsCancellationRequested) break;
				int take = (int)Math.Min(chunkClusters, count - c);
				int bytes = take * clusterSize;
				byte[] buf = new byte[bytes];
				vr.ReadInto((lcn + c) * (long)clusterSize, buf, bytes);
				int recsInBuf = bytes / recSize;
				for (int r = 0; r < recsInBuf; r++)
				{
					long idx = globalIndex + r;
					int off = r * recSize;
					if (off + 0x30 > buf.Length) break;
					if (buf[off] != (byte)'F' || buf[off + 1] != (byte)'I' || buf[off + 2] != (byte)'L' || buf[off + 3] != (byte)'E') continue;
					ApplyFixup(buf, off, recSize, bytesPerSector);
					ushort flags = BitConverter.ToUInt16(buf, off + 0x16);
					bool inUse = (flags & 0x01) != 0;
					bool isDir = (flags & 0x02) != 0;

					// Collect directory names (in-use or not) so we can rebuild paths.
					if (isDir)
					{
						var (dn, dp) = ReadNameAndParent(buf, off, recSize);
						if (!string.IsNullOrEmpty(dn)) dirNames[idx] = (dn, dp);
						continue;
					}
					if (inUse) continue;

					if (idx < 16) continue; // skip NTFS metafiles ($MFT, $LogFile, …)
					var df = ParseDeletedRecord(buf, off, recSize, out long parent);
					if (df == null || string.IsNullOrWhiteSpace(df.Name) || df.Name.StartsWith("$")) continue;
					if (df.Size <= 0 && !df.Resident) continue; // nothing to recover
					if (deleted.Count < MaxRecoverEntries) deleted.Add((df, parent));
				}
				globalIndex += recsInBuf;
				c += take; doneClusters += take;
				if (totalClusters > 0) progress((int)(doneClusters * 100 / totalClusters));
			}
		}

		// Resolve full paths from the parent chain (root = index 5).
		foreach (var (f, parent) in deleted)
		{
			f.Path = ResolvePath(parent, dirNames) + f.Name;
			result.Files.Add(f);
		}
		AnnotateNtfsHealth(vr, mftByteOffset, recSize, bytesPerSector, clusterSize, result.Files);
		result.Files = result.Files
			.GroupBy(f => f.Path + "|" + f.Size).Select(g => g.First())
			.OrderByDescending(f => f.Recoverable).ThenByDescending(f => f.ModifiedUtc ?? DateTime.MinValue).ToList();
		progress(100);
		return result;
	}

	private static string ResolvePath(long parent, Dictionary<long, (string name, long parent)> dirs)
	{
		var parts = new List<string>();
		long cur = parent; int guard = 0;
		while (cur != 5 && guard++ < 64 && dirs.TryGetValue(cur, out var d))
		{
			parts.Add(d.name);
			cur = d.parent;
		}
		parts.Reverse();
		return parts.Count > 0 ? string.Join("\\", parts) + "\\" : "";
	}

	private static (string name, long parent) ReadNameAndParent(byte[] buf, int baseOff, int recSize)
	{
		int attrOff = baseOff + BitConverter.ToUInt16(buf, baseOff + 0x14);
		int end = baseOff + recSize;
		string best = ""; int bestRank = -1; long parent = 5; int guard = 0;
		while (attrOff + 8 <= end && guard++ < 64)
		{
			uint type = BitConverter.ToUInt32(buf, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(buf, attrOff + 0x04);
			if (attrLen <= 0 || attrOff + attrLen > end) break;
			if (type == 0x30)
			{
				int c = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
				if (c + 0x42 <= buf.Length)
				{
					int nameLen = buf[c + 0x40]; int ns = buf[c + 0x41]; int nb = nameLen * 2;
					if (c + 0x42 + nb <= buf.Length)
					{
						string nm = Encoding.Unicode.GetString(buf, c + 0x42, nb);
						int rank = ns == 1 || ns == 3 ? 3 : ns == 0 ? 2 : 1;
						if (rank > bestRank) { bestRank = rank; best = nm; parent = BitConverter.ToInt64(buf, c) & 0xFFFFFFFFFFFF; }
					}
				}
			}
			attrOff += attrLen;
		}
		return (best, parent);
	}

	private DeletedFile? ParseDeletedRecord(byte[] buf, int baseOff, int recSize, out long parent)
	{
		parent = 5;
		var df = new DeletedFile();
		string bestName = ""; int bestNs = -1; bool haveData = false;
		int attrOff = baseOff + BitConverter.ToUInt16(buf, baseOff + 0x14);
		int end = baseOff + recSize;
		int guard = 0;
		while (attrOff + 8 <= end && guard++ < 96)
		{
			uint type = BitConverter.ToUInt32(buf, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(buf, attrOff + 0x04);
			if (attrLen <= 0 || attrOff + attrLen > end) break;

			if (type == 0x10) // $STANDARD_INFORMATION → timestamps
			{
				int c = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
				if (c + 0x20 <= buf.Length)
				{
					var modified = FileTimeToDate(BitConverter.ToInt64(buf, c + 0x08));
					if (modified.HasValue) df.ModifiedUtc = modified;
				}
			}
			else if (type == 0x30) // $FILE_NAME
			{
				int c = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
				if (c + 0x42 <= buf.Length)
				{
					int nameLen = buf[c + 0x40]; int ns = buf[c + 0x41]; int nb = nameLen * 2;
					if (c + 0x42 + nb <= buf.Length)
					{
						string nm = Encoding.Unicode.GetString(buf, c + 0x42, nb);
						int rank = ns == 1 || ns == 3 ? 3 : ns == 0 ? 2 : 1;
						if (rank > bestNs) { bestNs = rank; bestName = nm; parent = BitConverter.ToInt64(buf, c) & 0xFFFFFFFFFFFF; }
					}
				}
			}
			else if (type == 0x80 && buf[attrOff + 0x09] == 0) // unnamed $DATA
			{
				ushort af = BitConverter.ToUInt16(buf, attrOff + 0x0C);
				bool compEnc = (af & 0x4001) != 0;
				bool nonRes = buf[attrOff + 0x08] != 0;
				if (!nonRes)
				{
					int len = BitConverter.ToInt32(buf, attrOff + 0x10);
					int c = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
					if (len >= 0 && c + len <= buf.Length)
					{
						df.Resident = true; df.ResidentData = new byte[len];
						Array.Copy(buf, c, df.ResidentData, 0, len);
						df.Size = len; df.Recoverable = !compEnc;
						df.Status = compEnc ? L("RfStCompEnc") : L("RfStResident");
						haveData = true;
					}
				}
				else
				{
					long real = BitConverter.ToInt64(buf, attrOff + 0x30);
					int runOff = BitConverter.ToUInt16(buf, attrOff + 0x20);
					df.Runs = DecodeRuns(buf, attrOff + runOff, attrOff + attrLen);
					df.Size = real;
					bool sparse = df.Runs.Any(r => r.Lcn < 0);
					df.Recoverable = !compEnc && df.Runs.Count > 0;
					df.Status = compEnc ? L("RfStCompEnc") : sparse ? L("RfStSparse") : df.Runs.Count > 1 ? L("RfStFragmented") : L("RfStOk");
					haveData = true;
				}
			}
			attrOff += attrLen;
		}
		if (!haveData) { df.Recoverable = false; df.Status = L("RfStNoData"); }
		df.Name = bestName;
		df.SizeText = FormatBytes(df.Size);
		if (df.ModifiedUtc.HasValue) df.ModifiedText = df.ModifiedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
		return df;
	}

	// ---- Honest recoverability scoring ----
	// Conservative on purpose: free clusters can already have been partially rewritten and re-freed, so we never
	// claim 100% for an undelete. overwrittenFrac<0 means "unknown" (no bitmap) -> a heuristic from how it was found.
	private static void ApplyHealth(DeletedFile f, double overwrittenFrac, bool fragmented)
	{
		if (f.Carved) { f.StatusKind = "warn"; f.RecoverPercent = 70; return; }     // carving can't prove the bytes aren't fragmented
		if (!f.Recoverable) { f.StatusKind = "bad"; f.RecoverPercent = 0; return; }
		if (overwrittenFrac < 0) { f.StatusKind = "good"; f.RecoverPercent = fragmented ? 80 : 90; return; }
		if (overwrittenFrac <= 0.001) { f.StatusKind = "good"; f.RecoverPercent = fragmented ? 85 : 95; }
		else if (overwrittenFrac < 0.5) { f.StatusKind = "warn"; f.RecoverPercent = (int)Math.Round((1 - overwrittenFrac) * 90); }
		else { f.StatusKind = "bad"; f.RecoverPercent = (int)Math.Round((1 - overwrittenFrac) * 60); }
	}

	// NTFS: read the $Bitmap metafile (MFT entry 6) and check each deleted file's clusters — any now-allocated
	// cluster means the data was partially reused, so the recoverability score drops honestly.
	private void AnnotateNtfsHealth(VolumeReader vr, long mftByteOffset, int recSize, int bytesPerSector, int clusterSize, List<DeletedFile> files)
	{
		byte[]? bm = null;
		try
		{
			var runs = ReadMftRuns(vr, mftByteOffset + 6L * recSize, recSize, bytesPerSector); // entry 6 = $Bitmap
			if (runs.Count > 0)
			{
				using var ms = new MemoryStream();
				foreach (var (lcn, count) in runs)
				{
					if (lcn < 0) continue;
					long bytes = count * (long)clusterSize, off = lcn * (long)clusterSize, done = 0;
					while (done < bytes && ms.Length < (512L << 20)) { int chunk = (int)Math.Min(4 << 20, bytes - done); ms.Write(vr.Read(off + done, chunk), 0, chunk); done += chunk; }
				}
				bm = ms.ToArray(); // raw allocation bitmap, tested manually — BitArray's ctor overflows Int32 past ~256MB
			}
		}
		catch { bm = null; }
		bool IsUsed(long c) => bm != null && c >= 0 && (c >> 3) < bm.LongLength && (bm[(int)(c >> 3)] & (1 << (int)(c & 7))) != 0;
		foreach (var f in files)
		{
			if (f.Carved) { ApplyHealth(f, -1, false); continue; }
			if (f.Resident) { f.StatusKind = f.Recoverable ? "good" : "bad"; f.RecoverPercent = f.Recoverable ? 92 : 0; continue; }
			bool fragmented = f.Runs.Count(r => r.Lcn >= 0) > 1;
			if (bm == null || !f.Recoverable || f.Runs.Count == 0) { ApplyHealth(f, -1, fragmented); continue; }
			long totalC = 0, usedC = 0;
			foreach (var (lcn, count) in f.Runs)
			{
				if (lcn < 0) continue;
				for (long c = lcn; c < lcn + count; c++) { totalC++; if (IsUsed(c)) usedC++; }
			}
			ApplyHealth(f, totalC > 0 ? (double)usedC / totalC : -1, fragmented);
		}
	}

	// exFAT / FAT: bitmap reuse for a deleted file is unreliable (the chain is wiped on delete), so score by how
	// the file was found — existing-on-drive = 100, contiguous deleted = good, chained = partial, wiped = bad.
	private static void AnnotateGenericHealth(List<DeletedFile> files)
	{
		foreach (var f in files)
		{
			if (!f.Deleted) { f.StatusKind = "good"; f.RecoverPercent = 100; continue; }
			ApplyHealth(f, -1, f.ExFat && !f.Contiguous);
		}
	}

	// ---- exFAT scan ----
	private NtfsScanResult ScanExFat(char letter, VolumeReader vr, byte[] boot, CancellationToken ct, Action<int> progress)
	{
		int bytesPerSectorShift = boot[0x6C];
		int sectorsPerClusterShift = boot[0x6D];
		int bytesPerSector = 1 << bytesPerSectorShift;
		int clusterSize = bytesPerSector << sectorsPerClusterShift;
		long fatOffsetSec = BitConverter.ToUInt32(boot, 0x50);
		long clusterHeapOffsetSec = BitConverter.ToUInt32(boot, 0x58);
		long clusterCount = BitConverter.ToUInt32(boot, 0x5C);
		long rootDirCluster = BitConverter.ToUInt32(boot, 0x60);

		long fatOffset = fatOffsetSec * bytesPerSector;
		long dataOffset = clusterHeapOffsetSec * bytesPerSector; // byte offset of cluster #2
		long ClusterToByte(long cl) => dataOffset + (cl - 2) * (long)clusterSize;

		var result = new NtfsScanResult { Letter = letter, IsExFat = true, ClusterSize = clusterSize, BytesPerSector = bytesPerSector, DataAreaOffset = dataOffset, FatOffset = fatOffset };

		// Walk directories breadth-first starting at root, collecting deleted file entries.
		var toVisit = new Queue<(long cluster, string path)>();
		var visited = new HashSet<long>();
		toVisit.Enqueue((rootDirCluster, ""));
		int processed = 0;

		while (toVisit.Count > 0)
		{
			if (ct.IsCancellationRequested) break;
			var (startCluster, path) = toVisit.Dequeue();
			if (startCluster < 2 || !visited.Add(startCluster)) continue;
			// Read the directory's cluster chain (follow FAT).
			byte[] dir = ReadExFatChain(vr, startCluster, fatOffset, ClusterToByte, clusterSize, 64L * 1024 * 1024);
			processed++;
			progress(Math.Min(95, processed));

			int p = 0;
			while (p + 32 <= dir.Length)
			{
				byte entryType = dir[p];
				if (entryType == 0x00) break; // end of directory
				bool inUse = (entryType & 0x80) != 0;
				byte baseType = (byte)(entryType & 0x7F);

				if (baseType == 0x05) // File directory entry (0x85 in use / 0x05 deleted)
				{
					int secondaryCount = dir[p + 1];
					if (p + 32 * (secondaryCount + 1) > dir.Length) { p += 32; continue; }
					// Stream extension = next entry
					int sp = p + 32;
					byte streamType = (byte)(dir[sp] & 0x7F);
					if (streamType == 0x40)
					{
						byte secFlags = dir[sp + 1];
						bool noFatChain = (secFlags & 0x02) != 0;
						long firstCluster = BitConverter.ToUInt32(dir, sp + 0x14);
						long dataLength = BitConverter.ToInt64(dir, sp + 0x18);
						int nameLen = dir[sp + 0x03];
						// File name entries follow
						var sb = new StringBuilder();
						for (int n = 0; n < secondaryCount - 1 && sb.Length < nameLen; n++)
						{
							int np = p + 32 * (2 + n);
							if (np + 32 > dir.Length) break;
							if ((dir[np] & 0x7F) != 0x41) break;
							sb.Append(Encoding.Unicode.GetString(dir, np + 2, 30));
						}
						string name = sb.ToString();
						if (name.Length > nameLen) name = name.Substring(0, nameLen);
						bool isDir = (BitConverter.ToUInt16(dir, p + 0x04) & 0x10) != 0; // file attributes

						bool clusterOk = firstCluster >= 2 && firstCluster < 2 + clusterCount;
						if (isDir)
						{
							if (clusterOk && !visited.Contains(firstCluster)) toVisit.Enqueue((firstCluster, path + name + "\\"));
						}
						else if (!string.IsNullOrWhiteSpace(name) && result.Files.Count < MaxRecoverEntries) // file: list both deleted ones and existing ones (on drive)
						{
							bool canRec = clusterOk && dataLength > 0;
							result.Files.Add(new DeletedFile
							{
								ExFat = true, Deleted = !inUse, Name = name, Path = path + name,
								Size = dataLength, SizeText = FormatBytes(dataLength),
								FirstCluster = firstCluster, Contiguous = noFatChain,
								Recoverable = canRec,
								Status = inUse ? L("RfStOnDrive") : (canRec ? (noFatChain ? L("RfStOk") : L("RfStChained")) : (dataLength == 0 ? L("RfStEmpty") : L("RfStWiped")))
							});
						}
					}
					p += 32 * (secondaryCount + 1);
					continue;
				}
				p += 32;
			}
		}

		AnnotateGenericHealth(result.Files);
		result.Files = result.Files
			.GroupBy(f => f.Path + "|" + f.Size + "|" + f.FirstCluster).Select(g => g.First())
			.OrderByDescending(f => f.Deleted).ThenByDescending(f => f.Recoverable).ThenByDescending(f => f.Size).ToList();
		progress(100);
		return result;
	}

	// Reads an exFAT cluster chain into memory (follows the FAT unless it runs away), capped at maxBytes.
	private byte[] ReadExFatChain(VolumeReader vr, long firstCluster, long fatOffset, Func<long, long> clusterToByte, int clusterSize, long maxBytes)
	{
		var ms = new MemoryStream();
		long cl = firstCluster; int guard = 0;
		var seen = new HashSet<long>();
		while (cl >= 2 && ms.Length < maxBytes && guard++ < 1_000_000 && seen.Add(cl))
		{
			if (stopRequested) break;
			byte[] data = vr.Read(clusterToByte(cl), clusterSize);
			ms.Write(data, 0, data.Length);
			byte[] fatEntry = vr.Read(fatOffset + cl * 4, 4);
			long next = BitConverter.ToUInt32(fatEntry, 0);
			if (next >= 0xFFFFFFF8 || next < 2) break; // end of chain
			cl = next;
		}
		return ms.ToArray();
	}

	// ---- FAT32 scan ----
	// On FAT32 the cluster chain is wiped from the FAT when a file is deleted, so deleted files are recovered by
	// reading clusters CONTIGUOUSLY from the first cluster stored in the directory entry (the common case for
	// recently written files). Directory trees are walked via the FAT (valid for still-existing directories).
	private NtfsScanResult ScanFat32(char letter, VolumeReader vr, byte[] boot, CancellationToken ct, Action<int> progress)
	{
		int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
		int sectorsPerCluster = boot[0x0D];
		int reservedSectors = BitConverter.ToUInt16(boot, 0x0E);
		int numFats = boot[0x10];
		long fatSize32 = BitConverter.ToUInt32(boot, 0x24);
		long rootCluster = BitConverter.ToUInt32(boot, 0x2C);
		if (bytesPerSector == 0 || sectorsPerCluster == 0 || fatSize32 == 0) throw new IOException("Unreadable FAT32 boot sector.");
		int clusterSize = bytesPerSector * sectorsPerCluster;
		long fatOffset = (long)reservedSectors * bytesPerSector;
		long dataOffset = (reservedSectors + (long)numFats * fatSize32) * bytesPerSector; // byte offset of cluster #2

		long ClusterToByte(long cl) => dataOffset + (cl - 2) * (long)clusterSize;
		var result = new NtfsScanResult { Letter = letter, IsExFat = false, ClusterSize = clusterSize, BytesPerSector = bytesPerSector, DataAreaOffset = dataOffset, FatOffset = fatOffset };

		var toVisit = new Queue<(long cluster, string path)>();
		var visited = new HashSet<long>();
		toVisit.Enqueue((rootCluster, ""));
		int processed = 0;

		while (toVisit.Count > 0)
		{
			if (ct.IsCancellationRequested) break;
			var (startCluster, path) = toVisit.Dequeue();
			if (startCluster < 2 || !visited.Add(startCluster)) continue;
			byte[] dir = ReadFat32Chain(vr, startCluster, fatOffset, ClusterToByte, clusterSize, 32L * 1024 * 1024);
			processed++; progress(Math.Min(95, processed));

			var lfn = new List<string>();
			for (int p = 0; p + 32 <= dir.Length; p += 32)
			{
				byte first = dir[p];
				if (first == 0x00) break; // end of directory
				byte attr = dir[p + 0x0B];
				if (attr == 0x0F) // long-file-name entry
				{
					lfn.Add(ReadLfnChars(dir, p));
					continue;
				}
				if (attr == 0x08) { lfn.Clear(); continue; } // volume label
				bool deleted = first == 0xE5;
				bool isDir = (attr & 0x10) != 0;
				long firstCluster = ((long)BitConverter.ToUInt16(dir, p + 0x14) << 16) | BitConverter.ToUInt16(dir, p + 0x1A);
				long size = BitConverter.ToUInt32(dir, p + 0x1C);

				// Assemble the name: prefer the long name (reverse order), else the 8.3 short name.
				string name = "";
				if (lfn.Count > 0) { lfn.Reverse(); name = string.Concat(lfn).TrimEnd('￿', '\0', ' '); }
				lfn.Clear();
				if (string.IsNullOrEmpty(name)) name = ShortName(dir, p, deleted);

				if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") continue;

				if (isDir)
				{
					if (!deleted && firstCluster >= 2 && !visited.Contains(firstCluster))
						toVisit.Enqueue((firstCluster, path + name + "\\"));
					continue;
				}
				if (deleted)
				{
					// List EVERY deleted file, even when its data location was wiped by the OS on delete — that
					// way nothing silently disappears; unrecoverable ones are shown with a clear reason.
					bool canRec = firstCluster >= 2 && size > 0;
					result.Files.Add(new DeletedFile
					{
						ExFat = true, Contiguous = true, // recover by reading contiguous clusters via shared geometry
						Name = name, Path = path + name,
						Size = size, SizeText = FormatBytes(size),
						FirstCluster = firstCluster,
						ModifiedUtc = FatDateTime(dir, p),
						ModifiedText = FatDateTime(dir, p)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
						Recoverable = canRec,
						Status = canRec ? L("RfStContiguous") : (size == 0 ? L("RfStEmpty") : L("RfStWiped"))
					});
				}
			}
		}

		AnnotateGenericHealth(result.Files);
		result.Files = result.Files
			.GroupBy(f => f.Path + "|" + f.Size + "|" + f.FirstCluster).Select(g => g.First())
			.OrderByDescending(f => f.Deleted).ThenByDescending(f => f.Recoverable).ThenByDescending(f => f.ModifiedUtc ?? DateTime.MinValue).ToList();
		progress(100);
		return result;
	}

	private static string ReadLfnChars(byte[] dir, int p)
	{
		var sb = new StringBuilder();
		void grab(int off, int count) { for (int i = 0; i < count; i++) { int o = p + off + i * 2; if (o + 1 < dir.Length) { char c = (char)BitConverter.ToUInt16(dir, o); if (c == 0xFFFF || c == 0) return; sb.Append(c); } } }
		grab(0x01, 5); grab(0x0E, 6); grab(0x1C, 2);
		return sb.ToString();
	}

	private static string ShortName(byte[] dir, int p, bool deleted)
	{
		string baseName = Encoding.ASCII.GetString(dir, p, 8).TrimEnd(' ');
		string ext = Encoding.ASCII.GetString(dir, p + 8, 3).TrimEnd(' ');
		if (deleted && baseName.Length > 0) baseName = "_" + baseName.Substring(1); // first char was overwritten by 0xE5
		return ext.Length > 0 ? baseName + "." + ext : baseName;
	}

	private static DateTime? FatDateTime(byte[] dir, int p)
	{
		try
		{
			int t = BitConverter.ToUInt16(dir, p + 0x16);
			int d = BitConverter.ToUInt16(dir, p + 0x18);
			if (d == 0) return null;
			int year = ((d >> 9) & 0x7F) + 1980, month = (d >> 5) & 0x0F, day = d & 0x1F;
			int hour = (t >> 11) & 0x1F, min = (t >> 5) & 0x3F, sec = (t & 0x1F) * 2;
			if (month < 1 || month > 12 || day < 1 || day > 31) return null;
			return new DateTime(year, month, day, Math.Min(23, hour), Math.Min(59, min), Math.Min(59, sec), DateTimeKind.Local).ToUniversalTime();
		}
		catch { return null; }
	}

	private byte[] ReadFat32Chain(VolumeReader vr, long firstCluster, long fatOffset, Func<long, long> clusterToByte, int clusterSize, long maxBytes)
	{
		var ms = new MemoryStream();
		long cl = firstCluster; int guard = 0;
		var seen = new HashSet<long>();
		while (cl >= 2 && ms.Length < maxBytes && guard++ < 1_000_000 && seen.Add(cl))
		{
			if (stopRequested) break;
			ms.Write(vr.Read(clusterToByte(cl), clusterSize), 0, clusterSize);
			long next = BitConverter.ToUInt32(vr.Read(fatOffset + cl * 4, 4), 0) & 0x0FFFFFFF;
			if (next >= 0x0FFFFFF8 || next < 2) break;
			cl = next;
		}
		return ms.ToArray();
	}

	// ---- Recycle Bin ($I / $R) ----
	// The fastest, safest, highest-fidelity recovery: files in the Recycle Bin still exist on disk as $R files,
	// with the original name/path/date stored in the matching $I file. Recovery = a plain copy. (Win10 $I = v2.)
	private NtfsScanResult ScanRecycleBin(char letter)
	{
		var result = new NtfsScanResult { Letter = letter };
		string root = char.ToUpperInvariant(letter) + ":\\$Recycle.Bin";
		string[] sidDirs;
		try { sidDirs = Directory.GetDirectories(root); } catch { return result; }
		foreach (var sidDir in sidDirs)
		{
			string[] iFiles;
			try { iFiles = Directory.GetFiles(sidDir, "$I*"); } catch { continue; }
			foreach (var iPath in iFiles)
			{
				try
				{
					byte[] b = File.ReadAllBytes(iPath);
					if (b.Length < 24) continue;
					long ver = BitConverter.ToInt64(b, 0);
					long size = BitConverter.ToInt64(b, 8);
					var dt = FileTimeToDate(BitConverter.ToInt64(b, 16));
					string orig;
					if (ver == 2 && b.Length >= 28)
					{
						int nch = BitConverter.ToInt32(b, 24);
						int nb = Math.Max(0, (nch - 1)) * 2;
						if (28 + nb > b.Length) nb = Math.Max(0, b.Length - 28);
						orig = Encoding.Unicode.GetString(b, 28, nb);
					}
					else
					{
						int avail = Math.Min(520, b.Length - 24);
						orig = Encoding.Unicode.GetString(b, 24, Math.Max(0, avail));
					}
					orig = orig.Replace("\0", "");
					// $IXXXXXX.ext -> $RXXXXXX.ext (the actual data)
					string rPath = Path.Combine(Path.GetDirectoryName(iPath)!, "$R" + Path.GetFileName(iPath).Substring(2));
					bool isDir = Directory.Exists(rPath);
					if (!File.Exists(rPath) && !isDir) continue;
					long realSize = size;
					try { if (!isDir) { long fl = new FileInfo(rPath).Length; if (fl > 0) realSize = fl; } } catch { }
					string name = string.IsNullOrEmpty(orig) ? Path.GetFileName(rPath) : Path.GetFileName(orig.TrimEnd('\\'));
					if (string.IsNullOrEmpty(name)) name = Path.GetFileName(rPath);
					if (result.Files.Count >= MaxRecoverEntries) break;
					result.Files.Add(new DeletedFile
					{
						Deleted = true, Name = name, Path = string.IsNullOrEmpty(orig) ? name : orig,
						Size = realSize, SizeText = FormatBytes(realSize),
						ModifiedUtc = dt, ModifiedText = dt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
						Recoverable = true, SourcePath = rPath, StatusKind = "good", RecoverPercent = 100, Status = L("RfStRecycle")
					});
				}
				catch { }
			}
		}
		result.Files = result.Files.OrderByDescending(f => f.ModifiedUtc ?? DateTime.MinValue).ToList();
		return result;
	}

	// ---- Deep scan (signature carving) ----
	// Reads the whole volume and recovers files by their content signatures (header + footer), even when the
	// file system has no record of them. Best for photos and documents (clear headers/footers). Carved files
	// have no original name and large fragmented files may come out incomplete — that's inherent to carving.
	private sealed class Sig
	{
		public string Name = ""; public string Ext = "";
		public byte[] Header = Array.Empty<byte>();
		public byte[] Footer = Array.Empty<byte>();
		public long MaxLen; public int FooterTail;
		public int SizeAt = -1; public int SizeBytes; public long SizeAdd; // size-from-header carving
		public bool Riff;                                                  // RIFF container (WAV/AVI/WEBP)
		public bool Mp4;                                                   // ISO-BMFF container (MP4/MOV/M4V/HEIC) — length by box-walk
		public bool Sqlite;                                               // SQLite database — length from page-size x page-count
		public int HeadBack;                                              // bytes the real file starts before the matched header
	}

	private static readonly Sig[] CarveSigs =
	{
		new Sig { Name = "JPEG image", Ext = ".jpg", Header = new byte[]{0xFF,0xD8,0xFF}, Footer = new byte[]{0xFF,0xD9}, MaxLen = 64L<<20 },
		new Sig { Name = "PNG image", Ext = ".png", Header = new byte[]{0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A}, Footer = new byte[]{0x49,0x45,0x4E,0x44,0xAE,0x42,0x60,0x82}, MaxLen = 64L<<20 },
		new Sig { Name = "GIF image", Ext = ".gif", Header = new byte[]{0x47,0x49,0x46,0x38}, Footer = new byte[]{0x00,0x3B}, MaxLen = 32L<<20 },
		new Sig { Name = "BMP image", Ext = ".bmp", Header = new byte[]{0x42,0x4D}, SizeAt = 2, SizeBytes = 4, MaxLen = 64L<<20 },
		new Sig { Name = "PDF document", Ext = ".pdf", Header = new byte[]{0x25,0x50,0x44,0x46}, Footer = new byte[]{0x25,0x25,0x45,0x4F,0x46}, MaxLen = 256L<<20, FooterTail = 2 },
		new Sig { Name = "ZIP / Office", Ext = ".zip", Header = new byte[]{0x50,0x4B,0x03,0x04}, Footer = new byte[]{0x50,0x4B,0x05,0x06}, MaxLen = 512L<<20, FooterTail = 22 },
		new Sig { Name = "RIFF (WAV/AVI/WEBP)", Ext = ".riff", Header = new byte[]{0x52,0x49,0x46,0x46}, Riff = true, MaxLen = 1024L<<20 },
		new Sig { Name = "ISO-BMFF (MP4/MOV/HEIC)", Ext = ".mp4", Header = new byte[]{0x66,0x74,0x79,0x70}, Mp4 = true, HeadBack = 4, MaxLen = 4096L<<20 },
		new Sig { Name = "SQLite database", Ext = ".sqlite", Header = new byte[]{0x53,0x51,0x4C,0x69,0x74,0x65,0x20,0x66,0x6F,0x72,0x6D,0x61,0x74,0x20,0x33,0x00}, Sqlite = true, MaxLen = 2048L<<20 },
	};

	private NtfsScanResult DeepScan(char letter, CancellationToken ct, Action<int> progress, long startOffset = 0, int startCount = 0)
	{
		var result = new NtfsScanResult { Letter = letter };
		var vr = OpenVolume(letter);
		long total = 0, pos = startOffset;
		try
		{
			try { total = new DriveInfo(letter + ":").TotalSize; } catch { total = 0; }
			if (total <= 0) total = 256L << 30;
			const int block = 32 << 20;
			int maxHeader = CarveSigs.Max(s => s.Header.Length);
			int count = startCount;
			long lastPush = 0;
			pos -= pos % block; // resume on a block boundary
			while (pos < total && !stopRequested)
			{
				while (_recoverPaused && !stopRequested) System.Threading.Thread.Sleep(150); // pause/resume
				if (stopRequested) break;
				int want = (int)Math.Min(block + maxHeader, total - pos);
				if (want <= maxHeader) break;
				byte[] chunk = vr.Read(pos, want);
				int limit = want - maxHeader;
				for (int i = 0; i < limit; i++)
				{
					if (((pos + i) & 511) != 0) continue; // deleted file data starts on a sector boundary -> far fewer false hits + faster
					Sig? hit = null;
					foreach (var s in CarveSigs) if (StartsWith(chunk, i, s.Header)) { hit = s; break; }
					if (hit == null) continue;
					long fileStart = pos + i;
					long len = 0;
					string ext = hit.Ext;
					if (hit.HeadBack > 0 && fileStart >= hit.HeadBack) fileStart -= hit.HeadBack;
					if (hit.Mp4) { len = Mp4Length(vr, fileStart, hit.MaxLen); if (len > 0) ext = Mp4BrandExt(chunk, i + 4); }
					else if (hit.Sqlite) { len = SqliteLen(chunk, i); }
					else if (hit.Riff || hit.SizeAt >= 0) { len = HeaderDerivedLen(hit, chunk, i, ref ext); }
					else
					{
						long end = FindFooter(vr, fileStart + hit.Header.Length, hit.Footer, hit.MaxLen, hit.FooterTail);
						if (end > fileStart) len = end - fileStart;
					}
					if (len >= hit.Header.Length && len <= hit.MaxLen)
					{
						count++;
						lock (result.Files) result.Files.Add(new DeletedFile
						{
							Carved = true, ByteOffset = fileStart, Size = len, SizeText = FormatBytes(len),
							Name = $"deepscan_{count:D5}{ext}", Path = "(deep scan)",
							Recoverable = true, Status = hit.Name, StatusKind = "warn", RecoverPercent = 70
						});
						long advance = (fileStart + len) - pos;
						if (advance > i + 1) { i = (int)Math.Min(limit - 1, advance - 1); }
					}
				}
				pos += block;
				// Live feedback: the percent rounds to 0 for a long time on big drives, so also show bytes + count,
				// and stream the carved rows into the grid (snapshot under lock) so results appear as they're found.
				progress(total > 0 ? (int)Math.Min(99, pos * 100 / total) : 0);
				if (Environment.TickCount64 - lastPush > 500)
				{
					lastPush = Environment.TickCount64;
					long scanned = Math.Min(pos, total); int found = count;
					Volatile.Write(ref _progressDoneBytes, scanned); // drives the byte-based bar + real ETA + MB/s via the progress timer
					// Only the live COUNT is pushed to the grid (cheap). Re-binding the whole grid every tick caused
					// an O(n^2) render storm that made big scans crawl near the end; results bind once when done.
					Dispatcher.Invoke(() => { if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepLive"), FormatBytes(scanned), found); });
				}
			}
		}
		finally { vr.Dispose(); }
		result.ResumeOffset = pos; result.DeepPartial = pos < total;
		progress(100);
		return result;
	}

	// Deep scan (carving) over a disk-image file instead of a live volume.
	private NtfsScanResult DeepScanImage(string imagePath, CancellationToken ct, Action<int> progress, long startOffset = 0, int startCount = 0)
	{
		var result = new NtfsScanResult { ImagePath = imagePath };
		var vr = new VolumeReader(imagePath);
		long total = 0, pos = startOffset;
		try
		{
			try { total = new FileInfo(imagePath).Length; } catch { }
			if (total <= 0) total = 256L << 30;
			const int block = 32 << 20;
			int maxHeader = CarveSigs.Max(s => s.Header.Length);
			int count = startCount; long lastPush = 0;
			pos -= pos % block;
			while (pos < total && !stopRequested)
			{
				while (_recoverPaused && !stopRequested) System.Threading.Thread.Sleep(150);
				if (stopRequested) break;
				int want = (int)Math.Min(block + maxHeader, total - pos);
				if (want <= maxHeader) break;
				byte[] chunk = vr.Read(pos, want);
				int limit = want - maxHeader;
				for (int i = 0; i < limit; i++)
				{
					if (((pos + i) & 511) != 0) continue; // deleted file data starts on a sector boundary -> far fewer false hits + faster
					Sig? hit = null;
					foreach (var s in CarveSigs) if (StartsWith(chunk, i, s.Header)) { hit = s; break; }
					if (hit == null) continue;
					long fileStart = pos + i;
					long len = 0;
					string ext = hit.Ext;
					if (hit.HeadBack > 0 && fileStart >= hit.HeadBack) fileStart -= hit.HeadBack;
					if (hit.Mp4) { len = Mp4Length(vr, fileStart, hit.MaxLen); if (len > 0) ext = Mp4BrandExt(chunk, i + 4); }
					else if (hit.Sqlite) { len = SqliteLen(chunk, i); }
					else if (hit.Riff || hit.SizeAt >= 0) { len = HeaderDerivedLen(hit, chunk, i, ref ext); }
					else
					{
						long end = FindFooter(vr, fileStart + hit.Header.Length, hit.Footer, hit.MaxLen, hit.FooterTail);
						if (end > fileStart) len = end - fileStart;
					}
					if (len >= hit.Header.Length && len <= hit.MaxLen)
					{
						count++;
						lock (result.Files) result.Files.Add(new DeletedFile
						{
							Carved = true, ByteOffset = fileStart, Size = len, SizeText = FormatBytes(len),
							Name = $"deepscan_{count:D5}{ext}", Path = "(deep scan)",
							Recoverable = true, Status = hit.Name, StatusKind = "warn", RecoverPercent = 70
						});
						long advance = (fileStart + len) - pos;
						if (advance > i + 1) { i = (int)Math.Min(limit - 1, advance - 1); }
					}
				}
				pos += block;
				progress(total > 0 ? (int)Math.Min(99, pos * 100 / total) : 0);
				if (Environment.TickCount64 - lastPush > 500)
				{
					lastPush = Environment.TickCount64;
					long scanned = Math.Min(pos, total); int found = count;
					Volatile.Write(ref _progressDoneBytes, scanned); // drives the byte-based bar + real ETA + MB/s via the progress timer
					// Only the live COUNT is pushed to the grid (cheap). Re-binding the whole grid every tick caused
					// an O(n^2) render storm that made big scans crawl near the end; results bind once when done.
					Dispatcher.Invoke(() => { if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepLive"), FormatBytes(scanned), found); });
				}
			}
		}
		finally { vr.Dispose(); }
		result.ResumeOffset = pos; result.DeepPartial = pos < total;
		progress(100);
		return result;
	}

	// Creates a raw sector-by-sector image (.img) of a volume so recovery can be run on the copy, protecting a
	// failing drive (read once). Returns when done; honours Stop.
	private void CreateDiskImage(char letter, string destPath, long totalSize, Action<int> progress)
	{
		using var vr = OpenVolume(letter);
		using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
		const int block = 8 << 20;
		long pos = 0;
		while (pos < totalSize && !stopRequested)
		{
			int want = (int)Math.Min(block, totalSize - pos);
			byte[] data = vr.Read(pos, want);
			outFs.Write(data, 0, want);
			pos += want;
			if (totalSize > 0) progress((int)Math.Min(100, pos * 100 / totalSize));
		}
		outFs.Flush();
	}

	// Computes a carved file's length from header fields (RIFF size, BMP size), validating the structure so a
	// random "BM"/"RIFF" byte match doesn't create a bogus result. Returns 0 to reject the match.
	private static long HeaderDerivedLen(Sig hit, byte[] chunk, int i, ref string ext)
	{
		if (hit.Riff)
		{
			if (i + 12 > chunk.Length) return 0;
			string sub = Encoding.ASCII.GetString(chunk, i + 8, 4);
			if (sub != "WEBP" && sub != "WAVE" && sub != "AVI ") return 0; // only known RIFF subtypes
			ext = sub == "WEBP" ? ".webp" : sub == "WAVE" ? ".wav" : ".avi";
			return (uint)BitConverter.ToInt32(chunk, i + 4) + 8L;
		}
		if (hit.SizeAt >= 0) // BMP: validate the DIB header size before trusting the file size
		{
			if (i + 18 > chunk.Length) return 0;
			int dib = BitConverter.ToInt32(chunk, i + 14);
			if (!(dib == 12 || dib == 40 || dib == 52 || dib == 56 || dib == 64 || dib == 108 || dib == 124)) return 0;
			long val = 0;
			for (int k = 0; k < hit.SizeBytes; k++) val |= (long)chunk[i + hit.SizeAt + k] << (8 * k);
			return val >= 54 ? val + hit.SizeAdd : 0;
		}
		return 0;
	}

	// Maps an ISO-BMFF major brand (the 4 bytes after 'ftyp') to the right extension, so HEIC phone photos and
	// MOV / M4A / 3GP come out named correctly instead of every container being ".mp4".
	private static string Mp4BrandExt(byte[] chunk, int brandPos)
	{
		if (brandPos + 4 > chunk.Length) return ".mp4";
		string b = Encoding.ASCII.GetString(chunk, brandPos, 4);
		string b3 = b.Length >= 3 ? b.Substring(0, 3) : b;
		if (b.StartsWith("hei") || b == "mif1" || b == "msf1" || b == "hevc" || b == "heim" || b == "heis" || b == "heix") return ".heic";
		if (b == "avif" || b == "avis") return ".avif";
		if (b == "qt  ") return ".mov";
		if (b == "M4A " || b == "M4B ") return ".m4a";
		if (b == "M4V ") return ".m4v";
		if (b3 == "3gp" || b3 == "3g2") return ".3gp";
		return ".mp4";
	}

	// SQLite file size = page-size (offset 16, big-endian; 1 means 65536) x page-count (offset 28, big-endian).
	private static long SqliteLen(byte[] chunk, int i)
	{
		if (i + 32 > chunk.Length) return 0;
		int ps = (chunk[i + 16] << 8) | chunk[i + 17];
		long pageSize = ps == 1 ? 65536 : ps;
		if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0) return 0; // must be a power of two >= 512
		long pageCount = ((long)chunk[i + 28] << 24) | ((long)chunk[i + 29] << 16) | ((long)chunk[i + 30] << 8) | chunk[i + 31];
		long len = pageSize * pageCount;
		return len >= 512 ? len : 0;
	}

	private static bool StartsWith(byte[] data, int at, byte[] sig)
	{
		if (at + sig.Length > data.Length) return false;
		for (int i = 0; i < sig.Length; i++) if (data[at + i] != sig[i]) return false;
		return true;
	}

	// Scans forward from `start` for the footer pattern; returns the absolute offset just past the footer
	// (plus FooterTail bytes), or -1/0 if not found within maxLen.
	private long FindFooter(VolumeReader vr, long start, byte[] footer, long maxLen, int footerTail)
	{
		const int step = 4 << 20;
		long pos = start;
		long limit = start + maxLen;
		int overlap = footer.Length - 1;
		byte[] prev = Array.Empty<byte>();
		while (pos < limit && !stopRequested)
		{
			int want = (int)Math.Min(step, limit - pos);
			byte[] b = vr.Read(pos, want);
			// build a small joined view across the boundary
			if (prev.Length > 0)
			{
				byte[] join = new byte[prev.Length + Math.Min(footer.Length, b.Length)];
				Array.Copy(prev, 0, join, 0, prev.Length);
				Array.Copy(b, 0, join, prev.Length, join.Length - prev.Length);
				int jf = IndexOf(join, footer, 0);
				if (jf >= 0) return (pos - prev.Length) + jf + footer.Length + footerTail;
			}
			int f = IndexOf(b, footer, 0);
			if (f >= 0) return pos + f + footer.Length + footerTail;
			prev = overlap > 0 && b.Length >= overlap ? b[^overlap..] : Array.Empty<byte>();
			pos += want;
			if (want < step) break;
		}
		return 0;
	}

	private static int IndexOf(byte[] hay, byte[] needle, int from)
	{
		int end = hay.Length - needle.Length;
		for (int i = from; i <= end; i++)
		{
			int j = 0;
			while (j < needle.Length && hay[i + j] == needle[j]) j++;
			if (j == needle.Length) return i;
		}
		return -1;
	}

	// Computes an MP4/MOV (ISO base-media) file length by walking its top-level boxes from `start`.
	// Each box is [4-byte big-endian size][4-byte ASCII type]; size==1 means a 64-bit size follows the type.
	// Returns 0 (reject) unless the chain starts with a valid 'ftyp' and has at least one more known box,
	// so a stray "ftyp" inside another file can't produce a bogus carve.
	private long Mp4Length(VolumeReader vr, long start, long maxLen)
	{
		long pos = start;
		long limit = start + maxLen;
		bool sawFtyp = false;
		int boxes = 0;
		while (pos + 8 <= limit && !stopRequested)
		{
			byte[] h = vr.Read(pos, 16);
			long size = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
			string type = Encoding.ASCII.GetString(h, 4, 4);
			if (!IsMp4BoxType(type)) break;
			if (boxes == 0 && type != "ftyp") break;            // a real file leads with ftyp
			if (type == "ftyp") sawFtyp = true;
			if (size == 1)                                       // 64-bit extended size
				size = ((long)h[8] << 56) | ((long)h[9] << 48) | ((long)h[10] << 40) | ((long)h[11] << 32)
				     | ((long)h[12] << 24) | ((long)h[13] << 16) | ((long)h[14] << 8) | h[15];
			if (size < 8) break;                                // size 0 (to-EOF) or malformed → can't bound it
			pos += size;
			boxes++;
			if (boxes > 200000) break;
		}
		long len = pos - start;
		if (!sawFtyp || boxes < 2 || len < 16) return 0;
		return len <= maxLen ? len : 0;
	}

	// True for the small set of top-level boxes legitimately found at the start of / between MP4/MOV files.
	private static bool IsMp4BoxType(string t)
	{
		switch (t)
		{
			case "ftyp": case "moov": case "mdat": case "free": case "skip": case "wide":
			case "pdin": case "moof": case "mfra": case "meta": case "uuid": case "styp":
			case "sidx": case "ssix": case "prft":
				return true;
			default:
				return false;
		}
	}

	// ---- FAT16 / FAT12 scan ----
	// Like FAT32 but the root directory is a fixed region (not a cluster chain) and the first-cluster field is
	// only the low 16 bits. Used by smaller cards/sticks (a ~1 GB card formatted "FAT" is FAT16).
	private NtfsScanResult ScanFat16(char letter, VolumeReader vr, byte[] boot, CancellationToken ct, Action<int> progress)
	{
		int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
		int sectorsPerCluster = boot[0x0D];
		int reservedSectors = BitConverter.ToUInt16(boot, 0x0E);
		int numFats = boot[0x10];
		int rootEntCount = BitConverter.ToUInt16(boot, 0x11);
		int sectorsPerFat = BitConverter.ToUInt16(boot, 0x16);
		if (bytesPerSector == 0 || sectorsPerCluster == 0 || sectorsPerFat == 0) throw new IOException("Unreadable FAT boot sector.");
		int clusterSize = bytesPerSector * sectorsPerCluster;
		long fatOffset = (long)reservedSectors * bytesPerSector;
		int rootDirSectors = ((rootEntCount * 32) + (bytesPerSector - 1)) / bytesPerSector;
		long rootDirOffset = ((long)reservedSectors + (long)numFats * sectorsPerFat) * bytesPerSector;
		long dataOffset = rootDirOffset + (long)rootDirSectors * bytesPerSector; // byte offset of cluster #2
		long ClusterToByte(long cl) => dataOffset + (cl - 2) * (long)clusterSize;

		var result = new NtfsScanResult { Letter = letter, ClusterSize = clusterSize, BytesPerSector = bytesPerSector, DataAreaOffset = dataOffset, FatOffset = fatOffset };

		// Parse the fixed-size root directory first, then walk sub-directories via the (16-bit) FAT.
		byte[] root = vr.Read(rootDirOffset, rootDirSectors * bytesPerSector);
		var subdirs = new Queue<(long cluster, string path)>();
		ParseFatDirBuffer(root, "", false, subdirs, result.Files);
		progress(50);

		var visited = new HashSet<long>();
		while (subdirs.Count > 0 && !ct.IsCancellationRequested)
		{
			var (cl, path) = subdirs.Dequeue();
			if (cl < 2 || !visited.Add(cl)) continue;
			byte[] dir = ReadFat16Chain(vr, cl, fatOffset, ClusterToByte, clusterSize, 16L * 1024 * 1024);
			ParseFatDirBuffer(dir, path, false, subdirs, result.Files);
		}

		AnnotateGenericHealth(result.Files);
		result.Files = result.Files
			.GroupBy(f => f.Path + "|" + f.Size + "|" + f.FirstCluster).Select(g => g.First())
			.OrderByDescending(f => f.Deleted).ThenByDescending(f => f.Recoverable).ThenByDescending(f => f.ModifiedUtc ?? DateTime.MinValue).ToList();
		progress(100);
		return result;
	}

	private byte[] ReadFat16Chain(VolumeReader vr, long firstCluster, long fatOffset, Func<long, long> clusterToByte, int clusterSize, long maxBytes)
	{
		var ms = new MemoryStream();
		long cl = firstCluster; int guard = 0; var seen = new HashSet<long>();
		while (cl >= 2 && cl < 0xFFF0 && ms.Length < maxBytes && guard++ < 200000 && seen.Add(cl))
		{
			if (stopRequested) break;
			ms.Write(vr.Read(clusterToByte(cl), clusterSize), 0, clusterSize);
			long next = BitConverter.ToUInt16(vr.Read(fatOffset + cl * 2, 2), 0);
			if (next >= 0xFFF8 || next < 2) break;
			cl = next;
		}
		return ms.ToArray();
	}

	// Shared FAT directory-entry parser (FAT16 root region and FAT16/FAT32 sub-directories). fat32 selects how
	// the starting cluster is read (FAT32 uses the high word too).
	private void ParseFatDirBuffer(byte[] dir, string path, bool fat32, Queue<(long cluster, string path)> subdirs, List<DeletedFile> files)
	{
		var lfn = new List<string>();
		for (int p = 0; p + 32 <= dir.Length; p += 32)
		{
			byte first = dir[p];
			if (first == 0x00) break; // end of directory
			byte attr = dir[p + 0x0B];
			if (attr == 0x0F) { lfn.Add(ReadLfnChars(dir, p)); continue; }
			if (attr == 0x08) { lfn.Clear(); continue; } // volume label
			bool deleted = first == 0xE5;
			bool isDir = (attr & 0x10) != 0;
			long lo = BitConverter.ToUInt16(dir, p + 0x1A);
			long firstCluster = fat32 ? (((long)BitConverter.ToUInt16(dir, p + 0x14) << 16) | lo) : lo;
			long size = BitConverter.ToUInt32(dir, p + 0x1C);

			string name = "";
			if (lfn.Count > 0) { lfn.Reverse(); name = string.Concat(lfn).TrimEnd('￿', '\0', ' '); }
			lfn.Clear();
			if (string.IsNullOrEmpty(name)) name = ShortName(dir, p, deleted);
			if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") continue;

			if (isDir)
			{
				if (!deleted && firstCluster >= 2) subdirs.Enqueue((firstCluster, path + name + "\\"));
				continue;
			}
			if (files.Count >= MaxRecoverEntries) continue;
			bool canRec = firstCluster >= 2 && size > 0;
			files.Add(new DeletedFile
			{
				ExFat = true, Contiguous = true,
				Deleted = deleted,
				Name = name, Path = path + name,
				Size = size, SizeText = FormatBytes(size),
				FirstCluster = firstCluster,
				ModifiedUtc = FatDateTime(dir, p),
				ModifiedText = FatDateTime(dir, p)?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "",
				Recoverable = canRec,
				Status = !deleted ? L("RfStOnDrive") : (canRec ? L("RfStContiguous") : (size == 0 ? L("RfStEmpty") : L("RfStWiped")))
			});
		}
	}

	private NtfsScanResult ScanDeletedFiles(char letter, CancellationToken ct, Action<int> progress)
	{
		using var vr = OpenVolume(letter);
		return DispatchScan(vr, letter, ct, progress);
	}

	private NtfsScanResult ScanDeletedFilesImage(string imagePath, CancellationToken ct, Action<int> progress)
	{
		using var vr = new VolumeReader(imagePath);
		var r = DispatchScan(vr, '\0', ct, progress);
		r.ImagePath = imagePath;
		return r;
	}

	private NtfsScanResult DispatchScan(VolumeReader vr, char letter, CancellationToken ct, Action<int> progress)
	{
		byte[] boot = vr.Read(0, 512);
		bool isNtfs = boot.Length >= 8 && boot[3] == 'N' && boot[4] == 'T' && boot[5] == 'F' && boot[6] == 'S';
		bool isExFat = boot.Length >= 11 && boot[3] == 'E' && boot[4] == 'X' && boot[5] == 'F' && boot[6] == 'A' && boot[7] == 'T';
		bool isFat32 = boot.Length >= 0x5A && boot[0x52] == 'F' && boot[0x53] == 'A' && boot[0x54] == 'T' && boot[0x55] == '3' && boot[0x56] == '2';
		bool isFat16 = boot.Length >= 0x3B && boot[0x36] == 'F' && boot[0x37] == 'A' && boot[0x38] == 'T'; // FAT12 / FAT16
		if (isNtfs) return ScanNtfs(letter, vr, boot, ct, progress);
		if (isExFat) return ScanExFat(letter, vr, boot, ct, progress);
		if (isFat32) return ScanFat32(letter, vr, boot, ct, progress);
		if (isFat16) return ScanFat16(letter, vr, boot, ct, progress);
		throw new IOException("This source is not a supported volume (NTFS, exFAT, FAT32, FAT16 or FAT12).");
	}

	// Rebuilds one deleted file onto disk by reading its clusters off the raw volume.
	private static void CopyDirectory(string src, string destDir)
	{
		Directory.CreateDirectory(destDir);
		foreach (var file in Directory.GetFiles(src)) File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
		foreach (var sub in Directory.GetDirectories(src)) CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
	}

	private long RecoverOne(VolumeReader vr, DeletedFile f, NtfsScanResult g, string outPath)
	{
		// Recycle Bin entry: the data still exists as a real $R file/folder — just copy it. Zero-risk recovery.
		if (!string.IsNullOrEmpty(f.SourcePath))
		{
			if (Directory.Exists(f.SourcePath)) { CopyDirectory(f.SourcePath, outPath); return 0; }
			File.Copy(f.SourcePath, outPath, true);
			try { return new FileInfo(outPath).Length; } catch { return f.Size; }
		}
		using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
		int clusterSize = g.ClusterSize;

		if (f.Carved)
		{
			long remaining = f.Size, off = f.ByteOffset, wrote = 0;
			while (remaining > 0)
			{
				int chunk = (int)Math.Min(4L << 20, remaining);
				byte[] data = vr.Read(off, chunk, out int got);
				if (got <= 0) break;
				outFs.Write(data, 0, got);
				off += got; remaining -= got; wrote += got;
				if (got < chunk) break; // reached end of source / unreadable region — don't invent zeros
			}
			return wrote;
		}

		if (f.Resident && f.ResidentData != null) { outFs.Write(f.ResidentData, 0, f.ResidentData.Length); return f.ResidentData.Length; }

		if (f.ExFat)
		{
			long remaining = f.Size, cl = f.FirstCluster; int guard = 0;
			var seen = new HashSet<long>();
			while (remaining > 0 && cl >= 2 && guard++ < 10_000_000 && seen.Add(cl))
			{
				int chunk = (int)Math.Min(clusterSize, remaining);
				byte[] data = vr.Read(g.DataAreaOffset + (cl - 2) * (long)clusterSize, chunk);
				outFs.Write(data, 0, chunk);
				remaining -= chunk;
				if (f.Contiguous) { cl++; }
				else { long next = BitConverter.ToUInt32(vr.Read(g.FatOffset + cl * 4, 4), 0); if (next >= 0xFFFFFFF8 || next < 2) break; cl = next; }
			}
			return f.Size - remaining;
		}

		// NTFS: walk data runs (absolute LCNs).
		long left = f.Size, written = 0;
		foreach (var (lcn, count) in f.Runs)
		{
			if (left <= 0) break;
			long runBytes = count * clusterSize;
			if (lcn < 0)
			{
				long z = Math.Min(runBytes, left);
				byte[] zeros = new byte[(int)Math.Min(1 << 20, z)];
				long rem = z; while (rem > 0) { int w = (int)Math.Min(zeros.Length, rem); outFs.Write(zeros, 0, w); rem -= w; }
				written += z; left -= z; continue;
			}
			long off = lcn * (long)clusterSize, pos = 0, take = Math.Min(runBytes, left);
			while (take > 0)
			{
				int chunk = (int)Math.Min(4L << 20, take);
				byte[] data = vr.Read(off + pos, chunk, out int got);
				if (got <= 0) { left = 0; break; }          // unreadable cluster — stop rather than zero-pad
				outFs.Write(data, 0, got);
				pos += got; take -= got; written += got; left -= got;
				if (got < chunk) { left = 0; break; }
			}
		}
		return written;
	}
}
