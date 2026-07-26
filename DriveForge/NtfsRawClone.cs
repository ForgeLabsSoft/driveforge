using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DriveForge;

// =================================================================================================
// Raw NTFS clone engine — "option C" (the AV-transparent, no-temp, any-size faithful clone).
//
// WHY: the standard wimlib FILE-LEVEL capture opens every source file through the file-system
// filter stack, so a real-time antivirus minifilter scans each read and the WinSxS store crawls to
// ~100 B/s. Reading the MFT + data clusters straight off the raw volume device (as the recovery
// scanner already does) is INVISIBLE to that minifilter — no file is ever opened — so it stays fast
// with antivirus active, needs no intermediate WIM (works when the disk is almost full), and writes
// files onto a fresh right-sized target (fits a smaller drive than the source).
//
// THIS FILE = STAGE 0 ONLY: a READ-ONLY capture walker + a CSV dump. It writes to NO disk. Its job
// is to prove the in-use MFT walker — especially $ATTRIBUTE_LIST stitching, which if wrong would
// silently TRUNCATE large fragmented WinSxS binaries into a booting-but-broken clone — BEFORE any
// destructive write stage exists. It surfaces two self-consistency red flags (files whose size we
// could not resolve, and files whose data runs don't cover their declared size) that catch a
// stitching bug with zero hardware risk, plus totals to diff against a trusted wimlib capture.
//
// The later write stages ship behind an opt-in checkbox with wimlib/DISM kept as the default engine
// until each stage's fidelity is independently verified. See memory: block-clone-design.
// =================================================================================================
public partial class MainWindow
{
	// One data stream of a file: the unnamed $DATA (Name == "") or a named alternate data stream.
	// A stream can be split across several $ATTRIBUTE_LIST extension records, so runs are collected
	// as fragments (each tagged with its starting VCN) and reassembled in VCN order by the writer.
	private sealed class RawStream
	{
		public string Name = "";              // "" = main unnamed $DATA; otherwise an ADS name
		public bool Resident;
		public byte[]? ResidentData;          // captured for small resident streams (used by the writer in later stages)
		public long RealSize;                 // logical size: resident value length, or non-res $DATA +0x30 at VCN 0
		public long ValidDataLength;          // non-res +0x38 — bytes past this read back as zero, not stale clusters
		public bool Sparse;
		public bool Compressed;               // NTFS-native (LZNT1) compression flag — NOT WOF
		public bool Encrypted;                // EFS
		public bool SawVcnZero;               // did we find the fragment that carries RealSize? (false = stitching gap)
		public readonly List<(long StartVcn, List<(long Lcn, long Count)> Runs)> Fragments = new();

		public int RunCount { get { int n = 0; foreach (var f in Fragments) n += f.Runs.Count; return n; } }

	}

	// One in-use file or directory, reassembled from its base MFT record plus any extension records.
	private sealed class RawNode
	{
		public long RecNo;
		public ushort HardlinkCount;
		public bool IsDir;
		public uint DosAttributes;
		public long CreatedUtc, ModifiedUtc, ChangedUtc, AccessedUtc;   // raw FILETIME (100-ns since 1601)
		public uint SecurityId;
		public bool HasAttrList;
		public bool ExtIncomplete;             // an $ATTRIBUTE_LIST extension record (or a $DATA header) was unreadable/malformed — completeness can't be trusted
		public uint ReparseTag;
		public int ReparseLen;                 // resident reparse-buffer length; -1 = non-resident (rare)
		public byte[]? ReparseBuffer;          // full REPARSE_DATA_BUFFER (tag+len+data) to replay via FSCTL_SET_REPARSE_POINT
		public string PrimaryName = "";
		public long ParentRef = 5;             // parent directory MFT number (root = 5)
		public int NameRank = -1;              // internal: best $FILE_NAME namespace seen so far
		public readonly List<(string Name, long ParentRef)> Names = new();   // every hardlink name + its own parent dir
		public readonly Dictionary<string, RawStream> Streams = new(StringComparer.Ordinal);
	}

	private const uint ReparseTagWof = 0x80000017;   // IO_REPARSE_TAG_WOF (CompactOS / WOF-compressed system files)

	// Chunked sequential MFT reader (fast bulk I/O). Invokes `visit(recNo, buffer, offset, flags,
	// baseFileRef)` once per record that carries the "FILE" signature. Mirrors the reader in ScanNtfs.
	private void WalkMftRecords(VolumeReader vr, List<(long Lcn, long Count)> mftRuns, int recSize,
		int clusterSize, int bytesPerSector, Action<long, byte[], int, ushort, long, bool> visit)
	{
		int recsPerCluster = Math.Max(1, clusterSize / recSize);
		long globalIndex = 0;
		const int chunkClusters = 256;
		foreach (var (lcn, count) in mftRuns)
		{
			if (stopRequested) break;
			if (lcn < 0) { globalIndex += count * recsPerCluster; continue; }
			long c = 0;
			while (c < count)
			{
				if (stopRequested) break;
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
					// ApplyFixup corrects the consistent sectors regardless; `torn` is true if any sector sentinel
					// mismatched (a partially-updated record). We DON'T skip here — the callback decides: the file-DATA
					// passes skip torn records (stale runs = corrupt), but the dir-NAME passes tolerate them so a torn
					// directory still enters dirNames (its $FILE_NAME is early in the record, rarely in the torn tail) —
					// otherwise its whole subtree would resolve to a wrong, shorter path.
					bool torn = !ApplyFixup(buf, off, recSize, bytesPerSector);
					ushort flags = BitConverter.ToUInt16(buf, off + 0x16);
					long baseRef = BitConverter.ToInt64(buf, off + 0x20) & 0xFFFFFFFFFFFF;
					visit(idx, buf, off, flags, baseRef, torn);
				}
				globalIndex += recsInBuf;
				c += take;
			}
		}
	}

	// Guards against a fragmented $MFT: ReadMftRuns only reads the base $MFT record's $DATA runs and does NOT follow
	// the $MFT's own $ATTRIBUTE_LIST. If $MFT is fragmented enough to spill its runs into extension records, the run
	// list is TRUNCATED and the whole-volume walk would silently skip every record in the missing extents. Refuse
	// loudly instead of producing a clone that is silently missing files.
	private static void EnsureMftRunsComplete(VolumeReader vr, long mftByteOffset, int recSize, int bytesPerSector, int clusterSize, List<(long Lcn, long Count)> mftRuns)
	{
		byte[] rec = vr.Read(mftByteOffset, recSize);
		if (rec.Length < 4 || rec[0] != (byte)'F' || rec[1] != (byte)'I' || rec[2] != (byte)'L' || rec[3] != (byte)'E') return;
		ApplyFixup(rec, 0, recSize, bytesPerSector);
		int attrOff = BitConverter.ToUInt16(rec, 0x14);
		bool sawAttrList = false; long mftRealSize = -1; int guard = 0;
		while (attrOff + 8 <= rec.Length && guard++ < 64)
		{
			uint type = BitConverter.ToUInt32(rec, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(rec, attrOff + 0x04);
			if (attrLen <= 0 || attrOff + attrLen > rec.Length) break;
			if (type == 0x20) sawAttrList = true;
			if (type == 0x80 && rec[attrOff + 0x08] != 0 && mftRealSize < 0 && attrOff + 0x30 <= rec.Length)
			{
				long startVcn = BitConverter.ToInt64(rec, attrOff + 0x10);
				if (startVcn == 0) mftRealSize = BitConverter.ToInt64(rec, attrOff + 0x28);   // AllocatedSize — total bytes the runs must cover
			}
			attrOff += attrLen;
		}
		if (sawAttrList && mftRealSize > 0)
		{
			long covered = 0; foreach (var (lcn, cnt) in mftRuns) if (lcn >= 0) covered += cnt;
			if (covered * (long)clusterSize < mftRealSize)
				throw new IOException("This volume's $MFT is fragmented across an $ATTRIBUTE_LIST; the raw engine reads only the base MFT run list and would silently drop files. Use the standard/DISM engine (uncheck the raw engine) for this volume.");
		}
	}

	// Random-access read of MFT record `recNo`, mapping it through the MFT's own data runs. Used to
	// pull a file's extension records so fragmented/large files get their FULL run list — the single
	// most important correctness point for big WinSxS binaries. Returns null on a sparse/short read.
	private static byte[]? ReadMftRecordByNumber(VolumeReader vr, List<(long Lcn, long Count)> mftRuns,
		long recNo, int recSize, int clusterSize, int bytesPerSector)
	{
		long vbo = recNo * (long)recSize;              // byte offset inside the MFT data stream
		long targetVcn = vbo / clusterSize;
		long offInCluster = vbo % clusterSize;
		long cumVcn = 0;
		foreach (var (lcn, count) in mftRuns)
		{
			if (targetVcn < cumVcn + count)
			{
				if (lcn < 0) return null;              // sparse hole in the MFT (should not happen for a live record)
				long physical = (lcn + (targetVcn - cumVcn)) * (long)clusterSize + offInCluster;
				byte[] rec = vr.Read(physical, recSize, out int got);
				if (got < recSize) return null;
				if (rec.Length < 4 || rec[0] != (byte)'F' || rec[1] != (byte)'I' || rec[2] != (byte)'L' || rec[3] != (byte)'E') return null;
				if (!ApplyFixup(rec, 0, recSize, bytesPerSector)) return null;   // torn record — treat as unreadable
				return rec;
			}
			cumVcn += count;
		}
		return null;
	}

	// Merges one MFT record's attributes into `node`: timestamps/attrs/security-id ($STANDARD_INFO),
	// every $FILE_NAME (for the primary name + hardlink group), every $DATA fragment (main + ADS),
	// the reparse buffer, and — when `attrListTargets` is provided — the $ATTRIBUTE_LIST references.
	// Called for the base record and each extension record so a split file is reassembled.
	private void MergeRecordAttributes(byte[] buf, int off, int recSize, RawNode node,
		VolumeReader vr, int clusterSize, List<long>? attrListTargets)
	{
		int attrOff = off + BitConverter.ToUInt16(buf, off + 0x14);
		int end = off + recSize;
		int guard = 0;
		while (attrOff + 8 <= end && guard++ < 256)
		{
			uint type = BitConverter.ToUInt32(buf, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(buf, attrOff + 0x04);
			if (attrLen <= 0 || attrOff + attrLen > end) break;
			if (attrOff + 0x18 > end) break;   // must have room for a full resident attribute header before dereferencing it (torn record tail)
			bool nonRes = buf[attrOff + 0x08] != 0;
			int aNameLen = buf[attrOff + 0x09];
			int aNameOff = BitConverter.ToUInt16(buf, attrOff + 0x0A);
			ushort af = BitConverter.ToUInt16(buf, attrOff + 0x0C);

			switch (type)
			{
				case 0x10:  // $STANDARD_INFORMATION
				{
					int vlen = BitConverter.ToInt32(buf, attrOff + 0x10);
					int cpos = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
					if (cpos >= attrOff && cpos + 0x24 <= attrOff + attrLen)
					{
						node.CreatedUtc = BitConverter.ToInt64(buf, cpos + 0x00);
						node.ModifiedUtc = BitConverter.ToInt64(buf, cpos + 0x08);
						node.ChangedUtc = BitConverter.ToInt64(buf, cpos + 0x10);
						node.AccessedUtc = BitConverter.ToInt64(buf, cpos + 0x18);
						node.DosAttributes = BitConverter.ToUInt32(buf, cpos + 0x20);
						if (vlen >= 0x38 && cpos + 0x38 <= attrOff + attrLen) node.SecurityId = BitConverter.ToUInt32(buf, cpos + 0x34);
					}
					break;
				}
				case 0x30:  // $FILE_NAME
				{
					int cpos = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
					if (cpos >= attrOff && cpos + 0x42 <= attrOff + attrLen)
					{
						int fnLen = buf[cpos + 0x40]; int ns = buf[cpos + 0x41]; int nb = fnLen * 2;
						if (cpos + 0x42 + nb <= attrOff + attrLen)
						{
							string nm = Encoding.Unicode.GetString(buf, cpos + 0x42, nb);
							long par = BitConverter.ToInt64(buf, cpos) & 0xFFFFFFFFFFFF;
							if (ns != 2 && !string.IsNullOrEmpty(nm) && !node.Names.Any(x => x.Name == nm && x.ParentRef == par)) node.Names.Add((nm, par));  // skip DOS 8.3 companion
							int rank = ns == 1 || ns == 3 ? 3 : ns == 0 ? 2 : 1;
							if (rank > node.NameRank) { node.NameRank = rank; node.PrimaryName = nm; node.ParentRef = par; }
						}
					}
					break;
				}
				case 0x80:  // $DATA — unnamed = main file bytes; named = alternate data stream
				{
					string sname = aNameLen > 0 && attrOff + aNameOff + aNameLen * 2 <= attrOff + attrLen
						? Encoding.Unicode.GetString(buf, attrOff + aNameOff, aNameLen * 2) : "";
					if (!node.Streams.TryGetValue(sname, out var st)) { st = new RawStream { Name = sname }; node.Streams[sname] = st; }
					if ((af & 0x0001) != 0) st.Compressed = true;
					if ((af & 0x4000) != 0) st.Encrypted = true;
					if ((af & 0x8000) != 0) st.Sparse = true;
					if (!nonRes)
					{
						int vlen = BitConverter.ToInt32(buf, attrOff + 0x10);
						int cpos = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
						if (vlen >= 0 && cpos >= attrOff && cpos <= attrOff + attrLen && vlen <= attrOff + attrLen - cpos)
						{
							st.Resident = true; st.RealSize = vlen; st.ValidDataLength = vlen; st.SawVcnZero = true;
							st.ResidentData = new byte[vlen];
							Array.Copy(buf, cpos, st.ResidentData, 0, vlen);
						}
					}
					else
					{
						// A non-resident header must be a full 0x40 bytes (mapping-pairs offset @0x20, RealSize @0x30, VDL @0x38).
						// A shorter attrLen means a malformed record — reading these would pull size/offset from the NEXT
						// attribute (silent truncation or a garbage giant size). Skip + flag so a lost main stream fails loudly.
						if (attrLen < 0x40 || attrOff + 0x40 > end) { node.ExtIncomplete = true; break; }
						long startVcn = BitConverter.ToInt64(buf, attrOff + 0x10);
						int runOff = BitConverter.ToUInt16(buf, attrOff + 0x20);
						var runs = DecodeRuns(buf, attrOff + runOff, attrOff + attrLen);
						st.Fragments.Add((startVcn, runs));
						if (startVcn == 0)
						{
							st.SawVcnZero = true;
							st.RealSize = BitConverter.ToInt64(buf, attrOff + 0x30);
							st.ValidDataLength = BitConverter.ToInt64(buf, attrOff + 0x38);
						}
					}
					break;
				}
				case 0xC0:  // $REPARSE_POINT
				{
					if (!nonRes)
					{
						int vlen = BitConverter.ToInt32(buf, attrOff + 0x10);
						int cpos = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
						if (vlen >= 8 && cpos >= attrOff && cpos <= attrOff + attrLen && vlen <= attrOff + attrLen - cpos)
						{
							node.ReparseTag = BitConverter.ToUInt32(buf, cpos);
							node.ReparseLen = vlen;
							node.ReparseBuffer = new byte[vlen];        // the whole attribute value IS the FSCTL_SET_REPARSE_POINT input
							Array.Copy(buf, cpos, node.ReparseBuffer, 0, vlen);
						}
					}
					else
					{
						node.ReparseLen = -1; node.ReparseTag = 0xFFFFFFFFu; // sentinel tag routes it to CreateReparse (counts+skips, no misleading empty file/dir); non-resident reparse (very rare) — skipped
					}
					break;
				}
				case 0x20:  // $ATTRIBUTE_LIST — attributes spread across other MFT records
				{
					node.HasAttrList = true;
					if (attrListTargets != null) CollectAttrListTargets(buf, attrOff, attrLen, nonRes, vr, clusterSize, node.RecNo, attrListTargets);
					break;
				}
			}
			attrOff += attrLen;
		}
	}

	// Reads an $ATTRIBUTE_LIST (resident or non-resident) and appends every referenced extension
	// record number (other than the base record itself) to `targets`.
	private void CollectAttrListTargets(byte[] buf, int attrOff, int attrLen, bool nonRes,
		VolumeReader vr, int clusterSize, long selfRec, List<long> targets)
	{
		if (!nonRes)
		{
			int vlen = BitConverter.ToInt32(buf, attrOff + 0x10);
			int cpos = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
			if (vlen <= 0 || cpos < attrOff || vlen > attrOff + attrLen - cpos) return;   // bound to THIS attribute's value, not the whole record (else foreign list entries leak in)
			ParseAttrListEntries(buf, cpos, cpos + vlen, selfRec, targets);
		}
		else
		{
			if (attrLen < 0x40 || attrOff + 0x40 > buf.Length) return;   // malformed non-resident header — don't read runOff/RealSize past the attribute
			int runOff = BitConverter.ToUInt16(buf, attrOff + 0x20);
			long realSize = BitConverter.ToInt64(buf, attrOff + 0x30);
			var runs = DecodeRuns(buf, attrOff + runOff, attrOff + attrLen);
			using var ms = new MemoryStream();
			foreach (var (lcn, count) in runs)
			{
				if (lcn < 0) continue;
				long bytes = count * (long)clusterSize, o = lcn * (long)clusterSize, done = 0;
				while (done < bytes && ms.Length < (64L << 20)) { int ch = (int)Math.Min(1 << 20, bytes - done); ms.Write(vr.Read(o + done, ch), 0, ch); done += ch; }
			}
			byte[] la = ms.ToArray();
			int cap = realSize > 0 ? (int)Math.Min(la.Length, realSize) : la.Length;
			ParseAttrListEntries(la, 0, cap, selfRec, targets);
		}
	}

	private static void ParseAttrListEntries(byte[] la, int pos, int end, long selfRec, List<long> targets)
	{
		int guard = 0;
		while (pos + 0x18 <= end && guard++ < 8192)
		{
			int recordLen = BitConverter.ToUInt16(la, pos + 0x04);
			if (recordLen < 0x18) break;
			long baseRef = BitConverter.ToInt64(la, pos + 0x10) & 0xFFFFFFFFFFFF;
			if (baseRef != selfRec && baseRef != 0 && !targets.Contains(baseRef)) targets.Add(baseRef);
			pos += recordLen;
		}
	}

	// Fully reassembles one in-use base record into a RawNode: its own attributes plus every attribute
	// that lives in an $ATTRIBUTE_LIST extension record. Shared by the Stage-0 dump and the Stage-1 writer.
	private RawNode BuildNode(long recNo, byte[] buf, int off, bool isDir, VolumeReader vr,
		List<(long Lcn, long Count)> mftRuns, int recSize, int clusterSize, int bytesPerSector)
	{
		var node = new RawNode { RecNo = recNo, IsDir = isDir, HardlinkCount = BitConverter.ToUInt16(buf, off + 0x12) };
		var targets = new List<long>();
		MergeRecordAttributes(buf, off, recSize, node, vr, clusterSize, targets);
		if (node.HasAttrList)
		{
			int tg = 0;
			foreach (long trec in targets)
			{
				if (tg++ > 4096) { node.ExtIncomplete = true; break; }   // too many extension records to follow — can't guarantee completeness
				byte[]? ext = ReadMftRecordByNumber(vr, mftRuns, trec, recSize, clusterSize, bytesPerSector);
				if (ext != null) MergeRecordAttributes(ext, 0, recSize, node, vr, clusterSize, null);
				else node.ExtIncomplete = true;   // an extension record was unreadable — a $DATA fragment may be missing; don't write a silent truncated/0-byte file
			}
		}
		return node;
	}

	// ============================================================================================
	// STAGE 1 writer — the first DESTRUCTIVE part of the raw engine. It reads the source snapshot
	// straight off the raw device (antivirus-invisible) and writes each in-use file onto the freshly
	// formatted target with normal Win32 file APIs, reusing DriveForge's existing format / bcdboot /
	// portable-registry / verify / report pipeline unchanged (only the copy engine is swapped in).
	//
	// Stage-1 fidelity: directories, the main $DATA stream (honoring ValidDataLength so an uninitialised
	// tail is written as zero, not stale clusters), timestamps, DOS attributes, and HARDLINKS (WinSxS is
	// ~400k hardlinks; deduping them keeps the target from ballooning and every path present).
	// NOT YET applied (later stages, wimlib/DISM remain the trusted default): reparse points/junctions,
	// security descriptors/ACLs, alternate data streams, WOF/CompactOS decompression. Reparse files are
	// skipped this stage. Treat a Stage-1 clone as a TEST, not a trustworthy backup.
	// ============================================================================================
	private const uint CreateAlways = 2;
	private const uint DosAttrSettableMask = 0x2127;   // READONLY|HIDDEN|SYSTEM|ARCHIVE|TEMPORARY|NOT_CONTENT_INDEXED
	private const uint RawFileAttrNormal = 0x80;       // FILE_ATTRIBUTE_NORMAL (own name to avoid a clash with the existing const)

	// Extended-length path prefix so the raw Win32 P/Invokes (CreateFile/CreateHardLink/SetFileAttributes/
	// SetFileSecurity) accept WinSxS paths longer than MAX_PATH (260) — those long paths were the copy pass's write errors.
	private static string Ext(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;

	private sealed class RawCloneStats { public long Dirs, Files, Bytes, Links, LinkCopies, Reparse, ReparseSet, Errors, SecApplied, SecErrors, RunShortfalls, ReadShortfalls, CompressedViaApi, EfsSkipped, EfsCopied, DiskFull, AdsCopied, AdsErrors; public int SdsLoaded = -1; public int FirstSecError; }

	// Copies one alternate data stream (path:streamname) from the snapshot to the target via the file API, reading the
	// OS view so resident / non-resident / NTFS-compressed streams are handled uniformly. Backup semantics for ACLs.
	private void CopyStreamViaApi(string srcStreamPath, string tgtStreamPath, RawCloneStats stats)
	{
		using var srcH = new SafeFileHandleWrite(CreateFile(srcStreamPath, GenericRead, 0x7u, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero));
		if (srcH.Handle.IsInvalid) throw new IOException($"Open ADS source failed ({Marshal.GetLastWin32Error()}) for {srcStreamPath}");
		using var dstH = new SafeFileHandleWrite(CreateFile(tgtStreamPath, GenericWrite, 0x7u, IntPtr.Zero, CreateAlways, FileFlagBackupSemantics, IntPtr.Zero));
		if (dstH.Handle.IsInvalid) throw new IOException($"Create ADS target failed ({Marshal.GetLastWin32Error()}) for {tgtStreamPath}");
		using (var src = new FileStream(srcH.Handle, FileAccess.Read))
		using (var dst = new FileStream(dstH.Handle, FileAccess.Write))
			src.CopyTo(dst, 1 << 20);
		stats.AdsCopied++;
	}

	// True when an exception is a disk-full condition (ERROR_DISK_FULL 112 / ERROR_HANDLE_DISK_FULL 39).
	private static bool IsDiskFull(Exception ex)
	{
		int hr = ex.HResult & 0xFFFF;
		return hr == 112 || hr == 39 || (ex is IOException && ((ex.HResult & 0xFFFF) == 0x70 || (ex.HResult & 0xFFFF) == 0x27));
	}

	private const uint OpenExisting = 3;
	private const uint FsctlSetSparse = 0x000900C4;

	// Recreates a reparse point (symlink / junction / WOF) on the target by replaying its raw REPARSE_DATA_BUFFER —
	// exactly what Bitdefender's service-config symlinks need so its services start on the clone. For a directory
	// junction the empty dir already exists (dir pass); for a file symlink we create the empty file here first.
	private void CreateReparse(RawNode node, string fullPath, bool isDir, RawCloneStats stats)
	{
		if (node.ReparseBuffer == null || node.ReparseBuffer.Length < 8) { stats.Reparse++; return; }   // non-resident/absent — can't replay
		if (node.ReparseTag == ReparseTagWof) { stats.Reparse++; return; }   // WOF/CompactOS: real data is in a WofCompressedData ADS we don't handle yet — replaying only the tag = unreadable file (WOF=0 on this PC)
		uint disposition = isDir ? OpenExisting : CreateAlways;
		using var h = new SafeFileHandleWrite(CreateFile(Ext(fullPath), GenericWrite, 0x3u, IntPtr.Zero, disposition,
			FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero));
		if (h.Handle.IsInvalid) { stats.Errors++; return; }
		if (DeviceIoControl(h.Handle, FsctlSetReparsePoint, node.ReparseBuffer, node.ReparseBuffer.Length, null, 0, out _, IntPtr.Zero))
		{
			stats.ReparseSet++;
			if (node.CreatedUtc > 0 && node.ModifiedUtc > 0)
			{
				var cr = new FFileTime { Low = (uint)node.CreatedUtc, High = (uint)(node.CreatedUtc >> 32) };
				var wr = new FFileTime { Low = (uint)node.ModifiedUtc, High = (uint)(node.ModifiedUtc >> 32) };
				SetFileTime(h.Handle, ref cr, IntPtr.Zero, ref wr);
			}
		}
		else stats.Errors++;
	}

	// SECURITY_INFORMATION bits for SetFileSecurity.
	private const uint SiOwner = 0x00000001, SiGroup = 0x00000002, SiDacl = 0x00000004, SiSacl = 0x00000008;
	private const uint SiProtectedDacl = 0x80000000, SiUnprotectedDacl = 0x20000000;
	private const uint SiProtectedSacl = 0x40000000, SiUnprotectedSacl = 0x10000000;
	// Self-relative SECURITY_DESCRIPTOR control bits (at byte offset 2).
	private const ushort SeDaclPresent = 0x0004, SeSaclPresent = 0x0010, SeDaclProtected = 0x1000, SeSaclProtected = 0x2000;

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetFileSecurityW(string lpFileName, uint SecurityInformation, byte[] pSecurityDescriptor);

	// Loads $Secure:$SDS (MFT entry 9) into a map of security-id -> raw self-relative security-descriptor bytes.
	// These are applied verbatim to the clone (SAME machine, so no SID remapping) to reproduce owners/ACLs/SACLs.
	private Dictionary<uint, byte[]> LoadSecurityDescriptors(VolumeReader vr, List<(long Lcn, long Count)> mftRuns, int recSize, int clusterSize, int bytesPerSector)
	{
		var map = new Dictionary<uint, byte[]>();
		byte[]? rec = ReadMftRecordByNumber(vr, mftRuns, 9, recSize, clusterSize, bytesPerSector);
		if (rec == null) return map;
		RawNode node;
		try { node = BuildNode(9, rec, 0, false, vr, mftRuns, recSize, clusterSize, bytesPerSector); }   // resolves an $ATTRIBUTE_LIST if $Secure is fragmented
		catch { return map; }   // a corrupt $Secure record must not abort the whole security pass (which would leave the clone with default ACLs)
		if (!node.Streams.TryGetValue("$SDS", out var sds)) return map;
		byte[] data = ReadStreamBytes(vr, sds, clusterSize);
		long pos = 0; int guard = 0;
		while (pos + 20 <= data.Length && guard++ < 20_000_000)
		{
			uint id = BitConverter.ToUInt32(data, (int)pos + 4);
			uint len = BitConverter.ToUInt32(data, (int)pos + 16);
			if (len < 20 || len > 0x40000 || pos + len > data.Length)
			{
				long nextBlock = ((pos / 0x40000) + 1) * 0x40000;   // $SDS entries never cross a 256 KB block — skip the padding
				if (nextBlock <= pos) break;
				pos = nextBlock; continue;
			}
			int sdLen = (int)(len - 20);
			if (id != 0 && sdLen > 0 && !map.ContainsKey(id) && pos + 20 + sdLen <= data.Length)
			{
				var sd = new byte[sdLen];
				Array.Copy(data, (int)pos + 20, sd, 0, sdLen);
				map[id] = sd;
			}
			pos += (len + 15) & ~15L;   // entries are 16-byte aligned
		}
		return map;
	}

	// Reads a stream's full logical bytes (resident value, or its data runs). Used for the small $SDS metafile stream.
	private byte[] ReadStreamBytes(VolumeReader vr, RawStream st, int clusterSize)
	{
		if (st.Resident) return st.ResidentData ?? Array.Empty<byte>();
		using var ms = new MemoryStream();
		long left = Math.Min(st.RealSize > 0 ? st.RealSize : long.MaxValue, 512L << 20);   // cap: $SDS is small; a corrupt huge RealSize must not drive an unbounded MemoryStream (OOM)
		foreach (var frag in st.Fragments.OrderBy(f => f.StartVcn))
			foreach (var (lcn, count) in frag.Runs)
			{
				if (left <= 0) break;
				long runBytes = count * (long)clusterSize;
				if (lcn < 0) { long z = Math.Min(runBytes, left); long d = 0; var zb = new byte[(int)Math.Min(z, 1 << 20)]; while (d < z) { int w = (int)Math.Min(zb.Length, z - d); ms.Write(zb, 0, w); d += w; } left -= z; continue; }
				long o = lcn * (long)clusterSize, take = Math.Min(runBytes, left);
				while (take > 0) { int ch = (int)Math.Min(1 << 20, take); byte[] dd = vr.Read(o, ch, out int got); if (got <= 0) break; ms.Write(dd, 0, got); o += got; take -= got; left -= got; if (got < ch) break; }
			}
		return ms.ToArray();
	}

	// Extracts the $STANDARD_INFORMATION security-id from a record (light parse — no extension records needed;
	// the security-id always lives in the base record's $SI). Returns 0 if absent (pre-NTFS-3.0 $SI).
	private static uint ReadSecurityId(byte[] buf, int off, int recSize)
	{
		int attrOff = off + BitConverter.ToUInt16(buf, off + 0x14);
		int end = off + recSize; int guard = 0;
		while (attrOff + 8 <= end && guard++ < 64)
		{
			uint type = BitConverter.ToUInt32(buf, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(buf, attrOff + 0x04);
			if (attrLen <= 0 || attrOff + attrLen > end || attrOff + 0x18 > end) break;
			if (type == 0x10)
			{
				int vlen = BitConverter.ToInt32(buf, attrOff + 0x10);
				int c = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
				if (vlen >= 0x38 && c >= attrOff && c + 0x38 <= attrOff + attrLen) return BitConverter.ToUInt32(buf, c + 0x34);
				return 0;
			}
			attrOff += attrLen;
		}
		return 0;
	}

	// Reads the $STANDARD_INFORMATION DOS attributes (+0x20) — used to detect FILE_ATTRIBUTE_REPARSE_POINT (0x400)
	// cheaply without a full BuildNode. Returns 0 if absent.
	private static uint ReadDosAttributes(byte[] buf, int off, int recSize)
	{
		int attrOff = off + BitConverter.ToUInt16(buf, off + 0x14);
		int end = off + recSize; int guard = 0;
		while (attrOff + 8 <= end && guard++ < 64)
		{
			uint type = BitConverter.ToUInt32(buf, attrOff);
			if (type == 0xFFFFFFFF) break;
			int attrLen = BitConverter.ToInt32(buf, attrOff + 0x04);
			if (attrLen <= 0 || attrOff + attrLen > end || attrOff + 0x18 > end) break;
			if (type == 0x10)
			{
				int cpos = attrOff + BitConverter.ToUInt16(buf, attrOff + 0x14);
				if (cpos >= attrOff && cpos + 0x24 <= attrOff + attrLen) return BitConverter.ToUInt32(buf, cpos + 0x20);
				return 0;
			}
			attrOff += attrLen;
		}
		return 0;
	}

	// Applies a raw security descriptor to a path. Forces the DACL/SACL protected state to match the source SD's
	// control bits, so the stored (fully-materialised) ACL is reproduced EXACTLY and never re-merged with the fresh
	// volume's inherited defaults. Falls back to fewer components if a privilege (SACL/owner) is unavailable.
	private void ApplySecurity(string path, byte[] sd, RawCloneStats stats)
	{
		if (sd.Length < 20) { stats.SecErrors++; return; }
		ushort control = BitConverter.ToUInt16(sd, 2);
		uint si = SiOwner | SiGroup;
		if ((control & SeDaclPresent) != 0) si |= SiDacl | ((control & SeDaclProtected) != 0 ? SiProtectedDacl : SiUnprotectedDacl);
		if ((control & SeSaclPresent) != 0) si |= SiSacl | ((control & SeSaclProtected) != 0 ? SiProtectedSacl : SiUnprotectedSacl);
		if (SetFileSecurityW(path, si, sd)) { stats.SecApplied++; return; }
		if (stats.FirstSecError == 0) stats.FirstSecError = Marshal.GetLastWin32Error();
		uint noSacl = si & ~(SiSacl | SiProtectedSacl | SiUnprotectedSacl);                 // no SeSecurityPrivilege → drop SACL
		if (SetFileSecurityW(path, noSacl, sd)) { stats.SecApplied++; return; }
		uint daclOnly = si & (SiDacl | SiProtectedDacl | SiUnprotectedDacl);                // can't set owner → at least set the DACL
		if (daclOnly != 0 && SetFileSecurityW(path, daclOnly, sd)) { stats.SecApplied++; return; }
		stats.SecErrors++;
	}

	private async Task<RawCloneStats> RawNtfsWriteCloneAsync(char sourceLetter, char targetLetter, string targetRoot)
	{
		var s = await Task.Run(() => RawNtfsWriteClone(sourceLetter, targetRoot));
		Log($"Raw engine copy done: {s.Files:N0} files, {FormatBytes(s.Bytes)}, {s.Dirs:N0} dirs, " +
			$"{s.Links:N0} hardlinks ({s.LinkCopies:N0} copies), {s.ReparseSet:N0} reparse, {s.CompressedViaApi:N0} compressed/WOF, {s.AdsCopied:N0} ADS, {s.EfsCopied:N0} EFS copied, {s.EfsSkipped:N0} EFS skipped, {s.Errors:N0} write errors" +
			(s.RunShortfalls + s.ReadShortfalls > 0 ? $", {s.RunShortfalls + s.ReadShortfalls:N0} zero-filled gaps" : "") + (s.DiskFull > 0 ? ", DISK FULL" : "") + ".");
		if (s.Errors > 0) Log($"Raw engine: {s.Errors:N0} files could not be written — see above. (Still experimental.)");
		return s;
	}

	private RawCloneStats RawNtfsWriteClone(char sourceLetter, string targetRoot)
	{
		var stats = new RawCloneStats();
		using var vr = OpenVolume(sourceLetter);
		byte[] boot = vr.Read(0, 512);
		if (boot.Length < 0x50 || boot[3] != (byte)'N' || boot[4] != (byte)'T' || boot[5] != (byte)'F' || boot[6] != (byte)'S')
			throw new IOException("The snapshot volume is not NTFS — the raw engine only clones NTFS Windows volumes.");
		int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
		int sectorsPerCluster = boot[0x0D];
		if (bytesPerSector == 0 || sectorsPerCluster == 0) throw new IOException("Unreadable NTFS boot sector.");
		int clusterSize = bytesPerSector * sectorsPerCluster;
		long mftStartCluster = BitConverter.ToInt64(boot, 0x30);
		sbyte cpr = unchecked((sbyte)boot[0x40]);
		int recSize = cpr > 0 ? cpr * clusterSize : 1 << (-cpr);
		if (recSize <= 0 || recSize > (1 << 20)) recSize = 1024;
		long mftByteOffset = mftStartCluster * (long)clusterSize;
		var mftRuns = ReadMftRuns(vr, mftByteOffset, recSize, bytesPerSector);
		if (mftRuns.Count == 0) mftRuns.Add((mftStartCluster, 64));
		EnsureMftRunsComplete(vr, mftByteOffset, recSize, bytesPerSector, clusterSize, mftRuns);
		string root = targetRoot.TrimEnd('\\') + "\\";

		// Pass 1: every in-use directory name, for path resolution. NOTE: security descriptors are applied in a
		// SEPARATE pass (RawNtfsApplySecurity) that runs AFTER the registry/bcdboot post-processing — locking a
		// source ACL onto the hive files / config dir here would block the portable-registry step that runs next.
		var dirNames = new Dictionary<long, (string name, long parent)>();
		WalkMftRecords(vr, mftRuns, recSize, clusterSize, bytesPerSector, (recNo, buf, off, flags, baseRef, torn) =>
		{
			if ((flags & 0x01) == 0 || baseRef != 0 || (flags & 0x02) == 0) return;   // torn tolerated for a directory: keep name/parent so its subtree resolves correctly
			var (dn, dp) = ReadNameAndParent(buf, off, recSize);
			if (!string.IsNullOrEmpty(dn)) dirNames[recNo] = (dn, dp);
		});

		// Create the whole directory tree up front (CreateDirectory builds the parent chain, so order is free).
		// NB: this runs on a background thread — use only the thread-safe Log(), never SetStage (touches UI directly).
		Log("Raw NTFS engine: creating the directory tree (reading the snapshot MFT)...");
		foreach (var kv in dirNames)
		{
			if (stopRequested) break;
			if (kv.Key < 16 || kv.Key == 5) continue;
			string relDir = ResolvePath(kv.Value.parent, dirNames) + kv.Value.name;
			string full = "\\" + relDir;
			if (full.StartsWith("\\$Extend", StringComparison.OrdinalIgnoreCase)) continue;
			if (IsNtfsCloneExcluded(full, true)) continue;
			try { Directory.CreateDirectory(Ext(root + relDir)); stats.Dirs++; } catch { stats.Errors++; }
		}

		// Pass 2: write every in-use file — main stream + timestamps + attributes + hardlinks.
		Log("Raw NTFS engine: copying files directly off the snapshot (antivirus-transparent)...");
		byte[] zeroBuf = new byte[1 << 20];   // pre-zeroed, never mutated — shared source for sparse holes / VDL tails
		long processed = 0;
		double totalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1073741824.0);
		WalkMftRecords(vr, mftRuns, recSize, clusterSize, bytesPerSector, (recNo, buf, off, flags, baseRef, torn) =>
		{
			if ((flags & 0x01) == 0 || baseRef != 0) return;
			if (recNo < 16) return;                          // NTFS metafiles
			if (torn) { stats.Errors++; return; }            // torn/partially-updated data record — stale runs would corrupt the file
			bool isDir = (flags & 0x02) != 0;
			RawNode node;
			try { node = BuildNode(recNo, buf, off, isDir, vr, mftRuns, recSize, clusterSize, bytesPerSector); }
			catch { stats.Errors++; return; }   // one torn/unreadable record must not abort the whole destructive clone
			if (string.IsNullOrEmpty(node.PrimaryName) && node.Names.Count == 0) return;

			// Reparse points (symlinks / junctions): recreate them by replaying the raw buffer. A dir junction's
			// empty dir already exists (dir pass); a file symlink's empty file is created inside CreateReparse.
			// WOF/CompactOS files (0x80000017) are NOT reparse-recreated — they fall through to the file path below and
			// are materialised UNCOMPRESSED by reading the OS-decompressed bytes via the file API (else they'd be dropped).
			if (node.ReparseTag != 0 && node.ReparseTag != ReparseTagWof)
			{
				var rnames = node.Names.Count > 0 ? node.Names : new List<(string Name, long ParentRef)> { (node.PrimaryName, node.ParentRef) };
				foreach (var (nm, par) in rnames)
				{
					if (string.IsNullOrEmpty(nm)) continue;
					string rd = ResolvePath(par, dirNames);
					string rr = "\\" + rd + nm;
					if (rr.StartsWith("\\$Extend\\", StringComparison.OrdinalIgnoreCase)) continue;
					if (IsNtfsCloneExcluded(rr, isDir)) continue;
					if (!isDir) { try { Directory.CreateDirectory(Ext(Path.GetDirectoryName(root + rd + nm)!)); } catch { } }
					CreateReparse(node, root + rd + nm, isDir, stats);
					break;   // the reparse is on the object itself; hardlinked reparse points are not a real case
				}
				return;
			}

			if (isDir)
			{
				// Directories have no unnamed $DATA, but NTFS allows NAMED $DATA streams ON a directory. The directory
				// itself was created in the dir pass; copy any named streams onto it here (they were silently lost before).
				bool hasNamedStream = false;
				foreach (var k in node.Streams.Keys) if (k.Length > 0) { hasNamedStream = true; break; }
				if (hasNamedStream)
				{
					var dnames = node.Names.Count > 0 ? node.Names : new List<(string Name, long ParentRef)> { (node.PrimaryName, node.ParentRef) };
					foreach (var (nm, par) in dnames)
					{
						if (string.IsNullOrEmpty(nm)) continue;
						string dd = ResolvePath(par, dirNames);
						string dr = "\\" + dd + nm;
						if (dr.StartsWith("\\$Extend\\", StringComparison.OrdinalIgnoreCase)) continue;
						if (IsNtfsCloneExcluded(dr, true)) continue;
						string dfull = root + dd + nm, dsrc = sourceLetter + ":\\" + dd + nm;
						foreach (var kv in node.Streams)
						{
							if (kv.Key.Length == 0 || kv.Key == "WofCompressedData" || kv.Value.Encrypted) continue;
							try { CopyStreamViaApi(Ext(dsrc) + ":" + kv.Key, Ext(dfull) + ":" + kv.Key, stats); }
							catch { stats.AdsErrors++; }
						}
						break;   // directories aren't hardlinked — one path is enough
					}
				}
				return;                               // plain directories were created in the dir pass
			}
			node.Streams.TryGetValue("", out var main);
			// EFS-encrypted files: the on-disk $DATA is ciphertext we have no key for. Copy it (all streams, ciphertext +
			// $EFS metadata) via the EFS backup/restore RAW API — which needs NO decryption key. The clone carries the
			// same user's EFS certificate (their profile + registry are cloned), so the file stays decryptable there,
			// exactly like `robocopy /EFSRAW`. If the API fails, fall back to counting it skipped (the old behaviour).
			if (main != null && main.Encrypted)
			{
				bool efsAny = false;
				string? efsWritten = null;   // first kept name gets the real encrypted copy; the rest hardlink to it
				var enames = node.Names.Count > 0 ? node.Names : new List<(string Name, long ParentRef)> { (node.PrimaryName, node.ParentRef) };
				foreach (var (nm, par) in enames)
				{
					if (string.IsNullOrEmpty(nm)) continue;
					string ed = ResolvePath(par, dirNames);
					string er = "\\" + ed + nm;
					if (er.StartsWith("\\$Extend\\", StringComparison.OrdinalIgnoreCase)) continue;
					if (IsNtfsCloneExcluded(er, false)) continue;
					string efull = root + ed + nm;
					string esrc = sourceLetter + ":\\" + ed + nm;
					try { Directory.CreateDirectory(Ext(Path.GetDirectoryName(efull)!)); } catch { }
					try
					{
						// A hardlinked encrypted file (one MFT record, several names) must not be re-encrypted N times —
						// link the extra names to the first real copy, exactly like the plain-file path below.
						if (efsWritten != null && CreateHardLink(Ext(efull), Ext(efsWritten), IntPtr.Zero)) { stats.Links++; continue; }
						if (CopyEfsRaw(esrc, efull, out bool diskFull))
						{
							ApplyEfsFileTimesAndAttrs(node, efull);
							stats.Files++; efsAny = true;
							if (efsWritten == null) efsWritten = efull;
						}
						else { stats.EfsSkipped++; if (diskFull) stats.DiskFull++; }   // surface a target-full as DISK FULL, not a soft "skipped"
					}
					catch (Exception ex) { stats.EfsSkipped++; if (IsDiskFull(ex)) stats.DiskFull++; }
				}
				if (efsAny) stats.EfsCopied++;
				return;   // WriteEncryptedFileRaw already reproduced ALL streams (main + any ADS) — don't re-copy below
			}

			// Write every hardlink name of this record (WinSxS shares one MFT record under many paths). The first
			// KEPT name gets the real bytes; the rest become hardlinks to it (falling back to a full copy). Filtering
			// per-name — not on a single "primary" — means a kept name is never dropped just because another name of
			// the same record lands in an excluded folder. Fall back to the primary if only a DOS 8.3 name existed.
			var names = node.Names.Count > 0 ? node.Names : new List<(string Name, long ParentRef)> { (node.PrimaryName, node.ParentRef) };
			string? writtenPath = null, writtenSourceFull = null;
			foreach (var (nm, par) in names)
			{
				if (string.IsNullOrEmpty(nm)) continue;
				string d = ResolvePath(par, dirNames);
				string r = "\\" + d + nm;
				if (r.StartsWith("\\$Extend\\", StringComparison.OrdinalIgnoreCase)) continue;
				if (IsNtfsCloneExcluded(r, false)) continue;
				string full = root + d + nm;
				string sourceFull = sourceLetter + ":\\" + d + nm;
				try
				{
					try { Directory.CreateDirectory(Ext(Path.GetDirectoryName(full)!)); } catch { }
					if (writtenPath == null) { WriteFileWithMeta(vr, node, main, full, sourceFull, clusterSize, zeroBuf, stats); writtenPath = full; writtenSourceFull = sourceFull; stats.Files++; }
					else if (CreateHardLink(Ext(full), Ext(writtenPath), IntPtr.Zero)) stats.Links++;
					else { WriteFileWithMeta(vr, node, main, full, sourceFull, clusterSize, zeroBuf, stats); stats.LinkCopies++; }  // >1024 links / cross-dir: full copy so the path still exists
				}
				catch (Exception ex) { stats.Errors++; if (IsDiskFull(ex)) stats.DiskFull++; }
			}

			// Alternate data streams (attached to the file, so shared by all hardlink names): copy each named $DATA
			// stream to target:streamname. Reading the OS view via the file API handles resident / non-resident /
			// NTFS-compressed uniformly. WofCompressedData is the WOF backing (the main stream was materialised
			// decompressed already) and EFS streams can't be decrypted — skip both.
			if (writtenPath != null && writtenSourceFull != null && node.Streams.Keys.Any(k => k.Length > 0))
			{
				// WriteFileWithMeta already stamped the DOS attributes. CreateFile(CREATE_ALWAYS) on a file that already
				// carries READONLY, HIDDEN or SYSTEM returns ACCESS_DENIED (documented Win32 behaviour) — which would
				// silently drop EVERY ADS on such files. Temporarily clear all three for the ADS writes, then restore
				// them (re-applying the attributes does not remove the streams that were just written).
				const uint adsBlockAttrs = 0x1u | 0x2u | 0x4u; // FILE_ATTRIBUTE_READONLY | HIDDEN | SYSTEM
				uint tgtAttr = node.DosAttributes & DosAttrSettableMask;
				bool clearedAttrs = (tgtAttr & adsBlockAttrs) != 0;
				if (clearedAttrs) SetFileAttributesW(Ext(writtenPath), (tgtAttr & ~adsBlockAttrs) == 0 ? RawFileAttrNormal : (tgtAttr & ~adsBlockAttrs));
				try
				{
					foreach (var kv in node.Streams)
					{
						if (kv.Key.Length == 0 || kv.Key == "WofCompressedData" || kv.Value.Encrypted) continue;
						try { CopyStreamViaApi(Ext(writtenSourceFull) + ":" + kv.Key, Ext(writtenPath) + ":" + kv.Key, stats); }
						catch { stats.AdsErrors++; }
					}
				}
				finally { if (clearedAttrs) SetFileAttributesW(Ext(writtenPath), tgtAttr); }
			}

			if ((++processed & 0x3FFF) == 0)
				Log($"Raw engine progress: {stats.Files:N0} files, {FormatBytes(stats.Bytes)} (~{Math.Min(99.0, stats.Bytes / 1073741824.0 / totalGiB * 100.0):F0}% of source data).");
		});

		// Diagnostics for the copy pass (the in-app log isn't saved on a successful clone). The security pass
		// (RawNtfsApplySecurity) runs later and APPENDS its results to this same file.
		try
		{
			string diag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "raw-engine-diag.txt");
			File.WriteAllText(diag, "Raw NTFS engine diagnostics (copy pass)\n" +
				$"Files written          : {stats.Files}\n" +
				$"Directories            : {stats.Dirs}\n" +
				$"Hardlinks              : {stats.Links} (fallback copies {stats.LinkCopies})\n" +
				$"Reparse points set     : {stats.ReparseSet} (skipped {stats.Reparse})\n" +
				$"Compressed/WOF via API : {stats.CompressedViaApi}\n" +
				$"Alt data streams (ADS) : {stats.AdsCopied} ({stats.AdsErrors} errors)\n" +
				$"EFS files copied (raw)   : {stats.EfsCopied}\n" +
				$"EFS skipped (no key/raw) : {stats.EfsSkipped}\n" +
				$"Run shortfalls (0-fill): {stats.RunShortfalls}   Bad-sector fills: {stats.ReadShortfalls}\n" +
				$"Write errors           : {stats.Errors}" + (stats.DiskFull > 0 ? "   *** DISK FULL — clone incomplete ***" : "") + "\n");
		}
		catch { }
		return stats;
	}

	// SECURITY PASS — applies owners/ACLs/SACLs, run AFTER the registry + bcdboot post-processing (so a restrictive
	// source ACL on the hive files / config dir can't block that step). Re-reads the snapshot (raw, AV-invisible),
	// loads $Secure:$SDS, and applies each object's stored descriptor to the file/dir on the target.
	private async Task RawNtfsApplySecurityAsync(char sourceLetter, string targetRoot)
	{
		var s = await Task.Run(() => RawNtfsApplySecurity(sourceLetter, targetRoot));
		Log($"Raw engine security: {s.SdsLoaded:N0} descriptors, {s.SecApplied:N0} ACLs applied, {s.SecErrors:N0} failures" +
			(s.FirstSecError != 0 ? $" (first Win32 error {s.FirstSecError})" : "") + ".");
	}

	private RawCloneStats RawNtfsApplySecurity(char sourceLetter, string targetRoot)
	{
		var stats = new RawCloneStats();
		using var vr = OpenVolume(sourceLetter);
		byte[] boot = vr.Read(0, 512);
		if (boot.Length < 0x50 || boot[3] != (byte)'N' || boot[4] != (byte)'T' || boot[5] != (byte)'F' || boot[6] != (byte)'S') return stats;
		int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
		int sectorsPerCluster = boot[0x0D];
		if (bytesPerSector == 0 || sectorsPerCluster == 0) return stats;
		int clusterSize = bytesPerSector * sectorsPerCluster;
		long mftStartCluster = BitConverter.ToInt64(boot, 0x30);
		sbyte cpr = unchecked((sbyte)boot[0x40]);
		int recSize = cpr > 0 ? cpr * clusterSize : 1 << (-cpr);
		if (recSize <= 0 || recSize > (1 << 20)) recSize = 1024;
		long mftByteOffset = mftStartCluster * (long)clusterSize;
		var mftRuns = ReadMftRuns(vr, mftByteOffset, recSize, bytesPerSector);
		if (mftRuns.Count == 0) mftRuns.Add((mftStartCluster, 64));
		EnsureMftRunsComplete(vr, mftByteOffset, recSize, bytesPerSector, clusterSize, mftRuns);
		string root = targetRoot.TrimEnd('\\') + "\\";

		var sds = LoadSecurityDescriptors(vr, mftRuns, recSize, clusterSize, bytesPerSector);
		stats.SdsLoaded = sds.Count;
		if (sds.Count == 0) return stats;

		// Pass 1: dir names (for path resolution) + each dir's security-id.
		var dirNames = new Dictionary<long, (string name, long parent)>();
		var dirSecId = new Dictionary<long, uint>();
		WalkMftRecords(vr, mftRuns, recSize, clusterSize, bytesPerSector, (recNo, buf, off, flags, baseRef, torn) =>
		{
			if ((flags & 0x01) == 0 || baseRef != 0 || (flags & 0x02) == 0) return;   // torn tolerated for a directory (name/parent kept)
			var (dn, dp) = ReadNameAndParent(buf, off, recSize);
			if (string.IsNullOrEmpty(dn)) return;
			dirNames[recNo] = (dn, dp);
			// A directory junction/mount-point (FILE_ATTRIBUTE_REPARSE_POINT) must NOT be ACL'd by path: SetFileSecurityW
			// follows the reparse and would rewrite the junction TARGET's descriptor (e.g. \Users\All Users -> the live
			// host's C:\ProgramData). Giving it no security-id makes Pass 3 skip it, mirroring the file pass.
			if ((ReadDosAttributes(buf, off, recSize) & 0x400) == 0)
				dirSecId[recNo] = ReadSecurityId(buf, off, recSize);
		});

		// Pass 2: FILE ACLs — applied to the first kept name (hardlinks share one descriptor). Same name-filtering
		// as the copy pass, so exactly the objects that were written get their source ACL.
		Log("Raw NTFS engine: applying file security descriptors...");
		WalkMftRecords(vr, mftRuns, recSize, clusterSize, bytesPerSector, (recNo, buf, off, flags, baseRef, torn) =>
		{
			if (stopRequested) return;
			if ((flags & 0x01) == 0 || baseRef != 0 || torn) return;   // a torn file record wasn't ACL-relevant / wasn't written
			if (recNo < 16 || (flags & 0x02) != 0) return;
			RawNode node;
			try { node = BuildNode(recNo, buf, off, false, vr, mftRuns, recSize, clusterSize, bytesPerSector); }
			catch { return; }
			if (node.ReparseTag != 0 && node.ReparseTag != ReparseTagWof) return;   // genuine reparse points weren't written; WOF/CompactOS files WERE materialised as plain files, so ACL them too
			if (node.SecurityId == 0 || !sds.TryGetValue(node.SecurityId, out var sd)) return;
			var names = node.Names.Count > 0 ? node.Names : new List<(string Name, long ParentRef)> { (node.PrimaryName, node.ParentRef) };
			foreach (var (nm, par) in names)
			{
				if (string.IsNullOrEmpty(nm)) continue;
				string d = ResolvePath(par, dirNames);
				string rel = "\\" + d + nm;
				if (rel.StartsWith("\\$Extend\\", StringComparison.OrdinalIgnoreCase)) continue;
				if (IsNtfsCloneExcluded(rel, false)) continue;
				string cand = Ext(root + d + nm);
				if (!File.Exists(cand)) continue;                   // the copy pass wrote a LATER name (first name's write failed) — apply to the one that exists
				ApplySecurity(cand, sd, stats);
				break;                                              // one existing kept name is enough — the descriptor is shared
			}
		});

		// Pass 3: DIRECTORY ACLs. Descriptors are applied PROTECTED, so order is irrelevant.
		Log("Raw NTFS engine: applying directory security descriptors...");
		foreach (var kv in dirNames)
		{
			if (stopRequested) break;
			if (kv.Key < 16 || kv.Key == 5) continue;
			if (!dirSecId.TryGetValue(kv.Key, out uint sid) || sid == 0 || !sds.TryGetValue(sid, out var dsd)) continue;
			string relDir = ResolvePath(kv.Value.parent, dirNames) + kv.Value.name;
			string full = "\\" + relDir;
			if (full.StartsWith("\\$Extend", StringComparison.OrdinalIgnoreCase)) continue;
			if (IsNtfsCloneExcluded(full, true)) continue;
			ApplySecurity(Ext(root + relDir), dsd, stats);
		}

		try
		{
			string diag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "raw-engine-diag.txt");
			File.AppendAllText(diag, "\nSecurity pass (after post-processing)\n" +
				$"SDS descriptors loaded : {stats.SdsLoaded}\n" +
				$"ACLs applied           : {stats.SecApplied}\n" +
				$"ACL failures           : {stats.SecErrors}\n" +
				$"First SetFileSecurity Win32 error : {stats.FirstSecError}\n");
		}
		catch { }
		return stats;
	}

	// Writes one file's main stream to `path` with backup semantics, then stamps timestamps + DOS attributes.
	// `sourceFull` is the file's path on the mounted snapshot (sourceLetter:\...), used only to read the
	// OS-decompressed bytes of an NTFS-compressed (LZNT1) file — whose on-disk clusters are compressed and
	// would be garbage if copied raw.
	private void WriteFileWithMeta(VolumeReader vr, RawNode node, RawStream? main, string path, string sourceFull,
		int clusterSize, byte[] zeroBuf, RawCloneStats stats)
	{
		// If the unnamed $DATA lived in an $ATTRIBUTE_LIST extension record we couldn't read (or a $DATA header was
		// malformed), `main` is null because we LOST the data, not because the file is empty — writing now would produce
		// a silent 0-byte clone counted as success. Fail loudly so the caller records it as an error, not a phantom file.
		if (main == null && node.ExtIncomplete) throw new IOException($"$DATA unreadable (dropped/malformed $ATTRIBUTE_LIST extension) — refusing a silent 0-byte clone of {path}");
		bool ok = false;
		try
		{
			using (SafeFileHandleWrite h = new(CreateFile(Ext(path), GenericWrite, 0x3u, IntPtr.Zero, CreateAlways, FileFlagBackupSemantics, IntPtr.Zero)))
			{
				if (h.Handle.IsInvalid) { int err = Marshal.GetLastWin32Error(); throw new IOException($"CreateFile failed ({err}) for {path}", unchecked((int)(0x80070000u | (uint)err))); }
				// Mark a sparse main stream sparse BEFORE writing so its hole runs stay unallocated — otherwise a huge
				// sparse file (e.g. a WSL2 ext4.vhdx: 256 GB logical, ~20 GB real) would be materialised at full size.
				bool sparse = main != null && !main.Resident && main.Sparse && !main.Compressed && node.ReparseTag == 0;
				if (sparse) DeviceIoControl(h.Handle, FsctlSetSparse, null, 0, null, 0, out _, IntPtr.Zero);
				using (var fs = new FileStream(h.Handle, FileAccess.Write))
				{
					if (main != null)
					{
						if (main.Resident) { if (main.ResidentData != null) { fs.Write(main.ResidentData, 0, main.ResidentData.Length); stats.Bytes += main.ResidentData.Length; } }
						else if (main.Compressed || node.ReparseTag == ReparseTagWof)
						{
							// NTFS-native (LZNT1) compression OR WOF/CompactOS: the raw clusters are compressed / a sparse
							// placeholder, so copying them raw = corrupt/empty. Read the OS-decompressed bytes via the file
							// API on the snapshot (the FS/WOF filter decompresses transparently) and write a plain file.
							// Backup semantics so an ACL-restricted file still reads via SeBackupPrivilege.
							using var sh = new SafeFileHandleWrite(CreateFile(Ext(sourceFull), GenericRead, 0x7u, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero));
							if (sh.Handle.IsInvalid) { int err = Marshal.GetLastWin32Error(); throw new IOException($"Open compressed/WOF source failed ({err}) for {sourceFull}", unchecked((int)(0x80070000u | (uint)err))); }
							long cbefore = fs.Position;
							using (var src = new FileStream(sh.Handle, FileAccess.Read)) src.CopyTo(fs, 4 << 20);
							long ccopied = fs.Position - cbefore;
							stats.Bytes += ccopied; stats.CompressedViaApi++;
							if (ccopied < main.RealSize) stats.ReadShortfalls++;   // OS/WOF read returned fewer bytes than the recorded size — flag the (silent) truncation
						}
						else stats.Bytes += WriteNonResident(vr, main, fs, clusterSize, zeroBuf, stats, sparse);
					}
					fs.Flush();
					// Stamp creation + last-write from $STANDARD_INFORMATION while the handle is still open (write access).
					// AND (not OR): both come from the same $SI block, so stamp only when both are valid — avoids forcing a
					// zero component to 1601 (SetFileTime treats a non-null ref to 0 as "set to 1601", not "leave alone").
					if (node.CreatedUtc > 0 && node.ModifiedUtc > 0)
					{
						var cr = new FFileTime { Low = (uint)node.CreatedUtc, High = (uint)(node.CreatedUtc >> 32) };
						var wr = new FFileTime { Low = (uint)node.ModifiedUtc, High = (uint)(node.ModifiedUtc >> 32) };
						SetFileTime(h.Handle, ref cr, IntPtr.Zero, ref wr);
					}
				}
			}
			// DOS attributes LAST (setting READONLY earlier would block the data write / time stamp).
			uint attr = node.DosAttributes & DosAttrSettableMask;
			SetFileAttributesW(Ext(path), attr == 0 ? RawFileAttrNormal : attr);
			ok = true;
		}
		finally
		{
			// If any step above threw, the CreateAlways target already exists as a 0-byte/partial file — delete it so a
			// failed file is genuinely ABSENT (and reported via stats.Errors) rather than a silent truncated copy that
			// later passes File.Exists and gets an ACL applied.
			if (!ok) { try { File.Delete(Ext(path)); } catch { } }
		}
	}

	// Streams a non-resident stream's data runs to `fs`, honoring ValidDataLength (bytes past VDL are written
	// as zero, not copied from stale clusters). Uses the alignment-handling vr.Read so a non-cluster-multiple
	// tail is read correctly. Guards against silent corruption: refuses when the size fragment is missing,
	// refuses on a fragment gap (dropped $ATTRIBUTE_LIST extension → byte shift), and always writes exactly
	// RealSize bytes (zero-filling any run shortfall / bad-sector region). Returns bytes written.
	private long WriteNonResident(VolumeReader vr, RawStream main, FileStream fs, int clusterSize, byte[] zeroBuf, RawCloneStats stats, bool sparse)
	{
		// The VCN-0 fragment carries RealSize/VDL; if it was never resolved (unreadable extension record) we
		// don't know the size — writing now would silently truncate to 0 bytes. Fail loudly instead.
		if (!main.SawVcnZero) throw new IOException("Non-resident $DATA missing its VCN-0 size fragment (stitching gap) — refusing to write a truncated file.");
		long realSize = main.RealSize;
		// Honor an actual ValidDataLength of 0 (allocated-but-uninitialised file): everything past VDL reads
		// back as zero, so copy only [0,VDL) from disk and let the tail fill the rest. Clamp a corrupt VDL.
		long vdl = Math.Min(Math.Max(0L, main.ValidDataLength), realSize);
		long copyLeft = vdl, written = 0, expectedVcn = 0;
		foreach (var frag in main.Fragments.OrderBy(f => f.StartVcn))
		{
			if (copyLeft <= 0) break;
			// A gap means a $DATA fragment was dropped (unreadable extension record). Writing the later
			// clusters at the wrong file offset would corrupt every byte past the gap — fail instead.
			if (frag.StartVcn != expectedVcn) throw new IOException($"Data-run VCN gap (expected {expectedVcn}, got {frag.StartVcn}) — dropped $DATA fragment; refusing to write a shifted file.");
			foreach (var (lcn, count) in frag.Runs)
			{
				expectedVcn += count;                    // advance for EVERY run incl. sparse holes
				if (copyLeft <= 0) continue;
				long runBytes = count * (long)clusterSize;
				if (lcn < 0)   // sparse hole: on a sparse-marked target, SEEK past it (leave unallocated); else write zeros
				{
					long z = Math.Min(runBytes, copyLeft);
					if (sparse) fs.Seek(z, SeekOrigin.Current); else WriteZeros(fs, z, zeroBuf);
					copyLeft -= z; written += z; continue;
				}
				long baseOff = lcn * (long)clusterSize, pos = 0, take = Math.Min(runBytes, copyLeft);
				while (take > 0)
				{
					int chunk = (int)Math.Min(4L << 20, take);
					byte[] data = vr.Read(baseOff + pos, chunk, out int got);
					if (got > 0) { fs.Write(data, 0, got); pos += got; take -= got; copyLeft -= got; written += got; }
					if (got < chunk)   // bad/short source region — zero ONLY this chunk and KEEP copying later runs (they may be readable)
					{
						int missing = chunk - got;
						WriteZeros(fs, missing, zeroBuf);
						pos += missing; take -= missing; copyLeft -= missing; written += missing;
						stats.ReadShortfalls++;
					}
				}
			}
		}
		// Run list covered fewer bytes than VDL (truncated DecodeRuns) — fill so length == RealSize, and flag it.
		if (copyLeft > 0) { if (sparse) fs.Seek(copyLeft, SeekOrigin.Current); else WriteZeros(fs, copyLeft, zeroBuf); written += copyLeft; copyLeft = 0; stats.RunShortfalls++; }
		if (realSize > vdl) { if (sparse) fs.Seek(realSize - vdl, SeekOrigin.Current); else WriteZeros(fs, realSize - vdl, zeroBuf); written += realSize - vdl; }   // uninitialised tail past VDL
		if (sparse) fs.SetLength(realSize);   // seeking past end does not extend the file — finalize the length so holes stay unallocated
		return written;
	}

	private static void WriteZeros(FileStream fs, long count, byte[] zeroBuf)
	{
		while (count > 0) { int w = (int)Math.Min(zeroBuf.Length, count); fs.Write(zeroBuf, 0, w); count -= w; }
	}

	// ---------- EFS-encrypted file copy via the backup/restore raw API (no decryption key needed) ----------
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int EfsExportCallback(IntPtr pbData, IntPtr pvCallbackContext, uint ulLength);
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int EfsImportCallback(IntPtr pbData, IntPtr pvCallbackContext, ref uint ulLength);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int OpenEncryptedFileRawW(string lpFileName, uint ulFlags, out IntPtr pvContext);
	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern int ReadEncryptedFileRaw(EfsExportCallback pfExportCallback, IntPtr pvCallbackContext, IntPtr pvContext);
	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern int WriteEncryptedFileRaw(EfsImportCallback pfImportCallback, IntPtr pvCallbackContext, IntPtr pvContext);
	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern void CloseEncryptedFileRaw(IntPtr pvContext);

	// Reads the encrypted raw stream (ciphertext + $EFS metadata) from the source into a temp file, then writes it to
	// the target with CREATE_FOR_IMPORT — reconstructing the encrypted file WITHOUT the key. Streamed through a temp
	// file so a large encrypted file need not fit in memory. Returns false on any failure so the caller falls back to
	// counting the file skipped. Needs SeBackupPrivilege (read) + SeRestorePrivilege (write), both enabled by the clone.
	private bool CopyEfsRaw(string sourceFull, string targetFull, out bool diskFull)
	{
		diskFull = false;
		string tmp = Path.Combine(Path.GetTempPath(), "df-efs-" + Guid.NewGuid().ToString("N") + ".bin");
		IntPtr srcCtx = IntPtr.Zero, dstCtx = IntPtr.Zero;
		bool ok = false, importStarted = false;
		try
		{
			if (OpenEncryptedFileRawW(sourceFull, 0u, out srcCtx) != 0 || srcCtx == IntPtr.Zero) return false;
			IOException? writeEx = null;
			using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
			{
				byte[] rbuf = new byte[1 << 20];
				EfsExportCallback exp = (pbData, ctx, len) =>
				{
					// NEVER let a managed exception unwind through the native caller — capture it and abort the read.
					try
					{
						if (len > 0)
						{
							if (len > (uint)rbuf.Length) rbuf = new byte[len];
							Marshal.Copy(pbData, rbuf, 0, (int)len);
							outFs.Write(rbuf, 0, (int)len);   // may throw if the %TEMP% volume fills
						}
						return 0;   // ERROR_SUCCESS
					}
					catch (IOException ioe) { writeEx = ioe; return 1; }
				};
				int r = ReadEncryptedFileRaw(exp, IntPtr.Zero, srcCtx);
				GC.KeepAlive(exp);
				if (writeEx != null) { if (IsDiskFull(writeEx)) diskFull = true; return false; }
				if (r != 0) return false;
			}
			CloseEncryptedFileRaw(srcCtx); srcCtx = IntPtr.Zero;

			importStarted = true;   // from here CREATE_FOR_IMPORT may have created targetFull — clean it up on failure
			int openR = OpenEncryptedFileRawW(targetFull, 0x1u /*CREATE_FOR_IMPORT*/, out dstCtx);
			if (openR != 0 || dstCtx == IntPtr.Zero) { if (openR == 112 || openR == 39) diskFull = true; return false; }
			using (var inFs = new FileStream(tmp, FileMode.Open, FileAccess.Read))
			{
				byte[] wbuf = new byte[1 << 20];
				EfsImportCallback imp = (IntPtr pbData, IntPtr ctx, ref uint len) =>
				{
					if (len > (uint)wbuf.Length) wbuf = new byte[len];
					int read = inFs.Read(wbuf, 0, (int)len);
					if (read > 0) Marshal.Copy(wbuf, 0, pbData, read);
					len = (uint)read;   // 0 => end of data
					return 0;
				};
				int r = WriteEncryptedFileRaw(imp, IntPtr.Zero, dstCtx);
				GC.KeepAlive(imp);
				// ERROR_DISK_FULL (112) / ERROR_HANDLE_DISK_FULL (39): the TARGET volume filled during the import.
				if (r != 0) { if (r == 112 || r == 39) diskFull = true; return false; }
			}
			ok = true;
			return true;
		}
		catch (Exception ex) { if (IsDiskFull(ex)) diskFull = true; return false; }
		finally
		{
			if (srcCtx != IntPtr.Zero) CloseEncryptedFileRaw(srcCtx);
			if (dstCtx != IntPtr.Zero) CloseEncryptedFileRaw(dstCtx);
			try { File.Delete(tmp); } catch { }
			// A failed import leaves a 0-byte / partial encrypted stub at targetFull (CREATE_FOR_IMPORT already created
			// it). Delete it so a failure leaves the file cleanly ABSENT rather than as an undecryptable corrupt remnant.
			if (!ok && importStarted) { try { File.Delete(Ext(targetFull)); } catch { } }
		}
	}

	// After an EFS raw copy, stamp creation/last-write times and the settable DOS attributes (FILE_ATTRIBUTE_ENCRYPTED
	// is already set by the raw API and is not in DosAttrSettableMask). Best-effort.
	private void ApplyEfsFileTimesAndAttrs(RawNode node, string path)
	{
		try
		{
			using var h = new SafeFileHandleWrite(CreateFile(Ext(path), 0x100u /*FILE_WRITE_ATTRIBUTES*/, 0x7u, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero));
			if (!h.Handle.IsInvalid && node.CreatedUtc > 0 && node.ModifiedUtc > 0)
			{
				var cr = new FFileTime { Low = (uint)node.CreatedUtc, High = (uint)(node.CreatedUtc >> 32) };
				var wr = new FFileTime { Low = (uint)node.ModifiedUtc, High = (uint)(node.ModifiedUtc >> 32) };
				SetFileTime(h.Handle, ref cr, IntPtr.Zero, ref wr);
			}
		}
		catch { }
		try { uint attr = node.DosAttributes & DosAttrSettableMask; SetFileAttributesW(Ext(path), attr == 0 ? RawFileAttrNormal : attr); } catch { }
	}

	// Small owner so the raw SafeFileHandle from CreateFile is disposed even before it is wrapped in a FileStream.
	private readonly struct SafeFileHandleWrite : IDisposable
	{
		public readonly Microsoft.Win32.SafeHandles.SafeFileHandle Handle;
		public SafeFileHandleWrite(Microsoft.Win32.SafeHandles.SafeFileHandle h) { Handle = h; }
		public void Dispose() { if (!Handle.IsClosed) Handle.Dispose(); }
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);
}
