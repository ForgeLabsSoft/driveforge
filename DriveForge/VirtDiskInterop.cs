using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DriveForge;

// Native VHD/VHDX attach/detach via the Windows Virtual Disk API (virtdisk.dll) plus the mount-manager
// volume plumbing (kernel32). Replaces the powershell.exe Mount-DiskImage / Dismount-DiskImage round-trips:
// no child process, no output parsing, deterministic drive-letter control — and because the attach is opened
// WITHOUT a permanent lifetime, the OS detaches the image automatically if the process dies while the handle
// is open, so a crash can never leak a mount that keeps the image file locked.
internal static class VirtDisk
{
	private const int ERROR_SUCCESS = 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct VIRTUAL_STORAGE_TYPE
	{
		public uint DeviceId;   // 0 = UNKNOWN: let Windows detect .vhd vs .vhdx from the file
		public Guid VendorId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct OPEN_VIRTUAL_DISK_PARAMETERS
	{
		public int Version;     // OPEN_VIRTUAL_DISK_VERSION_1
		public uint RWDepth;    // OPEN_VIRTUAL_DISK_RW_DEPTH_DEFAULT
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ATTACH_VIRTUAL_DISK_PARAMETERS
	{
		public int Version;     // ATTACH_VIRTUAL_DISK_VERSION_1
		public uint Reserved;
	}

	private static readonly Guid VendorMicrosoft = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B");

	private const uint VIRTUAL_DISK_ACCESS_ATTACH_RO = 0x00010000;
	private const uint VIRTUAL_DISK_ACCESS_DETACH = 0x00040000;
	private const uint VIRTUAL_DISK_ACCESS_GET_INFO = 0x00080000;

	private const uint ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 0x00000001;
	private const uint ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 0x00000002;

	[DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
	private static extern int OpenVirtualDisk(ref VIRTUAL_STORAGE_TYPE virtualStorageType, string path, uint virtualDiskAccessMask, uint flags, ref OPEN_VIRTUAL_DISK_PARAMETERS parameters, out SafeFileHandle handle);

	[DllImport("virtdisk.dll")]
	private static extern int AttachVirtualDisk(SafeFileHandle virtualDiskHandle, IntPtr securityDescriptor, uint flags, uint providerSpecificFlags, ref ATTACH_VIRTUAL_DISK_PARAMETERS parameters, IntPtr overlapped);

	[DllImport("virtdisk.dll")]
	private static extern int DetachVirtualDisk(SafeFileHandle virtualDiskHandle, uint flags, uint providerSpecificFlags);

	[DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
	private static extern int GetVirtualDiskPhysicalPath(SafeFileHandle virtualDiskHandle, ref uint diskPathSizeInBytes, StringBuilder diskPath);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr FindFirstVolume(StringBuilder volumeName, uint bufferLength);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool FindNextVolume(IntPtr findHandle, StringBuilder volumeName, uint bufferLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FindVolumeClose(IntPtr findHandle);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, byte[] outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetVolumeMountPoint(string mountPoint, string volumeName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool DeleteVolumeMountPoint(string mountPoint);

	private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
	private const uint OPEN_EXISTING = 3;
	private const uint FILE_SHARE_READ_WRITE = 0x00000003;
	private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

	private static VIRTUAL_STORAGE_TYPE AnyMicrosoft() => new VIRTUAL_STORAGE_TYPE { DeviceId = 0, VendorId = VendorMicrosoft };

	// Attaches the image READ-ONLY with automount suppressed (NO_DRIVE_LETTER) and returns the open handle +
	// the physical disk number the image surfaced as. The attach lives exactly as long as the handle.
	public static SafeFileHandle AttachReadOnly(string imagePath, out int diskNumber)
	{
		VIRTUAL_STORAGE_TYPE type = AnyMicrosoft();
		var open = new OPEN_VIRTUAL_DISK_PARAMETERS { Version = 1, RWDepth = 1 };
		// DETACH is included so a caller CAN detach through this same handle (DetachVirtualDisk needs the right on
		// the handle it is given); without it an explicit DetachVirtualDisk returns ERROR_ACCESS_DENIED.
		int rc = OpenVirtualDisk(ref type, imagePath, VIRTUAL_DISK_ACCESS_ATTACH_RO | VIRTUAL_DISK_ACCESS_GET_INFO | VIRTUAL_DISK_ACCESS_DETACH, 0, ref open, out SafeFileHandle handle);
		if (rc != ERROR_SUCCESS)
			throw new Win32Exception(rc, "OpenVirtualDisk failed (" + rc + ") for " + imagePath);
		try
		{
			var attach = new ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };
			rc = AttachVirtualDisk(handle, IntPtr.Zero, ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY | ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER, 0, ref attach, IntPtr.Zero);
			if (rc != ERROR_SUCCESS)
				throw new Win32Exception(rc, "AttachVirtualDisk failed (" + rc + ") for " + imagePath);
			var sb = new StringBuilder(512);
			uint size = (uint)sb.Capacity * 2;
			rc = GetVirtualDiskPhysicalPath(handle, ref size, sb);
			if (rc != ERROR_SUCCESS)
				throw new Win32Exception(rc, "GetVirtualDiskPhysicalPath failed (" + rc + ")");
			string physical = sb.ToString(); // \\.\PhysicalDriveN
			int n = 0; bool any = false;
			foreach (char c in physical)
			{
				if (c >= '0' && c <= '9') { n = n * 10 + (c - '0'); any = true; }
				else if (any) break;
			}
			if (!any)
				throw new InvalidOperationException("Unexpected physical path for attached image: " + physical);
			diskNumber = n;
			return handle;
		}
		catch
		{
			handle.Dispose(); // closing the handle detaches — a failed attach never leaks a mount
			throw;
		}
	}

	// Explicit detach for a handle we attached; best-effort (closing the handle also detaches).
	public static void Detach(SafeFileHandle handle)
	{
		try { DetachVirtualDisk(handle, 0, 0); } catch { }
	}

	// Detaches an image attached by ANYONE (Explorer double-click, diskpart, a crashed run). Returns false
	// if the image is not attached or cannot be opened — callers treat this as best-effort cleanup.
	public static bool TryDetachByPath(string imagePath)
	{
		try
		{
			VIRTUAL_STORAGE_TYPE type = AnyMicrosoft();
			var open = new OPEN_VIRTUAL_DISK_PARAMETERS { Version = 1, RWDepth = 1 };
			int rc = OpenVirtualDisk(ref type, imagePath, VIRTUAL_DISK_ACCESS_DETACH, 0, ref open, out SafeFileHandle handle);
			if (rc != ERROR_SUCCESS) return false;
			using (handle)
				return DetachVirtualDisk(handle, 0, 0) == ERROR_SUCCESS;
		}
		catch { return false; }
	}

	// Locates the WINDOWS volume (>= minBytes) that lives on the given physical disk: it prefers the largest
	// volume that actually contains \Windows\System32\config\SYSTEM (so a multi-partition image whose data
	// partition is larger than the Windows partition still picks Windows), and only falls back to the largest
	// qualifying volume when none looks like Windows — in which case the caller's own hive check produces a
	// precise message. Returns the volume GUID path ("\\?\Volume{...}\") or null if no volume qualifies yet.
	// hasWindows tells the caller whether the returned volume is a confirmed Windows install.
	public static string? FindWindowsVolumeOnDisk(int diskNumber, long minBytes, out bool hasWindows)
	{
		string? bestWindows = null; long bestWindowsLen = -1;
		string? bestAny = null; long bestAnyLen = -1;
		var name = new StringBuilder(512);
		IntPtr find = FindFirstVolume(name, (uint)name.Capacity);
		if (find == IntPtr.Zero || find == INVALID_HANDLE_VALUE) { hasWindows = false; return null; }
		try
		{
			do
			{
				string vol = name.ToString();
				long length = ExtentLengthOnDisk(vol, diskNumber);
				if (length < minBytes) continue;
				if (length > bestAnyLen) { bestAnyLen = length; bestAny = vol; }
				// The volume is mounted (only its drive LETTER was suppressed), so the \\?\Volume{guid}\ path
				// is readable directly — probe it for a Windows installation without assigning a letter first.
				bool win = false;
				try { win = File.Exists(vol + "Windows\\System32\\config\\SYSTEM"); } catch { }
				if (win && length > bestWindowsLen) { bestWindowsLen = length; bestWindows = vol; }
			}
			while (FindNextVolume(find, name, (uint)name.Capacity));
		}
		finally { FindVolumeClose(find); }
		if (bestWindows != null) { hasWindows = true; return bestWindows; }
		hasWindows = false; return bestAny;
	}

	// Total bytes this volume occupies on the given disk (0 if it lives elsewhere or cannot be queried).
	private static long ExtentLengthOnDisk(string volumeGuidPath, int diskNumber)
	{
		// CreateFile wants the volume path WITHOUT the trailing backslash.
		string devicePath = volumeGuidPath.TrimEnd('\\');
		using SafeFileHandle volume = CreateFile(devicePath, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (volume.IsInvalid) return 0;
		byte[] buffer = new byte[8 + 24 * 16]; // VOLUME_DISK_EXTENTS: count + up to 16 extents
		if (!DeviceIoControl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out _, IntPtr.Zero))
			return 0;
		int count = BitConverter.ToInt32(buffer, 0);
		long total = 0;
		for (int i = 0; i < count && i < 16; i++)
		{
			int offset = 8 + i * 24; // DISK_EXTENT: DiskNumber(4) + pad(4) + StartingOffset(8) + ExtentLength(8)
			if (BitConverter.ToInt32(buffer, offset) == diskNumber)
				total += BitConverter.ToInt64(buffer, offset + 16);
		}
		return total;
	}

	public static void AssignDriveLetter(char letter, string volumeGuidPath)
	{
		if (!SetVolumeMountPoint(letter + ":\\", volumeGuidPath))
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not assign " + letter + ": to the image's Windows volume");
	}

	public static void RemoveDriveLetter(char letter)
	{
		try { DeleteVolumeMountPoint(letter + ":\\"); } catch { }
	}
}
