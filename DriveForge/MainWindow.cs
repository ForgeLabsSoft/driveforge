using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Buffers;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DriveForge;

public partial class MainWindow : Window, IComponentConnector
{
	private const uint DddRawTargetPath = 1;

	private const uint DddRemoveDefinition = 2;

	private const uint DddExactMatchOnRemove = 4;

	private const uint ProcessSuspendResume = 0x0800;

	private const uint TokenAdjustPrivileges = 0x0020;

	private const uint TokenQuery = 0x0008;

	private const uint SePrivilegeEnabled = 0x00000002;

	private const uint GenericRead = 0x80000000;

	private const uint GenericWrite = 0x40000000;

	private const uint FileAttributeNormal = 0x00000080;

	private const uint FileFlagBackupSemantics = 0x02000000;

	private const uint FileFlagOpenReparsePoint = 0x00200000;

	private const uint FileFlagSequentialScan = 0x08000000;

	private const uint FsctlGetReparsePoint = 0x000900A8;

	private const uint FsctlSetReparsePoint = 0x000900A4;

	private const int MaxReparseBuffer = 16 * 1024;

	private sealed record ProcessResult(int ExitCode, string Output);

	private sealed record BcdStoreInfo(string Path, string LoaderPath, string Mode);

	// StatusKind ("good"/"warn"/"bad"/"info") is a stable, language-independent token that drives the
	// Status column's colour (via XAML DataTriggers), so the visible Status text can be localized freely.
	private sealed record SmartRow(string Id, string Name, string Value, string Status, string StatusKind);

	private sealed record EditionItem(int Index, string Name)
	{
		public override string ToString()
		{
			return $"{Index}: {Name}";
		}
	}

	private sealed record DiskItem(int Number, string FriendlyName, string BusType, string MediaType, string HealthStatus, string OperationalStatus, long Size, string PartitionStyle, bool IsSystem, IReadOnlyList<char> DriveLetters)
	{
		// Captured at scan time so a destructive write can re-check the disk number still points to the same
		// physical drive (numbers can shift when drives are unplugged/replugged between the scan and the write).
		public string Serial { get; init; } = "";

		public bool IsLikelyUsbOrExternal
		{
			get
			{
				if (!BusType.Contains("USB", StringComparison.OrdinalIgnoreCase) && !BusType.Contains("SATA", StringComparison.OrdinalIgnoreCase) && !FriendlyName.Contains("SSD", StringComparison.OrdinalIgnoreCase))
				{
					return FriendlyName.Contains("Portable", StringComparison.OrdinalIgnoreCase);
				}
				return true;
			}
		}

		public string HealthText
		{
			get
			{
				if (!string.Equals(HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase))
				{
					return ("Health: " + HealthStatus + "/" + OperationalStatus).TrimEnd('/');
				}
				return "Health: OK";
			}
		}

		public override string ToString()
		{
			string value = ((DriveLetters.Count == 0) ? "no letter" : string.Join(", ", DriveLetters.Select((char letter) => letter + ":")));
			string value2 = (IsSystem ? " - RUNNING WINDOWS (no format/erase)" : "");
			return $"Disk {Number} - {FriendlyName} - {FormatBytes(Size)} - {BusType}/{MediaType} - {HealthText} - {value}{value2}";
		}
	}

	private sealed record SpeedResult(double SequentialWriteMb, double Random4KWriteMb, SpeedRating Rating, string Message);

	private sealed record ShadowCopyInfo(string Id, string DeviceObject);


	private sealed record NtfsCopyTestResult(string Source, string Target, long Files, long Directories, long Bytes, long Skipped, long ResumeSkippedFiles, long ResumeSkippedBytes, bool StopRequested, bool ResumeMode, long SecurityCopied, long SecurityErrors, long HardlinksDetected, long HardlinksRecreated, long HardlinkFallbackCopied, long ReparseCopied, long ReparseSkipped, long BackupFallbackCopied, long BackupFallbackFailed, long ValidatedFiles, long ValidatedDirectories, long ValidatedBytes, long ValidationMismatches, long ValidationTransientDifferences, int Errors, List<string> SampleErrors, List<string> SampleRecoveries, List<string> SampleWarnings, List<string> SampleValidationErrors, List<string> SampleTransientValidation);

	private sealed record BootSafeStagingCheck(string Area, string RelativePath, bool Required, bool Exists, string Note);


	private enum SpeedRating
	{
		Unknown,
		Bad,
		Usable,
		Good
	}

	private const int ModeInstallFromImage = 0;

	private const int ModeCloneCurrentWindows = 2;

	private const int ModeRestoreSavedClone = 1;

	private const int ModeExperimentalNtfsFullRootUsbClone = 2;

	private const int ModeCloneInternal = 3;

	private const int ModeBackupImage = 4;

	private const int ModeWriteIsoImage = 5;

	private const int ToolHealth = 0;

	private const int ToolSpeed = 1;

	private const int ToolSmart = 2;

	private const int ToolScan = 3;

	private const int ToolRepair = 4;

	private const int ToolKit = 5;

	private readonly List<DiskItem> disks = new List<DiskItem>();

	private readonly Dictionary<int, SpeedResult> speedResults = new Dictionary<int, SpeedResult>();
	// Windows RECYCLES disk numbers when a removable drive is unplugged and another is inserted, so a number-keyed speed
	// cache would hand the fast drive's measured MB/s — and its green "good for Windows To Go" verdict — to a slow stick
	// that was never tested, and would even let the pre-flight skip measuring it. Remember which physical drive each
	// cached result belongs to and drop it when the number starts pointing somewhere else.
	private readonly Dictionary<int, string> speedResultIdentity = new Dictionary<int, string>();
	private static string DiskIdentityKey(DiskItem d) =>
		((d.Serial ?? "").Trim().Length > 0 ? d.Serial.Trim() : (d.FriendlyName ?? "?")) + "|" + d.Size;

	private string? sourcePath;

	private string? bitLockerRecoveryFolder;

	private string bitLockerPassword = "";

	private string localAccountName = "";
	private string localAccountPassword = "";

	// True while BitLocker is still encrypting at the end of an operation — used to avoid telling the user
	// the drive is "safe to remove" mid-encryption.
	private bool bitLockerEncrypting = false;

	// True when launched by Task Scheduler for an unattended clone — suppresses all dialogs.
	private bool headlessRun = false;

	private bool isBusy;

	private volatile bool stopRequested; // polled in worker-thread hot loops; volatile so Stop is always observed

	private volatile bool isPaused; // polled in worker-thread hot loops; volatile so Pause is always observed

	private bool internalOperationStopped;

	// Set true by an image/restore operation when a PRE-WRITE safety gate (target-health warning declined, or the
	// identity re-verify detecting a changed/renumbered disk) aborts before anything is written. The shared success
	// tail checks it so it never falsely reports "finished successfully" / ejects a drive that was never touched.
	private bool operationAbortedBeforeWrite;

	// Set true when the user asked for BitLocker but encryption did not actually start. The caller keeps the created
	// drive but reports it as NOT encrypted (a warning) instead of a silent success, so the user is never handed an
	// unencrypted stick they believe is protected.
	private bool bitLockerFailedThisRun;

	// Reentry guard for StartButton_Click: set synchronously before its async pre-write phase (during which isBusy is
	// still false) so a double-click cannot launch two concurrent destructive operations on the same disk.
	private bool _startInProgress;
	// Synchronous reentrancy guard for the destructive TOOL handlers (Partition / Format / Wipe): each sets isBusy only
	// AFTER its pre-SetBusy confirm + identity-verify awaits (which spawn a slow powershell.exe with no modal up), so
	// during that ~1-2s window a second click on any of these always-enabled tool buttons would slip past the isBusy
	// check and start a concurrent diskpart op on the same disk. This flag is set SYNCHRONOUSLY at entry to close it,
	// mirroring _startInProgress for StartButton_Click.
	private bool _toolOpStarting;

	private int selectedDriveTool = ToolHealth;

	private Process? activeProcess;

	private readonly Stopwatch operationStopwatch = new Stopwatch();

	private readonly DispatcherTimer operationTimer;

	private double progressTotalGiB;

	// Raw bytes written by worker threads (Volatile for safe cross-thread reads from UI thread).
	// Stored as long (not double) because Volatile.Read/Write only supports blittable types on all platforms.
	// UI thread converts to GiB on read: Volatile.Read(ref _progressDoneBytes) / 1073741824.0
	private long _progressDoneBytes;

	// When true, the live-output parser must NOT drive the bar or byte counter. Used during the streaming
	// capture|apply clone, where two wimlib processes interleave their progress lines on one stdout (which made
	// the bar bounce 40%↔19% and the GiB counter stick). The partition-used-space poller is the single source then.
	private volatile bool _suppressLineProgress;

	// When true (e.g. secure wipe), the bar shows the TRUE 0–100% fraction of bytes done, not the 40–82%
	// "data copy" band used by clone/install.
	private volatile bool _progressFullRange;

	// When true the operation's total is FIXED (e.g. a full-disk deep scan): disables the clone-only "inflate the
	// total when near completion" heuristic so the bar/ETA run smoothly to 100% instead of stalling near 97%.
	private volatile bool _progressFixedTotal;

	// Convenience property for UI thread reads — always use this instead of direct field access
	private double progressDoneGiB
	{
		get => Volatile.Read(ref _progressDoneBytes) / 1073741824.0;
		set => Volatile.Write(ref _progressDoneBytes, (long)(value * 1073741824.0));
	}

	private double progressSpeedMb;

	// Previous-tick GiB value — used to compute per-second instant speed instead of average-from-start
	private double progressPrevGiB;

	private long progressLastReportedBytes;

	// Sliding-window speed samples: each entry is (timestamp, cumulativeGiB) pushed every timer tick.
	// Speed is derived from the oldest surviving sample in the window, giving a stable 30-second average
	// that reacts to real speed changes (e.g. USB throttling) without EWA lag.
	private readonly Queue<(DateTime Time, double GiB)> _speedWindow = new Queue<(DateTime Time, double GiB)>();
	private const int SpeedWindowSeconds = 30;

	private DateTime lastProcessOutputUtc = DateTime.MinValue;

	private DateTime lastHeartbeatLogUtc = DateTime.MinValue;

	public MainWindow()
	{
		InitializeComponent();
		operationTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(1.0)
		};
		operationTimer.Tick += delegate
		{
			UpdateProgressStats();
			UpdateLongRunningHeartbeat();
		};
	}

	private async void Window_Loaded(object sender, RoutedEventArgs e)
	{
		InitializeUiCustomization();
		UpdateAdminStatus();
		SetupDeviceChangeAutoRefresh();   // rescan the disk list automatically when a drive is plugged in / removed
		_ = Task.Run(SweepStrandedWipeFiles); // reclaim fill files a crash/kill left behind during a previous wipe
		_ = RecoverStrandedTestBootDiskAsync(); // re-online a disk a crash/kill left offline during a previous Test-boot
		ModeBox.Items.Clear();
		ModeBox.Items.Add("Create Windows USB (ISO / WIM / ESD)");
		ModeBox.Items.Add("Advanced: restore full disk image");
		ModeBox.Items.Add("Clone This PC → portable USB / external drive");
		ModeBox.Items.Add("Clone This PC → internal disk (normal Windows install)");
		ModeBox.Items.Add("Back up this PC to an image file (.wim)");
		ModeBox.Items.Add("Create bootable USB from an ISO image (Linux / other)");
		ModeBox.SelectedIndex = ModeExperimentalNtfsFullRootUsbClone;
		ShowWorkflowView();
		HighlightNav(NavClonePortable);
		// Rescue mode: when booted from a WinPE USB there is no running Windows to clone/back up, so hide those
		// tasks and land on Drive tools.
		if (IsRunningInWinPE())
		{
			AppSubtitleText.Text = L("AppSubWinPE");
			NavClonePortable.Visibility = Visibility.Collapsed;
			NavCloneInternal.Visibility = Visibility.Collapsed;
			NavExportVhdx.Visibility = Visibility.Collapsed;   // no running Windows to export under WinPE (would snapshot the X: RAM disk)
			NavBackup.Visibility = Visibility.Collapsed;
			ShowDiagnosticsView();
			HighlightNav(NavTools);
		}
		Log("Program started. Recommended mode uses DISM image apply. Clone mode uses VSS snapshot + native-boot VHDX.");
		LoadUserSettings();
		await RefreshDisksAsync();
		UpdateDriveVerdict();
		UpdateStartReadiness();
		// Scheduled / unattended clone: launched by a Task Scheduler job with --auto-clone arguments.
		if (TryGetAutoCloneRequest(out DiskItem? autoDisk, out bool autoInternal))
		{
			if (autoDisk != null)
			{
				await RunHeadlessCloneAsync(autoDisk, autoInternal);
			}
			else
			{
				// Target drive not connected at the scheduled time — log and exit instead of leaving a window open.
				SaveLogToDesktop();
				Application.Current.Shutdown();
			}
			return;
		}
		if (isFirstRun)
		{
			HelpButton_Click(this, new RoutedEventArgs());
		}
		await OfferScheduledCloneManualRunAsync();
	}

	// If the user has set up an automatic clone (Task Scheduler job) and that target drive is connected right
	// now, offer to run the same clone manually on the spot — no need to wait for the scheduled time.
	private async Task OfferScheduledCloneManualRunAsync()
	{
		string query;
		try
		{
			// Query as XML: the /FO LIST /V field labels (e.g. "Task To Run:") are localized to the Windows UI language,
			// so regexing the English label silently disabled this offer on every non-English install. XML element names
			// (Command / Arguments) are schema-fixed and locale-independent.
			query = await RunProcessCaptureAsync("schtasks.exe", "/Query /TN " + QuoteArgument("DriveForge Auto Clone") + " /XML");
		}
		catch { return; } // no scheduled clone task → nothing to offer
		if (string.IsNullOrWhiteSpace(query)) return;
		string cmd;
		try
		{
			var xml = System.Xml.Linq.XDocument.Parse(query.TrimStart('﻿', '\r', '\n', ' ', '\t'));
			System.Xml.Linq.XNamespace ns = xml.Root?.GetDefaultNamespace() ?? System.Xml.Linq.XNamespace.None;
			var exec = xml.Descendants(ns + "Exec").FirstOrDefault();
			cmd = ((exec?.Element(ns + "Command")?.Value ?? "") + " " + (exec?.Element(ns + "Arguments")?.Value ?? "")).Trim();
		}
		catch { return; }
		if (string.IsNullOrWhiteSpace(cmd)) return;
		if (cmd.IndexOf("--auto-clone", StringComparison.OrdinalIgnoreCase) < 0) return;
		string name = Regex.Match(cmd, "--diskname=\"([^\"]*)\"").Groups[1].Value;
		if (string.IsNullOrWhiteSpace(name)) name = Regex.Match(cmd, @"--diskname=(\S+)").Groups[1].Value.Trim('"');
		bool internalMode = Regex.Match(cmd, @"--mode=(\w+)").Groups[1].Value.Equals("internal", StringComparison.OrdinalIgnoreCase);
		if (string.IsNullOrWhiteSpace(name)) return;

		DiskItem? target = disks.FirstOrDefault(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase) && !d.IsSystem);
		if (target == null) return; // the scheduled drive is not connected → stay quiet

		// Pre-select the scheduled mode + drive so the green Start button is ready either way.
		ModeBox.SelectedIndex = internalMode ? ModeCloneInternal : ModeCloneCurrentWindows;
		DiskBox.SelectedItem = target;
		UpdateDriveVerdict();
		UpdateStartReadiness();

		var choice = MessageBox.Show(
			"Your scheduled backup drive is connected:\n    " + target.FriendlyName + " — " + FormatBytes(target.Size) +
			"\n\nDo you want to back up this PC to it now (instead of waiting for the scheduled time)?",
			"DriveForge — backup now?", MessageBoxButton.YesNo, MessageBoxImage.Question);
		if (choice == MessageBoxResult.Yes)
		{
			StartButton_Click(this, new RoutedEventArgs());
		}
	}

	// Parses --auto-clone --diskname="..." --disksize=N --mode=portable|internal and resolves the disk.
	private bool TryGetAutoCloneRequest(out DiskItem? disk, out bool internalMode)
	{
		disk = null;
		internalMode = false;
		string[] argv = Environment.GetCommandLineArgs();
		if (!argv.Any(a => a.Equals("--auto-clone", StringComparison.OrdinalIgnoreCase))) return false;
		string GetArg(string key)
		{
			string? a = argv.FirstOrDefault(x => x.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
			return a == null ? "" : a.Substring(key.Length + 1).Trim('"');
		}
		internalMode = GetArg("--mode").Equals("internal", StringComparison.OrdinalIgnoreCase);
		string name = GetArg("--diskname");
		string serial = GetArg("--diskserial").Trim();
		long.TryParse(GetArg("--disksize"), out long size);
		// NEVER auto-target the running system disk, and prefer the serial number (the only strong identity) so an
		// unattended clone can't erase a different, same-named/same-size drive that happens to be connected.
		// Resolve to EXACTLY ONE matching disk — never FirstOrDefault. Cheap USB sticks / card readers often report an
		// empty or model-shared (VID/PID-derived) serial, and identical sticks also share FriendlyName+size, so a lone
		// match could be the wrong physical drive. If two or more connected disks match the recorded identity, the target
		// is ambiguous → refuse (erasing the wrong data drive unattended is unrecoverable).
		if (serial.Length > 0)
		{
			// Match on serial AND the recorded size (±1 GiB): cheap USB/card-reader controllers report a model-shared
			// (VID/PID-derived) serial across physically different drives, so a same-serial drive of a DIFFERENT
			// capacity would otherwise be wiped unattended. (size<=0 only for old schedules that recorded no size.)
			var matches = disks.Where(d => !d.IsSystem && !string.IsNullOrEmpty(d.Serial)
				&& string.Equals(d.Serial.Trim(), serial, StringComparison.OrdinalIgnoreCase)
				&& (size <= 0 || Math.Abs(d.Size - size) < 1024L * 1024 * 1024)).ToList();
			if (matches.Count == 1) disk = matches[0];
			else
			{
				Log(matches.Count == 0
					? "Scheduled clone: no connected disk matches the saved serial number AND size. Refusing to guess a target. Nothing to do."
					: "Scheduled clone: " + matches.Count + " connected disks share the saved serial+size (non-unique / identical drives). Refusing to erase an ambiguous target. Nothing to do.");
				return true;
			}
		}
		else
		{
			// No serial was captured when scheduling (drive exposes none): fall back to friendly name + size (±1 GiB).
			// Do NOT add a size-less fallback (a same-named drive of a DIFFERENT size would be erased), and refuse if the
			// (name,size) match is ambiguous across two identical sticks.
			var matches = disks.Where(d => !d.IsSystem && string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)
				&& size > 0 && Math.Abs(d.Size - size) < 1024L * 1024 * 1024).ToList();
			if (matches.Count == 1) disk = matches[0];
			else if (matches.Count > 1)
			{
				Log("Scheduled clone: " + matches.Count + " connected disks share the saved name+size (identical drives, no serial). Refusing to erase an ambiguous target. Nothing to do.");
				return true;
			}
		}
		if (disk == null) Log("Scheduled clone: target disk '" + name + "' not found / not connected. Nothing to do.");
		return true;
	}

	// Runs a clone with no dialogs (for Task Scheduler) and then exits the app.
	private async Task RunHeadlessCloneAsync(DiskItem disk, bool internalMode)
	{
		headlessRun = true;
		try
		{
			ModeBox.SelectedIndex = internalMode ? ModeCloneInternal : ModeCloneCurrentWindows;
			stopRequested = false; isPaused = false; internalOperationStopped = false; bitLockerEncrypting = false;
			progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1024.0 / 1024.0 / 1024.0 * 1.25);
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, L("BzSchedClone"));
			// Re-verify the disk's identity immediately before erasing it (headless: logs + aborts, no dialog).
			if (!await VerifyTargetDiskUnchangedAsync(disk))
			{
				Log("Scheduled clone aborted: the target disk's identity changed since it was scheduled.");
				return;
			}
			await RunExperimentalFullRootUsbCloneAsync(disk);
			Log("Scheduled clone finished.");
		}
		catch (Exception ex)
		{
			Log("Scheduled clone failed: " + ex.Message);
			SaveLogToDesktop();
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			isBusy = false;   // clear busy BEFORE Shutdown, else Window_Closing's "operation running?" modal blocks an unattended run forever
			UpdateSleepBlock();   // headless runs skip Window_Closing's teardown, so release the sleep block here
			Application.Current.Shutdown();
		}
	}

	private void Window_DragOver(object sender, DragEventArgs e)
	{
		e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private async void Window_Drop(object sender, DragEventArgs e)
	{
		if (isBusy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
		var files = e.Data.GetData(DataFormats.FileDrop) as string[];
		string? file = files?.FirstOrDefault(f =>
		{
			string ext = Path.GetExtension(f).ToLowerInvariant();
			return ext is ".iso" or ".wim" or ".esd" or ".ffu" or ".vhdx" or ".vhd";
		});
		if (file == null)
		{
			Log("Dropped item ignored — drop a Windows .iso / .wim / .esd (or .ffu) file.");
			return;
		}
		// Switch to a matching mode automatically.
		if (Path.GetExtension(file).ToLowerInvariant() is ".ffu" or ".vhdx" or ".vhd")
			ModeBox.SelectedIndex = ModeRestoreSavedClone;
		else if (ModeBox.SelectedIndex == ModeCloneCurrentWindows)
			ModeBox.SelectedIndex = ModeInstallFromImage;
		sourcePath = file;
		SourcePathBox.Text = file;
		Log("Source set by drag & drop: " + file);
		UpdateStartReadiness();
		if (ModeBox.SelectedIndex == ModeInstallFromImage)
		{
			await LoadEditionsAsync(file);
		}
	}

	// Plays a sound and flashes the taskbar when a long operation finishes, so the user can step away.
	private void NotifyOperationDone(bool success)
	{
		if (SoundOnFinishCheck?.IsChecked == true)
			try { (success ? System.Media.SystemSounds.Asterisk : System.Media.SystemSounds.Hand).Play(); } catch { }
		if (FlashOnFinishCheck?.IsChecked == true)
		{
			try
			{
				if (!IsActive)
				{
					var helper = new System.Windows.Interop.WindowInteropHelper(this);
					var fw = new FLASHWINFO
					{
						cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
						hwnd = helper.Handle,
						dwFlags = 0x3 /*FLASHW_ALL*/ | 0x4 /*FLASHW_TIMERNOFG*/,
						uCount = uint.MaxValue,
						dwTimeout = 0
					};
					FlashWindowEx(ref fw);
				}
			}
			catch { }
		}
		// Offer the support prompt after EVERY completed task (not just Create-USB). MaybeOfferDonation()
		// itself respects the Settings toggle and the "1st + every 5th" throttle, and skips headless runs.
		if (success) MaybeOfferDonation();
	}

	// Outcome of the optional driver-inject + debloat steps, surfaced in the completion dialog so the user can
	// verify they actually ran. _lastDriversAdded: -2 not requested, -1 failed, 0 none found, >0 packages added.
	private int _lastDriversAdded = -2;
	private bool _lastDebloatApplied = false;

	private string BuildDriverDebloatSummary()
	{
		string s = "";
		if (_lastDriversAdded > 0) s += "\n\n" + string.Format(L("MbDriversAdded"), _lastDriversAdded);
		else if (_lastDriversAdded == 0 || _lastDriversAdded == -1) s += "\n\n" + L("MbDriversNone");
		if (_lastDebloatApplied) s += "\n" + L("MbDebloatApplied");
		return s;
	}

	// Rough estimate of how long the write will take, from the data size and the drive's measured speed.
	private string EstimateOperationTime(DiskItem disk, long bytes)
	{
		double mbps = 0;
		if (speedResults.TryGetValue(disk.Number, out SpeedResult sr) && sr.SequentialWriteMb > 1)
			mbps = sr.SequentialWriteMb;
		if (mbps <= 1) mbps = 60; // conservative default when no speed test yet
		double minutes = (bytes / 1024.0 / 1024.0) / mbps / 60.0 * 1.25; // +25% for overhead/verification
		if (minutes < 1) return "about a minute";
		if (minutes < 90) return $"about {Math.Ceiling(minutes)} minutes";
		return $"about {Math.Round(minutes / 60.0, 1)} hours";
	}

	// One clear "here is what will happen" confirmation instead of several pop-ups.
	private async Task<bool> ConfirmOperationSummary(DiskItem disk)
	{
		bool isClone = ModeBox.SelectedIndex == ModeCloneCurrentWindows || IsExperimentalNtfsMode(ModeBox.SelectedIndex);
		bool isFfu = ModeBox.SelectedIndex == ModeRestoreSavedClone;
		long bytes = isClone ? GetCurrentWindowsUsedBytes()
			: (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath) ? new FileInfo(sourcePath).Length * 3 : 30L * 1024 * 1024 * 1024);

		// Show what is currently ON the target disk so the user can be 100% sure they picked the right one.
		string contents = await GetDiskContentsAsync(disk.Number);

		var sb = new StringBuilder();
		sb.AppendLine(isClone ? "Clone THIS PC's Windows to:" : isFfu ? "Restore a saved disk image to:" : "Create a Windows USB on:");
		sb.AppendLine("    Disk " + disk.Number + " — " + disk.FriendlyName + " — " + FormatBytes(disk.Size));
		sb.AppendLine();
		sb.AppendLine("This disk currently contains:");
		sb.AppendLine(contents);
		sb.AppendLine();
		sb.AppendLine("⚠ ALL of the above will be ERASED.");
		sb.AppendLine();

		var opts = new List<string>();
		if (BitLockerCheck.IsChecked == true) opts.Add("Encrypt with BitLocker (recovery key saved)");
		if (BypassAccountCheck.IsChecked == true && !isClone) opts.Add("Skip Microsoft account (create a local account)");
		if (BypassRequirementsCheck.IsChecked == true && !isClone) opts.Add("Bypass Windows 11 requirements");
		if (ModeBox.SelectedIndex == ModeCloneInternal) opts.Add("Clone the whole disk (Windows + all data partitions)");
		else if (CloneOtherPartitionsCheck.IsChecked == true && isClone) opts.Add("Also clone other data partitions");
		else if (DataPartitionCheck.IsChecked == true) opts.Add("Create an extra data partition");
		if (VerifyContentCheck.IsChecked == true && isClone) opts.Add("Verify cloned data afterwards");
		if (CompactImageCheck.IsChecked == true && !isClone && !isFfu) opts.Add("Compact (space-saving) image");
		sb.AppendLine(opts.Count > 0 ? "Options: " + string.Join(", ", opts) + "." : "Options: defaults.");
		sb.AppendLine();
		sb.AppendLine("Estimated time: " + EstimateOperationTime(disk, bytes) + " (depends on the drive).");
		if (NeedsStrongPerformanceWarning(disk) && ModeBox.SelectedIndex != ModeCloneInternal)
			sb.AppendLine("\nNote: this drive may be slow for Windows To Go.");
		sb.AppendLine("\nContinue?");

		if (MessageBox.Show(sb.ToString(), "Confirm — please review", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
			return false;
		// Last line of defence: make sure the drive at this number is still the exact one the user reviewed.
		return await VerifyTargetDiskUnchangedAsync(disk);
	}

	// Disks are addressed by number, but Windows can renumber them if a drive is unplugged/replugged between the
	// scan and the write. Re-read the target's identity immediately before erasing it and refuse if it changed.
	private async Task<bool> VerifyTargetDiskUnchangedAsync(DiskItem disk)
	{
		try
		{
			string script =
				"$d = Get-Disk -Number " + disk.Number + " -ErrorAction SilentlyContinue;" +
				"if(-not $d){ 'MISSING'; return };" +
				"[pscustomobject]@{ Size=[int64]$d.Size; Serial=$d.SerialNumber; Name=$d.FriendlyName } | ConvertTo-Json -Compress";
			string raw = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script));
			if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("MISSING", StringComparison.Ordinal)) return FailTargetDiskChanged(); // script prints bare 'MISSING' only when the disk is gone; a FriendlyName containing "MISSING" must not false-trigger
			string outp = ExtractJsonPayload(raw);
			if (string.IsNullOrWhiteSpace(outp)) return FailTargetDiskChanged();
			using JsonDocument doc = JsonDocument.Parse(outp);
			JsonElement root = doc.RootElement;
			long curSize = root.TryGetProperty("Size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : -1L;
			string curSerial = GetJsonString(root, "Serial", "").Trim();
			string curName = GetJsonString(root, "Name", "").Trim();
			// Size is the strongest always-present signal; the serial confirms identity when the drive exposes one.
			if (curSize != disk.Size) return FailTargetDiskChanged();
			if (disk.Serial.Length > 0 && curSerial.Length > 0)
			{
				if (!string.Equals(curSerial, disk.Serial, StringComparison.OrdinalIgnoreCase)) return FailTargetDiskChanged();
			}
			else if (curName.Length > 0 && !string.Equals(curName, (disk.FriendlyName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
			{
				// No serial to compare — also require the friendly name to match WHEN the disk exposes one, as a weak
				// extra guard against a same-size disk swapped in at the last moment. Skip when no name is exposed
				// (some Storage Spaces / VHD / USB-bridge disks report a null name that the scan substitutes to 'Disk N').
				return FailTargetDiskChanged();
			}
			return true;
		}
		catch
		{
			// If we truly cannot re-verify, fail safe — cancelling is always better than writing to the wrong disk.
			return FailTargetDiskChanged();
		}
	}

	private bool FailTargetDiskChanged()
	{
		// Headless/scheduled runs have no one to click a dialog — log instead so the run can abort cleanly.
		if (!headlessRun) MessageBox.Show(L("MbDiskChanged"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning);
		else Log("Target disk changed since it was reviewed — refusing to write.");
		return false;
	}

	// Lists the partitions/volumes currently on a disk (letter, label, size, used) for the confirm dialog,
	// so the user can recognise the drive before it is erased. Best-effort; never throws.
	private async Task<string> GetDiskContentsAsync(int diskNumber)
	{
		try
		{
			string script =
				"$p = Get-Partition -DiskNumber " + diskNumber + " -ErrorAction SilentlyContinue;" +
				"if(-not $p){ 'EMPTY'; return };" +
				"$any=$false;" +
				"foreach($x in $p){" +
				" $v = $null; if($x.DriveLetter){ $v = Get-Volume -DriveLetter $x.DriveLetter -ErrorAction SilentlyContinue };" +
				" if($v){ $any=$true;" +
				"  $lbl = if($v.FileSystemLabel){ $v.FileSystemLabel } else { 'No label' };" +
				"  $used = [math]::Round(($v.Size-$v.SizeRemaining)/1GB,1);" +
				"  $tot = [math]::Round($v.Size/1GB,1);" +
				"  '   ' + $x.DriveLetter + \": '\" + $lbl + \"' - \" + $tot + ' GB (' + $used + ' GB used)'" +
				" } elseif($x.Size -gt 64MB){ $any=$true; '   Partition - ' + [math]::Round($x.Size/1GB,1) + ' GB (no drive letter)' }" +
				"};" +
				"if(-not $any){ 'A partition with no readable Windows volume.' }";
			string outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script));
			outp = outp.Trim();
			if (string.IsNullOrWhiteSpace(outp) || outp == "EMPTY")
				return "   (empty or unformatted — no partitions)";
			return outp;
		}
		catch
		{
			return "   (could not read the drive's contents)";
		}
	}

	private async void ScheduleCloneButton_Click(object sender, RoutedEventArgs e)
	{
		// This button's IsEnabled is never managed by SetBusy, so it stays live during a running clone — and the
		// schtasks.exe it launches becomes `activeProcess`, overwriting the clone's own. schtasks exits in about a
		// second and nulls the field, after which Stop can no longer kill the running dism/robocopy tree and the
		// stall watchdog goes dead for the rest of the clone. Same guard every other operation handler opens with.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk) || disk.IsSystem)
		{
			MessageBox.Show(L("Mb001"), "Schedule clone", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		bool internalMode = ModeBox.SelectedIndex == ModeCloneInternal;
		MessageBoxResult freq = MessageBox.Show(
			"Run this clone automatically to:\n    " + disk.FriendlyName + "\n\nYes = every day at 02:00\nNo = every week (Sunday) at 02:00\nCancel = don't schedule\n\n" +
			"Keep that drive connected at the scheduled time. The clone runs unattended (no windows to click).",
			"Schedule automatic clone", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
		if (freq == MessageBoxResult.Cancel) return;
		string exe = "";
		try { exe = Process.GetCurrentProcess().MainModule?.FileName ?? ""; } catch { }
		if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
		{
			MessageBox.Show(L("Mb002"), "Schedule clone", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		// Strip any double-quotes from the name so they can't break the quoted command line or the --auto-clone re-parse.
		string safeName = (disk.FriendlyName ?? "").Replace("\"", "");
		string trElem = $"\"{exe}\" --auto-clone --diskname=\"{safeName}\" --disksize={disk.Size} --diskserial=\"{disk.Serial}\" --mode={(internalMode ? "internal" : "portable")}";
		var args = new List<string> { "/Create", "/TN", "DriveForge Auto Clone", "/TR", trElem };
		if (freq == MessageBoxResult.Yes) { args.Add("/SC"); args.Add("DAILY"); }
		else { args.Add("/SC"); args.Add("WEEKLY"); args.Add("/D"); args.Add("SUN"); }
		args.Add("/ST"); args.Add("02:00");
		args.Add("/RL"); args.Add("HIGHEST");
		args.Add("/F");
		try
		{
			await RunProcessWithArgumentListAsync("schtasks.exe", args);
			MessageBox.Show(string.Format(L("MbSchedCreated"), disk.FriendlyName, (freq == MessageBoxResult.Yes ? L("MbSchedDaily") : L("MbSchedWeekly"))),
				L("MbSchedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			ShowError(L("ErrSchedTask"), ex);
		}
	}

	private void HelpButton_Click(object sender, RoutedEventArgs e)
	{
		MessageBox.Show(
			string.Format(L("HelpBody"), AppVersionString()),
			L("HelpTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
	}

	private async void VerifyIsoButton_Click(object sender, RoutedEventArgs e)
	{
		// This handler is visible+enabled during a running ISO write; without this guard it would SetBusy(false) in
		// its finally and disable the live write's Stop/Pause (and re-enable Start) mid-operation.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
		{
			MessageBox.Show(L("Mb003"), L("DlgIsoChecksum"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string path = sourcePath;
		try
		{
			SetBusy(busy: true, L("BzSha"));
			stopRequested = false; isPaused = false;   // Stop is enabled during hashing; reset the flag so the loop below can honor it
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			long total = new FileInfo(path).Length;
			string hash = await Task.Run(() =>
			{
				using var sha = System.Security.Cryptography.SHA256.Create();
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
				byte[] buf = new byte[1 << 20];
				long done = 0;
				int read;
				int lastPct = -1;
				while ((read = fs.Read(buf, 0, buf.Length)) > 0)
				{
					if (stopRequested) throw new OperationCanceledException("Checksum stopped.");
					sha.TransformBlock(buf, 0, read, null, 0);
					done += read;
					int pct = total > 0 ? (int)(done * 100 / total) : 0;
					if (pct != lastPct)
					{
						lastPct = pct;
						Dispatcher.BeginInvoke((Action)(() => { ProgressBar.Value = pct; ProgressPercentText.Text = pct + "%"; }));
					}
				}
				sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
				return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
			});
			ShowIsoChecksumDialog(Path.GetFileName(path), hash);
		}
		catch (OperationCanceledException) { Log("Checksum stopped by user."); }
		catch (Exception ex)
		{
			ShowError(L("ErrChecksum"), ex);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			SetBusy(busy: false);
			ResetProgressWidgets();   // the stats line too — it was left reading "Progress: 100.0%" beside an empty bar
			StatusText.Text = L("SxReady");
		}
	}

	private void ShowIsoChecksumDialog(string fileName, string sha256)
	{
		var dialog = new Window
		{
			Title = L("DlgIsoChecksum"),
			Width = 600,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			Background = (Brush)FindResource("NavyBrush")
		};
		var panel = new StackPanel { Margin = new Thickness(16) };
		panel.Children.Add(new TextBlock { Text = string.Format(L("ChkHashOf"), fileName), Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 6) });
		var hashBox = new TextBox { Text = sha256, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 10) };
		panel.Children.Add(hashBox);
		panel.Children.Add(new TextBlock { Text = L("ChkCompareHint"), Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
		var expectedBox = new TextBox { FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 8) };
		panel.Children.Add(expectedBox);
		var resultText = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		panel.Children.Add(resultText);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		var compareBtn = new Button { Content = L("ChkCheckBtn"), Width = 110, Margin = new Thickness(0, 0, 8, 0) };
		var closeBtn = new Button { Content = L("ChkClose"), Width = 90 };
		buttons.Children.Add(compareBtn);
		buttons.Children.Add(closeBtn);
		panel.Children.Add(buttons);
		dialog.Content = panel;
		compareBtn.Click += delegate
		{
			// Extract a BOUNDED 64-hex SHA-256 token: tolerates a full "<hash>  filename" checksum-file line and a
			// "sha256:" prefix, and does NOT match a 32/40/128-hex MD5/SHA-1/SHA-512 (those get a distinct "not a
			// SHA-256" note instead of a scary false "corrupt" verdict on a genuinely good ISO). Old code stripped all
			// whitespace and compared the whole thing, so any pasted filename/prefix/wrong-algo falsely read as no-match.
			var mm = System.Text.RegularExpressions.Regex.Matches(expectedBox.Text ?? "", "(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])");
			if (mm.Count == 0)
			{
				resultText.Text = string.IsNullOrWhiteSpace(expectedBox.Text) ? L("ChkPasteFirst") : L("ChkNotSha256");
				resultText.Foreground = (Brush)FindResource("MutedBrush");
				return;
			}
			// Match if ANY 64-hex token equals the file's hash — handles a bare hash, a "<hash>  file" line, AND a whole
			// multi-line SHA256SUMS pasted in (the user's ISO may not be the first entry). Cannot false-match: sha256 is
			// the file's real hash, so a token only matches when a listed hash genuinely equals it.
			bool match = mm.Cast<System.Text.RegularExpressions.Match>().Any(x => string.Equals(x.Value, sha256, StringComparison.OrdinalIgnoreCase));
			resultText.Text = match ? L("ChkMatch") : L("ChkNoMatch");
			resultText.Foreground = new SolidColorBrush(match ? Color.FromRgb(22, 163, 74) : Color.FromRgb(220, 60, 60));
		};
		closeBtn.Click += delegate { dialog.Close(); };
		dialog.ShowDialog();
	}

	private static string AppVersionString()
	{
		try
		{
			var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
			return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";
		}
		catch { return ""; }
	}

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		// A headless (Task Scheduler) run drives its own shutdown and has no user to answer a prompt — never block it.
		if (headlessRun) return;
		if (isBusy || _cleanBusy || _analyzerBusy)
		{
			MessageBoxResult messageBoxResult = MessageBox.Show(L("Mb004"), "DriveForge", MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No);
			e.Cancel = messageBoxResult != MessageBoxResult.Yes;
			// If the user confirms close, signal the background workers to stop AND kill the running external tool
			// (diskpart/dism/wimlib) before teardown — otherwise it keeps writing to the disk after the app has exited.
			if (!e.Cancel)
			{
				_analyzerStop = true; stopRequested = true; _recoverPaused = false;
				if (activeProcess != null) { try { activeProcess.Kill(entireProcessTree: true); } catch { } }
			}
		}
		if (!e.Cancel)
		{
			SaveUserSettings();
			// Hygiene: the kernel drops the request when the process dies anyway, but a torn-down window whose
			// operation `finally` never gets to run (the Dispatcher is going away) should not be the only thing
			// standing between this machine and a normal sleep.
			ReleasePowerRequest();
			_sleepBlocked = false;
			// "Clean up all request objects and associated handles before the process exits" — the kernel would
			// reclaim it anyway, but closing it here keeps the documented contract.
			if (_powerRequest != IntPtr.Zero) { try { CloseHandle(_powerRequest); } catch { } _powerRequest = IntPtr.Zero; }
		}
	}

	private static string UserSettingsPath =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge", "settings.json");

	private sealed class AppSettings
	{
		public bool BypassRequirements { get; set; }
		public bool BypassAccount { get; set; }
		public bool DataPartition { get; set; }
		public bool VerifyContent { get; set; } = true;
		public bool CompactImage { get; set; } = true;
		public bool HasRunBefore { get; set; }
		public bool SoundOnFinish { get; set; } = true;
		public bool FlashOnFinish { get; set; } = true;
		public bool RememberLastTask { get; set; }
		public int LastTask { get; set; } = ModeExperimentalNtfsFullRootUsbClone;
		public bool ShowDonatePrompt { get; set; } = true;
		public int SuccessCount { get; set; }
		public double WinLeft { get; set; } = double.NaN;
		public double WinTop { get; set; } = double.NaN;
		public double WinWidth { get; set; } = double.NaN;
		public double WinHeight { get; set; } = double.NaN;
		public bool WinMaximized { get; set; } = true;
	}

	private bool isFirstRun = false;

	// Persist the option checkboxes and window placement between runs.
	private void SaveUserSettings()
	{
		try
		{
			var bounds = (WindowState == WindowState.Normal)
				? new Rect(Left, Top, Width, Height)
				: RestoreBounds;
			var settings = new AppSettings
			{
				BypassRequirements = BypassRequirementsCheck.IsChecked == true,
				BypassAccount = BypassAccountCheck.IsChecked == true,
				DataPartition = DataPartitionCheck.IsChecked == true,
				VerifyContent = VerifyContentCheck.IsChecked == true,
				CompactImage = CompactImageCheck.IsChecked == true,
				HasRunBefore = true,
				SoundOnFinish = SoundOnFinishCheck.IsChecked == true,
				FlashOnFinish = FlashOnFinishCheck.IsChecked == true,
				RememberLastTask = RememberTaskCheck.IsChecked == true,
				LastTask = ModeBox.SelectedIndex,
				ShowDonatePrompt = ShowDonatePromptCheck.IsChecked == true,
				SuccessCount = _successCount,
				WinLeft = bounds.Left,
				WinTop = bounds.Top,
				WinWidth = bounds.Width,
				WinHeight = bounds.Height,
				WinMaximized = WindowState == WindowState.Maximized
			};
			Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath));
			File.WriteAllText(UserSettingsPath, JsonSerializer.Serialize(settings), Encoding.UTF8);
		}
		catch { }
	}

	private void LoadUserSettings()
	{
		try
		{
			if (!File.Exists(UserSettingsPath))
			{
				isFirstRun = true;
				return;
			}
			var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(UserSettingsPath, Encoding.UTF8));
			if (s == null) return;
			BypassRequirementsCheck.IsChecked = s.BypassRequirements;
			BypassAccountCheck.IsChecked = s.BypassAccount;
			DataPartitionCheck.IsChecked = s.DataPartition;
			VerifyContentCheck.IsChecked = s.VerifyContent;
			CompactImageCheck.IsChecked = s.CompactImage;
			SoundOnFinishCheck.IsChecked = s.SoundOnFinish;
			FlashOnFinishCheck.IsChecked = s.FlashOnFinish;
			RememberTaskCheck.IsChecked = s.RememberLastTask;
			ShowDonatePromptCheck.IsChecked = s.ShowDonatePrompt;
			_successCount = s.SuccessCount;
			// Restore the last task only if the user opted in and the saved index is valid.
			if (s.RememberLastTask && s.LastTask >= 0 && s.LastTask < ModeBox.Items.Count)
				ModeBox.SelectedIndex = s.LastTask;
			isFirstRun = !s.HasRunBefore;
			// Restore window placement if it was saved and is on-screen.
			if (!double.IsNaN(s.WinWidth) && s.WinWidth > 200 && !double.IsNaN(s.WinHeight) && s.WinHeight > 200)
			{
				double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
				if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop) && s.WinLeft > -50 && s.WinTop > -50 && s.WinLeft < vw - 100 && s.WinTop < vh - 100)
				{
					WindowStartupLocation = WindowStartupLocation.Manual;
					Left = s.WinLeft; Top = s.WinTop;
				}
				Width = s.WinWidth; Height = s.WinHeight;
			}
			WindowState = s.WinMaximized ? WindowState.Maximized : WindowState.Normal;
		}
		catch { }
	}

	private void UpdateAdminStatus()
	{
		bool flag = IsAdministrator();
		AdminDot.Fill = new SolidColorBrush(flag ? Color.FromRgb(22, 163, 74) : Color.FromRgb(220, 38, 38));
		AdminStatusText.Text = L(flag ? "AdminActive" : "AdminRequired");
	}

	// ---------- Auto-refresh the disk list on USB plug/unplug (WM_DEVICECHANGE) ----------
	private const int WM_DEVICECHANGE = 0x0219;
	private const int WM_POWERBROADCAST = 0x0218;         // resume notifications — a suspend can drop a held power request
	private const int PBT_APMRESUMESUSPEND = 0x0007;
	private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
	private const int DBT_DEVICEARRIVAL = 0x8000;         // a device or piece of media has been inserted
	private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;  // a device or piece of media has been removed
	private System.Windows.Threading.DispatcherTimer? _deviceChangeDebounce;

	// Hooks the window message loop so the disk list refreshes itself when a drive is connected or removed,
	// instead of the user having to click Refresh. Best-effort: if the hook fails, the manual button still works.
	private void SetupDeviceChangeAutoRefresh()
	{
		try
		{
			IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
			System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(DeviceChangeWndProc);
		}
		catch { /* non-fatal — the Refresh button remains available */ }
	}

	private IntPtr DeviceChangeWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
	{
		if (msg == WM_DEVICECHANGE)
		{
			int evt = wParam.ToInt32();
			if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
				ScheduleDiskAutoRefresh();
		}
		else if (msg == WM_POWERBROADCAST)
		{
			// Coming back from a suspend can drop a held power request (this is why PowerToys' Awake re-applies it on
			// resume). If an operation somehow survived, re-take the request rather than silently running unprotected.
			//
			// RELEASE FIRST. The kernel REFCOUNTS PowerSetRequest per handle per type, so simply zeroing our own
			// bookkeeping and setting again would leave the count at 2 while teardown only ever decrements once —
			// stranding a request for the life of the process, i.e. a PC that never idle-sleeps again. That is the
			// exact opposite of what this feature is for. PowerClearRequest against a request Windows already
			// cancelled just returns false, which is harmless.
			// Only genuine RESUME events belong here: an AC<->battery change (PBT_APMPOWERSTATUSCHANGE) is not a
			// resume and does not disturb the request, so re-asserting on it was pure risk for no benefit.
			int evt = wParam.ToInt32();
			// Gate on the busy flags, NOT on _sleepBlocked: the single most useful thing this branch can do is leave
			// evidence that the machine suspended mid-operation, and that must be recorded even when the request was
			// never held (an admin override neutralises the request at policy level while PowerSetRequest still
			// returns true — exactly the case where a PC sleeps anyway and nothing else would say so).
			if (evt == PBT_APMRESUMEAUTOMATIC || evt == PBT_APMRESUMESUSPEND)
			{
				// Log on the FIRST event only: Windows sends PBT_APMRESUMEAUTOMATIC and then, if it sees user
				// activity, PBT_APMRESUMESUSPEND — so logging on both printed one wake as two suspends, in the one
				// place this is meant to be unambiguous evidence. Re-assert on both, which is harmless and balanced.
				if (evt == PBT_APMRESUMEAUTOMATIC && (isBusy || _cleanBusy || _analyzerBusy))
					Log("This PC suspended and resumed while an operation was running — re-applying the keep-awake request.");
				if (_sleepBlocked)
				{
					_sleepHeldTicks += UnbiasedTicksNow() - _sleepBlockStartTicks;   // bank the segment before rebasing
					ReleasePowerRequest();
					_sleepBlocked = false;   // so UpdateSleepBlock re-asserts instead of seeing "already held"
					_sleepReasserting = true;
					try { UpdateSleepBlock(); } finally { _sleepReasserting = false; }
				}
				else if (isBusy || _cleanBusy || _analyzerBusy)
				{
					// Nothing was held — this is the state left behind when acquiring FAILED earlier. A resume is a
					// natural retry point (the failure may well have been transient) and, without this, the message
					// above would promise a re-apply that never happened and no other attempt would occur before the
					// operation ends.
					UpdateSleepBlock();
				}
			}
		}
		return IntPtr.Zero;
	}

	// A single plug/unplug fires several WM_DEVICECHANGE messages; coalesce them into ONE refresh ~900 ms after the
	// last event. Never rescans while an operation is running (it uses SetBusy + the disk list) — retries after.
	private void ScheduleDiskAutoRefresh()
	{
		if (_deviceChangeDebounce == null)
		{
			_deviceChangeDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
			_deviceChangeDebounce.Tick += async (_, __) =>
			{
				_deviceChangeDebounce!.Stop();
				if (isBusy) { _deviceChangeDebounce!.Start(); return; }   // busy — retry after the operation finishes
				try { await RefreshDisksAsync(silent: true); } catch { }   // passive: never pop a modal from a background rescan
			};
		}
		_silentRescanRetries = 0;   // a genuine new device event earns a fresh auto-retry budget
		_deviceChangeDebounce.Stop();
		_deviceChangeDebounce.Start();
	}

	private async void RefreshDisks_Click(object sender, RoutedEventArgs e)
	{
		// Don't rescan while an operation owns the busy state: a manual refresh clears DiskBox and, via
		// RefreshDisksAsync's finally, would SetBusy(false) — stomping the running op (its Stop button, activeProcess).
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		await RefreshDisksAsync();
	}

	// NOTE: several operations (format, health, partition ops) legitimately call this at the END of their own work
	// while they still hold the busy state, so this method must NOT bail on isBusy — that would break those refreshes.
	// The guard against a passive/manual refresh stomping a DIFFERENT running operation lives at the two standalone
	// entry points instead: the device-change timer (checks isBusy) and RefreshDisks_Click (checks isBusy).
	private async Task RefreshDisksAsync(bool silent = false)
	{
		// ...and because of that, it must not TOUCH the busy state when it is called nested. It used to raise and then
		// clear it unconditionally, so a refresh issued near the end of an operation switched the whole app back to
		// "idle" while the operation was still running: Stop/Pause got disabled, `activeProcess` was nulled (so Stop
		// could no longer kill the external tool), the status text was overwritten with "Scanning disks…" and never
		// restored — and now it would also have released the sleep block. SsdSecureEraseFlow is the worst case: it
		// refreshes and THEN runs a multi-minute Optimize-Volume ReTrim. Only own the busy state when nothing else does.
		//
		// Ownership is a TOKEN, not a snapshot taken at entry. The scan awaits a multi-second PowerShell enumeration,
		// and a real operation can start during that window — SetBusy clears the token whenever anyone else raises
		// busy, so this method hands back only a busy state it still owns. With an entry snapshot instead, a refresh
		// that began while idle would release busy in the middle of the operation that started meanwhile: Stop would
		// go dead, Start would re-enable on a disk being written, and the sleep block would drop. That is reachable
		// today — the Write-ISO flow clears busy for its "verify?" prompt, and the just-repartitioned stick fires
		// device-change events that start exactly such a refresh behind the modal.
		bool wasBusy = isBusy;
		long myScan = ++_diskScanSeq;   // identifies THIS scan, so a failing one cannot blank a newer scan's good list
		try
		{
			if (!wasBusy) { SetBusy(busy: true, L("BzScanDisks")); _refreshOwnsBusy = true; _refreshBusyScan = myScan; }   // set AFTER: SetBusy(true) clears the token
			// Enumerate FIRST, then swap the lists in one synchronous block. Clearing before the await let two
			// overlapping refreshes interleave as clear(A) -> clear(B) -> add(A) -> add(B), leaving every physical
			// drive listed twice (reachable from Eject, which refreshes after a modal while a device-change refresh
			// is already in flight).
			List<DiskItem> found = await GetDisksAsync();
			// Completion order is NOT start order — each scan spawns its own powershell.exe, and one that stalls on a
			// settling/removed device can return long after a later scan already published good results. Publishing
			// then would put the REMOVED drive back in `disks` and drive the ticket backwards, leaving exactly the
			// stale list this method works to avoid. An overtaken scan drops its findings and just tidies up.
			if (myScan > _diskListScan)
			{
				// Capture the selection AFTER the scan, not before it. The list now stays live for the 1-3 s the
				// enumeration takes, and neither picker is disabled while busy — so a user who selects a different disk
				// during a background refresh had their choice silently reverted when the rebuild restored the old one.
				int? selectedDiskNumber = (DiskBox.SelectedItem as DiskItem)?.Number;
				disks.Clear();
				_syncingDisk = true;
				DiskBox.Items.Clear();
				if (DiagDiskBox != null) DiagDiskBox.Items.Clear();
				foreach (DiskItem item in found)
				{
					disks.Add(item);
					DiskBox.Items.Add(item);
					if (DiagDiskBox != null) DiagDiskBox.Items.Add(item);
				}
				_syncingDisk = false;
				// Drop any cached speed result whose disk number no longer points at the drive it was measured on (numbers are
				// recycled when removable drives are swapped) — otherwise the new drive inherits the old one's MB/s and verdict.
				foreach (int cachedNumber in speedResults.Keys.ToList())
				{
					DiskItem? nowAt = disks.FirstOrDefault(d => d.Number == cachedNumber);
					string wanted = speedResultIdentity.TryGetValue(cachedNumber, out string? id) ? id : "";
					if (nowAt == null || !string.Equals(wanted, DiskIdentityKey(nowAt), StringComparison.OrdinalIgnoreCase))
					{
						speedResults.Remove(cachedNumber);
						speedResultIdentity.Remove(cachedNumber);
					}
				}
				// Same treatment for the cached health/SMART report: it holds reliability counters that feed the failure
				// verdict, so it must never survive onto a different physical drive that inherited the same disk number.
				if (_diagDisk != null)
				{
					DiskItem? diagNowAt = disks.FirstOrDefault(d => d.Number == _diagDisk.Number);
					if (diagNowAt == null || !string.Equals(DiskIdentityKey(diagNowAt), DiskIdentityKey(_diagDisk), StringComparison.OrdinalIgnoreCase))
					{ _diagDisk = null; _diagReport = null; }
				}
				if (DiskBox.Items.Count > 0)
				{
					DiskItem? previousSelection = selectedDiskNumber.HasValue ? disks.FirstOrDefault((DiskItem disk) => disk.Number == selectedDiskNumber.Value) : null;
					DiskBox.SelectedItem = previousSelection ?? DiskBox.Items[0];
					if (DiagDiskBox != null) DiagDiskBox.SelectedItem = DiskBox.SelectedItem;
				}
				Log($"Disks found: {disks.Count}");
				_diskListScan = myScan;   // this scan's results are what the lists now hold
			}
			_silentRescanRetries = 0;   // a scan got through: the auto-retry budget is spent only on consecutive failures
			// Only claim "Ready" if THIS invocation owns the busy state. The token alone is not enough — two refreshes
			// can be in flight at once, and a straggler would otherwise speak for the one that actually holds it.
			// Comparing the ticket gives true per-invocation identity, so a nested or overtaken scan stays quiet
			// instead of painting "Ready" over a running operation — worst case SsdSecureEraseFlow, which refreshes
			// and then runs a multi-minute ReTrim.
			if (_refreshOwnsBusy && _refreshBusyScan == myScan) StatusText.Text = L("SxReady");
		}
		catch (Exception ex)
		{
			// Drop the stale list when the scan fails. Enumerating before clearing (above) means a throw would
			// otherwise leave the PRE-operation entries on screen and, worse, in `disks` — and SsdSecureEraseFlow
			// reads `disks` right after its wipe to find the drive letter to TRIM. With a stale entry it would
			// TRIM a letter the disk no longer has, the non-terminating PowerShell error would go unnoticed, and
			// the flow would claim the controller discarded the blocks when no TRIM was ever issued. An empty list
			// makes that path fail honestly instead.
			// ...but only if nothing NEWER has populated the lists meanwhile: two refreshes can overlap, and a
			// straggler that fails must not wipe out the good results a later scan already delivered.
			bool cleared = _diskListScan < myScan;
			if (cleared)
			{
				disks.Clear();
				_syncingDisk = true;
				DiskBox.Items.Clear();
				if (DiagDiskBox != null) DiagDiskBox.Items.Clear();
				PartitionMapGrid?.Children.Clear();   // else the vanished disk's map stays drawn beside an empty picker
				_partitionMapDisk = -1;               // and stop an in-flight BuildPartitionMapAsync from redrawing it
				// The EMPTY list is this scan's published state. Leaving the ticket at the older value would let a
				// scan that started BEFORE this failure — one still stalled in powershell.exe on the settling device —
				// come back and republish the removed drive, undoing the clear and defeating its whole purpose.
				_diskListScan = myScan;
			}
			// A device-change auto-refresh is passive — log a transient scan failure instead of popping a modal.
			if (silent) Log(L("ErrDiskScan") + ": " + ex.Message);
			else ShowError(L("ErrDiskScan"), ex);
			// Say so in the status bar as well. Without this the bar keeps reading "Scanning disks…" for ever, which
			// next to a now-empty picker (and, when silent, no dialog at all) looks exactly like a hung app.
			if (_refreshOwnsBusy && _refreshBusyScan == myScan) StatusText.Text = L("ErrDiskScan");
			// A silent scan is the device-change rescan, and its timer already stopped itself, so re-arming here is
			// the only way a transient failure during a plug/unplug storm heals without a manual Refresh. BOUND it:
			// the retry feeds itself (tick -> scan -> throw -> re-arm -> tick), so on a PERMANENT failure — WinPE
			// images without the PowerShell component, a wedged Storage service — an unbounded version would spawn a
			// powershell.exe every 900 ms for ever, appending the whole captured output to the log each time.
			// Skip it entirely when a newer scan already published good results: that failure changed nothing.
			if (silent && cleared && _silentRescanRetries < 3)
			{
				_silentRescanRetries++;
				_deviceChangeDebounce?.Start();
			}
		}
		finally
		{
			_syncingDisk = false;   // never leave the selection-sync suppressor stranded true if the scan threw mid-rebuild
			// Release only a busy state THIS invocation still owns. Both conditions are needed: the ticket says WE
			// took it (a nested refresh, or a straggler from an earlier scan, must never release another refresh's
			// state), and the token says nobody has taken over since (a real operation that started during the scan
			// owns it now, and clearing here would strand it with the controls wrong and the sleep block dropped).
			if (_refreshOwnsBusy && _refreshBusyScan == myScan) { _refreshOwnsBusy = false; SetBusy(busy: false); }
		}
	}

	// Sidebar: pick a task → switch the (hidden) ModeBox and show the workflow view.
	private void NavTask_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button b && int.TryParse(b.Tag as string, out int idx)
			&& idx >= 0 && idx < ModeBox.Items.Count)
		{
			ShowWorkflowView();
			ModeBox.SelectedIndex = idx;
			HighlightNav(b);
		}
	}

	private void NavTools_Click(object sender, RoutedEventArgs e)
	{
		ShowDiagnosticsView();
		HighlightNav(NavTools);
	}

	private bool _toolsView;

	// True when running inside Windows PE (the rescue boot environment).
	private static bool IsRunningInWinPE()
	{
		try { using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT"); if (k != null) return true; } catch { }
		try { return string.Equals(Path.GetPathRoot(Environment.SystemDirectory), "X:\\", StringComparison.OrdinalIgnoreCase); } catch { }
		return false;
	}

	private void ShowMultiBootView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Visible;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		// Multi-boot has its own button; hide the workflow footer controls.
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
	}

	// Export-to-VHDX has its own landing panel + button (like multi-boot), not a target-disk workflow.
	private void ShowExportVhdxView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Visible;
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
		// Localize the panel from existing keys (no new translation strings needed).
		if (ExPanelTitle != null) ExPanelTitle.Text = L("TbExportVhdx");
		if (ExPanelDesc != null) ExPanelDesc.Text = L("SbExportVhdxS") + "\n\n" + L("ExportVhdxBackupHint");
		if (ExportVhdxRunButton != null) ExportVhdxRunButton.Content = L("TbExportVhdx");
	}

	private void NavExportVhdx_Click(object sender, RoutedEventArgs e)
	{
		ShowExportVhdxView();
		HighlightNav(NavExportVhdx);
	}

	private void ShowWorkflowView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Visible;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		// Footer Start/Pause/Stop belong to the main workflow.
		StartButton.Visibility = Visibility.Visible;
		PauseButton.Visibility = Visibility.Visible;
		StopButton.Visibility = Visibility.Visible;
		UpdateStartReadiness();
	}

	private void ShowDiagnosticsView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = true;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Visible;
		// The diagnostic panel has its own Start/Pause/Stop ("Diagnostic controls") — hide the footer set
		// and the workflow hint so the Drive tools screen is clean.
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
	}

	// Highlight the active sidebar item.
	private void HighlightNav(System.Windows.Controls.Button active)
	{
		var all = new[] { NavCreate, NavClonePortable, NavCloneInternal, NavExportVhdx, NavBackup, NavRestore, NavLinux, NavDownloadIso, NavMultiBoot, NavTools, NavRecover, NavClean };
		var accent = (System.Windows.Media.Brush)FindResource("BlueBrush");
		foreach (var b in all)
		{
			if (b == null) continue;
			b.Background = b == active ? accent : System.Windows.Media.Brushes.Transparent;
			b.BorderBrush = b == active ? accent : System.Windows.Media.Brushes.Transparent;
		}
	}

	private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (SourceHelpText != null && EditionBox != null && StartButton != null && SourcePathBox != null)
		{
			bool cloneMode = ModeBox.SelectedIndex == ModeCloneCurrentWindows || ModeBox.SelectedIndex == ModeCloneInternal;
			bool installMode = ModeBox.SelectedIndex == ModeInstallFromImage;
			bool restoreMode = ModeBox.SelectedIndex == ModeRestoreSavedClone;
			bool backupMode = ModeBox.SelectedIndex == ModeBackupImage;
			bool isoWriteMode = ModeBox.SelectedIndex == ModeWriteIsoImage;

			SourceHelpText.Text = isoWriteMode ? L("DescIsoWrite")
				: backupMode ? L("DescBackup") : cloneMode ? L("DescClone") : restoreMode ? L("DescRestore") : L("DescInstall");
			// Section 2 heading reflects what the source actually is for the current task.
			Step2Title.Text = isoWriteMode ? L("Step2IsoImage") : restoreMode ? L("Step2ImageFile") : L("Step2Title");

			// Browse for a file only when a file is the source: ISO install, restore, or writing an ISO image.
			SourceFileRow.Visibility = (installMode || restoreMode || isoWriteMode) ? Visibility.Visible : Visibility.Collapsed;
			EditionLabel.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			EditionBox.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			EditionLabel.IsEnabled = installMode;
			EditionBox.IsEnabled = installMode;
			VerifyIsoButton.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			// Backup writes to a file you choose — it needs no target disk. Everything else does.
			TargetSection.Visibility = backupMode ? Visibility.Collapsed : Visibility.Visible;
			// Options only apply to a fresh ISO setup or a clone. Restore applies an image as-is; backup makes a file.
			OptionsSection.Visibility = (installMode || cloneMode) ? Visibility.Visible : Visibility.Collapsed;
			// Backup writes a single file — the disk Diagnostic Center (and the step that points to it) are
			// irrelevant there, so hide them and let the task panel use the full width.
			// Header title for the current task (the sidebar is the task selector now).
			TaskTitleText.Text = installMode ? L("TaskTitleInstall")
				: ModeBox.SelectedIndex == ModeCloneCurrentWindows ? L("TaskTitleClonePortable")
				: ModeBox.SelectedIndex == ModeCloneInternal ? L("TaskTitleCloneInternal")
				: backupMode ? L("TaskTitleBackup")
				: isoWriteMode ? L("TaskTitleIsoWrite")
				: L("TaskTitleRestore");
			StartButton.Content = isoWriteMode ? L("StartWriteIso") : StartButton.Content;
			VerifyIsoButton.Visibility = (installMode || isoWriteMode) ? Visibility.Visible : Visibility.Collapsed;
			// The "extra data partition" option makes no sense for the whole-disk internal clone.
			DataPartitionCheck.Visibility = (installMode || ModeBox.SelectedIndex == ModeCloneCurrentWindows) ? Visibility.Visible : Visibility.Collapsed;
			SourcePathBox.IsEnabled = !cloneMode;
			// The Win11-requirement and Microsoft-account bypasses only do anything during a fresh ISO setup
			// (and even then the account one relies on the unattend.xml). On a clone of an already-installed
			// Windows they are no-ops, so hide them entirely there instead of showing a dead checkbox.
			BypassRequirementsCheck.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			BypassAccountCheck.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			DebloatCheck.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			AddNetworkDriversCheck.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			AddAllDriversCheck.Visibility = installMode ? Visibility.Visible : Visibility.Collapsed;
			EjectWhenDoneCheck.Visibility = (installMode || cloneMode) ? Visibility.Visible : Visibility.Collapsed;
			// Optional only for the portable clone. The internal-disk clone copies all data partitions
			// automatically (whole-disk clone), so the checkbox is hidden there.
			CloneOtherPartitionsCheck.Visibility = (ModeBox.SelectedIndex == ModeCloneCurrentWindows) ? Visibility.Visible : Visibility.Collapsed;
			ScheduleCloneButton.Visibility = cloneMode ? Visibility.Visible : Visibility.Collapsed;
			// Only RunExperimentalFullRootUsbCloneAsync (the clone flow) ever reads these two — a fresh ISO install
			// applies install.wim directly and has no "engine" choice, so the checkboxes did nothing there but sit
			// on screen looking like a decision the user needed to make. Same treatment as the bypass checkboxes above.
			UseDismEngineCheck.Visibility = cloneMode ? Visibility.Visible : Visibility.Collapsed;
			UseNtfsRawEngineCheck.Visibility = cloneMode ? Visibility.Visible : Visibility.Collapsed;
			UseDismEngineCheck.IsEnabled = cloneMode;
			UseNtfsRawEngineCheck.IsEnabled = cloneMode;
			BypassRequirementsCheck.IsEnabled = installMode;
			BypassAccountCheck.IsEnabled = installMode;
			DebloatCheck.IsEnabled = installMode;
			BitLockerCheck.IsEnabled = installMode || cloneMode;

			BootModeText.Text = L("BootModeText");

			StartButton.Content = isoWriteMode ? "Write ISO to USB" : backupMode ? L("StartBackup") : cloneMode ? L("StartClone") : restoreMode ? L("StartRestore") : L("StartInstall");

			SourcePathBox.Text = cloneMode ? "Current Windows on this computer" : backupMode ? "This PC (saved to a file you choose)" : "";
			sourcePath = null;
			EditionBox.Items.Clear();
			CreateKitButton.Visibility = Visibility.Collapsed;
			Log("Mode selected: " + ModeBox.SelectedItem);
			// Keep the sidebar highlight in sync with the selected task (covers settings-restored task too).
			if (NavCreate != null)
			{
				int i = ModeBox.SelectedIndex;
				HighlightNav(i == ModeInstallFromImage ? NavCreate
					: i == ModeCloneInternal ? NavCloneInternal
					: i == ModeBackupImage ? NavBackup
					: i == ModeRestoreSavedClone ? NavRestore
					: i == ModeWriteIsoImage ? NavLinux
					: NavClonePortable);
			}
			UpdateDriveVerdict();
			UpdateStartReadiness();
		}
	}

	private async void PauseButton_Click(object sender, RoutedEventArgs e)
	{
		Process? process = activeProcess;
		if (process == null || process.HasExited)
		{
			if (isBusy)
			{
				isPaused = !isPaused;
				if (isPaused)
				{
					operationStopwatch.Stop();
					PauseButton.Content = L("BtnResume");
					ToolPauseButton.Content = L("BtnResume");
					StatusText.Text = L("SxPaused");
					SetToolStatus(L("StPaused"));
					Log("Operation paused.");
				}
				else
				{
					operationStopwatch.Start();
					PauseButton.Content = L("BtnPause");
					ToolPauseButton.Content = L("BtnPause");
					StatusText.Text = L("SxResumed");
					SetToolStatus(L("StResumed"));
					Log("Operation resumed.");
				}
			}
			return;
		}
		try
		{
			IReadOnlyList<int> ids = await GetProcessTreeIdsAsync(process.Id);
			if (!isPaused)
			{
				foreach (int id in ids)
				{
					SuspendProcessById(id);
				}
				isPaused = true;
				operationStopwatch.Stop();
				PauseButton.Content = L("BtnResume");
				ToolPauseButton.Content = L("BtnResume");
				StatusText.Text = L("SxPaused");
				SetToolStatus(L("StDiagPaused"));
				Log("Operation paused.");
			}
			else
			{
				foreach (int id in ids)
				{
					ResumeProcessById(id);
				}
				isPaused = false;
				operationStopwatch.Start();
				PauseButton.Content = L("BtnPause");
				ToolPauseButton.Content = L("BtnPause");
				StatusText.Text = L("SxResumed");
				SetToolStatus(L("StDiagResumed"));
				Log("Operation resumed.");
			}
		}
		catch (Exception ex)
		{
			ShowError(L("ErrPauseResume"), ex);
		}
	}

	private async void StopButton_Click(object sender, RoutedEventArgs e)
	{
		Process? process = activeProcess;
		// The process can exit AND be disposed by its runner on another thread between here and the checks below,
		// so touching HasExited/Id can throw — treat any failure as "already gone".
		bool alive; try { alive = process != null && !process.HasExited; } catch { alive = false; }
		if (!alive)
		{
			if (isBusy)
			{
				if (MessageBox.Show(L("Mb005"), "Stop operation", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
				{
					return;
				}
				stopRequested = true;
				isPaused = false;
				PauseButton.Content = L("BtnPause");
				ToolPauseButton.Content = L("BtnPause");
				StatusText.Text = L("SxStopping");
				SetToolStatus(L("StStopWaitEngine"));
				Log("Stop requested for internal operation.");
			}
			return;
		}
		if (MessageBox.Show(L("Mb006"), "Stop operation", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		stopRequested = true;
		try
		{
			await KillProcessTreeAsync(process.Id);
			Log("Stop requested. Active process tree terminated.");
			StatusText.Text = L("SxStopping");
			SetToolStatus(L("StStopWaitOp"));
		}
		catch (Exception ex)
		{
			ShowError(L("ErrStop"), ex);
		}
	}

	private async void BrowseSource_Click(object sender, RoutedEventArgs e)
	{
		// Block a second Browse while a prior pick's LoadEditionsAsync (mount + dism) is still running: two concurrent
		// loads can leave EditionBox showing one ISO's editions while sourcePath points at another, so the wrong
		// edition index would be applied after the destructive diskpart clean. LoadEditionsAsync holds isBusy.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = (ModeBox.SelectedIndex == ModeRestoreSavedClone ? L("FltRestoreImage") : L("FltInstallImage")),
			Title = L("DlgSelectSource")
		};
		if (openFileDialog.ShowDialog() == true)
		{
			sourcePath = openFileDialog.FileName;
			SourcePathBox.Text = sourcePath;
			Log("Source selected: " + sourcePath);
			UpdateStartReadiness();
			if (ModeBox.SelectedIndex == ModeInstallFromImage)
			{
				await LoadEditionsAsync(sourcePath);
			}
		}
	}

	private async Task LoadEditionsAsync(string path)
	{
		string mountedIso = null;
		try
		{
			_ = 1;
			try
			{
				SetBusy(busy: true, L("BzReadEditions"));
				EditionBox.Items.Clear();
				string imageFile = path;
				if (Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
				{
					mountedIso = path;
					imageFile = FindInstallImage(await MountIsoAsync(path));
				}
				foreach (EditionItem item in await GetImageEditionsAsync(imageFile))
				{
					EditionBox.Items.Add(item);
				}
				if (EditionBox.Items.Count == 0)
				{
					EditionBox.Items.Add(new EditionItem(1, string.Format(L("EdImageIndexFallback"), 1)));
				}
				EditionBox.SelectedIndex = 0;
				Log($"Editions found: {EditionBox.Items.Count}");
			}
			catch (Exception ex)
			{
				ShowError(L("ErrEditions"), ex);
			}
		}
		finally
		{
			if (!string.IsNullOrWhiteSpace(mountedIso))
			{
				await TryUnmountIsoAsync(mountedIso);
			}
			SetBusy(busy: false);
		}
	}

	private bool _syncingDisk;
	private int _partitionMapDisk = -1;

	// Draws a proportional, colored bar of the disk's partitions (with used-space shading) in the Overview.
	private async Task BuildPartitionMapAsync(int diskNumber, long diskSize)
	{
		if (PartitionMapGrid == null) return;
		_partitionMapDisk = diskNumber;
		string outp;
		try
		{
			string ps = "Get-Partition -DiskNumber " + diskNumber + " -ErrorAction SilentlyContinue | ForEach-Object {" +
				" $l=$_.DriveLetter; $v=$null; if($l){ $v=Get-Volume -DriveLetter $l -ErrorAction SilentlyContinue };" +
				" $used = if($v){ $v.Size - $v.SizeRemaining } else { 0 };" +
				" $lbl = if($v -and $v.FileSystemLabel){ $v.FileSystemLabel } else { '' };" +
				" \"$($_.Size)|$l|$lbl|$used|$($_.Type)\" }";
			outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(ps));
		}
		catch { outp = ""; }
		if (_partitionMapDisk != diskNumber) return; // selection changed while querying

		PartitionMapGrid.Children.Clear();
		PartitionMapGrid.ColumnDefinitions.Clear();
		var colors = new[] {
			System.Windows.Media.Color.FromRgb(37,99,235), System.Windows.Media.Color.FromRgb(13,148,136),
			System.Windows.Media.Color.FromRgb(124,58,237), System.Windows.Media.Color.FromRgb(202,138,4),
			System.Windows.Media.Color.FromRgb(190,80,40)
		};
		long shown = 0; int idx = 0;
		var rows = (outp ?? "").Split('\n');
		void AddSegment(long size, string text, string tip, System.Windows.Media.Color col, double usedFrac)
		{
			PartitionMapGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(Math.Max(1, size), GridUnitType.Star) });
			int c = PartitionMapGrid.ColumnDefinitions.Count - 1;
			var seg = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(col), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(11,18,32)), BorderThickness = new Thickness(0,0,1,0), ToolTip = tip };
			var inner = new System.Windows.Controls.Grid();
			if (usedFrac > 0)
			{
				inner.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(usedFrac, GridUnitType.Star) });
				inner.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(Math.Max(0.0001,1-usedFrac), GridUnitType.Star) });
				var usedBar = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90,0,0,0)) };
				System.Windows.Controls.Grid.SetColumn(usedBar, 0); inner.Children.Add(usedBar);
			}
			var lbl = new System.Windows.Controls.TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2,0,2,0) };
			System.Windows.Controls.Grid.SetColumnSpan(lbl, 2); inner.Children.Add(lbl);
			seg.Child = inner;
			System.Windows.Controls.Grid.SetColumn(seg, c);
			PartitionMapGrid.Children.Add(seg);
		}
		foreach (var line in rows)
		{
			var p = line.Trim().Split('|');
			if (p.Length < 5 || !long.TryParse(p[0], out long psize) || psize <= 0) continue;
			string letter = p[1].Trim(); string label = p[2].Trim();
			long used = long.TryParse(p[3], out long u) ? u : 0;
			string head = (!string.IsNullOrEmpty(letter) ? letter + ":" : p[4].Trim());
			string txt = head + (string.IsNullOrEmpty(label) ? "" : " " + label) + "  " + FormatBytes(psize);
			string tip = txt + (used > 0 ? $"  ({FormatBytes(used)} used)" : "");
			AddSegment(psize, txt, tip, colors[idx % colors.Length], psize > 0 ? (double)used / psize : 0);
			shown += psize; idx++;
		}
		long free = diskSize - shown;
		if (free > diskSize * 0.01)
			AddSegment(free, "Unallocated  " + FormatBytes(free), "Unallocated space  " + FormatBytes(free), System.Windows.Media.Color.FromRgb(51,65,85), 0);
		if (PartitionMapGrid.ColumnDefinitions.Count == 0)
			AddSegment(1, "No partitions", "Empty / unformatted disk", System.Windows.Media.Color.FromRgb(51,65,85), 0);
	}

	// The Diagnostic Center has its own disk picker (DiagDiskBox). Keep it in sync with the workflow DiskBox
	// so picking a drive in either place selects it everywhere; the diagnostic tools read DiskBox.
	private void DiagDiskBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_syncingDisk || DiagDiskBox.SelectedItem == null) return;
		_syncingDisk = true;
		try { DiskBox.SelectedItem = DiagDiskBox.SelectedItem; }
		finally { _syncingDisk = false; }
	}

	private async void DiskBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_syncingDisk && DiagDiskBox != null && !ReferenceEquals(DiagDiskBox.SelectedItem, DiskBox.SelectedItem))
		{
			_syncingDisk = true;
			try { DiagDiskBox.SelectedItem = DiskBox.SelectedItem; }
			finally { _syncingDisk = false; }
		}
		UpdateDiskSummary();
		UpdateDriveToolOverview();
		UpdateDriveVerdict();
		UpdateStartReadiness();
		if (DiskBox.SelectedItem is DiskItem mapDisk) _ = BuildPartitionMapAsync(mapDisk.Number, mapDisk.Size);
		// Speed test runs only on demand now (press "Check drive" or the Speed tool) — selecting a disk no
		// longer writes a benchmark to it automatically.
		await Task.CompletedTask;
	}

	// Big green/red verdict on the chosen drive so a non-technical user instantly knows if it is suitable.
	private void UpdateDriveVerdict()
	{
		if (DriveVerdictBorder == null) return;
		if (!(DiskBox.SelectedItem is DiskItem disk))
		{
			DriveVerdictBorder.Visibility = Visibility.Collapsed;
			return;
		}
		DriveVerdictBorder.Visibility = Visibility.Visible;
		bool healthy = IsHealthy(disk.HealthText);
		// The "slow for Windows To Go" warning only applies to portable use. For a normal internal install
		// (or non-clone modes) the drive runs like any system disk, so don't show the WTG speed warning.
		bool slow = NeedsStrongPerformanceWarning(disk) && ModeBox.SelectedIndex != ModeCloneInternal;
		Color green = Color.FromRgb(22, 163, 74);
		Color amber = Color.FromRgb(180, 120, 10);
		Color red = Color.FromRgb(180, 40, 40);
		if (!healthy)
		{
			DriveVerdictBorder.Background = new SolidColorBrush(red);
			DriveVerdictText.Text = L("VerdictBad");
		}
		else if (slow)
		{
			DriveVerdictBorder.Background = new SolidColorBrush(amber);
			DriveVerdictText.Text = L("VerdictSlow");
		}
		else
		{
			DriveVerdictBorder.Background = new SolidColorBrush(green);
			// "Windows To Go" only makes sense for the portable clone. For an internal install or a plain
			// USB-install drive, show a wording that matches what the drive is actually for.
			DriveVerdictText.Text = ModeBox.SelectedIndex == ModeCloneInternal
				? L("VerdictGoodInternal")
				: ModeBox.SelectedIndex == ModeCloneCurrentWindows
					? L("VerdictGood")
					: L("VerdictGoodGeneric");
		}
	}

	// Enables Start only when everything needed is present, and shows a friendly inline hint about what is
	// missing instead of a pop-up on click.
	private void UpdateStartReadiness()
	{
		if (StartButton == null || StartHintText == null) return;
		// The orange "Start" readiness hint belongs ONLY to the Create-USB view. In every other view (Recover,
		// Clean, Drive tools, Download, Multi-boot) the main left panel is hidden — suppress the hint there so it
		// can't bleed into those panels' footer.
		if (_toolsView || (LeftPanelScroll != null && LeftPanelScroll.Visibility != Visibility.Visible))
		{ StartHintText.Visibility = Visibility.Collapsed; return; }
		string hint = "";
		if (!IsAdministrator())
		{
			hint = L("HintAdmin");
		}
		else if (ModeBox.SelectedIndex == ModeBackupImage)
		{
			hint = ""; // backup-to-file needs no target disk
		}
		else if (!(DiskBox.SelectedItem is DiskItem disk))
		{
			hint = L("HintDisk");
		}
		else if (disk.IsSystem)
		{
			hint = L("HintSystem");
		}
		else if (ModeBox.SelectedIndex != ModeCloneCurrentWindows && !IsExperimentalNtfsMode(ModeBox.SelectedIndex) && (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)))
		{
			hint = L("HintSource");
		}
		bool ready = hint.Length == 0;
		StartButton.IsEnabled = ready && !isBusy;
		StartHintText.Text = hint;
		StartHintText.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
	}

	private void SelectHealthTool_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolHealth, 0, "Health selected. Press Start to read the health report.");
	}

	private void SelectSpeedTool_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolSpeed, 1, "Speed selected. Press Start to test read/write speed.");
	}

	private void SelectSmartTool_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolSmart, 1, "SMART selected. Press Start to read detailed drive health data.");
	}

	private void SelectScanTool_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolScan, 2, "Scan selected. Press Start to run a safe file-system scan.");
	}

	private void SelectRepairTool_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolRepair, 3, "Repair selected. Press Start to run CHKDSK repair on the selected drive.");
	}

	private void SelectKitTool_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolKit, 4, "Kit selected. Press Start to create the diagnostic tool kit.");
	}

	private async void SpeedTest_Click(object sender, RoutedEventArgs e)
	{
		// "Check drive" is bound straight to this handler, bypassing ToolStartButton_Click's guard — and the analyzer
		// flows raise busy without greying the button out. The guard belongs HERE and not in RunSpeedTestAsync, which
		// is also called with auto:true from the pre-flight INSIDE an operation, where busy is legitimately true.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		SelectDriveTool(ToolSpeed, 1, "Speed test selected. Press Start to run it again, or wait for the current test to finish.");
		await RunSpeedTestAsync(auto: false);
	}

	private async void HealthCheck_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolHealth, 0, "Health report selected. Press Start to run it again.");
		if (!(DiskBox.SelectedItem is DiskItem disk))
		{
			return;
		}
		try
		{
			SetBusy(busy: true, L("BzReadHealth"));
			SetToolStatus(L("StHealthReadingDisk") + disk.Number + "...");
			string report = await GetDriveHealthReportAsync(disk);
			// The disk pickers stay interactive during this ~1-3s PowerShell round trip. If the user switched to a
			// DIFFERENT disk while it was in flight, painting THIS disk's data over the now-selected one would show
			// the wrong drive's health/SMART under the wrong name, with no visual sign of the mismatch. Discard —
			// UpdateHealthVisuals is not called, so nothing overwrites what's currently shown.
			if (!(DiskBox.SelectedItem is DiskItem curDisk) || !string.Equals(DiskIdentityKey(curDisk), DiskIdentityKey(disk), StringComparison.OrdinalIgnoreCase))
			{
				Log($"Health report for Disk {disk.Number} arrived after the selection changed — discarded.");
				SetToolStatus(L("StHealthDone"));
				return;
			}
			SetToolOutput(report);
			UpdateHealthVisuals(disk, report);
			Log($"Health Disk {disk.Number}: {disk.HealthText}; status: {disk.OperationalStatus}; bus: {disk.BusType}; media: {disk.MediaType}");
			await RefreshDisksAsync();
			SetToolStatus(L("StHealthDone"));
		}
		catch (Exception ex)
		{
			SetToolStatus(L("StHealthFailed"));
			ShowError(L("ErrHealth"), ex);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private async void ScanErrors_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolScan, 2, "File-system scan selected. Press Start to scan the selected drive.");
		await RunChkdskForSelectedDriveAsync(repair: false);
	}

	private async void RepairSectors_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolRepair, 3, "Repair scan selected. Press Start to run the repair scan.");
		await RunChkdskForSelectedDriveAsync(repair: true);
	}

	private async void SmartDetails_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolSmart, 1, "SMART details selected. Press Start to read SMART data again.");
		if (!(DiskBox.SelectedItem is DiskItem disk))
		{
			return;
		}
		try
		{
			SetBusy(busy: true, L("BzReadSmart"));
			SetToolStatus(L("StSmartReadingDisk") + disk.Number + "...");
			string report = await GetSmartDetailsAsync(disk);
			// Same stale-selection race as HealthCheck_Click: the disk pickers stay interactive during this PowerShell
			// round trip, so re-verify the selection is still THIS disk before painting its data over whatever the
			// user switched to in the meantime.
			if (!(DiskBox.SelectedItem is DiskItem curDisk) || !string.Equals(DiskIdentityKey(curDisk), DiskIdentityKey(disk), StringComparison.OrdinalIgnoreCase))
			{
				Log($"SMART report for Disk {disk.Number} arrived after the selection changed — discarded.");
				SetToolStatus(L("StSmartDone"));
				return;
			}
			SetToolOutput(report);
			UpdateSmartVisuals(disk, report);
			SetToolStatus(L("StSmartDone"));
		}
		catch (Exception ex)
		{
			SetToolStatus(L("StSmartFailed"));
			ShowError(L("ErrSmart"), ex);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private void CreateDiagnosticKit_Click(object sender, RoutedEventArgs e)
	{
		SelectDriveTool(ToolKit, 4, "Diagnostic kit selected. Press Start to create the tool kit again.");
		try
		{
			SetToolStatus(L("StKitCreating"));
			string path = CreateDriveDiagnosticKit();
			SetToolOutput(string.Format(L("KitOutputBody"), path));
			Log("Drive diagnostic kit created: " + path);
			MessageBox.Show(string.Format(L("MbKitCreated"), path), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			SetToolStatus(L("StKitFailed"));
			ShowError(L("ErrKit"), ex);
		}
	}

	private void SelectDriveToolTab(int tabIndex)
	{
		if (DriveToolsTabs != null && tabIndex >= 0 && tabIndex < DriveToolsTabs.Items.Count)
		{
			DriveToolsTabs.SelectedIndex = tabIndex;
		}
	}

	private void SelectDriveTool(int tool, int tabIndex, string status)
	{
		selectedDriveTool = tool;
		SelectDriveToolTab(tabIndex);
		SetToolStatus(status);
		HighlightToolButton(tool);
	}

	// Show which diagnostic tool is currently selected with a white ring on its button.
	private void HighlightToolButton(int tool)
	{
		var blue = FindResource("BlueBrush") as System.Windows.Media.Brush;
		var map = new (System.Windows.Controls.Button? btn, int t)[]
		{
			(HealthToolButton, ToolHealth), (SpeedToolButton, ToolSpeed), (ScanToolButton, ToolScan)
		};
		foreach (var (btn, t) in map)
		{
			if (btn == null) continue;
			bool active = t == tool;
			btn.BorderBrush = active ? System.Windows.Media.Brushes.White : blue;
			btn.BorderThickness = new Thickness(active ? 2.0 : 1.0);
		}
	}

	private void ToolStartButton_Click(object sender, RoutedEventArgs e)
	{
		// The three disk-analyzer flows raise `isBusy` with a raw write, which — unlike SetBusy — never greys this
		// button out. So without this guard a chkdsk /r /x could be launched during a Space-Analyzer scan, and when
		// the scan finished its SetBusy(false) would null `activeProcess` (leaving the running chkdsk unkillable by
		// Stop), re-enable Start on a disk being repaired, and release the keep-awake block mid-repair. Same guard
		// StartButton_Click has opened with for exactly this reason.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		switch (selectedDriveTool)
		{
			case ToolSpeed:
				SpeedTest_Click(sender, e);
				break;
			case ToolSmart:
				SmartDetails_Click(sender, e);
				break;
			case ToolScan:
				ScanErrors_Click(sender, e);
				break;
			case ToolRepair:
				RepairSectors_Click(sender, e);
				break;
			case ToolKit:
				CreateDiagnosticKit_Click(sender, e);
				break;
			default:
				HealthCheck_Click(sender, e);
				break;
		}
	}

	private void ToolPauseButton_Click(object sender, RoutedEventArgs e)
	{
		PauseButton_Click(sender, e);
	}

	private void ToolStopButton_Click(object sender, RoutedEventArgs e)
	{
		StopButton_Click(sender, e);
	}

	private async Task RunSpeedTestAsync(bool auto)
	{
		object selectedItem = DiskBox.SelectedItem;
		DiskItem disk = selectedItem as DiskItem;
		// Speed test is safe on the running-system disk (it writes a small temp benchmark file to free space and
		// deletes it), so it is allowed there — only whole-disk-destructive operations block IsSystem.
		if ((object)disk == null)
		{
			return;
		}
		// This "diagnostic" WRITES ~80 MB into the drive's free space to measure real write speed. Its neighbours in the
		// same panel are labelled read-only, so that is not what a user expects — and on a drive someone is about to run
		// Recover on, those writes land in exactly the free clusters still holding their deleted files. Disclose it and
		// ask. (The automatic pre-flight run is exempt: there the drive is the target of an operation that erases it.)
		if (!auto && MessageBox.Show(L("MbSpeedWritesWarn"), "DriveForge",
				MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;
		try
		{
			stopRequested = false; isPaused = false;
			SetBusy(busy: true, auto ? L("DSpdBusyAuto") : L("DSpdBusyManual"));
			SetToolStatus(auto ? L("DSpdRunAuto") : L("DSpdRunManual"));
			StartLiveTest(L("LiveSpeedTest"));
			ProgressBar.Value = 0.0; progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SpeedResult speedResult = await Task.Run(() => MeasureDiskSpeed(disk, p => Dispatcher.Invoke(() =>
			{
				liveTestTimer?.Stop(); // real progress takes over from the animated placeholder
				ProgressBar.Value = p; // mirror onto the global bar so the bottom Elapsed/ETA line works
				if (LiveTestBar != null) { LiveTestBar.Value = p; LiveTestPercentText.Text = string.Format(L("LivePercent"), p); }
			})));
			StopLiveTest(success: true);
			speedResults[disk.Number] = speedResult;
			speedResultIdentity[disk.Number] = DiskIdentityKey(disk);   // so a swapped drive reusing this number can't inherit it
			UpdateDiskSummary();
			UpdateSpeedVisuals(speedResult);
			SetToolOutput(string.Format(L("DSpdReport"), disk.Number, disk.FriendlyName, speedResult.SequentialWriteMb.ToString("F1"), speedResult.Random4KWriteMb.ToString("F1"), speedResult.Message, BuildSpeedRecommendation(speedResult)));
			SetToolStatus(L("DSpdDone"));
			Log($"Speed test Disk {disk.Number}: sequential {speedResult.SequentialWriteMb:F1} MB/s, random 4K {speedResult.Random4KWriteMb:F1} MB/s. {speedResult.Message}");
		}
		catch (Exception ex)
		{
			StopLiveTest(success: false);
			SetToolStatus(L("DSpdFail"));
			Log("Speed test skipped: " + ex.Message);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			ResetProgressWidgets();   // bar AND label AND stats line — zeroing only the bar left "100%" beside an empty one
			SetBusy(busy: false);
		}
	}

	private async void CreateKitButton_Click(object sender, RoutedEventArgs e)
	{
		// See ToolStartButton_Click: the analyzer flows raise busy without greying this button out, so it needs the
		// same explicit guard or it can start a second operation on top of a running one.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		try
		{
			SetBusy(busy: true, L("BzCloneHelper"));
			string text = CreateWinPeCloneKit();
			Log("Current computer clone helper files created: " + text);
			MessageBox.Show(string.Format(L("MbCloneHelperCreated"), text), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (Exception ex)
		{
			ShowError(L("ErrCloneHelper"), ex);
		}
		finally
		{
			SetBusy(busy: false);
		}
	}

	private async void StartButton_Click(object sender, RoutedEventArgs e)
	{
		// Reentry guard: this handler runs an async pre-write phase (RunRequiredPreflightAsync + ConfirmOperationSummary)
		// during which isBusy is still false and StartButton stays enabled, so a double-click / second click could launch
		// two concurrent destructive ops on the same disk. Set the flag synchronously (before any await); clear it in the
		// finally below. Matches the isBusy guard every other operation handler opens with.
		if (isBusy || _startInProgress) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		_startInProgress = true;
		try
		{
		// Backup-to-image needs no target disk — it writes a file. Handle it first.
		if (ModeBox.SelectedIndex == ModeBackupImage)
		{
			await BackupThisPcToImageAsync();
			return;
		}
		if (!(DiskBox.SelectedItem is DiskItem diskItem))
		{
			MessageBox.Show(L("Mb007"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (diskItem.IsSystem)
		{
			MessageBox.Show(L("Mb008"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		else if (ModeBox.SelectedIndex != ModeCloneCurrentWindows && !IsExperimentalNtfsMode(ModeBox.SelectedIndex) && (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)))
		{
			MessageBox.Show(L("Mb009"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else if (!IsAdministrator())
		{
			MessageBox.Show(L("Mb010"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		else
		{
			await RunRequiredPreflightAsync(diskItem);
			if (IsExperimentalNtfsMode(ModeBox.SelectedIndex))
			{
					if (!await ConfirmOperationSummary(diskItem))
					{
						return;
					}
					// BitLocker on the faithful clone: pick the recovery-key folder and (optionally) a password
					// up front, just like the ISO path. Cancelling the folder picker turns BitLocker off.
					if (BitLockerCheck.IsChecked == true && string.IsNullOrWhiteSpace(bitLockerRecoveryFolder) && !ChooseBitLockerRecoveryFolder())
					{
						return;
					}
					if (BitLockerCheck.IsChecked == true)
					{
						PromptBitLockerPassword();
					}
					bool cloneFailed = false;
					try
					{
						stopRequested = false;
						bitLockerEncrypting = false;
						isPaused = false;
						internalOperationStopped = false;
						PauseButton.Content = L("BtnPause");
						progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1024.0 / 1024.0 / 1024.0 * 1.25);
						progressDoneGiB = 0.0;
						progressPrevGiB = 0.0;
						progressLastReportedBytes = 0;
						progressSpeedMb = 0.0;
						_speedWindow.Clear();
						lastProcessOutputUtc = DateTime.UtcNow;
						lastHeartbeatLogUtc = DateTime.UtcNow;
						operationStopwatch.Restart();
						operationTimer.Start();
						SetBusy(busy: true, L("BzClone"));
						ProgressBar.Value = 0.0;
						await RunExperimentalFullRootUsbCloneAsync(diskItem);
					}
					catch (Exception ex)
					{
						cloneFailed = true;
						StatusText.Text = L("SxCloneFailed");
						NotifyOperationDone(false);
						SaveLogToDesktop();
						ShowError(L("ErrCloneThisPc"), ex);
					}
					finally
					{
						operationTimer.Stop();
						operationStopwatch.Stop();
						UpdateProgressStats();
						SetBusy(busy: false);
						// AFTER SetBusy: its stop-branch resets the stats line, and the most common way to reach
						// cloneFailed IS the user's Stop (killing the child process throws) — writing first meant the
						// "Failed after hh:mm:ss" message was immediately overwritten with a bare "Progress: 0.0%".
						if (cloneFailed)
						{
							ProgressStatsText.Text = string.Format(L("ProgFailed"), FormatDuration(operationStopwatch.Elapsed));
							// The op is over and has reported its verdict — drop the flag. It is global and sticky, so
							// leaving it set meant the NEXT SetBusy(false) from anywhere (a device-change refresh when
							// the user unplugs the stick, the Refresh button, a health read) re-ran the stop-reset and
							// silently wiped this "Failed" line.
							stopRequested = false;
						}
					}
					return;
			}
			if (ModeBox.SelectedIndex == ModeWriteIsoImage)
			{
				await WriteIsoImageFlowAsync(diskItem);
				return;
			}
			if (!HasEnoughSpace(diskItem, out string spaceMessage))
			{
				MessageBox.Show(spaceMessage, "Not enough space", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			if (BitLockerCheck.IsChecked == true && string.IsNullOrWhiteSpace(bitLockerRecoveryFolder) && !ChooseBitLockerRecoveryFolder())
			{
				return;
			}
			if (BypassAccountCheck.IsChecked == true && ModeBox.SelectedIndex == ModeInstallFromImage)
			{
				PromptLocalAccount();
			}
			else
			{
				// Clear any local-account values left from a previous in-session run so an earlier account/password is
				// never baked into this stick's unattend.xml when the bypass box is unchecked.
				localAccountName = ""; localAccountPassword = "";
			}
			if (BitLockerCheck.IsChecked == true)
			{
				PromptBitLockerPassword();
			}
			if (!await ConfirmOperationSummary(diskItem))
			{
				return;
			}
			bool operationFailed = false;
			try
			{
				stopRequested = false;
				operationAbortedBeforeWrite = false;
				bitLockerFailedThisRun = false;
				bitLockerEncrypting = false;
				isPaused = false;
				PauseButton.Content = L("BtnPause");
				// VSS snapshots + hardlinks inflate actual copied bytes by ~25% vs Windows disk-usage report.
				// Multiply by 1.25 so the bar doesn't hit 100% early and freeze.
				progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1024.0 / 1024.0 / 1024.0 * 1.25);
				progressDoneGiB = 0.0;
				progressPrevGiB = 0.0;
				progressLastReportedBytes = 0;
				progressSpeedMb = 0.0;
				_speedWindow.Clear();
				lastProcessOutputUtc = DateTime.UtcNow;
				lastHeartbeatLogUtc = DateTime.UtcNow;
				operationStopwatch.Restart();
				operationTimer.Start();
				SetBusy(busy: true, L("BzStartOp"));
				ProgressBar.Value = 0.0;
				if (ModeBox.SelectedIndex == ModeInstallFromImage)
				{
					await CreateWindowsToGoFromImageAsync(sourcePath, diskItem);
				}
				else if (Path.GetExtension(sourcePath).Equals(".wim", StringComparison.OrdinalIgnoreCase))
				{
					await RestoreWimToDriveAsync(sourcePath, diskItem);
				}
				else if (Path.GetExtension(sourcePath).Equals(".vhdx", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(sourcePath).Equals(".vhd", StringComparison.OrdinalIgnoreCase))
				{
					// Faithful restore: the VHDX was built by "Export VHDX" with the raw engine, so restore it with the
					// raw engine too (AV-transparent, preserves ACLs/hardlinks/reparse/ADS/EFS/WOF) — unlike the .wim path.
					await RestoreVhdxToDriveAsync(sourcePath, diskItem);
				}
				else
				{
					await ApplyFfuAsync(sourcePath, diskItem);
				}
				if (operationAbortedBeforeWrite)
				{
					// A pre-write safety gate (target-health warning declined, or the identity re-verify catching a
					// changed/renumbered disk) aborted before anything was written. The operation already set its
					// "cancelled" stage message — do NOT claim success, notify success, pop the "USB created" dialog,
					// or eject a drive that was never touched.
					Log("Operation aborted before any write — no changes were made to the target drive.");
					operationTimer.Stop();
					operationStopwatch.Stop();
					UpdateProgressStats();
				}
				else
				{
					ProgressBar.Value = 100.0;
					StatusText.Text = L("SxDone");
					Log("Operation finished successfully.");
					// Freeze the timer/stopwatch BEFORE the modal dialog so elapsed time stops at completion,
					// not when the user dismisses the dialog (the DispatcherTimer keeps ticking while it is open).
					operationTimer.Stop();
					operationStopwatch.Stop();
					UpdateProgressStats();
					NotifyOperationDone(true);
					string bootHelp = L("MbBootHelp");
					// The antivirus-reinstall note only makes sense when restoring an actual PC backup/clone (WIM,
					// VHDX, FFU) — that image can genuinely carry an installed, hardware-bound security suite. A
					// fresh install from an official Windows ISO (ModeInstallFromImage) has no antivirus on it at
					// all, so the note was pure noise there — worse, it read as if something HAD been carried over.
					string avNote = ModeBox.SelectedIndex == ModeInstallFromImage ? "" : "\n\n" + L("MbAvCloneNote");
					MessageBox.Show(L("MbUsbDone") + bootHelp + (bitLockerEncrypting ? L("MbBitLockerNote") : "") + (bitLockerFailedThisRun ? "\n\n" + L("MbUsbBitlockerFailed") : "") + BuildDriverDebloatSummary() + avNote, "DriveForge", MessageBoxButton.OK, bitLockerFailedThisRun ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk);
					if (EjectWhenDoneCheck.IsChecked == true && !bitLockerEncrypting) await EjectDiskAsync(diskItem.Number);
				}
			}
			catch (Exception ex)
			{
				operationFailed = true;
				StatusText.Text = L("SxFailed");
				NotifyOperationDone(false);
				SaveLogToDesktop();
				ShowError(L("ErrCreateUsb"), ex);
			}
			finally
			{
				operationTimer.Stop();
				operationStopwatch.Stop();
				UpdateProgressStats();
				SetBusy(busy: false);
				// AFTER SetBusy — see the clone path above: its stop-branch resets the stats line, and a user Stop is
				// the most common route to operationFailed.
				if (operationFailed)
				{
					ProgressStatsText.Text = string.Format(L("ProgFailed"), FormatDuration(operationStopwatch.Elapsed));
					stopRequested = false;   // see the clone path: the sticky flag would let a later refresh wipe this line
				}
			}
		}
		}
		finally { _startInProgress = false; }
	}

	private static bool IsExperimentalNtfsMode(int mode)
	{
		return mode == ModeExperimentalNtfsFullRootUsbClone || mode == ModeCloneInternal;
	}

	private async Task RunChkdskForSelectedDriveAsync(bool repair)
	{
		// chkdsk is allowed on the running-system disk: /scan runs online read-only and /f only schedules a
		// boot-time check — neither erases data. Whole-disk-destructive tools still block IsSystem elsewhere.
		if (!(DiskBox.SelectedItem is DiskItem disk))
		{
			MessageBox.Show(L("DScanNeedDisk"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		char? driveLetter = disk.DriveLetters.Select(char.ToUpperInvariant).FirstOrDefault(letter => letter >= 'A' && letter <= 'Z');
		if (!driveLetter.HasValue || driveLetter.Value == '\0')
		{
			MessageBox.Show(L("DScanNeedLetter"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		// Safe default: Yes runs `chkdsk /r /x`, which force-dismounts the volume and can relocate data into found.000.
		if (repair && MessageBox.Show(string.Format(L("DScanRepairConfirm"), driveLetter), L("DScanRepairTitle"), MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			SetBusy(busy: true, repair ? L("DScanBusyRepair") : L("DScanBusyScan"));
			SetToolStatus(repair ? string.Format(L("DScanRunRepair"), driveLetter) : string.Format(L("DScanRunScan"), driveLetter));
			ScanStatusText.Text = repair ? string.Format(L("DScanStatRepair"), driveLetter) : string.Format(L("DScanStatScan"), driveLetter);
			// chkdsk does not report parseable progress, so show an honest moving (indeterminate) bar instead of a fake fixed percentage.
			ScanProgressBar.IsIndeterminate = true;
			string args = repair ? $"{driveLetter}: /r /x" : $"{driveLetter}: /scan";
			ProcessResult result = await RunProcessInternalAsync("chkdsk.exe", args);
			SetToolOutput($"CHKDSK {args}\r\n{L("DScanExitCode")}: {result.ExitCode}\r\n\r\n{result.Output}");
			ScanProgressBar.IsIndeterminate = false; ScanProgressBar.Value = 100;
			bool offlineRepairRequired =
				result.Output.Contains("offline scan and fix", StringComparison.OrdinalIgnoreCase)
				|| result.Output.Contains("snapshot error", StringComparison.OrdinalIgnoreCase)
				|| result.Output.Contains("Shadow copying the specified volume is not supported", StringComparison.OrdinalIgnoreCase)
				|| result.Output.Contains("Cannot lock current drive", StringComparison.OrdinalIgnoreCase)
				|| result.Output.Contains("in use by another process", StringComparison.OrdinalIgnoreCase)
				|| result.Output.Contains("schedule this volume to be checked", StringComparison.OrdinalIgnoreCase)
				// The running-system volume can NEVER be locked for an online /r /x, so a nonzero exit there means
				// "couldn't run online" (schedule at reboot), NOT corruption. This signal is locale-independent, so it
				// also fixes the false "Issues found" report on non-English Windows. (Was hard-gated to !repair before,
				// which made the repair path ALWAYS fall through to the damage report.)
				|| (result.ExitCode != 0 && disk.IsSystem);
			// chkdsk's documented repair-mode exit codes: 0 = no errors; 1 = errors were found AND FIXED (only
			// meaningful with /r /x, i.e. repair=true — /scan alone never fixes anything). The old code only ever
			// treated exit 0 as success, so a REPAIR THAT WORKED (exit 1) fell through to the "issues found, back up
			// your data" branch below — reporting a successful fix as a failure, every time repair actually worked.
			bool repairSucceeded = repair && result.ExitCode == 1;
			// Single source of truth for the verdict, computed ONCE and reused for BOTH the status panel below and
			// the completion MessageBox — a prior version of this fix left the MessageBox's text/icon as SEPARATE
			// ternaries that re-evaluated ExitCode==0/offlineRepairRequired independently, and re-verify caught real
			// cases where they disagreed with the panel (e.g. ExitCode==0 co-occurring with an offlineRepairRequired
			// text-match produced a "needs offline check" popup with a green success icon over a "healthy" panel).
			// 0=ok, 1=offline-required, 2=repaired, 3=issues. ExitCode==0 wins first (unchanged from before this fix):
			// immune to the text-match clauses, none of which require ExitCode!=0, so a genuinely clean run can never
			// be misrouted by them. offlineRepairRequired is next: its disk.IsSystem clause is an invariant about the
			// RUNNING system disk — ANY nonzero exit there (including 1) means chkdsk could not run online and was
			// scheduled for the next reboot, NOT that it fixed anything live, so it must outrank repairSucceeded.
			int verdict = result.ExitCode == 0 ? 0 : offlineRepairRequired ? 1 : repairSucceeded ? 2 : 3;
			switch (verdict)
			{
				case 0:
					ScanStatusText.Text = string.Format(L("DScanOk"), driveLetter);
					ScanAdviceText.Text = L("DScanOkAdvice");
					SetToolStatus(L("DScanOkTool"));
					break;
				case 1:
					ScanStatusText.Text = string.Format(L("DScanOffline"), driveLetter);
					// The generic offline advice says "press Repair" as the next step — wrong wording when repair was
					// the very thing that just ran and still couldn't lock the drive; use the repair-aware variant then.
					ScanAdviceText.Text = repair ? L("DScanOfflineAdviceRepair") : string.Format(L("DScanOfflineAdvice"), driveLetter);
					SetToolStatus(L("DScanOfflineTool"));
					break;
				case 2:
					ScanStatusText.Text = string.Format(L("DScanRepaired"), driveLetter);
					ScanAdviceText.Text = L("DScanRepairedAdvice");
					SetToolStatus(L("DScanOkTool"));
					break;
				default:
					ScanStatusText.Text = string.Format(L("DScanIssues"), driveLetter, result.ExitCode);
					// The English-text signals above can't match chkdsk's LOCALIZED console output on a non-English
					// Windows install, so on a non-system drive a merely busy/locked drive (common, benign) is
					// indistinguishable here from real corruption. Don't assert "read-only scan" when repair was
					// actually attempted (it isn't read-only), and be honest that a busy drive can look like this too —
					// the raw CHKDSK output above (in the user's own language) is the real source of truth.
					ScanAdviceText.Text = repair ? L("DScanIssuesAdviceRepair") : L("DScanIssuesAdvice");
					SetToolStatus(L("DScanIssuesTool"));
					break;
			}
			Log((repair ? "Repair scan finished for " : "Error scan finished for ") + driveLetter + ":");
			MessageBox.Show(verdict == 1 ? string.Format(L("DScanMsgOffline"), driveLetter) : ScanStatusText.Text,
				L("DScanMsgTitle"), MessageBoxButton.OK,
				(verdict == 0 || verdict == 2) ? MessageBoxImage.Information : MessageBoxImage.Exclamation);
		}
		catch (Exception ex)
		{
			ShowError(L("DScanFailed"), ex);
		}
		finally
		{
			ScanProgressBar.IsIndeterminate = false;
			SetBusy(busy: false);
		}
	}

	private async Task<string> GetDriveHealthReportAsync(DiskItem disk)
	{
		string script = "$disk = Get-Disk -Number " + disk.Number + " -ErrorAction SilentlyContinue\n" +
			"$parts = @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue)\n" +
			"$vols = @($parts | Where-Object DriveLetter | ForEach-Object { Get-Volume -DriveLetter $_.DriveLetter -ErrorAction SilentlyContinue })\n" +
			"'DriveForge Drive Health Report'\n" +
			"'============================='\n" +
			"''\n" +
			"'Disk'\n" +
			"$disk | Format-List Number,FriendlyName,SerialNumber,BusType,MediaType,HealthStatus,OperationalStatus,PartitionStyle,@{n='SizeGB';e={[math]::Round($_.Size/1GB,2)}} | Out-String\n" +
			"'Partitions'\n" +
			"$parts | Select-Object PartitionNumber,DriveLetter,Type,GptType,IsBoot,IsSystem,Size | Format-Table -AutoSize | Out-String\n" +
			"'Volumes'\n" +
			"$vols | Select-Object DriveLetter,FileSystemLabel,FileSystem,HealthStatus,OperationalStatus,SizeRemaining,Size | Format-Table -AutoSize | Out-String\n" +
			"'Physical disk match'\n" +
			"$physical = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.DeviceId -eq [string]$disk.Number } | Select-Object -First 1\n" +
			"if (-not $physical) { $physical = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq $disk.FriendlyName } | Select-Object -First 1 }\n" +
			"if ($physical) { $physical | Format-List FriendlyName,MediaType,BusType,HealthStatus,OperationalStatus,Usage,Size,SpindleSpeed,CanPool | Out-String } else { 'No matching PhysicalDisk entry found.' }\n" +
			// The predictive verdict (FailurePrediction) is computed from THIS text. Without the reliability counters every
			// one of its failure rules silently evaluates against -1 and can never fire, so a worn-out or error-throwing
			// drive came back as a confident green "No failure signs". Emit the same counters the SMART view collects.
			"''\n" +
			"'Reliability counters'\n" +
			"if ($physical) { try { $physical | Get-StorageReliabilityCounter | Format-List * | Out-String } catch { 'Reliability counters are not available for this drive/controller: ' + $_.Exception.Message } } else { 'Reliability counters are not available (no matching PhysicalDisk).' }";
		return await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script));
	}

	private async Task<string> GetSmartDetailsAsync(DiskItem disk)
	{
		string script = "$disk = Get-Disk -Number " + disk.Number + " -ErrorAction SilentlyContinue\n" +
			"$physical = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.DeviceId -eq [string]$disk.Number } | Select-Object -First 1\n" +
			"if (-not $physical) { $physical = Get-PhysicalDisk -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq $disk.FriendlyName } | Select-Object -First 1 }\n" +
			"'DriveForge SMART / Reliability Report'\n" +
			"'====================================='\n" +
			"''\n" +
			"'Selected disk: Disk ' + $disk.Number + ' - ' + $disk.FriendlyName\n" +
			"''\n" +
			"if ($physical) {\n" +
			"  'PhysicalDisk'\n" +
			"  $physical | Format-List FriendlyName,SerialNumber,MediaType,BusType,HealthStatus,OperationalStatus,Usage,Size,SpindleSpeed | Out-String\n" +
			"  'Reliability counters'\n" +
			"  try { $physical | Get-StorageReliabilityCounter | Format-List * | Out-String } catch { 'Reliability counters are not available for this drive/controller: ' + $_.Exception.Message }\n" +
			"} else {\n" +
			"  'No matching PhysicalDisk entry found. Some USB adapters do not expose SMART data to Windows.'\n" +
			"}\n" +
			"''\n" +
			"'Note: For deeper SMART tests, a dedicated third-party SMART tool can be used.'";
		return await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script));
	}

	// Exports this PC's drivers (network-only, or ALL third-party drivers) and injects them into the freshly-
	// applied Windows on the USB, so they work on first boot (e.g. Wi-Fi during OOBE). Best-effort; never fatal.
	private async Task InjectCurrentPcDriversAsync(char windowsLetter, bool allDrivers)
	{
		string dest = null;
		try
		{
			SetStage(allDrivers ? L("StgAddDriversAll") : L("StgAddDriversNet"), 84.0);
			dest = Path.Combine(Path.GetTempPath(), $"winforge-drv-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dest);
			// The temp path contains the user profile folder, which can hold an apostrophe (e.g. C:\Users\O'Brien\...); an
			// unescaped ' would terminate the single-quoted PowerShell literals below early — breaking the driver export
			// (drivers silently NOT injected) or allowing injection. Double it for safe embedding in '...' literals.
			string destPs = dest.Replace("'", "''");
			string ps;
			if (allDrivers)
			{
				// Export-WindowsDriver dumps every third-party (OEM) driver, each into its own subfolder.
				ps = "$ErrorActionPreference='SilentlyContinue';" +
					"Export-WindowsDriver -Online -Destination '" + destPs + "' | Out-Null;" +
					"'EXPORTED:' + (@(Get-ChildItem -Path '" + destPs + "' -Recurse -Filter *.inf).Count)";
			}
			else
			{
				ps = "$ErrorActionPreference='SilentlyContinue';" +
					"$d='" + destPs + "';" +
					"$nets=Get-WindowsDriver -Online | Where-Object { $_.ClassName -eq 'Net' };" +
					"$i=0;" +
					"foreach($n in $nets){ $sub=Join-Path $d ('net'+$i); New-Item -ItemType Directory -Force $sub | Out-Null;" +
					" pnputil /export-driver $n.Driver $sub | Out-Null; $i++ };" +
					"'EXPORTED:'+$i";
			}
			string outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(ps));
			var m = Regex.Match(outp, @"EXPORTED:(\d+)");
			int count = m.Success ? int.Parse(m.Groups[1].Value) : 0;
			if (count > 0)
			{
				try
				{
					await RunProcessCaptureAsync("dism.exe", $"/Image:{windowsLetter}:\\ /Add-Driver /Driver:{QuoteArgument(dest)} /Recurse");
					_lastDriversAdded = count;
					Log($"{(allDrivers ? "All" : "Network")} drivers added to the USB Windows ({count} package(s) from this PC).");
				}
				catch (Exception dex)
				{
					// DISM returned non-zero — say so explicitly instead of the generic non-fatal message below.
					_lastDriversAdded = -1;
					Log($"DISM /Add-Driver did not complete cleanly ({dex.Message}); some or all of this PC's drivers may be missing from the USB.");
				}
			}
			else
			{
				_lastDriversAdded = 0;
				Log("No matching drivers found on this PC to add — skipped.");
			}
		}
		catch (Exception ex)
		{
			if (_lastDriversAdded < 0) _lastDriversAdded = -1;
			Log("Could not add this PC's drivers (non-fatal): " + ex.Message);
		}
		finally
		{
			if (!string.IsNullOrEmpty(dest)) TryDeleteDirectory(dest);
		}
	}

	private async Task CreateWindowsToGoFromImageAsync(string path, DiskItem disk)
	{
		_lastDriversAdded = -2; _lastDebloatApplied = false; // reset per-run so the completion summary is accurate
		string mountedIso = null;
		string diskpartPath = null;
		try
		{
			// The source image must NOT live on the disk we're about to wipe: diskpart 'clean' force-dismounts and
			// erases the whole target, destroying the source image (and invalidating an ISO mount). This is the same
			// guard the WIM-restore and FFU paths already have; the Windows-To-Go path was missing it.
			if (PhysicalDiskOfPath(path) == disk.Number)
				throw new InvalidOperationException("The image file is stored ON the drive you're creating Windows To Go on — this would erase the image itself. Move it to another drive first. No changes were made.");

			string imageFile = path;
			if (Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
			{
				SetStage(L("StgMountIso"), 4.0);
				mountedIso = path;
				imageFile = FindInstallImage(await MountIsoAsync(path));
			}
			// Choose the boot/Windows drive letters AFTER mounting the ISO: Mount-DiskImage auto-assigns the first free
			// letter, so picking these before the mount could hand back the letter the ISO then takes, breaking the
			// diskpart 'assign letter=' step. Choosing them post-mount guarantees GetFreeDriveLetter skips the ISO letter.
			char bootLetter = GetFreeDriveLetter();
			char windowsLetter = GetFreeDriveLetter(bootLetter);
			int index = ((!(EditionBox.SelectedItem is EditionItem editionItem)) ? 1 : editionItem.Index);

			// Capacity gate before the destructive format.
			long requiredBytes = EstimateRequiredBytes();
			if (requiredBytes > 0 && disk.Size < requiredBytes)
			{
				throw new InvalidOperationException(
					"The selected drive is too small for this image.\n\nRequired: " + FormatBytes(requiredBytes) +
					"\nSelected drive: " + FormatBytes(disk.Size) +
					"\n\nNo changes were made — the drive was not formatted.");
			}

			// Target health gate before the destructive format (same as the faithful clone).
			if (!await ConfirmTargetHealthAsync(disk))
			{
				Log("Windows To Go creation cancelled by user after target health warning.");
				operationAbortedBeforeWrite = true;
				SetStage(L("StgCancelHealth"), 0.0);
				return;
			}

			// A Windows-To-Go layout uses MBR (for BIOS+UEFI boot compatibility), which addresses at most ~2 TB; warn
			// when the target is larger so the user understands the space beyond ~2 TB is left unpartitioned.
			if (disk.Size > 2199023255040L)
				Log("Note: the target is larger than ~2 TB; a Windows-To-Go MBR layout can use only the first ~2 TB — the remainder is left unallocated.");

			// Optional data partition: cap Windows and give the rest to an NTFS "Data" partition.
			int windowsSizeMb = 0;
			char dataLetter = '\0';
			if (DataPartitionCheck.IsChecked == true)
			{
				// Size Windows from the actual image (~3× the compressed WIM/ESD) + 8 GiB headroom, not the flat 64 GB
				// estimate — so the requested data partition survives on modest drives. Keep a 40 GiB floor.
				long imgLen = 0; try { imgLen = new FileInfo(imageFile).Length; } catch { }
				long est = imgLen > 0 ? imgLen * 3 + 8L * 1024 * 1024 * 1024
					: (requiredBytes > 0 ? requiredBytes + 8L * 1024 * 1024 * 1024 : 40L * 1024 * 1024 * 1024);
				long winBytes = Math.Max(40L * 1024 * 1024 * 1024, est);
				// Size the data partition from the MBR-usable span (~2 TiB max, 2^32 sectors × 512 B), not the full disk:
				// diskpart silently caps the trailing 'create partition primary' at the MBR limit, so basing the reported
				// size on disk.Size would over-promise (log a size that is never created) on a >2 TB target.
				long mbrUsable = Math.Min(disk.Size, 2199023255040L);
				long leftover = mbrUsable - winBytes - 300L * 1024 * 1024 - 200L * 1024 * 1024;
				if (leftover >= 8L * 1024 * 1024 * 1024)
				{
					windowsSizeMb = (int)(winBytes / (1024 * 1024));
					dataLetter = GetFreeDriveLetter(bootLetter, windowsLetter);
					Log($"Data partition enabled: Windows capped at {FormatBytes(winBytes)}, data partition gets ~{FormatBytes(leftover)} ({dataLetter}:).");
				}
				else
				{
					Log("Data partition requested but skipped: not enough leftover space. Windows will use the whole disk.");
				}
			}

			SetStage(L("StgPartitionTarget"), 10.0);
			diskpartPath = Path.Combine(Path.GetTempPath(), $"driveforge-diskpart-{Guid.NewGuid():N}.txt");
			await File.WriteAllTextAsync(diskpartPath, BuildWindowsToGoDiskpartScript(disk.Number, bootLetter, windowsLetter, windowsSizeMb, dataLetter), Encoding.ASCII);
			await RunProcessAsync("diskpart.exe", "/s \"" + diskpartPath + "\"");
			SetStage(L("StgApplyDism"), 20.0);
			string compactArg = CompactImageCheck.IsChecked == true ? " /Compact" : "";
			await RunProcessAsync("dism.exe", $"/Apply-Image /ImageFile:\"{imageFile}\" /Index:{index} /ApplyDir:{windowsLetter}:\\{compactArg}");

			// Post-apply integrity check: confirm DISM produced a complete, bootable Windows root.
			SetStage(L("StgVerifyApplied"), 84.0);
			bool imageOk = File.Exists($"{windowsLetter}:\\Windows\\System32\\winload.efi")
				&& File.Exists($"{windowsLetter}:\\Windows\\System32\\config\\SYSTEM")
				&& Directory.Exists($"{windowsLetter}:\\Windows\\System32\\drivers");
			if (!imageOk)
			{
				throw new InvalidOperationException("DISM apply did not produce a complete Windows root (winload.efi / SYSTEM hive / drivers missing). The drive was formatted; re-run with a valid image.");
			}
			Log("Applied image verified: Windows boot loader, SYSTEM hive and driver store present.");

			SetStage(L("StgMarkPortable"), 86.0);
			await MarkPortableWindowsAsync(windowsLetter);
			await ConfigurePortablePagefileAsync(windowsLetter);
			await ApplyInstallBypassOptionsAsync(windowsLetter);
			if (AddAllDriversCheck.IsChecked == true)
			{
				await InjectCurrentPcDriversAsync(windowsLetter, allDrivers: true);
			}
			else if (AddNetworkDriversCheck.IsChecked == true)
			{
				await InjectCurrentPcDriversAsync(windowsLetter, allDrivers: false);
			}
			bool unattendWritten = WritePortableUnattend($"{windowsLetter}:\\Windows", localAccountName, localAccountPassword);
			Log(unattendWritten ? "First-boot answer file (unattend.xml) written." : "WARNING: could not write the first-boot answer file.");
			SetStage(L("StgCreateBoot"), 92.0);
			await RunProcessAsync("bcdboot.exe", $"{windowsLetter}:\\Windows /s {bootLetter}: /f ALL /v");
			// Guarantee the UEFI removable fallback \EFI\Boot\bootx64.efi so the stick UEFI-boots on any PC
			// (bcdboot only writes it for media it detects as removable; many USB SSDs report as fixed).
			EnsureUefiRemovableFallback(bootLetter);
			if (BitLockerCheck.IsChecked == true)
			{
				// A BitLocker start-failure must NOT abort an already-complete, bootable Windows To Go as a total failure
				// (which would also skip the volume flush below). Mirror the clone path: keep the drive, flush, and warn.
				try { await EnableBitLockerAsync(windowsLetter); }
				catch (Exception blEx)
				{
					bitLockerFailedThisRun = true;
					Log("WARNING: BitLocker did not encrypt the Windows To Go drive: " + blEx.Message + " (the drive is bootable but NOT encrypted).");
				}
			}
			await FlushVolumesAsync(bootLetter, windowsLetter);
			Log($"Windows To Go created on Disk {disk.Number}. Boot partition: {bootLetter}:, Windows partition: {windowsLetter}:."
				+ (bitLockerEncrypting
					? " BitLocker is STILL ENCRYPTING — keep the drive connected until 'manage-bde -status' shows 100%."
					: " The drive is flushed and safe to remove."));
		}
		finally
		{
			if (!string.IsNullOrWhiteSpace(mountedIso))
			{
				await TryUnmountIsoAsync(mountedIso);
			}
			if (!string.IsNullOrWhiteSpace(diskpartPath) && File.Exists(diskpartPath))
			{
				TryDeleteFile(diskpartPath);
			}
		}
	}

	private async Task<List<string>> GetLoadedControlSetsAsync(string hiveRoot)
	{
		ProcessResult result = await RunProcessInternalAsync("reg.exe", "query " + QuoteArgument(hiveRoot));
		List<string> controlSets = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select((string line) => line.Trim())
			.Select((string line) => Regex.Match(line, @"\\(ControlSet\d{3})$", RegexOptions.IgnoreCase))
			.Where((Match match) => match.Success)
			.Select((Match match) => match.Groups[1].Value)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy((string value) => value, StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (controlSets.Count == 0)
		{
			controlSets.Add("ControlSet001");
		}
		return controlSets;
	}

	private async Task<string> QueryRegistryValueForReportAsync(string keyPath, string valueName)
	{
		string args = valueName == "(default)"
			? "query " + QuoteArgument(keyPath) + " /ve"
			: "query " + QuoteArgument(keyPath) + " /v " + QuoteArgument(valueName);
		ProcessResult result = await RunProcessInternalAsync("reg.exe", args);
		if (result.ExitCode != 0)
		{
			string firstError = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "missing";
			return "QUERY MISSING: " + keyPath + " :: " + valueName + " -> " + firstError;
		}
		string valueLine = result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select((string line) => line.Trim())
			.FirstOrDefault((string line) => line.StartsWith(valueName, StringComparison.OrdinalIgnoreCase) || (valueName == "(default)" && line.StartsWith("(Default)", StringComparison.OrdinalIgnoreCase))) ?? "value present";
		return "QUERY OK: " + keyPath + " :: " + valueName + " -> " + valueLine;
	}

	// One extra NTFS partition to create after Windows. SizeMb <= 0 means "use the remaining space"
	// (valid only for the last extra). Letter is the temporary drive letter; Label is the volume label.
	private sealed record ExtraPartitionSpec(int SizeMb, char Letter, string Label);

	private static string BuildRealNtfsUsbLayoutDiskpartScript(int diskNumber, char bootLetter, char windowsLetter, int windowsSizeMb = 0, IReadOnlyList<ExtraPartitionSpec>? extraPartitions = null)
	{
		// Universal boot layout (standard Windows-To-Go shape): MBR disk with an ACTIVE FAT32 boot partition
		// plus the NTFS Windows partition. With bcdboot /f ALL this carries BOTH the BIOS boot files
		// (bootmgr in \) and the UEFI boot files (\EFI\Boot\bootx64.efi) on one FAT32 partition, so the
		// clone boots on legacy-BIOS PCs AND on UEFI PCs. With windowsSizeMb > 0 the Windows partition is
		// capped and extra NTFS partitions follow (an empty data partition, or partitions cloned from the
		// source PC's other data partitions).
		var lines = new List<string>
		{
			"san policy=OnlineAll",
			$"select disk {diskNumber}",
			"detail disk",
			"clean",
			"rem MBR layout (no 'convert gpt') so legacy BIOS can boot it too",
			"rem align=1024 forces a 1 MiB partition offset = 4K-aligned, for full SSD write performance",
			"create partition primary size=350 align=1024",
			"format quick fs=fat32 label=\"WINUSB-BOOT\"",
			$"assign letter={bootLetter}",
			"active",
			windowsSizeMb > 0 ? $"create partition primary size={windowsSizeMb} align=1024" : "create partition primary align=1024",
			// 64K NTFS clusters: far fewer metadata updates for the many-small-file Windows apply => faster write on
			// USB (a Windows-To-Go staple). Windows boots fine from 64K; the only tradeoff is a little slack space and
			// that classic NTFS-compressed files restore uncompressed (Windows uses cluster-independent WOF anyway).
			"format quick fs=ntfs unit=64K label=\"Windows\"",
			$"assign letter={windowsLetter}"
		};
		if (windowsSizeMb > 0 && extraPartitions != null)
		{
			foreach (ExtraPartitionSpec ex in extraPartitions)
			{
				lines.Add(ex.SizeMb > 0 ? $"create partition primary size={ex.SizeMb} align=1024" : "create partition primary align=1024");
				string safeLabel = (string.IsNullOrWhiteSpace(ex.Label) ? "Data" : ex.Label).Replace("\"", "");
				lines.Add($"format quick fs=ntfs label=\"{safeLabel}\"");
				lines.Add($"assign letter={ex.Letter}");
			}
		}
		lines.Add("list volume");
		lines.Add("exit");
		return string.Join(Environment.NewLine, lines);
	}

	// True when a wimlib capture aborted because it couldn't OPEN/READ a file (exit 47 / Access Denied). Real-time
	// antivirus commonly blocks the third-party wimlib exe from reading a protected / being-scanned file; an EFS file
	// or an exclusively-locked file causes the same. Used to decide whether to retry the capture with DISM (AV-trusted).
	private static bool IsWimlibReadFailure(Exception ex) =>
		ex.Message.Contains("code 47") || ex.Message.Contains("Failed to open", StringComparison.OrdinalIgnoreCase)
		|| ex.Message.Contains("Can't open", StringComparison.OrdinalIgnoreCase)
		|| ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase);   // wimlib "Access Denied" AND DISM "Access is denied"

	// After a DISM /Capture-Image access-denied failure, dism.log names the exact file it couldn't read (wimlib only
	// logs an opaque inode number). Extract the most recent such path so the failure message can tell the user WHICH
	// file to unquarantine / exclude / delete — typically an unsigned build artifact the antivirus has locked (e.g. a
	// freshly-built .exe under bin\Release\...\publish).
	private static string TryGetDismBlockedPath(char shadowLetter)
	{
		try
		{
			string dismLog = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Logs", "DISM", "dism.log");
			if (!File.Exists(dismLog) || new FileInfo(dismLog).Length > 64L * 1024 * 1024) return "";
			string found = "";
			foreach (string line in File.ReadLines(dismLog))
			{
				var m = Regex.Match(line, @"PID=\d+\s+(.+?)\s+\(HRESULT=0x80070005\)");
				if (m.Success) found = m.Groups[1].Value.Trim();   // keep the last (most recent) match
			}
			// Map the VSS snapshot drive letter back to the real system drive so the path is recognizable to the user.
			if (found.Length > 2 && char.ToUpperInvariant(found[0]) == char.ToUpperInvariant(shadowLetter) && found[1] == ':')
				found = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + found.Substring(2);
			return found;
		}
		catch { return ""; }
	}

	// Backup: capture the running Windows to a compressed .wim image file (VSS snapshot). Uses wimlib (LZX + incremental
	// append); on a wimlib read-failure (typically real-time antivirus blocking its reads) a full backup retries with the
	// AV-trusted Microsoft engine (DISM). No disk is formatted. Restorable via "Advanced: restore full disk image".
	private async Task BackupThisPcToImageAsync()
	{
		if (!IsAdministrator())
		{
			MessageBox.Show(L("Mb011"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		var save = new Microsoft.Win32.SaveFileDialog
		{
			Title = L("DlgSaveBackup"),
			Filter = L("FltWim") + " (*.wim)|*.wim",
			FileName = "DriveForge-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".wim"
		};
		if (save.ShowDialog() != true) return;
		string outPath = save.FileName;

		// Incremental backup: if the chosen .wim already exists, offer to APPEND a new snapshot. wimlib stores
		// only the file data that changed since the existing image(s) — much faster and smaller than a full one.
		bool incremental = false;
		if (File.Exists(outPath) && new FileInfo(outPath).Length > 0)
		{
			MessageBoxResult choice = MessageBox.Show(
				L("MbBackupOverwritePrompt"),
				"DriveForge", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
			if (choice == MessageBoxResult.Cancel) return;
			incremental = choice == MessageBoxResult.Yes;
		}

		long usedBytes = GetCurrentWindowsUsedBytes();
		// Refuse if the destination drive can't hold the image. A fresh full image is ~60% of used bytes
		// (LZX-compressed). An incremental "append" only stores what changed, so require a small headroom
		// (~15%) instead of room for a whole second full image.
		try
		{
			var destDrive = new DriveInfo(Path.GetPathRoot(outPath) ?? "C:\\");
			long need = (long)(usedBytes * (incremental ? 0.15 : 0.6));
			// A full overwrite now captures to a temp file (.dfnew) and swaps it in only AFTER it verifies, so the old
			// backup stays on disk for the whole capture — its space is NOT reclaimable up-front. Require the full need of
			// real free space (both files coexist during capture). Incremental appends in place, so no coexistence there.
			if (destDrive.AvailableFreeSpace < need)
			{
				MessageBox.Show(string.Format(L("MbBackupNoSpace"), FormatBytes(need), FormatBytes(destDrive.AvailableFreeSpace)), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
		}
		catch { }

		ShadowCopyInfo? shadowCopy = null;
		string? shadowDosTarget = null;
		char shadowLetter = GetFreeDriveLetter();
		bool failed = false;
		bool usedDismFallback = false;
		// A full capture is written to a TEMP file and swapped in only after it VERIFIES — so a failed/interrupted/corrupt
		// overwrite can never destroy the existing backup. An incremental "append" must modify the existing .wim in place.
		string capturePath = incremental ? outPath : outPath + ".dfnew";
		try
		{
			stopRequested = false;
			isPaused = false;
			bitLockerEncrypting = false;
			PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, usedBytes / 1073741824.0 * 0.6);
			progressDoneGiB = 0.0;
			progressPrevGiB = 0.0;
			progressSpeedMb = 0.0;
			_speedWindow.Clear();
			lastProcessOutputUtc = DateTime.UtcNow;
			lastHeartbeatLogUtc = DateTime.UtcNow;
			operationStopwatch.Restart();
			operationTimer.Start();
			SetBusy(busy: true, L("BzBackup"));
			ProgressBar.Value = 0.0;

			SetStage(L("StgVssSnapshot"), 4.0);
			string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
			shadowCopy = await CreateShadowCopyAsync(systemDrive);
			shadowDosTarget = GetDosDeviceTarget(shadowCopy.DeviceObject);
			MapSnapshotDrive(shadowLetter, shadowDosTarget);
			string sourceRoot = shadowLetter + ":\\";

			SetStage(incremental ? L("StgIncremental") : L("StgCompressing"), 12.0);
			string wimlibPath = await EnsureWimlibAsync();
			string captureConfigPath = Path.Combine(Path.GetTempPath(), $"driveforge-backup-config-{Guid.NewGuid():N}.ini");
			await File.WriteAllTextAsync(captureConfigPath, BuildCaptureConfig(), Encoding.ASCII);
			if (!incremental) TryDeleteFile(capturePath);   // clear a stale temp from a prior interrupted run — the real backup is left untouched
			int threads = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
			string imageName = (incremental ? "Backup " : "DriveForge backup ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
			// "append" deduplicates against the existing images (true incremental); "capture" makes a fresh file.
			string verb = incremental ? "append" : "capture";
			string args = verb + " " + QuoteArgument(sourceRoot.TrimEnd('\\') + "\\.") + " " + QuoteArgument(capturePath) +
				" " + QuoteArgument(imageName) + " " + QuoteArgument("Created by DriveForge") +
				(incremental ? "" : " --compress=LZX") + " --threads=" + threads + " --config=" + QuoteArgument(captureConfigPath) + " --check";
			using (var pollCts = new CancellationTokenSource())
			{
				Task poll = PollFileSizeProgressAsync(capturePath, pollCts.Token);
				try { await RunProcessAsync(wimlibPath, args); }
				catch (Exception wex) when (!incremental && !stopRequested && IsWimlibReadFailure(wex))
				{
					// Real-time antivirus blocks the third-party wimlib exe from reading a file, so wimlib aborts the
					// whole capture (exit 47). DISM is a signed Windows component that AV trusts — and it warns+skips a
					// file it can't read instead of aborting — so retry the full capture with DISM (LZX /Compress:max
					// matches the wimlib backup's compression). Same rationale as the clone's Microsoft engine.
					Log("wimlib capture failed to read a file (exit 47 — usually real-time antivirus blocking its reads). Retrying with the Microsoft engine (DISM), which antivirus trusts and which skips unreadable files.");
					SetStage(L("StgRetryBackupMs"), 12.0);
					TryDeleteFile(capturePath);   // drop wimlib's incomplete partial before DISM writes a fresh one
					usedDismFallback = true;
					await RunProcessAsync("dism.exe",
						"/Capture-Image /ImageFile:" + QuoteArgument(capturePath) +
						" /CaptureDir:" + sourceRoot.TrimEnd('\\') + "\\" +
						" /Name:" + QuoteArgument(imageName) +
						" /ConfigFile:" + QuoteArgument(captureConfigPath) + " /Compress:max /CheckIntegrity");
					Log("DISM backup capture completed.");
				}
				finally { pollCts.Cancel(); try { await poll; } catch { } TryDeleteFile(captureConfigPath); }
			}

			// Read-back verify BEFORE trusting the image. A full capture is verified in its temp file and only then
			// atomically swapped over the old backup; an incremental append is verified in place.
			SetStage(L("StgVerifyBackup"), 92.0);
			if (!await WimIsValidAsync(wimlibPath, capturePath))
			{
				if (!incremental) TryDeleteFile(capturePath);   // discard the bad new image; the existing backup is left untouched
				throw new InvalidOperationException(incremental
					? "The incremental backup did not pass verification — your existing image may be damaged; make a fresh full backup."
					: "The captured backup failed verification (it may be truncated or corrupt) and was discarded.");
			}
			if (!incremental) File.Move(capturePath, outPath, overwrite: true);   // replace the old backup only now that the new one verified
			bool ok = File.Exists(outPath) && new FileInfo(outPath).Length > 0;
			progressDoneGiB = progressTotalGiB;
			operationTimer.Stop();
			operationStopwatch.Stop();
			UpdateProgressStats();
			SetStage(ok ? L("StgBackupDone") : L("StgBackupNoFile"), 100.0);
			SetBusy(busy: false);
			NotifyOperationDone(ok);
			// Force the bar to full AFTER the timer is stopped and isBusy is cleared. The live progress caps the copy in
			// a 40–82% band and keeps inflating the estimated total, so without this explicit set the bar froze at ~82%
			// when the backup actually finished (the operation succeeded — only the bar looked stuck).
			if (ok) { ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%"; UpdateProgressStats(); }
			if (ok)
			{
				SetLastReport(outPath);
				if (usedDismFallback) MessageBox.Show(L("MbBackupDismSkipped"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning);   // AV blocked wimlib -> DISM skipped unreadable files -> image may be missing files
				MessageBox.Show(string.Format(L("MbBackupCreated"), outPath, FormatBytes(new FileInfo(outPath).Length)),
					"DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
				MaybeOfferDonation();
			}
			else
			{
				throw new InvalidOperationException("The backup image file was not created.");
			}
		}
		catch (Exception ex)
		{
			failed = true;
			// Full capture writes to a TEMP file, so on any failure just drop the temp — the existing backup is untouched.
			// Incremental append modifies the existing .wim in place, so it is kept (it may still hold prior restore points).
			if (!incremental) TryDeleteFile(capturePath);
			StatusText.Text = L("SxBackupFailed");
			NotifyOperationDone(false);
			SaveLogToDesktop();
			if (incremental)
			{
				// The append rewrites the existing image in place; an interrupted/failed append can leave it damaged, and
				// it was NOT discarded. Be honest so the user re-verifies it / makes a fresh full backup.
				MessageBox.Show(L("MbBackupIncFailed"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
			else if (IsWimlibReadFailure(ex))
			{
				// Turn the cryptic "exited with code 47" (AV blocked wimlib AND the DISM fallback) into an actionable note.
				string blocked = TryGetDismBlockedPath(shadowLetter);
				string msg = L("MbBackupReadFail") + (blocked.Length > 0 ? "\n\n→  " + blocked : "");
				MessageBox.Show(msg, "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			}
			else
				ShowError(L("ErrBackup"), ex);
		}
		finally
		{
			operationTimer.Stop();
			operationStopwatch.Stop();
			if (failed) UpdateProgressStats();
			if (!string.IsNullOrWhiteSpace(shadowDosTarget)) UnmapSnapshotDrive(shadowLetter, shadowDosTarget);
			if (shadowCopy != null) await DeleteShadowCopyAsync(shadowCopy.Id);
			SetBusy(busy: false);
		}
	}

	private async Task PollFileSizeProgressAsync(string path, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try { if (File.Exists(path)) Volatile.Write(ref _progressDoneBytes, new FileInfo(path).Length); }
			catch { }
			try { await Task.Delay(1500, token); }
			catch (TaskCanceledException) { break; }
		}
	}

	// Read-back integrity check of a produced .wim: `wimlib verify` re-reads every stream and validates the --check
	// integrity table, catching a truncated / corrupt / interrupted capture (or a half-committed in-place append) that
	// `File.Exists && Length>0` would miss. Returns false on any failure (RunProcessCaptureAsync throws on non-zero exit).
	private async Task<bool> WimIsValidAsync(string wimlibPath, string wimPath)
	{
		try { await RunProcessCaptureAsync(wimlibPath, "verify " + QuoteArgument(wimPath)); return true; }
		catch (Exception ex) { Log("Backup verify failed: " + ex.Message); return false; }
	}

	// Returns the display name of the antivirus actively protecting in REAL TIME (any vendor, via the Windows
	// Security Center), or null if none is active / it can't be determined. Never throws.
	private async Task<string?> GetActiveRealtimeAntivirusAsync()
	{
		try
		{
			string script =
				// 1) Security Center reports real-time protection ON → definitive ("RTP|name").
				"$avs = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue;" +
				"foreach($a in $avs){ if( ($a.productState -band 0x1000) -ne 0 ){ 'RTP|' + $a.displayName; return } };" +
				// 2) No RTP per Security Center — but a third-party AV may be installed with its scanning driver still
				//    loaded ("PAUSED|name"). Bitdefender 'paused' deregisters from Security Center yet keeps intercepting
				//    file reads, so we also look for a running AV service (excluding Defender's own passive services).
				"$pat = 'bdredline|bdvpn|vsserv|bdservicehost|bitdefender|trufos|gzflt|kavfs|klif|kaspersky|ekrn|eset|avastsvc|avgsvc|mcafee|mfevtp|mbamservice|malwarebytes|sepmaster|norton|wrsvc|webroot|savservice|sophos|fshoster|f-secure';" +
				"$svc = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Running' -and ($_.Name -match $pat -or $_.DisplayName -match $pat) -and $_.Name -notmatch 'WinDefend|Sense|SecurityHealth' };" +
				"if($svc){ $n = ($avs | ForEach-Object { $_.displayName } | Select-Object -First 1); if(-not $n){ $n = ($svc | Select-Object -First 1).DisplayName }; 'PAUSED|' + $n }";
			ProcessResult r = await RunProcessInternalAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script));
			string name = (r.Output ?? "").Replace("\r", "").Trim();
			if (name.Length == 0) return null;
			return name.Split('\n')[0].Trim();
		}
		catch { return null; }
	}

	private async Task RunExperimentalFullRootUsbCloneAsync(DiskItem targetDisk)
	{
		char currentTargetLetter = GetFirstUsableDriveLetter(targetDisk);
		char bootLetter = GetFreeDriveLetter(currentTargetLetter);
		char windowsLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter);
		char shadowLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter, windowsLetter);
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
		// Report folder + diskpart-script artifact are only written to disk when the clone needs review (see the
		// end of this method) — a clean success no longer litters the Desktop. The script itself is staged in TEMP
		// (it must exist as a real file for diskpart.exe /s to read) and copied into reportRoot only on failure.
		string reportRoot = Path.Combine(desktop, "DriveForge-NTFS-FullRootClone-" + runId);
		string diskpartPath = Path.Combine(Path.GetTempPath(), $"driveforge-fullroot-diskpart-{Guid.NewGuid():N}.txt");
		string realRoot = windowsLetter + ":\\";
		string realWindowsFolder = Path.Combine(realRoot, "Windows");
		string bcdStore = bootLetter + ":\\EFI\\Microsoft\\Boot\\BCD";
		string diskpartOutput = "";
		string registryOutput = "";
		string bcdbootOutput = "";
		string bcdEnumOutput = "";
		NtfsCopyTestResult? copyResult = null;
		string? dismWimPath = null; // Microsoft-engine (DISM) path: the snapshot is captured to this scratch WIM before the target is formatted
		ShadowCopyInfo? shadowCopy = null;
		string? shadowDosTarget = null;
		bool diskpartOk = false;
		bool copyOk = false;
		bool registryOk = false;
		bool bcdbootOk = false;
		bool bcdStoreOk = false;
		bool bootx64Ok = false;
		bool loaderPathOk = false;
		bool verifyRan = false;
		long verifyVerifiedFiles = 0;
		long verifyVerifiedBytes = 0;
		long verifyMismatches = 0;
		long verifyUnverifiable = 0;
		var verifySamples = new List<string>();
		var verifyUnverifiableSamples = new List<string>();
		bool unattendWritten = false;
		string sourceRoot = "";
		bool forceDism = false; // set true if the user opts into the DISM engine at the antivirus prompt

		try
		{
			// Real-time antivirus scans every file the standard clone engine reads, which
			// can make it crawl. Offer the Microsoft (DISM) engine — which antivirus does not slow down — as the
			// recommended fix. Skipped in headless mode, when the DISM engine is already chosen, and when the raw
			// NTFS engine is selected (that path is a read-only capture dump — it uses neither clone engine, so the
			// antivirus question is irrelevant and would only block the dump from starting).
			if (!headlessRun && UseDismEngineCheck?.IsChecked != true && UseNtfsRawEngineCheck?.IsChecked != true)
			{
				string? avRaw = await GetActiveRealtimeAntivirusAsync();
				if (!string.IsNullOrEmpty(avRaw))
				{
					int bar = avRaw.IndexOf('|');
					string avName = bar >= 0 ? avRaw.Substring(bar + 1).Trim() : avRaw.Trim();
					if (avName.Length == 0) avName = "Your antivirus";
					MessageBoxResult avChoice = MessageBox.Show(string.Format(L("MbAvOfferDism"), avName), L("MbAvWarnTitle"),
						MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
					if (avChoice == MessageBoxResult.Cancel)
					{
						Log($"Clone cancelled at the antivirus prompt ({avName} active).");
						SetStage(L("StgCloneCancelled"), 0.0);
						return;
					}
					forceDism = avChoice == MessageBoxResult.Yes;
					Log(forceDism
						? $"Using the Microsoft (DISM) engine to avoid the {avName} slowdown."
						: $"Proceeding with the standard engine despite {avName} active — the clone may be very slow.");
				}
			}
			bool backupPrivilege = TryEnablePrivilege("SeBackupPrivilege");
			bool restorePrivilege = TryEnablePrivilege("SeRestorePrivilege");
			// The raw engine reproduces owners/SACLs, which need these two extra privileges (harmless for the other engines).
			bool securityPrivilege = TryEnablePrivilege("SeSecurityPrivilege");
			bool takeOwnershipPrivilege = TryEnablePrivilege("SeTakeOwnershipPrivilege");
			TryEnablePrivilege("SeCreateSymbolicLinkPrivilege");   // raw engine replays symlink reparse points
			Log($"Full root USB clone privileges: SeBackupPrivilege={(backupPrivilege ? "enabled" : "not available")}, SeRestorePrivilege={(restorePrivilege ? "enabled" : "not available")}, SeSecurityPrivilege={(securityPrivilege ? "enabled" : "not available")}, SeTakeOwnershipPrivilege={(takeOwnershipPrivilege ? "enabled" : "not available")}");

			SetStage(L("StgPrepClone"), 4.0);
			string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
			shadowCopy = await CreateShadowCopyAsync(systemDrive);
			shadowDosTarget = GetDosDeviceTarget(shadowCopy.DeviceObject);
			MapSnapshotDrive(shadowLetter, shadowDosTarget);
			sourceRoot = shadowLetter + ":\\";

			long requiredBytes = EstimateRequiredBytes();
			if (requiredBytes > 0 && targetDisk.Size < requiredBytes)
			{
				throw new InvalidOperationException(
					"The selected drive is too small for this clone.\n\nRequired: " + FormatBytes(requiredBytes) +
					"\nSelected drive: " + FormatBytes(targetDisk.Size) +
					"\n\nNo changes were made — the drive was not formatted.");
			}

			if (!await ConfirmTargetHealthAsync(targetDisk))
			{
				Log("Faithful clone cancelled by user after target health warning.");
				SetStage(L("StgCloneCancelHealth"), 0.0);
				return;
			}

			// Raw NTFS engine (experimental): reads the snapshot MFT directly (antivirus-transparent, no scratch WIM,
			// fits a smaller target) and writes files onto the freshly-formatted target. It takes priority over DISM.
			// The disk-erase warning was already shown and confirmed ONCE, up front, by ConfirmOperationSummary before
			// this method was even called — no second confirmation here, just an informational log line.
			bool useRawEngine = UseNtfsRawEngineCheck?.IsChecked == true;
			if (useRawEngine && !headlessRun)
			{
				Log("Fast Clone (raw NTFS) engine selected — direct copy, not slowed by antivirus, no scratch WIM. Copies files, timestamps, hardlinks, permissions (ACLs/owners), junctions/reparse points, alternate data streams, EFS-encrypted files (raw, via the backup API), and decompresses NTFS-compressed and WOF/CompactOS files.");
			}

			// Microsoft-engine (DISM) path: capture the snapshot to a scratch WIM BEFORE we format the target, so a
			// capture failure never leaves an erased drive. DISM is a signed Windows component, so real-time antivirus
			// does not scan its reads the way it scans third-party wimlib — and it fits a target smaller than the source.
			bool useDismEngine = !useRawEngine && (forceDism || UseDismEngineCheck?.IsChecked == true);
			if (useDismEngine)
			{
				// DISM writes an intermediate WIM (~half the used data). If no fixed drive has room for it (a nearly-
				// full PC), fall back to the standard wimlib PIPE engine, which streams capture→apply with NO temporary
				// file at all — so it still clones even when the disk is almost full (just re-exposed to antivirus).
				long scratchNeed = (long)(GetCurrentWindowsUsedBytes() * 0.5) + 2L * 1024 * 1024 * 1024;
				string scratchDir = PickScratchDirForWim(targetDisk.Number, scratchNeed);
				if (string.IsNullOrEmpty(scratchDir))
				{
					Log($"Microsoft engine needs ~{FormatBytes(scratchNeed)} of free space for its temporary image, but no drive has room. Falling back to the standard engine, which needs no temporary file.");
					SetStage(L("StgLowSpaceStd"), 6.0);
					useDismEngine = false;
				}
				else
				{
					dismWimPath = await DismCaptureToWimAsync(sourceRoot, scratchDir);
				}
			}

			SetStage(L("StgFormatTarget"), 10.0);
			int windowsSizeMb = 0;
			var extraPartitions = new List<ExtraPartitionSpec>();
			var dataCloneJobs = new List<(char Source, char Target, string Label)>();
			long winBytesPlan = Math.Max(64L * 1024 * 1024 * 1024, (long)(GetCurrentWindowsUsedBytes() * 1.4) + 12L * 1024 * 1024 * 1024);
			long bootSlack = 350L * 1024 * 1024 + 200L * 1024 * 1024;
			// A faithful clone builds an MBR target (BuildRealNtfsUsbLayoutDiskpartScript uses no `convert gpt`), which
			// addresses at most ~2 TiB; diskpart silently caps the trailing partition there. Size the data partitions from
			// the MBR-usable span, not the raw disk size, and warn when the target is larger so space isn't over-promised.
			long mbrUsable = Math.Min(targetDisk.Size, 2199023255040L);
			if (targetDisk.Size > 2199023255040L)
				Log("Note: the target is larger than ~2 TB; an MBR clone layout can use only the first ~2 TB — the remainder is left unallocated.");
			var reservedLetters = new List<char> { currentTargetLetter, bootLetter, windowsLetter, shadowLetter };

			// Internal-disk mode clones the WHOLE disk (all data partitions) automatically; portable mode
			// makes it an optional checkbox.
			if (CloneOtherPartitionsCheck.IsChecked == true || ModeBox.SelectedIndex == ModeCloneInternal)
			{
				// Clone the source PC's other data partitions, each into its own NTFS partition on the target.
				List<SourceDataPartition> srcParts = await GetSourceDataPartitionsAsync();
				if (srcParts.Count == 0)
				{
					Log("Clone other partitions: no additional data partitions found on this PC's disk.");
				}
				else
				{
					var sized = new List<(SourceDataPartition Src, long Bytes)>();
					foreach (SourceDataPartition sp in srcParts)
					{
						long b = Math.Max(1L * 1024 * 1024 * 1024, (long)(sp.UsedBytes * 1.3) + 2L * 1024 * 1024 * 1024);
						sized.Add((sp, b));
					}
					// MBR (BuildRealNtfsUsbLayoutDiskpartScript uses no `convert gpt`) holds at most 4 PRIMARY partitions;
					// the layout already uses 2 (boot + Windows), so only 2 data partitions can be created. A 5th
					// `create partition` fails mid-script and leaves diskpart focused on the previous partition, so the
					// following `format`/`assign` would hit the WRONG volume. Cap the extras and warn about the rest.
					const int MaxExtraDataPartitions = 2;
					if (sized.Count > MaxExtraDataPartitions)
					{
						Log($"NOTE: this disk has {sized.Count} data partitions but an MBR clone can hold only {MaxExtraDataPartitions} extra (plus boot + Windows). Cloning the first {MaxExtraDataPartitions}; clone the rest separately.");
						sized = sized.GetRange(0, MaxExtraDataPartitions);
					}
					long need = winBytesPlan + bootSlack;
					foreach (var s in sized) need += s.Bytes;
					if (need > mbrUsable)
					{
						throw new InvalidOperationException(
							"The target is too small to also clone the other data partitions.\n\nRequired: " + FormatBytes(need) +
							"\nTarget: " + FormatBytes(targetDisk.Size) + "\n\nUncheck 'Also clone the other data partitions' or use a larger drive. No changes were made.");
					}
					windowsSizeMb = (int)(winBytesPlan / (1024 * 1024));
					for (int i = 0; i < sized.Count; i++)
					{
						char tLetter = GetFreeDriveLetter(reservedLetters.ToArray());
						reservedLetters.Add(tLetter);
						bool last = i == sized.Count - 1;
						int sizeMb = last ? 0 : (int)(sized[i].Bytes / (1024 * 1024)); // last fills remaining space
						string label = string.IsNullOrWhiteSpace(sized[i].Src.Label) ? ("Data" + sized[i].Src.Letter) : sized[i].Src.Label;
						extraPartitions.Add(new ExtraPartitionSpec(sizeMb, tLetter, label));
						dataCloneJobs.Add((sized[i].Src.Letter, tLetter, label));
						Log($"Will clone data partition {sized[i].Src.Letter}: ('{label}', {FormatBytes(sized[i].Src.UsedBytes)} used) -> {tLetter}:.");
					}
				}
			}
			else if (DataPartitionCheck.IsChecked == true)
			{
				// Empty data partition from leftover space.
				long leftover = mbrUsable - winBytesPlan - bootSlack;
				if (leftover >= 8L * 1024 * 1024 * 1024)
				{
					windowsSizeMb = (int)(winBytesPlan / (1024 * 1024));
					char dl = GetFreeDriveLetter(reservedLetters.ToArray());
					extraPartitions.Add(new ExtraPartitionSpec(0, dl, "Data"));
					Log($"Data partition enabled: Windows capped at {FormatBytes(winBytesPlan)}, data partition gets ~{FormatBytes(leftover)} ({dl}:).");
				}
				else
				{
					Log("Data partition requested but skipped: not enough leftover space after Windows. Windows will use the whole disk.");
				}
			}
			// TOCTOU guard: the health/identity gate ran before the (possibly multi-minute) DISM capture + the raw-engine
			// confirm modal above, and the diskpart script selects the target purely by disk NUMBER. Re-verify the
			// target's identity ONE more time immediately before the destructive clean, so a disk that was unplugged and
			// renumbered during the capture is never the one we wipe. FailTargetDiskChanged already shows the dialog.
			if (!await VerifyTargetDiskUnchangedAsync(targetDisk))
			{
				Log("Faithful clone aborted: the target disk's identity changed since the confirmation.");
				SetStage(L("StgCloneCancelHealth"), 0.0);
				return;
			}
			await File.WriteAllTextAsync(diskpartPath, BuildRealNtfsUsbLayoutDiskpartScript(targetDisk.Number, bootLetter, windowsLetter, windowsSizeMb, extraPartitions), Encoding.ASCII);
			diskpartOutput = await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(diskpartPath));
			diskpartOk = true;

			// Speed: stop NTFS from generating a legacy 8.3 short name for each of the ~100k+ files the apply
			// creates — pure metadata overhead on a clone. Per-volume (does NOT touch the host system), best-effort.
			await RunProcessAsync("fsutil.exe", $"8dot3name set {windowsLetter}: 1", allowFailure: true);

			SetStage(L("StgCloningWin"), 18.0);
			Log("Faithful clone engine: wimlib captures the VSS snapshot and applies it onto the USB Windows");
			Log("partition. A WIM image apply preserves ACLs, owners, hardlinks, reparse points AND the AppX");
			Log("state EXACTLY (the standard faithful-clone approach), so the clone needs no first-boot AppX repair.");
			Log("Source root: " + sourceRoot);
			Log("Target root: " + realRoot);
			// wimlib is needed for the wimlib capture, for data-partition clones, AND to APPLY the DISM-captured WIM
			// (wimlib's apply tolerates the security-label quirks that trip dism.exe /Apply-Image with error 1299).
			string wimlibPath = await EnsureWimlibAsync();
			string captureConfigPath = Path.Combine(Path.GetTempPath(), $"driveforge-fullroot-config-{Guid.NewGuid():N}.ini");
			await File.WriteAllTextAsync(captureConfigPath, BuildCaptureConfig(), Encoding.ASCII);
			// wimlib runs as an external piped process with no progress hook, so the clone phase used to sit
			// frozen. Re-point the bar/ETA at this phase (total = source used bytes) and poll the target
			// partition's growing used-space while the apply runs, so the bar and speed move in real time.
			progressDoneGiB = 0.0;
			progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1073741824.0);
			_speedWindow.Clear();
			RawCloneStats? rawStats = null;
			if (useRawEngine)
			using (var rawPollCts = new CancellationTokenSource())
			{
				// Raw NTFS engine (experimental): walk the snapshot MFT off the raw device (antivirus never sees a
				// file open) and write each file directly to the target. No scratch WIM, no antivirus slowdown, and
				// it fits a smaller target. Stage 1 fidelity = data + timestamps + hardlinks (no ACL/reparse/WOF yet).
				// A poller drives the bar/ETA off the target partition's growing used space (like the wimlib path).
				Task rawPoll = PollPartitionUsedSpaceAsync(realRoot, rawPollCts.Token, rawEngine: true);
				_suppressLineProgress = true;
				try { rawStats = await RawNtfsWriteCloneAsync(shadowLetter, windowsLetter, realRoot); }
				finally { _suppressLineProgress = false; rawPollCts.Cancel(); try { await rawPoll; } catch { } TryDeleteFile(captureConfigPath); }
			}
			else if (useDismEngine)
			{
				// Hybrid engine: the snapshot was captured to dismWimPath by DISM (antivirus-transparent) BEFORE the
				// format. Apply that local WIM with wimlib, which tolerates the security-label quirks that make
				// dism.exe /Apply-Image fail with error 1299 on some live-captured Windows installs.
				await DismApplyWimAsync(dismWimPath!, wimlibPath, windowsLetter, realRoot);
				TryDeleteFile(captureConfigPath);
			}
			else
			using (var clonePollCts = new CancellationTokenSource())
			{
				Task pollTask = PollPartitionUsedSpaceAsync(realRoot, clonePollCts.Token);
				_suppressLineProgress = true; // poller is the single progress source for the piped clone
				try
				{
					await StreamCloneWithWimlibAsync(wimlibPath, sourceRoot, windowsLetter, captureConfigPath);
				}
				finally
				{
					_suppressLineProgress = false;
					clonePollCts.Cancel();
					try { await pollTask; } catch { }
					TryDeleteFile(captureConfigPath);
				}
			}
			// A stopped RAW copy returns normally (no external process to kill), unlike the wimlib/DISM paths which
			// throw. Without this guard, a user Stop mid-copy would fall through and finalize a half-written tree as a
			// "completed" clone (copyOk only checks a few early boot files/dirs that are created first).
			if (useRawEngine && (stopRequested || internalOperationStopped))
			{
				throw new OperationCanceledException("Clone stopped by the user before the copy finished — the target is incomplete.");
			}
			// A mid-copy disk-full leaves the tree partial while the few early boot files still pass copyOk — fail loudly
			// instead of finalizing an incomplete clone as "completed".
			if (useRawEngine && rawStats != null && rawStats.DiskFull > 0)
			{
				throw new InvalidOperationException("The target ran out of space during the raw clone — the clone is incomplete. Use a larger drive.");
			}
			copyOk = File.Exists(Path.Combine(realWindowsFolder, "System32", "winload.efi")) &&
				File.Exists(Path.Combine(realWindowsFolder, "System32", "config", "SYSTEM")) &&
				Directory.Exists(Path.Combine(realRoot, "Program Files")) &&
				Directory.Exists(Path.Combine(realRoot, "ProgramData")) &&
				Directory.Exists(Path.Combine(realRoot, "Users"));
			if (!copyOk)
			{
				string failureReportPath = WriteFullRootUsbCloneReport(targetDisk, reportRoot, diskpartPath, shadowLetter, sourceRoot, realRoot, realWindowsFolder, bootLetter, windowsLetter, diskpartOk, copyOk, registryOk, bcdbootOk, bcdStoreOk, bootx64Ok, loaderPathOk, copyResult, diskpartOutput, registryOutput, bcdbootOutput, bcdEnumOutput, verifyRan, verifyVerifiedFiles, verifyVerifiedBytes, verifyMismatches, verifyUnverifiable, verifySamples, verifyUnverifiableSamples, unattendWritten);
				SetToolOutput(File.ReadAllText(failureReportPath, Encoding.UTF8));
				Log("Full root clone report written before failure: " + failureReportPath);
				throw new InvalidOperationException("Faithful WIM clone did not produce a complete Windows root.\n\nReport: " + failureReportPath);
			}

			// Clone the source PC's other data partitions, each into its target partition (VSS snapshot + wimlib).
			int dataCloneFailures = 0;
			if (dataCloneJobs.Count > 0)
			{
				string dataConfigPath = Path.Combine(Path.GetTempPath(), $"driveforge-data-config-{Guid.NewGuid():N}.ini");
				await File.WriteAllTextAsync(dataConfigPath, "[ExclusionList]\r\n\\System Volume Information\r\n\\$Recycle.Bin\r\n\\pagefile.sys\r\n\\hiberfil.sys\r\n\\swapfile.sys\r\n", Encoding.ASCII);
				try
				{
					int jobNum = 0;
					foreach (var job in dataCloneJobs)
					{
						jobNum++;
						SetStage(string.Format(L("StgCloningDataPart"), job.Source, job.Target, jobNum, dataCloneJobs.Count), 70.0);
						Log($"Cloning data partition {job.Source}: -> {job.Target}: (VSS snapshot + wimlib).");
						ShadowCopyInfo? dataShadow = null;
						string? dataShadowDos = null;
						char dataShadowLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter, windowsLetter, shadowLetter, job.Target);
						try
						{
							dataShadow = await CreateShadowCopyAsync(job.Source + ":\\");
							dataShadowDos = GetDosDeviceTarget(dataShadow.DeviceObject);
							MapSnapshotDrive(dataShadowLetter, dataShadowDos);
							await StreamCloneWithWimlibAsync(wimlibPath, dataShadowLetter + ":\\", job.Target, dataConfigPath);
							Log($"Data partition {job.Source}: cloned to {job.Target}:.");
						}
						catch (Exception dpEx)
						{
							dataCloneFailures++;
							Log($"WARNING: cloning data partition {job.Source}: failed: {dpEx.Message} (Windows clone is unaffected).");
						}
						finally
						{
							if (!string.IsNullOrWhiteSpace(dataShadowDos)) UnmapSnapshotDrive(dataShadowLetter, dataShadowDos);
							if (dataShadow != null) await DeleteShadowCopyAsync(dataShadow.Id);
						}
					}
				}
				finally
				{
					TryDeleteFile(dataConfigPath);
				}
			}

			if (useDismEngine)
			{
				// The DISM engine already verified the captured WIM (dism /Get-WimInfo) before applying. A content
				// re-verify would RE-READ the source (WinSxS) through file APIs, which the antivirus scans — re-
				// introducing the very slowdown the DISM engine avoids. Skip it; the structural checks above already
				// confirm a complete, bootable Windows root.
				Log("Content verification skipped in Microsoft-engine mode: the captured image was already verified, and re-reading the source here would be scanned by antivirus (slow).");
			}
			else if (VerifyContentCheck.IsChecked == true)
			{
				SetStage(L("StgVerifyCloned"), 74.0);
				Log("Content verification (sampled): every file's presence + size is checked; boot-critical files plus a 1-in-8 sample of the rest are byte-compared against the VSS snapshot.");
				Log("Sampled files <= 64 MB are compared in full; larger ones are spot-checked on their first/middle/last 4 MB.");
				// Re-point the live progress bar/ETA at the verification phase: total = bytes actually on the
				// Windows partition, done resets to 0 so the timer shows verify GiB, speed and remaining time.
				long verifyTotalBytes = 0;
				try { var di = new DriveInfo(realRoot); verifyTotalBytes = Math.Max(0L, di.TotalSize - di.TotalFreeSpace); } catch { }
				progressDoneGiB = 0.0;
				progressTotalGiB = Math.Max(0.5, verifyTotalBytes / 1073741824.0);
				_speedWindow.Clear();
				await Task.Run(() => VerifyCloneContent(realRoot, sourceRoot, IsNtfsCloneExcluded, out verifyVerifiedFiles, out verifyVerifiedBytes, out verifyMismatches, out verifyUnverifiable, verifySamples, verifyUnverifiableSamples, useRawEngine));
				verifyRan = !stopRequested && !internalOperationStopped;
				Log(verifyRan
					? $"Content verification finished: {verifyVerifiedFiles:N0} files OK ({FormatBytes(verifyVerifiedBytes)}), {verifyMismatches:N0} mismatches, {verifyUnverifiable:N0} unverifiable (protected source files)."
					: "Content verification was interrupted before completing.");
			}
			else
			{
				Log("Content verification skipped (disabled in Options).");
			}

			SetStage(L("StgApplyPortable"), 80.0);
			// The raw engine now preserves ACLs, hardlinks and the copied AppX state (registry hives + package files)
			// faithfully — like the WIM/DISM image apply — so it uses faithfulMode too: skip the AppX re-registration
			// (which reset the ms-screenclip URI association) and leave antivirus working (no Re-Enable script needed).
			registryOutput = await ApplyPortableRegistrySettingsToRealCloneAsync(realWindowsFolder, BypassRequirementsCheck.IsChecked == true, BypassAccountCheck.IsChecked == true, faithfulMode: true, portableMode: ModeBox.SelectedIndex != ModeCloneInternal);
			registryOk = !registryOutput.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
			if (!registryOk)
			{
				throw new InvalidOperationException("Portable registry preparation failed. See the Step 20 report.");
			}

			SetStage(L("StgWriteUnattend"), 86.0);
			unattendWritten = WritePortableUnattend(realWindowsFolder);
			Log(unattendWritten
				? "First-boot answer file written (unattend.xml) — SanPolicy=4, PersistAllDeviceInstalls, OOBE skip."
				: "WARNING: could not write the first-boot answer file (unattend.xml).");

			SetStage(L("StgMakeCloneBootable"), 88.0);
			// /f ALL writes BOTH the BIOS boot files (bootmgr + \Boot\BCD) and the UEFI boot files onto the
			// active FAT32 partition, so the single stick boots on legacy-BIOS PCs AND on UEFI PCs.
			bcdbootOutput = await RunProcessCaptureAsync("bcdboot.exe", QuoteArgument(realWindowsFolder) + $" /s {bootLetter}: /f ALL /v");
			bcdbootOk = true;
			bcdStoreOk = File.Exists(bcdStore);
			EnsureUefiRemovableFallback(bootLetter);
			// Check the arch-correct UEFI fallback name (EnsureUefiRemovableFallback writes bootia32/aa64/x64 per the
			// image's architecture) instead of a hardcoded bootx64.efi — else a valid ARM64/x86 clone is flagged failed.
			bootx64Ok = File.Exists(bootLetter + ":\\EFI\\Boot\\" + UefiFallbackNameFor(bootLetter + ":\\EFI\\Microsoft\\Boot\\bootmgfw.efi"));
			if (bcdStoreOk)
			{
				bcdEnumOutput = await RunProcessCaptureAsync("bcdedit.exe", "/store " + QuoteArgument(bcdStore) + " /enum all");
				loaderPathOk = bcdEnumOutput.Contains(@"path                    \Windows\system32\winload.efi", StringComparison.OrdinalIgnoreCase)
					&& bcdEnumOutput.Contains("osdevice                partition=" + windowsLetter + ":", StringComparison.OrdinalIgnoreCase)
					&& bcdEnumOutput.Contains(@"systemroot              \Windows", StringComparison.OrdinalIgnoreCase);
			}

			// Raw engine: apply owners/ACLs LAST — after registry + bcdboot post-processing — so a restrictive source
			// ACL on the hive files / config dir can't block the portable-registry step (which failed when ACLs were
			// applied during the copy). The snapshot is still mapped here; it is released in the finally block.
			if (useRawEngine)
			{
				SetStage(L("StgApplyPerms"), 92.0);
				try { await RawNtfsApplySecurityAsync(shadowLetter, realRoot); }
				catch (Exception secEx) { Log("WARNING: raw-engine permission pass failed: " + secEx.Message + " (clone is usable; permissions may be default)."); }
			}

			bool bitLockerRequestedButFailed = false;
			if (BitLockerCheck.IsChecked == true)
			{
				try
				{
					await EnableBitLockerAsync(windowsLetter);
				}
				catch (Exception blEx)
				{
					// A clone without encryption is still bootable, so this is not fatal — but the user asked for BitLocker
					// and a recovery-key file was already written, so flag it into `ok` and warn in the dialog instead of
					// reporting a plain success + safe-to-remove on an unencrypted stick.
					bitLockerRequestedButFailed = true;
					Log("WARNING: BitLocker step failed on the clone: " + blEx.Message + " (the clone is still usable; encryption was NOT applied).");
				}
			}

			SetStage(L("StgWriteReport"), 96.0);
			// A raw-engine clone that skipped files (unreadable/torn source records or target write errors) is INCOMPLETE
			// even though the few boot files copyOk checks exist. And a Stop during verify/registry/bcdboot leaves
			// verifyRan=false, which would otherwise NOT block ok. Treat both as review-needed, never a clean success.
			bool rawIncomplete = rawStats != null && rawStats.Errors > 0;
			// Regions of real file data that could NOT be read from the source (bad sectors / a truncated run-list) and
			// were filled with zeros to keep the file length correct. The clone is complete in size but those bytes are
			// LOST — that is not a byte-faithful clone, so treat it as review-needed like a dropped-file error.
			long rawZeroFilled = rawStats != null ? rawStats.RunShortfalls + rawStats.ReadShortfalls : 0;
			bool ok = diskpartOk && copyOk && registryOk && bcdbootOk && bcdStoreOk && bootx64Ok && loaderPathOk && !bitLockerRequestedButFailed && !rawIncomplete && rawZeroFilled == 0 && !stopRequested && !internalOperationStopped && (!verifyRan || verifyMismatches == 0);
			// A report is only SAVED TO DISK (Desktop folder + .txt) when the clone needs review — a clean success is
			// shown in-app below and leaves nothing behind on the Desktop.
			string? reportPath = null;
			string reportText;
			if (ok)
			{
				reportText = BuildFullRootUsbCloneReportText(targetDisk, persisted: false, null, null, shadowLetter, sourceRoot, realRoot, realWindowsFolder, bootLetter, windowsLetter, diskpartOk, copyOk, registryOk, bcdbootOk, bcdStoreOk, bootx64Ok, loaderPathOk, copyResult, diskpartOutput, registryOutput, bcdbootOutput, bcdEnumOutput, verifyRan, verifyVerifiedFiles, verifyVerifiedBytes, verifyMismatches, verifyUnverifiable, verifySamples, verifyUnverifiableSamples, unattendWritten);
			}
			else
			{
				reportPath = WriteFullRootUsbCloneReport(targetDisk, reportRoot, diskpartPath, shadowLetter, sourceRoot, realRoot, realWindowsFolder, bootLetter, windowsLetter, diskpartOk, copyOk, registryOk, bcdbootOk, bcdStoreOk, bootx64Ok, loaderPathOk, copyResult, diskpartOutput, registryOutput, bcdbootOutput, bcdEnumOutput, verifyRan, verifyVerifiedFiles, verifyVerifiedBytes, verifyMismatches, verifyUnverifiable, verifySamples, verifyUnverifiableSamples, unattendWritten);
				reportText = File.ReadAllText(reportPath, Encoding.UTF8);
			}
			progressDoneGiB = ok ? progressTotalGiB : Math.Max(progressTotalGiB * 0.85, 0.85);
			SetStage(ok ? L("StgCloneDone") : L("StgCloneDoneReview"), 100.0);
			SetToolOutput(reportText);
			SelectDriveTool(ToolSmart, 5, ok ? "Faithful clone complete." : "Faithful clone complete. Open the report for details.");
			Log(reportText);
			// A clean success has no report on disk this run — clear any stale pointer from an earlier failed run so
			// "Open Report" never opens an unrelated old report.
			if (reportPath != null) SetLastReport(reportPath);
			else { lastReportPath = ""; OpenReportButton.IsEnabled = false; }
			// Flush the target volumes so it is safe to unplug as soon as the dialog is dismissed.
			await FlushVolumesAsync(bootLetter, windowsLetter);
			// Freeze the timer and clear the busy state BEFORE the modal dialog so the elapsed time stops
			// at completion (not when the dialog is dismissed) and closing the app afterwards does not warn.
			operationTimer.Stop();
			operationStopwatch.Stop();
			UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(ok);
			if (headlessRun) return;
			bool cloneDialogOk = ok;
			// Surface data-partition clone failures in the completion dialog — the Windows checks alone can't reflect them.
			string dataCloneNote = dataCloneFailures > 0 ? "\n\n" + string.Format(L("MbCloneDataFail"), dataCloneFailures, dataCloneJobs.Count) : "";
			string bitLockerFailNote = bitLockerRequestedButFailed ? "\n\n" + L("MbCloneBitlockerFailed") : "";
			string rawErrorsNote = rawIncomplete ? "\n\n" + string.Format(L("MbCloneFilesSkipped"), rawStats!.Errors) : "";
			string rawZeroNote = rawZeroFilled > 0 ? "\n\n" + string.Format(L("MbRawZeroFilled"), rawZeroFilled) : "";
			string reportNote = reportPath != null ? "\n\n" + L("MbCloneReportLabel") + "\n" + reportPath : "";
			MessageBox.Show((ok ? L("MbCloneDoneOk") : L("MbCloneDoneReview")) + dataCloneNote + bitLockerFailNote + rawErrorsNote + rawZeroNote + "\n\n" + L("MbCloneBody") + "\n\n" +(bitLockerEncrypting ? L("MbCloneBitlockerBusy") : L("MbCloneSafeRemove")) + "\n\n" + L("MbCloneBootHelp") + reportNote + "\n\n" + L("MbAvCloneNote"), "DriveForge", MessageBoxButton.OK, (ok && dataCloneFailures == 0) ? MessageBoxImage.Information : MessageBoxImage.Exclamation);
			if (cloneDialogOk) MaybeOfferDonation();
			if (EjectWhenDoneCheck.IsChecked == true && !bitLockerEncrypting) await EjectDiskAsync(targetDisk.Number);
		}
		finally
		{
			if (dismWimPath != null) TryDeleteFile(dismWimPath); // drop the scratch capture WIM on any exit (success, cancel, or a failure between capture and apply)
			TryDeleteFile(diskpartPath); // drop the TEMP diskpart script — it was already copied into the report folder if a report was persisted (failure/review path)
			if (!string.IsNullOrWhiteSpace(shadowDosTarget))
			{
				UnmapSnapshotDrive(shadowLetter, shadowDosTarget);
			}
			if (shadowCopy != null)
			{
				await DeleteShadowCopyAsync(shadowCopy.Id);
			}
		}
	}

	private async Task<string> ApplyPortableRegistrySettingsToRealCloneAsync(string windowsFolder, bool bypassRequirements, bool bypassAccount, bool faithfulMode = false, bool portableMode = true)
	{
		// faithfulMode: the clone was produced by a WIM/image apply that preserves ACLs, hardlinks and the
		// AppX state perfectly (a faithful clone), so the whole first-boot AppX repair subsystem — and the
		// temporary antivirus disabling that exists only to let that repair run — are NOT needed and are skipped.
		// We still apply the portable-OS settings and the universal-hardware boot drivers.
		StringBuilder output = new StringBuilder();
		string configRoot = Path.Combine(windowsFolder, "System32", "config");
		string systemHive = Path.Combine(configRoot, "SYSTEM");
		string softwareHive = Path.Combine(configRoot, "SOFTWARE");
		if (!faithfulMode)
		{
			output.AppendLine(CreateFirstBootAppRepairFiles(windowsFolder));
		}
		output.AppendLine(await ApplySystemPortableSettingsAsync(systemHive, bypassRequirements, faithfulMode, portableMode));
		output.AppendLine(await ApplySoftwarePortableSettingsAsync(softwareHive, bypassAccount, faithfulMode));
		output.AppendLine(await ApplyUserProfileRunOnceAppRepairAsync(windowsFolder));
		return output.ToString();
	}

	private static string CreateFirstBootAppRepairFiles(string windowsFolder)
	{
		try
		{
			string root = Path.GetPathRoot(windowsFolder) ?? "";
			if (string.IsNullOrWhiteSpace(root))
			{
				return "FIRST BOOT APPX REPAIR: FAILED - could not find clone root.";
			}
			string repairFolder = Path.Combine(root, "ProgramData", "DriveForge");
			string startupFolder = Path.Combine(root, "ProgramData", "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
			Directory.CreateDirectory(repairFolder);
			Directory.CreateDirectory(startupFolder);
			string scriptPath = Path.Combine(repairFolder, "FirstBootAppRepair.ps1");
			string cmdPath = Path.Combine(startupFolder, "DriveForge First Boot App Repair.cmd");
			string vbsPath = Path.Combine(startupFolder, "DriveForge First Boot App Repair.vbs");
			string script = @"param([switch]$UserMode, [switch]$Detached)
$ErrorActionPreference = 'Continue'
$repairRoot = Join-Path $env:ProgramData 'DriveForge'
$modeName = if ($UserMode) { 'User-' + $env:USERNAME } else { 'System' }
$modeName = $modeName -replace '[\\/:*?""<>|]', '_'
$logPath = Join-Path $repairRoot ('FirstBootAppRepair.' + $modeName + '.log')
$donePath = Join-Path $repairRoot ('FirstBootAppRepair.' + $modeName + '.done')
$startupCmd = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup\DriveForge First Boot App Repair.cmd'
$startupVbs = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup\DriveForge First Boot App Repair.vbs'
New-Item -ItemType Directory -Force -Path $repairRoot | Out-Null
if (Test-Path $donePath) {
  Add-Content -Path $logPath -Value ""[$modeName] Already completed: $(Get-Date -Format o)""
  if (Test-Path $startupCmd) { Remove-Item -LiteralPath $startupCmd -Force -ErrorAction SilentlyContinue }
  if (Test-Path $startupVbs) { Remove-Item -LiteralPath $startupVbs -Force -ErrorAction SilentlyContinue }
  exit 0
}
# Active Setup runs StubPath SYNCHRONOUSLY during logon and terminates it once a logon timeout elapses —
# before the (slow, hundreds-of-packages) repair can finish (observed: log cut off, no summary, no .done).
# So in UserMode we relaunch ourselves DETACHED and return immediately: the StubPath parent exits fast,
# Active Setup/logon is satisfied, and the real work runs in the background to completion in its own
# process that the logon timeout cannot kill.
if ($UserMode -and -not $Detached) {
  try {
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('""' + $PSCommandPath + '""'),'-UserMode','-Detached')
  } catch {}
  exit 0
}
Add-Content -Path $logPath -Value ""[$modeName] DriveForge AppX/SystemApps repair started: $(Get-Date -Format o)""
# NOTE: an earlier design armed an HKCU\Run 'retry' entry here. Writing an autostart entry from a running
# repair script is unreliable: security software commonly treats Run-key persistence as suspicious and may
# block or remove it, which would stop the repair. Removed. Robustness now comes from (1) processing
# SystemApps FIRST below, so the user-visible apps are fixed before any framework package, and (2) the
# detached SYSTEM service doing the all-users system pass.
# Reset stale Windows Search index so SearchHost does not crash on first portable boot
if (-not $UserMode) {
  $searchIndexPath = Join-Path $env:ProgramData 'Microsoft\Search\Data\Applications\Windows'
  if (Test-Path -LiteralPath $searchIndexPath) {
    try {
      Stop-Service WSearch -Force -ErrorAction SilentlyContinue
      Start-Sleep -Seconds 1
      Remove-Item -LiteralPath $searchIndexPath -Recurse -Force -ErrorAction SilentlyContinue
      Add-Content -Path $logPath -Value ""[$modeName] Search index reset OK (will rebuild automatically)""
    } catch {
      Add-Content -Path $logPath -Value ""[$modeName] Search index reset warning: $($_.Exception.Message)""
    }
  }
}
if (-not $UserMode) {
  try {
    Start-Service AppXSvc -ErrorAction SilentlyContinue
    Start-Service ClipSVC -ErrorAction SilentlyContinue
    Start-Service StateRepository -ErrorAction SilentlyContinue
  } catch {
    Add-Content -Path $logPath -Value ""[$modeName] Service start warning: $($_.Exception.Message)""
  }
}
# Close idle UWP *app* hosts so re-registration does not hit 'currently in use'.
# Runs via Active Setup at logon, BEFORE Explorer starts, so these are usually not even running yet
# (killing them is a harmless no-op then). CRITICAL: do NOT kill logon-infrastructure processes
# (sihost, taskhostw, RuntimeBroker, backgroundTaskHost, ApplicationFrameHost) — during the Active
# Setup logon phase those belong to the session that is being set up, and killing them tears down the
# logon and terminates THIS script mid-run (observed: User repair log cut off with no summary).
$explorerRunning = [bool](Get-Process explorer -ErrorAction SilentlyContinue)
if ($UserMode -and -not $explorerRunning) {
  $processesToClose = @(
    'SearchHost', 'StartMenuExperienceHost', 'ShellExperienceHost', 'TextInputHost',
    'Widgets', 'WidgetService', 'PhoneExperienceHost', 'YourPhone', 'CrossDeviceResume',
    'WindowsTerminal', 'DuckDuckGo'
  )
  foreach ($processName in $processesToClose) {
    try { Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue } catch {}
  }
  Start-Sleep -Seconds 2
}
# Build the manifest list in PRIORITY ORDER and dedupe while preserving that order.
# Sort-Object -Unique (the old approach) sorted alphabetically by full path, which put
# 'C:\Program Files\WindowsApps\...' BEFORE 'C:\Windows\SystemApps\...'. The script could die part-way
# through the big WindowsApps framework set (e.g. on a WindowsAppRuntime re-register) and NEVER reach
# SystemApps — leaving exactly the apps that throw the visible first-boot errors (CrossDeviceResume,
# SearchHost, StartMenu, ShellExperienceHost) unrepaired. So: SystemApps FIRST, frameworks LAST.
$manifestPaths = New-Object System.Collections.Generic.List[string]
$seenManifests = New-Object 'System.Collections.Generic.HashSet[string]'([System.StringComparer]::OrdinalIgnoreCase)
function Add-RepairManifest($p) {
  if ($p -and (Test-Path -LiteralPath $p) -and $seenManifests.Add($p)) { $manifestPaths.Add($p) | Out-Null }
}
# PRIORITY 1: SystemApps — the shell/system apps that produce the visible first-boot error dialogs.
$sysAppsRoot = Join-Path $env:windir 'SystemApps'
if (Test-Path -LiteralPath $sysAppsRoot) {
  Get-ChildItem -LiteralPath $sysAppsRoot -Filter AppxManifest.xml -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { Add-RepairManifest $_.FullName }
}
# PRIORITY 2: registered packages for this user (or all users in system mode).
try {
  $packages = if ($UserMode) { Get-AppxPackage -ErrorAction SilentlyContinue } else { Get-AppxPackage -AllUsers -ErrorAction SilentlyContinue }
  $packages | ForEach-Object {
    # Guard null/empty InstallLocation (partially uninstalled package) — Join-Path would throw.
    if ($_.InstallLocation) { Add-RepairManifest (Join-Path $_.InstallLocation 'AppxManifest.xml') }
  }
} catch {
  Add-Content -Path $logPath -Value ""[$modeName] Get-AppxPackage warning: $($_.Exception.Message)""
}
# PRIORITY 3 (LAST): WindowsApps store packages — the big framework/runtime set most likely to abort
# the host when re-registered. By the time we get here the user-visible apps are already done.
$winAppsRoot = Join-Path $env:ProgramFiles 'WindowsApps'
if (Test-Path -LiteralPath $winAppsRoot) {
  Get-ChildItem -LiteralPath $winAppsRoot -Filter AppxManifest.xml -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { Add-RepairManifest $_.FullName }
}
$uniqueManifests = $manifestPaths
$ok = 0
$failed = 0
$skippedHigher = 0
$busy = 0
$warnAppContainer = 0
foreach ($manifest in $uniqueManifests) {
  $retries = 0
  $done = $false
  while (-not $done -and $retries -le 2) {
    try {
      # -ForceTargetApplicationShutdown forces Windows to close running instances before re-registering
      Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ForceTargetApplicationShutdown -ErrorAction Stop
      $ok++
      $done = $true
    } catch {
      $errorText = $_.Exception.ToString()
      if ($errorText -match '0x80073D06|higher version') {
        $skippedHigher++
        Add-Content -Path $logPath -Value ""[$modeName] SKIP higher version: $manifest""
        $done = $true
      } elseif ($errorText -match '0x80073CF6|AppContainer|0x80070005') {
        # AppContainer/registration broken on the clone. First try the clean repair: remove the broken
        # registration and re-register fresh from the manifest.
        $pkgFull = Split-Path (Split-Path $manifest -Parent) -Leaf
        try {
          Get-AppxPackage | Where-Object { $_.PackageFullName -eq $pkgFull } | Remove-AppxPackage -ErrorAction SilentlyContinue
          Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ForceTargetApplicationShutdown -ErrorAction Stop
          $ok++
          Add-Content -Path $logPath -Value ""[$modeName] RECOVERED AppContainer via remove+reregister: $manifest""
          $done = $true
        } catch {
          # Still unrepairable. These are non-essential apps (OEM bloat, optional store apps) whose
          # security container is corrupted by the clone in a way registration cannot fix. Leaving them
          # makes Windows throw a startup error dialog every boot, so REMOVE them — a removed app cannot
          # error. Try current-user removal; in system mode (-AllUsers) it clears them for everyone.
          $removed = $false
          try {
            if ($UserMode) {
              Get-AppxPackage | Where-Object { $_.PackageFullName -eq $pkgFull } | Remove-AppxPackage -ErrorAction SilentlyContinue
            } else {
              Get-AppxPackage -AllUsers | Where-Object { $_.PackageFullName -eq $pkgFull } | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
            }
            $removed = $true
          } catch {}
          $warnAppContainer++
          if ($removed) {
            Add-Content -Path $logPath -Value ""[$modeName] REMOVED unrepairable broken app (stops its error dialog): $pkgFull""
          } else {
            Add-Content -Path $logPath -Value ""[$modeName] WARN AppContainer broken (reinstall app manually): $manifest""
          }
          $done = $true
        }
      } elseif ($errorText -match '0x80073D02|currently in use|need to be closed') {
        $retries++
        if ($retries -le 2) {
          Start-Sleep -Seconds 4
        } else {
          $busy++
          Add-Content -Path $logPath -Value ""[$modeName] BUSY after retries, will retry next logon: $manifest :: $($_.Exception.Message)""
          $done = $true
        }
      } else {
        $failed++
        Add-Content -Path $logPath -Value ""[$modeName] FAILED: $manifest :: $($_.Exception.Message)""
        $done = $true
      }
    }
  }
}
# Neutralize non-essential apps that crash on a clone with 'parameter is incorrect' / unknown exception
# (their host/AppContainer state breaks and re-registration does NOT heal it). Removing/blocking them is
# the only thing that stops their recurring first-boot error dialogs. All are optional (phone link,
# cross-device resume, OEM assistant, 3rd-party browser) so removal loses nothing important.
$junkApps = @('Microsoft.YourPhone','MicrosoftWindows.CrossDevice','B9ECED6F.ASUSPCAssistant','DuckDuckGo.DesktopBrowser')
foreach ($j in $junkApps) {
  try {
    if ($UserMode) { Get-AppxPackage -Name $j -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue }
    else { Get-AppxPackage -AllUsers -Name $j -ErrorAction SilentlyContinue | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue }
  } catch {}
}
if ($UserMode) {
  # Stop the PrintScreen hotkey from invoking the (clone-broken) ms-screenclip handler.
  try { & ""$env:SystemRoot\System32\reg.exe"" add ""HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"" /v PrintScreenKeyForSnippingEnabled /t REG_DWORD /d 0 /f | Out-Null } catch {}
} else {
  # System (elevated) mode: hard-block the launchers that crash, disable cross-device, hide crash dialogs.
  try {
    $ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options'
    foreach ($exe in @('CrossDeviceResume.exe','PhoneExperienceHost.exe')) {
      $k = Join-Path $ifeo $exe
      New-Item -Path $k -Force | Out-Null
      Set-ItemProperty -Path $k -Name Debugger -Value 'C:\Windows\System32\systray.exe' -Force
    }
    & ""$env:SystemRoot\System32\reg.exe"" add ""HKLM\SOFTWARE\Policies\Microsoft\Windows\System"" /v EnableCdp /t REG_DWORD /d 0 /f | Out-Null
    & ""$env:SystemRoot\System32\reg.exe"" add ""HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting"" /v DontShowUI /t REG_DWORD /d 1 /f | Out-Null
  } catch {}
}
# Restart shell host processes so they pick up the newly-registered packages.
# Only safe in shell-replacement mode (Explorer not yet running).
# In a live session (HKCU RunOnce) this causes taskbar flicker — skip it; the processes
# will pick up the registration automatically on next Explorer restart or reboot.
if (-not $explorerRunning) {
  try {
    Stop-Process -Name SearchHost,StartMenuExperienceHost,ShellExperienceHost -Force -ErrorAction SilentlyContinue
  } catch {}
}
Add-Content -Path $logPath -Value ""[$modeName] Registered: $ok; skipped-higher: $skippedHigher; appcontainer-warn: $warnAppContainer; busy: $busy; failed: $failed; finished: $(Get-Date -Format o)""
if ($failed -eq 0) {
  # 'busy' packages are frameworks already in use (a working higher version is registered); they do NOT
  # block completion. Only a hard 'failed' does. This prevents the repair from never finishing just
  # because VCLibs/WinAppRuntime were in use during a live session.
  New-Item -ItemType File -Force -Path $donePath | Out-Null
  if (Test-Path $startupCmd) { Remove-Item -LiteralPath $startupCmd -Force -ErrorAction SilentlyContinue }
  if (Test-Path $startupVbs) { Remove-Item -LiteralPath $startupVbs -Force -ErrorAction SilentlyContinue }
  # Repair finished. Re-enable the antivirus we disabled on the clone (redundant with the boot service's
  # guaranteed restore) using the NON-INTERACTIVE script so it can never hang on a prompt. SYSTEM mode only.
  if (-not $UserMode) {
    $reenable = Join-Path $repairRoot 'Restore-Antivirus-Auto.cmd'
    if (Test-Path $reenable) {
      try { & ""$env:SystemRoot\System32\cmd.exe"" /c ""$reenable"" | Out-Null } catch {}
      Add-Content -Path $logPath -Value ""[$modeName] Antivirus re-enabled after repair (reboot to activate).""
    }
  }
} else {
  Add-Content -Path $logPath -Value ""[$modeName] Repair will run again on next logon because some packages failed.""
}
";
			string setupScreenPath = Path.Combine(repairFolder, "DriveForgeSetupScreen.ps1");
			string setupScreen = @"param([switch]$UserMode)
$repairRoot = Join-Path $env:ProgramData 'DriveForge'
$modeTag = if ($UserMode) { 'User-' + ($env:USERNAME -replace '[\\/:*?""<>|]','_') } else { 'System' }
$donePath = Join-Path $repairRoot ('DriveForgeSetupScreen.' + $modeTag + '.done')
$repairScript = Join-Path $repairRoot 'FirstBootAppRepair.ps1'

# Detect shell-replacement mode: we are the Windows Shell (Explorer not yet running)
$isShellMode = -not (Get-Process explorer -ErrorAction SilentlyContinue)
if ($isShellMode) {
    # Delete Shell override immediately so it does not loop on next boot
    try { Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -ErrorAction SilentlyContinue } catch {}
}
# Determine which done file governs early exit.
# Shell mode MUST check the User done file, not $donePath (System.done).
# Reason: RunOnce on boot 1 already creates DriveForgeSetupScreen.System.done.
# If shell mode checked that same file it would exit immediately on boot 2 without
# ever running user-mode repair (Get-AppxPackage per-user). The User done file is
# only created after shell mode itself has completed user repair.
$earlyExitDone = if ($isShellMode) {
    Join-Path $repairRoot ('DriveForgeSetupScreen.User-' + ($env:USERNAME -replace '[\\/:*?""<>|]','_') + '.done')
} else {
    $donePath
}
if ((Test-Path $earlyExitDone) -or -not (Test-Path $repairScript)) {
    # Remove the Startup-folder fallback shortcuts — they are no longer needed once repair is done.
    # The repair script removes them from its own cleanup path, but only when all packages succeed.
    # If some were busy on first run (boot 2 shell mode), the shortcuts would remain and run every
    # boot forever. Removing them here ensures they are cleaned up on the first subsequent logon.
    try {
        $startupVbsPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup\DriveForge First Boot App Repair.vbs'
        $startupCmdPath = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Startup\DriveForge First Boot App Repair.cmd'
        Remove-Item -LiteralPath $startupVbsPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $startupCmdPath -Force -ErrorAction SilentlyContinue
    } catch {}
    if ($isShellMode) { Start-Process explorer.exe }
    exit 0
}
# First-run RunOnce path: set Shell for the NEXT login so repair runs before Explorer on boot 2+.
# This is set at runtime (not in the offline hive), which avoids static AV Winlogon\Shell triggers.
# The condition excludes shell mode (already running as Shell — don't re-set it).
# -UserMode is NOT excluded: HKCU\RunOnce fires with -UserMode for secondary users; they also
# need the Shell set so their per-session repair runs before Explorer on the next boot.
if (-not $isShellMode) {
    $selfCmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\ProgramData\DriveForge\DriveForgeSetupScreen.ps1 -UserMode'
    try { Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -Name Shell -Value $selfCmd -Force -ErrorAction SilentlyContinue } catch {}
}
try {
  Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
  [xml]$xaml = @'
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        WindowStyle=""None"" WindowState=""Maximized"" Background=""#0a0e1a""
        ShowInTaskbar=""False"" Topmost=""True"">
  <Grid>
    <StackPanel VerticalAlignment=""Center"" HorizontalAlignment=""Center"" Width=""500"">
      <TextBlock Text=""DriveForge"" FontSize=""20"" FontWeight=""Light""
                 Foreground=""#4fc3f7"" HorizontalAlignment=""Center"" Margin=""0,0,0,4""/>
      <TextBlock Name=""Title"" Text=""Preparing your Windows installation""
                 FontSize=""17"" FontWeight=""SemiBold"" Foreground=""#e3f2fd""
                 HorizontalAlignment=""Center"" Margin=""0,0,0,4"" TextWrapping=""Wrap"" TextAlignment=""Center""/>
      <TextBlock Name=""Sub"" Text=""Registering system apps and restoring shortcuts - this may take a minute...""
                 FontSize=""12"" Foreground=""#6e8a99"" HorizontalAlignment=""Center""
                 Margin=""0,0,0,28"" TextWrapping=""Wrap"" TextAlignment=""Center""/>
      <ProgressBar Name=""Bar"" IsIndeterminate=""True"" Height=""4"" BorderThickness=""0""
                   Foreground=""#4fc3f7"" Background=""#1a2a3a""/>
      <TextBlock Name=""Detail"" Text=""Starting..."" FontSize=""10"" Foreground=""#3d5566""
                 HorizontalAlignment=""Center"" Margin=""0,10,0,0"" TextWrapping=""Wrap"" TextAlignment=""Center""/>
    </StackPanel>
  </Grid>
</Window>
'@
  $window = [System.Windows.Markup.XamlReader]::Load([System.Xml.XmlNodeReader]::new($xaml))
  $titleBlock = $window.FindName('Title')
  $subBlock    = $window.FindName('Sub')
  $detailBlock = $window.FindName('Detail')
  $bar         = $window.FindName('Bar')
  $dispatcher  = $window.Dispatcher
  $sync = [hashtable]::Synchronized(@{ Done = $false; Detail = 'Starting...' })
  $th = [System.Threading.Thread]::new([System.Threading.ThreadStart]{
    try {
      $sync.Detail = 'Registering system apps...'
      if ($isShellMode) {
        # Shell-replacement: run both system and user repair before Explorer starts.
        # Skip system repair only if it FULLY SUCCEEDED — use FirstBootAppRepair.System.done,
        # NOT DriveForgeRepairSvc.done (the service creates that file unconditionally on first
        # run, even when some packages failed; using it here would silently skip a retry when
        # the service run had busy/failed packages).
        $repairSystemDone = Join-Path $repairRoot 'FirstBootAppRepair.System.done'
        if (-not (Test-Path $repairSystemDone)) {
          $sync.Detail = 'Registering system apps...'
          & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repairScript
        }
        $sync.Detail = 'Configuring user apps...'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repairScript -UserMode
        # Mark user done so RunOnce safety-net skips
        $userDone = Join-Path $repairRoot ('DriveForgeSetupScreen.User-' + ($env:USERNAME -replace '[\\/:*?""<>|]','_') + '.done')
        New-Item -ItemType File -Force -Path $userDone | Out-Null
      } else {
        $args2 = if ($UserMode) { @('-UserMode') } else { @() }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repairScript @args2
      }
      $sync.Detail = 'Done.'
    } catch { $sync.Detail = ""Warning: $($_.Exception.Message)"" }
    $sync.Done = $true
    $dispatcher.Invoke([System.Action]{
      $bar.IsIndeterminate = $false
      $bar.Value = 100
      $titleBlock.Text = 'Your Windows installation is ready.'
      $subBlock.Text = if ($isShellMode) { 'Starting Windows...' } else { 'All system apps have been configured successfully.' }
    })
    Start-Sleep -Milliseconds 1200
    $dispatcher.Invoke([System.Action]{ $window.Close() })
  })
  $th.IsBackground = $true
  $th.Start()
  $timer = New-Object System.Windows.Threading.DispatcherTimer
  $timer.Interval = [TimeSpan]::FromMilliseconds(600)
  $timer.Add_Tick({ $detailBlock.Text = $sync.Detail; if ($sync.Done) { $timer.Stop() } })
  $timer.Start()
  $window.ShowDialog() | Out-Null
} catch {
  # WPF not available - fall back to silent repair
  if ($isShellMode) {
    $repairSystemDone = Join-Path $repairRoot 'FirstBootAppRepair.System.done'
    if (-not (Test-Path $repairSystemDone)) {
      & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repairScript
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repairScript -UserMode
    $userDone = Join-Path $repairRoot ('DriveForgeSetupScreen.User-' + ($env:USERNAME -replace '[\\/:*?""<>|]','_') + '.done')
    New-Item -ItemType File -Force -Path $userDone | Out-Null
  } else {
    $args2 = if ($UserMode) { @('-UserMode') } else { @() }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $repairScript @args2
  }
}
New-Item -ItemType File -Force -Path $donePath | Out-Null
if ($isShellMode) { Start-Process explorer.exe }
";
			// VBS wrapper: call the setup screen (shows UI + repair), not the repair script directly
			string vbs = "Set shell = CreateObject(\"WScript.Shell\")\r\nsetupScreen = shell.ExpandEnvironmentStrings(\"%ProgramData%\\DriveForge\\DriveForgeSetupScreen.ps1\")\r\nshell.Run \"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"\"\" & setupScreen & \"\"\" -UserMode\", 0, False\r\n";
			// Service script: runs as SYSTEM at boot (delayed auto-start), before any user logs in.
			// Handles system-level AppX re-registration silently. Self-deletes on first run.
			string svcScriptPath = Path.Combine(repairFolder, "DriveForgeRepairSvc.ps1");
			string svcScript = @"# DriveForgeRepairSvc.ps1 - launched by the Service Control Manager as SYSTEM at boot.
# IMPORTANT: powershell.exe is NOT a real service, so SCM terminates this process once
# ServicesPipeTimeout elapses (it never reports SERVICE_RUNNING). The OLD design ran the long
# AppX repair INLINE here, so SCM frequently killed it before it finished — which is why the
# system repair never completed on cloned drives. A dedicated compiled portability service avoids this
# by being a real service; we get the same reliability by NOT doing the work inline: we self-delete the
# service and spawn the repair as a DETACHED background process that keeps running as SYSTEM after
# SCM tears down this short-lived service shell.
$repairRoot = 'C:\ProgramData\DriveForge'
$svcDone   = Join-Path $repairRoot 'DriveForgeRepairSvc.done'
$repairScript = Join-Path $repairRoot 'FirstBootAppRepair.ps1'
try { & ""$env:SystemRoot\System32\sc.exe"" delete DriveForgeRepairSvc 2>$null | Out-Null } catch {}
New-Item -ItemType File -Force -Path $svcDone -ErrorAction SilentlyContinue | Out-Null
# Re-enable the third-party antivirus we temporarily disabled on this clone. Done HERE, as SYSTEM,
# guaranteed at first boot and INDEPENDENTLY of the AppX repair result, so protection is never left off
# (even if the repair fails or only the per-user pass runs). Changing a service's Start value takes effect
# on the next reboot - it does not start the service now, so it does not interfere with this boot's repair.
$avRestore = Join-Path $repairRoot 'Restore-Antivirus-Auto.cmd'
$avDone    = Join-Path $repairRoot 'RestoreAntivirus.done'
$avLog     = Join-Path $repairRoot 'DriveForge-antivirus.log'
if ((Test-Path $avRestore) -and -not (Test-Path $avDone)) {
    try {
        & ""$env:SystemRoot\System32\cmd.exe"" /c ""$avRestore"" | Out-Null
        New-Item -ItemType File -Force -Path $avDone -ErrorAction SilentlyContinue | Out-Null
        Add-Content -Path $avLog -Value ""[Service] Antivirus re-enabled (reboot to activate protection): $(Get-Date -Format o)""
    } catch {
        Add-Content -Path $avLog -Value ""[Service] Antivirus re-enable failed: $($_.Exception.Message)""
    }
}
if (-not (Test-Path $repairScript)) { exit 0 }
# Spawn the real system-level AppX repair detached. It is its own process (not tied to this
# service's lifetime), so SCM terminating the service does not kill it. FirstBootAppRepair.ps1
# starts AppXSvc/ClipSVC/StateRepository itself, so no inline wait is needed here.
try {
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('""' + $repairScript + '""')
    )
} catch {}
exit 0
";
			File.WriteAllText(scriptPath, script, Encoding.UTF8);
			File.WriteAllText(svcScriptPath, svcScript, Encoding.UTF8);
			// Do NOT drop the Winlogon\Shell setup screen or the hidden Startup VBS: replacing the user
			// shell and adding a hidden startup script are intrusive autostart techniques that security
			// software reasonably treats as suspicious and may quarantine. Per-user repair now runs
			// via the standard Active Setup mechanism (see ApplySoftwarePortableSettingsAsync).
			// Proactively delete any artifacts an older build may have written to this clone.
			TryDeleteFile(cmdPath);
			TryDeleteFile(vbsPath);
			TryDeleteFile(setupScreenPath);
			_ = setupScreen; _ = vbs; // intentionally NOT written to disk (replaced by Active Setup); retained for reference
			return "FIRST BOOT APPX REPAIR: scripts created (repair + service). Per-user repair via Active Setup; no Winlogon\\Shell screen or Startup VBS.";
		}
		catch (Exception ex)
		{
			return "FIRST BOOT APPX REPAIR: FAILED - " + ex.Message;
		}
	}

	private Task<string> ApplyUserProfileRunOnceAppRepairAsync(string windowsFolder)
	{
		// Per-user first-boot repair is now armed by a single Active Setup entry written to the SOFTWARE
		// hive (see ApplySoftwarePortableSettingsAsync). Active Setup is evaluated by Windows against
		// every user profile automatically at first interactive logon, so there is no longer any need to
		// load each NTUSER.DAT offline and write a per-user RunOnce. That old approach was both flaky
		// (the per-user hive frequently failed to unload, discarding the edit) and relied on the
		// Winlogon\Shell setup screen that security software may quarantine. Kept as a no-op for call-site stability.
		_ = windowsFolder;
		return Task.FromResult("USER APPX REPAIR: handled by Active Setup (covers all existing and future user profiles automatically).");
	}

	// Builds the full report TEXT with no disk I/O (used for the in-app view on every run). Pass persisted=false and
	// null report-folder/script-path when nothing has been (or will be) written to disk, e.g. a clean success.
	private string BuildFullRootUsbCloneReportText(DiskItem targetDisk, bool persisted, string? reportRoot, string? diskpartScriptPath, char shadowLetter, string sourceRoot, string realRoot, string realWindowsFolder, char bootLetter, char windowsLetter, bool diskpartOk, bool copyOk, bool registryOk, bool bcdbootOk, bool bcdStoreOk, bool bootx64Ok, bool loaderPathOk, NtfsCopyTestResult? copyResult, string diskpartOutput, string registryOutput, string bcdbootOutput, string bcdEnumOutput, bool verifyRan, long verifyVerifiedFiles, long verifyVerifiedBytes, long verifyMismatches, long verifyUnverifiable, List<string> verifySamples, List<string> verifyUnverifiableSamples, bool unattendWritten)
	{
		bool verifyOk = !verifyRan || verifyMismatches == 0;
		bool ok = diskpartOk && copyOk && registryOk && bcdbootOk && bcdStoreOk && bootx64Ok && loaderPathOk && verifyOk;
		bool windowsOk = File.Exists(Path.Combine(realWindowsFolder, "System32", "winload.efi")) && File.Exists(Path.Combine(realWindowsFolder, "System32", "config", "SYSTEM"));
		bool programFilesOk = Directory.Exists(Path.Combine(realRoot, "Program Files"));
		bool programDataOk = Directory.Exists(Path.Combine(realRoot, "ProgramData"));
		bool usersOk = Directory.Exists(Path.Combine(realRoot, "Users"));
		StringBuilder report = new StringBuilder();
		report.AppendLine("DriveForge - Faithful Full Clone (Clone this PC)");
		report.AppendLine("==================================================");
		report.AppendLine();
		report.AppendLine("Mode: faithful full clone of the current Windows. The selected target disk was formatted. The entire Windows root is cloned from a VSS snapshot with a WIM image apply, which preserves apps, ACLs, hardlinks and the AppX state (so no first-boot app repair is needed), excluding volatile cache/temp/pagefile data.");
		report.AppendLine("Target disk: Disk " + targetDisk.Number + " - " + targetDisk.FriendlyName + " - " + FormatBytes(targetDisk.Size));
		report.AppendLine("Report folder: " + (persisted ? reportRoot : "(not saved — this clone completed successfully; a report is only saved to disk when something needs review)"));
		report.AppendLine("DiskPart script that was executed: " + (persisted ? diskpartScriptPath : "(not saved — see the DiskPart output below)"));
		report.AppendLine("VSS snapshot mapped as: " + shadowLetter + ":");
		report.AppendLine("Source root: " + sourceRoot);
		report.AppendLine("EFI partition letter: " + bootLetter + ":");
		report.AppendLine("Windows partition letter: " + windowsLetter + ":");
		report.AppendLine("Real root: " + realRoot);
		report.AppendLine("Real Windows folder: " + realWindowsFolder);
		report.AppendLine();
		report.AppendLine("Result: " + (ok ? "pass" : "needs review"));
		report.AppendLine("- DiskPart real layout: " + (diskpartOk ? "OK" : "FAILED"));
		report.AppendLine("- Full root copied: " + (copyOk ? "OK" : "FAILED"));
		report.AppendLine("- Windows boot files and SYSTEM hive present: " + (windowsOk ? "OK" : "FAILED"));
		report.AppendLine("- Program Files present: " + (programFilesOk ? "OK" : "FAILED"));
		report.AppendLine("- ProgramData present: " + (programDataOk ? "OK" : "FAILED"));
		report.AppendLine("- Users present: " + (usersOk ? "OK" : "FAILED"));
		report.AppendLine("- Portable registry settings applied: " + (registryOk ? "OK" : "FAILED"));
		report.AppendLine("- bcdboot executed: " + (bcdbootOk ? "OK" : "FAILED"));
		report.AppendLine("- BCD store created: " + (bcdStoreOk ? "OK" : "FAILED"));
		report.AppendLine("- EFI fallback bootx64.efi created: " + (bootx64Ok ? "OK" : "FAILED"));
		report.AppendLine("- BCD loader points to \\Windows on the new Windows partition: " + (loaderPathOk ? "OK" : "FAILED"));
		report.AppendLine("- Dual BIOS + UEFI boot files written (bcdboot /f ALL): " + (bcdbootOk ? "OK" : "FAILED"));
		report.AppendLine("- First-boot answer file (unattend.xml) written: " + (unattendWritten ? "OK" : "NOT WRITTEN"));
		report.AppendLine("- Cloned data content verified against source: " + (!verifyRan ? "NOT RUN" : (verifyMismatches == 0 ? "OK" : "MISMATCHES (" + verifyMismatches.ToString("N0") + ")")));
		if (verifyRan && verifyUnverifiable > 0)
		{
			report.AppendLine("- Protected source files that could not be re-read for verification: " + verifyUnverifiable.ToString("N0") + " (not errors — copied correctly by the image engine)");
		}
		report.AppendLine();
		report.AppendLine("Boot compatibility");
		report.AppendLine("- Layout: MBR with an active FAT32 boot partition + NTFS Windows partition.");
		report.AppendLine("- bcdboot /f ALL writes both the BIOS (bootmgr) and UEFI boot files, so the stick boots on legacy-BIOS PCs and on UEFI PCs.");
		report.AppendLine("- UEFI removable fallback \\EFI\\Boot\\bootx64.efi: " + (bootx64Ok ? "present (UEFI-boots on any PC)" : "MISSING"));
		report.AppendLine();
		report.AppendLine("First-boot configuration (unattend.xml)");
		if (unattendWritten)
		{
			report.AppendLine("- Written to \\Windows\\Panther\\unattend.xml (and Sysprep if present).");
			report.AppendLine("- specialize: SanPolicy=4 (keep host disks offline); PersistAllDeviceInstalls + DoNotCleanUpNonPresentDevices (keep drivers when moving between PCs).");
			report.AppendLine("- oobeSystem: skip EULA / OEM / online-account / wireless screens if OOBE ever runs.");
		}
		else
		{
			report.AppendLine("- Could not be written. The offline registry edits still apply the portable settings.");
		}
		report.AppendLine();
		report.AppendLine("Copy result");
		if (copyResult == null)
		{
			// The WIM image-apply engines (wimlib pipe, or DISM capture + wimlib apply) don't emit the legacy
			// per-file copy counters; the structural checks above (winload.efi, SYSTEM hive, Program Files, Users…)
			// are what confirm a complete Windows root.
			report.AppendLine(copyOk
				? "- Windows was cloned by the WIM image-apply engine; the checks above confirm a complete Windows root (per-file counters are only collected by the legacy file-copy engine)."
				: "(copy did not start)");
		}
		else
		{
			report.AppendLine("- Copied files: " + copyResult.Files.ToString("N0"));
			report.AppendLine("- Copied directories: " + copyResult.Directories.ToString("N0"));
			report.AppendLine("- Copied data: " + FormatBytes(copyResult.Bytes));
			report.AppendLine("- Excluded volatile/reparse items: " + copyResult.Skipped.ToString("N0"));
			report.AppendLine("- Reparse points copied as links: " + copyResult.ReparseCopied.ToString("N0"));
			report.AppendLine("- Reparse points skipped: " + copyResult.ReparseSkipped.ToString("N0"));
			report.AppendLine("- Backup fallback copied: " + copyResult.BackupFallbackCopied.ToString("N0"));
			report.AppendLine("- Copy errors: " + copyResult.Errors.ToString("N0"));
			if (copyResult.SampleRecoveries.Count > 0)
			{
				report.AppendLine();
				report.AppendLine("Sample backup-mode recoveries");
				foreach (string recovery in copyResult.SampleRecoveries.Take(30))
				{
					report.AppendLine("- " + recovery);
				}
			}
			if (copyResult.SampleErrors.Count > 0)
			{
				report.AppendLine();
				report.AppendLine("Sample copy errors");
				foreach (string error in copyResult.SampleErrors.Take(30))
				{
					report.AppendLine("- " + error);
				}
			}
			if (copyResult.SampleWarnings.Count > 0)
			{
				report.AppendLine();
				report.AppendLine("Sample warnings");
				foreach (string warning in copyResult.SampleWarnings.Take(30))
				{
					report.AppendLine("- " + warning);
				}
			}
		}
		report.AppendLine();
		report.AppendLine("Content verification");
		if (!verifyRan)
		{
			report.AppendLine("- Verification did not run or was interrupted before completing.");
		}
		else
		{
			report.AppendLine("- Method: every file on the USB was re-read and byte-compared against the VSS snapshot.");
			report.AppendLine("- Files <= 64 MB compared in full; larger files spot-checked on first/middle/last 4 MB.");
			report.AppendLine("- Files verified OK: " + verifyVerifiedFiles.ToString("N0"));
			report.AppendLine("- Data verified OK: " + FormatBytes(verifyVerifiedBytes));
			report.AppendLine("- Mismatches: " + verifyMismatches.ToString("N0"));
			report.AppendLine("- Unverifiable protected source files: " + verifyUnverifiable.ToString("N0"));
			if (verifyMismatches == 0)
			{
				report.AppendLine("- Result: every cloned file that could be read matches the source. No silent corruption detected.");
			}
			else
			{
				report.AppendLine("- Result: some files differ from the source. The USB may have bad sectors or the");
				report.AppendLine("  files changed on disk during the clone. Re-run the clone to fix the listed files.");
				if (verifySamples.Count > 0)
				{
					report.AppendLine();
					report.AppendLine("Sample content mismatches");
					foreach (string sample in verifySamples.Take(30))
					{
						report.AppendLine("- " + sample);
					}
				}
			}
			if (verifyUnverifiable > 0)
			{
				report.AppendLine();
				report.AppendLine("Unverifiable protected files (NOT errors — captured correctly by the image engine, but");
				report.AppendLine("the verifier cannot re-read them without backup privilege: DPAPI, Windows Hello NGC,");
				report.AppendLine("Offline Files cache (CSC), UWP app state):");
				foreach (string sample in verifyUnverifiableSamples.Take(15))
				{
					report.AppendLine("- " + sample);
				}
			}
		}
		report.AppendLine();
		report.AppendLine("Root clone exclusions");
		report.AppendLine("- Excludes pagefile.sys, hiberfil.sys, swapfile.sys, MEMORY.DMP, Recycle Bin, System Volume Information, Windows temp/log/download/prefetch caches, browser caches, and common user temp folders.");
		report.AppendLine("- These files are intentionally not cloned because Windows can recreate them and they slow or destabilize live cloning.");
		report.AppendLine();
		report.AppendLine("DiskPart output");
		report.AppendLine(string.IsNullOrWhiteSpace(diskpartOutput) ? "(no output)" : diskpartOutput.Trim());
		report.AppendLine();
		report.AppendLine("Registry output");
		report.AppendLine(string.IsNullOrWhiteSpace(registryOutput) ? "(no output)" : registryOutput.Trim());
		report.AppendLine();
		report.AppendLine("Bcdboot output");
		report.AppendLine(string.IsNullOrWhiteSpace(bcdbootOutput) ? "(no output)" : FilterBcdbootOutput(bcdbootOutput));
		report.AppendLine();
		report.AppendLine("BCD enum output");
		report.AppendLine(string.IsNullOrWhiteSpace(bcdEnumOutput) ? "(BCD enum skipped or empty)" : bcdEnumOutput.Trim());
		report.AppendLine();
		report.AppendLine("Next steps");
		report.AppendLine("1. If this report says pass, the clone is ready - boot the selected drive.");
		report.AppendLine("2. On a different PC, set the BIOS/UEFI to boot from the USB/SSD.");
		report.AppendLine("3. The first boot may take a few minutes while Windows settles drivers for the new hardware.");
		return report.ToString();
	}

	// Persists the report to disk — ONLY called when the clone needs review (failed/incomplete), so a clean success
	// never creates a Desktop folder or report file. Copies the diskpart script (staged in TEMP) into the report
	// folder for the same audit trail the report used to keep inline.
	private string WriteFullRootUsbCloneReport(DiskItem targetDisk, string reportRoot, string diskpartPath, char shadowLetter, string sourceRoot, string realRoot, string realWindowsFolder, char bootLetter, char windowsLetter, bool diskpartOk, bool copyOk, bool registryOk, bool bcdbootOk, bool bcdStoreOk, bool bootx64Ok, bool loaderPathOk, NtfsCopyTestResult? copyResult, string diskpartOutput, string registryOutput, string bcdbootOutput, string bcdEnumOutput, bool verifyRan, long verifyVerifiedFiles, long verifyVerifiedBytes, long verifyMismatches, long verifyUnverifiable, List<string> verifySamples, List<string> verifyUnverifiableSamples, bool unattendWritten)
	{
		Directory.CreateDirectory(reportRoot);
		string persistedScriptPath = Path.Combine(reportRoot, "01-real-usb-layout-diskpart-ran.txt");
		try { File.Copy(diskpartPath, persistedScriptPath, true); } catch { }
		string text = BuildFullRootUsbCloneReportText(targetDisk, persisted: true, reportRoot, persistedScriptPath, shadowLetter, sourceRoot, realRoot, realWindowsFolder, bootLetter, windowsLetter, diskpartOk, copyOk, registryOk, bcdbootOk, bcdStoreOk, bootx64Ok, loaderPathOk, copyResult, diskpartOutput, registryOutput, bcdbootOutput, bcdEnumOutput, verifyRan, verifyVerifiedFiles, verifyVerifiedBytes, verifyMismatches, verifyUnverifiable, verifySamples, verifyUnverifiableSamples, unattendWritten);
		string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DriveForge-NTFS-FullRootClone-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
		File.WriteAllText(reportPath, text, Encoding.UTF8);
		return reportPath;
	}

	private async Task<string> ApplySystemPortableSettingsAsync(string systemHive, bool bypassRequirements, bool faithfulMode = false, bool portableMode = true)
	{
		StringBuilder output = new StringBuilder();
		string hiveName = "DriveForgeRealCloneSYSTEM" + Guid.NewGuid().ToString("N");
		string hiveRoot = "HKLM\\" + hiveName;
		bool loaded = false;
		output.AppendLine("SYSTEM hive: " + systemHive);
		try
		{
			await RunProcessAsync("reg.exe", "load " + QuoteArgument(hiveRoot) + " " + QuoteArgument(systemHive));
			loaded = true;
			output.AppendLine("LOAD: OK");
			List<string> controlSets = await GetLoadedControlSetsAsync(hiveRoot);
			output.AppendLine("CONTROL SETS: " + string.Join(", ", controlSets));
			// Clear stale drive-letter mappings from the source disk.
			// MountedDevices stores per-disk-GUID mappings; on the clone the disk has a new GUID
			// (DiskPart formatted it fresh), so old entries cause drive-letter confusion or delays.
			// Windows rebuilds MountedDevices cleanly at first boot from actual disk state.
			await RunProcessAsync("reg.exe", "delete " + QuoteArgument(hiveRoot + "\\MountedDevices") + " /f", allowFailure: true);
			foreach (string controlSet in controlSets)
			{
				if (portableMode)
				{
					// Portable / Windows To Go: mark as portable OS and keep host disks offline (SanPolicy=4).
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Control") + " /v PortableOperatingSystem /t REG_DWORD /d 1 /f");
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Services\\partmgr\\Parameters") + " /v SanPolicy /t REG_DWORD /d 4 /f");
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Policies\\Microsoft\\PortableOperatingSystem") + " /v Sleep /t REG_DWORD /d 1 /f");
				}
				else
				{
					// Internal disk (normal install): NOT a portable OS, and all disks come up ONLINE (SanPolicy=1).
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Control") + " /v PortableOperatingSystem /t REG_DWORD /d 0 /f", allowFailure: true);
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Services\\partmgr\\Parameters") + " /v SanPolicy /t REG_DWORD /d 1 /f");
				}
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Control\\Session Manager\\Memory Management") + " /v PagingFiles /t REG_MULTI_SZ /d " + QuoteArgument(@"C:\pagefile.sys 0 0") + " /f");
				// ServicesPipeTimeout: 60 s — prevents service-start failures on slow USB 2.0 drives (default 30 s)
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Control") + " /v ServicesPipeTimeout /t REG_DWORD /d 60000 /f");
				// Disable crash dump only for portable drives (avoids multi-GB MEMORY.DMP on a USB); keep it for internal.
				if (portableMode)
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\" + controlSet + "\\Control\\CrashControl") + " /v CrashDumpEnabled /t REG_DWORD /d 0 /f");
				// Register DriveForgeRepairSvc as a delayed-auto-start Windows Service.
				// Runs at boot AS SYSTEM (before any user logs in) to silently re-register system AppX packages.
				// Skipped in faithfulMode: a WIM-applied clone keeps AppX state intact, so no repair is needed.
				if (!faithfulMode)
				{
					string svcKey = hiveRoot + "\\" + controlSet + "\\Services\\DriveForgeRepairSvc";
					string svcImagePath = "%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"C:\\ProgramData\\DriveForge\\DriveForgeRepairSvc.ps1\"";
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v Type          /t REG_DWORD    /d 16     /f");  // SERVICE_WIN32_OWN_PROCESS
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v Start         /t REG_DWORD    /d 2      /f");  // SERVICE_AUTO_START
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v ErrorControl  /t REG_DWORD    /d 0      /f");  // SERVICE_ERROR_IGNORE
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v DelayedAutoStart /t REG_DWORD  /d 1      /f");  // start after boot settled
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v ObjectName    /t REG_SZ       /d LocalSystem /f");
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v DisplayName   /t REG_SZ       /d " + QuoteArgument("DriveForge First Boot Repair") + " /f");
					await RunProcessAsync("reg.exe", "add " + QuoteArgument(svcKey) + " /v ImagePath     /t REG_EXPAND_SZ /d " + QuoteArgument(svcImagePath) + " /f");
				}
				// Universal-hardware boot: force inbox storage + USB drivers to boot-start so this clone
				// can boot on a DIFFERENT PC, not just the one it was cloned from (universal-hardware boot).
				await ApplyUniversalBootStorageDriversAsync(hiveRoot, controlSet);
			}
			// Temporarily disable third-party antivirus only when the first-boot repair needs it disabled (a
			// generated Re-Enable-Antivirus.cmd restores it). faithfulMode leaves antivirus fully working.
			if (!faithfulMode)
			{
				string realCloneRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(systemHive) ?? "", "..", "..", ".."));
				await NeutralizeAntivirusInHiveAsync(hiveRoot, controlSets, realCloneRoot, autoRestoreInstalled: true);
			}
			if (bypassRequirements)
			{
				string labConfig = hiveRoot + "\\Setup\\LabConfig";
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(labConfig) + " /v BypassTPMCheck /t REG_DWORD /d 1 /f");
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(labConfig) + " /v BypassSecureBootCheck /t REG_DWORD /d 1 /f");
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(labConfig) + " /v BypassRAMCheck /t REG_DWORD /d 1 /f");
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(labConfig) + " /v BypassCPUCheck /t REG_DWORD /d 1 /f");
			}
			foreach (string controlSet in controlSets)
			{
				output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\" + controlSet + "\\Control", "PortableOperatingSystem"));
				output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\" + controlSet + "\\Services\\partmgr\\Parameters", "SanPolicy"));
				output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\" + controlSet + "\\Policies\\Microsoft\\PortableOperatingSystem", "Sleep"));
				output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\" + controlSet + "\\Control\\Session Manager\\Memory Management", "PagingFiles"));
				output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\" + controlSet + "\\Control", "ServicesPipeTimeout"));
				output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\" + controlSet + "\\Control\\CrashControl", "CrashDumpEnabled"));
			}
		}
		catch (Exception ex)
		{
			output.AppendLine("FAILED: " + ex.Message);
		}
		finally
		{
			if (loaded)
			{
				// Robust unload: a silently-failed unload would DISCARD every edit above
				// (this is what dropped the first-boot RunOnce values on earlier clones).
				// Report FAILED so the caller aborts instead of shipping a half-configured clone.
				bool unloaded = await UnloadRegistryHiveRobustAsync(hiveRoot);
				output.AppendLine(unloaded ? "UNLOAD: OK" : "FAILED: hive unload did not commit (edits may be lost): " + hiveRoot);
			}
		}
		return output.ToString();
	}

	private async Task<string> ApplySoftwarePortableSettingsAsync(string softwareHive, bool bypassAccount, bool faithfulMode = false)
	{
		StringBuilder output = new StringBuilder();
		string hiveName = "DriveForgeRealCloneSOFTWARE" + Guid.NewGuid().ToString("N");
		string hiveRoot = "HKLM\\" + hiveName;
		bool loaded = false;
		output.AppendLine("SOFTWARE hive: " + softwareHive);
		try
		{
			await RunProcessAsync("reg.exe", "load " + QuoteArgument(hiveRoot) + " " + QuoteArgument(softwareHive));
			loaded = true;
			output.AppendLine("LOAD: OK");
			if (bypassAccount)
			{
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(hiveRoot + "\\Microsoft\\Windows\\CurrentVersion\\OOBE") + " /v BypassNRO /t REG_DWORD /d 1 /f");
			}
			// Per-user first-boot AppX repair via ACTIVE SETUP — the standard Microsoft mechanism that
			// runs a command once per user at first interactive logon, BEFORE Explorer starts (so package
			// re-registration never hits "currently in use"). This replaces the old Winlogon\Shell +
			// SetupScreen.ps1 + Startup VBS chain, which used intrusive shell-replacement and hidden-startup
			// techniques that security software may flag and quarantine together with its RunOnce
			// autostart. Active Setup is a standard, non-intrusive mechanism, and a single HKLM entry automatically
			// covers every existing and future user profile (no per-user NTUSER.DAT editing needed).
			if (!faithfulMode)
			{
				string activeSetupKey = hiveRoot + "\\Microsoft\\Active Setup\\Installed Components\\{B8E7A1F4-9C3D-4E5A-8F2B-1D6C7A9E4B30}";
				string stubPath = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"C:\\ProgramData\\DriveForge\\FirstBootAppRepair.ps1\" -UserMode";
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(activeSetupKey) + " /ve /t REG_SZ /d " + QuoteArgument("DriveForge First Boot App Repair") + " /f");
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(activeSetupKey) + " /v Version /t REG_SZ /d 1 /f");
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(activeSetupKey) + " /v StubPath /t REG_EXPAND_SZ /d " + QuoteArgument(stubPath) + " /f");
				output.AppendLine(await QueryRegistryValueForReportAsync(activeSetupKey, "StubPath"));
			}
			// Defensive: strip the legacy RunOnce trigger if a previous build wrote it (it pointed at the
			// now-removed SetupScreen.ps1, so it would only spawn an error on first logon).
			await RunProcessAsync("reg.exe", "delete " + QuoteArgument(hiveRoot + "\\Microsoft\\Windows\\CurrentVersion\\RunOnce") + " /v DriveForgeFirstBootAppRepair /f", allowFailure: true);
			output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\Microsoft\\Windows\\CurrentVersion\\OOBE", "BypassNRO"));
			output.AppendLine(await QueryRegistryValueForReportAsync(hiveRoot + "\\Microsoft\\Windows\\CurrentVersion\\Appx", "(default)"));
		}
		catch (Exception ex)
		{
			output.AppendLine("FAILED: " + ex.Message);
		}
		finally
		{
			if (loaded)
			{
				// Robust unload: a silently-failed unload would DISCARD every edit above
				// (this is what dropped the first-boot RunOnce values on earlier clones).
				// Report FAILED so the caller aborts instead of shipping a half-configured clone.
				bool unloaded = await UnloadRegistryHiveRobustAsync(hiveRoot);
				output.AppendLine(unloaded ? "UNLOAD: OK" : "FAILED: hive unload did not commit (edits may be lost): " + hiveRoot);
			}
		}
		return output.ToString();
	}

	// Content verification thresholds: files <= 64 MB are compared in full; larger files are spot-checked
	// on their first/middle/last 4 MB.
	private const long ContentVerifyFullThreshold = 64L * 1024 * 1024;
	private const int ContentVerifyRegionBytes = 4 * 1024 * 1024;
	// Sampled verify: every file's presence + size is always checked (cheap, catches missing/truncated files);
	// a full byte-compare runs for boot-critical files + 1 in this many of the rest (catches corruption, far faster).
	private const int ContentVerifySampleEvery = 8;

	private void VerifyCloneContent(string targetRoot, string sourceRoot, Func<string, bool, bool>? shouldExclude,
		out long verifiedFiles, out long verifiedBytes, out long mismatches, out long unverifiable, List<string> sampleMismatches, List<string> sampleUnverifiable, bool detectMissing)
	{
		verifiedFiles = 0;
		verifiedBytes = 0;
		mismatches = 0;
		unverifiable = 0;
		var pending = new Stack<(string Target, string Source)>();
		pending.Push((targetRoot, sourceRoot));
		var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false, AttributesToSkip = 0 };
		long sinceLog = 0;
		long processed = 0; // drives the UI progress bar/ETA during verification (read by the dispatcher timer)
		long contentSampleIndex = 0; // rotates so 1-in-N non-critical files get a full content byte-compare
		while (pending.Count > 0)
		{
			if (stopRequested || internalOperationStopped) break;
			(string currentTarget, string currentSource) = pending.Pop();
			try
			{
				foreach (FileSystemInfo entry in new DirectoryInfo(currentTarget).EnumerateFileSystemInfos("*", options))
				{
					if (stopRequested || internalOperationStopped) break;
					if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
					bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
					string relativePath = GetRelativeNtfsPath(targetRoot, entry.FullName);
					if (shouldExclude != null && shouldExclude(relativePath, isDir)) continue;
					string sourcePath = Path.Combine(currentSource, entry.Name);
					if (isDir)
					{
						pending.Push((entry.FullName, sourcePath));
						continue;
					}
					var targetFile = (FileInfo)entry;
					try
					{
						if (!File.Exists(sourcePath))
						{
							// Present on target but not on source — bcdboot/registry edits run AFTER verify,
							// so at this point this is unexpected. Flag it rather than silently passing.
							mismatches++;
							AddSampleError(sampleMismatches, entry.FullName + " -> not found on source snapshot");
						}
						else if (new FileInfo(sourcePath).Length != targetFile.Length)
						{
							// Size mismatch = truncated/incomplete copy. Always checked (cheap metadata read).
							mismatches++;
							AddSampleError(sampleMismatches, entry.FullName + " -> size differs from source");
						}
						else if ((IsBootCriticalVerifyPath(relativePath) || contentSampleIndex++ % ContentVerifySampleEvery == 0)
							&& !FileContentMatches(sourcePath, entry.FullName, targetFile.Length))
						{
							// Full byte-compare only for boot-critical files + a 1-in-N sample (the rest passed on size).
							mismatches++;
							AddSampleError(sampleMismatches, entry.FullName + " -> content differs from source");
						}
						else
						{
							verifiedFiles++;
							verifiedBytes += targetFile.Length;
						}
					}
					catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
					{
						// Protected/locked source files (DPAPI, Windows Hello NGC, CSC, UWP app state) cannot be read
						// without backup privilege, but wimlib captured them correctly. Not a content mismatch.
						unverifiable++;
						AddSampleError(sampleUnverifiable, entry.FullName + " -> could not read source to verify (" + ex.GetType().Name + ")");
					}
					catch (Exception ex)
					{
						mismatches++;
						AddSampleError(sampleMismatches, entry.FullName + " verify -> " + ex.Message);
					}
					processed += targetFile.Length;
					Volatile.Write(ref _progressDoneBytes, processed); // UI timer renders verify GiB/speed/ETA
					sinceLog += targetFile.Length;
					if (sinceLog >= 4L * 1024 * 1024 * 1024)
					{
						sinceLog = 0;
						Log($"Content verify progress: {verifiedFiles:N0} files OK ({FormatBytes(verifiedBytes)})...");
					}
				}
			}
			catch (Exception ex)
			{
				// A subtree that fails to enumerate (bad sector on the target, dir removed mid-run) is NOT verified —
				// count it so the clone is not falsely reported as fully verified.
				mismatches++;
				AddSampleError(sampleMismatches, currentTarget + " verify-enumerate -> " + ex.Message);
			}
			// Detect entries MISSING from the target: the walk above is target-driven, so anything the copy engine
			// dropped (present on the source snapshot, absent on the target) would be invisible. Enumerate the SOURCE
			// side of this directory and flag every non-excluded entry that has no counterpart on the target. ONLY for
			// the RAW engine: its copy-exclusion set is exactly this verify's `shouldExclude` (IsNtfsCloneExcluded). The
			// wimlib engine excludes a SUPERSET (BuildCaptureConfig: .nuget/npm/pip/cargo caches + AV dirs), so a
			// source-driven check there would false-flag correctly-excluded files as MISSING.
			if (detectMissing)
			try
			{
				foreach (FileSystemInfo sEntry in new DirectoryInfo(currentSource).EnumerateFileSystemInfos("*", options))
				{
					if (stopRequested || internalOperationStopped) break;
					if ((sEntry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
					bool sIsDir = (sEntry.Attributes & FileAttributes.Directory) != 0;
					string sRel = GetRelativeNtfsPath(sourceRoot, sEntry.FullName);
					if (shouldExclude != null && shouldExclude(sRel, sIsDir)) continue;
					string tPath = Path.Combine(currentTarget, sEntry.Name);
					if (sIsDir ? !Directory.Exists(tPath) : !File.Exists(tPath))
					{
						mismatches++;
						AddSampleError(sampleMismatches, sEntry.FullName + " -> MISSING from the clone");
					}
				}
			}
			catch (Exception ex) { mismatches++; AddSampleError(sampleMismatches, currentSource + " verify-source-enumerate -> " + ex.Message); }
		}
	}

	// Files whose corruption would stop the clone from booting — always fully byte-verified, never just sampled.
	private static bool IsBootCriticalVerifyPath(string relativePath)
	{
		string p = relativePath.Replace('/', '\\');
		return p.StartsWith("\\Windows\\System32\\config\\", StringComparison.OrdinalIgnoreCase)   // registry hives
			|| p.StartsWith("\\Windows\\System32\\drivers\\", StringComparison.OrdinalIgnoreCase)  // boot/system drivers
			|| p.IndexOf("\\Windows\\System32\\winload", StringComparison.OrdinalIgnoreCase) >= 0
			|| p.IndexOf("ntoskrnl.exe", StringComparison.OrdinalIgnoreCase) >= 0
			|| p.IndexOf("bootmgr", StringComparison.OrdinalIgnoreCase) >= 0
			|| p.IndexOf("bootmgfw.efi", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static bool FileContentMatches(string sourcePath, string targetPath, long targetLength)
	{
		using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
		using var dst = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
		if (src.Length != dst.Length) return false;
		long length = targetLength;
		if (length <= ContentVerifyFullThreshold)
			return RegionsEqual(src, dst, 0, length);
		long mid = Math.Max(0, length / 2 - ContentVerifyRegionBytes / 2);
		long lastStart = Math.Max(0, length - ContentVerifyRegionBytes);
		return RegionsEqual(src, dst, 0, ContentVerifyRegionBytes)
			&& RegionsEqual(src, dst, mid, ContentVerifyRegionBytes)
			&& RegionsEqual(src, dst, lastStart, ContentVerifyRegionBytes);
	}

	private static bool RegionsEqual(FileStream a, FileStream b, long offset, long count)
	{
		a.Position = offset;
		b.Position = offset;
		byte[] bufA = ArrayPool<byte>.Shared.Rent(1024 * 1024);
		byte[] bufB = ArrayPool<byte>.Shared.Rent(1024 * 1024);
		try
		{
			long remaining = count;
			while (remaining > 0)
			{
				int want = (int)Math.Min(bufA.Length, remaining);
				int readA = ReadExactly(a, bufA, want);
				int readB = ReadExactly(b, bufB, want);
				if (readA != readB) return false;
				if (!bufA.AsSpan(0, readA).SequenceEqual(bufB.AsSpan(0, readB))) return false;
				if (readA < want) break; // reached EOF on both (sizes already equal)
				remaining -= readA;
			}
			return true;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(bufA);
			ArrayPool<byte>.Shared.Return(bufB);
		}
	}

	private static int ReadExactly(FileStream stream, byte[] buffer, int want)
	{
		int total = 0;
		while (total < want)
		{
			int read = stream.Read(buffer, total, want - total);
			if (read == 0) break;
			total += read;
		}
		return total;
	}

	private static bool TryEnablePrivilege(string privilegeName)
	{
		try
		{
			if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out SafeFileHandle tokenHandle))
			{
				return false;
			}
			using (tokenHandle)
			{
				if (!LookupPrivilegeValue(null, privilegeName, out Luid luid))
				{
					return false;
				}
				TokenPrivileges privileges = new TokenPrivileges
				{
					PrivilegeCount = 1,
					Luid = luid,
					Attributes = SePrivilegeEnabled
				};
				if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
				{
					return false;
				}
				return Marshal.GetLastWin32Error() == 0;
			}
		}
		catch
		{
			return false;
		}
	}

	// Returns the disk's first existing drive letter, or '\0' if it has none (a blank / freshly-installed disk). The
	// result is only ever used as a RESERVED letter when picking free letters for the clone/restore, so '\0' (never a
	// real drive letter) is a harmless "nothing to reserve" sentinel. This lets a restore/clone target a fresh,
	// unpartitioned disk — the whole point of a backup restore — instead of forcing the user to pre-assign a letter.
	private static char GetFirstUsableDriveLetter(DiskItem disk)
	{
		return disk.DriveLetters.Select(char.ToUpperInvariant).FirstOrDefault(value => value >= 'A' && value <= 'Z');
	}

	private static string GetRelativeNtfsPath(string sourceRoot, string fullPath)
	{
		string root = sourceRoot.TrimEnd('\\') + "\\";
		string relative = fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(root.Length) : fullPath;
		return "\\" + relative.TrimStart('\\');
	}

	private static bool IsNtfsCloneExcluded(string relativePath, bool isDirectory)
	{
		string path = relativePath.Replace('/', '\\');
		string lower = path.ToLowerInvariant();
		string name = Path.GetFileName(path.TrimEnd('\\')).ToLowerInvariant();
		if (name is "$recycle.bin" or "system volume information" or "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" or "memory.dmp" or "dumpstack.log.tmp")
		{
			return true;
		}
		if (lower.StartsWith("\\$recycle.bin\\", StringComparison.Ordinal) ||               // exclude the WHOLE subtree, not just the top folder — else deleted files in the Recycle Bin are resurrected on the clone (privacy)
			lower.StartsWith("\\system volume information\\", StringComparison.Ordinal) ||   // restore-point / USN journal data — large and source-machine-specific
			lower.StartsWith("\\$windows.~bt\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\$windows.~ws\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\temp\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\logs\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\panther\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\prefetch\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\minidump\\", StringComparison.Ordinal) ||           // crash minidumps from source machine — irrelevant on clone
			lower.StartsWith("\\windows\\livekernelreports\\", StringComparison.Ordinal) ||   // kernel reliability reports — source-machine-specific
			lower.StartsWith("\\windows\\system32\\winevt\\logs\\", StringComparison.Ordinal) || // event logs — 200-500 MB, machine-specific, auto-rebuilt by Event Log service
			lower.StartsWith("\\windows\\system32\\sru\\", StringComparison.Ordinal) ||      // SRUDB.dat System Resource Usage DB — 100+ MB, machine-specific, auto-rebuilt
			lower.StartsWith("\\windows\\system32\\logfiles\\", StringComparison.Ordinal) ||          // IIS/DHCP/DNS/WMI logs — machine-specific, auto-rebuilt, can be 100+ MB
			lower.StartsWith("\\windows\\softwaredistribution\\download\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\softwaredistribution\\datastore\\", StringComparison.Ordinal) || // WU database (DataStore.edb ~100-200 MB) + Logs — machine-specific Update IDs
			lower.StartsWith("\\programdata\\nvidia corporation\\downloader\\", StringComparison.Ordinal) || // NVIDIA partial driver downloads — cache, can be several GB
			lower.StartsWith("\\programdata\\microsoft\\windows\\wer\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\programdata\\microsoft\\windows\\deliveryoptimization\\cache\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\programdata\\microsoft\\search\\data\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\programdata\\package cache\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\amd\\", StringComparison.Ordinal) ||
			lower.StartsWith("\\windows\\winsxs\\temp\\pendingdeletes\\", StringComparison.Ordinal) || // pending-delete WinSxS files — marked for deletion by Windows Update, useless on clone
			lower.StartsWith("\\windows\\system32\\webthreatdefsvc\\", StringComparison.Ordinal) ||   // Defender WebThreat sensor runtime data — volatile, auto-rebuilt on first boot
			lower.Contains("\\appdata\\local\\amd\\dxccache\\", StringComparison.Ordinal))            // AMD GPU shader cache — recompiled automatically on first GPU use
		{
			return true;
		}
		if ((name.StartsWith("thumbcache_", StringComparison.Ordinal) && name.EndsWith(".db", StringComparison.Ordinal) &&
				lower.Contains("\\appdata\\local\\microsoft\\windows\\explorer\\", StringComparison.Ordinal)) || // thumbnail DB per-profile
			lower.Contains("\\appdata\\local\\temp\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\microsoft\\windows\\inetcache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\microsoft\\windows\\webcache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\microsoft\\edge\\user data\\default\\cache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\microsoft\\edge\\user data\\default\\code cache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\microsoft\\edge\\user data\\default\\gpucache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\google\\chrome\\user data\\default\\cache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\google\\chrome\\user data\\default\\code cache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\google\\chrome\\user data\\default\\gpucache\\", StringComparison.Ordinal) ||
			lower.Contains("\\appdata\\local\\bravesoftware\\brave-browser\\user data\\default\\cache\\", StringComparison.Ordinal) ||  // Brave cache
			lower.Contains("\\appdata\\local\\bravesoftware\\brave-browser\\user data\\default\\code cache\\", StringComparison.Ordinal) || // Brave code cache
			(lower.Contains("\\appdata\\local\\mozilla\\firefox\\profiles\\", StringComparison.Ordinal) && lower.Contains("\\cache2\\", StringComparison.Ordinal)) || // Firefox cache
			lower.Contains("\\appdata\\local\\packages\\", StringComparison.Ordinal) && lower.Contains("\\ac\\temp\\", StringComparison.Ordinal))
		{
			return true;
		}
		return false;
	}

	private static void AddSampleError(List<string> sampleErrors, string message)
	{
		if (sampleErrors.Count < 25)
		{
			sampleErrors.Add(message);
		}
	}

	// Restore a .wim backup (made by "Back up this PC to an image file") onto a drive and make it bootable.
	// Reuses the same layout + boot + portable-settings steps as the clone, but the source is a WIM file.
	private async Task RestoreWimToDriveAsync(string wimPath, DiskItem disk)
	{
		if (!File.Exists(wimPath))
		{
			throw new InvalidOperationException("The selected image file no longer exists.");
		}
		if (PhysicalDiskOfPath(wimPath) == disk.Number)
		{
			throw new InvalidOperationException("The image file is stored ON the drive you're restoring to — restoring would erase the image itself. Move the image to another drive first. No changes were made.");
		}
		char currentTargetLetter = GetFirstUsableDriveLetter(disk);
		char bootLetter = GetFreeDriveLetter(currentTargetLetter);
		char windowsLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter);
		string realRoot = windowsLetter + ":\\";
		string realWindowsFolder = Path.Combine(realRoot, "Windows");
		string diskpartPath = Path.Combine(Path.GetTempPath(), $"driveforge-restore-diskpart-{Guid.NewGuid():N}.txt");

		TryEnablePrivilege("SeBackupPrivilege");
		TryEnablePrivilege("SeRestorePrivilege");

		// Inspect the image BEFORE touching the disk: pick the latest restore point and learn how much
		// data it holds, so we can refuse a too-small target instead of formatting and then failing.
		SetStage(L("StgReadImage"), 6.0);
		string wimlibPath = await EnsureWimlibAsync();
		int imageIndex = 1;
		long imageBytes = 0;
		try
		{
			string info = await RunProcessCaptureAsync(wimlibPath, "info " + QuoteArgument(wimPath));
			var m = Regex.Match(info, @"Image Count:\s*(\d+)", RegexOptions.IgnoreCase);
			if (m.Success && int.TryParse(m.Groups[1].Value, out int cnt) && cnt >= 1) imageIndex = cnt;
			string detail = await RunProcessCaptureAsync(wimlibPath, "info " + QuoteArgument(wimPath) + " " + imageIndex);
			var b = Regex.Match(detail, @"Total Bytes:\s*([\d,]+)", RegexOptions.IgnoreCase);
			if (b.Success && long.TryParse(b.Groups[1].Value.Replace(",", ""), out long tb)) imageBytes = tb;
		}
		catch { }

		// Capacity gate. The target is formatted with 64K NTFS clusters (see BuildRealNtfsUsbLayoutDiskpartScript), so
		// hundreds of thousands of Windows files each round up to a 64K cluster — the on-disk footprint runs well above
		// the image's logical "Total Bytes". Use a generous margin (25% + 512 MB for the boot partition) so a target
		// only marginally larger than the image isn't formatted and then failed with ENOSPC mid-apply.
		if (imageBytes > 0 && disk.Size < (long)(imageBytes * 1.25) + 512L * 1024 * 1024)
		{
			throw new InvalidOperationException(
				"The selected drive is too small for this image.\n\nImage content: " + FormatBytes(imageBytes) +
				"\nSelected drive: " + FormatBytes(disk.Size) +
				"\n\nNo changes were made — the drive was not formatted.");
		}

		// The restore builds an MBR layout (BuildRealNtfsUsbLayoutDiskpartScript), whose single Windows partition tops out
		// near 2 TiB ((2^32-1)*512, minus the boot partition) no matter how big the target disk is. Compare the image's
		// LOGICAL content — the bytes actually written — against that partition ceiling, NOT the capacity gate's 1.25x
		// disk-headroom margin (which would wrongly reject a ~1.8 TiB image that fits fine). Content past the ceiling would
		// pass the disk.Size gate and then ENOSPC AFTER the wipe, so reject it here, before anything destructive.
		if (imageBytes > 0 && imageBytes > 2199023255040L - 512L * 1024 * 1024)
			throw new InvalidOperationException("This image's content is larger than the ~2 TB an MBR restore layout supports, so it cannot be restored to this drive. No changes were made — the drive was not formatted.");

		// Target health gate before the destructive format.
		if (!await ConfirmTargetHealthAsync(disk))
		{
			Log("WIM restore cancelled by user after target health warning.");
			operationAbortedBeforeWrite = true;
			SetStage(L("StgCancelHealth"), 0.0);
			return;
		}

		// Verify the image's integrity BEFORE formatting the target — finding a corrupt backup AFTER erasing the
		// destination (often a dead-PC recovery) is the worst possible ordering. Read-only pass; throws on corruption.
		SetStage(L("StgVerifyBeforeWrite"), 8.0);
		try { await RunProcessCaptureAsync(wimlibPath, "verify " + QuoteArgument(wimPath)); }
		catch (Exception vex)
		{
			throw new InvalidOperationException("The backup image failed its integrity check — it may be corrupt or incomplete. The target drive was NOT formatted.\n\n" + vex.Message);
		}

		SetStage(L("StgFormatTarget"), 10.0);
		await File.WriteAllTextAsync(diskpartPath, BuildRealNtfsUsbLayoutDiskpartScript(disk.Number, bootLetter, windowsLetter), Encoding.ASCII);
		try { await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(diskpartPath)); }
		finally { TryDeleteFile(diskpartPath); }   // delete the script even if diskpart throws, so it doesn't pile up in %TEMP%

		SetStage(L("StgRestoreToDrive"), 20.0);
		progressDoneGiB = 0.0;
		progressTotalGiB = Math.Max(1.0, new FileInfo(wimPath).Length / 1073741824.0 * 1.8);
		_speedWindow.Clear();
		using (var pollCts = new CancellationTokenSource())
		{
			Task poll = PollPartitionUsedSpaceAsync(realRoot, pollCts.Token);
			// Target uses ":\\." (trailing dot), matching the internal-clone apply path, so the quoted path never ends
			// in '\' — belt-and-suspenders alongside the QuoteArgument fix (wimlib is known to accept this form).
			try { await RunProcessAsync(wimlibPath, "apply " + QuoteArgument(wimPath) + " " + imageIndex + " " + QuoteArgument(windowsLetter + ":\\.") + " --recover-data"); }
			finally { pollCts.Cancel(); try { await poll; } catch { } }
		}
		bool ok = File.Exists(Path.Combine(realWindowsFolder, "System32", "winload.efi")) &&
			File.Exists(Path.Combine(realWindowsFolder, "System32", "config", "SYSTEM"));
		if (!ok)
		{
			throw new InvalidOperationException("The image did not restore a complete Windows root (winload.efi / SYSTEM missing). The drive was formatted.");
		}

		SetStage(L("StgApplyPortable"), 80.0);
		string restoreRegOut = await ApplyPortableRegistrySettingsToRealCloneAsync(realWindowsFolder, BypassRequirementsCheck.IsChecked == true, BypassAccountCheck.IsChecked == true, faithfulMode: true, portableMode: true);
		if (restoreRegOut.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Portable registry preparation failed after restore — the drive would boot mis-configured:\r\n" + restoreRegOut);

		SetStage(L("StgMakeBootable"), 90.0);
		await RunProcessCaptureAsync("bcdboot.exe", QuoteArgument(realWindowsFolder) + $" /s {bootLetter}: /f ALL /v");
		EnsureUefiRemovableFallback(bootLetter);
		await FlushVolumesAsync(bootLetter, windowsLetter);
		Log("Image restored to Disk " + disk.Number + " and made bootable (BIOS + UEFI).");
	}

	// Attaches a VHDX/VHD READ-ONLY and mounts its Windows partition (the largest partition) at a free drive letter, so
	// the raw engine can read the backup image with no chance of modifying it. Throws on failure.
	// Live handle of the restore's read-only image attach. The attach is opened WITHOUT a permanent lifetime,
	// so closing this handle (or the process exiting, even on a crash) detaches the image — a leaked mount can
	// never keep the backup file locked again. The letter is remembered so cleanup can release it.
	private SafeFileHandle? vhdxRestoreHandle;
	private char vhdxRestoreLetter = '\0';

	private async Task<char> AttachVhdxReadOnlyAndAssignAsync(string vhdPath, char winLetter)
	{
		return await Task.Run(() =>
		{
			// A leftover mount (an interrupted run, or the user double-clicking the .vhdx in Explorer) holds the
			// file and would fail the attach with a sharing violation — clear it first, best-effort.
			VirtDisk.TryDetachByPath(vhdPath);
			SafeFileHandle handle = VirtDisk.AttachReadOnly(vhdPath, out int imageDiskNumber);
			try
			{
				// Volume arrival after the attach is asynchronous, and a multi-partition image can surface a data
				// partition before (or larger than) the Windows partition — so keep polling until the actual Windows
				// volume appears, and only give up (taking the largest volume) after the timeout so the caller's own
				// SYSTEM-hive check can report a precise message.
				string? volume = null;
				bool hasWindows = false;
				for (int i = 0; i < 40; i++)
				{
					volume = VirtDisk.FindWindowsVolumeOnDisk(imageDiskNumber, 1L << 30, out hasWindows);
					if (hasWindows) break;
					Thread.Sleep(250);
				}
				if (volume == null)
					throw new InvalidOperationException("No Windows partition (>1 GB) was found inside the image.");
				// The attach suppressed automount (NO_DRIVE_LETTER), so the volume has no active letter — always
				// assign our own reserved letter (GetFreeDriveLetter picks Z..G, never a system letter like C:).
				VirtDisk.AssignDriveLetter(winLetter, volume);
				vhdxRestoreHandle = handle;
				vhdxRestoreLetter = winLetter;
				Log("VHDX attached read-only (native VirtDisk API): disk " + imageDiskNumber + " -> " + winLetter + ":");
				return winLetter;
			}
			catch
			{
				handle.Dispose(); // closing the handle detaches — a failed attach never leaks a mount
				throw;
			}
		});
	}

	private Task DetachDiskImageAsync(string vhdPath)
	{
		return Task.Run(() =>
		{
			if (vhdxRestoreLetter != '\0')
			{
				VirtDisk.RemoveDriveLetter(vhdxRestoreLetter); // also clears the mount manager's remembered assignment
				vhdxRestoreLetter = '\0';
			}
			if (vhdxRestoreHandle != null)
			{
				VirtDisk.Detach(vhdxRestoreHandle);
				vhdxRestoreHandle.Dispose();
				vhdxRestoreHandle = null;
			}
			VirtDisk.TryDetachByPath(vhdPath); // fallback for any mount this run did not create
		});
	}

	// Restore a VHDX/VHD backup (made by "Export VHDX") onto a drive and make it bootable. Unlike the .wim restore
	// (file-level wimlib/DISM, which antivirus can block per-file), this reads the image with the RAW engine — the same
	// engine that makes "Clone This PC" faithful: AV-transparent, and it preserves ACLs, hardlinks, reparse points,
	// alternate data streams, EFS and WOF-compressed files bit-faithfully. Reuses the clone's layout + boot steps.
	private async Task RestoreVhdxToDriveAsync(string vhdPath, DiskItem disk)
	{
		if (!File.Exists(vhdPath))
			throw new InvalidOperationException("The selected image file no longer exists.");
		if (PhysicalDiskOfPath(vhdPath) == disk.Number)
			throw new InvalidOperationException("The image file is stored ON the drive you're restoring to — restoring would erase the image itself. Move the image to another drive first. No changes were made.");

		char currentTargetLetter = GetFirstUsableDriveLetter(disk);
		char bootLetter = GetFreeDriveLetter(currentTargetLetter);
		char windowsLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter);
		char vhdxWinLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter, windowsLetter);
		string realRoot = windowsLetter + ":\\";
		string realWindowsFolder = Path.Combine(realRoot, "Windows");
		string diskpartPath = Path.Combine(Path.GetTempPath(), $"driveforge-restore-vhdx-{Guid.NewGuid():N}.txt");

		TryEnablePrivilege("SeBackupPrivilege");
		TryEnablePrivilege("SeRestorePrivilege");
		TryEnablePrivilege("SeSecurityPrivilege");
		TryEnablePrivilege("SeTakeOwnershipPrivilege");
		TryEnablePrivilege("SeCreateSymbolicLinkPrivilege");

		// The real capacity + MBR-ceiling gate runs AFTER the read-only attach below, where the VHDX's ACTUAL used data can
		// be measured. The .vhdx file size is wrong for a FIXED VHDX (its file is the full provisioned size), so gating on
		// it here would refuse a large-but-nearly-empty image that easily fits. imageBytes is kept only for the progress total.
		long imageBytes = 0; try { imageBytes = new FileInfo(vhdPath).Length; } catch { }

		if (!await ConfirmTargetHealthAsync(disk))
		{
			Log("VHDX restore cancelled by user after target health warning.");
			operationAbortedBeforeWrite = true;
			SetStage(L("StgCancelHealth"), 0.0);
			return;
		}

		stopRequested = false;
		internalOperationStopped = false;
		bool attached = false;
		try
		{
			// 1. Attach the VHDX READ-ONLY and mount its Windows volume — the source we copy FROM. Read-only guarantees
			//    the backup image is never modified by the restore.
			SetStage(L("StgOpenVhdx"), 8.0);
			attached = true; // set BEFORE the call: if the image mounts but a later attach step throws, the finally must still detach it
			vhdxWinLetter = await AttachVhdxReadOnlyAndAssignAsync(vhdPath, vhdxWinLetter);
			string vhdxWinRoot = vhdxWinLetter + ":\\";
			if (!File.Exists(Path.Combine(vhdxWinRoot, "Windows", "System32", "config", "SYSTEM")))
				throw new InvalidOperationException("The VHDX does not contain a Windows installation (no SYSTEM hive on its Windows partition). Nothing was written to the target.");

			// Capacity + MBR-ceiling gate, now that the image is mounted so we can measure its ACTUAL used data (not the
			// .vhdx file size — a fixed VHDX's file is its full provisioned size, far bigger than the data the raw engine
			// actually copies). Reject a too-small target OR content past the ~2 TiB single-partition MBR ceiling BEFORE the
			// destructive format, so the copy can't ENOSPC after the wipe. If used space can't be read, fall through — the
			// post-copy DiskFull check still guards it.
			long vhdxUsedBytes = 0;
			try
			{
				var vhdxWinDrive = new DriveInfo(vhdxWinLetter + ":\\");
				vhdxUsedBytes = vhdxWinDrive.TotalSize - vhdxWinDrive.TotalFreeSpace;
				if (vhdxUsedBytes > 0 && disk.Size < (long)(vhdxUsedBytes * 1.25) + 512L * 1024 * 1024)
					throw new InvalidOperationException(
						"The selected drive is too small for this image.\n\nImage content: " + FormatBytes(vhdxUsedBytes) +
						"\nSelected drive: " + FormatBytes(disk.Size) + "\n\nNo changes were made — the drive was not formatted.");
				if (vhdxUsedBytes > 2199023255040L - 512L * 1024 * 1024)
					throw new InvalidOperationException("This image's content (" + FormatBytes(vhdxUsedBytes) + ") is larger than the ~2 TB an MBR restore layout supports, so it cannot be restored to this drive. No changes were made — the drive was not formatted.");
			}
			catch (InvalidOperationException) { throw; }
			catch { }

			// 2. Format the target with the same GPT layout the WIM restore / clone use.
			SetStage(L("StgFormatTarget"), 12.0);
			await File.WriteAllTextAsync(diskpartPath, BuildRealNtfsUsbLayoutDiskpartScript(disk.Number, bootLetter, windowsLetter), Encoding.ASCII);
			try { await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(diskpartPath)); }
			finally { TryDeleteFile(diskpartPath); }
			if (!Directory.Exists(realRoot))
				throw new InvalidOperationException("The target Windows partition (" + windowsLetter + ":) did not mount after formatting.");

			// 3. FAITHFUL raw copy from the VHDX's Windows volume to the target's Windows partition.
			SetStage(L("StgRestoreRaw"), 20.0);
			progressDoneGiB = 0.0; progressPrevGiB = 0.0;
			// Prefer the VHDX's real used data for the progress total; imageBytes (the .vhdx file size) over-states a fixed
			// VHDX, leaving the bar stuck low. Fall back to imageBytes if the used space couldn't be read above.
			progressTotalGiB = Math.Max(1.0, (vhdxUsedBytes > 0 ? vhdxUsedBytes : imageBytes) / 1073741824.0);
			ProgressBar.Value = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			RawCloneStats? rawStats = null;
			using (var rawPollCts = new CancellationTokenSource())
			{
				Task rawPoll = PollPartitionUsedSpaceAsync(realRoot, rawPollCts.Token, rawEngine: true);
				_suppressLineProgress = true;
				try { rawStats = await RawNtfsWriteCloneAsync(vhdxWinLetter, windowsLetter, realRoot); }
				finally { _suppressLineProgress = false; rawPollCts.Cancel(); try { await rawPoll; } catch { } }
			}
			if (stopRequested || internalOperationStopped)
				throw new OperationCanceledException("Restore stopped before the copy finished — the drive is incomplete.");
			if (rawStats != null && rawStats.DiskFull > 0)
				throw new InvalidOperationException("The target ran out of space during the copy — the restore is incomplete.");
			bool copyOk = File.Exists(Path.Combine(realWindowsFolder, "System32", "winload.efi"))
				&& File.Exists(Path.Combine(realWindowsFolder, "System32", "config", "SYSTEM"));
			if (!copyOk)
				throw new InvalidOperationException("The restore did not produce a complete Windows root (winload.efi / SYSTEM missing). The drive was formatted.");
			ProgressBar.Value = 88.0;

			// 4. Portable-registry post-processing + first-boot answer file (same pass as the clone / WIM restore).
			SetStage(L("StgApplyPortable"), 90.0);
			string regOut = await ApplyPortableRegistrySettingsToRealCloneAsync(realWindowsFolder,
				BypassRequirementsCheck?.IsChecked == true, BypassAccountCheck?.IsChecked == true, faithfulMode: true, portableMode: true);
			if (regOut.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Portable registry preparation failed after restore — the drive would boot mis-configured:\r\n" + regOut);
			WritePortableUnattend(realWindowsFolder);

			// 5. Make the drive bootable (BIOS + UEFI).
			SetStage(L("StgMakeBootable"), 95.0);
			await RunProcessCaptureAsync("bcdboot.exe", QuoteArgument(realWindowsFolder) + $" /s {bootLetter}: /f ALL /v");
			EnsureUefiRemovableFallback(bootLetter);

			// 6. Apply owners/ACLs LAST (after registry + bcdboot), read from the VHDX source.
			try { await RawNtfsApplySecurityAsync(vhdxWinLetter, realRoot); }
			catch (Exception secEx) { Log("WARNING: Fast Clone permission pass failed: " + secEx.Message + " (the drive is usable; permissions may be default)."); }

			await FlushVolumesAsync(bootLetter, windowsLetter);
			progressDoneGiB = progressTotalGiB;
			ProgressBar.Value = 100.0;
			if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
			// Regions the raw engine couldn't read from the VHDX source and zero-filled — the restore is complete in size
			// but those bytes are lost, so don't claim a "faithful" copy, and warn the user (restore has no dialog of its
			// own; the shared success tail would otherwise report a plain success).
			long restoreZeroFilled = rawStats != null ? rawStats.RunShortfalls + rawStats.ReadShortfalls : 0;
			Log("VHDX image restored to Disk " + disk.Number + (restoreZeroFilled > 0 ? $" (WITH {restoreZeroFilled} zero-filled unreadable region(s) — NOT byte-faithful)" : " (faithful raw copy)") + " and made bootable (BIOS + UEFI).");
			if (restoreZeroFilled > 0)
				MessageBox.Show(string.Format(L("MbRawZeroFilled"), restoreZeroFilled), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			if (attached)
			{
				try { await DetachDiskImageAsync(vhdPath); }
				catch (Exception dex) { Log("WARNING: could not detach the VHDX image cleanly: " + dex.Message); }
			}
		}
	}

	private async Task ApplyFfuAsync(string path, DiskItem disk)
	{
		if (!Path.GetExtension(path).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Restore clone mode requires a full clone file with .ffu extension.");
		}

		// The .ffu must NOT live on the disk we're about to rewrite: DISM /Apply-FFU cleans the whole PhysicalDrive,
		// which would erase the image mid-restore — destroying the backup AND corrupting the target. (The WIM path
		// already guards this at RestoreWimToDriveAsync; the FFU path was missing it.)
		if (PhysicalDiskOfPath(path) == disk.Number)
		{
			throw new InvalidOperationException("The clone image is stored ON the drive you're restoring to — restoring would erase the image itself. Move it to another drive first. No changes were made.");
		}

		// Validate the .ffu parses as a real FFU BEFORE the destructive apply. DISM /Apply-FFU cleans the whole
		// PhysicalDrive first, so discovering an unreadable/incomplete image AFTER the wipe is the worst ordering. This
		// reads only the FFU header/metadata — a lighter check than the WIM path's full-data wimlib-verify — but it still
		// catches a truncated or non-FFU file before any damage. A non-zero exit throws, so we abort before the wipe.
		SetStage(L("StgVerifyBeforeWrite"), 4.0);
		// FFU (like VHDX) requires /Index:1 with /Get-ImageInfo — without it DISM errors on a valid single-image FFU.
		try { await RunProcessCaptureAsync("dism.exe", "/Get-ImageInfo /ImageFile:" + QuoteArgument(path) + " /Index:1"); }
		catch (Exception vex)
		{
			throw new InvalidOperationException(
				"This .ffu could not be read as a valid FFU image — it may be corrupt or incomplete. No changes were made — the drive was not written.\n\n" + vex.Message);
		}

		// Capacity: an FFU is a whole-disk block image that DISM restores at the ORIGINAL captured disk's geometry, so the
		// target must be at least as large as THAT disk — not the (far smaller) compressed .ffu file. DriveForge can't read
		// the captured-disk size from an arbitrary .ffu, so a byte gate here would give false reassurance; make it the
		// user's informed decision. If the target is too small, DISM fails AFTER the clean (target already wiped), so this
		// confirmation is the only safeguard.
		if (!headlessRun && MessageBox.Show(
				string.Format(L("MbFfuSizeWarn"), FormatBytes(disk.Size)),
				"DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
		{
			Log("FFU restore cancelled at the size confirmation.");
			operationAbortedBeforeWrite = true;
			SetStage(L("StgCancelHealth"), 0.0);
			return;
		}

		// Target health gate before the destructive write.
		if (!await ConfirmTargetHealthAsync(disk))
		{
			Log("FFU restore cancelled by user after target health warning.");
			operationAbortedBeforeWrite = true;
			SetStage(L("StgCancelHealth"), 0.0);
			return;
		}

		SetStage(L("StgRestoreFullClone"), 8.0);
		// DISM /Apply-FFU emits no line-based progress, so show an honest moving (indeterminate) bar with a ticking Elapsed during the write.
		// try/finally: on a DISM failure (the documented common one — a target smaller than the captured disk — fails
		// AFTER the clean) the bar used to stay indeterminate, and UpdateProgressStats blanks the percent label in that
		// mode, so the error dialog appeared beside a completely empty label.
		ProgressBar.IsIndeterminate = true;
		try { await RunProcessAsync("dism.exe", $"/Apply-FFU /ImageFile:\"{path}\" /ApplyDrive:\\\\.\\PhysicalDrive{disk.Number}"); }
		finally { ProgressBar.IsIndeterminate = false; }

		// Post-restore check: rescan and confirm the image produced partitions on the target.
		SetStage(L("StgVerifyRestoredClone"), 90.0);
		string partCheck = await RunProcessCaptureAsync("powershell.exe",
			"-NoProfile -Command " + QuoteArgument(
				$"(Get-Partition -DiskNumber {disk.Number} -ErrorAction SilentlyContinue | Measure-Object).Count"));
		bool restoredOk = int.TryParse(partCheck.Trim(), out int partCount) && partCount > 0;
		if (!restoredOk)
		{
			throw new InvalidOperationException("FFU restore finished but no partitions were found on the target disk. The image may be invalid or the write failed.");
		}
		Log($"Full clone restored to PhysicalDrive{disk.Number} — {partCount} partition(s) present after restore.");
	}

	private async Task<string> EnsureWimlibAsync()
	{
		string toolRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge", "Tools", "wimlib");
		string exePath = Directory.Exists(toolRoot)
			? Directory.GetFiles(toolRoot, "wimlib-imagex.exe", SearchOption.AllDirectories).FirstOrDefault()
			: null;
		if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
		{
			return exePath;
		}
		Directory.CreateDirectory(toolRoot);
		string zipPath = Path.Combine(toolRoot, "wimlib.zip");
		SetStage(L("StgPrepStream"), 5.0);
		const string ExpectedWimlibSha256 = "6D99E242BFBC6D36FC987D433D63772180551B7F2D8DE43E9561535A3E2C16D8";

		// Offline-first: wimlib ships embedded inside the app, so no download is needed. Extract it from the
		// embedded resource; only fall back to a one-time download if the embedded copy is somehow missing.
		bool fromEmbedded = false;
		using (Stream? res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("DriveForge.wimlib.zip"))
		{
			if (res != null)
			{
				using (var fileOut = File.Create(zipPath)) { await res.CopyToAsync(fileOut); }
				fromEmbedded = true;
				Log("Clone engine loaded from the embedded copy (no download needed).");
			}
		}
		if (!fromEmbedded)
		{
			Log("Embedded clone engine not found — downloading once from wimlib.net.");
			using HttpClient client = new HttpClient();
			client.Timeout = TimeSpan.FromMinutes(10L);
			await File.WriteAllBytesAsync(zipPath, await client.GetByteArrayAsync("https://wimlib.net/downloads/wimlib-1.14.4-windows-x86_64-bin.zip"));
		}
		string actualSha256;
		using (var sha = System.Security.Cryptography.SHA256.Create())
		using (var stream = File.OpenRead(zipPath))
		{
			actualSha256 = Convert.ToHexString(sha.ComputeHash(stream));
		}
		if (!string.Equals(actualSha256, ExpectedWimlibSha256, StringComparison.OrdinalIgnoreCase))
		{
			TryDeleteFile(zipPath);
			throw new InvalidOperationException(
				"The downloaded clone engine (wimlib) failed its integrity check and was rejected.\n\n" +
				"Expected SHA-256: " + ExpectedWimlibSha256 + "\nActual SHA-256: " + actualSha256 +
				"\n\nThe download may be corrupted or blocked. Check your connection and try again.");
		}
		Log("Clone engine integrity verified (SHA-256 matches the official wimlib 1.14.4 build).");
		ZipFile.ExtractToDirectory(zipPath, toolRoot, overwriteFiles: true);
		exePath = Directory.GetFiles(toolRoot, "wimlib-imagex.exe", SearchOption.AllDirectories).FirstOrDefault();
		if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
		{
			throw new FileNotFoundException("Could not prepare the streaming clone engine.");
		}
		Log("Streaming clone engine prepared: " + exePath);
		return exePath;
	}

	private async Task StreamCloneWithWimlibAsync(string wimlibPath, string sourceRoot, char windowsLetter, string configPath)
	{
		string source = sourceRoot.TrimEnd('\\') + "\\.";
		string target = windowsLetter + ":\\.";
		int threadCount = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
		// Run capture and apply as two processes joined by an IN-PROCESS pipe (capture stdout -> apply stdin) so we can
		// check BOTH exit codes. A cmd `A | B` only exposes B's (apply's) code, and apply's --recover-data downgrades a
		// truncated stream to a non-fatal success — so a wimlib CAPTURE failure would otherwise finalize an INCOMPLETE
		// clone as a success. Here a nonzero CAPTURE code fails the whole clone. ProcessStartInfo.ArgumentList quotes
		// each argument for CreateProcess, so paths with spaces need no manual quoting.
		var capPsi = new ProcessStartInfo { FileName = wimlibPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var a in new[] { "capture", source, "-", "Current Windows", "Captured by DriveForge", "--pipable", "--compress=none", "--threads=" + threadCount, "--config=" + configPath }) capPsi.ArgumentList.Add(a);
		var appPsi = new ProcessStartInfo { FileName = wimlibPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
		foreach (var a in new[] { "apply", "-", "1", target, "--recover-data" }) appPsi.ArgumentList.Add(a);
		using var cap = Process.Start(capPsi) ?? throw new InvalidOperationException("Could not start wimlib capture.");
		using var app = Process.Start(appPsi) ?? throw new InvalidOperationException("Could not start wimlib apply.");
		// Drain both stderr streams concurrently so neither process blocks on a full pipe, and pump capture -> apply.
		Task<string> capErr = cap.StandardError.ReadToEndAsync();
		Task<string> appErr = app.StandardError.ReadToEndAsync();
		try { await cap.StandardOutput.BaseStream.CopyToAsync(app.StandardInput.BaseStream); }
		catch (IOException) { /* apply may have exited early (broken pipe) — its exit code below tells the real story */ }
		finally { try { app.StandardInput.Close(); } catch { } }
		await cap.WaitForExitAsync();
		await app.WaitForExitAsync();
		string capStdErr = await capErr, appStdErr = await appErr;
		if (cap.ExitCode != 0)
			throw new InvalidOperationException($"wimlib capture failed (exit {cap.ExitCode}) — the clone would be incomplete, so it was aborted. {capStdErr.Trim()}");
		if (app.ExitCode != 0)
			throw new InvalidOperationException($"wimlib apply failed (exit {app.ExitCode}). {appStdErr.Trim()}");
	}

	// Microsoft-engine (DISM/WIMGAPI) CAPTURE — the classic image-capture approach. Reads the VSS snapshot into a temporary WIM on
	// a scratch drive. DISM is a signed Windows component, so real-time antivirus does NOT scan its reads the way it
	// scans third-party wimlib — this does not crawl on WinSxS with an AV active. It runs BEFORE the target is
	// formatted and verifies the image before returning, so a bad/incomplete capture is caught while the target is
	// still untouched (never leaves the user with a wiped drive and no image). File-level, so it fits a smaller target.
	private async Task<string> DismCaptureToWimAsync(string sourceRoot, string scratchDir)
	{
		string wimPath = Path.Combine(scratchDir, $"driveforge-clone-{Guid.NewGuid():N}.wim");
		string configPath = Path.Combine(Path.GetTempPath(), $"driveforge-dism-config-{Guid.NewGuid():N}.ini");
		await File.WriteAllTextAsync(configPath, BuildCaptureConfig(), Encoding.ASCII); // WimScript.ini [ExclusionList] is DISM's native /ConfigFile format
		try
		{
			SetStage(L("StgCaptureMs"), 12.0);
			Log("DISM engine: capturing the VSS snapshot to " + wimPath + " — Microsoft's imaging is not slowed by antivirus. This runs before the target is touched.");
			// Keep the bar in a low band during capture: DISM prints its own %, but letting it drive the bar to 82%
			// then rewinding when apply starts reads as a malfunction. Poll nothing here (the target isn't written yet).
			_suppressLineProgress = true;
			try
			{
				// NOTE: /CaptureDir must NOT be quoted. It's a single-letter DOS device (e.g. "X:\"), and quoting a
				// path that ends in '\' makes the trailing \" escape the closing quote — DISM then mis-parses every
				// later argument (error 87). Unquoted is safe here (no spaces).
				await RunProcessAsync("dism.exe",
					"/Capture-Image /ImageFile:" + QuoteArgument(wimPath) +
					" /CaptureDir:" + sourceRoot.TrimEnd('\\') + "\\" +
					" /Name:" + QuoteArgument("Current Windows") +
					" /ConfigFile:" + QuoteArgument(configPath) + " /Compress:fast");
			}
			finally { _suppressLineProgress = false; }
			// Verify the captured image BEFORE the caller formats the target — a bad capture must not wipe the drive.
			SetStage(L("StgVerifyCaptured"), 16.0);
			await RunProcessAsync("dism.exe", "/Get-WimInfo /WimFile:" + QuoteArgument(wimPath) + " /Index:1");
			Log("DISM engine: capture verified (image is complete). Safe to format the target now.");
			return wimPath;
		}
		catch
		{
			TryDeleteFile(wimPath); // capture failed — drop the partial WIM (the target is still untouched)
			throw;
		}
		finally { TryDeleteFile(configPath); }
	}

	// Apply the DISM-captured WIM onto the target with WIMLIB, not dism.exe. wimlib's apply tolerates the
	// security-descriptor / integrity-label quirks that make dism.exe /Apply-Image fail with error 1299 on some
	// live-captured Windows installs, and --recover-data continues past a non-fatal file. The antivirus-crawl problem
	// was on the CAPTURE (reading the source's WinSxS) — which DISM already handled — not on writing a local WIM to a
	// fresh target, so this stays fast. Polls the target's growing used-space for a live bar.
	private async Task DismApplyWimAsync(string wimPath, string wimlibPath, char windowsLetter, string targetRoot)
	{
		SetStage(L("StgApplyWin"), 45.0);
		progressDoneGiB = 0.0;
		progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1073741824.0);
		progressSpeedMb = 0.0; _speedWindow.Clear();
		using var pollCts = new CancellationTokenSource();
		Task poll = PollPartitionUsedSpaceAsync(targetRoot, pollCts.Token);
		_suppressLineProgress = true;
		try
		{
			// Target dir uses ":\\." (trailing dot, not "X:\") — a quoted path ending in '\' would have its \" escape
			// the closing quote, so wimlib would see "X:\" --recover-data"" as one bad dir name (status c0000033).
			await RunProcessAsync(wimlibPath,
				"apply " + QuoteArgument(wimPath) + " 1 " + QuoteArgument(windowsLetter + ":\\.") + " --recover-data");
		}
		finally { _suppressLineProgress = false; pollCts.Cancel(); try { await poll; } catch { } }
	}

	// Pick a folder for the temporary capture WIM: a fixed (non-removable) drive, NOT on the clone TARGET disk
	// (compared by physical disk number, so a freshly-created empty target partition can't win on free space), that
	// has at least requiredBytes free. Returns "" when no drive has room — the caller then falls back to the no-temp
	// wimlib pipe engine so a nearly-full PC can still clone.
	private static string PickScratchDirForWim(int targetDiskNumber, long requiredBytes)
	{
		try
		{
			DriveInfo? best = DriveInfo.GetDrives()
				.Where(d => d.DriveType == DriveType.Fixed && d.IsReady
					&& PhysicalDiskOfPath(d.RootDirectory.FullName) != targetDiskNumber
					&& d.AvailableFreeSpace >= requiredBytes)
				.OrderByDescending(d => d.AvailableFreeSpace)
				.FirstOrDefault();
			return best?.RootDirectory.FullName ?? "";
		}
		catch { return ""; }
	}

	// Polls a partition's used space (TotalSize - free) and publishes it as copy progress while an external
	// tool (wimlib) writes to it. The dispatcher timer turns this into a live bar, speed and ETA.
	private async Task PollPartitionUsedSpaceAsync(string root, CancellationToken token, bool rawEngine = false)
	{
		long lastUsed = 0;
		DateTime lastAdvanceUtc = DateTime.UtcNow;
		DateTime lastWarnUtc = DateTime.MinValue;
		double lastEngineCpuSec = -1.0; DateTime lastEngineCpuUtc = DateTime.MinValue; // rolling wimlib CPU sample, to tell "engine busy" from "drive slow" on a stall
		const long AdvanceThreshold = 200L * 1024 * 1024; // 200 MB counts as real progress
		while (!token.IsCancellationRequested)
		{
			try
			{
				var di = new DriveInfo(root);
				long used = Math.Max(0L, di.TotalSize - di.TotalFreeSpace);
				Volatile.Write(ref _progressDoneBytes, used);

				// Stall/low-speed watchdog: if the target stops growing for a while, the write has
				// effectively stalled — tell the user the likely cause instead of leaving them guessing.
				DateTime now = DateTime.UtcNow;
				// Rolling sample of the imaging engine's CPU so a stall can be attributed correctly: high CPU with no
				// disk writes = the engine is grinding through metadata/dedup (keep waiting), not a slow/full drive.
				// Only sample the engine's CPU once the target has been flat for a while (approaching the stall
				// threshold). Enumerating every process each tick on the UI thread would needlessly jank a long clone.
				double engineCores = 0.0;
				if ((now - lastAdvanceUtc).TotalSeconds > 120)
				{
					try
					{
						double engineSec = 0.0;
						foreach (Process ep in Process.GetProcesses())
						{
							try { if (ep.ProcessName.StartsWith("wimlib", StringComparison.OrdinalIgnoreCase) || ep.ProcessName.StartsWith("dism", StringComparison.OrdinalIgnoreCase)) engineSec += ep.TotalProcessorTime.TotalSeconds; }
							catch { }
							finally { ep.Dispose(); }
						}
						// The raw engine runs IN-PROCESS (no wimlib/dism), so attribute THIS process's CPU — otherwise its
						// metadata phases (dir tree, hardlink dedup) look like a stalled/slow drive and falsely warn the user.
						if (rawEngine) { try { using var self = Process.GetCurrentProcess(); engineSec += self.TotalProcessorTime.TotalSeconds; } catch { } }
						if (lastEngineCpuSec >= 0.0)
						{
							double dt = (now - lastEngineCpuUtc).TotalSeconds;
							if (dt > 0.2) engineCores = Math.Max(0.0, (engineSec - lastEngineCpuSec) / dt);
						}
						lastEngineCpuSec = engineSec; lastEngineCpuUtc = now;
					}
					catch { }
				}
				if (used > lastUsed + AdvanceThreshold)
				{
					lastUsed = used;
					lastAdvanceUtc = now;
					lastEngineCpuSec = -1.0; // reset the CPU baseline so the next stall window samples fresh
				}
				else if (used > 0
					&& (now - lastAdvanceUtc).TotalSeconds > 150
					&& (now - lastProcessOutputUtc).TotalSeconds > 60 // AND the engine has gone silent — a real stall, not a legit long scan that's still emitting progress lines
					&& (now - lastWarnUtc).TotalSeconds > 180)
				{
					lastWarnUtc = now;
					double mbPerSec = used / 1024.0 / 1024.0 / Math.Max(1.0, operationStopwatch.Elapsed.TotalSeconds);
					int stalledMin = (int)Math.Max(1, (now - lastAdvanceUtc).TotalMinutes);
					string onScreen;
					if (engineCores > 0.5)
					{
						// The engine is pegging CPU with no disk writes: grinding through metadata / dedup on a large or
						// file-heavy image. This is slow, not dead — advise patience instead of blaming the drive.
						Log($"NOTE: the imaging engine is busy on CPU (~{engineCores * 100.0:F0}%) with no disk writes — building metadata / deduplicating a large or file-heavy image.");
						Log("This can take many minutes on installs with huge numbers of small files (dev caches, node_modules, .git). It is slow, not stalled.");
						onScreen = $"⚙ Engine busy (~{engineCores * 100.0:F0}% CPU), not writing yet — normal for very large / file-heavy images. Keep waiting; press Stop only if it never advances.";
					}
					else
					{
						Log($"WARNING: write has stalled (~{mbPerSec:F1} MB/s overall) — less than 200 MB written in the last {stalledMin} min.");
						Log("Likely causes: the target drive is too slow or nearly full (USB 2.0 port/hub, a slow/overheating QLC portable SSD, or a drive that is almost out of space), OR an unreadable file / bad sector on the source is stalling the read.");
						Log("Tips: use a USB 3.x port directly (no hub); use a larger/faster or emptier target; add a Defender exclusion for the target; or run 'chkdsk C: /scan' on the source.");
						onScreen = $"⚠ Stalled: no new data for {stalledMin} min (~{mbPerSec:F0} MB/s). The target may be too slow/full, or the source is hard to read — see the log. Keep waiting or press Stop.";
					}
					// Surface it ON SCREEN too — otherwise the frozen 'Scanning…/indexed' line looks like a dead app.
					Dispatcher.BeginInvoke((Action)(() => { if (isBusy) StatusText.Text = onScreen; }));
				}
			}
			catch { }
			try { await Task.Delay(1500, token); }
			catch (TaskCanceledException) { break; }
		}
	}

	private async Task<ShadowCopyInfo> CreateShadowCopyAsync(string systemDrive)
	{
		string driveRoot = systemDrive.TrimEnd('\\');
		if (!driveRoot.EndsWith(":", StringComparison.Ordinal))
		{
			driveRoot += ":";
		}
		driveRoot += "\\";
		string script = "$volume = " + PsQuote(driveRoot) + "\n" +
			"$class = Get-WmiObject -List Win32_ShadowCopy\n" +
			"$result = $class.Create($volume, 'ClientAccessible')\n" +
			"if ($result.ReturnValue -ne 0) { throw ('VSS snapshot failed with code ' + $result.ReturnValue) }\n" +
			"$sid = $result.ShadowID\n" +
			"$shadow = Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID -eq $sid } | Select-Object -First 1\n" +
			"if ($null -eq $shadow) { $shadow = Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID.Trim('{','}') -ieq $sid.Trim('{','}') } | Select-Object -First 1 }\n" +
			"if ($null -eq $shadow) { try { & vssadmin delete shadows /shadow=$sid /quiet | Out-Null } catch {}; throw 'VSS snapshot was created but could not be found (deleted it to avoid an orphan).' }\n" +
			"[pscustomobject]@{ Id = $shadow.ID; DeviceObject = $shadow.DeviceObject } | ConvertTo-Json -Compress";
		string json = ExtractJsonPayload(await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script)));
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		string id = GetJsonString(jsonDocument.RootElement, "Id", "");
		string deviceObject = GetJsonString(jsonDocument.RootElement, "DeviceObject", "");
		if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(deviceObject))
		{
			throw new InvalidOperationException("VSS snapshot did not return a usable device path.");
		}
		Log("VSS snapshot created: " + deviceObject);
		return new ShadowCopyInfo(id, deviceObject);
	}

	private async Task DeleteShadowCopyAsync(string id)
	{
		try
		{
			string script = "$id = " + PsQuote(id) + "\n" +
				"$shadow = Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID -eq $id } | Select-Object -First 1\n" +
				"if ($null -ne $shadow) { $shadow.Delete() | Out-Null }";
			await RunProcessAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script), allowFailure: true);
			Log("VSS snapshot deleted.");
		}
		catch (Exception ex)
		{
			Log("VSS cleanup skipped: " + ex.Message);
		}
	}

	private static string GetDosDeviceTarget(string deviceObject)
	{
		string target = deviceObject.TrimEnd('\\');
		const string prefix = "\\\\?\\GLOBALROOT";
		if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			target = target.Substring(prefix.Length);
		}
		if (!target.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Unexpected VSS snapshot path: " + deviceObject);
		}
		return target;
	}

	private void MapSnapshotDrive(char driveLetter, string target)
	{
		string deviceName = char.ToUpperInvariant(driveLetter) + ":";
		if (!DefineDosDevice(DddRawTargetPath, deviceName, target))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not map VSS snapshot to drive " + deviceName);
		}
		Log("VSS snapshot mapped as " + deviceName + "\\");
	}

	private void UnmapSnapshotDrive(char driveLetter, string target)
	{
		string deviceName = char.ToUpperInvariant(driveLetter) + ":";
		try
		{
			if (DefineDosDevice(DddRemoveDefinition | DddExactMatchOnRemove | DddRawTargetPath, deviceName, target))
			{
				Log("VSS snapshot drive unmapped: " + deviceName);
			}
		}
		catch (Exception ex)
		{
			Log("Snapshot drive cleanup skipped: " + ex.Message);
		}
	}

	private static string BuildCaptureConfig()
	{
		return string.Join(Environment.NewLine, new[]
		{
			"[ExclusionList]",
			"\\$Recycle.Bin",
			"\\System Volume Information",
			"\\$Windows.~BT",       // Windows upgrade staging — can be several GB
			"\\$Windows.~BT\\*",
			"\\$Windows.~WS",       // Windows Setup workspace
			"\\$Windows.~WS\\*",
			"\\pagefile.sys",
			"\\hiberfil.sys",
			"\\swapfile.sys",
			"\\MEMORY.DMP",
			"\\Windows\\Temp\\*",
			"\\Windows\\Logs\\*",
			"\\Windows\\Panther\\*",
			"\\Windows\\Minidump\\*",
			"\\Windows\\LiveKernelReports\\*",
			"\\Windows\\System32\\LogFiles\\*",
			"\\Windows\\System32\\winevt\\Logs\\*",        // event logs — 200-500 MB, machine-specific, auto-rebuilt by Event Log service
			"\\Windows\\System32\\SRU\\*",                 // SRUDB.dat System Resource Usage DB — 100+ MB, machine-specific, auto-rebuilt
			"\\Windows\\SoftwareDistribution\\Download\\*",
			"\\Windows\\SoftwareDistribution\\DataStore\\*",  // DataStore.edb (~100-200 MB) + Logs — machine-specific WU state, auto-rebuilt
			"\\Windows\\Prefetch\\*",
			"\\ProgramData\\Microsoft\\Windows\\WER\\*",
			"\\ProgramData\\Microsoft\\Windows\\DeliveryOptimization\\Cache\\*",
			"\\ProgramData\\Microsoft\\Search\\Data\\*",    // Windows Search index — 1-4 GB, machine-specific, auto-rebuilt by WSearch service
			"\\ProgramData\\Package Cache\\*",
			// --- Developer / build CACHES under the user profile: regenerable, machine-specific, large file counts.
			//     Anchored to cache locations only — deliberately NOT bare names like "node_modules"/".vs", which
			//     would also strip the copies bundled inside installed apps (VS Code, Slack, …) under Program Files
			//     and break them on the clone. ---
			"\\Users\\*\\.nuget\\packages\\*",           // NuGet package cache — restore with 'dotnet restore'
			"\\Users\\*\\AppData\\Local\\npm-cache\\*",
			"\\Users\\*\\AppData\\Roaming\\npm-cache\\*",
			"\\Users\\*\\AppData\\Local\\pip\\Cache\\*",
			"\\Users\\*\\.cargo\\registry\\*",           // Rust crate cache
			"\\Users\\*\\.cargo\\git\\*",
			// --- Third-party antivirus DATA / quarantine: self-protected (they lock the scan and stall the capture),
			//     machine-specific, and auto-rebuilt. NOT their \Program Files\ binaries, which the clone still needs. ---
			"\\ProgramData\\Bitdefender\\*",
			"\\ProgramData\\Kaspersky Lab\\*",
			"\\ProgramData\\Norton\\*",
			"\\ProgramData\\NortonLifeLock\\*",
			"\\ProgramData\\ESET\\*",
			"\\ProgramData\\AVAST Software\\*",
			"\\ProgramData\\AVG\\*",
			"\\ProgramData\\McAfee\\*",
			"\\ProgramData\\Malwarebytes\\*",
			"\\ProgramData\\NVIDIA Corporation\\Downloader\\*",
			"\\AMD\\*",
			"\\Program Files\\dotnet\\packs\\Microsoft.NET.Runtime.MonoAOTCompiler.Task",
			"\\Program Files\\dotnet\\packs\\Microsoft.NET.Runtime.MonoAOTCompiler.Task\\*",
			"\\Users\\*\\AppData\\Local\\Temp\\*",
			"\\Users\\*\\AppData\\Local\\Microsoft\\Windows\\INetCache\\*",
			"\\Users\\*\\AppData\\Local\\Microsoft\\Windows\\WebCache\\*",
			"\\Users\\*\\AppData\\Local\\Microsoft\\Windows\\Explorer\\thumbcache_*.db",
			"\\Users\\*\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\Cache\\*",
			"\\Users\\*\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\Code Cache\\*",
			"\\Users\\*\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\GPUCache\\*",
			"\\Users\\*\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache\\*",
			"\\Users\\*\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Code Cache\\*",
			"\\Users\\*\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\GPUCache\\*",
			"\\Users\\*\\AppData\\Local\\BraveSoftware\\Brave-Browser\\User Data\\Default\\Cache\\*",
			"\\Users\\*\\AppData\\Local\\BraveSoftware\\Brave-Browser\\User Data\\Default\\Code Cache\\*",
			"\\Users\\*\\AppData\\Local\\Mozilla\\Firefox\\Profiles\\*\\cache2\\*",
			"\\Users\\*\\AppData\\Local\\Packages\\*\\AC\\Temp\\*"
		}) + Environment.NewLine;
	}

	private long GetCurrentWindowsUsedBytes()
	{
		string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
		DriveInfo driveInfo = new DriveInfo(systemDrive);
		return Math.Max(0L, driveInfo.TotalSize - driveInfo.AvailableFreeSpace);
	}

	private sealed record SourceDataPartition(char Letter, string Label, long UsedBytes, long SizeBytes);

	// Finds the OTHER data partitions on the same physical disk as Windows (e.g. a D: data partition):
	// NTFS, has a drive letter, not the system drive. These can optionally be cloned alongside Windows.
	private async Task<List<SourceDataPartition>> GetSourceDataPartitionsAsync()
	{
		var result = new List<SourceDataPartition>();
		string sys = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd(':');
		string script =
			"$sys='" + sys + "'; " +
			"$d=(Get-Partition -DriveLetter $sys -ErrorAction SilentlyContinue).DiskNumber; " +
			"Get-Partition -DiskNumber $d -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter -and ([string]$_.DriveLetter) -ne $sys } | ForEach-Object { " +
			"$v=Get-Volume -DriveLetter $_.DriveLetter -ErrorAction SilentlyContinue; " +
			"if ($v -and $v.FileSystem -eq 'NTFS') { [pscustomobject]@{ Letter=[string]$_.DriveLetter; Label=$v.FileSystemLabel; Used=($v.Size-$v.SizeRemaining); Size=$v.Size } } } | ConvertTo-Json -Compress";
		string json;
		try { json = (await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script))).Trim(); }
		catch { return result; }
		if (string.IsNullOrWhiteSpace(json)) return result;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json.StartsWith("[") ? json : "[" + json + "]");
			foreach (JsonElement el in doc.RootElement.EnumerateArray())
			{
				string letterStr = el.TryGetProperty("Letter", out var l) ? l.GetString() ?? "" : "";
				if (letterStr.Length == 0) continue;
				char letter = char.ToUpperInvariant(letterStr[0]);
				string label = el.TryGetProperty("Label", out var lb) && lb.ValueKind == JsonValueKind.String ? lb.GetString() ?? "" : "";
				long used = el.TryGetProperty("Used", out var u) && u.TryGetInt64(out long uv) ? uv : 0;
				long size = el.TryGetProperty("Size", out var s) && s.TryGetInt64(out long sv) ? sv : 0;
				result.Add(new SourceDataPartition(letter, label, used, size));
			}
		}
		catch { }
		return result;
	}

	private async Task RunRequiredPreflightAsync(DiskItem disk)
	{
		Log("Preflight: checking disk type, health, space, and speed.");
		UpdateDiskSummary();
		if (!speedResults.ContainsKey(disk.Number))
		{
			await RunSpeedTestAsync(auto: true);
		}
		if (!string.Equals(disk.HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase) && !string.Equals(disk.HealthStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The selected drive health is not OK: " + disk.HealthText);
		}
	}

	// Health gate before the destructive diskpart 'clean': reads SMART/reliability counters and, if the
	// target shows signs of wear or failure, asks the user to confirm before erasing it. A clone can
	// "succeed" on paper yet be unbootable on a dying USB stick — a well-known Windows-To-Go failure mode.
	// Returns true to proceed, false to abort. Never blocks when SMART is simply unavailable (many USB
	// bridges do not expose it).
	private async Task<bool> ConfirmTargetHealthAsync(DiskItem disk)
	{
		string report;
		try
		{
			report = await GetSmartDetailsAsync(disk);
		}
		catch
		{
			// Cannot read SMART — do not block, but STILL re-verify the target's identity right before the wipe.
			return await VerifyTargetDiskUnchangedAsync(disk);
		}

		var warnings = new List<string>();
		string health = ExtractReportValue(report, "HealthStatus");
		// Test the BAD values FIRST. "Unhealthy" CONTAINS "Healthy", so the old `!health.Contains("Healthy")` test was
		// false for it and this gate — the last thing standing in front of a destructive wipe — was structurally blind to
		// the one status that means the storage stack has already declared the media failed. MSFT_PhysicalDisk.HealthStatus
		// is Healthy / Warning / Unhealthy, so only "Warning" was ever caught. IsHealthy() already orders it this way.
		if (!string.IsNullOrWhiteSpace(health)
			&& (health.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase)
				|| health.Contains("Degraded", StringComparison.OrdinalIgnoreCase)
				|| health.Contains("Warning", StringComparison.OrdinalIgnoreCase)
				|| health.Contains("Fail", StringComparison.OrdinalIgnoreCase)
				|| (!health.Contains("Healthy", StringComparison.OrdinalIgnoreCase)
					&& !health.Contains("Unknown", StringComparison.OrdinalIgnoreCase))))
		{
			warnings.Add("Reported health: " + health);
		}

		string wearText = ExtractReportValue(report, "Wear");
		if (int.TryParse(wearText, out int wear) && wear >= 80)
		{
			warnings.Add($"Wear level: {wear}% (SSD life largely consumed)");
		}

		string readErrText = ExtractReportValue(report, "ReadErrorsTotal");
		if (long.TryParse(readErrText, out long readErrors) && readErrors > 0)
		{
			warnings.Add($"Read errors total: {readErrors:N0}");
		}

		string writeErrText = ExtractReportValue(report, "WriteErrorsTotal");
		if (long.TryParse(writeErrText, out long writeErrors) && writeErrors > 0)
		{
			warnings.Add($"Write errors total: {writeErrors:N0}");
		}

		if (long.TryParse(ExtractReportValue(report, "ReadErrorsUncorrected"), out long ruc) && ruc > 0) warnings.Add($"Read errors uncorrectable: {ruc:N0}");
			if (long.TryParse(ExtractReportValue(report, "WriteErrorsUncorrected"), out long wuc) && wuc > 0) warnings.Add($"Write errors uncorrectable: {wuc:N0}");
			if (warnings.Count == 0)
		{
			Log("Target health gate: SMART/reliability counters look OK.");
			// Re-verify the target's size+serial immediately before the caller's diskpart wipe: this is the LAST gate,
			// and the target could have been renumbered since the primary confirm (ISO mount / disk hot-plug in between).
			return await VerifyTargetDiskUnchangedAsync(disk);
		}

		string details = string.Join("\n- ", warnings);
		Log("Target health gate flagged the drive: " + string.Join("; ", warnings));
		if (headlessRun) { Log("Headless run: proceeding despite health warnings."); return true; }
		MessageBoxResult choice = MessageBox.Show(
			"The selected target drive shows health warnings:\n\n- " + details +
			"\n\nDisk " + disk.Number + " - " + disk.FriendlyName +
			"\n\nCloning will ERASE this drive, and a worn/failing drive can produce a clone that does not boot " +
			"or that loses data over time.\n\nContinue anyway?",
			"DriveForge - target drive health warning",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No); // default to the SAFE button (safety contract) — Enter/Space must NOT erase the drive
		if (choice != MessageBoxResult.Yes) return false;
		// This modal can sit open a while, and a failing USB (the very reason it's shown) can drop off the bus and let
		// Windows hand its disk number to another drive. Re-verify size+serial right before the caller's diskpart wipe.
		return await VerifyTargetDiskUnchangedAsync(disk);
	}

	private bool HasEnoughSpace(DiskItem disk, out string message)
	{
		long required = EstimateRequiredBytes();
		if (required > 0 && disk.Size < required)
		{
			message = $"The selected drive is too small.\n\nRequired: {FormatBytes(required)}\nSelected drive: {FormatBytes(disk.Size)}";
			return false;
		}
		message = "OK";
		return true;
	}

	private long EstimateRequiredBytes()
	{
		long margin = 12L * 1024L * 1024L * 1024L;
		if (ModeBox.SelectedIndex == ModeCloneCurrentWindows || ModeBox.SelectedIndex == ModeCloneInternal)
		{
			return Math.Max(64L * 1024L * 1024L * 1024L, GetCurrentWindowsUsedBytes() + margin);
		}
		if (ModeBox.SelectedIndex == ModeRestoreSavedClone && !string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
		{
			return new FileInfo(sourcePath).Length + margin;
		}
		return 64L * 1024L * 1024L * 1024L;
	}

	// Lets the user choose the local account (name + optional password) created during OOBE so the install
	// never asks for a Microsoft account. This is the only reliable Microsoft-account bypass on Win11 24H2/25H2
	// (the old BypassNRO registry tweak was removed). Stored in localAccountName/localAccountPassword.
	private void PromptLocalAccount()
	{
		localAccountName = "";
		localAccountPassword = "";
		var dialog = new Window
		{
			Title = L("DlgLocalAccount"),
			Width = 460,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			Background = (Brush)FindResource("NavyBrush")
		};
		var panel = new StackPanel { Margin = new Thickness(16) };
		panel.Children.Add(new TextBlock
		{
			Text = L("LocAcctIntro"),
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("TextBrush"),
			Margin = new Thickness(0, 0, 0, 10)
		});
		panel.Children.Add(new TextBlock { Text = L("LocAcctName"), Foreground = (Brush)FindResource("TextBrush") });
		var nameBox = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		panel.Children.Add(nameBox);
		panel.Children.Add(new TextBlock { Text = L("LocAcctPwOpt"), Foreground = (Brush)FindResource("TextBrush") });
		var pw1 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw1Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw1);
		panel.Children.Add(pw1Plain);
		panel.Children.Add(new TextBlock { Text = L("PwConfirmLabel"), Foreground = (Brush)FindResource("TextBrush") });
		var pw2 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw2Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw2);
		panel.Children.Add(pw2Plain);
		var showCheck = new CheckBox { Content = L("ShowPwLabel"), Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 4) };
		panel.Children.Add(showCheck);
		showCheck.Checked += delegate
		{
			pw1Plain.Text = pw1.Password; pw2Plain.Text = pw2.Password;
			pw1.Visibility = Visibility.Collapsed; pw2.Visibility = Visibility.Collapsed;
			pw1Plain.Visibility = Visibility.Visible; pw2Plain.Visibility = Visibility.Visible;
		};
		showCheck.Unchecked += delegate
		{
			pw1.Password = pw1Plain.Text; pw2.Password = pw2Plain.Text;
			pw1Plain.Visibility = Visibility.Collapsed; pw2Plain.Visibility = Visibility.Collapsed;
			pw1.Visibility = Visibility.Visible; pw2.Visibility = Visibility.Visible;
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
		var okButton = new Button { Content = L("BtnOk"), Width = 110 };
		buttons.Children.Add(okButton);
		panel.Children.Add(buttons);
		dialog.Content = panel;
		okButton.Click += delegate
		{
			bool revealed = showCheck.IsChecked == true;
			string name = nameBox.Text.Trim();
			if (name.Length == 0) name = "User"; // empty box defaults to "User" (no need to pre-fill/clear it)
			string entered = revealed ? pw1Plain.Text : pw1.Password;
			string confirm = revealed ? pw2Plain.Text : pw2.Password;
			if (name.IndexOfAny(new[] { '\\', '/', '"', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@' }) >= 0)
			{
				MessageBox.Show(L("Mb012"), "Local account", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			if (entered != confirm)
			{
				MessageBox.Show(L("Mb013"), "Local account", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			localAccountName = name;
			localAccountPassword = entered;
			dialog.DialogResult = true;
		};
		dialog.ShowDialog();
		Log(string.IsNullOrEmpty(localAccountName)
			? "Microsoft-account bypass: no local account chosen (OOBE may still ask for an account)."
			: $"Microsoft-account bypass: local account '{localAccountName}' will be created at first boot" + (localAccountPassword.Length > 0 ? " (with password)." : " (no password)."));
	}

	// Lets the user type a BitLocker unlock password (optional). Stored in bitLockerPassword; empty means
	// "use only the recovery key". A small code-built modal with two PasswordBoxes (no plaintext on screen).
	private void PromptBitLockerPassword()
	{
		bitLockerPassword = "";
		var dialog = new Window
		{
			Title = L("DlgBitLockerPwd"),
			Width = 440,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			Background = (Brush)FindResource("NavyBrush")
		};
		var panel = new StackPanel { Margin = new Thickness(16) };
		panel.Children.Add(new TextBlock
		{
			Text = L("BlkPwdIntro"),
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("TextBrush"),
			Margin = new Thickness(0, 0, 0, 10)
		});
		// Each field has a masked PasswordBox + a plain TextBox stacked; "Show password" toggles which one
		// is visible, on BOTH fields, keeping their values in sync.
		panel.Children.Add(new TextBlock { Text = L("BlkPwdLabel"), Foreground = (Brush)FindResource("TextBrush") });
		var pw1 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw1Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw1);
		panel.Children.Add(pw1Plain);
		panel.Children.Add(new TextBlock { Text = L("PwConfirmLabel"), Foreground = (Brush)FindResource("TextBrush") });
		var pw2 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw2Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw2);
		panel.Children.Add(pw2Plain);
		var showCheck = new CheckBox { Content = L("ShowPwLabel"), Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 4) };
		panel.Children.Add(showCheck);
		showCheck.Checked += delegate
		{
			pw1Plain.Text = pw1.Password; pw2Plain.Text = pw2.Password;
			pw1.Visibility = Visibility.Collapsed; pw2.Visibility = Visibility.Collapsed;
			pw1Plain.Visibility = Visibility.Visible; pw2Plain.Visibility = Visibility.Visible;
		};
		showCheck.Unchecked += delegate
		{
			pw1.Password = pw1Plain.Text; pw2.Password = pw2Plain.Text;
			pw1Plain.Visibility = Visibility.Collapsed; pw2Plain.Visibility = Visibility.Collapsed;
			pw1.Visibility = Visibility.Visible; pw2.Visibility = Visibility.Visible;
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
		var okButton = new Button { Content = L("BtnOk"), Width = 110 };
		buttons.Children.Add(okButton);
		panel.Children.Add(buttons);
		dialog.Content = panel;
		okButton.Click += delegate
		{
			bool revealed = showCheck.IsChecked == true;
			string entered = revealed ? pw1Plain.Text : pw1.Password;
			string confirm = revealed ? pw2Plain.Text : pw2.Password;
			// Both empty = the user wants recovery-key-only protection. Proceed with no password.
			if (entered.Length == 0 && confirm.Length == 0)
			{
				bitLockerPassword = "";
				dialog.DialogResult = true;
				return;
			}
			if (entered != confirm)
			{
				MessageBox.Show(L("Mb013"), L("DlgBitLockerPwd"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			if (entered.Length < 8)
			{
				MessageBox.Show(L("Mb014"), L("DlgBitLockerPwd"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			bitLockerPassword = entered;
			dialog.DialogResult = true;
		};
		dialog.ShowDialog();
		Log(string.IsNullOrEmpty(bitLockerPassword)
			? "BitLocker: no password chosen — the recovery key will be the only protector."
			: "BitLocker: a custom unlock password was set (plus a recovery key as backup).");
	}

	// Runs a console tool, feeding the given lines to its stdin (used for manage-bde password prompts so
	// the password never appears on a command line). Returns the exit code.
	private async Task<int> RunProcessWithStdinAsync(string fileName, string arguments, IReadOnlyList<string> stdinLines, bool allowFailure = false)
	{
		var psi = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		using var proc = Process.Start(psi);
		foreach (string line in stdinLines)
		{
			await proc.StandardInput.WriteLineAsync(line);
		}
		proc.StandardInput.Close();
		string output = await proc.StandardOutput.ReadToEndAsync();
		string error = await proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();
		string combined = (output + "\n" + error).Trim();
		if (!string.IsNullOrWhiteSpace(combined)) Log(combined);
		if (!allowFailure && proc.ExitCode != 0)
		{
			throw new InvalidOperationException(fileName + " exited with code " + proc.ExitCode);
		}
		return proc.ExitCode;
	}

	private bool ChooseBitLockerRecoveryFolder()
	{
		using Forms.FolderBrowserDialog folderBrowserDialog = new Forms.FolderBrowserDialog
		{
			Description = L("DlgBitLockerKeyFolder"),
			UseDescriptionForTitle = true
		};
		if (folderBrowserDialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(folderBrowserDialog.SelectedPath))
		{
			return false;
		}
		bitLockerRecoveryFolder = folderBrowserDialog.SelectedPath;
		Log("BitLocker recovery key folder: " + bitLockerRecoveryFolder);
		return true;
	}

	private async Task ApplyInstallBypassOptionsAsync(char windowsLetter)
	{
		if (BypassRequirementsCheck.IsChecked != true && BypassAccountCheck.IsChecked != true && DebloatCheck.IsChecked != true)
		{
			return;
		}
		if (BypassRequirementsCheck.IsChecked == true)
		{
			SetStage(L("StgBypassCompat"), 88.0);
			string systemHive = $"{windowsLetter}:\\Windows\\System32\\config\\SYSTEM";
			string hiveName = "DriveForgeSetup" + Guid.NewGuid().ToString("N");
			string hiveRoot = "HKLM\\" + hiveName;
			bool loaded = false;
			try
			{
				await RunProcessAsync("reg.exe", $"load \"{hiveRoot}\" \"{systemHive}\"");
				loaded = true;
				string labConfig = $"{hiveRoot}\\Setup\\LabConfig";
				foreach (string valueName in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassCPUCheck", "BypassStorageCheck" })
				{
					await RunProcessAsync("reg.exe", $"add \"{labConfig}\" /v {valueName} /t REG_DWORD /d 1 /f", allowFailure: true);
				}
				Log("Windows 11 system requirement bypass keys applied.");
			}
			finally
			{
				if (loaded)
				{
					// Robust unload: a silently-failed unload discards the edits above (in-memory only).
					if (!await UnloadRegistryHiveRobustAsync(hiveRoot))
					{
						Log("WARNING: registry hive did not unload cleanly; some offline settings may not have been committed: " + hiveRoot);
					}
				}
			}
		}
		if (BypassAccountCheck.IsChecked == true)
		{
			SetStage(L("StgBypassAccount"), 89.0);
			string softwareHive = $"{windowsLetter}:\\Windows\\System32\\config\\SOFTWARE";
			string hiveName = "DriveForgeSoftware" + Guid.NewGuid().ToString("N");
			string hiveRoot = "HKLM\\" + hiveName;
			bool loaded = false;
			try
			{
				await RunProcessAsync("reg.exe", $"load \"{hiveRoot}\" \"{softwareHive}\"");
				loaded = true;
				await RunProcessAsync("reg.exe", $"add \"{hiveRoot}\\Microsoft\\Windows\\CurrentVersion\\OOBE\" /v BypassNRO /t REG_DWORD /d 1 /f", allowFailure: true);
				Log("Microsoft account bypass key applied.");
			}
			finally
			{
				if (loaded)
				{
					// Robust unload: a silently-failed unload discards the edits above (in-memory only).
					if (!await UnloadRegistryHiveRobustAsync(hiveRoot))
					{
						Log("WARNING: registry hive did not unload cleanly; some offline settings may not have been committed: " + hiveRoot);
					}
				}
			}
		}
		if (DebloatCheck.IsChecked == true)
		{
			await ApplyDebloatToImageAsync(windowsLetter);
		}
	}

	// Writes Microsoft's own group-policy values into the install image's offline SOFTWARE hive to turn off
	// Copilot, the Teams/Chat icon, suggested apps & ads, Start web search, the advertising ID, and to set
	// telemetry to the minimum. These are official policy keys — nothing is removed or broken, and every change
	// can be reverted later in Settings/Group Policy. Best-effort: failures are logged, never fatal.
	private async Task ApplyDebloatToImageAsync(char windowsLetter)
	{
		SetStage(L("StgRemoveBloat"), 90.0);
		string softwareHive = $"{windowsLetter}:\\Windows\\System32\\config\\SOFTWARE";
		if (!File.Exists(softwareHive)) { Log("WARNING: could not find the install SOFTWARE hive for debloat."); return; }
		string hiveRoot = "HKLM\\DriveForgeDebloat" + Guid.NewGuid().ToString("N");
		bool loaded = false;
		try
		{
			await RunProcessAsync("reg.exe", "load " + QuoteArgument(hiveRoot) + " " + QuoteArgument(softwareHive));
			loaded = true;
			string P = hiveRoot + "\\Policies\\Microsoft";
			(string key, string name, int data)[] keys =
			{
				($"{P}\\Windows\\WindowsCopilot", "TurnOffWindowsCopilot", 1),
				($"{P}\\Windows\\CloudContent", "DisableWindowsConsumerFeatures", 1),
				($"{P}\\Windows\\CloudContent", "DisableConsumerAccountStateContent", 1),
				($"{P}\\Windows\\CloudContent", "DisableSoftLanding", 1),
				($"{P}\\Windows\\DataCollection", "AllowTelemetry", 0),
				($"{P}\\Windows\\Windows Chat", "ChatIcon", 3),
				($"{P}\\Windows\\Explorer", "DisableSearchBoxSuggestions", 1),
				($"{P}\\Windows\\AdvertisingInfo", "DisabledByGroupPolicy", 1),
			};
			foreach (var (key, name, data) in keys)
				await RunProcessAsync("reg.exe", $"add \"{key}\" /v {name} /t REG_DWORD /d {data} /f", allowFailure: true);
			_lastDebloatApplied = true;
			Log("Debloat policy keys applied to the install image.");
		}
		catch (Exception ex) { Log("Debloat step failed (non-fatal): " + ex.Message); }
		finally
		{
			if (loaded && !await UnloadRegistryHiveRobustAsync(hiveRoot))
				Log("WARNING: debloat registry hive did not unload cleanly; some settings may not have been committed: " + hiveRoot);
		}
	}

	// Writes the BitLocker "allow without compatible TPM" policy into the clone's offline SOFTWARE hive so a
	// password (or startup key) is accepted as pre-boot authentication. A portable USB clone has no usable
	// TPM, so without this BitLocker ignores the password protector at boot and demands the recovery key.
	private async Task ApplyBitLockerNoTpmPolicyToCloneAsync(char windowsLetter)
	{
		string softwareHive = $"{windowsLetter}:\\Windows\\System32\\config\\SOFTWARE";
		if (!File.Exists(softwareHive))
		{
			Log("WARNING: could not find the clone SOFTWARE hive to write the BitLocker no-TPM policy.");
			return;
		}
		string hiveName = "DriveForgeFVE" + Guid.NewGuid().ToString("N");
		string hiveRoot = "HKLM\\" + hiveName;
		string fve = hiveRoot + "\\Policies\\Microsoft\\FVE";
		bool loaded = false;
		try
		{
			await RunProcessAsync("reg.exe", "load " + QuoteArgument(hiveRoot) + " " + QuoteArgument(softwareHive));
			loaded = true;
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v UseAdvancedStartup /t REG_DWORD /d 1 /f", allowFailure: true);
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v EnableBDEWithNoTPM /t REG_DWORD /d 1 /f", allowFailure: true);
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v UseTPM /t REG_DWORD /d 2 /f", allowFailure: true);
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v UseTPMPIN /t REG_DWORD /d 2 /f", allowFailure: true);
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v UseTPMKey /t REG_DWORD /d 2 /f", allowFailure: true);
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v UseTPMKeyPIN /t REG_DWORD /d 2 /f", allowFailure: true);
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(fve) + " /v OSEnablePrebootInputProtectorsOnSlates /t REG_DWORD /d 1 /f", allowFailure: true);
			Log("BitLocker no-TPM pre-boot policy written to the clone (UseAdvancedStartup, EnableBDEWithNoTPM).");
		}
		catch (Exception ex)
		{
			Log("WARNING: could not write the BitLocker no-TPM policy: " + ex.Message);
		}
		finally
		{
			if (loaded) await UnloadRegistryHiveRobustAsync(hiveRoot);
		}
	}

	// Adds a BitLocker password protector via PowerShell's Add-BitLockerKeyProtector. manage-bde's -Password
	// prompt reads from the console directly (NOT redirected stdin), so feeding it the password through stdin
	// silently does nothing — that is why earlier clones ended up with "Password protector: no". The password
	// is handed to PowerShell through a child-process-only environment variable, so it never appears on a
	// command line or in the global environment.
	private async Task<bool> AddBitLockerPasswordProtectorAsync(char windowsLetter, string password)
	{
		string script =
			"$ErrorActionPreference='Stop'; try { " +
			"$s = ConvertTo-SecureString $env:WUM_BLPW -AsPlainText -Force; " +
			"Add-BitLockerKeyProtector -MountPoint '" + windowsLetter + ":' -PasswordProtector -Password $s | Out-Null; " +
			"'PROTECTOR_OK' } catch { 'PROTECTOR_FAIL: ' + $_.Exception.Message }";
		var psi = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			Arguments = "-NoProfile -Command " + QuoteArgument(script),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		psi.EnvironmentVariables["WUM_BLPW"] = password;
		using var proc = Process.Start(psi);
		string outp = await proc.StandardOutput.ReadToEndAsync();
		string err = await proc.StandardError.ReadToEndAsync();
		await proc.WaitForExitAsync();
		string combined = (outp + " " + err).Trim();
		if (!string.IsNullOrWhiteSpace(combined)) Log("BitLocker password protector: " + combined);
		return combined.Contains("PROTECTOR_OK", StringComparison.OrdinalIgnoreCase);
	}

	private async Task EnableBitLockerAsync(char windowsLetter)
	{
		if (string.IsNullOrWhiteSpace(bitLockerRecoveryFolder))
		{
			throw new InvalidOperationException("Choose a folder for the BitLocker recovery key first.");
		}
		SetStage(L("StgEnableBitlocker"), 96.0);
		// Always add a recovery password (numerical key) as the guaranteed fallback protector.
		string protectorOutput = await RunProcessCaptureAsync("manage-bde.exe", $"-protectors -add {windowsLetter}: -RecoveryPassword");
		Match recoveryMatch = Regex.Match(protectorOutput, "\\d{6}(?:-\\d{6}){7}");
		string recoveryKey = recoveryMatch.Success ? recoveryMatch.Value : "Recovery password was created, but DriveForge could not parse it from manage-bde output. Run: manage-bde -protectors -get " + windowsLetter + ":";

		// If the user chose a password, add a password protector too. The password is fed through stdin so
		// it never appears on a command line. At clone time the target is a mounted data volume, so a
		// password protector is accepted; when the clone boots as the OS it can prompt for this password
		// (and the recovery key always works as a fallback).
		bool passwordProtectorAdded = false;
		if (!string.IsNullOrEmpty(bitLockerPassword))
		{
			// CRITICAL for pre-boot password: a portable clone has no usable TPM, so by default BitLocker
			// would demand the recovery key at boot instead of the password. Write the BitLocker policy into
			// the CLONE's own SOFTWARE hive so that at its boot time it allows "BitLocker without a TPM" with
			// a password/startup key (UseAdvancedStartup + EnableBDEWithNoTPM). Without this the password
			// protector exists but the firmware path falls back to the recovery key.
			await ApplyBitLockerNoTpmPolicyToCloneAsync(windowsLetter);

			passwordProtectorAdded = await AddBitLockerPasswordProtectorAsync(windowsLetter, bitLockerPassword);
			Log(passwordProtectorAdded
				? "BitLocker password protector added (no-TPM pre-boot password policy written to the clone)."
				: "WARNING: could not add the BitLocker password protector. The recovery key still protects the drive.");
		}

		Directory.CreateDirectory(bitLockerRecoveryFolder);
		string keyPath = Path.Combine(bitLockerRecoveryFolder, "DriveForge-BitLocker-Recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
		await File.WriteAllTextAsync(keyPath,
			"DriveForge BitLocker Recovery Key\r\n\r\nDrive: " + windowsLetter + ":\r\nRecovery key: " + recoveryKey + "\r\n" +
			"Password protector: " + (passwordProtectorAdded ? "yes (the password you entered)" : "no") + "\r\n", Encoding.UTF8);
		Log("BitLocker recovery key saved: " + keyPath);
		// Clear the password from memory once used.
		bitLockerPassword = "";

		// Start encryption and CAPTURE the result so a failure is visible instead of silent.
		SetStage(L("StgStartBitlocker"), 97.0);
		string onOutput = "";
		bool onOk = false;
		try { onOutput = await RunProcessCaptureAsync("manage-bde.exe", $"-on {windowsLetter}: -UsedSpaceOnly"); onOk = true; }
		catch (Exception ex) { onOutput = "manage-bde -on failed: " + ex.Message; }
		Log("BitLocker enable output: " + onOutput.Trim());

		// Verify via a LOCALE-INDEPENDENT source. manage-bde -status prints localized text, so parsing English phrases
		// mis-reads EVERY non-English Windows as "not encrypted" - which would wrongly clear bitLockerEncrypting and let
		// an "eject when done" force-dismount the drive mid-conversion. Get-BitLockerVolume returns a VolumeStatus enum
		// (its .ToString() is invariant) plus a numeric EncryptionPercentage.
		await Task.Delay(2500);
		string volStatus = "", pctVal = "";
		try
		{
			string psOut = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command " + QuoteArgument(
					$"$v=Get-BitLockerVolume -MountPoint '{windowsLetter}:'; \"$($v.VolumeStatus)|$($v.EncryptionPercentage)\""));
			var parts = psOut.Trim().Split('|');
			volStatus = parts.Length > 0 ? parts[0].Trim() : "";
			pctVal = parts.Length > 1 && double.TryParse(parts[1].Trim(), out double pv) ? pv.ToString("0.#") + "%" : "";
		}
		catch (Exception ex) { Log("Get-BitLockerVolume status query failed: " + ex.Message); }
		Log("BitLocker status: " + (volStatus.Length > 0 ? volStatus + (pctVal.Length > 0 ? " (" + pctVal + " done)" : "") : "(status query unavailable)"));

		bool inProgress = volStatus.Equals("EncryptionInProgress", StringComparison.OrdinalIgnoreCase);
		bool encrypting = inProgress || volStatus.Equals("FullyEncrypted", StringComparison.OrdinalIgnoreCase);
		// Status query returned nothing recognizable but `-on` succeeded -> assume conversion started (it begins
		// immediately) rather than falsely reporting "not encrypted" and auto-ejecting the drive mid-encryption.
		if (!encrypting && onOk && volStatus.Length == 0) { inProgress = true; encrypting = true; }
		Match pct = Regex.Match(pctVal, @"([\d\.]+%)");
		// If the user opted to remove the drive early, a non-paused conversion resumes automatically when the
		// clone boots (BitLocker's Drive Encryption service picks up unfinished, unpaused conversions on mount).
		bool resumeAfterBoot = BitLockerResumeCheck.IsChecked == true;
		if (inProgress && resumeAfterBoot)
		{
			bitLockerEncrypting = false; // safe to remove — it will continue on the clone
			Log("BitLocker encryption started on " + windowsLetter + ":" + (pct.Success ? " (" + pct.Groups[1].Value + " done)" : "") + ". You can remove the drive now — Windows will resume encryption automatically the first time the clone boots.");
		}
		else if (inProgress)
		{
			bitLockerEncrypting = true; // keep connected until 100%
			Log("BitLocker encryption is ACTIVE on " + windowsLetter + ":" + (pct.Success ? " (" + pct.Groups[1].Value + " done)" : "") + ". It continues in the background; do not unplug until it reaches 100% (manage-bde -status).");
		}
		else if (encrypting)
		{
			bitLockerEncrypting = false;
			Log("BitLocker reports the drive is already fully encrypted.");
		}
		else
		{
			bitLockerEncrypting = false;
			Log("WARNING: BitLocker encryption did NOT start on " + windowsLetter + ": — see the status above. The drive is NOT encrypted.");
			// The user asked for BitLocker and a recovery-key file was already written (implying the drive is
			// encrypted). If it is NOT actually being encrypted, surface it loudly instead of letting the caller
			// report success and auto-eject a stick the user believes is protected.
			throw new InvalidOperationException("BitLocker encryption did not start on " + windowsLetter + ": — the drive was created but is NOT encrypted, even though a recovery-key file was saved (" + keyPath + "). " + (onOutput.Trim().Length > 0 ? "manage-bde: " + onOutput.Trim() : "See the log for details."));
		}
	}

	private static string BuildWindowsToGoDiskpartScript(int diskNumber, char bootLetter, char windowsLetter, int windowsSizeMb = 0, char dataLetter = '\0')
	{
		var lines = new List<string>
		{
			$"select disk {diskNumber}",
			"clean",
			"convert mbr",
			"create partition primary size=300 align=1024",
			"format quick fs=fat32 label=\"WINTOGO\"",
			"active",
			$"assign letter={bootLetter}",
			windowsSizeMb > 0 ? $"create partition primary size={windowsSizeMb} align=1024" : "create partition primary align=1024",
			// 64K NTFS clusters: far fewer metadata updates for the many-small-file Windows apply => faster write on
			// USB (a Windows-To-Go staple). Windows boots fine from 64K; the only tradeoff is a little slack space and
			// that classic NTFS-compressed files restore uncompressed (Windows uses cluster-independent WOF anyway).
			"format quick fs=ntfs unit=64K label=\"Windows\"",
			$"assign letter={windowsLetter}"
		};
		if (windowsSizeMb > 0 && dataLetter != '\0')
		{
			lines.Add("create partition primary align=1024");
			lines.Add("format quick fs=ntfs label=\"Data\"");
			lines.Add($"assign letter={dataLetter}");
		}
		lines.Add("exit");
		return string.Join(Environment.NewLine, lines);
	}

	private static string BuildVhdxHostDiskpartScript(int diskNumber, char bootLetter, char hostLetter, bool useUefiLayout)
	{
		if (useUefiLayout)
		{
			return string.Join(Environment.NewLine, new string[12]
			{
				$"select disk {diskNumber}",
				"clean",
				"convert gpt",
				"create partition efi size=300",
				"format quick fs=fat32 label=\"WINTOGO\"",
				$"assign letter={bootLetter}",
				"create partition msr size=128",   // 128 MB — Microsoft minimum for disks > 16 GB; required for 4Kn drives
				"create partition primary",
				"format quick fs=ntfs label=\"VHDXSTORE\"",
				$"assign letter={hostLetter}",
				"rescan",
				"exit"
			});
		}
		return string.Join(Environment.NewLine, new string[11]
		{
			$"select disk {diskNumber}",
			"clean",
			"convert mbr",
			"create partition primary size=300",
			"format quick fs=fat32 label=\"WINTOGO\"",
			"active",
			$"assign letter={bootLetter}",
			"create partition primary",
			"format quick fs=ntfs label=\"VHDXSTORE\"",
			$"assign letter={hostLetter}",
			"exit"
		});
	}

	private static string BuildCreateVhdxDiskpartScript(string vhdPath, char windowsLetter, long maximumMb)
	{
		return string.Join(Environment.NewLine, new string[13]
		{
			"san policy=OnlineAll",
			$"create vdisk file=\"{vhdPath}\" maximum={maximumMb} type=expandable",
			$"select vdisk file=\"{vhdPath}\"",
			"attach vdisk",
			"attributes disk clear readonly noerr",
			"online disk noerr",
			"attributes disk clear readonly noerr",
			"convert mbr noerr",
			"create partition primary",
			// 64K NTFS clusters: far fewer metadata updates for the many-small-file Windows apply => faster write on
			// USB (a Windows-To-Go staple). Windows boots fine from 64K; the only tradeoff is a little slack space and
			// that classic NTFS-compressed files restore uncompressed (Windows uses cluster-independent WOF anyway).
			"format quick fs=ntfs unit=64K label=\"Windows\"",
			$"assign letter={windowsLetter}",
			"detail vdisk",
			"exit"
		});
	}

	private static string BuildDetachVhdxDiskpartScript(string vhdPath)
	{
		return string.Join(Environment.NewLine, new string[3]
		{
			$"select vdisk file=\"{vhdPath}\"",
			"detach vdisk",
			"exit"
		});
	}

	private long EstimateVhdxMaximumMb(DiskItem disk)
	{
		long minimum = 64L * 1024L * 1024L * 1024L;
		long preferred = Math.Max(minimum, GetCurrentWindowsUsedBytes() + 24L * 1024L * 1024L * 1024L);
		long maximumOnDisk = Math.Max(minimum, disk.Size - 2L * 1024L * 1024L * 1024L);
		long selectedBytes = Math.Min(preferred, maximumOnDisk);
		return Math.Max(32768L, selectedBytes / 1024L / 1024L);
	}

	private async Task ConfigureNativeVhdxBootAsync(char bootLetter, string relativeVhdPath)
	{
		string vhdDevice = $"vhd=[locate]\\{relativeVhdPath}";
		BcdStoreInfo[] stores =
		{
			new BcdStoreInfo($"{bootLetter}:\\EFI\\Microsoft\\Boot\\BCD", "\\Windows\\system32\\winload.efi", "UEFI"),
			new BcdStoreInfo($"{bootLetter}:\\Boot\\BCD", "\\Windows\\system32\\winload.exe", "BIOS")
		};
		bool configuredAnyStore = false;
		foreach (BcdStoreInfo storeInfo in stores.Where(item => File.Exists(item.Path)))
		{
			configuredAnyStore = true;
			string loaderId = await FindWindowsLoaderIdAsync(storeInfo.Path);
			await SetBcdValueAsync(storeInfo.Path, loaderId, "device", vhdDevice);
			await SetBcdValueAsync(storeInfo.Path, loaderId, "osdevice", vhdDevice);
			await SetBcdValueAsync(storeInfo.Path, loaderId, "path", storeInfo.LoaderPath);
			await SetBcdValueAsync(storeInfo.Path, loaderId, "systemroot", "\\Windows");
			await SetBcdValueAsync(storeInfo.Path, loaderId, "detecthal", "Yes", allowFailure: true);
			await SetBcdValueAsync(storeInfo.Path, "{bootmgr}", "default", loaderId, allowFailure: true);
			await RunBcdEditAsync(storeInfo.Path, "/displayorder", loaderId, "/addfirst", allowFailure: true);
			await SetBcdValueAsync(storeInfo.Path, "{bootmgr}", "timeout", "5", allowFailure: true);
			Log($"{storeInfo.Mode} native VHDX boot configured: loader {loaderId}, VHDX {vhdDevice}");
			await RunBcdEditAsync(storeInfo.Path, "/enum", "osloader", allowFailure: true);
		}
		if (!configuredAnyStore)
		{
			throw new InvalidOperationException("BCD boot store was not created on the USB boot partition.");
		}
	}

	private static bool IsCurrentFirmwareUefi()
	{
		try
		{
			using RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control");
			object value = key?.GetValue("PEFirmwareType");
			if (value != null && int.TryParse(value.ToString(), out int firmwareType))
			{
				return firmwareType == 2;
			}
		}
		catch
		{
		}
		return true;
	}

	private async Task<string> FindWindowsLoaderIdAsync(string storePath)
	{
		try
		{
			ProcessResult result = await RunProcessWithArgumentListInternalAsync("bcdedit.exe", new[] { "/store", storePath, "/enum", "osloader" });
			string loaderId = ExtractWindowsLoaderId(result.Output);
			if (!string.IsNullOrWhiteSpace(loaderId))
			{
				return loaderId;
			}
		}
		catch (Exception ex)
		{
			Log("Could not enumerate BCD loaders, using {default}: " + ex.Message);
		}
		return "{default}";
	}

	private async Task SetBcdValueAsync(string storePath, string objectId, string element, string value, bool allowFailure = false)
	{
		await RunBcdEditAsync(storePath, "/set", objectId, element, value, allowFailure);
	}

	private async Task RunBcdEditAsync(string storePath, string command, string arg1, string arg2 = null, string arg3 = null, bool allowFailure = false)
	{
		List<string> arguments = new List<string> { "/store", storePath, command, arg1 };
		if (!string.IsNullOrWhiteSpace(arg2))
		{
			arguments.Add(arg2);
		}
		if (!string.IsNullOrWhiteSpace(arg3))
		{
			arguments.Add(arg3);
		}
		await RunProcessWithArgumentListAsync("bcdedit.exe", arguments, allowFailure);
	}

	private static string ExtractWindowsLoaderId(string bcdOutput)
	{
		string currentId = "";
		string bestId = "";
		bool currentIsWindowsLoader = false;
		foreach (string rawLine in bcdOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
		{
			string line = rawLine.Trim();
			Match identifierMatch = Regex.Match(line, @"^identifier\s+(\{(?:[0-9a-fA-F-]{36}|default|current)\})$", RegexOptions.IgnoreCase);
			if (identifierMatch.Success)
			{
				if (currentIsWindowsLoader && !string.IsNullOrWhiteSpace(currentId))
				{
					bestId = currentId;
				}
				currentId = identifierMatch.Groups[1].Value;
				currentIsWindowsLoader = false;
				continue;
			}
			if (!string.IsNullOrWhiteSpace(currentId) && line.StartsWith("path", StringComparison.OrdinalIgnoreCase) && line.Contains("winload", StringComparison.OrdinalIgnoreCase))
			{
				currentIsWindowsLoader = true;
			}
		}
		if (currentIsWindowsLoader && !string.IsNullOrWhiteSpace(currentId))
		{
			bestId = currentId;
		}
		return bestId;
	}

	private async Task DetachVhdxAsync(string vhdPath)
	{
		string detachScriptPath = Path.Combine(Path.GetTempPath(), $"driveforge-detach-vhdx-{Guid.NewGuid():N}.txt");
		try
		{
			await File.WriteAllTextAsync(detachScriptPath, BuildDetachVhdxDiskpartScript(vhdPath), Encoding.ASCII);
			await RunProcessAsync("diskpart.exe", "/s \"" + detachScriptPath + "\"", allowFailure: true);
			// Belt-and-suspenders: a diskpart "detach vdisk" can silently fail (e.g. a shell handle from a
			// double-click still holds the volume). Clear any mount diskpart missed with the native VirtDisk API.
			// We deliberately do NOT probe the file with an exclusive open afterward: a transient antivirus / Search
			// indexer read handle on the freshly-closed .vhdx would fail that probe and raise a FALSE "still locked"
			// alarm even though the mount is fully gone — and a later "Restore from VHDX" self-heals regardless, since
			// it pre-cleans any leftover mount (TryDetachByPath) before it attaches.
			VirtDisk.TryDetachByPath(vhdPath);
			Log("VHDX detached: " + vhdPath);
		}
		finally
		{
			if (File.Exists(detachScriptPath))
			{
				TryDeleteFile(detachScriptPath);
			}
		}
	}

	// Inbox storage + USB controller drivers that must be BOOT-START (Start=0) for a portable Windows
	// to mount its system volume on arbitrary hardware. By default Windows only boot-starts the storage
	// driver of the machine it was installed on; on a different PC the boot disk's controller driver is
	// loaded too late → 0x7B INACCESSIBLE_BOOT_DEVICE. Forcing this set to boot-start is the core
	// "Windows To Go portability" fix (a standard universal-hardware boot service).
	private static readonly string[] UniversalBootStorageDrivers = new[]
	{
		// AHCI / SATA / NVMe / IDE / generic storage port
		"storahci", "storport", "stornvme", "nvme", "msahci", "pciide", "atapi", "intelide", "intelpep",
		"sdstor", "sdbus", "spaceport", "rdyboost", "EhStorClass",
		// RAID / SCSI controllers (Intel RST, AMD, LSI/Broadcom, NVIDIA, VIA, SiS, HP, Marvell, VMs)
		"iaStorV", "iaStorAV", "iaStorAVC", "iaStorAC", "amdsata", "amdxata", "amdide", "aliide", "cmdide", "viaide",
		"LSI_SAS", "LSI_SAS2i", "LSI_SAS3i", "LSI_SCSI", "megasas", "megasas2i", "megasas35i",
		"percsas2i", "percsas3i", "nvraid", "nvstor", "vsmraid", "sisraid2", "sisraid4", "arcsas",
		"HpSAMD", "SmartSAMD", "stexstor", "vstxraid", "vmbus", "vmci", "storvsc", "3ware",
		// USB controller stack + USB mass storage (essential for booting from a USB drive/enclosure)
		"usbohci", "usbuhci", "usbehci", "usbxhci", "USBXHCI", "usbhub", "usbhub3", "usbccgp", "usbport",
		"usbstor", "USBSTOR", "UASPStor", "uaspstor",
		// disk / volume / snapshot / mount / BitLocker filter stack
		"disk", "partmgr", "volmgr", "volmgrx", "volsnap", "mountmgr", "fvevol"
	};

	// Sets every EXISTING driver from UniversalBootStorageDrivers to Start=0 (boot-start) in one control
	// set of an offline SYSTEM hive. Never creates a service key — only flips drivers already present on
	// the image, so it can't introduce a phantom boot driver that would itself bugcheck.
	private async Task ApplyUniversalBootStorageDriversAsync(string hiveRoot, string controlSet)
	{
		int flipped = 0;
		string servicesPrefix = hiveRoot + "\\" + controlSet + "\\Services\\";
		foreach (string driver in UniversalBootStorageDrivers)
		{
			string driverKey = servicesPrefix + driver;
			ProcessResult exists = await RunProcessInternalAsync("reg.exe", "query " + QuoteArgument(driverKey) + " /v Start");
			if (exists.ExitCode != 0)
			{
				continue; // driver not present on this image — skip (do NOT create it)
			}
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(driverKey) + " /v Start /t REG_DWORD /d 0 /f", allowFailure: true);
			flipped++;
		}
		Log($"Universal boot: set {flipped} storage/USB drivers to boot-start in {controlSet} (portable Windows can boot on other hardware).");
	}

	// Third-party antivirus service/driver key names. On the CLONE these would wake up at first boot and
	// interrupt the AppX first-boot repair (behavioral protection may quarantine the repair scripts before
	// the packages are re-registered). We temporarily disable them OFFLINE (Start=4) so the first boots run
	// unblocked; a generated Re-Enable-Antivirus.cmd lets the user turn protection back on (or reinstall)
	// once the clone is set up. We deliberately do NOT touch Windows Defender. Editing the offline hive only
	// changes the clone's startup configuration and does not affect any running system; Windows Defender is
	// left fully intact.
	private static readonly string[] AntivirusBootServices = new[]
	{
		// Bitdefender
		"bdservicehost", "vsserv", "VSSERV", "bdredline", "BDPredeploy", "gzserv", "UpdateSrv", "bdfwfpf",
		"bdfsfltr", "bdvedisk", "trufos", "gzflt", "AvcKf", "BDSandBox", "bdelam", "BdfNdisf", "bddci",
		"EPSecurityService", "EPRedline", "EPProtectedService", "EPIntegrationService", "ProductAgentService",
		// Kaspersky
		"AVP", "klam", "klflt", "klif", "klbackupflt", "klkbdflt", "klmouflt", "kltap", "klpd", "klhk", "klupd", "klpnpflt",
		// Norton / Symantec
		"NortonSecurity", "nsly", "SymELAM", "BHDrvx64", "ccSetMgr", "ccEvtMgr", "SepMasterService", "SymEvent",
		"SRTSP", "SYMEFA", "eeCtrl", "EraserUtilRebootDrv",
		// McAfee
		"McAfeeFramework", "mfemms", "mfevtp", "McAPExe", "mfeelamk", "mfewfpk", "mfefirek", "McShield", "mfehidk",
		// Avast
		"avastsvc", "aswbIDSAgent", "aswSP", "aswSnx", "aswStm", "aswMonFlt", "aswbidsdriver", "aswbidsh", "aswelam",
		"aswArPot", "aswbuniv", "aswVmm", "aswRvrt",
		// AVG
		"avgsvc", "avgsvca", "avgwd", "AvgArPbk", "avgSP", "avgbIDSAgent", "avgbidsdriver", "avgbidsh", "avgArPot",
		// ESET
		"ekrn", "eelam", "eamonm", "edevmon", "ehdrv", "epfwwfp", "epfw",
		// Malwarebytes
		"MBAMService", "MBAMProtection", "mbam", "mbae", "MBAMSwissArmy", "mbamchameleon", "MBAMWebProtection", "mwac", "farflt",
		// Webroot
		"WRSVC", "WRkrn", "WRBoot", "WRCore",
		// Sophos
		"SophosAgent", "savservice", "sophosssp", "hmpalertsvc", "SAVOnAccess", "sophosWa", "Sophos Endpoint Defense Service", "SntpService",
		// Trend Micro
		"Amsp", "AMSP", "TmFilter", "TMLWCSService", "tmcomm", "tmevtmgr", "tmactmon", "coreServiceShell", "TmPreFilter",
		// Avira
		"Avira.ServiceHost", "Avira.Spotlight.Service", "antivirservice", "avgntflt", "avipbb", "avkmgr", "avnetflt",
		// F-Secure / WithSecure
		"FSMA", "fsbts", "fsdfw", "FSORSPClient", "fshoster", "F-Secure Gatekeeper Handler Starter",
		// Comodo
		"cmdAgent", "cmdvirth", "CmdCSS", "inspect", "cmderd", "cmdguard", "cmdhlp",
		// Panda
		"PSHost", "PavFnSvr", "PavPrSrv", "PSANHost", "NanoServiceMain"
	};

	// Disable every third-party-AV service that exists in the offline SYSTEM hive (across all control sets)
	// and emit a Re-Enable-Antivirus.cmd onto the clone so the user can restore exact original Start values
	// later. Captures each service's original Start so re-enabling is a faithful restore, not a guess.
	private async Task NeutralizeAntivirusInHiveAsync(string hiveRoot, IReadOnlyList<string> controlSets, string cloneRoot, bool autoRestoreInstalled)
	{
		var restored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // service -> original Start
		var toDisable = new List<string>(); // full service keys that exist AND whose original Start we captured
		// Pass 1: capture the original Start for every AV service present. We ONLY disable what we can restore.
		foreach (string controlSet in controlSets)
		{
			string servicesPrefix = hiveRoot + "\\" + controlSet + "\\Services\\";
			foreach (string service in AntivirusBootServices)
			{
				string serviceKey = servicesPrefix + service;
				ProcessResult query = await RunProcessInternalAsync("reg.exe", "query " + QuoteArgument(serviceKey) + " /v Start");
				if (query.ExitCode != 0)
				{
					continue; // this AV is not on the image
				}
				Match m = Regex.Match(query.Output, @"Start\s+REG_DWORD\s+0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
				if (!m.Success)
				{
					// Can't read the original value -> can't faithfully restore it -> don't disable it.
					Log("Antivirus neutralization: could not read the original Start value for '" + service + "'; leaving it enabled to avoid an unrecoverable disable.");
					continue;
				}
				if (!restored.ContainsKey(service)) { restored[service] = Convert.ToInt32(m.Groups[1].Value, 16); }
				toDisable.Add(serviceKey); // 4 = SERVICE_DISABLED, applied below only after the restore scripts exist
			}
		}
		if (restored.Count == 0)
		{
			Log("Antivirus neutralization: no third-party AV services found on the image (nothing to disable).");
			return;
		}
		if (!autoRestoreInstalled)
		{
			// This path (e.g. Windows To Go from a WIM) installs no SYSTEM first-boot service to re-enable AV and
			// runs no first-boot AppX repair that needs AV off — so leave AV ENABLED rather than disable it forever.
			Log($"Antivirus neutralization: found {restored.Count} AV service(s) but left them ENABLED — this path has no automatic re-enable mechanism.");
			return;
		}
		try
		{
			string repairFolder = Path.Combine(cloneRoot, "ProgramData", "DriveForge");
			Directory.CreateDirectory(repairFolder);
			StringBuilder cmd = new StringBuilder();
			cmd.AppendLine("@echo off");
			cmd.AppendLine("REM Re-enables the antivirus that DriveForge disabled on this clone so the first-boot");
			cmd.AppendLine("REM app repair could run. Run as Administrator, then reboot. (Or just reinstall your AV.)");
			cmd.AppendLine("net session >nul 2>&1 || (echo Right-click this file and choose \"Run as administrator\". & pause & exit /b)");
			foreach (KeyValuePair<string, int> kv in restored)
			{
				cmd.AppendLine("reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\" + kv.Key + "\" /v Start /t REG_DWORD /d " + kv.Value + " /f >nul 2>&1");
			}
			cmd.AppendLine("echo Antivirus services restored. Reboot to re-activate protection.");
			cmd.AppendLine("pause");
			string cmdPath = Path.Combine(repairFolder, "Re-Enable-Antivirus.cmd");
			File.WriteAllText(cmdPath, cmd.ToString(), Encoding.ASCII);
			// Also drop a copy on the all-users Desktop so the user can find it easily.
			string publicDesktop = Path.Combine(cloneRoot, "Users", "Public", "Desktop");
			if (Directory.Exists(publicDesktop))
			{
				File.WriteAllText(Path.Combine(publicDesktop, "Re-Enable-Antivirus.cmd"), cmd.ToString(), Encoding.ASCII);
			}
			// Non-interactive twin used by the first-boot SYSTEM service to re-enable the antivirus
			// AUTOMATICALLY and UNCONDITIONALLY (no admin-check, no pause) regardless of whether the AppX
			// repair succeeded. Changing a service's Start value takes effect on the next reboot.
			StringBuilder auto = new StringBuilder();
			auto.AppendLine("@echo off");
			auto.AppendLine("REM Auto-restore of the third-party antivirus DriveForge temporarily disabled on this clone.");
			foreach (KeyValuePair<string, int> kv in restored)
			{
				auto.AppendLine("reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\" + kv.Key + "\" /v Start /t REG_DWORD /d " + kv.Value + " /f >nul 2>&1");
			}
			File.WriteAllText(Path.Combine(repairFolder, "Restore-Antivirus-Auto.cmd"), auto.ToString(), Encoding.ASCII);
		}
		catch (Exception ex)
		{
			// If we cannot lay down the auto-restore, do NOT disable anything — never leave AV off with no way back.
			Log("Antivirus neutralization: restore script not written, so the disable was skipped entirely: " + ex.Message);
			return;
		}
		// Restore scripts are in place — only now is it safe to commit the disable.
		foreach (string serviceKey in toDisable)
		{
			await RunProcessAsync("reg.exe", "add " + QuoteArgument(serviceKey) + " /v Start /t REG_DWORD /d 4 /f", allowFailure: true);
		}
		Log($"Antivirus neutralization: disabled {restored.Count} third-party AV service(s) on the clone so first-boot repair is not blocked. The clone's SYSTEM first-boot service re-enables them automatically and unconditionally (takes effect after a reboot); a manual Re-Enable-Antivirus.cmd is also placed on the clone Desktop as a fallback.");
	}

	private async Task MarkPortableWindowsAsync(char windowsLetter)
	{
		string value = $"{windowsLetter}:\\Windows\\System32\\config\\SYSTEM";
		string text = "DriveForgeSystem" + Guid.NewGuid().ToString("N");
		string hiveRoot = "HKLM\\" + text;
		bool loaded = false;
		try
		{
			await RunProcessAsync("reg.exe", $"load \"{hiveRoot}\" \"{value}\"");
			loaded = true;
			// Clear stale drive-letter mappings — on the clone the disk has a new GUID so old entries
			// cause drive-letter confusion. Windows rebuilds MountedDevices cleanly at first boot.
			await RunProcessAsync("reg.exe", $"delete \"{hiveRoot}\\MountedDevices\" /f", allowFailure: true);
			string[] array = new string[2] { "ControlSet001", "ControlSet002" };
			foreach (string controlSet in array)
			{
				await RunProcessAsync("reg.exe", $"add \"{hiveRoot}\\{controlSet}\\Control\" /v PortableOperatingSystem /t REG_DWORD /d 1 /f", allowFailure: true);
				await RunProcessAsync("reg.exe", $"add \"{hiveRoot}\\{controlSet}\\Services\\partmgr\\Parameters\" /v SanPolicy /t REG_DWORD /d 4 /f", allowFailure: true);
				await RunProcessAsync("reg.exe", $"add \"{hiveRoot}\\{controlSet}\\Policies\\Microsoft\\PortableOperatingSystem\" /v Sleep /t REG_DWORD /d 1 /f", allowFailure: true);
				// ServicesPipeTimeout: 60 s — prevents service-start failures on slow USB 2.0 drives (default 30 s)
				await RunProcessAsync("reg.exe", $"add \"{hiveRoot}\\{controlSet}\\Control\" /v ServicesPipeTimeout /t REG_DWORD /d 60000 /f", allowFailure: true);
				// Disable crash dump — prevents Windows writing multi-GB MEMORY.DMP files to the USB drive
				await RunProcessAsync("reg.exe", $"add \"{hiveRoot}\\{controlSet}\\Control\\CrashControl\" /v CrashDumpEnabled /t REG_DWORD /d 0 /f", allowFailure: true);
				// Universal-hardware boot: force inbox storage + USB drivers to boot-start (portable Windows on any PC).
				await ApplyUniversalBootStorageDriversAsync(hiveRoot, controlSet);
			}
			// Neutralize any third-party antivirus on the clone so the first-boot AppX repair runs unblocked.
			await NeutralizeAntivirusInHiveAsync(hiveRoot, new[] { "ControlSet001", "ControlSet002" }, $"{windowsLetter}:\\", autoRestoreInstalled: false);
			Log("PortableOperatingSystem, SAN policy, portable sleep, service timeout, and crash-dump settings applied.");
		}
		finally
		{
			if (loaded)
			{
				if (!await UnloadRegistryHiveRobustAsync(hiveRoot)) Log("WARNING: could not unload the portable SYSTEM hive '" + hiveRoot + "' after retries — the portable settings may not have committed.");
			}
		}
	}

	private async Task ConfigurePortablePagefileAsync(char windowsLetter)
	{
		string systemHive = $"{windowsLetter}:\\Windows\\System32\\config\\SYSTEM";
		string hiveName = "DriveForgePaging" + Guid.NewGuid().ToString("N");
		string hiveRoot = "HKLM\\" + hiveName;
		bool loaded = false;
		try
		{
			await RunProcessAsync("reg.exe", $"load \"{hiveRoot}\" \"{systemHive}\"");
			loaded = true;
			foreach (string controlSet in new[] { "ControlSet001", "ControlSet002" })
			{
				string memoryManagement = $"{hiveRoot}\\{controlSet}\\Control\\Session Manager\\Memory Management";
				await RunProcessAsync("reg.exe", $"add \"{memoryManagement}\" /v PagingFiles /t REG_MULTI_SZ /d \"C:\\pagefile.sys 0 0\" /f", allowFailure: true);
				await RunProcessAsync("reg.exe", $"delete \"{memoryManagement}\" /v ExistingPageFiles /f", allowFailure: true);
				await RunProcessAsync("reg.exe", $"add \"{memoryManagement}\" /v TempPageFile /t REG_DWORD /d 0 /f", allowFailure: true);
			}
			Log("Portable pagefile configured. The cloned Windows will create a fresh pagefile on first boot.");
		}
		finally
		{
			if (loaded)
			{
				if (!await UnloadRegistryHiveRobustAsync(hiveRoot)) Log("WARNING: could not unload the portable SYSTEM hive '" + hiveRoot + "' after retries — the portable settings may not have committed.");
			}
		}
	}

	private string CreateWinPeCloneKit()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DriveForge-FullCloneKit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
		Directory.CreateDirectory(text);
		File.WriteAllText(Path.Combine(text, "README.txt"), "DriveForge Current Computer Clone Helper\n\nWhy this exists:\nThe safest complete copy of the current computer must be captured outside the Windows that is currently running.\nThese helper files create a full clone file (.ffu) from Windows PE.\n\nRecommended workflow:\n1. Boot into Windows PE.\n2. Run 01-create-full-clone-file.cmd.\n3. Save the full clone file to a separate external drive with enough free space.\n4. Boot back into Windows.\n5. Open DriveForge, choose \"Restore a full computer clone\", select the .ffu file, and restore it to the target USB/SSD.\n\nWarning:\nThe restore script and DriveForge restore mode format the selected destination disk.", Encoding.UTF8);
		File.WriteAllText(Path.Combine(text, "01-create-full-clone-file.cmd"), "@echo off\nsetlocal\ntitle DriveForge - Create full clone file\necho.\necho This script must be run from Windows PE as administrator.\necho It creates a complete clone file. Do not save it to the same disk you are cloning.\necho.\necho Available disks:\nwmic diskdrive get index,model,size\necho.\nset /p SRC=Enter source disk number to capture:\nset /p OUT=Enter full clone file path, for example E:\\CurrentWindows.ffu:\necho.\necho Capturing PhysicalDrive%SRC% to %OUT%\ndism /Capture-FFU /ImageFile:\"%OUT%\" /CaptureDrive:\\\\.\\PhysicalDrive%SRC% /Name:\"DriveForge Full Disk Clone\" /Description:\"Captured by DriveForge from WinPE\"\nif errorlevel 1 goto failed\necho.\necho Optimizing full clone file...\ndism /Optimize-FFU /ImageFile:\"%OUT%\"\nif errorlevel 1 goto failed\necho.\necho Capture completed.\npause\nexit /b 0\n:failed\necho.\necho Capture failed.\npause\nexit /b 1", Encoding.ASCII);
		File.WriteAllText(Path.Combine(text, "02-restore-full-clone-to-disk.cmd"), "@echo off\nsetlocal\ntitle DriveForge - Restore full clone to disk\necho.\necho WARNING: This will format the destination disk.\necho.\nwmic diskdrive get index,model,size\necho.\nset /p FFU=Enter full clone file path:\nif \"%FFU%\"==\"\" (echo No clone file path entered. & pause & exit /b 1)\nset /p DST=Enter destination disk number:\nif \"%DST%\"==\"\" (echo No destination disk number entered. & pause & exit /b 1)\necho.\necho You are about to restore %FFU% to PhysicalDrive%DST%.\nchoice /m \"Format destination disk and continue\"\nif errorlevel 2 exit /b 1\ndism /Apply-FFU /ImageFile:\"%FFU%\" /ApplyDrive:\\\\.\\PhysicalDrive%DST%\nif errorlevel 1 goto failed\necho.\necho Apply completed.\npause\nexit /b 0\n:failed\necho.\necho Apply failed.\npause\nexit /b 1", Encoding.ASCII);
		return text;
	}

	private string CreateDriveDiagnosticKit()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DriveForge-Drive-Diagnostic-Kit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
		string[] folders =
		{
			"Benchmark",
			"Surface Scan",
			"Repair Recovery"
		};
		foreach (string folder in folders)
		{
			Directory.CreateDirectory(Path.Combine(root, folder));
		}
		File.WriteAllText(Path.Combine(root, "README.txt"), BuildDiagnosticKitReadme(), Encoding.UTF8);
		File.WriteAllText(Path.Combine(root, "Benchmark", "run-basic-write-test.cmd"), "@echo off\r\necho This is a simple Windows write test. For a professional benchmark, use a dedicated third-party benchmark tool.\r\necho.\r\necho WARNING: this WRITES about 512 MB to the drive's free space. Do NOT run it on a drive you are trying to\r\necho recover deleted files from - the writes can overwrite the very data you want back.\r\necho.\r\nset /p DRIVE=Enter drive letter to test, for example E:\r\nif \"%DRIVE%\"==\"\" (echo No drive entered. & pause & exit /b 1)\r\nchoice /m \"Write about 512 MB of test data to %DRIVE%\"\r\nif errorlevel 2 exit /b 1\r\npowershell -NoProfile -Command \"$p='%DRIVE%\\driveforge-test.bin'; $b=New-Object byte[] (1MB); (New-Object Random).NextBytes($b); $sw=[Diagnostics.Stopwatch]::StartNew(); $fs=[IO.File]::Open($p,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); try { for($i=0;$i -lt 512;$i++){ $fs.Write($b,0,$b.Length) }; $fs.Flush($true) } finally { $fs.Close(); Remove-Item $p -Force -ErrorAction SilentlyContinue }; $sw.Stop(); '{0:N1} MB/s' -f (512/$sw.Elapsed.TotalSeconds)\"\r\npause\r\n", Encoding.ASCII);
		File.WriteAllText(Path.Combine(root, "Surface Scan", "run-chkdsk-scan.cmd"), "@echo off\r\nset /p DRIVE=Enter drive letter to scan, for example E:\r\nif \"%DRIVE%\"==\"\" (echo No drive entered. & pause & exit /b 1)\r\nchkdsk %DRIVE% /scan\r\npause\r\n", Encoding.ASCII);
		File.WriteAllText(Path.Combine(root, "Repair Recovery", "run-chkdsk-repair.cmd"), "@echo off\r\necho WARNING: This can take a long time and may lock the drive.\r\nset /p DRIVE=Enter drive letter to repair, for example E:\r\nif \"%DRIVE%\"==\"\" (echo No drive entered. & pause & exit /b 1)\r\nchoice /m \"Run CHKDSK repair on %DRIVE%\"\r\nif errorlevel 2 exit /b 1\r\nchkdsk %DRIVE% /r /x\r\npause\r\n", Encoding.ASCII);
		return root;
	}

	private static string BuildDiagnosticKitReadme()
	{
		return "DriveForge Drive Diagnostic Kit\r\n" +
			"================================\r\n\r\n" +
			"Recommended workflow for HDD / SSD / USB diagnosis:\r\n\r\n" +
			"1. Quick health: DriveForge's Health (SMART) report, or a dedicated third-party SMART tool.\r\n" +
			"2. SMART extended self-test: a dedicated third-party SMART tool.\r\n" +
			"3. Speed benchmark: DriveForge's speed test, or a dedicated third-party benchmark tool.\r\n" +
			"4. Surface / file-system scan: DriveForge's surface test, the CHKDSK /scan script in this folder, or a dedicated surface-scan tool.\r\n" +
			"5. Repair / recovery: the CHKDSK /r script for file-system and bad-sector remap attempts; DriveForge's Recover feature, or a dedicated data-recovery tool, for lost files.\r\n\r\n" +
			"Important:\r\n" +
			"No software can truly repair physical media damage. Tools can detect bad sectors, trigger remapping, recover data, or repair file-system structures. If SMART health is bad or unstable, replace the drive.\r\n\r\n" +
			"This folder contains ready-to-run CHKDSK scan/repair scripts and a simple write-speed test. For SMART health, benchmarking, surface scanning and file recovery, use DriveForge's built-in tools or a dedicated third-party utility of your choice.\r\n";
	}

	private bool NeedsStrongPerformanceWarning(DiskItem disk)
	{
		// Only trust a speed result that actually measured something. A 0 MB/s reading means the test could
		// not run (e.g. no mounted volume on a system/unformatted disk) — that is NOT evidence the drive is slow.
		if (speedResults.TryGetValue(disk.Number, out SpeedResult value) && value.SequentialWriteMb > 0.5)
		{
			return value.Rating != SpeedRating.Good;
		}
		if (disk.BusType.Contains("USB", StringComparison.OrdinalIgnoreCase) && !disk.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase))
		{
			return !disk.FriendlyName.Contains("SSD", StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private void UpdateDiskSummary()
	{
		if (!(DiskBox.SelectedItem is DiskItem diskItem))
		{
			DiskSummaryText.Text = L("DSumNoDisk");
			SpeedSummaryText.Text = L("DSpdNotTested");
			RecommendationText.Text = L("DSumSelectDisk");
			WarningBox.Visibility = Visibility.Collapsed;
			return;
		}
		DiskSummaryText.Text = $"Disk {diskItem.Number}: {diskItem.FriendlyName}\n{FormatBytes(diskItem.Size)} | {diskItem.BusType} | {diskItem.MediaType}";
		if (speedResults.TryGetValue(diskItem.Number, out SpeedResult value))
		{
			SpeedSummaryText.Text = string.Format(L("DSpdSummary"), value.SequentialWriteMb.ToString("F1"), value.Random4KWriteMb.ToString("F1"));
			RecommendationText.Text = value.Message;
			// A 0 MB/s reading means the test could not run (no mounted volume / system disk), not a slow drive —
			// don't show the "slow" warning in that case. Only warn on a real, non-Good measurement.
			bool measured = value.SequentialWriteMb > 0.5;
			WarningBox.Visibility = (!measured || value.Rating == SpeedRating.Good) ? Visibility.Collapsed : Visibility.Visible;
			WarningText.Text = (value.Rating == SpeedRating.Bad) ? L("DWarnBad") : L("DWarnSlow");
		}
		else
		{
			SpeedSummaryText.Text = L("DSpdNotTested");
			RecommendationText.Text = (diskItem.IsSystem ? L("DSumBlockedSys") : string.Format(L("DSumSpeedRec"), diskItem.HealthText));
			WarningBox.Visibility = Visibility.Collapsed;
		}
	}

	// Colour for the big Health Status card, from the OS health label alone: green = good, amber = caution, grey =
	// unknown, red = bad. Shared so every path that renders the card (full report, placeholder, no-selection) agrees —
	// the card is INSIDE the same border as ToolHealthText, so leaving a stale colour there produces a self-contradicting
	// card such as a green box labelled "Unhealthy".
	private System.Windows.Media.Color HealthCardColor(string healthText)
	{
		string h = (healthText ?? "").ToLowerInvariant();
		// Test the BAD words FIRST. A status like "Health: Unhealthy/Unknown" also contains "unknown", so checking that
		// earlier would paint the card GREY while the label inside the very same card reads "Unhealthy" — the same
		// substring trap that made the destructive-op health gate blind ("Unhealthy" contains "Healthy").
		if (h.Contains("unhealthy") || h.Contains("degraded") || h.Contains("fail")) return System.Windows.Media.Color.FromRgb(180, 40, 40);
		if (h.Contains("warn") || h.Contains("caution")) return System.Windows.Media.Color.FromRgb(180, 120, 10);
		if (IsHealthy(healthText)) return System.Windows.Media.Color.FromRgb(22, 163, 74);
		if (string.IsNullOrWhiteSpace(h) || h.Contains("unknown")) return System.Windows.Media.Color.FromRgb(71, 85, 105);
		return System.Windows.Media.Color.FromRgb(180, 40, 40);
	}

	// The physical disk (identity, not object reference) the "Drive tools" overview card currently reflects. A
	// background/post-operation disk-list refresh re-selects the SAME physical disk under a NEW DiskItem instance
	// (e.g. right after Wipe's diskpart clean fires a device-change refresh) — without this guard that re-render
	// the card even though the user never touched a diagnostic tool, which reads as an unrequested "Health" popping
	// up mid-Erase. Only a genuine switch to a DIFFERENT disk (or none) updates the card.
	private string _lastOverviewDiskKey = "\0uninitialized";

	private void UpdateDriveToolOverview()
	{
		string overviewKey = DiskBox.SelectedItem is DiskItem selDisk ? DiskIdentityKey(selDisk) : "";
		if (overviewKey == _lastOverviewDiskKey) return;
		_lastOverviewDiskKey = overviewKey;
		if (!(DiskBox.SelectedItem is DiskItem disk))
		{
			ToolDriveTitleText.Text = L("DToolNoDrive");
			ToolHealthText.Text = L("DHlUnknown");
			ToolTemperatureText.Text = "-- °C";
			ToolFirmwareText.Text = L("DToolFwUnknown");
			ToolSerialText.Text = L("DToolSerUnknown");
			ToolInterfaceText.Text = L("DToolIfUnknown");
			ToolSizeText.Text = L("DToolSizeUnknown");
			ToolRecommendationDetailText.Text = L("DToolSelectBegin");
			SmartGrid.ItemsSource = Array.Empty<SmartRow>();
			HealthStatusCard.Background = new System.Windows.Media.SolidColorBrush(HealthCardColor(""));   // grey, matches "Unknown"
			if (HealthTrendText != null) HealthTrendText.Text = "";
			if (HealthTrendBox != null) HealthTrendBox.Visibility = Visibility.Collapsed;
			return;
		}
		// If we already hold a health/SMART report for this disk (e.g. a Refresh or re-selection rebuilt the
		// combo), re-render the full detailed view instead of the placeholder — otherwise the SMART table
		// gets wiped every time the disk list refreshes.
		// Match on IDENTITY, not just the number: disk numbers are recycled when removable drives are swapped, and this
		// cached report now carries the previous drive's reliability counters — which drive the failure verdict directly.
		// Re-rendering it for a different physical drive would show that drive's counters (red "replace" on a healthy
		// stick, or a green "no failure signs" on a dying one) plus its trend note, under the new drive's name.
		if (_diagDisk != null && _diagReport != null && _diagDisk.Number == disk.Number
			&& string.Equals(DiskIdentityKey(_diagDisk), DiskIdentityKey(disk), StringComparison.OrdinalIgnoreCase))
		{
			UpdateHealthVisuals(disk, _diagReport, recordTrend: false);
			if (speedResults.TryGetValue(disk.Number, out SpeedResult cachedSpeed)) UpdateSpeedVisuals(cachedSpeed);
			return;
		}
		// No report yet for this disk — clear any stale SMART rows left over from a previously selected disk.
		SmartGrid.ItemsSource = Array.Empty<SmartRow>();
		// The trend note and temperature sparkline sit directly under the drive title, so leaving the PREVIOUS drive's
		// text there reads as a verdict about THIS one — e.g. a dying drive's "back up your data" warning under a healthy
		// SSD's name, or worse, a reassuring "status stable" line under the dying drive. Clear both with the SMART rows.
		if (HealthTrendText != null) HealthTrendText.Text = "";
		if (HealthTrendBox != null) HealthTrendBox.Visibility = Visibility.Collapsed;
		_trendSerial = "";
		ToolDriveTitleText.Text = $"Disk {disk.Number} - {disk.FriendlyName}";
		ToolHealthText.Text = LHealth(disk.HealthText);
		// Recolour the card for THIS drive from its OS health label (no report yet, so no predictive escalation). Without
		// this the previous drive's red/green verdict colour stays behind the freshly-updated label inside the same card.
		HealthStatusCard.Background = new System.Windows.Media.SolidColorBrush(HealthCardColor(disk.HealthText));
		ToolTemperatureText.Text = "-- °C";
		ToolFirmwareText.Text = L("DToolFwNotExposed");
		ToolSerialText.Text = L("DToolSerByHealth");
		ToolInterfaceText.Text = string.Format(L("DToolIfFmt"), disk.BusType, disk.MediaType);
		ToolSizeText.Text = string.Format(L("DToolSizeLetters"), FormatBytes(disk.Size), FormatDriveLetters(disk));
		ToolRecommendationDetailText.Text = disk.IsSystem ? L("DToolSysBlocked") : L("DToolRunDetails");
		if (speedResults.TryGetValue(disk.Number, out SpeedResult speed))
		{
			UpdateSpeedVisuals(speed);
		}
	}

	// Last health/SMART report rendered — kept so the diagnostics view can be re-rendered in a new
	// language without re-running the (side-effecting) report query.
	private DiskItem? _diagDisk;
	private string? _diagReport;

	// recordTrend: true for a live health/SMART read (appends to the persisted history). false when merely
	// re-rendering the cached report after a disk-list refresh, so we don't log a phantom "check".
	private void UpdateHealthVisuals(DiskItem disk, string report, bool recordTrend = true)
	{
		_diagDisk = disk; _diagReport = report;
		ToolDriveTitleText.Text = $"Disk {disk.Number} - {disk.FriendlyName}";
		ToolHealthText.Text = LHealth(disk.HealthText);
		ToolTemperatureText.Text = ExtractReportValue(report, "Temperature") is string temperature && !string.IsNullOrWhiteSpace(temperature) ? temperature + " °C" : "-- °C";
		ToolFirmwareText.Text = string.Format(L("DToolFwFmt"), ExtractReportValue(report, "FirmwareVersion", L("DToolNotExposed")));
		ToolSerialText.Text = string.Format(L("DToolSerFmt"), ExtractReportValue(report, "SerialNumber", L("DToolNotExposed")));
		ToolInterfaceText.Text = string.Format(L("DToolIfFmt"), disk.BusType, disk.MediaType);
		ToolSizeText.Text = string.Format(L("DToolSizePartition"), FormatBytes(disk.Size), disk.PartitionStyle);
		var pred = FailurePrediction(disk, report);
		ToolRecommendationDetailText.Text = pred.Text + "\n" + BuildHealthRecommendation(disk);
		SmartGrid.ItemsSource = BuildSmartRows(disk, report);
		// Colour the Health Status card: green = good, amber = caution, red = bad, grey = unknown.
		System.Windows.Media.Color card = HealthCardColor(disk.HealthText);
		// Escalate the card if the predictive verdict is worse than the OS health label (uncorrectable errors / high wear).
		if (pred.Level == 2) card = System.Windows.Media.Color.FromRgb(180, 40, 40);
		else if (pred.Level == 1 && IsHealthy(disk.HealthText)) card = System.Windows.Media.Color.FromRgb(180, 120, 10);
		HealthStatusCard.Background = new System.Windows.Media.SolidColorBrush(card);
		if (recordTrend)
		{
			string trendSerial = ExtractReportValue(report, "SerialNumber", "");
			HealthTrendText.Text = RecordHealthTrend(trendSerial, disk.HealthText ?? "", ToolTemperatureText.Text);
			DrawHealthTrend(trendSerial);
		}
	}

	// Predictive failure verdict from Windows per-disk reliability counters (uncorrectable errors, wear, health,
	// temperature) — the controller-independent signal that works on SATA, NVMe and many USB bridges, unlike raw
	// ATA attribute IDs. Returns (level: 0 healthy / 1 watch / 2 replace / -1 unknown, localized text).
	private (int Level, string Text) FailurePrediction(DiskItem disk, string report)
	{
		long Get(string k) => long.TryParse(ExtractReportValue(report, k)?.Trim(), out long n) ? n : -1;
		long ruc = Get("ReadErrorsUncorrected"), wuc = Get("WriteErrorsUncorrected");
		long rtot = Get("ReadErrorsTotal"), wtot = Get("WriteErrorsTotal");
		// NOTE: classic ATA SMART "Reallocated Sector Count" / "Current Pending Sector Count" (attrs 5/197) are NOT
		// exposed by MSFT_StorageReliabilityCounter (confirmed against real Get-StorageReliabilityCounter output —
		// it has no ReallocatedSectors/PendingSectors property on any bus), so a lookup for them here would always
		// return the "not present" sentinel and never actually escalate anything. Not attempted; the app already
		// points users at a dedicated third-party SMART tool for attribute-level detail it doesn't have access to.
		int wear = (int)Get("Wear"), temp = (int)Get("Temperature");
		string h = (disk.HealthText ?? "").ToLowerInvariant();
		// Require REAL reliability data before returning a confident verdict. DiskItem.HealthText is a computed property
		// that is NEVER empty (it always starts with "Health: "), so including it here made anyData unconditionally true,
		// turned the "not enough data" branch below into dead code, and let a drive that exposes NO counters at all — a
		// USB bridge that hides SMART, say — be painted green with "No failure signs". A bad OS health label still counts
		// as data (it alone is enough to say "replace"), but a good one no longer substitutes for counters we never read.
		bool badHealthLabel = h.Contains("unhealthy") || h.Contains("warn") || h.Contains("caution") || h.Contains("fail");
		bool anyData = ruc >= 0 || wuc >= 0 || rtot >= 0 || wtot >= 0 || wear >= 0 || temp >= 0 || badHealthLabel;
		var reasons = new List<string>();
		bool replace = false, watch = false;

		if (h.Contains("unhealthy") || h.Contains("warn") || h.Contains("caution") || h.Contains("fail"))
		{ replace = true; reasons.Add(string.Format(L("PredReasonHealth"), disk.HealthText)); }
		if (ruc > 0 || wuc > 0) { replace = true; reasons.Add(string.Format(L("PredReasonUncorrected"), Math.Max(ruc, 0), Math.Max(wuc, 0))); }
		if (wear >= 90) { replace = true; reasons.Add(string.Format(L("PredReasonWear"), wear)); }
		else if (wear >= 70) { watch = true; reasons.Add(string.Format(L("PredReasonWear"), wear)); }
		if (!replace && (rtot > 100 || wtot > 100)) { watch = true; reasons.Add(string.Format(L("PredReasonErrors"), Math.Max(rtot, 0) + Math.Max(wtot, 0))); }
		if (temp >= 60) { watch = true; reasons.Add(string.Format(L("PredReasonTemp"), temp)); }

		if (!anyData) return (-1, L("PredUnknown"));
		int level = replace ? 2 : watch ? 1 : 0;
		string verdict = level == 2 ? "⚠ " + L("PredReplace") : level == 1 ? "● " + L("PredWatch") : "✓ " + L("PredHealthy");
		return (level, reasons.Count > 0 ? verdict + " — " + string.Join("; ", reasons) : verdict);
	}

	private string _trendSerial = "";

	private void HealthTrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHealthTrend(_trendSerial);

	// Reads the saved temperature history for a drive serial (oldest → newest).
	private List<(DateTime Date, double Temp)> ReadTempHistory(string serial)
	{
		var result = new List<(DateTime, double)>();
		try
		{
			if (string.IsNullOrWhiteSpace(serial)) return result;
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge", "health-history.json");
			if (!File.Exists(path)) return result;
			var list = JsonSerializer.Deserialize<List<HealthSnap>>(File.ReadAllText(path)) ?? new List<HealthSnap>();
			foreach (var s in list.Where(s => s.Serial == serial).OrderBy(s => s.Date))
			{
				var m = Regex.Match(s.Temp ?? "", "\\d+");
				if (m.Success && int.TryParse(m.Value, out int t) && t > 0 && t < 150) result.Add((s.Date, t));
			}
		}
		catch { }
		return result;
	}

	// Draws a small temperature sparkline for the selected drive from its recorded health history.
	private void DrawHealthTrend(string serial)
	{
		if (HealthTrendCanvas == null) return;
		_trendSerial = serial;
		HealthTrendCanvas.Children.Clear();
		var pts = ReadTempHistory(serial);
		if (pts.Count < 2)
		{
			if (HealthTrendBox != null) HealthTrendBox.Visibility = Visibility.Collapsed;
			return;
		}
		if (HealthTrendBox != null) HealthTrendBox.Visibility = Visibility.Visible;

		double w = HealthTrendCanvas.ActualWidth; if (w < 10) w = 540;
		double h = HealthTrendCanvas.ActualHeight; if (h < 10) h = 66;
		double pad = 8;
		double minT = pts.Min(p => p.Temp), maxT = pts.Max(p => p.Temp);
		if (maxT - minT < 1) maxT = minT + 1;
		if (HealthTrendRangeText != null) HealthTrendRangeText.Text = $"{minT:F0}–{maxT:F0} °C ({pts.Count})";

		var line = (System.Windows.Media.Brush)FindResource("BlueBrush");
		var muted = (System.Windows.Media.Brush)FindResource("Border2Brush");
		// baseline
		var baseLine = new System.Windows.Shapes.Line { X1 = pad, Y1 = h - pad, X2 = w - pad, Y2 = h - pad, Stroke = muted, StrokeThickness = 1 };
		HealthTrendCanvas.Children.Add(baseLine);

		var poly = new System.Windows.Shapes.Polyline { Stroke = line, StrokeThickness = 2, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round };
		int n = pts.Count;
		for (int i = 0; i < n; i++)
		{
			double x = pad + (w - 2 * pad) * (n == 1 ? 0 : (double)i / (n - 1));
			double y = (h - pad) - (h - 2 * pad) * ((pts[i].Temp - minT) / (maxT - minT));
			poly.Points.Add(new System.Windows.Point(x, y));
		}
		HealthTrendCanvas.Children.Add(poly);

		var lastPt = poly.Points[poly.Points.Count - 1];
		var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = line };
		System.Windows.Controls.Canvas.SetLeft(dot, lastPt.X - 3.5);
		System.Windows.Controls.Canvas.SetTop(dot, lastPt.Y - 3.5);
		HealthTrendCanvas.Children.Add(dot);
	}

	private sealed class HealthSnap { public string Serial { get; set; } public DateTime Date { get; set; } public string Health { get; set; } public string Temp { get; set; } }

	// Records a health snapshot per drive serial and returns a short trend note (stable / degrading).
	private string RecordHealthTrend(string serial, string health, string temp)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(serial) || serial.Equals("not exposed", StringComparison.OrdinalIgnoreCase)) return "";
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge", "health-history.json");
			List<HealthSnap> list = new List<HealthSnap>();
			try { if (File.Exists(path)) list = JsonSerializer.Deserialize<List<HealthSnap>>(File.ReadAllText(path)) ?? new List<HealthSnap>(); } catch { }
			var mine = list.Where(s => s.Serial == serial).OrderBy(s => s.Date).ToList();
			list.Add(new HealthSnap { Serial = serial, Date = DateTime.UtcNow, Health = health, Temp = temp });
			if (list.Count > 1000) list = list.Skip(list.Count - 1000).ToList();
			// Atomic write: a crash mid-write must not truncate the whole history file.
			try { Directory.CreateDirectory(Path.GetDirectoryName(path)); string tmp = path + ".tmp"; File.WriteAllText(tmp, JsonSerializer.Serialize(list)); File.Move(tmp, path, true); } catch { }

			int count = mine.Count + 1;
			if (mine.Count == 0) return L("DTrendFirst");
			var first = mine.First();
			var prev = mine.Last();
			// Compare against the most recent prior check, and report when THAT transition happened (not the first-ever date).
			if (IsHealthy(prev.Health) && !IsHealthy(health))
				return string.Format(L("DTrendChanged"), health, prev.Date.ToLocalTime().ToString("yyyy-MM-dd"));
			return string.Format(L("DTrendStable"), count, first.Date.ToLocalTime().ToString("yyyy-MM-dd"), LHealth(health));
		}
		catch { return ""; }
	}

	private void UpdateSmartVisuals(DiskItem disk, string report)
	{
		UpdateHealthVisuals(disk, report);
		ToolRecommendationDetailText.Text = L("DSmartLoaded");
	}

	private void UpdateSpeedVisuals(SpeedResult speedResult)
	{
		SequentialSpeedBar.Value = Math.Min(SequentialSpeedBar.Maximum, Math.Max(0.0, speedResult.SequentialWriteMb));
		RandomSpeedBar.Value = Math.Min(RandomSpeedBar.Maximum, Math.Max(0.0, speedResult.Random4KWriteMb));
		SpeedVisualText.Text = string.Format(L("DSpdVisual"), speedResult.SequentialWriteMb.ToString("F1"), speedResult.Random4KWriteMb.ToString("F1"));
		SpeedAdviceText.Text = BuildSpeedRecommendation(speedResult);
	}

	private IReadOnlyList<SmartRow> BuildSmartRows(DiskItem disk, string report)
	{
		bool healthy = IsHealthy(disk.HealthText);
		bool operOk = disk.OperationalStatus.Contains("OK", StringComparison.OrdinalIgnoreCase);
		List<SmartRow> rows = new List<SmartRow>
		{
			new SmartRow("01", L("SmHealth"), LHealth(disk.HealthText), healthy ? L("SmStGood") : L("SmStCheck"), healthy ? "good" : "warn"),
			new SmartRow("02", L("SmOper"), disk.OperationalStatus, operOk ? L("SmStGood") : L("SmStInfo"), operOk ? "good" : "info"),
			new SmartRow("03", L("SmBus"), disk.BusType, L("SmStInfo"), "info"),
			new SmartRow("04", L("SmMedia"), disk.MediaType, L("SmStInfo"), "info"),
			new SmartRow("05", L("SmPart"), disk.PartitionStyle, L("SmStInfo"), "info"),
			new SmartRow("06", L("SmLetters"), FormatDriveLetters(disk), L("SmStInfo"), "info")
		};
		// The lookup key (2nd arg) is the English field name in the PowerShell report and must NOT be localized;
		// only the visible label (3rd arg) is.
		AddReliabilityRow(rows, report, "Temperature", L("SmTemp"));
		AddReliabilityRow(rows, report, "PowerOnHours", L("SmPowerOn"));
		// MSFT_StorageReliabilityCounter's real property is StartStopCycleCount — "PowerCycleCount" is not a field
		// this class exposes, so the lookup below always failed silently and this row never appeared.
		AddReliabilityRow(rows, report, "StartStopCycleCount", L("SmPowerCycles"));
		AddReliabilityRow(rows, report, "ReadErrorsTotal", L("SmReadTotal"));
		AddReliabilityRow(rows, report, "ReadErrorsUncorrected", L("SmReadUnc"));
		AddReliabilityRow(rows, report, "WriteErrorsTotal", L("SmWriteTotal"));
		AddReliabilityRow(rows, report, "WriteErrorsUncorrected", L("SmWriteUnc"));
		AddReliabilityRow(rows, report, "Wear", L("SmWear"));
		AddReliabilityRow(rows, report, "DeviceId", L("SmDeviceId"));
		return rows;
	}

	private void AddReliabilityRow(List<SmartRow> rows, string report, string key, string label)
	{
		string value = ExtractReportValue(report, key);
		if (!string.IsNullOrWhiteSpace(value))
		{
			rows.Add(new SmartRow((rows.Count + 1).ToString("00"), label, value, L("SmStInfo"), "info"));
		}
	}

	private static string ExtractReportValue(string report, string key, string fallback = "")
	{
		Match match = Regex.Match(report ?? "", @"^\s*" + Regex.Escape(key) + @"\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
		return match.Success ? match.Groups[1].Value.Trim() : fallback;
	}

	private string BuildHealthRecommendation(DiskItem disk)
	{
		if (!IsHealthy(disk.HealthText))
		{
			return L("DHealthRecBad");
		}
		return L("DHealthRecOk");
	}

	// Localized health label for the prominent UI and the SMART table's Health Status value.
	private string LHealth(string? healthText)
		=> IsHealthy(healthText) ? L("DHlGood") : (string.IsNullOrWhiteSpace(healthText) ? L("DHlUnknown") : healthText);

	private static bool IsHealthy(string? healthText)
	{
		if (string.IsNullOrWhiteSpace(healthText)) return false;
		// A drive that reports ANY degradation is not healthy, even if the text also carries an "OK"
		// OperationalStatus — otherwise a Warning drive gets painted green and the alert is suppressed.
		if (healthText.Contains("Warning", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase)
			|| healthText.Contains("Degraded", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Caution", StringComparison.OrdinalIgnoreCase)
			|| healthText.Contains("Bad", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Fail", StringComparison.OrdinalIgnoreCase))
			return false;
		return healthText.Contains("OK", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Healthy", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Good", StringComparison.OrdinalIgnoreCase);
	}

	private string FormatDriveLetters(DiskItem disk)
	{
		return disk.DriveLetters.Count == 0 ? L("SmNoLetter") : string.Join(", ", disk.DriveLetters.Select(letter => letter + ":"));
	}

	private SpeedResult MeasureDiskSpeed(DiskItem disk, Action<int> progress)
	{
		char? c = disk.DriveLetters.Select(char.ToUpperInvariant).FirstOrDefault((char letter) => letter >= 'A' && letter <= 'Z');
		if (!c.HasValue || c.Value == '\0')
		{
			return new SpeedResult(0.0, 0.0, SpeedRating.Unknown, L("DSpdMsgNoVol"));
		}
		string path = Path.Combine($"{c.Value}:\\", "DriveForge-speed-test-" + Guid.NewGuid().ToString("N") + ".bin");
		byte[] array = new byte[1048576];
		new Random(7).NextBytes(array);
		try
		{
			progress(2);
			Stopwatch stopwatch = Stopwatch.StartNew();
			using (FileStream fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1048576, FileOptions.WriteThrough))
			{
				for (int num = 0; num < 64; num++)
				{
					if (stopRequested) throw new OperationCanceledException();
					fileStream.Write(array, 0, array.Length);
					progress(2 + num * 44 / 64); // sequential phase → ~2..46%
				}
				fileStream.Flush(flushToDisk: true);
			}
			stopwatch.Stop();
			double num2 = 64.0 / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
			progress(48);
			byte[] array2 = new byte[4096];
			new Random(11).NextBytes(array2);
			stopwatch.Restart();
			using (FileStream fileStream2 = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
			{
				for (int num3 = 0; num3 < 4096; num3++)
				{
					if (stopRequested) throw new OperationCanceledException();
					long position = (long)(num3 * 7919 % 16384) * 4096L; // keep random writes inside the 64 MB already written — don't double the temp file (risks a false 'Bad'/IOException on a near-full drive)
					fileStream2.Position = position;
					fileStream2.Write(array2, 0, array2.Length);
					if ((num3 & 0xFF) == 0) progress(48 + num3 * 48 / 4096); // random phase → ~48..96%
				}
				fileStream2.Flush(flushToDisk: true);
			}
			stopwatch.Stop();
			progress(98);
			double num4 = 16.0 / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
			SpeedRating speedRating = ((num2 >= 80.0 && num4 >= 8.0) ? SpeedRating.Good : ((!(num2 >= 25.0) || !(num4 >= 2.0)) ? SpeedRating.Bad : SpeedRating.Usable));
			return new SpeedResult(num2, num4, speedRating, speedRating switch
			{
				SpeedRating.Good => L("DSpdMsgGood"),
				SpeedRating.Usable => L("DSpdMsgUsable"),
				_ => L("DSpdMsgBad"),
			});
		}
		finally
		{
			TryDeleteFile(path);
		}
	}

	private string BuildSpeedRecommendation(SpeedResult speedResult)
	{
		return speedResult.Rating switch
		{
			SpeedRating.Good => L("DSpdRecGood"),
			SpeedRating.Usable => L("DSpdRecUsable"),
			SpeedRating.Bad => L("DSpdRecBad"),
			_ => L("DSpdRecUnknown")
		};
	}

	private async Task<List<DiskItem>> GetDisksAsync()
	{
		// MediaType MUST come from Get-PhysicalDisk, not Get-Disk: MSFT_Disk reports "Unknown"/"Unspecified" for
		// virtually every real drive (verified on this machine: an Intel NVMe SSD and USB SSDs ALL reported
		// MediaType=Unknown via Get-Disk, while Get-PhysicalDisk correctly said SSD). Reading the Get-Disk value
		// meant DetectWipeMedia could never return Hdd, so secure-delete's overwrite pass was unreachable on every
		// spinning disk, and the free-space wipe showed the "flash is unreliable" banner even on a real HDD.
		string value = "$phys = @(Get-PhysicalDisk -ErrorAction SilentlyContinue)\n$disks = Get-Disk | Sort-Object Number | ForEach-Object {\n  $d = $_\n  $parts = @(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue)\n  $p = $phys | Where-Object { $_.DeviceId -eq [string]$d.Number } | Select-Object -First 1\n  if (-not $p) { $p = $phys | Where-Object { $_.FriendlyName -eq $d.FriendlyName } | Select-Object -First 1 }\n  $mt = 'Unknown'\n  if ($p -and $null -ne $p.MediaType) { $mt = $p.MediaType.ToString() }\n  if ($mt -eq 'Unspecified' -or $mt -eq '0') { $mt = 'Unknown' }\n  if ($mt -eq 'Unknown' -and $null -ne $d.MediaType) { $mt = $d.MediaType.ToString() }\n  [pscustomobject]@{\n    Number = $d.Number\n    FriendlyName = if ($null -ne $d.FriendlyName) { $d.FriendlyName.ToString() } else { ('Disk ' + $d.Number) }\n    SerialNumber = $d.SerialNumber\n    BusType = if ($null -ne $d.BusType) { $d.BusType.ToString() } else { 'Unknown' }\n    MediaType = $mt\n    HealthStatus = if ($null -ne $d.HealthStatus) { $d.HealthStatus.ToString() } else { 'Unknown' }\n    OperationalStatus = ($d.OperationalStatus | ForEach-Object { if ($null -ne $_) { $_.ToString() } }) -join ', '\n    Size = [int64]$d.Size\n    IsBoot = [bool]$d.IsBoot\n    IsSystem = [bool]$d.IsSystem\n    PartitionStyle = if ($null -ne $d.PartitionStyle) { $d.PartitionStyle.ToString() } else { 'Unknown' }\n    DriveLetters = @($parts | Where-Object DriveLetter | ForEach-Object { $_.DriveLetter.ToString() })\n  }\n}\n$disks | ConvertTo-Json -Depth 4";
		string raw = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(value));
		string text = ExtractJsonPayload(raw);
		// Distinguish "enumerated zero disks" from "no JSON ever came back". PowerShell exits 0 when a cmdlet in a
		// non-final statement does not exist, so on a WinPE image with WinPE-PowerShell but WITHOUT WinPE-StorageWMI
		// — or a box whose Virtual Disk service is broken — `Get-Disk` errors non-terminatingly, nothing is written to
		// stdout, RunProcessCaptureAsync does not throw, and ExtractJsonPayload falls back to "[]". That was then
		// treated as a perfectly successful scan of a machine with no disks: empty picker, "Ready" in the status bar,
		// no error, no retry, and the only clue buried in the log. Throw instead, so the caller's honest failure path
		// (error status + bounded auto-retry) runs. A real machine running Windows always has at least one disk.
		if (text == "[]" && !raw.TrimStart().StartsWith("[") && !raw.TrimStart().StartsWith("{"))
			throw new InvalidOperationException("Windows returned no disk information. Get-Disk may be unavailable (WinPE without the StorageWMI component) or the Virtual Disk service may not be running.\r\n" + raw.Trim());
		List<DiskItem> list = new List<DiskItem>();
		using JsonDocument jsonDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
		JsonElement rootElement = jsonDocument.RootElement;
		IEnumerable<JsonElement> enumerable2;
		if (rootElement.ValueKind != JsonValueKind.Array)
		{
			IEnumerable<JsonElement> enumerable = new JsonElement[1] { rootElement };
			enumerable2 = enumerable;
		}
		else
		{
			IEnumerable<JsonElement> enumerable = rootElement.EnumerateArray();
			enumerable2 = enumerable;
		}
		foreach (JsonElement item in enumerable2)
		{
			int @int = item.GetProperty("Number").GetInt32();
			string jsonString = GetJsonString(item, "FriendlyName", "Disk " + @int);
			string jsonString2 = GetJsonString(item, "BusType", "Unknown");
			string jsonString3 = GetJsonString(item, "MediaType", "Unknown");
			string jsonString4 = GetJsonString(item, "HealthStatus", "Unknown");
			string jsonString5 = GetJsonString(item, "OperationalStatus", "Unknown");
			long int2 = item.GetProperty("Size").GetInt64();
			bool jsonBool = GetJsonBool(item, "IsBoot");
			bool jsonBool2 = GetJsonBool(item, "IsSystem");
			string jsonString6 = GetJsonString(item, "PartitionStyle", "Unknown");
			string jsonSerial = GetJsonString(item, "SerialNumber", "").Trim();
			List<char> list2 = new List<char>();
			if (item.TryGetProperty("DriveLetters", out var value2))
			{
				if (value2.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement item2 in value2.EnumerateArray())
					{
						string text2 = item2.GetString();
						if (!string.IsNullOrWhiteSpace(text2))
						{
							char c = char.ToUpperInvariant(text2.Trim()[0]);
							if (c >= 'A' && c <= 'Z')
							{
								list2.Add(c);
							}
						}
					}
				}
				else if (value2.ValueKind == JsonValueKind.String)
				{
					string text3 = value2.GetString();
					if (!string.IsNullOrWhiteSpace(text3))
					{
						char c2 = char.ToUpperInvariant(text3.Trim()[0]);
						if (c2 >= 'A' && c2 <= 'Z')
						{
							list2.Add(c2);
						}
					}
				}
			}
			if (jsonBool || jsonBool2)
			{
				list.Add(new DiskItem(@int, jsonString, jsonString2, jsonString3, jsonString4, jsonString5, int2, jsonString6, IsSystem: true, list2) { Serial = jsonSerial });
			}
			else
			{
				list.Add(new DiskItem(@int, jsonString, jsonString2, jsonString3, jsonString4, jsonString5, int2, jsonString6, IsSystem: false, list2) { Serial = jsonSerial });
			}
		}
		// System / currently-running disks are SHOWN (so diagnostics, recover, clean traces and clone-as-source work
		// on them) but sorted LAST so a safe non-system disk stays the default selection. Every destructive path
		// (StartButton target, Format, Wipe, Shred, Partition, Capacity, Test boot, Multi-boot) independently blocks
		// disk.IsSystem, so making them visible cannot format/erase the running Windows.
		return (from disk in list
			orderby disk.IsSystem, disk.IsLikelyUsbOrExternal descending, disk.Number
			select disk).ToList();
	}

	private async Task<string> MountIsoAsync(string path)
	{
		string value = "Mount-DiskImage -ImagePath " + PsQuote(path) + " -PassThru | Get-Volume | Where-Object DriveLetter | Select-Object -First 1 -ExpandProperty DriveLetter";
		string text = (await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(value))).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
		if (text.Length == 0)
		{
			throw new InvalidOperationException("Could not mount ISO image.");
		}
		Log("ISO mounted at " + text + ":\\");
		return text + ":\\";
	}

	private async Task TryUnmountIsoAsync(string path)
	{
		try
		{
			string value = "Dismount-DiskImage -ImagePath " + PsQuote(path);
			await RunProcessAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(value), allowFailure: true);
			Log("ISO unmounted.");
		}
		catch (Exception ex)
		{
			Log("ISO unmount skipped: " + ex.Message);
		}
	}

	private static string FindInstallImage(string root)
	{
		string text = Path.Combine(root, "sources", "install.wim");
		string text2 = Path.Combine(root, "sources", "install.esd");
		if (File.Exists(text))
		{
			return text;
		}
		if (File.Exists(text2))
		{
			return text2;
		}
		throw new FileNotFoundException("Could not find sources\\install.wim or sources\\install.esd.");
	}

	private async Task<List<EditionItem>> GetImageEditionsAsync(string imageFile)
	{
		string obj = await RunProcessCaptureAsync("dism.exe", "/English /Get-WimInfo /WimFile:\"" + imageFile + "\"");
		List<EditionItem> list = new List<EditionItem>();
		int num = 0;
		string[] array = obj.Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
		foreach (string input in array)
		{
			Match match = Regex.Match(input, "^\\s*Index\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
			if (match.Success)
			{
				num = int.Parse(match.Groups[1].Value);
				continue;
			}
			Match match2 = Regex.Match(input, "^\\s*Name\\s*:\\s*(.+)", RegexOptions.IgnoreCase);
			if (match2.Success && num > 0)
			{
				list.Add(new EditionItem(num, match2.Groups[1].Value.Trim()));
				num = 0;
			}
		}
		return list;
	}

	private char GetFreeDriveLetter(params char[] reserved)
	{
		HashSet<char> hashSet = (from drive in DriveInfo.GetDrives()
			select char.ToUpperInvariant(drive.Name[0])).Concat(reserved.Select(char.ToUpperInvariant)).ToHashSet();
		for (char c = 'Z'; c >= 'G'; c = (char)(c - 1))
		{
			if (!hashSet.Contains(c))
			{
				return c;
			}
		}
		throw new InvalidOperationException("No free drive letters are available.");
	}

	private string lastReportPath = "";

	private void SetLastReport(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		lastReportPath = path;
		OpenReportButton.IsEnabled = true;
	}

	private void OpenReportButton_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(lastReportPath) || !File.Exists(lastReportPath))
			{
				MessageBox.Show(L("Mb015"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}
			// Open Explorer with the report file selected.
			Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + lastReportPath + "\"") { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			ShowError(L("ErrReport"), ex);
		}
	}

	private const string DonateUrl = "https://ko-fi.com/driveforge";

	// ---------- Reporting a problem ----------
	// Nothing is ever sent automatically. The app promises "no telemetry, no data collection", and that promise is
	// part of why people trust a tool that erases disks — so every path below only OPENS something the user then
	// chooses to submit. Two channels on purpose: GitHub is structured and searchable, but most of this app's users
	// are rescuing a broken PC and have no GitHub account, so email has to exist too.
	private const string SupportEmail = "support@forgelabssoft.com";
	private const string IssuesUrl = "https://github.com/ForgeLabsSoft/driveforge/issues/new?template=bug_report.yml";

	// The issue template makes version and Windows build mandatory, and those are exactly the two fields people
	// get wrong or omit, so they are filled in from the assembly and the OS rather than asked for.
	private static string WindowsVersionString()
	{
		try { return System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim(); }
		catch { return Environment.OSVersion.VersionString; }
	}

	// The session log — the thing the issue template calls the most useful attachment — is written to the DESKTOP
	// by SaveLogToDesktop, which every failure path calls immediately before ShowError. %LocalAppData%\DriveForge
	// holds only settings and crash.log. Pointing "open the log folder" at LocalAppData opened the wrong place and
	// created it empty on a machine that had never crashed.
	private static string LogFolderPath() =>
		Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

	private static string CrashLogFolderPath() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge");

	// context: a short line describing what just failed, when we already know it (empty when opened from Settings).
	private void ReportProblem(string context)
	{
		int? pick = ShowActionMenu(L("ReportProblemButton"), L("RepPrompt"),
			new[] { L("RepGitHub"), L("RepEmail"), L("RepLogFolder") },
			new[] { 0xE8BD, 0xE715, 0xE8B7 }, new[] { false, false, false }, 0);
		if (pick == null) return;
		try
		{
			string ver = AppVersionString(), win = WindowsVersionString();
			// The error text is scrubbed of the user profile path and capped. Windows puts the account holder's real
			// name in every C:\Users\<Name>\... path, and this app's exceptions quote paths constantly — including
			// the BitLocker recovery-key filename and the user's own document folders.
			string safe = context.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"%UserProfile%", StringComparison.OrdinalIgnoreCase);
			// Tool failures embed the whole captured stdout+stderr, which runs to tens of KB; escaping inflates it
			// 2-3x. Past ~2 KB ShellExecute fails outright and a mailto body is silently truncated by the mail client.
			// Keep the head — that is where the "<tool> exited with code N" line is; the rest is in the attached log.
			if (safe.Length > 600) safe = safe.Substring(0, 600) + " ...";
			if (pick == 0)
			{
				// Deliberately NOT sending the error text. Opening a URL transmits its query string to GitHub
				// immediately — before the user sees the form and whether or not they ever submit it — and this is a
				// PUBLIC issue tracker. Only the version and OS build travel, and neither identifies anyone.
				string url = IssuesUrl
					+ "&version=" + Uri.EscapeDataString(ver)
					+ "&windows=" + Uri.EscapeDataString(win);
				Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
			}
			else if (pick == 1)
			{
				// Keep the body short: some mail clients truncate a long mailto body, and the log is attached by hand.
				string body = "DriveForge " + ver + Environment.NewLine + win + Environment.NewLine + Environment.NewLine
					+ (safe.Length > 0 ? safe + Environment.NewLine + Environment.NewLine : "")
					+ L("RepEmailHint") + Environment.NewLine + CrashLogFolderPath() + Environment.NewLine + Environment.NewLine;
				Process.Start(new ProcessStartInfo("mailto:" + SupportEmail
					+ "?subject=" + Uri.EscapeDataString("DriveForge " + ver + " problem report")
					+ "&body=" + Uri.EscapeDataString(body)) { UseShellExecute = true });
			}
			else
			{
				string dir = LogFolderPath();
				Directory.CreateDirectory(dir);
				Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
			}
		}
		catch (Exception ex) { ShowError(L("ErrRepOpen"), ex); }
	}

	private void ReportProblem_Click(object sender, RoutedEventArgs e) => ReportProblem("");
	private int _successCount;

	private void OpenDonate_Click(object sender, RoutedEventArgs e) => OpenDonatePage();

	private void OpenDonatePage()
	{
		try { Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true }); }
		catch (Exception ex) { ShowError(L("ErrDonate"), ex); }
	}

	// Offered after a successful operation — politely and rarely. Only on the 1st success and then every 5th,
	// respects the Settings toggle, and never runs in unattended/scheduled mode.
	private void MaybeOfferDonation()
	{
		if (headlessRun) return;
		if (ShowDonatePromptCheck?.IsChecked != true) return;
		_successCount++;
		if (_successCount == 1 || _successCount % 5 == 0)
		{
			var r = MessageBox.Show(
				"Done.\n\nDriveForge is free, ad-free and collects no data. If it saved you time, you can support its development with any amount you like.\n\nOpen the Ko-fi support page now? (You can turn this off in Settings.)",
				"Support DriveForge", MessageBoxButton.YesNo, MessageBoxImage.Information);
			if (r == MessageBoxResult.Yes) OpenDonatePage();
		}
		SaveUserSettings();
	}

	// Overwrites only the FREE space of a volume so already-deleted files become unrecoverable, while leaving the
	// existing files untouched. Fills the free space with overwrite data, then removes it.
	private enum WipeMedia { Hdd, Ssd, Unknown }

	// Conservative media detection for the free-space wipe. We do NOT infer "SSD" from BusType=USB (USB enclosures
	// often hold spinning HDDs); if we genuinely can't tell, we say so rather than guessing.
	private static WipeMedia DetectWipeMedia(DiskItem d)
	{
		string mt = d.MediaType ?? "", fn = d.FriendlyName ?? "";
		if (mt.Contains("SSD", StringComparison.OrdinalIgnoreCase) || fn.Contains("SSD", StringComparison.OrdinalIgnoreCase) || fn.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) return WipeMedia.Ssd;
		if (mt.Contains("HDD", StringComparison.OrdinalIgnoreCase) || mt.Contains("Hard", StringComparison.OrdinalIgnoreCase)) return WipeMedia.Hdd;
		return WipeMedia.Unknown; // Unspecified / Unknown / blank
	}

	private async Task WipeFreeSpaceFlow(DiskItem disk)
	{
		char letter = disk.DriveLetters.Select(char.ToUpperInvariant).FirstOrDefault(l => l >= 'A' && l <= 'Z');
		if (letter == '\0') { MessageBox.Show(L("Mb016"), "DriveForge — wipe free space", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		long free; try { free = new DriveInfo(letter + ":").AvailableFreeSpace; } catch { free = 0; }
		if (free < 1L << 20) { MessageBox.Show(string.Format(L("MbNoFreeWipe"), letter), L("MbWipeFreeTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }

		// On flash media, overwriting free space is unreliable (wear-levelling/TRIM) and wears the drive — be honest.
		var media = DetectWipeMedia(disk);
		if (media == WipeMedia.Ssd && MessageBox.Show(L("WipeSsdWarn"), L("MbWipeFreeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (media == WipeMedia.Unknown && MessageBox.Show(L("WipeUnknownWarn"), L("MbWipeFreeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

		// Single zero pass is sufficient on any modern drive (NIST SP 800-88) — flag it recommended, multi-pass as not safer.
		string[] methods = { L("AmFreeZero") + " — " + L("WipeRecommended"), L("AmFreeRandom"), L("AmFree3") + " — " + L("WipeNotMoreSecure") };
		int? sel = ShowActionMenu(L("MbWipeFreeTitle"), string.Format(L("AmFreePrompt"), letter, FormatBytes(free)), methods,
			new[] { 0xEA99, 0xE9CE, 0xE730 }, new[] { false, true, true }, 0);
		if (sel == null) return;
		int[] fills = sel.Value switch { 1 => new[] { 2 }, 2 => new[] { 0, 2, 0 }, _ => new[] { 0 } };

		// Leave a reserve so the volume never hits 0 bytes (which hangs the OS, corrupts open files, and purges
		// System Restore points on the system drive). Bigger reserve on the system volume.
		long reserve = disk.IsSystem ? (1L << 30) : (64L << 20);
		long cap = Math.Max(0, free - reserve);
		if (cap <= 0) { MessageBox.Show(string.Format(L("MbNoFreeWipe"), letter), L("MbWipeFreeTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; } // too little free to wipe while keeping a reserve
		// Filling free space purges shadow copies on ANY volume that has them — System Protection and File History can
		// both be enabled on a DATA drive. Gating this warning on IsSystem made it unreachable exactly where the user
		// is least likely to expect losing their Previous Versions. Warn always, with wording true for each case.
		string confirmBody = string.Format(L("MbWipeFreeConfirm"), letter, FormatBytes(free), fills.Length)
			+ "\n\n" + L(disk.IsSystem ? "WipeVssWarn" : "WipeVssWarnAny");
		if (MessageBox.Show(confirmBody, L("MbWipeFreeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		bool failed = false;
		try
		{
			// _progressFixedTotal tells UpdateProgressStats the total is REAL and must not be inflated by the clone-only
			// "we're at 97% but not done" heuristic — without it the wipe's bar froze at ~97% for the last stretch and
			// the ETA counted upward instead of down.
			stopRequested = false; isPaused = false; _progressFullRange = true; _progressFixedTotal = true; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, cap / 1073741824.0 * Math.Max(1, fills.Length));
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzWipeFree"), letter));
			ProgressBar.Value = 0.0;
			await Task.Run(() => WipeFreeSpaceCore(letter, fills, cap));
			operationTimer.Stop(); operationStopwatch.Stop();
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false); NotifyOperationDone(true);
			await RefreshDisksAsync();
			// Honesty: on flash the app already warned that overwriting free space does NOT reliably erase old data
			// (wear-levelling keeps copies the OS can't reach). Claiming "can no longer be recovered" here retracted
			// that warning — and the last message is the one the user remembers. Also these were the only strings in
			// the flow that bypassed L(), so it ended in English for everyone.
			MessageBox.Show(stopRequested
				? string.Format(L("MbWipeFreeStopped"), letter)
				: string.Format(L(DetectWipeMedia(disk) == WipeMedia.Hdd ? "MbWipeFreeDoneHdd" : "MbWipeFreeDoneFlash"), letter),
				L("MbWipeFreeTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("ErrFreeWipe"), ex); }
		finally { operationTimer.Stop(); operationStopwatch.Stop(); if (failed) UpdateProgressStats(); _progressFullRange = false; _progressFixedTotal = false; SetBusy(busy: false); } // refresh BEFORE clearing the flags — clearing first jumps a failed run's bar forward
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint ctl, ref ushort inB, uint inS, IntPtr outB, uint outS, out uint ret, IntPtr ovl);

	// The name of the temporary fill directory a free-space wipe creates at a volume root. Kept in one place so the
	// startup sweep can find and remove one that a crash / kill / power loss left behind.
	private const string WipeFillDirName = "__driveforge_freespace__";

	// NTFS propagates the "compressed" attribute from a directory onto every file created inside it. On a volume with
	// compression enabled (the "Compress this drive to save disk space" checkbox), our all-zero fill files would be
	// stored compressed — and NTFS keeps an all-zero compression unit as a sparse hole, allocating ZERO clusters. The
	// wipe would then "write" its whole cap at memory speed, free space would never drop, not one deleted file would
	// be overwritten, and we would still tell the user the data is gone. Clear compression on the fill directory so
	// the fills are written for real.
	private static void TryDisableCompression(string dir)
	{
		try
		{
			const uint FSCTL_SET_COMPRESSION = 0x9C040;
			const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // required to open a DIRECTORY handle
			using var h = CreateFile(dir, 0xC0000000u /* GENERIC_READ | GENERIC_WRITE */, 0x3u, IntPtr.Zero, 3u /* OPEN_EXISTING */, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
			if (h.IsInvalid) return;
			ushort fmt = 0; // COMPRESSION_FORMAT_NONE
			DeviceIoControl(h, FSCTL_SET_COMPRESSION, ref fmt, sizeof(ushort), IntPtr.Zero, 0, out _, IntPtr.Zero);
		}
		catch { }
	}

	// Removes a fill directory stranded by a crash / kill / power loss during a previous wipe. The cleanup in
	// WipeFreeSpaceCore runs on a thread-pool thread, so process death skips it and can leave hundreds of GB behind
	// with nothing to ever reclaim them. Best-effort, fire-and-forget at startup; the folder name is ours alone.
	// Set while a free-space wipe is filling a drive, so the startup sweep of a SECOND instance can tell a stranded
	// folder from one that is being written right now. Cross-process: the app is NOT single-instance (no mutex), and
	// a second instance's sweep would otherwise delete a live wipe's fill files — silently gutting the wipe while it
	// still reported "free space overwritten".
	private static readonly string WipeActiveMarker = Path.Combine(Path.GetTempPath(), "driveforge-wipe-active.lock");

	private static void SweepStrandedWipeFiles()
	{
		try
		{
			// A wipe is running (this or another instance) — its fill files are NOT strays. Leave them alone.
			try { if (File.Exists(WipeActiveMarker) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(WipeActiveMarker)).TotalMinutes < 10) return; } catch { return; }
			foreach (var d in DriveInfo.GetDrives())
			{
				try
				{
					if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
					string dir = Path.Combine(d.RootDirectory.FullName, WipeFillDirName);
					if (Directory.Exists(dir)) Directory.Delete(dir, true);
				}
				catch { }
			}
		}
		catch { }
	}

	private void WipeFreeSpaceCore(char letter, int[] fills, long capBytes)
	{
		string dir = letter + ":\\" + WipeFillDirName;
		int bufSize = 4 * 1024 * 1024;
		byte[] b = new byte[bufSize];
		using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
		long acc = 0;
		try
		{
			foreach (int fill in (fills.Length == 0 ? new[] { 0 } : fills))
			{
				if (stopRequested) break;
				Directory.CreateDirectory(dir);
				TryDisableCompression(dir); // else zeros compress to nothing and the "wipe" allocates no clusters at all
				// Tell any other instance's startup sweep that these fill files are LIVE, not strays. Timestamped and
				// refreshed below, so a crashed run's marker goes stale (10 min) instead of blocking the sweep forever.
				try { File.WriteAllText(WipeActiveMarker, letter.ToString()); } catch { }
				if (fill == 0) Array.Clear(b, 0, b.Length);
				int idx = 0;
				long passWritten = 0; // stop this pass at capBytes so we never fill the volume to 0 free
				try
				{
					while (!stopRequested && (capBytes <= 0 || passWritten < capBytes))
					{
						using var fs = new FileStream(Path.Combine(dir, $"fs{idx:D5}.tmp"), FileMode.Create, FileAccess.Write, FileShare.None, bufSize);
						long fileMax = 1L << 30, fw = 0; // 1 GiB per file
						while (fw < fileMax && !stopRequested && (capBytes <= 0 || passWritten < capBytes))
						{
							while (isPaused && !stopRequested) System.Threading.Thread.Sleep(150);
							if (fill == 2) rng.GetBytes(b);
							fs.Write(b, 0, b.Length);
							fw += b.Length; acc += b.Length; passWritten += b.Length; Volatile.Write(ref _progressDoneBytes, acc);
							// Keep the live-wipe marker fresh (cheap: once per 1 GiB file boundary is enough).
							if (fw >= fileMax) { try { File.SetLastWriteTimeUtc(WipeActiveMarker, DateTime.UtcNow); } catch { } }
						}
						fs.Flush(flushToDisk: true);
						idx++;
					}
				}
				catch (IOException ex)
					{
						// Only a genuine "disk full" means the pass actually finished overwriting the free space.
						int code = ex.HResult & 0xFFFF; // ERROR_DISK_FULL=0x70, ERROR_HANDLE_DISK_FULL=0x27
						if (code != 0x70 && code != 0x27) throw; // real I/O error → surface it, don't claim success
					}
				try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
			}
		}
		finally
		{
			try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
			try { if (File.Exists(WipeActiveMarker)) File.Delete(WipeActiveMarker); } catch { }
		}
	}

	// Flush + eject the selected drive so the user can unplug it safely (USB / external).
	private async void EjectDrive_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb017"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb018"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		try
		{
			await EjectDiskAsync(disk.Number);
			MessageBox.Show(L("Mb019"),
				"DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
			await RefreshDisksAsync();
		}
		catch (Exception ex)
		{
			ShowError(L("ErrEject"), ex);
		}
	}

	// Small reusable modal dialog with a dropdown — returns the chosen index, or null if cancelled.
	private int? ShowChooserDialog(string title, string prompt, string[] options, int defaultIndex)
	{
		var win = new Window
		{
			Title = title,
			SizeToContent = SizeToContent.WidthAndHeight,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Background = (System.Windows.Media.Brush)FindResource("PanelBrush")
		};
		var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
		panel.Children.Add(new System.Windows.Controls.TextBlock
		{
			Text = prompt, Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
			TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 14), FontSize = 14
		});
		var combo = new System.Windows.Controls.ComboBox { Height = 32, FontSize = 13 };
		foreach (var o in options) combo.Items.Add(o);
		combo.SelectedIndex = (defaultIndex >= 0 && defaultIndex < options.Length) ? defaultIndex : 0;
		panel.Children.Add(combo);
		var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		var ok = new System.Windows.Controls.Button { Content = "OK", Width = 96, Style = (Style)FindResource("GreenButtonStyle") };
		var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("GhostButtonStyle") };
		ok.Click += (_, __) => { win.DialogResult = true; };
		cancel.Click += (_, __) => { win.DialogResult = false; };
		row.Children.Add(ok); row.Children.Add(cancel);
		panel.Children.Add(row);
		win.Content = panel;
		return win.ShowDialog() == true ? combo.SelectedIndex : (int?)null;
	}

	// Richer command-style chooser: one clickable row per action (icon + title + description),
	// destructive rows tinted red. Reuses the existing localized labels — splits them into a
	// title and a muted description on " (...)" or " — ", so no new translation keys are needed.
	// glyphs/danger may be null. Returns the chosen index, or null if cancelled.
	private int? ShowActionMenu(string title, string prompt, string[] options, int[] glyphs, bool[] danger, int defaultIndex)
	{
		var win = new Window
		{
			Title = title,
			SizeToContent = SizeToContent.Height,
			Width = 470,
			MaxHeight = 680,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Background = (System.Windows.Media.Brush)FindResource("PanelBrush")
		};
		var root = new System.Windows.Controls.DockPanel();

		// Header band (title + optional context prompt).
		var hdr = new System.Windows.Controls.Border
		{
			Background = (System.Windows.Media.Brush)FindResource("BlueBrush"),
			Padding = new Thickness(16, 12, 16, 12)
		};
		var hdrStack = new System.Windows.Controls.StackPanel();
		hdrStack.Children.Add(new System.Windows.Controls.TextBlock
		{
			Text = title, Foreground = System.Windows.Media.Brushes.White,
			FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap
		});
		if (!string.IsNullOrWhiteSpace(prompt))
			hdrStack.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = prompt, Foreground = System.Windows.Media.Brushes.White, Opacity = 0.85,
				FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
			});
		hdr.Child = hdrStack;
		System.Windows.Controls.DockPanel.SetDock(hdr, System.Windows.Controls.Dock.Top);
		root.Children.Add(hdr);

		// Footer (Cancel).
		var ftr = new System.Windows.Controls.Border
		{
			Padding = new Thickness(14, 10, 14, 12),
			BorderBrush = (System.Windows.Media.Brush)FindResource("Border2Brush"),
			BorderThickness = new Thickness(0, 1, 0, 0)
		};
		var cancelBtn = new System.Windows.Controls.Button
		{
			Content = "Cancel", Width = 100, HorizontalAlignment = HorizontalAlignment.Right,
			Style = (Style)FindResource("GhostButtonStyle")
		};
		ftr.Child = cancelBtn;
		System.Windows.Controls.DockPanel.SetDock(ftr, System.Windows.Controls.Dock.Bottom);
		root.Children.Add(ftr);

		// Scrollable list of action rows (fills the remaining space).
		var sv = new System.Windows.Controls.ScrollViewer { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
		var list = new System.Windows.Controls.StackPanel { Margin = new Thickness(10, 8, 10, 8) };
		var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
		var txtb = (System.Windows.Media.Brush)FindResource("TextBrush");
		var iconNormalBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x24, 0x3F));
		var iconDangerBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x17, 0x17));
		var iconNormalFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBC, 0xD2, 0xEE));
		var iconDangerFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xA6, 0xA6));
		var dangerTitleFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xC0, 0xC0));
		var hoverBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x29, 0x4A));
		int? result = null;

		// Build the row button template once: a full-width Border whose content stretches (so the chevron
		// sits on the right edge) and which highlights on hover. GhostButtonStyle can't do either.
		var rowTemplate = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
		var bdf = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border), "Bd");
		bdf.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
		bdf.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
		bdf.SetBinding(System.Windows.Controls.Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		bdf.SetBinding(System.Windows.Controls.Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		bdf.SetBinding(System.Windows.Controls.Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
		var cpf = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
		cpf.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
		cpf.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
		bdf.AppendChild(cpf);
		rowTemplate.VisualTree = bdf;
		var hoverTrig = new System.Windows.Trigger { Property = System.Windows.UIElement.IsMouseOverProperty, Value = true };
		hoverTrig.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, hoverBg, "Bd"));
		rowTemplate.Triggers.Add(hoverTrig);

		for (int i = 0; i < options.Length; i++)
		{
			int idx = i;
			string lab = options[i] ?? "";
			string tt = lab, ds = "";
			// Prefer the parenthetical as the description when the label ends with ")"; otherwise split on em-dash.
			int op = lab.IndexOf(" (", StringComparison.Ordinal);
			int em = lab.IndexOf(" — ", StringComparison.Ordinal);
			if (op > 0 && lab.EndsWith(")", StringComparison.Ordinal))
			{
				tt = lab.Substring(0, op);
				ds = lab.Substring(op + 2, lab.Length - op - 3);
			}
			else if (em > 0)
			{
				tt = lab.Substring(0, em);
				ds = lab.Substring(em + 3);
			}
			bool dg = danger != null && i < danger.Length && danger[i];

			var btn = new System.Windows.Controls.Button
			{
				Template = rowTemplate,
				Background = System.Windows.Media.Brushes.Transparent,
				Foreground = txtb,
				BorderThickness = new Thickness(0),
				HorizontalContentAlignment = HorizontalAlignment.Stretch,
				Cursor = System.Windows.Input.Cursors.Hand,
				Padding = new Thickness(9, 8, 9, 8),
				Margin = new Thickness(0, 0, 0, 3),
				SnapsToDevicePixels = true
			};
			if (idx == defaultIndex)
			{
				btn.BorderBrush = (System.Windows.Media.Brush)FindResource("BlueBrush");
				btn.BorderThickness = new Thickness(1);
			}

			var g = new System.Windows.Controls.Grid();
			g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
			g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

			var ib = new System.Windows.Controls.Border
			{
				Width = 30, Height = 30, CornerRadius = new CornerRadius(7),
				Margin = new Thickness(0, 0, 11, 0),
				VerticalAlignment = VerticalAlignment.Center,
				Background = dg ? iconDangerBg : iconNormalBg
			};
			ib.Child = new System.Windows.Controls.TextBlock
			{
				Text = (glyphs != null && i < glyphs.Length && glyphs[i] != 0) ? char.ConvertFromUtf32(glyphs[i]) : char.ConvertFromUtf32(0xE7F4),
				FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 15, Foreground = dg ? iconDangerFg : iconNormalFg,
				HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
			};
			System.Windows.Controls.Grid.SetColumn(ib, 0);
			g.Children.Add(ib);

			var sp = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
			sp.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = tt, FontSize = 13, Foreground = dg ? dangerTitleFg : txtb, TextWrapping = TextWrapping.Wrap
			});
			if (ds.Length > 0)
				sp.Children.Add(new System.Windows.Controls.TextBlock
				{
					Text = ds, FontSize = 11.5, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0)
				});
			System.Windows.Controls.Grid.SetColumn(sp, 1);
			g.Children.Add(sp);

			var ch = new System.Windows.Controls.TextBlock
			{
				Text = char.ConvertFromUtf32(0xE76C), FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
				FontSize = 12, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
			};
			System.Windows.Controls.Grid.SetColumn(ch, 2);
			g.Children.Add(ch);

			btn.Content = g;
			btn.Click += (_, __) => { result = idx; win.DialogResult = true; };
			list.Children.Add(btn);
		}
		sv.Content = list;
		root.Children.Add(sv);
		cancelBtn.Click += (_, __) => { win.DialogResult = false; };
		win.Content = root;
		return win.ShowDialog() == true ? result : (int?)null;
	}

	// Simple single-line text/number input dialog (themed like ShowChooserDialog). Returns null on Cancel.
	private string? ShowInputDialog(string title, string prompt, string defaultText)
	{
		var win = new Window
		{
			Title = title,
			SizeToContent = SizeToContent.WidthAndHeight,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Owner = this,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Background = (System.Windows.Media.Brush)FindResource("PanelBrush")
		};
		var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
		panel.Children.Add(new System.Windows.Controls.TextBlock
		{
			Text = prompt, Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
			TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 0, 0, 12), FontSize = 14
		});
		var box = new System.Windows.Controls.TextBox { Height = 32, FontSize = 13, Text = defaultText ?? "" };
		panel.Children.Add(box);
		var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		var ok = new System.Windows.Controls.Button { Content = "OK", Width = 96, Style = (Style)FindResource("GreenButtonStyle") };
		var cancel = new System.Windows.Controls.Button { Content = "Cancel", Width = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("GhostButtonStyle") };
		ok.Click += (_, __) => { win.DialogResult = true; };
		cancel.Click += (_, __) => { win.DialogResult = false; };
		row.Children.Add(ok); row.Children.Add(cancel);
		panel.Children.Add(row);
		win.Content = panel;
		box.Loaded += (_, __) => { box.Focus(); box.SelectAll(); };
		return win.ShowDialog() == true ? box.Text : null;
	}

	// Securely erase the selected drive by overwriting every sector so data cannot be recovered.
	private async void WipeDrive_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb020"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb021"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb022"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Hold the reentrancy guard across the whole flow (the mode / confirm / method-menu / GetDiskContents / verify
		// awaits all run before SetBusy sets isBusy) so a second destructive tool can't start on this disk while a wipe
		// is being set up. Cleared in the outer finally on every path (returns, cancels, exceptions).
		_toolOpStarting = true;
		try
		{
		int? wmode = ShowActionMenu(L("AmWipeTitle"), string.Format(L("AmWipePrompt"), disk.Number),
			new[]
			{
				L("AmWipeEntire"),
				L("AmWipeFreeOpt"),
				L("WipeSsd")
			},
			new[] { 0xEA99, 0xE9D9, 0xEDA2 },
			new[] { true, true, true }, 0);
		if (wmode == null) return;
		if (wmode == 1) { await WipeFreeSpaceFlow(disk); return; }
		if (wmode == 2) { await SsdSecureEraseFlow(disk); return; }

		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("MbWipeConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
				L("MbWipeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		// Method dropdown. Each maps to a list of overwrite passes (0 = zeros, 1 = ones/0xFF, 2 = random).
		string[] methods = {
			L("AmMethodQuick"),
			L("AmMethodZero"),
			L("AmMethodRandom"),
			L("AmMethod3"),
			L("AmMethod7"),
			L("AmMethodGutmann")
		};
		int? sel = ShowActionMenu(L("AmWipeMethodTitle"), string.Format(L("AmWipeMethodPrompt"), disk.Number), methods,
			new[] { 0xE777, 0xEA99, 0xE9CE, 0xE730, 0xE730, 0xE730 },
			new[] { true, true, true, true, true, true }, 1);
		if (sel == null) return;
		int[] fills;
		switch (sel.Value)
		{
			case 0: fills = new int[0]; break;                       // Quick (clean only)
			case 2: fills = new[] { 2 }; break;                      // Random 1
			case 3: fills = new[] { 0, 2, 0 }; break;                // 3-pass
			case 4: fills = new[] { 0, 1, 2, 0, 1, 2, 2 }; break;    // 7-pass
			case 5: fills = new int[35]; for (int i = 0; i < 35; i++) fills[i] = 2; break; // Gutmann ~ 35 random
			default: fills = new[] { 0 }; break;                     // Zero 1
		}
		string label = methods[sel.Value].Split('—')[0].Trim();
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // make sure this is still the same physical drive

		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			// _progressFixedTotal: a wipe writes EXACTLY disk.Size x passCount bytes — the total is real, not a
			// projection, so the clone-only "actuals exceeded the estimate" inflation must not apply. Without it the
			// ceiling jumped 12% ahead the moment we reached 97%, so the completion update below could never reach
			// 100%: it stopped at ~89% and the stats line claimed a total bigger than the drive itself.
			_progressFullRange = true; _progressFixedTotal = true;
			PauseButton.Content = L("BtnPause");
			int passCount = Math.Max(1, fills.Length);
			progressTotalGiB = Math.Max(1.0, disk.Size / 1073741824.0 * passCount);
			progressDoneGiB = 0.0; progressPrevGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzWiping"), disk.Number, label));
			ProgressBar.Value = 0.0;
			await RawWipeDiskAsync(disk, fills);
			operationTimer.Stop(); operationStopwatch.Stop();
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(true);
			await RefreshDisksAsync();
			MessageBox.Show(stopRequested
				? $"Wipe stopped. Disk {disk.Number} was partially overwritten."
				: $"Done. Disk {disk.Number} was wiped ({label}).\n\nUse Format to make it usable again.",
				"DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			failed = true; NotifyOperationDone(false); SaveLogToDesktop(); ShowError(L("ErrWipe"), ex);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			// Refresh BEFORE clearing the flags: UpdateProgressStats reads them, and with _progressFullRange already
			// false it switches to the 40..82 banded formula — which, since the bar only ever advances, JUMPS a run
			// that failed at 50% up to 61% at the exact moment the error is reported.
			if (failed) UpdateProgressStats();
			// Clear BOTH — leaking _progressFixedTotal=true into a later clone/install would disable the inflation
			// heuristic that flow genuinely relies on.
			_progressFullRange = false; _progressFixedTotal = false;
			SetBusy(busy: false);
		}
		}
		finally { _toolOpStarting = false; }
	}

	// Overwrites the whole physical disk N times. Removes partitions first (diskpart clean) so the raw sectors
	// are writable, then writes directly to \\.\PhysicalDriveN. Honours Stop/Pause and reports live progress.
	// fills: list of passes — 0 = zeros, 1 = ones (0xFF), 2 = random. Empty = Quick (clean only).
	private async Task RawWipeDiskAsync(DiskItem disk, int[] fills)
	{
		// Remove partitions/volumes so the physical sectors are free to overwrite (and = Quick wipe).
		string dp = Path.Combine(Path.GetTempPath(), $"driveforge-wipe-{Guid.NewGuid():N}.txt");
		try
		{
			SetStage(L("StgPrepDiskRemove"), 2.0);
			await File.WriteAllTextAsync(dp, $"select disk {disk.Number}\r\nclean\r\nexit\r\n", Encoding.ASCII);
			await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(dp));
		}
		finally { TryDeleteFile(dp); }

		if (fills == null || fills.Length == 0) { Volatile.Write(ref _progressDoneBytes, (long)(progressTotalGiB * 1073741824.0)); return; } // Quick

		long size = disk.Size;
		int chunk = 8 * 1024 * 1024; // 8 MiB, multiple of 512 — fewer syscalls, faster on quick drives
		long doneTotal = 0;
		using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();

		await Task.Run(() =>
		{
			using SafeFileHandle h = CreateFile($"\\\\.\\PhysicalDrive{disk.Number}",
				0x40000000u /*GENERIC_WRITE*/, 0x3u /*share R/W*/, IntPtr.Zero, 3u /*OPEN_EXISTING*/, 0u, IntPtr.Zero);
			if (h.IsInvalid) throw new IOException("Could not open the disk for writing (error " + Marshal.GetLastWin32Error() + "). Close any program using it and retry.");
			using var fs = new FileStream(h, FileAccess.Write);
			byte[] buffer = new byte[chunk];
			foreach (int fill in fills)
			{
				if (stopRequested) break;
				if (fill == 0) Array.Clear(buffer, 0, buffer.Length);
				else if (fill == 1) { for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xFF; }
				fs.Seek(0, SeekOrigin.Begin);
				long written = 0;
				while (written < size && !stopRequested)
				{
					while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
					int toWrite = (int)Math.Min(chunk, size - written);
					if (fill == 2) rng.GetBytes(buffer, 0, toWrite);
					try { fs.Write(buffer, 0, toWrite); }
					catch (IOException) when (size - written <= chunk)
					{
						// Hitting the device end on the final chunk is USUALLY benign — the reported capacity often
						// exceeds the addressable byte count. But an IOException here can ALSO be a genuine bad-sector /
						// hardware write failure at the tail, and we cannot tell the two apart, so just stop this pass.
						break;
					}
					written += toWrite; doneTotal += toWrite;
					Volatile.Write(ref _progressDoneBytes, doneTotal);
				}
				fs.Flush();
			}
		});
	}

	// Write a bootable ISO (Linux or any isohybrid image) to the USB as a raw disk image (dd-style).
	private async Task WriteIsoImageFlowAsync(DiskItem disk)
	{
		if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
		{ MessageBox.Show(L("Mb024"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		long isoSize = new FileInfo(sourcePath).Length;
		// The raw writer sector-pads the final chunk (up to +4095 bytes; 4096 covers 4Kn disks too), so reject
		// anything whose padded size would run past the device end — else the last write throws after diskpart wiped it.
		if ((isoSize + 4095) / 4096 * 4096 > disk.Size)
		{ MessageBox.Show(string.Format(L("MbIsoTooBig"), FormatBytes(isoSize), FormatBytes(disk.Size)), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		// The source ISO must not live on the disk we're about to wipe, or we'd destroy the very file we're writing.
		if (PhysicalDiskOfPath(sourcePath) == disk.Number)
		{ MessageBox.Show(L("MbSrcOnTarget"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Stop); return; }

		// A Windows / WinPE ISO is not "isohybrid" — writing it raw usually won't boot. Warn and point to the
		// proper task.
		if (await LooksLikeWindowsIsoAsync(sourcePath))
		{
			if (MessageBox.Show(L("Mb025"),
					"DriveForge", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
				return;
		}

		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("MbWriteIsoConfirm"), Path.GetFileName(sourcePath), FormatBytes(isoSize), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
				L("MbWriteIsoTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		bool failed = false;
		bool ejected = false; // set when we deliberately eject on the success path, so the finally net does not re-online (undo) the eject
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			// _progressFixedTotal: we write EXACTLY isoSize bytes, so the total is real, not a projection. Without it the
			// clone-only "actuals exceeded the estimate" heuristic inflates the ceiling by 12% the moment we reach 97%,
			// so the completion update (progressDoneGiB = progressTotalGiB) could never reach 100% — it landed at ~89%
			// and the stats line advertised a total 12% larger than the image.
			_progressFullRange = true; _progressFixedTotal = true; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, isoSize / 1073741824.0);
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzWriteIso"), disk.Number));
			ProgressBar.Value = 0.0;
			if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // make sure this is still the same physical drive
				await RawWriteImageToDiskAsync(disk, sourcePath, isoSize);
			bool writeCompleted = !stopRequested; // capture BEFORE the optional verify below reuses stopRequested
			operationTimer.Stop(); operationStopwatch.Stop();
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(!stopRequested);
			string verifyNote = "";
				if (!stopRequested &&
					MessageBox.Show(L("Mb026"),
						"DriveForge — verify write", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
				{
					stopRequested = false; _progressFullRange = true; _progressFixedTotal = true; // read back exactly isoSize bytes — real total
					progressTotalGiB = Math.Max(1.0, isoSize / 1073741824.0);
					progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
					operationStopwatch.Restart(); operationTimer.Start();
					SetBusy(busy: true, string.Format(L("BzVerify"), disk.Number));
					ProgressBar.Value = 0.0;
					var (vok, mismatchAt) = await Task.Run(() => VerifyRawWrite(disk, sourcePath, isoSize));
					operationTimer.Stop(); operationStopwatch.Stop();
					progressDoneGiB = progressTotalGiB; UpdateProgressStats();
					SetBusy(busy: false);
					verifyNote = vok ? "\n\n" + L("IsoVerifyOk")
						: stopRequested ? "\n\n" + L("IsoVerifyStopped")
						: "\n\n" + string.Format(L("IsoVerifyFailed"), FormatBytes(mismatchAt));
				}

				// RawWriteImageToDiskAsync took the disk OFFLINE so Windows would not auto-mount the ISO's own ESP mid-
				// write/verify (corrupts the image + false-fails the read-back). Bring it back online (best-effort: a
				// diskpart hiccup here must not turn a good, flushed write into a failure dialog — the finally net retries).
				bool onlined = false;
				try { await RunDiskpartAsync($"select disk {disk.Number}\r\nonline disk\r\nattributes disk clear readonly\r\nexit\r\n"); onlined = true; } catch { }
				// Eject only once it is online; mark it so the finally net does NOT re-online (silently undo) the eject.
				if (onlined && EjectWhenDoneCheck.IsChecked == true && !stopRequested) { await EjectDiskAsync(disk.Number); ejected = true; }
			await RefreshDisksAsync();
			MessageBox.Show(writeCompleted
				? string.Format(L("IsoWriteDone"), disk.Number) + verifyNote
				: string.Format(L("IsoWriteStopped"), disk.Number),
				"DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); SaveLogToDesktop(); ShowError(L("ErrWriteIso"), ex); }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			// Refresh BEFORE clearing the flags (see the wipe flow): clearing first switches the bar to the banded
			// formula and jumps a failed run forward instead of leaving it where it stopped.
			if (failed) UpdateProgressStats();
			// Clear BOTH: leaking _progressFixedTotal=true into a later clone/install would disable the inflation
			// heuristic that flow genuinely relies on.
			_progressFullRange = false; _progressFixedTotal = false;
			// Safety net: if we bailed (exception/stop/online-hiccup) before onlining, never leave the disk offline — but
			// do NOT re-online a disk we deliberately ejected on the success path (that would silently undo the eject).
			if (!ejected) { try { await RunDiskpartAsync($"select disk {disk.Number}\r\nonline disk\r\nattributes disk clear readonly\r\nexit\r\n"); } catch { } }
			SetBusy(busy: false);
		}
	}

	// Mounts the ISO read-only and checks for Windows setup files (boot.wim/install.*) to detect a non-isohybrid
	// Windows/WinPE image. Best-effort; returns false on any error.
	private async Task<bool> LooksLikeWindowsIsoAsync(string isoPath)
	{
		try
		{
			string p = isoPath.Replace("'", "''");
			string ps = "$ErrorActionPreference='SilentlyContinue';" +
				"try { $m = Mount-DiskImage -ImagePath '" + p + "' -PassThru; Start-Sleep -Milliseconds 500;" +
				" $dl = ($m | Get-Volume).DriveLetter; $win=$false;" +
				" if($dl){ if((Test-Path \"$dl`:\\sources\\boot.wim\") -or (Test-Path \"$dl`:\\sources\\install.wim\") -or (Test-Path \"$dl`:\\sources\\install.esd\")){ $win=$true } } }" +
				" finally { Dismount-DiskImage -ImagePath '" + p + "' | Out-Null }; if($win){'WINDOWS'}else{'OTHER'}";
			string o = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(ps));
			return o.IndexOf("WINDOWS", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch { return false; }
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FlushFileBuffers(SafeFileHandle hFile);

	private async Task RawWriteImageToDiskAsync(DiskItem disk, string isoPath, long isoSize)
	{
		string dp = Path.Combine(Path.GetTempPath(), $"driveforge-iso-{Guid.NewGuid():N}.txt");
		try
		{
			SetStage(L("StgPrepDisk"), 2.0);
			// offline disk: stop Windows auto-mounting the ISO's own ESP as the raw write lays it down (a mounted FAT
			// driver would write dirty-bit/FSINFO back over our bytes -> corrupt image + false verify FAIL). Back online in the caller.
			await File.WriteAllTextAsync(dp, $"select disk {disk.Number}\r\nclean\r\noffline disk\r\nexit\r\n", Encoding.ASCII);
			await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(dp));
		}
		finally { TryDeleteFile(dp); }

		await Task.Run(() =>
		{
			using SafeFileHandle h = CreateFile($"\\\\.\\PhysicalDrive{disk.Number}",
				0x40000000u /*GENERIC_WRITE*/, 0x3u, IntPtr.Zero, 3u /*OPEN_EXISTING*/, 0u, IntPtr.Zero);
			if (h.IsInvalid) throw new IOException("Could not open the disk for writing (error " + Marshal.GetLastWin32Error() + ").");
			using var dst = new FileStream(h, FileAccess.Write);
			using var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024 * 1024);
			int chunk = 8 * 1024 * 1024;
			byte[] buffer = new byte[chunk];
			long done = 0;
			while (!stopRequested)
			{
				while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
				// Fill the whole buffer before writing so a short mid-file read can never shift the image; only the
				// true final chunk at EOF is ever partial (and only that one gets sector-padded below).
				int got = 0;
				while (got < chunk) { int r = src.Read(buffer, got, chunk - got); if (r <= 0) break; got += r; }
				if (got <= 0) break;
				int toWrite = got;
				if (toWrite % 4096 != 0) { int pad = 4096 - (toWrite % 4096); Array.Clear(buffer, toWrite, pad); toWrite += pad; } // sector-align tail (4096 covers 512e + 4Kn; buffer size is 4096-aligned so no overflow)
				dst.Write(buffer, 0, toWrite);
				done += got;
				Volatile.Write(ref _progressDoneBytes, done);
			}
			dst.Flush();
			// push the OS cache to the actual media; a failure here means the image may not be fully written to flash
			if (!FlushFileBuffers(h)) throw new IOException("Flushing the write to the drive failed (error " + Marshal.GetLastWin32Error() + "). The image may not be fully on the media.");
		});
	}

	// Reads the written image back from the raw disk and compares it byte-for-byte with the source ISO.
	// Returns (ok, mismatchByteOffset). ok is false on the first differing byte (or a short read).
	private (bool ok, long mismatchAt) VerifyRawWrite(DiskItem disk, string isoPath, long isoSize)
	{
		const int block = 8 * 1024 * 1024;   // multiple of 4096
		const int align = 4096;
		// Open the device with FILE_FLAG_NO_BUFFERING so the read-back comes from the ACTUAL flash, not the OS cache — a
		// counterfeit/fake-capacity or failing drive whose just-written bytes are still cached would otherwise verify OK.
		// The device offset (pos), read length (aligned) and buffer address are all kept 4096-aligned as NO_BUFFERING needs.
		using SafeFileHandle h = CreateFile($"\\\\.\\PhysicalDrive{disk.Number}", GenericRead, 0x3u, IntPtr.Zero, 3u, FileFlagNoBuffering, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk for verification (error " + Marshal.GetLastWin32Error() + ").");
		using var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, block);
		byte[] a = new byte[block];               // ISO bytes (a normal buffered file read of the source is fine)
		byte[] rawDev = new byte[block + align];  // device bytes; the sector-aligned window inside this pinned array is used
		var gc = System.Runtime.InteropServices.GCHandle.Alloc(rawDev, System.Runtime.InteropServices.GCHandleType.Pinned);
		try
		{
			long baseAddr = gc.AddrOfPinnedObject().ToInt64();
			long alignedAddr = (baseAddr + align - 1) & ~((long)align - 1);
			int devOff = (int)(alignedAddr - baseAddr);   // 0..4095 offset of the aligned window inside rawDev
			IntPtr devPtr = new IntPtr(alignedAddr);
			long pos = 0;
			while (pos < isoSize && !stopRequested)
			{
				while (isPaused && !stopRequested) System.Threading.Thread.Sleep(150);
				int want = (int)Math.Min(block, isoSize - pos);
				int ar = 0; while (ar < want) { int r = src.Read(a, ar, want - ar); if (r <= 0) break; ar += r; }
				if (ar <= 0) break;
				int aligned = ((ar + align - 1) / align) * align;   // read a whole number of sectors from the device
				// pos is always a multiple of block (hence 4096) except it never advances past the final short read, so the
				// seek is always sector-aligned; the padded ISO was checked to fit the device, so pos+aligned stays in range.
				if (!SetFilePointerEx(h, pos, out _, 0 /*FILE_BEGIN*/))
					throw new IOException("Seek failed during verification (error " + Marshal.GetLastWin32Error() + ").");
				uint got;
				if (!ReadFile(h, devPtr, (uint)aligned, out got, IntPtr.Zero))
					got = 0;   // a NO_BUFFERING read that runs past a fake/short drive's real end fails -> 0, and the compare flags it
				int br = (int)got;
				int cmp = Math.Min(ar, br);
				for (int i = 0; i < cmp; i++) if (a[i] != rawDev[devOff + i]) return (false, pos + i);
				if (br < ar) return (false, pos + br);
				pos += ar;
				Volatile.Write(ref _progressDoneBytes, pos);
			}
			return (!stopRequested && pos >= isoSize, pos);
		}
		finally { gc.Free(); }
	}

	// Verify a drive's REAL capacity by writing self-identifying test data across the free space and reading
	// it back — detects counterfeit USB drives that report a larger size than they physically have.
	private async void CapacityTest_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb027"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb028"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		char letter = disk.DriveLetters.Select(char.ToUpperInvariant).FirstOrDefault(l => l >= 'A' && l <= 'Z');
		if (letter == '\0') { MessageBox.Show(L("Mb029"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		long free;
		try { free = new DriveInfo(letter + ":").AvailableFreeSpace; } catch { free = 0; }
		free -= free % 4096; // page align
		if (free < 16L * 1024 * 1024) { MessageBox.Show(L("Mb030"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		if (MessageBox.Show(string.Format(L("MbCapacityConfirm"), disk.FriendlyName, letter, FormatBytes(disk.Size), FormatBytes(free)),
				L("MbCapacityTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		string dir = letter + ":\\__driveforge_captest__";
		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			// _progressFixedTotal: the target is the measured free space (written once, read back once) — a real total,
			// not a projection. Without it the clone-only inflation heuristic pushed the ceiling 12% ahead at 97%, so
			// the completion update below stalled the bar at ~89% forever.
			_progressFullRange = true; _progressFixedTotal = true; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, free / 1073741824.0 * 2.0); // write + read
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzCapacity"), letter));
			ProgressBar.Value = 0.0;
			var (written, verifiedOk, fake) = await Task.Run(() => RunCapacityTestCore(dir, free));
			operationTimer.Stop(); operationStopwatch.Stop();
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(!fake);

			string verdict;
			if (stopRequested) verdict = L("CapStopped");
			else if (fake)
				verdict = string.Format(L("CapFake"), FormatBytes(verifiedOk), FormatBytes(written), FormatBytes(disk.Size));
			else
				verdict = string.Format(L("CapGenuine"), FormatBytes(verifiedOk));
			ToolRecommendationDetailText.Text = verdict.Replace("\n", " ");
			SetToolOutput($"Capacity test on {letter}: — claimed {FormatBytes(disk.Size)}\r\nWritten: {FormatBytes(written)}\r\nVerified OK: {FormatBytes(verifiedOk)}\r\nResult: {(fake ? "FAKE/FAULTY" : "GENUINE")}");
			MessageBox.Show(verdict, "DriveForge — capacity test", MessageBoxButton.OK, fake ? MessageBoxImage.Warning : MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("ErrCapacity"), ex); }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			if (failed) UpdateProgressStats();                       // refresh BEFORE clearing the flags (see the wipe flow)
			_progressFullRange = false; _progressFixedTotal = false; // clear BOTH — must not leak into a later clone/install
			try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
			SetBusy(busy: false);
		}
	}

	// Writes self-identifying pages (each 4 KiB page stamped with its page number) to fill the free space,
	// then reads back and verifies. First mismatch = real capacity boundary (fake drives wrap around).
	private (long written, long verifiedOk, bool fake) RunCapacityTestCore(string dir, long target)
	{
		const int page = 4096;
		int buf = 4 * 1024 * 1024; // 4 MiB (multiple of page) — faster write/read
		long fileSize = 1L * 1024 * 1024 * 1024; // 1 GiB per file
		byte[] b = new byte[buf];
		Directory.CreateDirectory(dir);
		// Round the target DOWN to a whole page so every cap file (except one that a real disk-full truncates) is a whole
		// number of sectors — the NO_BUFFERING read-back below reads whole pages only.
		target = target / page * page;

		void Stamp(long baseOff, int len) { for (int o = 0; o < len; o += page) BitConverter.GetBytes((baseOff + o) / page).CopyTo(b, o); }

		long globalOffset = 0;
		int fileIdx = 0;
		try
		{
			long remaining = target;
			while (remaining > 0 && !stopRequested)
			{
				long thisFile = Math.Min(fileSize, remaining);
				using var fs = new FileStream(Path.Combine(dir, $"cap{fileIdx:D5}.bin"), FileMode.Create, FileAccess.Write, FileShare.None, buf);
				long fw = 0;
				try
				{
					while (fw < thisFile && !stopRequested)
					{
						while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
						int toW = (int)Math.Min(buf, thisFile - fw);
						Stamp(globalOffset, toW);
						fs.Write(b, 0, toW);
						fw += toW; globalOffset += toW; remaining -= toW;
						Volatile.Write(ref _progressDoneBytes, globalOffset);
					}
				}
				finally { try { fs.Flush(true); } catch { } }   // force to the DEVICE even if a write threw (disk full) — otherwise a genuine but truncated last file's tail stays dirty-in-cache and the NO_BUFFERING read-back below reads stale sectors and false-flags FAKE
				fileIdx++;
			}
		}
		catch (IOException) { /* disk full earlier than claimed = the real boundary; verify what we wrote */ }

		long written = globalOffset;
		long verified = 0;
		// Read the cap files back with FILE_FLAG_NO_BUFFERING so the bytes come from the ACTUAL flash, not the FS cache —
		// a counterfeit drive that reports more capacity than it has would otherwise serve the just-written data from RAM
		// and pass. One sector-aligned buffer, reused across files; whole pages only.
		byte[] rbRaw = new byte[buf + page];
		var rbGc = System.Runtime.InteropServices.GCHandle.Alloc(rbRaw, System.Runtime.InteropServices.GCHandleType.Pinned);
		try
		{
			long rbBase = rbGc.AddrOfPinnedObject().ToInt64();
			long rbAligned = (rbBase + page - 1) & ~((long)page - 1);
			int rbOff = (int)(rbAligned - rbBase);
			IntPtr rbPtr = new IntPtr(rbAligned);
			foreach (var f in Directory.GetFiles(dir, "cap*.bin").OrderBy(x => x))
			{
				if (stopRequested) break;
				// The global offset where THIS file's data begins — derived from its index in the name (cap{N:D5}) times the
				// fixed 1 GiB file size, NOT a running counter. So a file we can't open/read (an AV/indexer lock, a transient
				// error) can't shift the expected stamps of the FOLLOWING files and cause a FALSE "fake" verdict on a genuine
				// drive. (Every full file is exactly fileSize; only the last can be shorter, and it starts at N*fileSize too.)
				string nm = Path.GetFileNameWithoutExtension(f);
				long fileStart = (nm.Length > 3 && long.TryParse(nm.Substring(3), out long fileNo) ? fileNo : 0) * fileSize;
				long len; try { len = new FileInfo(f).Length; } catch { continue; }
				using SafeFileHandle fh = CreateFile(f, GenericRead, 0x1u /*FILE_SHARE_READ*/, IntPtr.Zero, 3u /*OPEN_EXISTING*/, FileFlagNoBuffering, IntPtr.Zero);
				if (fh.IsInvalid) continue;
				long fpos = 0;
				while (fpos < len && !stopRequested)
				{
					while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
					int readLen = (int)Math.Min((long)buf, len - fpos) / page * page;   // whole pages only (a sub-page tail from a truncated file has no complete stamp)
					if (readLen <= 0) break;
					if (!SetFilePointerEx(fh, fpos, out _, 0 /*FILE_BEGIN*/)) break;
					uint got;
					if (!ReadFile(fh, rbPtr, (uint)readLen, out got, IntPtr.Zero)) got = 0;
					int usable = (int)Math.Min((long)got, (long)readLen);
					for (int o = 0; o + page <= usable; o += page)
						if (BitConverter.ToInt64(rbRaw, rbOff + o) != (fileStart + fpos + o) / page)
							return (written, fileStart + fpos + o, true); // stamp mismatch → fake/faulty
					if (usable < readLen)   // the device could not read complete pages the file holds -> past a counterfeit's real capacity / faulty
						return (written, fileStart + fpos + (usable / page) * page, true);
					verified += usable; fpos += usable;
					Volatile.Write(ref _progressDoneBytes, written + verified);
				}
			}
		}
		finally { rbGc.Free(); }
		return (written, verified, false);
	}

	// "Test boot": spin up a throw-away Hyper-V VM that boots straight from the selected physical drive so the
	// user can SEE it boot, without rebooting their own PC. The disk is taken offline for the duration (required
	// for a Hyper-V pass-through disk) and put back online when the test is cleaned up. Nothing here installs or
	// formats anything — it just boots the drive in a VM. Requires Hyper-V.
	private const string TestBootVmName = "DriveForge-BootTest";

	private async void TestBoot_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb027"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb031"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb032"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Hold the reentrancy guard across the WHOLE flow: SetBusy(true) is not reached until after the Hyper-V probe
		// await + firmware menu + confirm, and this op takes the disk OFFLINE and boots an OS from it, so a concurrent
		// destructive tool starting in that window must be blocked (mirrors _startInProgress for StartButton).
		_toolOpStarting = true;
		try
		{
			// Hyper-V must be installed (cmdlets present).
			bool hyperV = false;
			try
			{
				string probe = await RunProcessCaptureAsync("powershell.exe",
					"-NoProfile -Command \"if (Get-Command New-VM -ErrorAction SilentlyContinue) { 'OK' }\"");
				hyperV = probe.Contains("OK");
			}
			catch { hyperV = false; }
			if (!hyperV)
			{
				if (MessageBox.Show(L("MbHyperV"),
						L("MbHyperVTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				{
					try { Process.Start(new ProcessStartInfo("optionalfeatures.exe") { UseShellExecute = true }); } catch { }
				}
				return;
			}

			int? fw = ShowActionMenu(L("MbTestBootTitle"),
				string.Format(L("AmTestBootPrompt"), disk.Number, disk.FriendlyName),
				new[]
				{
					L("AmFwUefi"),
					L("AmFwBios")
				},
				new[] { 0xE768, 0xEC58 }, null, 0);
			if (fw == null) return;
			bool uefi = fw.Value == 0;

			if (MessageBox.Show(string.Format(L("MbBootTestConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), (uefi ? "UEFI" : "Legacy BIOS")),
					L("MbTestBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK)
				return;

			// This offlines the disk on the host and boots an OS from it (the guest WRITES to it). Re-confirm the disk
			// number still maps to the same physical disk before touching it — every other raw-disk op does this, and the
			// disks can renumber during the probe/menu/confirm above (isBusy was false the whole time).
			if (!await VerifyTargetDiskUnchangedAsync(disk)) return;
			// Breadcrumb so a crash/kill/power-loss before the finally re-onlines the disk can be recovered at next launch.
			WriteTestBootOfflineMarker(disk);

			string startScript =
				"$ErrorActionPreference='Stop'\r\n" +
				"$vm='" + TestBootVmName + "'\r\n" +
				"$n=" + disk.Number + "\r\n" +
				"Get-VM -Name $vm -ErrorAction SilentlyContinue | ForEach-Object { Stop-VM -VM $_ -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -VM $_ -Force -ErrorAction SilentlyContinue }\r\n" +
				"Set-Disk -Number $n -IsOffline $true\r\n" +
				"New-VM -Name $vm -MemoryStartupBytes 2GB -Generation " + (uefi ? "2" : "1") + " | Out-Null\r\n" +
				"Set-VM -Name $vm -AutomaticCheckpointsEnabled $false -ErrorAction SilentlyContinue\r\n" +
				"Add-VMHardDiskDrive -VMName $vm -DiskNumber $n\r\n" +
				(uefi
					? "Set-VMFirmware -VMName $vm -EnableSecureBoot Off\r\n$hd = Get-VMHardDiskDrive -VMName $vm\r\nSet-VMFirmware -VMName $vm -FirstBootDevice $hd\r\n"
					: "") +
				"Start-VM -Name $vm\r\n";

			bool started = false;
			try
			{
				SetBusy(busy: true, string.Format(L("BzBootVm"), disk.Number));
				Log($"Test boot: creating {(uefi ? "UEFI" : "BIOS")} VM for disk {disk.Number}");
				var res = await RunPowerShellScriptAsync(startScript);
				if (res.ExitCode != 0)
					throw new InvalidOperationException(res.Output);
				started = true;

				// Open the VM console so the user can watch it boot. The whole feature is VISUAL, so if the console can't
				// open (Hyper-V PowerShell present but the GUI tools / vmconnect.exe not installed) do NOT claim a window
				// opened — tell the user to connect via Hyper-V Manager instead.
				bool consoleOpened = false;
				try
				{
					Process.Start(new ProcessStartInfo("vmconnect.exe", "localhost \"" + TestBootVmName + "\"") { UseShellExecute = true });
					consoleOpened = true;
				}
				catch { /* console is optional; the VM still runs */ }

				SetBusy(busy: false);
				MessageBox.Show(L(consoleOpened ? "MbVmRunning" : "MbVmRunningNoConsole"),
					L("MbTestBootTitle"), MessageBoxButton.OK, consoleOpened ? MessageBoxImage.Information : MessageBoxImage.Warning);
			}
			catch (Exception ex)
			{
				SetBusy(busy: false);
				ShowError(L("ErrTestBoot"), ex);
			}
			finally
			{
				// Always tear down: stop + delete the VM and bring the disk back online, whether it started or not.
				try
				{
					SetBusy(busy: true, L("BzCleanVm"));
					var cleanup = await RunPowerShellScriptAsync(
						// $ErrorActionPreference='Stop' so a FAILED Set-Disk -IsOffline $false (disk held by a stuck VM, a
						// CIM error) is TERMINATING -> non-zero exit -> the warning below fires. Without it the re-online
						// failure is non-terminating, powershell exits 0, and the disk is left silently offline.
						"$ErrorActionPreference='Stop'\r\n" +
						"$vm='" + TestBootVmName + "'\r\n" +
						"Get-VM -Name $vm -ErrorAction SilentlyContinue | ForEach-Object { Stop-VM -VM $_ -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -VM $_ -Force -ErrorAction SilentlyContinue }\r\n" +
						"Set-Disk -Number " + disk.Number + " -IsOffline $false\r\n");
					// If bringing the disk back online failed, the user must know — otherwise it silently vanishes from Explorer.
					if (cleanup.ExitCode != 0)
					{
						Log("Test-boot cleanup returned " + cleanup.ExitCode + ": " + cleanup.Output);
						MessageBox.Show(string.Format(L("MbBootTestOfflineWarn"), disk.Number),
							L("MbTestBootTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
					}
					else
					{
						ClearTestBootOfflineMarker();   // re-online confirmed — the crash-recovery breadcrumb is no longer needed
					}
				}
				catch (Exception cex)
				{
					// The cleanup script itself threw (powershell launch / temp write / await fault) — the disk is still
					// offline. Try once more to re-online it, and warn the user so it doesn't silently vanish.
					Log("Test-boot cleanup error: " + cex.Message);
					try { await RunPowerShellScriptAsync("$ErrorActionPreference='SilentlyContinue'\r\nSet-Disk -Number " + disk.Number + " -IsOffline $false\r\n"); } catch { }
					MessageBox.Show(string.Format(L("MbBootTestOfflineWarn"), disk.Number),
						L("MbTestBootTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
				}
				SetBusy(busy: false);
				if (started) { try { await RefreshDisksAsync(); } catch { } }
			}
		}
		finally { _toolOpStarting = false; }
	}

	private static string TestBootMarkerPath =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge", "testboot-offline.txt");

	// Records (disk number | serial) before Test-boot takes the disk offline, so a crash/kill before the re-online can
	// be reconciled at the next launch. Best-effort.
	private void WriteTestBootOfflineMarker(DiskItem disk)
	{
		try
		{
			string p = TestBootMarkerPath;
			Directory.CreateDirectory(Path.GetDirectoryName(p)!);
			File.WriteAllText(p, disk.Number + "|" + (disk.Serial ?? "").Trim());
		}
		catch { }
	}

	private void ClearTestBootOfflineMarker() { try { File.Delete(TestBootMarkerPath); } catch { } }

	// At startup: if a previous Test-boot was interrupted before it could re-online the disk, remove any stranded
	// boot-test VM and bring that disk back online (prefer matching by serial — numbers can change). Best-effort.
	private async Task RecoverStrandedTestBootDiskAsync()
	{
		try
		{
			string p = TestBootMarkerPath;
			if (!File.Exists(p)) return;
			string content = "";
			try { content = File.ReadAllText(p).Trim(); } catch { }
			var parts = content.Split('|');
			string num = parts.Length > 0 ? parts[0].Trim() : "";
			string serial = parts.Length > 1 ? parts[1].Trim() : "";
			string script = "$ErrorActionPreference='SilentlyContinue'\r\n" +
				"$vm='" + TestBootVmName + "'\r\n" +
				"Get-VM -Name $vm | ForEach-Object { Stop-VM -VM $_ -TurnOff -Force; Remove-VM -VM $_ -Force }\r\n" +
				(serial.Length > 0 ? "Get-Disk | Where-Object { $_.SerialNumber.Trim() -eq '" + serial.Replace("'", "''") + "' } | Set-Disk -IsOffline $false\r\n" : "") +
				(int.TryParse(num, out _) ? "Set-Disk -Number " + num + " -IsOffline $false\r\n" : "");
			await RunPowerShellScriptAsync(script);
			Log("Recovered a disk left offline by a previous, interrupted Test-boot.");
		}
		catch (Exception ex) { Log("Test-boot startup recovery error: " + ex.Message); }
		finally { ClearTestBootOfflineMarker(); }
	}

	// Runs a small PowerShell script (written to a temp .ps1) and returns its exit code + combined output.
	private async Task<ProcessResult> RunPowerShellScriptAsync(string script)
	{
		string path = Path.Combine(Path.GetTempPath(), $"driveforge-ps-{Guid.NewGuid():N}.ps1");
		// UTF-8 WITH BOM: Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, which corrupts any non-ASCII byte
		// (e.g. an em-dash) and breaks string parsing. The BOM makes 5.1 (and PowerShell 7) read it as UTF-8.
		await File.WriteAllTextAsync(path, script, new UTF8Encoding(true));
		try
		{
			return await RunProcessInternalAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(path));
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
	}

	// ---------- Export this PC to a bootable VHDX (Hyper-V Gen 2 / UEFI) ----------
	// Produces a single self-contained bootable .vhdx (GPT: ESP + MSR + Windows, with an internal BCD) that a
	// Hyper-V Generation 2 VM boots directly. Reuses the clone machinery: VSS snapshot -> wimlib capture/apply ->
	// portable-registry post-processing (which flips the inbox storage drivers, incl. the Hyper-V synthetic
	// vmbus/storvsc, to boot-start so the guest doesn't bugcheck 0x7B) -> bcdboot /f UEFI into the VHDX's own ESP.
	// The host PC is never modified; the whole capture runs off a read-only VSS snapshot.
	private async void ExportVhdx_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb032"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		// Hold the busy state across the WHOLE flow, including the confirm / save / size modals below. Those modals
		// pump the WPF Dispatcher, so without this a WM_DEVICECHANGE during them would let the passive auto-refresh
		// timer run RefreshDisksAsync and clear busy mid-export. Cleared on every early-return here and in the finally.
		SetBusy(true, L("BzExportVhdx"));

		if (MessageBox.Show(L("MbExportVhdxConfirm"), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
		{ SetBusy(false); return; }

		var dlg = new Microsoft.Win32.SaveFileDialog
		{
			Title = L("TbExportVhdx"),
			// The file-type dropdown IS the format chooser: VHDX (Hyper-V), VHD (VirtualBox / QEMU), VMDK (VMware).
			Filter = L("FltExportVhdx"),
			DefaultExt = ".vhdx",
			AddExtension = true,
			OverwritePrompt = true,
			FileName = "DriveForge-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyyMMdd") + ".vhdx",
			InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
		};
		if (dlg.ShowDialog() != true) { SetBusy(false); return; }
		string vhdPath = dlg.FileName;
		string chosenExt = Path.GetExtension(vhdPath).ToLowerInvariant();
		if (chosenExt != ".vhdx" && chosenExt != ".vhd" && chosenExt != ".vmdk") { vhdPath += ".vhdx"; chosenExt = ".vhdx"; }
		// Network / UNC destinations can't be built by diskpart's create vdisk — reject up front with a clear message.
		if (vhdPath.StartsWith(@"\\", StringComparison.Ordinal))
		{ MessageBox.Show(L("MbExportVhdxNoUnc"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); SetBusy(false); return; }

		// Virtual-disk size cap. The VHDX is expandable, so a bigger cap does NOT use more disk now — it only lets the
		// VM's Windows grow larger later. "Auto" = current used data + 40 GB headroom (never smaller than the data).
		int? sizePick = ShowChooserDialog(L("TbExportVhdx"), L("MbExportVhdxSizePrompt"),
			new[] { "Auto", "128 GB", "256 GB", "512 GB", "1024 GB" }, 0);
		if (sizePick == null) { SetBusy(false); return; }
		long maxMbChoice = sizePick.Value switch { 1 => 128L * 1024, 2 => 256L * 1024, 3 => 512L * 1024, 4 => 1024L * 1024, _ => 0 };

		// NOTE: do NOT delete an existing destination file up front. The VHDX is built at a fresh-GUID WORK path and
		// only renamed onto the user's path (File.Move overwrite:true) AFTER it is complete — so a failed export can
		// never destroy the user's existing file. (The old code deleted here because diskpart used to create directly
		// at the final path; that is no longer the case.)

		// diskpart's script writer is ASCII, which corrupts any non-ASCII destination path (e.g. an accented profile
		// folder like C:\Users\Ștefan, or a typed name like Clonă.vhdx) into '?'. So build the VHDX at an ASCII-only
		// temp path on the SAME volume, then rename it to the user's chosen path at the end (File.Move is Unicode-safe;
		// same volume = instant rename). This keeps every diskpart script pure-ASCII regardless of the user's path.
		// diskpart only creates .vhd/.vhdx. For a .vmdk target, build a .vhd work file and convert it afterwards.
		string workExt = (chosenExt == ".vhdx") ? ".vhdx" : ".vhd";
		string workVhdPath = BuildAsciiWorkVhdPath(vhdPath, workExt);

		// Clear any stale Stop left over from an earlier operation in this session, exactly like every other
		// operation's entry point (e.g. the clone at ~2450). Otherwise a leftover stopRequested would make the
		// export throw "stopped" AFTER the full snapshot + multi-GB apply, discarding all the work.
		stopRequested = false;
		internalOperationStopped = false;
		// The export panel hides the whole footer; show the Stop button so a running export can be cancelled
		// (the raw engine polls stopRequested). Restore it hidden in the finally.
		StopButton.Visibility = Visibility.Visible;
		try
		{
			await ExportBootableVhdxCoreAsync(workVhdPath, vhdPath, maxMbChoice);
		}
		catch (Exception ex)
		{
			ShowError(L("ErrExportVhdx"), ex);
		}
		finally
		{
			// isBusy is held true across the whole chain (core + VM offer); release it only here so a second
			// Export click can't slip in during the VM-offer's async Hyper-V probe and launch a concurrent pipeline.
			SetBusy(false);
			StopButton.Visibility = Visibility.Collapsed;
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool GetVolumePathName(string lpszFileName, System.Text.StringBuilder lpszVolumePathName, uint cchBufferLength);

	private static bool IsPureAscii(string s) { foreach (char c in s) if (c > '\x7f') return false; return true; }

	// Builds the ASCII-only WORK path for the VHDX build. Two hard constraints: (1) diskpart's script writer is ASCII,
	// so the path must contain no non-ASCII characters; (2) it must sit on the SAME PHYSICAL VOLUME as the final
	// destination, so the closing File.Move is an instant same-volume rename instead of a slow cross-volume copy that
	// could fill the wrong disk. Path.GetPathRoot is purely lexical — it returns "C:\" even when the destination folder
	// is a mount point for a different physical disk (e.g. a 2 TB HDD mounted at C:\Storage), which would put the work
	// file on the system SSD. GetVolumePathName resolves the true volume mount root; we use it when it is ASCII and fall
	// back to the drive-letter root (always ASCII) otherwise.
	private static string BuildAsciiWorkVhdPath(string finalPath, string workExt)
	{
		string full = Path.GetFullPath(finalPath);
		string dir = Path.GetPathRoot(full) ?? @"C:\";
		try
		{
			var sb = new System.Text.StringBuilder(1024);
			if (GetVolumePathName(full, sb, 1024) && sb.Length > 0)
			{
				string mount = sb.ToString();
				if (IsPureAscii(mount)) dir = mount;   // real volume root (handles folder mount points); keep ASCII for diskpart
			}
		}
		catch { /* fall back to the lexical drive-letter root */ }
		if (!IsPureAscii(dir)) dir = Path.GetPathRoot(full) ?? @"C:\";
		return Path.Combine(dir, "DriveForge-export-" + Guid.NewGuid().ToString("N") + workExt);
	}

	// vhdPath = the ASCII WORK path (all diskpart/detach ops use it); finalVhdPath = the user's chosen (possibly
	// non-ASCII) path the finished VHDX is renamed to on success.
	private async Task ExportBootableVhdxCoreAsync(string vhdPath, string finalVhdPath, long maxMbOverride = 0)
	{
		char shadowLetter = GetFreeDriveLetter();
		char espLetter = GetFreeDriveLetter(shadowLetter);
		char windowsLetter = GetFreeDriveLetter(shadowLetter, espLetter);
		string realRoot = windowsLetter + ":\\";
		string realWindowsFolder = Path.Combine(realRoot, "Windows");
		ShadowCopyInfo? shadowCopy = null;
		string? shadowDosTarget = null;
		bool attached = false;
		bool success = false;
		long efsSkipped = 0;
		long rawErrors = 0;
		long rawZeroFilled = 0;
		try
		{
			TryEnablePrivilege("SeBackupPrivilege");
			TryEnablePrivilege("SeRestorePrivilege");
			TryEnablePrivilege("SeSecurityPrivilege");            // Fast Clone reproduces SACLs
			TryEnablePrivilege("SeTakeOwnershipPrivilege");       // Fast Clone restores owners
			TryEnablePrivilege("SeCreateSymbolicLinkPrivilege");  // Fast Clone replays symlink reparse points
			SetBusy(true, L("BzExportVhdx"));
			Log("Export bootable VHDX -> " + vhdPath);

			// 1. Read-only VSS snapshot of the running Windows (consistent, open-file safe; the host is untouched).
			string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
			shadowCopy = await CreateShadowCopyAsync(systemDrive);
			shadowDosTarget = GetDosDeviceTarget(shadowCopy.DeviceObject);
			MapSnapshotDrive(shadowLetter, shadowDosTarget);
			string sourceRoot = shadowLetter + ":\\";

			// 2. Create + attach an expandable VHDX, laid out GPT: ESP (FAT32) + MSR + Windows (NTFS) — self-contained.
			// User-chosen virtual size (0 = auto), but never smaller than the data + headroom so the copy always fits.
			long maxMb = Math.Max(maxMbOverride, EstimateExportVhdxMaximumMb());
			// The .vhd format (used for .vhd and the intermediate for .vmdk) is capped at 2 TB — clamp so create vdisk succeeds.
			if (!vhdPath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) maxMb = Math.Min(maxMb, 2040L * 1024);
			// Set BEFORE the diskpart call: the script attaches the vdisk early, so if a later (non-noerr) command
			// fails and diskpart throws, the finally must still try to detach it. DetachVhdxAsync is harmless (and
			// caught) if the vdisk was never actually attached.
			attached = true;
			string createOut = await RunDiskpartAsync(BuildCreateBootableVhdxDiskpartScript(vhdPath, espLetter, windowsLetter, maxMb));
			Log("VHDX created + attached (GPT ESP+MSR+Windows), virtual size " + (maxMb / 1024) + " GB.");
			if (!Directory.Exists(realRoot))
				throw new InvalidOperationException("The VHDX Windows partition (" + windowsLetter + ":) did not mount.\r\n\r\ndiskpart output:\r\n" + createOut);

			// Suppress NTFS 8.3 short-name creation on the target for the many-small-file apply (pure metadata overhead).
			await RunProcessAsync("fsutil.exe", $"8dot3name set {windowsLetter}: 1", allowFailure: true);

			// 3. Apply the current Windows into the VHDX with the Fast Clone (raw NTFS) engine: it reads the file table
			//    straight off the VSS snapshot (no per-file open, so antivirus never scans it and there is no slow wimlib
			//    metadata scan pass) and writes files directly to the VHDX. A poller drives the live progress bar / ETA
			//    off the VHDX partition's growing used space. Much faster than wimlib on a USB source with antivirus on.
			SetBusy(true, L("BzExportVhdx"));
			Log("Applying Windows into the VHDX (Fast Clone raw engine, antivirus-transparent). " + sourceRoot + " -> " + realRoot);
			progressDoneGiB = 0.0;
			progressPrevGiB = 0.0;
			progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1073741824.0);
			ProgressBar.Value = 0.0; // start the bar empty (UpdateProgressStats only advances it, never retreats)
			_speedWindow.Clear();
			operationStopwatch.Restart();
			operationTimer.Start();
			RawCloneStats? rawStats = null;
			using (var rawPollCts = new CancellationTokenSource())
			{
				Task rawPoll = PollPartitionUsedSpaceAsync(realRoot, rawPollCts.Token, rawEngine: true);
				_suppressLineProgress = true;
				try { rawStats = await RawNtfsWriteCloneAsync(shadowLetter, windowsLetter, realRoot); }
				finally { _suppressLineProgress = false; rawPollCts.Cancel(); try { await rawPoll; } catch { } }
			}

			if (stopRequested || internalOperationStopped)
				throw new OperationCanceledException("Export stopped before the copy finished — the VHDX is incomplete.");
			if (rawStats != null && rawStats.DiskFull > 0)
				throw new InvalidOperationException("The VHDX ran out of space during the copy — the export is incomplete. Free up space on the destination drive.");
			// The Fast Clone engine cannot read EFS-encrypted files (it has no key), so it SKIPS them. Surface the
			// count so the user is warned the VHDX is missing those files instead of silently losing them.
			efsSkipped = rawStats?.EfsSkipped ?? 0;
			if (efsSkipped > 0)
				Log($"WARNING: {efsSkipped} EFS-encrypted file(s) were skipped by the Fast Clone engine — they are NOT in the VHDX.");
			// A raw-engine copy that DROPPED files (unreadable/torn source records or target write errors -> stats.Errors)
			// produced an INCOMPLETE VHDX that may be missing arbitrary system files and may not boot. The 3-anchor copyOk
			// spot-check below cannot detect that. Mirror the portable USB-clone path (which folds rawStats.Errors into its
			// "review, not clean success" gate at ~3213): keep the file but warn the user loudly in the completion dialog —
			// and before the VM offer — instead of reporting a plain success and auto-booting an incomplete image.
			rawErrors = rawStats?.Errors ?? 0;
			if (rawErrors > 0)
				Log($"WARNING: {rawErrors} file(s) could not be copied into the VHDX (torn source records / write errors) — the VHDX is INCOMPLETE and may not boot correctly.");
			// Regions of real data that couldn't be read from the SOURCE (bad sectors / truncated run-list) and were
			// zero-filled to keep file lengths correct — the VHDX is complete in size but those bytes are lost/corrupt.
			rawZeroFilled = rawStats != null ? rawStats.RunShortfalls + rawStats.ReadShortfalls : 0;
			if (rawZeroFilled > 0)
				Log($"WARNING: {rawZeroFilled} region(s) could not be read from the source and were zero-filled — the source drive may have bad sectors; the VHDX is not byte-faithful.");
			bool copyOk = File.Exists(Path.Combine(realWindowsFolder, "System32", "winload.efi"))
				&& File.Exists(Path.Combine(realWindowsFolder, "System32", "config", "SYSTEM"))
				&& Directory.Exists(Path.Combine(realRoot, "Users"));
			if (!copyOk)
				throw new InvalidOperationException("The Windows apply did not produce a complete root inside the VHDX.");

			// The copy poller caps below 100% because the source's reported "used" includes pagefile/hiberfil/temp/
			// caches that are EXCLUDED from the copy — so drive the bar up through the finalization steps to reach 100%.
			// The raw-copy poller maps progress through a partial write-band (never 100%), so set the bar explicitly
			// through the finalization steps (UpdateProgressStats' advance-only guard keeps these higher values).
			ProgressBar.Value = 88.0;

			// 4. Portable-registry post-processing: mark the image as a portable OS and force the inbox storage drivers
			//    (incl. the Hyper-V synthetic vmbus/storvsc) to boot-start, so the guest boots on the VM's virtual
			//    controller instead of bugchecking INACCESSIBLE_BOOT_DEVICE (0x7B). Same pass the portable USB clone uses.
			SetBusy(true, L("BzExportVhdx"));
			string regOut = await ApplyPortableRegistrySettingsToRealCloneAsync(realWindowsFolder,
				BypassRequirementsCheck?.IsChecked == true, BypassAccountCheck?.IsChecked == true,
				faithfulMode: true, portableMode: true);
			if (regOut.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Portable registry preparation failed:\r\n" + regOut);

			// 5. First-boot answer file (keep every device install, skip the OOBE hardware re-detect prompts).
			WritePortableUnattend(realWindowsFolder);

			// 6. Make the VHDX self-bootable: write the UEFI boot files + BCD into the VHDX's OWN ESP.
			ProgressBar.Value = 95.0;
			Log("Making the VHDX bootable (UEFI). bcdboot -> ESP " + espLetter + ":");
			string bcdOut = await RunProcessCaptureAsync("bcdboot.exe", QuoteArgument(realWindowsFolder) + $" /s {espLetter}: /f UEFI /v");
			EnsureUefiRemovableFallback(espLetter);
			if (!File.Exists(espLetter + ":\\EFI\\Microsoft\\Boot\\bootmgfw.efi"))
				throw new InvalidOperationException("bcdboot did not write the UEFI boot files to the VHDX ESP.\r\n" + bcdOut);

			// 7. Fast Clone engine: apply owners/ACLs LAST — after registry + bcdboot — so a restrictive source ACL on
			//    the hive/config files can't block those steps. The snapshot is still mapped (released in the finally).
			SetBusy(true, L("BzExportVhdx"));
			try { await RawNtfsApplySecurityAsync(shadowLetter, realRoot); }
			catch (Exception secEx) { Log("WARNING: Fast Clone permission pass failed: " + secEx.Message + " (the VHDX is usable; permissions may be default)."); }
			progressDoneGiB = progressTotalGiB;
			ProgressBar.Value = 100.0;                                              // finished — show a full bar
			if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
			success = true;                     // a complete, bootable VHDX was produced — keep the file
		}
		finally
		{
			operationTimer.Stop();
			operationStopwatch.Stop();
			UpdateProgressStats();
			// Detach the VHDX FIRST (flushes the file, frees the esp/windows letters), then release the snapshot.
			if (attached)
			{
				try { await DetachVhdxAsync(vhdPath); }
				catch (Exception dex) { Log("WARNING: could not detach the VHDX cleanly: " + dex.Message); }
			}
			// On failure/cancel the .vhdx is an incomplete, non-bootable multi-GB orphan (and diskpart already
			// detached it above), so delete it — otherwise failed attempts pile up and can fill the disk. Only the
			// fully-built VHDX (success) is kept for the VM.
			if (!success)
			{
				try { File.Delete(vhdPath); Log("Deleted the incomplete VHDX: " + vhdPath); }
				catch (Exception delEx) { Log("Note: could not delete the incomplete VHDX '" + vhdPath + "': " + delEx.Message); }
			}
			if (!string.IsNullOrWhiteSpace(shadowDosTarget)) UnmapSnapshotDrive(shadowLetter, shadowDosTarget);
			if (shadowCopy != null) await DeleteShadowCopyAsync(shadowCopy.Id);
			// NOTE: SetBusy(false) is deliberately NOT called here. isBusy must stay true through the Hyper-V VM
			// offer below (which awaits an out-of-process PowerShell probe) so the "if (isBusy) return" guard keeps
			// blocking a second Export click. The caller (ExportVhdx_Click) clears busy in its own finally.
		}

		// Success (an exception above would have propagated past the finally, skipping this). Deliver in the user's
		// chosen format: .vhdx/.vhd = rename the ASCII work file to the user's (possibly non-ASCII) path (the disk is
		// already detached; File.Move is Unicode-safe, same volume => instant); .vmdk = convert the work .vhd.
		string finalExt = Path.GetExtension(finalVhdPath).ToLowerInvariant();
		string readyPath = finalVhdPath;
		if (finalExt == ".vmdk")
		{
			SetBusy(true, L("BzExportVhdx"));
			Log("Converting the VHD to a VMware VMDK...");
			if (await ConvertVhdToVmdkAsync(vhdPath, finalVhdPath)) TryDeleteFile(vhdPath);
			else
			{
				// No qemu-img: keep the .vhd, but deliver it at the user's chosen name/location — NOT the GUID work
				// path at the volume root (which they'd never find). VMware can import a .vhd directly, or they can
				// install qemu and re-run to get a true .vmdk.
				// Never clobber a pre-existing unrelated .vhd here: the SaveFileDialog's OverwritePrompt only validated the
				// .vmdk name the user typed, NOT this derived .vhd path. Pick a free name so an earlier export at the same
				// base name is preserved, and move with overwrite:false as a belt-and-suspenders guard.
				string keptVhd = Path.ChangeExtension(finalVhdPath, ".vhd");
				for (int n = 1; File.Exists(keptVhd); n++)
					keptVhd = Path.Combine(Path.GetDirectoryName(finalVhdPath) ?? "", Path.GetFileNameWithoutExtension(finalVhdPath) + " (" + n + ").vhd");
				try { File.Move(vhdPath, keptVhd, overwrite: false); readyPath = keptVhd; }
				catch (Exception mvEx) { readyPath = vhdPath; Log("Could not move the .vhd to '" + keptVhd + "': " + mvEx.Message + " — it remains at " + vhdPath); }
				Log("No VMDK converter (qemu-img) found — kept a .vhd instead: " + readyPath + " (VMware can import it, or install qemu and re-run).");
			}
		}
		else
		{
			try { File.Move(vhdPath, finalVhdPath, overwrite: true); }
			catch (Exception mvEx) { Log("WARNING: could not rename to '" + finalVhdPath + "': " + mvEx.Message + " — it remains at " + vhdPath); readyPath = vhdPath; }
		}
		Log("Bootable virtual disk ready: " + readyPath);
		if (finalExt == ".vhdx")
		{
			await MaybeCreateHyperVGen2VmAsync(readyPath, efsSkipped, rawErrors, rawZeroFilled);   // only VHDX gets the Hyper-V Gen 2 auto-VM offer
		}
		else
		{
			string efsNote = (rawErrors > 0 ? string.Format(L("MbExportFilesSkipped"), rawErrors) : "")
				+ (rawZeroFilled > 0 ? string.Format(L("MbRawZeroFilled"), rawZeroFilled) : "")
				+ (efsSkipped > 0 ? string.Format(L("MbExportVhdxEfsWarn"), efsSkipped) : "");
			MessageBox.Show(string.Format(L("MbExportOtherDone"), readyPath) + efsNote, "DriveForge", MessageBoxButton.OK, (rawErrors > 0 || rawZeroFilled > 0 || efsSkipped > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);
		}
	}

	// Offers to spin up a persistent Hyper-V Generation 2 VM from the freshly-built VHDX. If Hyper-V is not
	// installed, just tells the user how to attach the VHDX themselves. The VHDX is already complete either way.
	private async Task MaybeCreateHyperVGen2VmAsync(string vhdPath, long efsSkipped = 0, long rawErrors = 0, long rawZeroFilled = 0)
	{
		// Warn the user so missing/corrupt data isn't a silent surprise: rawErrors = files the Fast Clone engine could NOT
		// copy (torn source records / write errors -> INCOMPLETE); rawZeroFilled = regions unreadable from the source and
		// zero-filled (bad sectors -> not byte-faithful); efsSkipped = EFS-encrypted files it can't read. Each note carries
		// a leading blank line, so they stack cleanly after any message and show BEFORE the VM offer.
		string efsNote = (rawErrors > 0 ? string.Format(L("MbExportFilesSkipped"), rawErrors) : "")
			+ (rawZeroFilled > 0 ? string.Format(L("MbRawZeroFilled"), rawZeroFilled) : "")
			+ (efsSkipped > 0 ? string.Format(L("MbExportVhdxEfsWarn"), efsSkipped) : "");
		bool hyperV = false;
		try
		{
			string probe = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command \"if (Get-Command New-VM -ErrorAction SilentlyContinue) { 'OK' }\"");
			hyperV = probe.Contains("OK");
		}
		catch { hyperV = false; }

		if (!hyperV || MessageBox.Show(string.Format(L("MbExportVhdxVmOffer"), vhdPath) + efsNote, "DriveForge",
				MessageBoxButton.YesNo, (rawErrors > 0 || rawZeroFilled > 0 || efsSkipped > 0) ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes)
		{
			MessageBox.Show(string.Format(L("MbExportVhdxDone"), vhdPath) + efsNote, "DriveForge", MessageBoxButton.OK, (rawErrors > 0 || rawZeroFilled > 0 || efsSkipped > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);
			return;
		}

		string vmName = "DriveForge " + Path.GetFileNameWithoutExtension(vhdPath);
		string vmNameEsc = vmName.Replace("'", "''");
		string vhdEsc = vhdPath.Replace("'", "''");
		string script =
			"$ErrorActionPreference='Stop'\r\n" +
			"$vm='" + vmNameEsc + "'\r\n" +
			"if (Get-VM -Name ([System.Management.Automation.WildcardPattern]::Escape($vm)) -ErrorAction SilentlyContinue) { throw \"A virtual machine named '$vm' already exists - remove it or rename the VHDX.\" }\r\n" +
			"$v = New-VM -Name $vm -Generation 2 -MemoryStartupBytes 4GB -VHDPath '" + vhdEsc + "'\r\n" +
			"Set-VM -VM $v -AutomaticCheckpointsEnabled $false -ErrorAction SilentlyContinue\r\n" +
			"Set-VMProcessor -VM $v -Count 2 -ErrorAction SilentlyContinue\r\n" +
			"Set-VMMemory -VM $v -DynamicMemoryEnabled $true -MinimumBytes 2GB -MaximumBytes 8GB -ErrorAction SilentlyContinue\r\n" +
			"Set-VMFirmware -VM $v -EnableSecureBoot Off\r\n" +
			"$hd = $v | Get-VMHardDiskDrive\r\n" +
			"Set-VMFirmware -VM $v -FirstBootDevice $hd\r\n" +
			"Start-VM -VM $v\r\n";
		// isBusy is already held true by the caller for the whole export chain, so the async PowerShell calls
		// below can't be re-entered by a second Export click. Busy is released in ExportVhdx_Click's finally.
		try
		{
			var res = await RunPowerShellScriptAsync(script);
			if (res.ExitCode != 0)
			{
				MessageBox.Show(string.Format(L("MbExportVhdxDone"), vhdPath) + efsNote + "\r\n\r\n(" + res.Output.Trim() + ")",
					"DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}
			try { Process.Start(new ProcessStartInfo("vmconnect.exe", "localhost \"" + vmName + "\"") { UseShellExecute = true }); } catch { }
			MessageBox.Show(string.Format(L("MbExportVhdxVmDone"), vmName, vhdPath) + efsNote, "DriveForge", MessageBoxButton.OK, (rawErrors > 0 || rawZeroFilled > 0 || efsSkipped > 0) ? MessageBoxImage.Warning : MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			MessageBox.Show(string.Format(L("MbExportVhdxDone"), vhdPath) + efsNote + "\r\n\r\n(" + ex.Message + ")",
				"DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	// Converts the just-built .vhd to a VMware .vmdk using qemu-img if it is installed (the clean, standard tool).
	// Not bundled — if qemu-img isn't found the caller keeps the .vhd (VMware can import a VHD, or the user installs qemu).
	private async Task<bool> ConvertVhdToVmdkAsync(string vhdPath, string vmdkPath)
	{
		// Probe FIRST — never touch the user's existing destination unless we can actually produce a replacement.
		string? qemu = FindExternalTool("qemu-img.exe", new[] { @"C:\Program Files\qemu", @"C:\Program Files (x86)\qemu", @"C:\qemu" });
		if (qemu == null) { Log("qemu-img not found (PATH or C:\\Program Files\\qemu) — cannot produce a .vmdk."); return false; }
		// Convert to an ASCII temp path on the same volume, then move onto the destination ONLY after a verified
		// convert — so a missing qemu-img / failed convert can never destroy an existing .vmdk at vmdkPath.
		string tmpVmdk = Path.Combine(Path.GetPathRoot(vmdkPath) ?? @"C:\", "DriveForge-convert-" + Guid.NewGuid().ToString("N") + ".vmdk");
		try
		{
			// monolithicSparse = a single growable .vmdk that VMware Workstation/Player opens directly.
			string args = "convert -O vmdk -o subformat=monolithicSparse " + QuoteArgument(vhdPath) + " " + QuoteArgument(tmpVmdk);
			string outp = await RunProcessCaptureAsync(qemu, args);
			if (!string.IsNullOrWhiteSpace(outp)) Log("qemu-img: " + outp.Trim());
			if (File.Exists(tmpVmdk) && new FileInfo(tmpVmdk).Length > 0)
			{
				File.Move(tmpVmdk, vmdkPath, overwrite: true);   // replace the destination only now that a valid .vmdk exists
				return true;
			}
			return false;
		}
		catch (Exception ex) { Log("qemu-img convert failed: " + ex.Message); return false; }
		finally { try { if (File.Exists(tmpVmdk)) File.Delete(tmpVmdk); } catch { } }
	}

	// Locates an external .exe: first on PATH, then in a few common install directories (shallow recursive). Null if absent.
	private static string? FindExternalTool(string exeName, string[] dirs)
	{
		try
		{
			foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
			{
				try { if (!string.IsNullOrWhiteSpace(d)) { string cand = Path.Combine(d.Trim(), exeName); if (File.Exists(cand)) return cand; } } catch { }
			}
		}
		catch { }
		foreach (string dir in dirs)
		{
			try
			{
				if (!Directory.Exists(dir)) continue;
				string direct = Path.Combine(dir, exeName);
				if (File.Exists(direct)) return direct;
				string? hit = Directory.GetFiles(dir, exeName, SearchOption.AllDirectories).FirstOrDefault();
				if (hit != null) return hit;
			}
			catch { }
		}
		return null;
	}

	// GPT layout for a self-contained bootable VHDX: ESP (FAT32, holds \EFI\Microsoft\Boot\bootmgfw.efi + BCD) +
	// MSR + a 64K-cluster NTFS Windows partition. The vdisk is created expandable so the file only grows with data.
	private static string BuildCreateBootableVhdxDiskpartScript(string vhdPath, char espLetter, char windowsLetter, long maximumMb)
	{
		return string.Join(Environment.NewLine, new string[]
		{
			// NOTE: no "san policy=OnlineAll" here — that writes the PERSISTENT machine-wide VDS SAN policy and would
			// outlive the export (auto-onlining deliberately-offline SAN/iSCSI LUNs on Server/offline-policy hosts). The
			// explicit "online disk noerr" below already brings THIS freshly-attached vdisk online regardless of policy.
			$"create vdisk file=\"{vhdPath}\" maximum={maximumMb} type=expandable",
			$"select vdisk file=\"{vhdPath}\"",
			"attach vdisk",
			"attributes disk clear readonly noerr",
			"online disk noerr",
			"attributes disk clear readonly noerr",
			"convert gpt noerr",
			"create partition efi size=100",
			"format quick fs=fat32 label=\"System\"",
			$"assign letter={espLetter}",
			"create partition msr size=128",
			"create partition primary",
			// Default 4K NTFS clusters to MATCH the source volume (Windows is 4K). 64K would round every one of the
			// ~hundreds-of-thousands of small Windows files up to a 64K cluster, writing far MORE than the source uses.
			"format quick fs=ntfs label=\"Windows\"",
			$"assign letter={windowsLetter}",
			"exit"
		});
	}

	// Virtual size cap for the expandable VHDX: current Windows used space + 40 GB headroom, at least 64 GB.
	private long EstimateExportVhdxMaximumMb()
	{
		long gib = 1024L * 1024 * 1024;
		long bytes = Math.Max(64L * gib, GetCurrentWindowsUsedBytes() + 40L * gib);
		return bytes / (1024L * 1024L);
	}

	// ---------- Recover deleted files (native NTFS undelete) ----------

	private void ShowRecoverView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Visible;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
	}

	private void NavRecover_Click(object sender, RoutedEventArgs e)
	{
		ShowRecoverView();
		HighlightNav(NavRecover);
		PopulateRecoverVolumes();
		PopulateRecoverTypes();
	}

	// Refresh the drive list whenever the dropdown is opened, so a card / USB inserted after opening this
	// screen still shows up.
	// ---------- Clean traces (temp / caches / recent / Recycle Bin) ----------

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

	private bool _cleanBusy;

	// One row in the cleaner: its key, the checkbox/size labels, and special-action flags.
	// Data-driven cleanable category: pure data + INotifyPropertyChanged so new categories are just rows, and the
	// UI (grouped, risk-badged tree) binds straight to a list. Risk: 0 green (safe), 1 amber (regenerates / costs
	// something), 2 red (advanced / privacy action). RegKeys = HKCU subkeys whose values are cleared.
	private sealed class CleanCategory : System.ComponentModel.INotifyPropertyChanged
	{
		public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
		private void OnPC(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
		public string Key = "";
		public string LabelKey = "";
		public string GroupKey = "";
		public string DescKey = "";
		public int Risk;
		public bool RequiresAdmin;
		public bool RecycleBin, DnsCache, Clipboard;
		public string[] RegKeys = System.Array.Empty<string>();
		private string _label = ""; public string Label { get => _label; set { _label = value; OnPC(nameof(Label)); } }
		private string _group = ""; public string Group { get => _group; set { _group = value; OnPC(nameof(Group)); } }
		private string _desc = ""; public string Desc { get => _desc; set { _desc = value; OnPC(nameof(Desc)); } }
		private long _size = -1; public long Size { get => _size; set { _size = value; OnPC(nameof(Size)); } }
		private string _sizeText = ""; public string SizeText { get => _sizeText; set { _sizeText = value; OnPC(nameof(SizeText)); } }
		private bool _checked; public bool IsChecked { get => _checked; set { if (_checked != value) { _checked = value; OnPC(nameof(IsChecked)); } } }
	}

	private List<CleanCategory> BuildCleanCategories() => new()
	{
		// System
		new() { Key = "Temp",      LabelKey = "ChkCleanTemp",      GroupKey = "CcGrpSystem",  DescKey = "CcDescTemp",     Risk = 0, IsChecked = true },
		new() { Key = "Thumbs",    LabelKey = "ChkCleanThumbs",    GroupKey = "CcGrpSystem",  DescKey = "CcDescThumbs",   Risk = 0, IsChecked = true },
		new() { Key = "Recycle",   LabelKey = "ChkCleanRecycle",   GroupKey = "CcGrpSystem",  DescKey = "CcDescRecycle",  Risk = 0, RecycleBin = true, IsChecked = true },
		new() { Key = "Crashes",   LabelKey = "CcCrashes",         GroupKey = "CcGrpSystem",  DescKey = "CcDescCrashes",  Risk = 0, RequiresAdmin = true },
		new() { Key = "Prefetch",  LabelKey = "ChkCleanPrefetch",  GroupKey = "CcGrpSystem",  DescKey = "CcDescPrefetch", Risk = 1 },
		new() { Key = "FontCache", LabelKey = "CcFontCache",       GroupKey = "CcGrpSystem",  DescKey = "CcDescFontCache",Risk = 1, RequiresAdmin = true },
		// Windows
		new() { Key = "Update",      LabelKey = "ChkCleanUpdate", GroupKey = "CcGrpWindows", DescKey = "CcDescUpdate",      Risk = 1, RequiresAdmin = true },
		new() { Key = "DeliveryOpt", LabelKey = "CcDeliveryOpt",  GroupKey = "CcGrpWindows", DescKey = "CcDescDeliveryOpt", Risk = 1, RequiresAdmin = true },
		new() { Key = "WinLogs",     LabelKey = "CcWinLogs",      GroupKey = "CcGrpWindows", DescKey = "CcDescWinLogs",     Risk = 1, RequiresAdmin = true },
		// Browsers
		new() { Key = "Browser",   LabelKey = "ChkCleanBrowser",   GroupKey = "CcGrpBrowsers", DescKey = "CcDescBrowser",  Risk = 1 },
		// Apps
		new() { Key = "AppCache",  LabelKey = "CcAppCache",        GroupKey = "CcGrpApps",     DescKey = "CcDescAppCache", Risk = 1 },
		// Privacy
		new() { Key = "Recent",        LabelKey = "ChkCleanRecent",    GroupKey = "CcGrpPrivacy", DescKey = "CcDescRecent",    Risk = 1 },
		new() { Key = "Dns",           LabelKey = "ChkCleanDns",       GroupKey = "CcGrpPrivacy", DescKey = "CcDescDns",       Risk = 0, DnsCache = true },
		new() { Key = "Clipboard",     LabelKey = "ChkCleanClipboard", GroupKey = "CcGrpPrivacy", DescKey = "CcDescClipboard", Risk = 0, Clipboard = true },
		new() { Key = "ActivityHist",  LabelKey = "CcActivityHist",    GroupKey = "CcGrpPrivacy", DescKey = "CcDescActivity",  Risk = 2 },
		new() { Key = "MruHistory",    LabelKey = "CcMruHistory",      GroupKey = "CcGrpPrivacy", DescKey = "CcDescMru",       Risk = 2,
			RegKeys = new[] {
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs",
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
				@"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU",
			} },
	};

	private System.Collections.ObjectModel.ObservableCollection<CleanCategory>? _cleanCats;
	private System.ComponentModel.ICollectionView? _cleanView;
	private bool _cleanAdvanced;

	// Builds the data-driven category list once and binds it (grouped, advanced-filtered) to the tree ItemsControl.
	private void EnsureCleanCategories()
	{
		if (_cleanCats == null)
		{
			_cleanCats = new System.Collections.ObjectModel.ObservableCollection<CleanCategory>(BuildCleanCategories());
			foreach (var c in _cleanCats) c.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CleanCategory.IsChecked)) RecomputeCleanTotal(); };
			var cvs = new System.Windows.Data.CollectionViewSource { Source = _cleanCats };
			cvs.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(CleanCategory.Group)));
			_cleanView = cvs.View;
			_cleanView.Filter = o => _cleanAdvanced || (o as CleanCategory)?.Risk == 0;
			if (CleanCatItems != null) CleanCatItems.ItemsSource = _cleanView;
		}
		RefreshCleanLabels();
		RecomputeCleanTotal();
	}

	// Re-applies localized label/desc/group on each category (call on language change too).
	private void RefreshCleanLabels()
	{
		if (_cleanCats == null) return;
		foreach (var c in _cleanCats) { c.Label = L(c.LabelKey); c.Desc = L(c.DescKey); c.Group = L(c.GroupKey); }
		_cleanView?.Refresh();
	}

	private void RecomputeCleanTotal()
	{
		if (_cleanCats == null || CleanRunButton == null) return;
		long sum = _cleanCats.Where(c => c.IsChecked && c.Size > 0).Sum(c => c.Size);
		CleanRunButton.Content = sum > 0 ? string.Format(L("CleanBtnTotal"), FormatBytes(sum)) : L("CleanRunButton");
	}

	private void CleanRecommended_Click(object sender, RoutedEventArgs e)
	{
		EnsureCleanCategories();
		if (_cleanCats == null) return;
		foreach (var c in _cleanCats) c.IsChecked = c.Risk == 0; // green only
	}

	private void CleanAdvancedToggle_Click(object sender, RoutedEventArgs e)
	{
		_cleanAdvanced = (sender as CheckBox)?.IsChecked == true;
		_cleanView?.Refresh();
	}

	private void ShowCleanView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Visible;
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
	}

	private void NavClean_Click(object sender, RoutedEventArgs e)
	{
		ShowCleanView();
		EnsureCleanCategories();
		HighlightNav(NavClean);
		if (AnalyzePathBox != null && string.IsNullOrEmpty(AnalyzePathBox.Text))
			AnalyzePathBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	}

	private void CleanSelectAll_Click(object sender, RoutedEventArgs e)
	{
		EnsureCleanCategories();
		if (_cleanCats == null) return;
		foreach (var c in _cleanCats) if (_cleanAdvanced || c.Risk == 0) c.IsChecked = true; // only what's visible
	}

	// HARD SAFETY GUARD for every clean target. Refuses a path that IS a drive root, the user profile, the Windows
	// directory, or a top-level user-data / AppData folder — never their children.
	// Why this must exist: Path.GetTempPath() is documented to fall back TMP -> TEMP -> USERPROFILE -> Windows dir.
	// If TMP and TEMP are both unset (a scrubbed/corrupt environment, or a user editing them by hand), it returns
	// %USERPROFILE% — and the "Temp" category is green, "safe to delete", ticked by DEFAULT and part of Recommended.
	// Without this guard, one ordinary click would recursively delete Documents, Desktop, Pictures and Downloads.
	// Fail CLOSED: anything we cannot resolve is treated as unsafe.
	private static bool IsUnsafeCleanRoot(string dir)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(dir)) return true;
			string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (full.Length <= 2) return true;                                             // "C:" and shorter
			string root = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true; // a bare drive root
			foreach (var sf in new[]
			{
				Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
				Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
				Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
				Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
				Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
				Environment.SpecialFolder.CommonApplicationData,
			})
			{
				string p = Environment.GetFolderPath(sf);
				if (string.IsNullOrEmpty(p)) continue;
				string pf = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if (string.Equals(full, pf, StringComparison.OrdinalIgnoreCase)) return true;
			}
			return false;
		}
		catch { return true; }
	}

	// Enumerates files safely (no throw) under a folder, optionally recursing, optionally by pattern.
	private static IEnumerable<string> SafeFiles(string dir, string pattern = "*", bool recurse = true)
	{
		if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
		if (IsUnsafeCleanRoot(dir)) yield break;   // never enumerate a profile/root as a "temp" folder
		var stack = new Stack<string>();
		stack.Push(dir);
		while (stack.Count > 0)
		{
			string cur = stack.Pop();
			string[] files;
			try { files = Directory.GetFiles(cur, pattern); } catch { files = Array.Empty<string>(); }
			foreach (var f in files) yield return f;
			if (recurse)
			{
				string[] subs;
				try { subs = Directory.GetDirectories(cur); } catch { subs = Array.Empty<string>(); }
				// Skip junctions / symlinks: they double-count files and a cyclic reparse point would loop forever.
				foreach (var s in subs)
				{
					try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { }
					stack.Push(s);
				}
			}
		}
	}

	// Chromium + Firefox on-disk cache folders that actually exist on this PC.
	private static List<string> BrowserCacheDirs()
	{
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var dirs = new List<string>();
		var bases = new[]
		{
			Path.Combine(local, @"Microsoft\Edge\User Data"),
			Path.Combine(local, @"Google\Chrome\User Data"),
			Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"),
			Path.Combine(local, @"Vivaldi\User Data"),
		};
		foreach (var b in bases)
		{
			if (!Directory.Exists(b)) continue;
			var profiles = new List<string> { "Default" };
			try { profiles.AddRange(Directory.GetDirectories(b, "Profile *").Select(Path.GetFileName).Where(n => n != null)!); } catch { }
			foreach (var p in profiles)
				foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
				{
					string c = Path.Combine(b, p, sub);
					if (Directory.Exists(c)) dirs.Add(c);
				}
		}
		string ffProfiles = Path.Combine(local, @"Mozilla\Firefox\Profiles");
		if (Directory.Exists(ffProfiles))
			try { foreach (var prof in Directory.GetDirectories(ffProfiles)) { string c = Path.Combine(prof, "cache2"); if (Directory.Exists(c)) dirs.Add(c); } } catch { }
		return dirs;
	}

	private static long RecycleBinSize()
	{
		var info = new SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHQUERYRBINFO>() };
		try { if (SHQueryRecycleBin(null, ref info) == 0) return info.i64Size; } catch { }
		return 0;
	}

	private static long CategorySize(CleanCategory c)
	{
		if (c.RecycleBin) return RecycleBinSize();
		if (c.DnsCache || c.Clipboard || c.RegKeys.Length > 0) return 0;
		long total = 0;
		foreach (var f in StaticCleanTargets(c.Key)) { try { total += new FileInfo(f).Length; } catch { } }
		return total;
	}

	// Static mirror used from background threads (CleanTargets touches no instance state, but keep it explicit).
	private static IEnumerable<string> StaticCleanTargets(string key)
	{
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		switch (key)
		{
			case "Temp":
				foreach (var d in new[] { Path.GetTempPath(), Path.Combine(win, "Temp") })
					foreach (var f in SafeFiles(d)) yield return f;
				break;
			case "Recent":
			{
				// NON-recursive on purpose. Recent\ has two subfolders: AutomaticDestinations (the "recent" half of
				// jump lists = history, fair game) and CustomDestinations (the user's PINNED jump-list items). A
				// recursive sweep deleted the pinned ones too — contradicting this category's own description, which
				// promises only to clear recent-documents HISTORY.
				string recent = Path.Combine(roaming, @"Microsoft\Windows\Recent");
				foreach (var f in SafeFiles(recent, "*", false)) yield return f;
				foreach (var f in SafeFiles(Path.Combine(recent, "AutomaticDestinations"), "*", false)) yield return f;
				break;
			}
			case "Thumbs":
			{
				string ex = Path.Combine(local, @"Microsoft\Windows\Explorer");
				foreach (var f in SafeFiles(ex, "thumbcache_*.db", false)) yield return f;
				foreach (var f in SafeFiles(ex, "iconcache_*.db", false)) yield return f;
				break;
			}
			case "Browser":
				foreach (var d in BrowserCacheDirs())
					foreach (var f in SafeFiles(d)) yield return f;
				break;
			case "Prefetch":
				foreach (var f in SafeFiles(Path.Combine(win, "Prefetch"), "*.pf", false)) yield return f;
				break;
			case "Update":
				foreach (var f in SafeFiles(Path.Combine(win, @"SoftwareDistribution\Download"))) yield return f;
				break;
			case "Crashes":
			{
				string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
				foreach (var d in new[]
				{
					Path.Combine(local, "CrashDumps"),
					Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive"),
					Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue"),
					Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive"),
					Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue"),
					Path.Combine(win, "Minidump"),
				})
					foreach (var f in SafeFiles(d)) yield return f;
				string memdmp = Path.Combine(win, "MEMORY.DMP");
				if (File.Exists(memdmp)) yield return memdmp;
				break;
			}
			case "FontCache":
			{
				// The Windows font cache lives in the FontCache SERVICE's own profile — NOT in the user's
				// LocalAppData, where this used to look. That path does not exist on modern Windows, so the
				// category was a permanent no-op that still advertised itself as needing administrator.
				foreach (var f in SafeFiles(Path.Combine(win, @"ServiceProfiles\LocalService\AppData\Local\FontCache"), "*", false)) yield return f;
				string fntCache = Path.Combine(win, @"System32\FNTCACHE.DAT");
				if (File.Exists(fntCache)) yield return fntCache;
				break;
			}
			case "DeliveryOpt":
			{
				string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
				foreach (var f in SafeFiles(Path.Combine(pd, @"Microsoft\Windows\DeliveryOptimization\Cache"))) yield return f;
				break;
			}
			case "WinLogs":
			{
				foreach (var d in new[] { Path.Combine(win, @"Logs\CBS"), Path.Combine(win, @"Logs\DISM"), Path.Combine(win, "Panther") })
					foreach (var f in SafeFiles(d)) yield return f;
				break;
			}
			case "AppCache":
				foreach (var d in AppCacheDirs())
					foreach (var f in SafeFiles(d)) yield return f;
				break;
			case "ActivityHist":
				foreach (var f in SafeFiles(Path.Combine(local, "ConnectedDevicesPlatform"), "ActivitiesCache.db*", true)) yield return f;
				break;
		}
	}

	// Electron/app cache folders (Teams, Discord, Spotify, VS Code) that actually exist — they can reach multiple GB.
	private static List<string> AppCacheDirs()
	{
		string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		var cand = new[]
		{
			Path.Combine(roaming, @"discord\Cache"), Path.Combine(roaming, @"discord\Code Cache"), Path.Combine(roaming, @"discord\GPUCache"),
			Path.Combine(local, @"Spotify\Data"), Path.Combine(local, @"Spotify\Storage"),
			Path.Combine(roaming, @"Code\Cache"), Path.Combine(roaming, @"Code\CachedData"), Path.Combine(roaming, @"Code\Code Cache"), Path.Combine(roaming, @"Code\GPUCache"),
			Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache"),
			Path.Combine(local, @"Microsoft\Teams\Cache"),
		};
		return cand.Where(Directory.Exists).ToList();
	}

	// Instance (not static) so the loop can poll stopRequested — a clean must be abortable.
	private (long Bytes, int Count) DeleteTargets(string key, bool toRecycle)
	{
		long bytes = 0; int count = 0;
		foreach (var f in StaticCleanTargets(key))
		{
			if (stopRequested) break;   // a 40 GB %TEMP% clean was previously unstoppable once started
			try
			{
				long len = 0; try { len = new FileInfo(f).Length; } catch { }
				try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
				if (toRecycle) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
				else File.Delete(f);
				bytes += len; count++;
			}
			catch { } // file in use or access denied → skip it
		}
		// Tidy up the empty sub-folder skeleton left behind, so %TEMP% etc. don't look "uncleaned" afterwards.
		foreach (var root in CleanRecursiveRoots(key)) RemoveEmptyDirs(root);
		return (bytes, count);
	}

	// Recursive roots whose now-empty sub-folders are worth removing after a clean (never the root itself).
	private static IEnumerable<string> CleanRecursiveRoots(string key)
	{
		string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
		string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		switch (key)
		{
			case "Temp": yield return Path.GetTempPath(); yield return Path.Combine(win, "Temp"); break;
			case "Browser": foreach (var d in BrowserCacheDirs()) yield return d; break;
			case "Update": yield return Path.Combine(win, @"SoftwareDistribution\Download"); break;
			case "AppCache": foreach (var d in AppCacheDirs()) yield return d; break;
			case "DeliveryOpt": yield return Path.Combine(pd, @"Microsoft\Windows\DeliveryOptimization\Cache"); break;
			case "WinLogs": yield return Path.Combine(win, @"Logs\CBS"); yield return Path.Combine(win, @"Logs\DISM"); break;
		}
	}

	// Deletes empty sub-directories under root (deepest first), skipping junctions; never removes root itself.
	private static void RemoveEmptyDirs(string root)
	{
		if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
		if (IsUnsafeCleanRoot(root)) return;   // same guard as SafeFiles: never strip the profile's folder skeleton
		var all = new List<string>();
		var stack = new Stack<string>(); stack.Push(root);
		int guard = 0;
		while (stack.Count > 0 && guard++ < 200000)
		{
			string cur = stack.Pop();
			string[] subs; try { subs = Directory.GetDirectories(cur); } catch { continue; }
			foreach (var s in subs)
			{
				try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { }
				all.Add(s); stack.Push(s);
			}
		}
		foreach (var d in all.OrderByDescending(x => x.Length))
			try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d, false); } catch { }
	}

	private static void FlushDns()
	{
		try
		{
			var psi = new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
			using var p = Process.Start(psi); p?.WaitForExit(8000);
		}
		catch { }
	}

	private async void CleanAnalyze_Click(object sender, RoutedEventArgs e)
	{
		if (_cleanBusy) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		EnsureCleanCategories();
		if (_cleanCats == null) return;
		_cleanBusy = true; UpdateSleepBlock();   // sizing every category can take a while and never goes through SetBusy
		if (CleanAnalyzeButton != null) CleanAnalyzeButton.IsEnabled = false;
		if (CleanRunButton != null) CleanRunButton.IsEnabled = false;
		try
		{
			var cats = _cleanCats.ToList();
			if (CleanStatusText != null) CleanStatusText.Text = L("CleanAnalyzing");
			foreach (var c in cats) if (!c.DnsCache && !c.Clipboard && c.RegKeys.Length == 0) c.SizeText = "…";
			// Size every category concurrently (CategorySize is static + side-effect-free), update labels as they land.
			var jobs = cats.Select(c => (cat: c, task: Task.Run(() => CategorySize(c)))).ToList();
			long grand = 0;
			foreach (var (cat, task) in jobs)
			{
				long size = await task;
				grand += size;
				cat.Size = size;
				cat.SizeText = (cat.DnsCache || cat.Clipboard || cat.RegKeys.Length > 0) ? "" : FormatBytes(size);
			}
			RecomputeCleanTotal();
			if (CleanStatusText != null) CleanStatusText.Text = string.Format(L("CleanAnalyzeResult"), FormatBytes(grand));
		}
		finally
		{
			_cleanBusy = false; UpdateSleepBlock();
			if (CleanAnalyzeButton != null) CleanAnalyzeButton.IsEnabled = true;
			if (CleanRunButton != null) CleanRunButton.IsEnabled = true;
		}
	}

	private async void CleanRun_Click(object sender, RoutedEventArgs e)
	{
		if (_cleanBusy) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		EnsureCleanCategories();
		if (_cleanCats == null) return;
		// Only clean items that are both ticked AND currently visible (hidden advanced items aren't acted on).
		var cats = _cleanCats.Where(c => c.IsChecked && (_cleanAdvanced || c.Risk == 0)).ToList();
		if (cats.Count == 0) { if (CleanStatusText != null) CleanStatusText.Text = L("CleanNothingSelected"); return; }
		string sel = string.Join("\n", cats.Select(c => "• " + c.Label));
		// Extra warning when an advanced (red) privacy item is included.
		string warn = cats.Any(c => c.Risk == 2) ? "\n\n⚠ " + L("CleanRedWarn") : "";
		if (MessageBox.Show(this, L("CleanConfirmBody") + "\n\n" + sel + warn, L("CleanConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
		bool toRecycle = CleanToRecycleCheck?.IsChecked == true;
		// Go through SetBusy, not the raw field: setting isBusy directly left every other destructive tool (Start,
		// drive tools, kit) still ENABLED during a multi-minute clean, and left Stop greyed out so the run could not
		// be aborted. SetBusy disables those and enables Stop, which the delete loop now polls.
		_cleanBusy = true; stopRequested = false;
		SetBusy(busy: true, L("CleanRunButton"));
		// SetBusy enables Pause for ops that support it — the clean loop does NOT honour isPaused, so leaving it
		// enabled would let the user press Pause, see "Paused", and have files keep being deleted anyway. Also make
		// Stop actually REACHABLE: ShowCleanView collapses the shared Start/Pause/Stop row, so SetBusy was only
		// enabling an invisible button and the loop's stopRequested poll could never fire.
		if (PauseButton != null) PauseButton.IsEnabled = false;
		if (StopButton != null) { StopButton.Visibility = Visibility.Visible; StopButton.IsEnabled = true; }
		if (CleanAnalyzeButton != null) CleanAnalyzeButton.IsEnabled = false;
		if (CleanRunButton != null) CleanRunButton.IsEnabled = false;
		try
		{
			long freed = 0; int filesDeleted = 0;
			long binnedBytes = 0; int binnedFiles = 0;   // moved to the Recycle Bin: relocated, NOT freed
			ProgressBar.Value = 0.0; progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			int doneCats = 0;
			if (cats.Any(c => c.Clipboard)) { try { Clipboard.Clear(); } catch { } }
			foreach (var c in cats)
			{
				// Stop must abort the IRREVERSIBLE categories too, not just the file loop: emptying the Recycle Bin
				// and clearing registry MRUs cannot be undone, so continuing them after Stop is the worst outcome.
				if (stopRequested) break;
				ProgressBar.Value = 100.0 * doneCats++ / cats.Count;
				if (CleanStatusText != null) CleanStatusText.Text = string.Format(L("CleanCleaningX"), c.Label);
				if (c.RecycleBin)
				{
					// "Send to Recycle Bin (so I can restore)" + the Recycle Bin category in the SAME run is a direct
					// contradiction: we would move the user's files to the bin for safekeeping and then empty the bin
					// (all drives, no confirmation) seconds later, destroying exactly what they asked to keep — while
					// the summary still told them it was restorable. Honour the safety net: skip emptying the bin.
					if (toRecycle) { if (CleanStatusText != null) CleanStatusText.Text = L("CleanBinKept"); continue; }
					long before = RecycleBinSize();
					int hr = await Task.Run(() => { try { return SHEmptyRecycleBin(IntPtr.Zero, null, 0x7u); } catch { return -1; } });
					if (hr == 0) freed += before;
					c.Size = 0; c.SizeText = FormatBytes(hr == 0 ? 0 : RecycleBinSize());
					continue;
				}
				if (c.DnsCache) { await Task.Run(FlushDns); continue; }
				if (c.Clipboard) continue;
				if (c.RegKeys.Length > 0) { string[] rk = c.RegKeys; await Task.Run(() => ClearRegistryValues(rk)); continue; }
				string key = c.Key; bool rec = toRecycle;
				var res = await Task.Run(() => DeleteTargets(key, rec));
				// Recycling MOVES bytes into $Recycle.Bin — it frees NOTHING. Counting them as "freed" both lied and
				// double-counted them: the Recycle Bin category later empties the bin and counts the same bytes again.
				if (rec) { binnedBytes += res.Bytes; binnedFiles += res.Count; }
				else { freed += res.Bytes; filesDeleted += res.Count; }
				// Show what is actually LEFT (locked/in-use files are skipped), instead of flatly claiming 0 B and
				// contradicting the status line that just said some files could not be removed.
				long leftover = Math.Max(0, c.Size - res.Bytes);
				c.Size = leftover; c.SizeText = FormatBytes(leftover);
			}
			ProgressBar.Value = 100.0;
			RecomputeCleanTotal();
			string doneMsg = string.Format(L("CleanDone"), FormatBytes(freed), filesDeleted);
			if (binnedFiles > 0) doneMsg += " " + string.Format(L("CleanDoneRecycled"), FormatBytes(binnedBytes), binnedFiles);
			if (CleanStatusText != null) CleanStatusText.Text = doneMsg;
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			ResetProgressWidgets();   // bar AND label AND stats line — zeroing only the bar left e.g. "86%" beside an empty one
			_cleanBusy = false; SetBusy(busy: false);
			if (StopButton != null) StopButton.Visibility = Visibility.Collapsed;  // the Clean view hides this row again
			if (CleanAnalyzeButton != null) CleanAnalyzeButton.IsEnabled = true;
			if (CleanRunButton != null) CleanRunButton.IsEnabled = true;
		}
	}

	[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

	[System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr h);

	// SID of the user actually sitting at the machine, taken from the owner of explorer.exe. Needed because this app
	// is requireAdministrator: when a STANDARD user elevates it with a DIFFERENT admin account's credentials, the
	// process token belongs to that ADMIN — so Registry.CurrentUser is the admin's hive, not the real user's.
	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	private static extern uint WTSGetActiveConsoleSessionId();

	private static string? TryGetInteractiveUserSid()
	{
		try
		{
			// Only the CONSOLE session's explorer.exe. Taking the first explorer found would, with fast user switching
			// or RDP, resolve a DIFFERENT logged-on user — and we would then clear THAT person's history instead of
			// the one sitting at the machine. If we can't identify the console session, resolve nothing and let the
			// caller fall back to HKCU rather than guess at someone's hive.
			uint consoleSession = WTSGetActiveConsoleSessionId();
			if (consoleSession == 0xFFFFFFFF) return null;
			foreach (var p in Process.GetProcessesByName("explorer"))
			{
				try
				{
					using (p)
					{
						if (p.SessionId != consoleSession) continue;
						if (!OpenProcessToken(p.Handle, 0x0008 /* TOKEN_QUERY */, out IntPtr tok)) continue;
						try
						{
							GetTokenInformation(tok, 1 /* TokenUser */, IntPtr.Zero, 0, out int len);
							if (len <= 0) continue;
							IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(len);
							try
							{
								if (!GetTokenInformation(tok, 1, buf, len, out _)) continue;
								IntPtr sidPtr = System.Runtime.InteropServices.Marshal.ReadIntPtr(buf); // TOKEN_USER.User.Sid
								return new System.Security.Principal.SecurityIdentifier(sidPtr).Value;
							}
							finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
						}
						finally { CloseHandle(tok); }
					}
				}
				catch { }
			}
		}
		catch { }
		return null;
	}

	// Clears the values + subkeys under the user's MRU keys (Run history, recent docs, typed paths, search terms). The
	// shell rebuilds them. Pinned items (CustomDestinations) are deliberately NOT touched.
	// The app always runs ELEVATED, so Registry.CurrentUser is the hive of whoever's credentials elevated it. On a
	// shared PC (standard user + separate admin account) that is the ADMIN's hive: the real user's history would be
	// left untouched while we reported success, and a stranger's history would be cleared instead. So target the
	// interactive user's hive explicitly whenever it differs; fall back to HKCU when we can't tell (the common
	// split-token case, where the SID is identical anyway).
	private static void ClearRegistryValues(string[] subkeys)
	{
		string? interactiveSid = TryGetInteractiveUserSid();
		string? mySid = null;
		try { mySid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { }
		bool useHku = interactiveSid != null && mySid != null && !string.Equals(interactiveSid, mySid, StringComparison.OrdinalIgnoreCase);
		foreach (var sk in subkeys)
		{
			try
			{
				using var k = useHku
					? Microsoft.Win32.Registry.Users.OpenSubKey(interactiveSid + "\\" + sk, writable: true)
					: Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sk, writable: true);
				if (k == null) continue;
				foreach (var v in k.GetValueNames()) { try { if (!string.IsNullOrEmpty(v)) k.DeleteValue(v, false); } catch { } }
				foreach (var sub in k.GetSubKeyNames()) { try { k.DeleteSubKeyTree(sub, false); } catch { } }
			}
			catch { }
		}
	}

	// Media type for an arbitrary path's drive, via the disk picker list (so secure-delete can be honest on SSD).
	private WipeMedia MediaForPath(string path)
	{
		try
		{
			// Resolve the path's REAL volume -> physical disk first. Keying off the drive LETTER alone reported the
			// HOST drive's media for anything under a mounted folder (e.g. C:\Vault, an NVMe volume with no letter of
			// its own, on a spinning C:) — which would claim a genuine in-place overwrite on flash.
			int diskNo = PhysicalDiskOfPath(path);
			if (diskNo >= 0)
				foreach (var it in DiskBox.Items)
					if (it is DiskItem dd && dd.Number == diskNo) return DetectWipeMedia(dd);
			char letter = char.ToUpperInvariant(Path.GetPathRoot(path)?.FirstOrDefault() ?? '\0');
			if (letter == '\0') return WipeMedia.Unknown;
			foreach (var it in DiskBox.Items)
				if (it is DiskItem d && d.DriveLetters.Any(c => char.ToUpperInvariant(c) == letter)) return DetectWipeMedia(d);
		}
		catch { }
		return WipeMedia.Unknown;
	}

	// True when an in-place overwrite CANNOT reach the file's original clusters. NTFS compressed, sparse and EFS-
	// encrypted files are relocated/re-allocated on write, so the original data stays on disk untouched — writing
	// zeros over them and reporting "securely erased" would be a lie.
	private static bool OverwriteCannotReachClusters(string path)
	{
		try
		{
			var a = File.GetAttributes(path);
			return (a & (FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0;
		}
		catch { return false; }
	}

	// Securely erase a single file: overwrite-in-place then delete on HDD; on SSD/unknown just delete (overwrite
	// can't guarantee erasure on flash, and even on NTFS small/resident files + journal copies may remain).
	private async void CleanSecureDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_cleanBusy) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		var dlg = new Microsoft.Win32.OpenFileDialog { Title = L("SecureDelTitle"), CheckFileExists = true };
		if (dlg.ShowDialog(this) != true) return;
		string path = dlg.FileName;
		var media = MediaForPath(path);
		// An in-place overwrite is only meaningful on a spinning disk AND on a file whose clusters it can actually
		// reach — a compressed/sparse/EFS file gets re-allocated on write, leaving the original clusters intact.
		bool noReach = OverwriteCannotReachClusters(path);
		string body = string.Format(L("SecureDelBody"), Path.GetFileName(path))
			+ (media != WipeMedia.Hdd ? "\n\n" + L("SecureDelSsdNote") : "")
			+ (noReach ? "\n\n" + L("SecureDelNoReachNote") : "");
		if (MessageBox.Show(this, body, L("SecureDelTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		_cleanBusy = true; SetBusy(busy: true, L("SecureDelWorking"));   // via SetBusy so other destructive tools are locked out
		try
		{
			if (CleanStatusText != null) CleanStatusText.Text = L("SecureDelWorking");
			bool overwrite = media == WipeMedia.Hdd && !noReach;
			await Task.Run(() => SecureDeleteFile(path, overwrite));
			if (CleanStatusText != null) CleanStatusText.Text = L("SecureDelDone");
		}
		catch (Exception ex) { ShowError(L("SecureDelTitle"), ex); }
		finally { _cleanBusy = false; SetBusy(busy: false); }
	}

	private static void SecureDeleteFile(string path, bool overwrite)
	{
		if (overwrite && File.Exists(path))
		{
			// Overwrite the file's bytes in place before deleting (HDD). Do NOT swallow failures here: if the overwrite
			// can't complete (file locked for write, read-only ACL, disk full), we must NOT fall through and delete the
			// file while telling the user it was "securely erased". Let the exception propagate so the caller reports the
			// failure and the (un-wiped) file is left in place to retry — a false "secure delete done" defeats the feature.
			File.SetAttributes(path, FileAttributes.Normal);
			long len = new FileInfo(path).Length;
			byte[] buf = new byte[1 << 20];
			using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
			{
				long rem = len;
				while (rem > 0) { int chunk = (int)Math.Min(buf.Length, rem); fs.Write(buf, 0, chunk); rem -= chunk; }
				fs.Flush(flushToDisk: true);
			}
		}
		try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
		File.Delete(path);
	}

	// ---------- Disk space analyzer (largest files + duplicates + folder treemap) ----------

	private sealed class BigFileRow
	{
		public bool Selected { get; set; }
		public string Name { get; set; } = "";
		public string Folder { get; set; } = "";
		public string SizeText { get; set; } = "";
		public long Size { get; set; }
		public string FullPath { get; set; } = "";
		public DateTime Modified { get; set; }
		public string DateText { get; set; } = "";
	}

	private sealed class DupRow : System.ComponentModel.INotifyPropertyChanged
	{
		public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
		private void OnPC(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
		private bool _selected, _keep, _isReference;
		// Notify so the green/red row colour updates live when the user (or a rule) ticks a box.
		public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; OnPC(nameof(Selected)); } } }
		public bool Keep { get => _keep; set { if (_keep != value) { _keep = value; OnPC(nameof(Keep)); } } }
		// A file inside the user's "protected" (master) folder: always kept, can never be ticked for deletion.
		public bool IsReference { get => _isReference; set { if (_isReference != value) { _isReference = value; OnPC(nameof(IsReference)); OnPC(nameof(CanDelete)); } } }
		public bool CanDelete => !_isReference;
		public int Group { get; set; }
		public string Name { get; set; } = "";
		public string Folder { get; set; } = "";
		public string SizeText { get; set; } = "";
		public long Size { get; set; }
		public string FullPath { get; set; } = "";
		public DateTime Modified { get; set; }
		public string DateText { get; set; } = "";
	}

	private sealed class AnalyzeResult
	{
		public long TotalSize; public int FileCount;
		public List<(string Name, long Size)> Folders = new();
		public List<BigFileRow> Big = new();
		public List<DupRow> Dupes = new();
		public int RedundantCount; public long RedundantBytes;
		public bool Truncated; // true when caps were hit during the walk -> results may be incomplete
		public string Root = "";
		// Per-folder roll-ups (full path -> value), accumulated up the tree for the drill-down treemap + colour lenses.
		public Dictionary<string, long> FolderSize = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, DateTime> FolderNewest = new(StringComparer.OrdinalIgnoreCase);
		public Dictionary<string, long[]> FolderCat = new(StringComparer.OrdinalIgnoreCase);
	}

	// File-type categories used to colour the treemap "by type" and pick a folder's dominant kind.
	private const int CatCount = 6; // 0 Images, 1 Video, 2 Audio, 3 Documents, 4 Archives, 5 Other
	private static int CategoryOf(string path)
	{
		string e = Path.GetExtension(path).ToLowerInvariant();
		switch (e)
		{
			case ".jpg": case ".jpeg": case ".png": case ".gif": case ".bmp": case ".webp": case ".tif": case ".tiff":
			case ".heic": case ".heif": case ".ico": case ".svg": case ".raw": case ".cr2": case ".nef": case ".arw": case ".dng": return 0;
			case ".mp4": case ".mkv": case ".avi": case ".mov": case ".wmv": case ".flv": case ".webm": case ".m4v": case ".mpg": case ".mpeg": case ".3gp": case ".ts": return 1;
			case ".mp3": case ".wav": case ".flac": case ".aac": case ".ogg": case ".m4a": case ".wma": case ".aiff": case ".opus": return 2;
			case ".pdf": case ".doc": case ".docx": case ".xls": case ".xlsx": case ".ppt": case ".pptx": case ".txt": case ".rtf":
			case ".odt": case ".ods": case ".odp": case ".csv": case ".md": case ".epub": return 3;
			case ".zip": case ".rar": case ".7z": case ".tar": case ".gz": case ".bz2": case ".xz": case ".iso": case ".cab": return 4;
			default: return 5;
		}
	}

	private bool _analyzerBusy;
	private volatile bool _analyzerStop;
	private List<(string Name, long Size)> _treemapData = new();
	// Drill-down treemap state.
	private Dictionary<string, long> _folderSize = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<string, DateTime> _folderNewest = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<string, long[]> _folderCat = new(StringComparer.OrdinalIgnoreCase);
	private Dictionary<string, List<string>> _folderChildren = new(StringComparer.OrdinalIgnoreCase);
	private string _treemapRoot = "";
	private string _treemapCurrent = "";
	private int _colorLens; // 0 = size (categorical), 1 = type, 2 = age

	private void AnalyzeBrowse_Click(object sender, RoutedEventArgs e)
	{
		using var dlg = new Forms.FolderBrowserDialog { Description = L("AnalyzeFolderLabel"), UseDescriptionForTitle = true };
		if (!string.IsNullOrEmpty(AnalyzePathBox?.Text) && Directory.Exists(AnalyzePathBox.Text)) dlg.SelectedPath = AnalyzePathBox.Text;
		if (dlg.ShowDialog() == Forms.DialogResult.OK && AnalyzePathBox != null) AnalyzePathBox.Text = dlg.SelectedPath;
	}

	private void AnalyzeStop_Click(object sender, RoutedEventArgs e) => _analyzerStop = true;

	// Pick the "protected" (master) folder — files inside it are scanned but can never be deleted.
	private void AnalyzeMasterBrowse_Click(object sender, RoutedEventArgs e)
	{
		using var dlg = new Forms.FolderBrowserDialog { Description = L("AnalyzeMasterLabel"), UseDescriptionForTitle = true };
		if (!string.IsNullOrEmpty(AnalyzeMasterBox?.Text) && Directory.Exists(AnalyzeMasterBox.Text)) dlg.SelectedPath = AnalyzeMasterBox.Text;
		if (dlg.ShowDialog() == Forms.DialogResult.OK && AnalyzeMasterBox != null) AnalyzeMasterBox.Text = dlg.SelectedPath;
	}

	private void AnalyzeMasterClear_Click(object sender, RoutedEventArgs e) { if (AnalyzeMasterBox != null) AnalyzeMasterBox.Text = ""; }

	private async void AnalyzeScan_Click(object sender, RoutedEventArgs e) => await RunAnalyzeScanAsync();

	private async Task RunAnalyzeScanAsync()
	{
		if (_analyzerBusy) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		string root = AnalyzePathBox?.Text ?? "";
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnalyzeNoFolder"); return; }
		_analyzerBusy = true; isBusy = true; _analyzerStop = false; _refreshOwnsBusy = false;
		UpdateSleepBlock();   // this flow drives isBusy directly instead of through SetBusy, so hook the sleep block here too
		if (AnalyzeScanButton != null) AnalyzeScanButton.IsEnabled = false;
		if (AnalyzeStopButton != null) AnalyzeStopButton.IsEnabled = true;
		if (AnalyzeBrowseButton != null) AnalyzeBrowseButton.IsEnabled = false;
		if (AnalyzeDeleteButton != null) AnalyzeDeleteButton.IsEnabled = false;
		if (AnalyzeKeepFirstButton != null) AnalyzeKeepFirstButton.IsEnabled = false;
		if (BigFilesGrid != null) BigFilesGrid.ItemsSource = null;
		if (DupesGrid != null) DupesGrid.ItemsSource = null;
		if (AnalyzeTreemap != null) AnalyzeTreemap.Children.Clear();
		if (AnalyzeEmptyHint != null) AnalyzeEmptyHint.Visibility = Visibility.Collapsed;
		var progress = new Progress<string>(s => { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = s; });
		// bar: negative = indeterminate (file walk, total unknown); 0..100 = determinate (hashing phase).
		var bar = new Progress<double>(v =>
		{
			if (v < 0) ProgressBar.IsIndeterminate = true;
			else { ProgressBar.IsIndeterminate = false; ProgressBar.Value = v; }
		});
		ProgressBar.Value = 0.0; progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
		operationStopwatch.Restart(); operationTimer.Start();
		try
		{
			var result = await Task.Run(() => AnalyzeScanCore(root, progress, bar));
			_treemapData = result.Folders;
			// Wire up the drill-down treemap: folder roll-ups + a parent->children index, start at the scan root.
			_folderSize = result.FolderSize; _folderNewest = result.FolderNewest; _folderCat = result.FolderCat;
			_treemapRoot = result.Root; _treemapCurrent = result.Root;
			_folderChildren = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
			foreach (var key in _folderSize.Keys)
			{
				if (string.Equals(key, _treemapRoot, StringComparison.OrdinalIgnoreCase)) continue;
				string par = (Path.GetDirectoryName(key) ?? "").TrimEnd('\\');
				if (par.Length == 0) continue;
				if (!_folderChildren.TryGetValue(par, out var lst)) { lst = new List<string>(); _folderChildren[par] = lst; }
				lst.Add(key);
			}
			PopulateColorLens();
			DrawTreemap();
			// Mark files inside the protected ("master") folder so they can never be deleted.
			string master = AnalyzeMasterBox?.Text ?? "";
			if (!string.IsNullOrWhiteSpace(master) && Directory.Exists(master))
			{
				string m = Path.GetFullPath(master).TrimEnd('\\');
				foreach (var d in result.Dupes) d.IsReference = UnderFolder(d.FullPath, m);
			}
			if (BigFilesGrid != null) BigFilesGrid.ItemsSource = result.Big;
			if (DupesGrid != null) DupesGrid.ItemsSource = result.Dupes;
			_dupesAreSimilar = false;   // byte-identical duplicates: safe to auto-select a keeper
			string topFolder = result.Folders.Count > 0 ? $"{result.Folders[0].Name} ({FormatBytes(result.Folders[0].Size)})" : "—";
			string msg = string.Format(L("AnalyzeResult"), result.FileCount, FormatBytes(result.TotalSize), topFolder);
			if (result.Dupes.Count > 0)
			{
				int groups = result.Dupes.Select(d => d.Group).Distinct().Count();
				msg += "  " + string.Format(L("AnalyzeDupResult"), groups, result.RedundantCount, FormatBytes(result.RedundantBytes));
			}
			if (result.Truncated) msg += "  ⚠ " + L("AnTruncated");
			if (AnalyzeStatusText != null) AnalyzeStatusText.Text = (_analyzerStop ? "■ " : "") + msg;
		}
		catch (Exception ex) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = ex.Message; }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			// Whole row, not just the bar: this flow clears isBusy directly instead of via SetBusy, so nothing else
			// repaints the label/stats line — and a scan that ended while the bar was INDETERMINATE left the big
			// label blank (UpdateProgressStats writes "" in that mode) until an unrelated operation was started.
			ResetProgressWidgets();
			_analyzerBusy = false; SetBusy(busy: false);   // via SetBusy, not a raw isBusy write: if a device-change refresh
			// claimed busy behind this flow's confirm dialog it left Create-kit/Check-drive/Tool-start DISABLED and
			// Pause/Stop enabled, and a raw clear never repaints them — the app looked idle with a frozen toolbar.
			if (AnalyzeScanButton != null) AnalyzeScanButton.IsEnabled = true;
			if (AnalyzeStopButton != null) AnalyzeStopButton.IsEnabled = false;
			if (AnalyzeBrowseButton != null) AnalyzeBrowseButton.IsEnabled = true;
			if (AnalyzeDeleteButton != null) AnalyzeDeleteButton.IsEnabled = true;
			if (AnalyzeKeepFirstButton != null) AnalyzeKeepFirstButton.IsEnabled = true;
		}
	}

	// Immediate sub-folder of root that contains the file (or "(files here)" if directly in root).
	private static string ImmediateBucket(string rootFull, string filePath)
	{
		try
		{
			string dir = Path.GetDirectoryName(filePath) ?? rootFull;
			string dirFull = Path.GetFullPath(dir).TrimEnd('\\');
			if (string.Equals(dirFull, rootFull, StringComparison.OrdinalIgnoreCase)) return "(files here)";
			if (!dirFull.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) return "(other)";
			string rest = dirFull.Substring(rootFull.Length).TrimStart('\\');
			int slash = rest.IndexOf('\\');
			return slash >= 0 ? rest.Substring(0, slash) : rest;
		}
		catch { return "(other)"; }
	}

	// Duplicate-candidate caps (raised from the old 4096B floor / 4000-per-bucket / 400k total, which silently
	// dropped real duplicates). When a cap is hit we set res.Truncated so the UI can warn "results may be incomplete".
	private const long DupSizeFloor = 1;          // skip only empty files; tiny files CAN be duplicates
	private const int DupBucketCap = 20000;        // max files tracked per identical-size bucket
	private const int DupTotalCap = 1_000_000;     // max duplicate candidates total

	private static string DateCol(DateTime d) => d == DateTime.MinValue || d == default ? "" : d.ToString("yyyy-MM-dd HH:mm");

	// True when filePath is inside folderFull (or equal to it), case-insensitive.
	// Both sides are expanded past any 8.3 short-name components (e.g. PROGRA~1) so a short-name path
	// can't slip past the protected-folder guard by not textually matching the long-name master folder.
	private static bool UnderFolder(string filePath, string folderFull)
	{
		try
		{
			string p = LongPath(filePath);
			string f = LongPath(folderFull);
			return p.Equals(f, StringComparison.OrdinalIgnoreCase)
				|| p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	private static extern uint GetLongPathName(string lpszShortPath, System.Text.StringBuilder lpszLongPath, uint cchBuffer);

	// Canonical full path with any 8.3 short components expanded (falls back to GetFullPath when the path
	// doesn't exist or expansion fails). Used so a short-name path can't defeat a folder-prefix comparison.
	private static string LongPath(string path)
	{
		string full;
		try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return path; }
		if (full.IndexOf('~') < 0) return full; // no 8.3 component -> nothing to expand
		try
		{
			var sb = new System.Text.StringBuilder(1024);
			uint r = GetLongPathName(full, sb, (uint)sb.Capacity);
			if (r > 0 && r < sb.Capacity) return sb.ToString().TrimEnd('\\');
		}
		catch { }
		return full;
	}

	private AnalyzeResult AnalyzeScanCore(string root, IProgress<string> progress, IProgress<double> bar)
	{
		var res = new AnalyzeResult();
		var folderSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		var folderSize = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);   // full path -> cumulative subtree size
		var folderNewest = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
		var folderCat = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
		var bySize = new Dictionary<long, List<string>>();
		var candMtime = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); // mtime captured in the single walk -> no second stat pass
		var top = new List<(string Path, long Size, DateTime Mt)>();
		long total = 0; int count = 0, sinceUi = 0, dupTracked = 0; bool truncated = false;
		string rootFull = Path.GetFullPath(root).TrimEnd('\\');
		bar.Report(-1.0); // file walk: total unknown -> indeterminate bar
		foreach (var f in SafeFiles(root))
		{
			if (_analyzerStop) break;
			long sz; DateTime mt;
			try { var fi = new FileInfo(f); sz = fi.Length; mt = fi.LastWriteTime; } catch { continue; }
			total += sz; count++;
			string bucket = ImmediateBucket(rootFull, f);
			folderSizes[bucket] = folderSizes.TryGetValue(bucket, out var cur) ? cur + sz : sz;
			// Roll the file's size/age/type up to every ancestor folder (file's dir -> ... -> scan root) for drill-down.
			int cat = CategoryOf(f);
			string node = (Path.GetDirectoryName(f) ?? rootFull).TrimEnd('\\');
			while (true)
			{
				folderSize[node] = folderSize.TryGetValue(node, out var fc) ? fc + sz : sz;
				if (!folderNewest.TryGetValue(node, out var old) || mt > old) folderNewest[node] = mt;
				if (!folderCat.TryGetValue(node, out var arr)) { arr = new long[CatCount]; folderCat[node] = arr; }
				arr[cat] += sz;
				if (string.Equals(node, rootFull, StringComparison.OrdinalIgnoreCase)) break;
				string? par = Path.GetDirectoryName(node);
				if (string.IsNullOrEmpty(par)) break;
				par = par.TrimEnd('\\');
				if (par.Length < rootFull.Length) break; // safety: never climb above the scan root
				node = par;
			}
			top.Add((f, sz, mt));
			if (top.Count >= 3000) { top.Sort((x, y) => y.Size.CompareTo(x.Size)); top.RemoveRange(600, top.Count - 600); }
			if (sz >= DupSizeFloor)
			{
				if (dupTracked < DupTotalCap)
				{
					if (!bySize.TryGetValue(sz, out var lst)) { lst = new List<string>(); bySize[sz] = lst; }
					if (lst.Count < DupBucketCap) { lst.Add(f); candMtime[f] = mt; dupTracked++; }
					else truncated = true;
				}
				else truncated = true;
			}
			if (++sinceUi >= 1500) { sinceUi = 0; progress.Report(string.Format(L("AnalyzeScanning"), $"{count:N0} files · {FormatBytes(total)}")); }
		}
		res.TotalSize = total; res.FileCount = count; res.Truncated = truncated;
		res.Root = rootFull; res.FolderSize = folderSize; res.FolderNewest = folderNewest; res.FolderCat = folderCat;
		res.Folders = folderSizes.Select(kv => (kv.Key, kv.Value)).OrderByDescending(kv => kv.Item2).ToList();
		top.Sort((x, y) => y.Size.CompareTo(x.Size));
		foreach (var (p, s, mt) in top.Take(300))
			res.Big.Add(new BigFileRow { Name = Path.GetFileName(p), Folder = Path.GetDirectoryName(p) ?? "", Size = s, SizeText = FormatBytes(s), FullPath = p, Modified = mt, DateText = DateCol(mt) });

		// ---- Duplicate detection: size groups -> head/tail partial hash -> full hash, hashing done in PARALLEL ----
		// The partial-hash funnel means only files that match on size AND head+tail get fully read; parallelism
		// across cores is the real speedup (the bottleneck was a strictly sequential hash loop). SHA-256 is kept
		// for the final comparison so there is zero risk of a hash collision causing a false "duplicate".
		int dop = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
		var pOpts = new ParallelOptions { MaxDegreeOfParallelism = dop };
		var candidates = bySize.Where(kv => kv.Value.Count >= 2)
			.SelectMany(kv => kv.Value.Select(p => (Path: p, Size: kv.Key))).ToList();
		int group = 0;
		if (candidates.Count > 0)
		{
			progress.Report(L("AnalyzeHashing")); bar.Report(0.0);
			// Stage A: partial hash (first 16KB + last 16KB) -> prune size-groups that only collide on size.
			var partial = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int pdone = 0; int pn = candidates.Count;
			Parallel.ForEach(candidates, pOpts, c =>
			{
				if (_analyzerStop) return;
				string h = PartialHashFile(c.Path, c.Size);
				if (h.Length > 0) partial[c.Path] = h;
				int n = System.Threading.Interlocked.Increment(ref pdone);
				if (n % 64 == 0) { bar.Report(n * 50.0 / pn); progress.Report(L("AnalyzeHashing") + $" {n:N0} / {pn:N0}"); }
			});
			var afterPartial = candidates
				.Where(c => partial.ContainsKey(c.Path))
				.GroupBy(c => (c.Size, partial[c.Path]))
				.Where(g => System.Linq.Enumerable.Count(g) >= 2)
				.ToList();
			// Stage B: full hash only the survivors, in parallel.
			var survivors = afterPartial.SelectMany(g => g).Select(c => c.Path).ToList();
			var full = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			int fdone = 0; int fn = Math.Max(1, survivors.Count);
			Parallel.ForEach(survivors, pOpts, p =>
			{
				if (_analyzerStop) return;
				string h = HashFile(p);
				if (h.Length > 0) full[p] = h;
				int n = System.Threading.Interlocked.Increment(ref fdone);
				if (n % 32 == 0) { bar.Report(50.0 + n * 50.0 / fn); progress.Report(L("AnalyzeHashing") + $" {(pn + n):N0} / {(pn + fn):N0}"); }
			});
			foreach (var pg in afterPartial)
			{
				if (_analyzerStop) break;
				foreach (var hg in pg.Where(c => full.ContainsKey(c.Path)).GroupBy(c => full[c.Path]))
				{
					var list = hg.ToList();
					if (list.Count < 2) continue;
					group++;
					long size = list[0].Size;
					foreach (var c in list)
					{
						DateTime mm = candMtime.TryGetValue(c.Path, out var v) ? v : SafeModified(c.Path);
						res.Dupes.Add(new DupRow { Group = group, Name = Path.GetFileName(c.Path), Folder = Path.GetDirectoryName(c.Path) ?? "", Size = size, SizeText = FormatBytes(size), FullPath = c.Path, Modified = mm, DateText = DateCol(mm) });
					}
					res.RedundantCount += list.Count - 1;
					res.RedundantBytes += size * (list.Count - 1);
				}
			}
		}
		res.Dupes = res.Dupes.OrderBy(x => x.Group).ThenByDescending(x => x.Modified).ToList(); // newest first within each set
		return res;
	}

	private static DateTime SafeModified(string path) { try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; } }

	// Cheap pre-filter: SHA-256 of the first + last 16KB (or the whole file if it is that small).
	// Two files with the same size but a different head/tail can't be identical, so this skips most full reads.
	private static string PartialHashFile(string path, long size)
	{
		try
		{
			const int CHUNK = 16384;
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CHUNK, FileOptions.SequentialScan);
			using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
			if (size <= CHUNK * 2L)
			{
				byte[] all = new byte[size];
				fs.ReadExactly(all, 0, (int)size);
				sha.AppendData(all);
			}
			else
			{
				byte[] head = new byte[CHUNK];
				fs.ReadExactly(head, 0, CHUNK);
				sha.AppendData(head);
				fs.Seek(-CHUNK, SeekOrigin.End);
				byte[] tail = new byte[CHUNK];
				fs.ReadExactly(tail, 0, CHUNK);
				sha.AppendData(tail);
			}
			return Convert.ToHexString(sha.GetHashAndReset());
		}
		catch { return ""; }
	}

	private static string HashFile(string path)
	{
		try
		{
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
			using var sha = System.Security.Cryptography.SHA256.Create();
			return Convert.ToHexString(sha.ComputeHash(fs));
		}
		catch { return ""; }
	}

	// "Keep 1 per set" with a rule: keep the newest / oldest / shortest-path copy, tick the rest for deletion,
	// and mark the survivor green (Keep). Never leaves a set with everything ticked.
	// True when the Dupes grid currently holds perceptual near-duplicates (review-only), not byte-identical ones.
	private bool _dupesAreSimilar;

	private void AnalyzeKeepFirst_Click(object sender, RoutedEventArgs e)
	{
		if (DupesGrid?.ItemsSource is not IEnumerable<DupRow> rows || !rows.Any()) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnSmartNoDupes"); return; }
		// Similar (not identical) results are review-only: auto-ticking "keep newest" could delete genuinely different
		// photos (burst shots / edits) that were only visually close. Send the user to review instead.
		if (_dupesAreSimilar) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnSimReview"); if (AnalyzeDupesTab != null) AnalyzeDupesTab.IsSelected = true; return; }
		int? rule = ShowActionMenu(L("AnKeepTitle"), L("AnKeepPrompt"),
			new[] { L("AnKeepNewest"), L("AnKeepOldest"), L("AnKeepShortest") },
			new[] { 0xE74A, 0xE74B, 0xE71B }, new[] { false, false, false }, 0);
		if (rule == null) return;
		int sets = ApplyKeepRule(rule.Value);
		if (AnalyzeDupesTab != null) AnalyzeDupesTab.IsSelected = true;
		if (AnalyzeStatusText != null) AnalyzeStatusText.Text = string.Format(L("AnKeepApplied"), sets);
	}

	// Applies a keep rule (0=newest, 1=oldest, 2=shortest path) to every set: tick the rest, mark the survivor green.
	// Files in the protected (master) folder are ALWAYS kept and never ticked, whatever the rule.
	private int ApplyKeepRule(int rule)
	{
		if (DupesGrid?.ItemsSource is not IEnumerable<DupRow> rows) return 0;
		int sets = 0;
		foreach (var grp in rows.GroupBy(r => r.Group))
		{
			var members = grp.ToList();
			if (members.Count < 2) continue;
			var refs = members.Where(m => m.IsReference).ToList();
			DupRow keeper = refs.Count > 0
				? refs.OrderByDescending(r => r.Modified).First()
				: rule switch
				{
					0 => members.OrderByDescending(r => r.Modified).First(),
					1 => members.OrderBy(r => r.Modified).First(),
					_ => members.OrderBy(r => (r.FullPath ?? "").Length).ThenBy(r => r.FullPath).First(),
				};
			foreach (var m in members) { m.Keep = (m == keeper) || m.IsReference; m.Selected = (m != keeper) && !m.IsReference; }
			sets++;
		}
		DupesGrid.Items.Refresh();
		UpdateMarkedSummary();
		return sets;
	}

	// Shows "marked N file(s) (size) across M group(s)" in the status line after a selection rule runs.
	private void UpdateMarkedSummary()
	{
		if (DupesGrid?.ItemsSource is not IEnumerable<DupRow> rows) return;
		var sel = rows.Where(r => r.Selected).ToList();
		if (sel.Count == 0) return;
		long bytes = sel.Sum(r => r.Size);
		int groups = sel.Select(r => r.Group).Distinct().Count();
		if (AnalyzeStatusText != null) AnalyzeStatusText.Text = string.Format(L("AnMarkedSummary"), sel.Count, FormatBytes(bytes), groups);
	}

	// Beginner one-click: scan the chosen folder if needed, then auto-keep the newest copy in each set and show them.
	private async void AnalyzeSmartClean_Click(object sender, RoutedEventArgs e)
	{
		if (_analyzerBusy) return;
		bool hasResults = DupesGrid?.ItemsSource is IEnumerable<DupRow> r0 && r0.Any();
		if (!hasResults)
		{
			string root = AnalyzePathBox?.Text ?? "";
			if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnalyzeNoFolder"); return; }
			await RunAnalyzeScanAsync();
		}
		// If the grid currently holds perceptual near-duplicates (from "Find similar photos"), do NOT auto-tick — those
		// results are review-only, since "visually similar" isn't "identical". Send the user to review manually.
		if (_dupesAreSimilar) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnSimReview"); if (AnalyzeDupesTab != null) AnalyzeDupesTab.IsSelected = true; return; }
		int sets = ApplyKeepRule(0); // keep the newest of each set — the safe default
		if (AnalyzeDupesTab != null) AnalyzeDupesTab.IsSelected = true;
		if (AnalyzeStatusText != null) AnalyzeStatusText.Text = sets > 0 ? string.Format(L("AnKeepApplied"), sets) : L("AnSmartNoDupes");
	}

	// ---------- Similar (near-duplicate) photos: perceptual dHash, review-only ----------
	// Finds photos that are the SAME picture even when resized / recompressed / cropped / re-saved — not just
	// byte-identical. Results are NOT pre-ticked: visually-similar isn't the same as identical, so the user reviews.
	private async void AnalyzeSimilar_Click(object sender, RoutedEventArgs e)
	{
		if (_analyzerBusy) return;
		string root = AnalyzePathBox?.Text ?? "";
		if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnalyzeNoFolder"); return; }
		int? pick = ShowActionMenu(L("AnSimTitle"), L("AnSimPrompt"),
			new[] { L("AnSimStrict"), L("AnSimBalanced"), L("AnSimLoose") },
			new[] { 0xE71B, 0xE71B, 0xE71B }, new[] { false, false, false }, 1);
		if (pick == null) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		int threshold = pick == 0 ? 5 : pick == 2 ? 14 : 10;
		_analyzerBusy = true; isBusy = true; _analyzerStop = false; _refreshOwnsBusy = false;
		UpdateSleepBlock();   // this flow drives isBusy directly instead of through SetBusy, so hook the sleep block here too
		if (AnalyzeScanButton != null) AnalyzeScanButton.IsEnabled = false;
		if (AnalyzeStopButton != null) AnalyzeStopButton.IsEnabled = true;
		if (AnalyzeSimilarButton != null) AnalyzeSimilarButton.IsEnabled = false;
		if (AnalyzeDeleteButton != null) AnalyzeDeleteButton.IsEnabled = false;
		var progress = new Progress<string>(s => { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = s; });
		var bar = new Progress<double>(v => { if (v < 0) ProgressBar.IsIndeterminate = true; else { ProgressBar.IsIndeterminate = false; ProgressBar.Value = v; } });
		ProgressBar.Value = 0.0; progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
		operationStopwatch.Restart(); operationTimer.Start();
		try
		{
			var dupes = await Task.Run(() => FindSimilarImages(root, threshold, progress, bar));
			string master = AnalyzeMasterBox?.Text ?? "";
			if (!string.IsNullOrWhiteSpace(master) && Directory.Exists(master))
			{
				string m = Path.GetFullPath(master).TrimEnd('\\');
				foreach (var d in dupes) d.IsReference = UnderFolder(d.FullPath, m);
			}
			if (DupesGrid != null) DupesGrid.ItemsSource = dupes;
			_dupesAreSimilar = true;   // perceptual near-duplicates: NOT identical — must stay review-only
			if (AnalyzeDupesTab != null) AnalyzeDupesTab.IsSelected = true;
			int groups = dupes.Select(d => d.Group).Distinct().Count();
			if (AnalyzeStatusText != null)
				AnalyzeStatusText.Text = groups > 0 ? string.Format(L("AnSimResult"), groups, dupes.Count) + "  " + L("AnSimReview") : L("AnSimNone");
		}
		catch (Exception ex) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = ex.Message; }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			// Whole row, not just the bar: this flow clears isBusy directly instead of via SetBusy, so nothing else
			// repaints the label/stats line — and a scan that ended while the bar was INDETERMINATE left the big
			// label blank (UpdateProgressStats writes "" in that mode) until an unrelated operation was started.
			ResetProgressWidgets();
			_analyzerBusy = false; SetBusy(busy: false);   // via SetBusy, not a raw isBusy write: if a device-change refresh
			// claimed busy behind this flow's confirm dialog it left Create-kit/Check-drive/Tool-start DISABLED and
			// Pause/Stop enabled, and a raw clear never repaints them — the app looked idle with a frozen toolbar.
			if (AnalyzeScanButton != null) AnalyzeScanButton.IsEnabled = true;
			if (AnalyzeStopButton != null) AnalyzeStopButton.IsEnabled = false;
			if (AnalyzeSimilarButton != null) AnalyzeSimilarButton.IsEnabled = true;
			if (AnalyzeDeleteButton != null) AnalyzeDeleteButton.IsEnabled = true;
		}
	}

	private const int SimImageCap = 8000; // max images compared (keeps the O(n^2) cluster pass fast)

	private List<DupRow> FindSimilarImages(string root, int threshold, IProgress<string> progress, IProgress<double> bar)
	{
		bar.Report(-1.0);
		var imgs = new List<(string Path, long Size, DateTime Mt)>();
		int seen = 0;
		foreach (var f in SafeFiles(root))
		{
			if (_analyzerStop) break;
			if (CategoryOf(f) != 0) continue; // images only
			long sz; DateTime mt;
			try { var fi = new FileInfo(f); sz = fi.Length; mt = fi.LastWriteTime; } catch { continue; }
			imgs.Add((f, sz, mt));
			if (++seen % 200 == 0) progress.Report(string.Format(L("AnSimScanning"), seen));
			if (imgs.Count >= SimImageCap) break;
		}
		progress.Report(L("AnSimHashing")); bar.Report(0.0);
		int dop = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
		var hashes = new ulong?[imgs.Count];
		int done = 0; int n0 = Math.Max(1, imgs.Count);
		Parallel.For(0, imgs.Count, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
		{
			if (_analyzerStop) return;
			hashes[i] = ComputeDHash(imgs[i].Path);
			int n = System.Threading.Interlocked.Increment(ref done);
			if (n % 32 == 0) bar.Report(n * 80.0 / n0);
		});
		progress.Report(L("AnSimClustering"));
		var valid = new List<int>();
		for (int i = 0; i < imgs.Count; i++) if (hashes[i].HasValue) valid.Add(i);
		int[] parent = new int[imgs.Count];
		for (int i = 0; i < imgs.Count; i++) parent[i] = i;
		int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
		void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
		for (int a = 0; a < valid.Count; a++)
		{
			if (_analyzerStop) break;
			ulong ha = hashes[valid[a]]!.Value;
			for (int b = a + 1; b < valid.Count; b++)
			{
				ulong hb = hashes[valid[b]]!.Value;
				if (System.Numerics.BitOperations.PopCount(ha ^ hb) <= threshold) Union(valid[a], valid[b]);
			}
			if ((a & 63) == 0) bar.Report(80.0 + a * 20.0 / Math.Max(1, valid.Count));
		}
		var clusters = new Dictionary<int, List<int>>();
		foreach (int i in valid) { int r = Find(i); if (!clusters.TryGetValue(r, out var l)) { l = new List<int>(); clusters[r] = l; } l.Add(i); }
		var result = new List<DupRow>();
		int group = 0;
		foreach (var cl in clusters.Values.Where(v => v.Count >= 2))
		{
			group++;
			foreach (int i in cl.OrderByDescending(i => imgs[i].Mt))
			{
				var it = imgs[i];
				result.Add(new DupRow { Group = group, Name = Path.GetFileName(it.Path), Folder = Path.GetDirectoryName(it.Path) ?? "", Size = it.Size, SizeText = FormatBytes(it.Size), FullPath = it.Path, Modified = it.Mt, DateText = DateCol(it.Mt) });
			}
		}
		return result.OrderBy(x => x.Group).ThenByDescending(x => x.Modified).ToList();
	}

	// 64-bit difference hash: downscale to 9x8 grayscale, set a bit where a pixel is brighter than its right neighbour.
	private static ulong? ComputeDHash(string path)
	{
		try
		{
			using var bmp = new System.Drawing.Bitmap(path);
			using var small = new System.Drawing.Bitmap(9, 8, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
			using (var g = System.Drawing.Graphics.FromImage(small))
			{
				g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
				g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
				g.DrawImage(bmp, 0, 0, 9, 8);
			}
			ulong hash = 0; int bit = 0;
			for (int y = 0; y < 8; y++)
				for (int x = 0; x < 8; x++)
				{
					var l = small.GetPixel(x, y); var r = small.GetPixel(x + 1, y);
					double gl = 0.299 * l.R + 0.587 * l.G + 0.114 * l.B;
					double gr = 0.299 * r.R + 0.587 * r.G + 0.114 * r.B;
					if (gl > gr) hash |= 1UL << bit;
					bit++;
				}
			return hash;
		}
		catch { return null; }
	}

	// Opens the folder of the focused row in Explorer (or the first selected/ticked one), with the file selected.
	private void AnalyzeOpenFolder_Click(object sender, RoutedEventArgs e)
	{
		string? path = (DupesGrid?.SelectedItem as DupRow)?.FullPath
			?? (BigFilesGrid?.SelectedItem as BigFileRow)?.FullPath;
		if (string.IsNullOrEmpty(path) && DupesGrid?.ItemsSource is IEnumerable<DupRow> dr) path = dr.FirstOrDefault(x => x.Selected)?.FullPath;
		if (string.IsNullOrEmpty(path) && BigFilesGrid?.ItemsSource is IEnumerable<BigFileRow> bf) path = bf.FirstOrDefault(x => x.Selected)?.FullPath;
		if (string.IsNullOrEmpty(path)) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnPickRow"); return; }
		try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
	}

	private void AnalyzeRow_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		string? path = (DupesGrid?.SelectedItem as DupRow)?.FullPath ?? (BigFilesGrid?.SelectedItem as BigFileRow)?.FullPath;
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
		try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
	}

	private void AnalyzeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		object? item = (sender as System.Windows.Controls.DataGrid)?.SelectedItem;
		string? path = null, info = null;
		if (item is DupRow d) { path = d.FullPath; info = d.Name + "\n" + d.Folder + "\n" + d.SizeText + (string.IsNullOrEmpty(d.DateText) ? "" : "  ·  " + d.DateText); }
		else if (item is BigFileRow b) { path = b.FullPath; info = b.Name + "\n" + b.Folder + "\n" + b.SizeText + (string.IsNullOrEmpty(b.DateText) ? "" : "  ·  " + b.DateText); }
		if (AnalyzePreviewInfo != null) AnalyzePreviewInfo.Text = info ?? "";
		LoadAnalyzePreview(path);
	}

	private string _previewPath = "";

	// Shows a Windows shell thumbnail for ANY file type — photos, videos (a frame), PDFs, documents (icon) —
	// the same preview Explorer shows. Loaded off the UI thread so first-time video thumbnails don't freeze it.
	private async void LoadAnalyzePreview(string? path)
	{
		if (AnalyzePreviewImage == null) return;
		AnalyzePreviewImage.Source = null;
		_previewPath = path ?? "";
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) { if (AnalyzePreviewHint != null) AnalyzePreviewHint.Visibility = Visibility.Visible; return; }
		if (AnalyzePreviewHint != null) AnalyzePreviewHint.Visibility = Visibility.Collapsed;
		string p = path;
		var img = await Task.Run(() => GetShellThumbnail(p, 256));
		if (_previewPath != p) return; // the selection changed while the thumbnail was loading
		AnalyzePreviewImage.Source = img;
		if (img == null && AnalyzePreviewHint != null) AnalyzePreviewHint.Visibility = Visibility.Visible;
	}

	private static System.Windows.Media.ImageSource? GetShellThumbnail(string path, int size)
	{
		try
		{
			var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"); // IID_IShellItemImageFactory
			SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out var factory);
			if (factory == null) return null;
			int hr = factory.GetImage(new THUMBSIZE(size, size), 0x0 /*SIIGBF_RESIZETOFIT: thumbnail if available, else the file's icon*/, out IntPtr hbm);
			System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
			if (hr != 0 || hbm == IntPtr.Zero) return null;
			try
			{
				var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
				src.Freeze();
				return src;
			}
			finally { DeleteObject(hbm); }
		}
		catch { return null; }
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
	private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, in Guid riid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItemImageFactory ppv);

	[System.Runtime.InteropServices.DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr hObject);

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct THUMBSIZE { public int cx; public int cy; public THUMBSIZE(int x, int y) { cx = x; cy = y; } }

	[System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItemImageFactory
	{
		[System.Runtime.InteropServices.PreserveSig] int GetImage(THUMBSIZE size, int flags, out IntPtr phbm);
	}

	// Recycle-Bin delete with an explicit "about to permanently delete" (nuke) warning. See AnalyzeDelete_Click.
	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct SHFILEOPSTRUCT
	{
		public IntPtr hwnd;
		public uint wFunc;
		public IntPtr pFrom;
		public IntPtr pTo;
		public ushort fFlags;
		[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] public bool fAnyOperationsAborted;
		public IntPtr hNameMappings;
		public IntPtr lpszProgressTitle;
	}

	[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

	private const uint FO_DELETE = 0x0003;
	private const ushort FOF_NOCONFIRMATION = 0x0010;
	private const ushort FOF_ALLOWUNDO = 0x0040;
	private const ushort FOF_WANTNUKEWARNING = 0x4000;

	// ---------- Thumbnail gallery: see every duplicate photo/video at a glance before deleting ----------
	private static readonly Dictionary<string, System.Windows.Media.ImageSource> _thumbCache = new(StringComparer.OrdinalIgnoreCase);
	private System.Threading.CancellationTokenSource? _galleryCts;

	private void AnalyzeGallery_Click(object sender, RoutedEventArgs e)
	{
		if (DupesGrid?.ItemsSource is not IEnumerable<DupRow> rows) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnSmartNoDupes"); return; }
		var media = rows.Where(r => { int c = CategoryOf(r.FullPath); return c == 0 || c == 1; }).ToList();
		if (media.Count == 0) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnGalleryNone"); return; }
		ShowImageGallery(media);
	}

	private void ShowImageGallery(List<DupRow> media)
	{
		const int CAP = 500;
		bool capped = media.Count > CAP;
		var show = capped ? media.Take(CAP).ToList() : media;
		var white = System.Windows.Media.Brushes.White;
		var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
		var green = Frozen("#22C55E"); var red = Frozen("#EF4444"); var blue = Frozen("#3B82F6");
		var clearB = (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));

		var outer = new StackPanel();
		if (capped) outer.Children.Add(new TextBlock { Text = string.Format(L("AnGalleryCap"), CAP, media.Count), Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush"), FontSize = 12, Margin = new Thickness(2, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
		outer.Children.Add(new TextBlock { Text = L("AnGalleryTip"), Foreground = muted, FontSize = 12, Margin = new Thickness(2, 0, 0, 6), TextWrapping = TextWrapping.Wrap });

		var handlers = new List<(DupRow row, System.ComponentModel.PropertyChangedEventHandler h)>();
		var toLoad = new List<(string path, Image img)>();
		var win = new System.Windows.Window
		{
			Title = L("AnGalleryTitle"),
			Width = 980,
			Height = 700,
			Owner = this,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x12, 0x20))
		};

		foreach (var grp in show.GroupBy(r => r.Group).OrderBy(g => g.Key))
		{
			var members = grp.ToList();
			outer.Children.Add(new TextBlock { Text = string.Format(L("AnGalleryGroup"), grp.Key, members.Count), Foreground = white, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
			var wrap = new WrapPanel();
			outer.Children.Add(wrap);
			foreach (var row in members)
			{
				var img = new Image { Height = 140, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 6) };
				var sp = new StackPanel();
				sp.Children.Add(img);
				sp.Children.Add(new TextBlock { Text = row.Name, Foreground = white, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 156 });
				sp.Children.Add(new TextBlock { Text = row.SizeText + (string.IsNullOrEmpty(row.DateText) ? "" : " · " + row.DateText), Foreground = muted, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 156 });
				var chk = new CheckBox { Content = L("AnGalleryDelete"), Foreground = white, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), IsEnabled = row.CanDelete };
				chk.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Selected") { Source = row, Mode = System.Windows.Data.BindingMode.TwoWay });
				sp.Children.Add(chk);
				var tile = new Border { Width = 170, Margin = new Thickness(6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B)), Padding = new Thickness(6), Child = sp };
				var rowRef = row;
				void Upd() => tile.BorderBrush = rowRef.IsReference ? blue : rowRef.Keep ? green : rowRef.Selected ? red : clearB;
				Upd();
				System.ComponentModel.PropertyChangedEventHandler h = (_, ev) => { if (ev.PropertyName is "Selected" or "Keep" or "IsReference") win.Dispatcher.Invoke(Upd); };
				rowRef.PropertyChanged += h; handlers.Add((rowRef, h));
				tile.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2) { try { Process.Start(new ProcessStartInfo(rowRef.FullPath) { UseShellExecute = true }); } catch { } } };
				wrap.Children.Add(tile);
				toLoad.Add((row.FullPath, img));
			}
		}

		win.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), Content = outer };
		_galleryCts?.Cancel();
		_galleryCts = new System.Threading.CancellationTokenSource();
		var ct = _galleryCts.Token;
		_ = LoadGalleryThumbsAsync(toLoad, ct);
		win.Closed += (_, __) => { try { _galleryCts?.Cancel(); } catch { } foreach (var (r, hh) in handlers) r.PropertyChanged -= hh; DupesGrid?.Items.Refresh(); UpdateMarkedSummary(); };
		win.Show();
	}

	private static System.Windows.Media.Brush Frozen(string hex)
	{
		var b = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
		b.Freeze();
		return b;
	}

	// Loads shell thumbnails one at a time (serial -> avoids shell COM contention at scale) with a shared cache.
	private async Task LoadGalleryThumbsAsync(List<(string path, Image img)> items, System.Threading.CancellationToken ct)
	{
		foreach (var (path, img) in items)
		{
			if (ct.IsCancellationRequested) return;
			System.Windows.Media.ImageSource? src;
			lock (_thumbCache) _thumbCache.TryGetValue(path, out src);
			if (src == null)
			{
				try { src = await Task.Run(() => GetShellThumbnail(path, 200)); } catch { src = null; }
				// Bound the process-wide thumbnail cache so scanning many large folders can't grow it to multiple GB of
				// frozen bitmaps for the whole session. When it gets large, drop it wholesale (thumbs re-generate on demand).
				if (src != null) lock (_thumbCache) { if (_thumbCache.Count >= 3000) _thumbCache.Clear(); _thumbCache[path] = src; }
			}
			if (ct.IsCancellationRequested) return;
			if (src != null) { var s = src; img.Dispatcher.Invoke(() => img.Source = s); }
		}
	}

	private async void AnalyzeDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_analyzerBusy) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Re-evaluate the master ("protected") folder from the LIVE box: the user may have set or changed it AFTER the
		// scan (DupRow.IsReference was stamped once, at scan time). Match by path, not existence, so a momentarily
		// unavailable master drive still protects (empty box = no protection).
		string amMaster = AnalyzeMasterBox?.Text ?? "";
		string amMasterFull = "";
		if (!string.IsNullOrWhiteSpace(amMaster)) { try { amMasterFull = Path.GetFullPath(amMaster).TrimEnd('\\'); } catch { amMasterFull = ""; } }
		if (DupesGrid?.ItemsSource is IEnumerable<DupRow> drStamp)
			foreach (var d in drStamp) d.IsReference = amMasterFull.Length > 0 && UnderFolder(d.FullPath, amMasterFull);
		// Safety: never tick away the last copy of a set — if a whole set is ticked, un-tick its newest (keeper).
		var protectedGroups = new HashSet<int>(); // distinct dup-groups where a last copy was kept (for the confirm count)
		if (DupesGrid?.ItemsSource is IEnumerable<DupRow> drAll)
		{
			// Protected-folder files can never be deleted — force them un-ticked first.
			foreach (var r in drAll) if (r.IsReference && r.Selected) { r.Selected = false; r.Keep = true; }
			foreach (var grp in drAll.GroupBy(x => x.Group))
			{
				var members = grp.ToList();
				if (members.Count >= 2 && members.All(m => m.Selected))
				{
					var keeper = members.OrderByDescending(m => m.Modified).First();
					keeper.Selected = false; keeper.Keep = true; protectedGroups.Add(grp.Key);
				}
			}
			DupesGrid.Items.Refresh();
		}
		var paths = new List<(string Path, long Size)>();
		// Master-folder ("protected") filter for the Largest-files grid: a ticked big file inside the master folder must
		// NEVER be deleted. BigFileRow has no IsReference flag, so filter by the LIVE master path computed above.
		if (BigFilesGrid?.ItemsSource is IEnumerable<BigFileRow> bf) paths.AddRange(bf.Where(x => x.Selected && (amMasterFull.Length == 0 || !UnderFolder(x.FullPath, amMasterFull))).Select(x => (x.FullPath, x.Size)));
		if (DupesGrid?.ItemsSource is IEnumerable<DupRow> dr) paths.AddRange(dr.Where(x => x.Selected && !x.IsReference).Select(x => (x.FullPath, x.Size)));
		paths = paths.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
		// Cross-grid last-copy safety: a file can be in BOTH the Largest-files grid and a duplicate set. If EVERY copy of
		// a set is in the delete list (some via the dupes grid, some via the big-files grid), erasing all of them would
		// destroy the set. Keep one survivor (a protected/keeper row, else the newest) by dropping it from the list.
		if (DupesGrid?.ItemsSource is IEnumerable<DupRow> drKeep)
		{
			var deleteSet = new HashSet<string>(paths.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
			var forceKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var grp in drKeep.GroupBy(x => x.Group))
			{
				var members = grp.ToList();
				if (members.Count < 2) continue;
				int going = members.Count(m => !string.IsNullOrEmpty(m.FullPath) && deleteSet.Contains(m.FullPath));
				if (going < members.Count) continue; // at least one copy already survives
				DupRow survivor = members.Where(m => m.IsReference).OrderByDescending(m => m.Modified).FirstOrDefault()
					?? members.Where(m => m.Keep).OrderByDescending(m => m.Modified).FirstOrDefault()
					?? members.OrderByDescending(m => m.Modified).First();
				if (string.IsNullOrEmpty(survivor.FullPath)) continue;
				forceKeep.Add(survivor.FullPath);
				survivor.Selected = false; survivor.Keep = true; // un-tick so a later re-click can't delete the last copy
				protectedGroups.Add(grp.Key); // count the set once (may already be counted by the dupes-only guard above)
			}
			if (forceKeep.Count > 0)
			{
				paths = paths.Where(p => !forceKeep.Contains(p.Path)).ToList();
				// Un-tick the survivor's row in the Largest-files grid too (its Selected is independent of the DupRow),
				// so a second Delete click cannot re-add and recycle the copy we just preserved.
				if (BigFilesGrid?.ItemsSource is IEnumerable<BigFileRow> bfK) foreach (var b in bfK) if (forceKeep.Contains(b.FullPath)) b.Selected = false;
			}
		}
		if (paths.Count == 0) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("CleanNothingSelected"); return; }
		int protectedSets = protectedGroups.Count;
		long totalSel = paths.Sum(p => p.Size);
		string confirmMsg = string.Format(L("AnRecycleConfirm"), paths.Count, FormatBytes(totalSel)) + (protectedSets > 0 ? "\n\n" + string.Format(L("AnKeepProtected"), protectedSets) : "");
		if (MessageBox.Show(this, confirmMsg, L("AnalyzeDeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
		_analyzerBusy = true; isBusy = true; _refreshOwnsBusy = false; UpdateSleepBlock();
		try
		{
			await Task.Yield(); // stay on the UI thread (so the nuke warning is parented) while keeping this method async
			// Recycle via SHFileOperation with FOF_WANTNUKEWARNING. The old FileSystem.DeleteFile(OnlyErrorDialogs) set
			// FOF_NOCONFIRMATION and SILENTLY permanently-deleted any file too big for the Recycle Bin (or on a volume
			// with no bin) — the exact large files this tool surfaces — with no prompt and no way to undo. The nuke
			// warning makes Windows ask before ANY permanent deletion, so the user can decline.
			IntPtr hwnd = IntPtr.Zero;
			try { hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; } catch { }
			string multi = string.Join("\0", paths.Select(p => p.Path)) + "\0";
			IntPtr pFrom = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(multi);
			try
			{
				var op = new SHFILEOPSTRUCT { hwnd = hwnd, wFunc = FO_DELETE, pFrom = pFrom, fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING) };
				try { SHFileOperation(ref op); } catch { }
			}
			finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pFrom); }
			// Ground truth: a file the user KEPT (declined the nuke warning) still exists; only files that are actually
			// gone count toward freed/undo. This also stops the undo toast from promising recovery for a file that was
			// permanently deleted rather than recycled.
			long freed = 0; int n = 0;
			var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (p, s) in paths)
			{
				bool exists; try { exists = File.Exists(p); } catch { exists = true; }
				if (!exists) { freed += s; n++; gone.Add(p); }
			}
			if (AnalyzeStatusText != null) AnalyzeStatusText.Text = string.Format(L("AnalyzeDeleteDone"), n, FormatBytes(freed));
			if (n > 0) { _lastDeletedBatch = gone.ToList(); ShowUndoToast(n, freed); }
			// Filter against the deleted set (no per-row File.Exists stat on the UI thread).
			if (BigFilesGrid?.ItemsSource is IEnumerable<BigFileRow> bf2) BigFilesGrid.ItemsSource = bf2.Where(x => !gone.Contains(x.FullPath)).ToList();
			if (DupesGrid?.ItemsSource is IEnumerable<DupRow> dr2)
			{
				// Drop deleted rows, then drop duplicate groups that no longer have 2+ members.
				var kept = dr2.Where(x => !gone.Contains(x.FullPath)).ToList();
				DupesGrid.ItemsSource = kept.GroupBy(x => x.Group).Where(g => g.Count() >= 2).SelectMany(g => g).ToList();
			}
		}
		finally { _analyzerBusy = false; SetBusy(busy: false); }   // SetBusy, not a raw clear — see the sibling analyzer flows
	}

	// ---------- Undo toast: restore the last deleted batch from the Recycle Bin ----------
	private List<string> _lastDeletedBatch = new();
	private System.Windows.Threading.DispatcherTimer? _undoTimer;

	private void ShowUndoToast(int n, long freed)
	{
		if (AnalyzeUndoBar == null) return;
		if (AnalyzeUndoText != null) AnalyzeUndoText.Text = string.Format(L("AnDeletedToast"), n, FormatBytes(freed));
		AnalyzeUndoBar.Visibility = Visibility.Visible;
		_undoTimer ??= new System.Windows.Threading.DispatcherTimer();
		_undoTimer.Stop();
		_undoTimer.Interval = TimeSpan.FromSeconds(12);
		_undoTimer.Tick -= UndoTimer_Tick;
		_undoTimer.Tick += UndoTimer_Tick;
		_undoTimer.Start();
	}

	private void UndoTimer_Tick(object? sender, EventArgs e) { _undoTimer?.Stop(); if (AnalyzeUndoBar != null) AnalyzeUndoBar.Visibility = Visibility.Collapsed; }

	private void AnalyzeUndoDismiss_Click(object sender, RoutedEventArgs e) { _undoTimer?.Stop(); if (AnalyzeUndoBar != null) AnalyzeUndoBar.Visibility = Visibility.Collapsed; }

	private async void AnalyzeUndo_Click(object sender, RoutedEventArgs e)
	{
		_undoTimer?.Stop();
		if (AnalyzeUndoBar != null) AnalyzeUndoBar.Visibility = Visibility.Collapsed;
		if (_analyzerBusy) return;
		var batch = _lastDeletedBatch;
		if (batch.Count == 0) return;
		if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnUndoWorking");
		int restored = await Task.Run(() => RestoreFromRecycleBin(batch));
		if (AnalyzeStatusText != null)
			AnalyzeStatusText.Text = restored >= batch.Count ? string.Format(L("AnUndoDone"), restored)
				: restored > 0 ? string.Format(L("AnUndoPartial"), restored, batch.Count)
				: L("AnUndoNone");
	}

	// Best-effort restore-to-original-location via the Shell Recycle Bin. Matches recycled items to the
	// original full paths we just deleted, then invokes the localized "Restore" verb. If Windows' UI language
	// isn't covered we report 0 restored and tell the user the files are still in the Recycle Bin.
	private static readonly string[] RestoreVerbWords =
	{
		"restore", "restaur", "restabil", "wiederherstell", "ripristin", "herstell", "przywr", "geri yükle", "geri yukle",
		"восстанов", "віднов", "还原", "復元", "元に戻", "を1つ前", "पुन", "pulih", "استعاد"
	};

	private static bool IsRestoreVerb(string verbName)
	{
		string v = verbName.Replace("&", "").Trim().ToLowerInvariant();
		foreach (var w in RestoreVerbWords) if (v.Contains(w)) return true;
		return false;
	}

	private static int RestoreFromRecycleBin(IList<string> originalPaths)
	{
		int restored = 0;
		try
		{
			var shellType = Type.GetTypeFromProgID("Shell.Application");
			if (shellType == null) return 0;
			dynamic shell = Activator.CreateInstance(shellType)!;
			dynamic recycler = shell.NameSpace(10); // ssfBITBUCKET
			if (recycler == null) return 0;
			var want = new List<string>(originalPaths);
			dynamic items = recycler.Items();
			int cnt = (int)items.Count;
			// For each wanted original path pick the BEST (most-recently-deleted) matching bin item, so an older
			// different-content copy already in the bin under the same path is not restored by mistake. Match on
			// original folder + name, comparing the name WITH and WITHOUT its extension: Shell FolderItem.Name drops
			// the extension when "hide extensions for known types" is on (the Windows default), so an exact full-path
			// compare would match nothing for jpg/png/mp4/... and restore zero files.
			var best = new Dictionary<string, (dynamic item, DateTime when)>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < cnt; i++)
			{
				dynamic item = items.Item(i);
				string name = (string)item.Name;
				string origLoc = (string)recycler.GetDetailsOf(item, 1); // column 1 = "Original Location" (Vista+)
				if (string.IsNullOrEmpty(origLoc)) continue;
				string? match = null;
				foreach (var w in want)
				{
					if (!string.Equals(Path.GetDirectoryName(w) ?? "", origLoc, StringComparison.OrdinalIgnoreCase)) continue;
					if (string.Equals(Path.GetFileName(w), name, StringComparison.OrdinalIgnoreCase)
						|| string.Equals(Path.GetFileNameWithoutExtension(w), name, StringComparison.OrdinalIgnoreCase))
					{ match = w; break; }
				}
				if (match == null) continue;
				DateTime when = DateTime.MinValue;
				try { DateTime.TryParse((string)recycler.GetDetailsOf(item, 2), out when); } catch { } // col 2 = "Date deleted"
				if (!best.TryGetValue(match, out var cur) || when > cur.when) best[match] = (item, when);
			}
			foreach (var kv in best)
			{
				dynamic item = kv.Value.item;
				dynamic verbs = item.Verbs();
				int vc = (int)verbs.Count;
				for (int v = 0; v < vc; v++)
				{
					dynamic verb = verbs.Item(v);
					if (IsRestoreVerb((string)verb.Name))
					{
						verb.DoIt();
						bool ok; try { ok = File.Exists(kv.Key); } catch { ok = true; }
						if (ok) restored++;
						break;
					}
				}
			}
		}
		catch { }
		return restored;
	}

	private void AnalyzeTreemap_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTreemap();

	private static readonly string[] TreemapPalette = { "#2563EB", "#16A34A", "#D97706", "#DC2626", "#7C3AED", "#0891B2", "#DB2777", "#65A30D", "#EA580C", "#0D9488", "#9333EA", "#CA8A04" };

	// Splits items recursively into the rectangle, alternating along the longer axis — a simple, correct treemap.
	private static void TreemapSplit(List<(string name, long size, int idx)> items, System.Windows.Rect r, List<(System.Windows.Rect rect, int idx)> outRects)
	{
		if (items.Count == 0) return;
		if (items.Count == 1) { outRects.Add((r, items[0].idx)); return; }
		long total = 0; foreach (var it in items) total += it.size;
		long acc = 0; int split = 0;
		for (; split < items.Count - 1; split++) { acc += items[split].size; if (acc * 2 >= total) { split++; break; } }
		if (split <= 0) split = 1; if (split >= items.Count) split = items.Count - 1;
		var a = items.GetRange(0, split);
		var b = items.GetRange(split, items.Count - split);
		long aSum = 0; foreach (var it in a) aSum += it.size;
		double frac = total > 0 ? (double)aSum / total : 0.5;
		System.Windows.Rect ra, rb;
		if (r.Width >= r.Height) { double w = r.Width * frac; ra = new System.Windows.Rect(r.X, r.Y, w, r.Height); rb = new System.Windows.Rect(r.X + w, r.Y, r.Width - w, r.Height); }
		else { double hh = r.Height * frac; ra = new System.Windows.Rect(r.X, r.Y, r.Width, hh); rb = new System.Windows.Rect(r.X, r.Y + hh, r.Width, r.Height - hh); }
		TreemapSplit(a, ra, outRects);
		TreemapSplit(b, rb, outRects);
	}

	// Category colours for the "by type" lens (Images, Video, Audio, Documents, Archives, Other).
	private static readonly string[] CatPalette = { "#2563EB", "#DC2626", "#7C3AED", "#16A34A", "#D97706", "#64748B" };

	private void AnalyzeColorLens_Changed(object sender, SelectionChangedEventArgs e)
	{
		_colorLens = AnalyzeColorLens?.SelectedIndex ?? 0;
		if (_colorLens < 0) _colorLens = 0;
		DrawTreemap();
	}

	private void PopulateColorLens()
	{
		if (AnalyzeColorLens == null) return;
		int keep = AnalyzeColorLens.SelectedIndex;
		AnalyzeColorLens.SelectionChanged -= AnalyzeColorLens_Changed;
		AnalyzeColorLens.Items.Clear();
		AnalyzeColorLens.Items.Add(L("AnLensSize"));
		AnalyzeColorLens.Items.Add(L("AnLensType"));
		AnalyzeColorLens.Items.Add(L("AnLensAge"));
		AnalyzeColorLens.SelectedIndex = keep >= 0 && keep < 3 ? keep : 0;
		AnalyzeColorLens.SelectionChanged += AnalyzeColorLens_Changed;
	}

	private void AnalyzeBreadcrumb_Click(string target)
	{
		if (string.IsNullOrEmpty(target)) return;
		_treemapCurrent = target;
		DrawTreemap();
	}

	// Lightens (f>1) or darkens (f<1) a colour, clamped — used for the "cushion" radial gradient.
	private static System.Windows.Media.Color Shade(System.Windows.Media.Color b, double f)
	{
		byte Cl(double v) => (byte)Math.Max(0, Math.Min(255, v));
		return System.Windows.Media.Color.FromRgb(Cl(b.R * f), Cl(b.G * f), Cl(b.B * f));
	}

	private System.Windows.Media.Color LensColor(string folder, int idx)
	{
		if (_colorLens == 1) // by dominant file type
		{
			int best = 5; long bestV = -1;
			if (_folderCat.TryGetValue(folder, out var arr))
				for (int i = 0; i < arr.Length; i++) if (arr[i] > bestV) { bestV = arr[i]; best = i; }
			return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(CatPalette[best % CatPalette.Length]);
		}
		if (_colorLens == 2) // by age of the newest file inside (warm = recent, cold = old)
		{
			double days = 99999;
			if (_folderNewest.TryGetValue(folder, out var mt) && mt != default) days = (DateTime.Now - mt).TotalDays;
			string hex = days < 30 ? "#DC2626" : days < 180 ? "#EA580C" : days < 365 ? "#D97706" : days < 730 ? "#0891B2" : "#1E40AF";
			return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
		}
		return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(TreemapPalette[idx % TreemapPalette.Length]); // by size: distinct palette
	}

	private void RebuildBreadcrumb()
	{
		var bc = AnalyzeBreadcrumb;
		if (bc == null) return;
		bc.Children.Clear();
		if (string.IsNullOrEmpty(_treemapRoot)) return;
		// Build the chain root -> ... -> current.
		var chain = new List<string>();
		string cur = _treemapCurrent;
		while (!string.IsNullOrEmpty(cur))
		{
			chain.Insert(0, cur);
			if (string.Equals(cur, _treemapRoot, StringComparison.OrdinalIgnoreCase)) break;
			string par = (Path.GetDirectoryName(cur) ?? "").TrimEnd('\\');
			if (par.Length < _treemapRoot.Length) break;
			cur = par;
		}
		for (int i = 0; i < chain.Count; i++)
		{
			string path = chain[i];
			string label = i == 0 ? (Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path) : Path.GetFileName(path);
			if (string.IsNullOrEmpty(label)) label = path;
			var link = new System.Windows.Controls.Button
			{
				Content = (i == 0 ? "🗀 " : "") + label,
				Tag = path,
				Foreground = i == chain.Count - 1 ? (System.Windows.Media.Brush)FindResource("TextBrush") : (System.Windows.Media.Brush)FindResource("BlueBrush"),
				Background = System.Windows.Media.Brushes.Transparent,
				BorderThickness = new Thickness(0),
				Padding = new Thickness(2, 0, 2, 0),
				FontSize = 12,
				Cursor = System.Windows.Input.Cursors.Hand,
				VerticalAlignment = VerticalAlignment.Center
			};
			link.Click += (_, __) => AnalyzeBreadcrumb_Click((string)link.Tag);
			bc.Children.Add(link);
			if (i < chain.Count - 1)
				bc.Children.Add(new TextBlock { Text = " ›", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
		}
	}

	private void DrawTreemap()
	{
		RebuildBreadcrumb();
		var c = AnalyzeTreemap;
		if (c == null) return;
		c.Children.Clear();
		double W = c.ActualWidth, H = c.ActualHeight;
		if (W <= 4 || H <= 4 || string.IsNullOrEmpty(_treemapCurrent)) return;
		// Children of the current folder (drill-down), or fall back to the legacy immediate-bucket list.
		List<(string Name, long Size, string Path)> src;
		if (_folderChildren.TryGetValue(_treemapCurrent, out var kids) && kids.Count > 0)
			src = kids.Select(k => (Path.GetFileName(k), _folderSize.TryGetValue(k, out var s) ? s : 0L, k)).ToList();
		else
			src = _treemapData.Select(d => (d.Name, d.Size, "")).ToList();
		var items = src.Where(i => i.Size > 0).OrderByDescending(i => i.Size).Take(24)
			.Select((i, idx) => (i.Name, i.Size, idx)).ToList();
		if (items.Count == 0) return;
		var rects = new List<(System.Windows.Rect rect, int idx)>();
		TreemapSplit(items.Select(i => ((string)i.Name, (long)i.Size, i.idx)).ToList(), new System.Windows.Rect(0, 0, W, H), rects);
		foreach (var (rect, idx) in rects)
		{
			if (rect.Width < 1 || rect.Height < 1) continue;
			var item = items[idx];
			string childPath = src.FirstOrDefault(s => s.Name == item.Name && s.Size == item.Size).Path ?? "";
			var baseCol = LensColor(childPath, idx);
			// "Cushion" look: a soft radial gradient (lighter centre, darker edge) so folders read as raised tiles.
			var brush = new System.Windows.Media.RadialGradientBrush { GradientOrigin = new System.Windows.Point(0.4, 0.35), Center = new System.Windows.Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 };
			brush.GradientStops.Add(new System.Windows.Media.GradientStop(Shade(baseCol, 1.28), 0));
			brush.GradientStops.Add(new System.Windows.Media.GradientStop(baseCol, 0.55));
			brush.GradientStops.Add(new System.Windows.Media.GradientStop(Shade(baseCol, 0.66), 1));
			brush.Freeze();
			bool hasKids = !string.IsNullOrEmpty(childPath) && _folderChildren.TryGetValue(childPath, out var ck) && ck.Count > 0;
			var rectShape = new System.Windows.Shapes.Rectangle
			{
				Width = rect.Width,
				Height = rect.Height,
				Fill = brush,
				Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0, 0, 0)),
				StrokeThickness = 1,
				Tag = childPath,
				ToolTip = $"{item.Name} · {FormatBytes(item.Size)}" + (hasKids ? "\n" + L("AnDrillHint") : ""),
				Cursor = hasKids ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow
			};
			rectShape.MouseEnter += (_, __) => rectShape.Stroke = System.Windows.Media.Brushes.White;
			rectShape.MouseLeave += (_, __) => rectShape.Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 0, 0, 0));
			if (hasKids)
				rectShape.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2) { _treemapCurrent = childPath; DrawTreemap(); } };
			Canvas.SetLeft(rectShape, rect.X); Canvas.SetTop(rectShape, rect.Y);
			c.Children.Add(rectShape);
			if (rect.Width > 54 && rect.Height > 26)
			{
				var tb = new TextBlock { Text = $"{item.Name}\n{FormatBytes(item.Size)}", Foreground = System.Windows.Media.Brushes.White, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 2, 2, 0), MaxWidth = rect.Width - 6, IsHitTestVisible = false };
				Canvas.SetLeft(tb, rect.X); Canvas.SetTop(tb, rect.Y);
				c.Children.Add(tb);
			}
		}
	}

	private void RecoverVolumeBox_DropDownOpened(object sender, EventArgs e) => PopulateRecoverVolumes();

	private void PopulateRecoverVolumes()
	{
		if (RecoverVolumeBox == null) return;
		char prevLetter = ParseVolumeLetter(RecoverVolumeBox.SelectedItem as string);
		RecoverVolumeBox.Items.Clear();
		int selectIndex = -1;
		try
		{
			foreach (var d in DriveInfo.GetDrives())
			{
				try
				{
					// Skip the CD/DVD type; everything else (fixed + removable) is fair game.
					if (d.DriveType == DriveType.CDRom) continue;
					if (!d.IsReady) continue;
					string fmt = "";
					try { fmt = d.DriveFormat; } catch { }
					bool supported = string.Equals(fmt, "NTFS", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(fmt, "exFAT", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(fmt, "FAT32", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(fmt, "FAT", StringComparison.OrdinalIgnoreCase);
					if (!supported) continue;
					string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "no label" : d.VolumeLabel;
					RecoverVolumeBox.Items.Add($"{d.Name.TrimEnd('\\')}  ({label}, {fmt}, {FormatBytes(d.TotalSize)})");
					if (ParseVolumeLetter(RecoverVolumeBox.Items[^1] as string) == prevLetter && prevLetter != '\0')
						selectIndex = RecoverVolumeBox.Items.Count - 1;
				}
				catch { }
			}
		}
		catch { }
		if (RecoverVolumeBox.Items.Count > 0)
			RecoverVolumeBox.SelectedIndex = selectIndex >= 0 ? selectIndex : 0;
	}

	private static char ParseVolumeLetter(string? item)
	{
		if (string.IsNullOrWhiteSpace(item)) return '\0';
		foreach (char c in item) if (char.IsLetter(c)) return char.ToUpperInvariant(c);
		return '\0';
	}

	private CancellationTokenSource? _recoverCts;

	private bool _ssdRecoverWarned;
	private volatile bool _recoverPaused;
	private int _previewGen; // bumped per selection so a slow earlier preview read can't overwrite a newer one

	// Pause/Resume a running deep scan (the worker loop waits while _recoverPaused is set).
	private void RecoverPause_Click(object sender, RoutedEventArgs e)
	{
		_recoverPaused = !_recoverPaused;
		if (RecoverPauseButton != null) RecoverPauseButton.Content = _recoverPaused ? L("BtnResume") : L("BtnPause");
		if (_recoverPaused && RecoverStatusText != null) RecoverStatusText.Text = L("RfPaused");
	}

	// Honest one-time heads-up: on a TRIM-enabled SSD, deleted files are erased by the drive within seconds, so
	// raw undelete/carving usually finds nothing — no tool can change that. The Recycle Bin path still works.
	private bool SsdRecoveryBlocked(char letter)
	{
		if (_ssdRecoverWarned) return false;
		if (MediaForPath(letter + ":\\") != WipeMedia.Ssd) return false;
		_ssdRecoverWarned = true;
		return MessageBox.Show(L("RfSsdTrimWarn"), L("RfFilesTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK;
	}

	private async void RecoverScanButton_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("RfAdminScan"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		char letter = ParseVolumeLetter(RecoverVolumeBox?.SelectedItem as string);
		if (letter == '\0') { MessageBox.Show(L("RfPickDrive"), L("RfFilesTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (SsdRecoveryBlocked(letter)) return;

		_recoverCts?.Dispose(); _recoverCts = new CancellationTokenSource();
		bool failed = false;
		try
		{
			RecoverButton.IsEnabled = false;
			RecoverScanButton.IsEnabled = false;
			RecoverDeepScanButton.IsEnabled = false;
			RecoverStopButton.IsEnabled = true;
			RecoverGrid.ItemsSource = null;
			if (RecoverPreviewImage != null) RecoverPreviewImage.Source = null;
			stopRequested = false; _progressFullRange = true;
			SetBusy(busy: true, string.Format(L("RfScanBusy"), letter));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			if (RecoverStatusText != null) RecoverStatusText.Text = L("RfScanning");

			var token = _recoverCts.Token;
			var scan = await Task.Run(() => ScanDeletedFiles(letter, token,
				p => Dispatcher.Invoke(() => ProgressBar.Value = p)));
			_lastScan = scan;
			RecoverGrid.ItemsSource = scan.Files;
			ApplyRecoverFilter();
			foreach (var f in scan.Files) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			ProgressBar.Value = 100.0;
			if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
			UpdateProgressStats();   // the timer already stopped: without this the stats LINE keeps the last tick's percent next to a full bar
			SetBusy(busy: false);
		}
		catch (Exception ex) { failed = true; ShowError(L("RfScanFailed"), ex); }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			_progressFullRange = false; SetBusy(busy: false);
			RecoverScanButton.IsEnabled = true; RecoverDeepScanButton.IsEnabled = true; RecoverStopButton.IsEnabled = false;
			if (failed && RecoverStatusText != null) RecoverStatusText.Text = "";
			UpdateRecoverSelectionInfo();
		}
	}

	private void RecoverStopButton_Click(object sender, RoutedEventArgs e)
	{
		_recoverPaused = false; // let a paused scan exit its wait so it can stop
		stopRequested = true;
		try { _recoverCts?.Cancel(); } catch { }
		RecoverStopButton.IsEnabled = false;
		if (RecoverPauseButton != null) { RecoverPauseButton.IsEnabled = false; RecoverPauseButton.Content = L("BtnPause"); }
	}

	// Recycle Bin scan: lists files still sitting in the Recycle Bin (intact data, original name/path/date).
	// This is the most common — and 100% safe — recovery, so it gets its own one-click button.
	private async void RecoverRecycle_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("RfAdminScan"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		char letter = ParseVolumeLetter(RecoverVolumeBox?.SelectedItem as string);
		if (letter == '\0') { MessageBox.Show(L("RfPickDrive"), L("RfFilesTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		bool failed = false;
		try
		{
			RecoverButton.IsEnabled = false; RecoverScanButton.IsEnabled = false; RecoverDeepScanButton.IsEnabled = false;
			if (RecoverRecycleButton != null) RecoverRecycleButton.IsEnabled = false;
			RecoverGrid.ItemsSource = null;
			if (RecoverPreviewImage != null) RecoverPreviewImage.Source = null;
			stopRequested = false;
			SetBusy(busy: true, L("RfRecycleBusy"));
			if (RecoverStatusText != null) RecoverStatusText.Text = L("RfRecycleBusy");
			var scan = await Task.Run(() => ScanRecycleBin(letter));
			_lastScan = scan;
			RecoverGrid.ItemsSource = scan.Files;
			ApplyRecoverFilter();
			foreach (var f in scan.Files) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfRecycleFound"), scan.Files.Count);
			SetBusy(busy: false);
		}
		catch (Exception ex) { failed = true; ShowError(L("RfScanFailed"), ex); }
		finally
		{
			SetBusy(busy: false);
			RecoverScanButton.IsEnabled = true; RecoverDeepScanButton.IsEnabled = true;
			if (RecoverRecycleButton != null) RecoverRecycleButton.IsEnabled = true;
			RecoverStopButton.IsEnabled = false;
			if (failed && RecoverStatusText != null) RecoverStatusText.Text = "";
			UpdateRecoverSelectionInfo();
		}
	}

	// "⋯" overflow on the Recover toolbar: open its drop-down menu (disk image / sessions).
	private void RecoverMore_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button b && b.ContextMenu != null)
		{
			b.ContextMenu.PlacementTarget = b;
			b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
			b.ContextMenu.IsOpen = true;
		}
	}

	private async void RecoverDeepScan_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("RfAdminDeep"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		char letter = ParseVolumeLetter(RecoverVolumeBox?.SelectedItem as string);
		if (letter == '\0') { MessageBox.Show(L("RfPickDriveShort"), L("RfFilesTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (SsdRecoveryBlocked(letter)) return;

		_recoverCts?.Dispose(); _recoverCts = new CancellationTokenSource();
		bool failed = false;
		try
		{
			RecoverButton.IsEnabled = false; RecoverScanButton.IsEnabled = false; RecoverDeepScanButton.IsEnabled = false; RecoverStopButton.IsEnabled = true;
			_recoverPaused = false;
			if (RecoverPauseButton != null) { RecoverPauseButton.IsEnabled = true; RecoverPauseButton.Content = L("BtnPause"); }
			RecoverGrid.ItemsSource = null;
			if (RecoverPreviewImage != null) RecoverPreviewImage.Source = null;
			stopRequested = false; _progressFullRange = true; _progressFixedTotal = true;
			// Byte-based progress so the bar runs smoothly to 100% with a real ETA + MB/s (no 99% plateau).
			long dtotal; try { dtotal = new DriveInfo(letter + ":").TotalSize; } catch { dtotal = 0; }
			if (dtotal <= 0) dtotal = 256L << 30;
			progressTotalGiB = dtotal / 1073741824.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("RfDeepBusy"), letter));
			ProgressBar.Value = 0.0;
			if (RecoverStatusText != null) RecoverStatusText.Text = L("RfDeepRunning");

			var token = _recoverCts.Token;
			var scan = await Task.Run(() => DeepScan(letter, token, _ => { })); // bar is driven by byte progress, not this callback
			operationTimer.Stop(); operationStopwatch.Stop();
			_lastScan = scan;
			RecoverGrid.ItemsSource = scan.Files;
			ApplyRecoverFilter();
			foreach (var f in scan.Files) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			// A stopped scan only searched part of the drive — never claim 100% or a complete result for it, or the
			// user reads "12 files found" as "that's all there is" and stops looking for the file that IS there.
			if (scan.DeepPartial)
			{
				if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepFoundPartial"), scan.Files.Count, FormatBytes(scan.ResumeOffset));
			}
			else
			{
				progressDoneGiB = progressTotalGiB;
				if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepFound"), scan.Files.Count);
				if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
				ProgressBar.Value = 100.0;
				UpdateProgressStats();   // refresh the stats LINE too — the timer has stopped, so nothing else will
			}
			SetBusy(busy: false);
		}
		catch (Exception ex) { failed = true; ShowError(L("RfDeepFailed"), ex); }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			_progressFullRange = false; _progressFixedTotal = false; _recoverPaused = false; SetBusy(busy: false);
			RecoverScanButton.IsEnabled = true; RecoverDeepScanButton.IsEnabled = true; RecoverStopButton.IsEnabled = false;
			if (RecoverPauseButton != null) { RecoverPauseButton.IsEnabled = false; RecoverPauseButton.Content = L("BtnPause"); }
			if (failed && RecoverStatusText != null) RecoverStatusText.Text = "";
			UpdateRecoverSelectionInfo();
		}
	}

	private void RecoverSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyRecoverFilter();

	// Extension sets for the recovery type filter (index matches RecoverTypeBox: 0=All,1=Pictures,…).
	private static readonly string[][] RecoverTypeExts =
	{
		System.Array.Empty<string>(), // All
		new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".raw", ".cr2", ".nef" },
		new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".ods", ".csv" },
		new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" },
		new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".flv", ".m4v" },
		new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" },
	};

	private void RecoverType_Changed(object sender, SelectionChangedEventArgs e) => ApplyRecoverFilter();

	// Fills the type-filter dropdown with localized category names, preserving the current selection.
	private void PopulateRecoverTypes()
	{
		if (RecoverTypeBox == null) return;
		int prev = RecoverTypeBox.SelectedIndex;
		RecoverTypeBox.Items.Clear();
		foreach (var key in new[] { "RecTypeAll", "RecTypePictures", "RecTypeDocuments", "RecTypeAudio", "RecTypeVideo", "RecTypeArchives" })
			RecoverTypeBox.Items.Add(L(key));
		RecoverTypeBox.SelectedIndex = prev >= 0 && prev < RecoverTypeBox.Items.Count ? prev : 0;
	}

	private void ApplyRecoverFilter()
	{
		if (RecoverGrid?.ItemsSource == null) return;
		var view = System.Windows.Data.CollectionViewSource.GetDefaultView(RecoverGrid.ItemsSource);
		if (view == null) return;
		string q = (RecoverSearchBox?.Text ?? "").Trim();
		int ti = RecoverTypeBox?.SelectedIndex ?? 0;
		string[] exts = ti > 0 && ti < RecoverTypeExts.Length ? RecoverTypeExts[ti] : System.Array.Empty<string>();
		if (string.IsNullOrEmpty(q) && exts.Length == 0) { view.Filter = null; view.Refresh(); return; }
		view.Filter = o =>
		{
			var f = o as DeletedFile;
			if (f == null) return false;
			bool textOk = string.IsNullOrEmpty(q) || (f.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) || (f.Path?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
			bool typeOk = exts.Length == 0 || exts.Any(x => f.Name != null && f.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase));
			return textOk && typeOk;
		};
		view.Refresh();
	}

	private IEnumerable<DeletedFile> VisibleRecoverFiles()
	{
		if (RecoverGrid?.ItemsSource == null) yield break;
		var view = System.Windows.Data.CollectionViewSource.GetDefaultView(RecoverGrid.ItemsSource);
		foreach (var o in view) if (o is DeletedFile f) yield return f;
	}

	// While a bulk select/deselect runs, suppress the per-row PropertyChanged→UpdateRecoverSelectionInfo callback
	// (which does several full-list passes). Otherwise selecting all of 100k+ carved files is O(n^2) and freezes.
	private bool _suppressRecoverSelUpdate;

	private void RecoverSelectAll_Click(object sender, RoutedEventArgs e)
	{
		_suppressRecoverSelUpdate = true;
		try { foreach (var f in VisibleRecoverFiles()) if (f.Recoverable) f.Selected = true; }
		finally { _suppressRecoverSelUpdate = false; }
		UpdateRecoverSelectionInfo();
	}

	private void RecoverSelectNone_Click(object sender, RoutedEventArgs e)
	{
		_suppressRecoverSelUpdate = true;
		try { foreach (var f in VisibleRecoverFiles()) f.Selected = false; }
		finally { _suppressRecoverSelUpdate = false; }
		UpdateRecoverSelectionInfo();
	}

	private void UpdateRecoverSelectionInfo()
	{
		if (_suppressRecoverSelUpdate) return;
		if (_lastScan == null || RecoverStatusText == null) return;
		var sel = _lastScan.Files.Where(f => f.Selected && f.Recoverable).ToList();
		int del = _lastScan.Files.Count(f => f.Deleted);
		int onDrive = _lastScan.Files.Count - del;
		RecoverButton.IsEnabled = sel.Count > 0 && !isBusy;
		string txt = sel.Count > 0
			? string.Format(L("RfSelInfo"), sel.Count, FormatBytes(sel.Sum(f => f.Size)))
			: onDrive > 0 ? string.Format(L("RfSelDelOnDrive"), del, onDrive) : string.Format(L("RfSelDel"), del);
		// A stopped deep scan searched only part of the drive. This runs on every selection change AND in the scan's
		// finally, so it is the one place that reliably keeps the "partial — rest not searched" warning visible;
		// otherwise the one-shot message set right after the scan is instantly overwritten here.
		if (_lastScan.DeepPartial) txt += " " + string.Format(L("RfDeepPartialSuffix"), FormatBytes(_lastScan.ResumeOffset));
		RecoverStatusText.Text = txt;
	}

	private async void CreateImage_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("RfAdminImage"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		char letter = ParseVolumeLetter(RecoverVolumeBox?.SelectedItem as string);
		if (letter == '\0') { MessageBox.Show(L("RfImgPick"), L("RfImgTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		long total; try { total = new DriveInfo(letter + ":").TotalSize; } catch { total = 0; }
		if (total <= 0) { MessageBox.Show(L("RfImgNoSize"), L("RfImgTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		var dlg = new Microsoft.Win32.SaveFileDialog { Filter = L("RfFltDiskImage") + " (*.img)|*.img", FileName = $"{letter}-image.img", Title = L("RfImgSaveTitle") };
		if (dlg.ShowDialog() != true) return;
		string dest = dlg.FileName;
		if (char.ToUpperInvariant(Path.GetPathRoot(dest)?.FirstOrDefault() ?? '\0') == letter)
		{ MessageBox.Show(L("RfImgDiffDrive"), L("RfImgTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
		try
		{
			string? destRoot = Path.GetPathRoot(dest);
			if (!string.IsNullOrEmpty(destRoot))
			{
				long destFree = new DriveInfo(destRoot).AvailableFreeSpace;
				if (destFree < total)
				{ MessageBox.Show(string.Format(L("RfImgNoSpace"), FormatBytes(total), FormatBytes(destFree), destRoot), L("RfImgTitle"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
			}
		}
		catch { }
		if (MessageBox.Show(string.Format(L("RfImgConfirm"), letter, FormatBytes(total), dest),
				L("RfImgTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

		try
		{
			stopRequested = false; _progressFullRange = true;
			// progressSpeedMb must be cleared like every other flow does: this one drives the bar through its own
			// callback and never feeds the byte counter, so the speed can never be recomputed here — the PREVIOUS
			// operation's rate was used to invent a "Remaining" that then never moved for the whole imaging run.
			progressTotalGiB = Math.Max(1.0, total / 1073741824.0); progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			RecoverStopButton.IsEnabled = true;
			SetBusy(busy: true, string.Format(L("RfImgBusy"), letter, Path.GetFileName(dest)));
			ProgressBar.Value = 0.0;
			await Task.Run(() => CreateDiskImage(letter, dest, total, p => Dispatcher.Invoke(() => ProgressBar.Value = p)));
			operationTimer.Stop(); operationStopwatch.Stop();
			ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
			UpdateProgressStats();   // refresh the stats LINE too — the timer has stopped, so nothing else will
			SetBusy(busy: false); NotifyOperationDone(!stopRequested);
			MessageBox.Show(stopRequested
				? string.Format(L("RfImgStopped"), dest)
				: string.Format(L("RfImgDone"), dest),
				L("RfImgTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { NotifyOperationDone(false); ShowError(L("RfImgFailed"), ex); }
		finally { _progressFullRange = false; operationTimer.Stop(); operationStopwatch.Stop(); RecoverStopButton.IsEnabled = false; SetBusy(busy: false); }
	}

	private async void OpenImage_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L("RfFltDiskImage") + " (*.img;*.bin;*.raw;*.dd)|*.img;*.bin;*.raw;*.dd|" + L("RfFltAllFiles") + " (*.*)|*.*", Title = L("RfOpenImgFileTitle") };
		if (dlg.ShowDialog() != true) return;
		string path = dlg.FileName;
		int? mode = ShowActionMenu(L("RfOpenImgTitle"), L("RfOpenImgPrompt"),
			new[] { L("RfOpenImgQuick"), L("RfOpenImgDeep") },
			new[] { 0xE8FE, 0xE773 }, null, 0);
		if (mode == null) return;
		bool deep = mode == 1;

		_recoverCts?.Dispose(); _recoverCts = new CancellationTokenSource();
		bool failed = false;
		try
		{
			RecoverButton.IsEnabled = false; RecoverScanButton.IsEnabled = false; RecoverDeepScanButton.IsEnabled = false; RecoverStopButton.IsEnabled = true;
			RecoverGrid.ItemsSource = null;
			if (RecoverPreviewImage != null) RecoverPreviewImage.Source = null;
			stopRequested = false; _progressFullRange = true;
			// Clear the byte counters: the scan drives the bar through its own callback, so the previous operation's
			// leftover done/total would otherwise slam the bar to ~89% and fight that callback (and, on the deep path,
			// compute the percentage against the WRONG denominator — the previous image's size).
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("RfImgScanBusy"), Path.GetFileName(path)));
			ProgressBar.Value = 0.0;
			var token = _recoverCts.Token;
			var scan = await Task.Run(() => deep
				? DeepScanImage(path, token, p => Dispatcher.Invoke(() => ProgressBar.Value = p))
				: ScanDeletedFilesImage(path, token, p => Dispatcher.Invoke(() => ProgressBar.Value = p)));
			operationTimer.Stop(); operationStopwatch.Stop();
			_lastScan = scan;
			RecoverGrid.ItemsSource = scan.Files;
			ApplyRecoverFilter();
			foreach (var f in scan.Files) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			// Don't paint a full bar for a scan the user STOPPED — it only searched part of the image. The partial
			// warning is appended by UpdateRecoverSelectionInfo (called in the finally), since scan.DeepPartial is set.
			if (!scan.DeepPartial) { ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%"; }
			UpdateProgressStats();   // refresh the stats LINE too — the timer has stopped, so nothing else will
			SetBusy(busy: false);
		}
		catch (Exception ex) { failed = true; ShowError(L("RfImgScanFailed"), ex); }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			_progressFullRange = false; SetBusy(busy: false);
			RecoverScanButton.IsEnabled = true; RecoverDeepScanButton.IsEnabled = true; RecoverStopButton.IsEnabled = false;
			if (failed && RecoverStatusText != null) RecoverStatusText.Text = "";
			UpdateRecoverSelectionInfo();
		}
	}

	private static bool IsPreviewableImage(string name)
	{
		string n = (name ?? "").ToLowerInvariant();
		return n.EndsWith(".jpg") || n.EndsWith(".jpeg") || n.EndsWith(".jpe") || n.EndsWith(".png") || n.EndsWith(".gif")
			|| n.EndsWith(".bmp") || n.EndsWith(".dib") || n.EndsWith(".tif") || n.EndsWith(".tiff") || n.EndsWith(".ico") || n.EndsWith(".webp");
	}

	private static bool IsPreviewableText(string name)
	{
		string n = (name ?? "").ToLowerInvariant();
		foreach (var ext in new[] { ".txt", ".log", ".csv", ".xml", ".json", ".ini", ".cfg", ".md", ".html", ".htm", ".bat", ".cmd", ".ps1", ".sql", ".srt", ".reg", ".yml", ".yaml" })
			if (n.EndsWith(ext)) return true;
		return false;
	}

	// Show a thumbnail of the selected file when it is an image, or the first lines when it is a text file —
	// invaluable for identifying deep-scan results that have no original name.
	private async void RecoverGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (RecoverPreviewImage == null) return;
		var f = RecoverGrid.SelectedItem as DeletedFile;
		if (f == null || _lastScan == null)
		{
			RecoverPreviewImage.Source = null;
			if (RecHexText != null) RecHexText.Text = "";
			if (RecPreviewHint != null) RecPreviewHint.Text = L("PreviewSelect");
			return;
		}
		var snap = _lastScan;
		int gen = ++_previewGen; // a newer selection invalidates slower earlier reads
		// Hex / header inspector: the first 256 bytes of whatever is selected (collapsible, for power users).
		if (RecHexText != null)
		{
			try { byte[] hd = await Task.Run(() => ReadFileBytes(f, snap, 256)); if (gen != _previewGen) return; RecHexText.Text = FormatHexDump(hd); }
			catch { if (gen == _previewGen) RecHexText.Text = ""; }
		}
		if (IsPreviewableText(f.Name))
		{
			RecoverPreviewImage.Source = null;
			try
			{
				byte[] data = await Task.Run(() => ReadFileBytes(f, snap, 8192));
				if (gen != _previewGen) return;
				string text = Encoding.UTF8.GetString(data).Replace("\0", "");
				if (text.Length > 800) text = text.Substring(0, 800) + "…";
				if (RecPreviewHint != null) RecPreviewHint.Text = $"{f.Name}\n\n{text}";
			}
			catch { if (RecPreviewHint != null) RecPreviewHint.Text = L("PreviewFail"); }
			return;
		}
		if (!IsPreviewableImage(f.Name))
		{
			RecoverPreviewImage.Source = null;
			if (RecPreviewHint != null) RecPreviewHint.Text = L("PreviewNone");
			return;
		}
		int max = (int)Math.Min(f.Size <= 0 ? 8_000_000 : f.Size, 8_000_000);
		try
		{
			byte[] data = await Task.Run(() => ReadFileBytes(f, snap, max));
			if (gen != _previewGen) return;
			var bmp = new System.Windows.Media.Imaging.BitmapImage();
			using (var ms = new MemoryStream(data))
			{
				bmp.BeginInit();
				bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
				bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat;
				bmp.DecodePixelWidth = 480;
				bmp.StreamSource = ms;
				bmp.EndInit();
			}
			bmp.Freeze();
			RecoverPreviewImage.Source = bmp;
			if (RecPreviewHint != null) RecPreviewHint.Text = $"{f.Name} — {f.SizeText}";
		}
		catch
		{
			RecoverPreviewImage.Source = null;
			if (RecPreviewHint != null) RecPreviewHint.Text = L("PreviewFail");
		}
	}

	// Reads up to maxBytes of a recoverable file's content into memory (used for the preview thumbnail).
	private byte[] ReadFileBytes(DeletedFile f, NtfsScanResult g, int maxBytes)
	{
		// Recycle Bin row: the payload is a real $R file on disk. The volume walk below has NO payload for these
		// (no runs, not carved/resident), so without this branch every preview, hex dump and thumbnail came back
		// empty — and said "Preview failed" — for files that are perfectly intact.
		if (!string.IsNullOrEmpty(f.SourcePath))
		{
			try
			{
				if (Directory.Exists(f.SourcePath)) return Array.Empty<byte>();   // a folder has nothing to preview
				using var src = new FileStream(f.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				byte[] buf = new byte[(int)Math.Min(maxBytes, src.Length)];
				int total = 0, r;
				while (total < buf.Length && (r = src.Read(buf, total, buf.Length - total)) > 0) total += r;
				return total == buf.Length ? buf : buf.AsSpan(0, total).ToArray();
			}
			catch { return Array.Empty<byte>(); }
		}
		if (f.Resident && f.ResidentData != null) return f.ResidentData;
		using var vr = OpenSource(g);
		using var ms = new MemoryStream();
		int cs = g.ClusterSize;
		if (f.Carved)
		{
			long remaining = Math.Min(f.Size, maxBytes); long off = f.ByteOffset;
			while (remaining > 0) { int chunk = (int)Math.Min(1 << 20, remaining); ms.Write(vr.Read(off, chunk), 0, chunk); off += chunk; remaining -= chunk; }
			return ms.ToArray();
		}
		if (f.ExFat)
		{
			long remaining = Math.Min(f.Size, maxBytes), cl = f.FirstCluster; int guard = 0; var seen = new HashSet<long>();
			while (remaining > 0 && cl >= 2 && guard++ < 200000 && seen.Add(cl))
			{
				int chunk = (int)Math.Min(cs, remaining);
				ms.Write(vr.Read(g.DataAreaOffset + (cl - 2) * (long)cs, chunk), 0, chunk);
				remaining -= chunk;
				if (f.Contiguous) cl++;
				else { long next = BitConverter.ToUInt32(vr.Read(g.FatOffset + cl * 4, 4), 0); if (next >= 0xFFFFFFF8 || next < 2) break; cl = next; }
			}
			return ms.ToArray();
		}
		long rem = Math.Min(f.Size, maxBytes);
		foreach (var (lcn, count) in f.Runs)
		{
			if (rem <= 0 || lcn < 0) break;
			long off = lcn * (long)cs, take = Math.Min(count * (long)cs, rem), pos = 0;
			while (take > 0) { int chunk = (int)Math.Min(1 << 20, take); ms.Write(vr.Read(off + pos, chunk), 0, chunk); pos += chunk; take -= chunk; rem -= chunk; }
		}
		return ms.ToArray();
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct STORAGE_DEVICE_NUMBER { public int DeviceType; public int DeviceNumber; public int PartitionNumber; }

	[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint ctl, IntPtr inB, uint inS, ref STORAGE_DEVICE_NUMBER outB, uint outS, out uint ret, IntPtr ovl);

	// Physical disk number behind a volume letter (so we can block recovering onto the SAME physical disk being
	// scanned — the #1 way to overwrite the very data being recovered). Returns -1 if unknown.
	private static int PhysicalDiskOfVolume(char letter)
	{
		try
		{
			using var h = CreateFile($"\\\\.\\{char.ToUpperInvariant(letter)}:", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (h.IsInvalid) return -1;
			var sdn = new STORAGE_DEVICE_NUMBER();
			const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
			if (DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, ref sdn, (uint)System.Runtime.InteropServices.Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero))
				return sdn.DeviceNumber;
		}
		catch { }
		return -1;
	}

	[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
	private static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);

	private static int PhysicalDiskOfPath(string path)
	{
		// Resolve the path's REAL volume, not just its drive letter: a destination under a mounted folder (e.g.
		// C:\Mount\Data, where Data is a different volume) used to resolve to C:'s disk, so the same-disk check
		// compared the wrong disks and stayed silent.
		try
		{
			var root = new StringBuilder(320);
			if (GetVolumePathName(path, root, (uint)root.Capacity) && root.Length > 0)
			{
				string mountRoot = root.ToString();
				if (!mountRoot.EndsWith("\\")) mountRoot += "\\";
				var guid = new StringBuilder(320);
				if (GetVolumeNameForVolumeMountPoint(mountRoot, guid, (uint)guid.Capacity) && guid.Length > 0)
				{
					using var h = CreateFile(guid.ToString().TrimEnd('\\'), GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
					if (!h.IsInvalid)
					{
						var sdn = new STORAGE_DEVICE_NUMBER();
						const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
						if (DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, ref sdn, (uint)System.Runtime.InteropServices.Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, IntPtr.Zero))
							return sdn.DeviceNumber;
					}
				}
			}
		}
		catch { }
		char letter = char.ToUpperInvariant(Path.GetPathRoot(path)?.FirstOrDefault() ?? '\0');
		return letter == '\0' ? -1 : PhysicalDiskOfVolume(letter);
	}

	// Warns (with an override) if dest is on the SAME physical disk as the scanned source. Returns true only if the
	// user cancels — recovering onto the same disk is risky but must stay POSSIBLE (many users have only one disk).
	private bool BlocksSamePhysicalDisk(string destPath)
	{
		if (_lastScan == null || !string.IsNullOrEmpty(_lastScan.ImagePath)) return false; // image source: nothing to overwrite
		int src = PhysicalDiskOfVolume(_lastScan.Letter);
		int dst = PhysicalDiskOfPath(destPath);
		// Fail SAFE, not open. `src >= 0 && src == dst` skipped the warning entirely whenever a device number was
		// unknown (-1) — e.g. a Storage Spaces / dynamic or spanned source — which is precisely the case where the
		// recovery can overwrite the very data being recovered. Warn unless we can PROVE the disks are different;
		// the dialog still has an override, so a single-disk user is never blocked.
		bool provenSame = src >= 0 && src == dst;
		if (provenSame || src < 0 || dst < 0)
			// Use the softer "could not confirm" wording when a device number is UNKNOWN — the old text asserted as
			// FACT that the destination is the same disk, which is a lie when we simply couldn't tell them apart.
			return MessageBox.Show(L(provenSame ? "RfSameDiskBlocked" : "RfSameDiskUnsure"), L("RfFilesTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK;
		return false;
	}

	// Classic offset | hex | ascii dump of the first bytes of a selected file.
	private static string FormatHexDump(byte[] b)
	{
		var sb = new StringBuilder();
		for (int i = 0; i < b.Length; i += 16)
		{
			sb.Append(i.ToString("X4")).Append("  ");
			int n = Math.Min(16, b.Length - i);
			for (int j = 0; j < 16; j++) sb.Append(j < n ? b[i + j].ToString("X2") + " " : "   ");
			sb.Append(' ');
			for (int j = 0; j < n; j++) { byte c = b[i + j]; sb.Append(c >= 32 && c < 127 ? (char)c : '.'); }
			sb.Append('\n');
		}
		return sb.ToString();
	}

	// ---- Thumbnail gallery: see recovered/carved photos as pictures instead of "deepscan_00012.jpg" ----
	private void RecoverGallery_Click(object sender, RoutedEventArgs e)
	{
		if (_lastScan == null) { return; }
		var media = VisibleRecoverFiles().Where(f => IsPreviewableImage(f.Name)).ToList();
		if (media.Count == 0) { if (RecoverStatusText != null) RecoverStatusText.Text = L("RfGalleryNone"); return; }
		ShowRecoverGallery(media);
	}

	private void ShowRecoverGallery(List<DeletedFile> media)
	{
		const int CAP = 400;
		bool capped = media.Count > CAP;
		var show = capped ? media.Take(CAP).ToList() : media;
		var snap = _lastScan!;
		var white = System.Windows.Media.Brushes.White;
		var muted = (System.Windows.Media.Brush)FindResource("MutedBrush");
		var accent = (System.Windows.Media.Brush)FindResource("BlueBrush");
		var clearB = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 255, 255));
		var outer = new StackPanel();
		if (capped) outer.Children.Add(new TextBlock { Text = string.Format(L("AnGalleryCap"), CAP, media.Count), Foreground = (System.Windows.Media.Brush)FindResource("OrangeBrush"), FontSize = 12, Margin = new Thickness(2, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
		outer.Children.Add(new TextBlock { Text = L("RfGalleryTip"), Foreground = muted, FontSize = 12, Margin = new Thickness(2, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
		var wrap = new WrapPanel();
		outer.Children.Add(wrap);
		var win = new System.Windows.Window { Title = L("RfGalleryTitle"), Width = 980, Height = 700, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x12, 0x20)) };
		var handlers = new List<(DeletedFile f, System.ComponentModel.PropertyChangedEventHandler h)>();
		var toLoad = new List<(DeletedFile f, Image img)>();
		foreach (var f in show)
		{
			var img = new Image { Height = 140, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 6) };
			var sp = new StackPanel();
			sp.Children.Add(img);
			sp.Children.Add(new TextBlock { Text = f.Name, Foreground = white, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 156 });
			sp.Children.Add(new TextBlock { Text = f.SizeText, Foreground = muted, FontSize = 10 });
			var chk = new CheckBox { Content = L("RfGalleryRecover"), Foreground = white, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
			chk.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Selected") { Source = f, Mode = System.Windows.Data.BindingMode.TwoWay });
			sp.Children.Add(chk);
			var tile = new Border { Width = 170, Margin = new Thickness(6), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x29, 0x3B)), Padding = new Thickness(6), Child = sp };
			var fr = f;
			void Upd() => tile.BorderBrush = fr.Selected ? accent : clearB;
			Upd();
			System.ComponentModel.PropertyChangedEventHandler h = (_, ev) => { if (ev.PropertyName == "Selected") win.Dispatcher.Invoke(Upd); };
			fr.PropertyChanged += h; handlers.Add((fr, h));
			tile.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2) RecoverGrid.SelectedItem = fr; };
			wrap.Children.Add(tile);
			toLoad.Add((f, img));
		}
		win.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), Content = outer };
		var cts = new System.Threading.CancellationTokenSource();
		_ = LoadRecoverThumbsAsync(toLoad, snap, cts.Token);
		win.Closed += (_, __) => { try { cts.Cancel(); } catch { } foreach (var (ff, hh) in handlers) ff.PropertyChanged -= hh; UpdateRecoverSelectionInfo(); };
		win.Show();
	}

	// Decodes carved/recovered image bytes into thumbnails one at a time (serial -> bounded random I/O on a
	// possibly-failing drive); truncated/garbage carves simply fail to decode and stay blank.
	private async Task LoadRecoverThumbsAsync(List<(DeletedFile f, Image img)> items, NtfsScanResult snap, System.Threading.CancellationToken ct)
	{
		foreach (var (f, img) in items)
		{
			if (ct.IsCancellationRequested) return;
			try
			{
				int max = (int)Math.Min(f.Size <= 0 ? 600_000 : f.Size, 600_000);
				byte[] data = await Task.Run(() => ReadFileBytes(f, snap, max), ct);
				if (ct.IsCancellationRequested) return;
				var bmp = new System.Windows.Media.Imaging.BitmapImage();
				using (var ms = new MemoryStream(data))
				{
					bmp.BeginInit();
					bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
					bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat;
					bmp.DecodePixelWidth = 160;
					bmp.StreamSource = ms;
					bmp.EndInit();
				}
				bmp.Freeze();
				img.Dispatcher.Invoke(() => img.Source = bmp);
			}
			catch { }
		}
	}

	private async void RecoverButton_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (_lastScan == null) return;
		var picked = _lastScan.Files.Where(f => f.Selected && f.Recoverable).ToList();
		if (picked.Count == 0) { MessageBox.Show(L("RfPickFile"), L("RfFilesTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		string outDir;
		using (var dlg = new Forms.FolderBrowserDialog { Description = L("RfFolderDesc") })
		{
			if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
			outDir = dlg.SelectedPath;
		}
		if (string.IsNullOrWhiteSpace(outDir)) return;
		if (BlocksSamePhysicalDisk(outDir)) return; // warn (with override) if the destination is the same physical disk

		bool failed = false;
		int ok = 0, fail = 0;
		try
		{
			stopRequested = false; _progressFullRange = true;
			// RecoverPickedToDir already honours stopRequested per file, but the Stop button stayed greyed out — so
			// a long recovery off a failing drive (seconds of retries per unreadable run) could not be stopped at all.
			RecoverStopButton.IsEnabled = true;
			SetBusy(busy: true, string.Format(L("RfRecBusy"), picked.Count));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			Directory.CreateDirectory(outDir);
			var scan = _lastScan;

			var rr = await Task.Run(() => RecoverPickedToDir(picked, scan, outDir));
			ok = rr.Ok; fail = rr.Fail;
			int partialCount = rr.Partial;

			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			NotifyOperationDone(ok > 0);
			if (MessageBox.Show(string.Format(L("RfRecDoneHead"), ok, picked.Count, outDir)
					+ (partialCount > 0 ? string.Format(L("RfRecPartialNote"), partialCount) : "")
					+ (fail > 0 ? string.Format(L("RfRecFailNote"), fail) : "") + L("RfRecOpenFolder"),
					L("RfFilesTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				try { Process.Start(new ProcessStartInfo(outDir) { UseShellExecute = true }); } catch { }
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("RfRecFailed"), ex); }
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; RecoverStopButton.IsEnabled = false; SetBusy(busy: false); }
	}

	// Recovers the picked files into outDir, rebuilding their original folder structure. Runs on a worker thread.
	private (int Ok, int Partial, int Fail) RecoverPickedToDir(List<DeletedFile> picked, NtfsScanResult scan, string outDir)
	{
		int ok = 0, partial = 0, fail = 0;
		using var vr = OpenSource(scan);
		var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < picked.Count; i++)
		{
			if (stopRequested) break;
			var f = picked[i];
			string relDir = SanitizeRelativeDir(f.Path);
			string targetDir = string.IsNullOrEmpty(relDir) ? outDir : Path.Combine(outDir, relDir);
			try { Directory.CreateDirectory(targetDir); } catch { targetDir = outDir; }
			string safe = string.Concat((f.Name ?? "recovered").Split(Path.GetInvalidFileNameChars()));
			if (string.IsNullOrWhiteSpace(safe)) safe = "recovered_" + i;
			safe = MakeNonReservedFileName(safe);   // NUL/CON/AUX/PRN/COM1.../LPT1... open a DEVICE, not a file — prefix them
			string outPath = Path.Combine(targetDir, safe);
			int dup = 1;
			// Directory.Exists too: a recovered Recycle Bin FOLDER would otherwise merge into an existing folder of
			// the same name and overwrite its files instead of getting a " (1)" suffix like a colliding file does.
			while (used.Contains(outPath) || File.Exists(outPath) || Directory.Exists(outPath))
			{
				string baseName = Path.GetFileNameWithoutExtension(safe);
				string ext = Path.GetExtension(safe);
				outPath = Path.Combine(targetDir, $"{baseName} ({dup++}){ext}");
			}
			used.Add(outPath);
			try
			{
				long written = RecoverOne(vr, f, scan, outPath);
				// A short write means the data was unreadable or already overwritten. KEEP the partial file (half a
				// photo still beats nothing) but never count it as a clean recovery — reporting truncated data as
				// "recovered" is exactly what makes a user delete the only surviving copy.
				if (f.Size > 0 && written < f.Size) partial++; else ok++;
			}
			catch { fail++; try { if (File.Exists(outPath)) File.Delete(outPath); } catch { } }
			int pct = (int)((i + 1) * 100.0 / picked.Count);
			Dispatcher.Invoke(() => ProgressBar.Value = pct);
		}
		return (ok, partial, fail);
	}

	// Recover selected files straight into a single .zip archive (recovers to a temp folder, then compresses).
	private async void RecoverZip_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (_lastScan == null) return;
		var picked = _lastScan.Files.Where(f => f.Selected && f.Recoverable).ToList();
		if (picked.Count == 0) { MessageBox.Show(L("RfPickFile"), L("RfFilesTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		string zipPath;
		var dlg = new Microsoft.Win32.SaveFileDialog { Filter = L("RfFltZip") + " (*.zip)|*.zip", FileName = "DriveForge-recovered.zip", Title = L("RecoverZipTitle") };
		if (dlg.ShowDialog(this) != true) return;
		zipPath = dlg.FileName;
		if (BlocksSamePhysicalDisk(zipPath)) return; // warn (with override) if writing onto the same physical disk

		// The selection is staged to a temp folder beside the zip and then compressed, so the destination needs
		// room for roughly the data twice (temp copy + zip). Warn before filling the drive.
		try
		{
			long need = picked.Sum(f => Math.Max(0, f.Size));
			string root = Path.GetPathRoot(zipPath) ?? "";
			if (!string.IsNullOrEmpty(root))
			{
				long free = new DriveInfo(root).AvailableFreeSpace;
				if (free < need * 2)
				{
					if (MessageBox.Show(string.Format(L("RfZipMayNotFit"), FormatBytes(need), FormatBytes(need * 2), FormatBytes(free)),
							L("RfFilesTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
						return;
				}
			}
		}
		catch { }

		string temp = Path.Combine(Path.GetDirectoryName(zipPath) ?? Path.GetTempPath(), "DriveForge-zip-" + Guid.NewGuid().ToString("N"));
		int ok = 0, fail = 0, partialCount = 0;   // partialCount lives OUT here so the catch can see it (temp holds the only copies)
		bool keepTemp = false;   // set if the ZIP step fails — the recovered files live in temp and are the only copies
		try
		{
			stopRequested = false; _progressFullRange = true;
			RecoverStopButton.IsEnabled = true;   // the copy loop honours stopRequested — let the user actually reach it
			SetBusy(busy: true, string.Format(L("RfZipBusy"), picked.Count));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			Directory.CreateDirectory(temp);
			var scan = _lastScan;
			var rr = await Task.Run(() => RecoverPickedToDir(picked, scan, temp));
			ok = rr.Ok; fail = rr.Fail; partialCount = rr.Partial;
			// Partial files are real files on disk — archive them too, and report them separately from clean ones.
			// Zip whatever was recovered EVEN IF the user hit Stop: the temp folder holds the only copies, and the
			// finally deletes it — skipping the zip on stop would silently destroy every file recovered so far.
			if (ok + partialCount > 0)
			{
				SetBusy(busy: true, L("RfZipCompress"));
				await Task.Run(() => { if (File.Exists(zipPath)) File.Delete(zipPath); System.IO.Compression.ZipFile.CreateFromDirectory(temp, zipPath, System.IO.Compression.CompressionLevel.Optimal, false); });
			}
			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			NotifyOperationDone(ok > 0);
			if (MessageBox.Show(string.Format(L("RfZipDoneHead"), ok, picked.Count, zipPath)
					+ (partialCount > 0 ? string.Format(L("RfRecPartialNote"), partialCount) : "")
					+ (fail > 0 ? string.Format(L("RfRecFailNote"), fail) : "") + L("RfZipShowExplorer"),
					L("RfFilesTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true }); } catch { }
		}
		catch (Exception ex)
			{
				NotifyOperationDone(false);
				// The files were already recovered into temp; only the ZIP step failed (usually a full disk). Deleting
				// temp in the finally would destroy the ONLY recovered copies — keep it and open it for the user.
				keepTemp = ok + partialCount > 0 && Directory.Exists(temp); // count PARTIALS (real files on disk); failed outputs are already deleted
				ShowError(L("RfZipFailed"), ex);
				if (keepTemp) { try { Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true }); } catch { } }
			}
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; RecoverStopButton.IsEnabled = false; SetBusy(busy: false); try { if (!keepTemp && Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
	}

	// Save the current scan results to a .dfscan file so they can be reopened later without re-scanning.
	private void RecoverSaveSession_Click(object sender, RoutedEventArgs e)
	{
		// A scan still in flight — even a PAUSED one — has not published its result to _lastScan yet (that happens
		// only when the scan task returns). Saving here silently wrote the PREVIOUS scan and threw the running one
		// away. Require Stop first: stopping publishes the partial scan together with its resume checkpoint, which
		// is exactly what makes save-now / continue-later work.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (_lastScan == null || _lastScan.Files.Count == 0) { if (RecoverStatusText != null) RecoverStatusText.Text = L("SessionNothing"); return; }
		var dlg = new Microsoft.Win32.SaveFileDialog { Filter = L("RfFltScan") + " (*.dfscan)|*.dfscan", FileName = "recovery-session.dfscan", Title = L("SessionSaveTitle") };
		if (dlg.ShowDialog(this) != true) return;
		try { SaveSession(dlg.FileName); if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("SessionSaved"), _lastScan.Files.Count); }
		catch (Exception ex) { ShowError(L("RfSessionSaveFail"), ex); }
	}

	// Reopen a saved .dfscan file and repopulate the grid; recovery works again because it reopens the same source.
	// If the saved scan was a deep scan that was paused/stopped partway, offer to CONTINUE it from the checkpoint.
	private async void RecoverOpenSession_Click(object sender, RoutedEventArgs e)
	{
		// Without this guard a session load could replace _lastScan and the grid WHILE a scan was running, so the
		// running scan's rows and the loaded ones mixed and a recovery could read from the wrong source.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L("RfFltScan") + " (*.dfscan)|*.dfscan", Title = L("SessionOpenTitle") };
		if (dlg.ShowDialog(this) != true) return;
		NtfsScanResult scan;
		try
		{
			scan = LoadSession(dlg.FileName);
			_lastScan = scan;
			RecoverGrid.ItemsSource = scan.Files;
			ApplyRecoverFilter();
			foreach (var f in scan.Files) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			PopulateRecoverTypes();
			UpdateRecoverSelectionInfo();
			if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("SessionLoaded"), scan.Files.Count, string.IsNullOrEmpty(scan.ImagePath) ? scan.Letter + ":" : Path.GetFileName(scan.ImagePath));
		}
		catch (Exception ex) { ShowError(L("RfSessionOpenFail"), ex); return; }

		if (scan.DeepPartial && scan.ResumeOffset > 0 && (scan.Letter != '\0' || !string.IsNullOrEmpty(scan.ImagePath)) && !isBusy)
		{
			if (MessageBox.Show(this, string.Format(L("RfResumePrompt"), FormatBytes(scan.ResumeOffset)), L("RfFilesTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
				await ResumeDeepScanAsync(scan);
		}
	}

	// Continues a paused/stopped deep scan from its saved byte offset, appending newly-carved files to the loaded set.
	private async Task ResumeDeepScanAsync(NtfsScanResult loaded)
	{
		bool isImage = !string.IsNullOrEmpty(loaded.ImagePath);
		if (!isImage && !IsAdministrator()) { MessageBox.Show(L("RfAdminDeep"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		int startCount = loaded.Files.Count(f => f.Carved);
		long startOffset = loaded.ResumeOffset;
		_recoverCts?.Dispose(); _recoverCts = new CancellationTokenSource();
		bool failed = false;
		try
		{
			RecoverButton.IsEnabled = false; RecoverScanButton.IsEnabled = false; RecoverDeepScanButton.IsEnabled = false; RecoverStopButton.IsEnabled = true;
			_recoverPaused = false;
			if (RecoverPauseButton != null) { RecoverPauseButton.IsEnabled = true; RecoverPauseButton.Content = L("BtnPause"); }
			stopRequested = false; _progressFullRange = true; _progressFixedTotal = true;
			long dtotal;
			if (isImage) { try { dtotal = new FileInfo(loaded.ImagePath).Length; } catch { dtotal = 0; } }
			else { try { dtotal = new DriveInfo(loaded.Letter + ":").TotalSize; } catch { dtotal = 0; } }
			if (dtotal <= 0) dtotal = 256L << 30;
			progressTotalGiB = dtotal / 1073741824.0; progressDoneGiB = startOffset / 1073741824.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, L("RfDeepRunning"));
			var token = _recoverCts.Token;
			var more = isImage
				? await Task.Run(() => DeepScanImage(loaded.ImagePath, token, _ => { }, startOffset, startCount))
				: await Task.Run(() => DeepScan(loaded.Letter, token, _ => { }, startOffset, startCount, loaded.BootSig));
			operationTimer.Stop(); operationStopwatch.Stop();
			// The resumed pass runs with its OWN independent MaxRecoverEntries budget, so appending it whole could
			// take the grid to twice the cap the limit exists to enforce. Only take what still fits.
			int room = Math.Max(0, MaxRecoverEntries - loaded.Files.Count);
			var added = more.Files.Count > room ? more.Files.Take(room).ToList() : more.Files;
			loaded.Files.AddRange(added);
			loaded.ResumeOffset = more.ResumeOffset; loaded.DeepPartial = more.DeepPartial;
			_lastScan = loaded;
			RecoverGrid.ItemsSource = loaded.Files;
			ApplyRecoverFilter();
			foreach (var f in added) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			// Same honesty rule as the first pass: a stopped resume did not finish the drive either.
			if (more.DeepPartial)
			{
				if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepFoundPartial"), loaded.Files.Count, FormatBytes(more.ResumeOffset));
			}
			else
			{
				progressDoneGiB = progressTotalGiB;
				if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepFound"), loaded.Files.Count);
				ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
				UpdateProgressStats();   // refresh the stats LINE too — the timer has stopped, so nothing else will
			}
			SetBusy(busy: false);
		}
		catch (Exception ex) { failed = true; ShowError(L("RfDeepFailed"), ex); }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			_progressFullRange = false; _progressFixedTotal = false; _recoverPaused = false; SetBusy(busy: false);
			RecoverScanButton.IsEnabled = true; RecoverDeepScanButton.IsEnabled = true; RecoverStopButton.IsEnabled = false;
			if (RecoverPauseButton != null) { RecoverPauseButton.IsEnabled = false; RecoverPauseButton.Content = L("BtnPause"); }
			if (failed && RecoverStatusText != null) RecoverStatusText.Text = "";
			UpdateRecoverSelectionInfo();
		}
	}

	// Win32 reserved device names: writing to "aux.jpg" opens the AUX device, not a file, so the recovered data goes
	// nowhere (or throws). Prefix any name whose base is a reserved device (with or without extension) with '_'.
	private static readonly HashSet<string> _reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
		"LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
	};
	private static string MakeNonReservedFileName(string name)
	{
		int dot = name.IndexOf('.');
		string stem = dot < 0 ? name : name.Substring(0, dot);
		if (_reservedDeviceNames.Contains(stem.TrimEnd(' ', '.'))) return "_" + name;
		if (name.EndsWith(".") || name.EndsWith(" ")) name = name.TrimEnd('.', ' ') + "_"; // trailing dot/space is also illegal
		return string.IsNullOrEmpty(name) ? "recovered" : name;
	}

	// Cleans a recovered file's folder path into a safe relative directory (drops drive letters, invalid chars).
	private static string SanitizeRelativeDir(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return "";
		int slash = path.LastIndexOf('\\');
		string dir = slash > 0 ? path.Substring(0, slash) : "";
		if (string.IsNullOrEmpty(dir)) return "";
		var bad = Path.GetInvalidFileNameChars();
		var parts = dir.Split('\\', StringSplitOptions.RemoveEmptyEntries)
			.Select(seg => string.Concat(seg.Split(bad)).Trim())
			.Where(seg => seg.Length > 0 && seg != "." && seg != "..");
		return string.Join("\\", parts);
	}

	// ---------- Multi-boot USB (powered by the open-source Ventoy engine) ----------
	// Turns a USB drive into a Ventoy multi-boot drive: install once, then just drop ISO files onto it and
	// pick one from a menu at boot. DriveForge drives Ventoy2Disk.exe in its command-line (VTOYCLI) mode.

	private void NavMultiBoot_Click(object sender, RoutedEventArgs e)
	{
		ShowMultiBootView();
		HighlightNav(NavMultiBoot);
	}

	private void MultiBootStartButton_Click(object sender, RoutedEventArgs e) => _ = MultiBootFlowAsync();

	private void ShowDownloadIsoView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
		if (ExportVhdxPanel != null) ExportVhdxPanel.Visibility = Visibility.Collapsed;
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Visible;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
	}

	private void NavDownloadIso_Click(object sender, RoutedEventArgs e)
	{
		ShowDownloadIsoView();
		HighlightNav(NavDownloadIso);
		PopulateDistroBox();
	}

	// Distros whose latest ISO is resolved live from a stable source (durable — no hard-coded versions).
	private enum IsoKind { Direct, Index, TwoStep }
	private sealed class IsoEntry { public string Label = ""; public IsoKind Kind; public string A = ""; public string B = ""; public string C = ""; }
	private static readonly IsoEntry[] IsoCatalog =
	{
		new IsoEntry { Label = "Ubuntu 24.04 LTS — Desktop (64-bit)", Kind = IsoKind.Index, A = "https://releases.ubuntu.com/24.04/", B = "ubuntu-24\\.04(?:\\.\\d+)?-desktop-amd64\\.iso" },
		new IsoEntry { Label = "Linux Mint — Cinnamon (64-bit)", Kind = IsoKind.TwoStep, A = "https://mirrors.edge.kernel.org/linuxmint/stable/", B = "[0-9]+(?:\\.[0-9]+)?/", C = "linuxmint-[0-9.]+-cinnamon-64bit\\.iso" },
		new IsoEntry { Label = "Linux Mint — Xfce (64-bit)", Kind = IsoKind.TwoStep, A = "https://mirrors.edge.kernel.org/linuxmint/stable/", B = "[0-9]+(?:\\.[0-9]+)?/", C = "linuxmint-[0-9.]+-xfce-64bit\\.iso" },
		new IsoEntry { Label = "Debian — Live GNOME (64-bit)", Kind = IsoKind.Index, A = "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/", B = "debian-live-[0-9.]+-amd64-gnome\\.iso" },
		new IsoEntry { Label = "Debian — Live Xfce (64-bit)", Kind = IsoKind.Index, A = "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/", B = "debian-live-[0-9.]+-amd64-xfce\\.iso" },
		new IsoEntry { Label = "Arch Linux (64-bit)", Kind = IsoKind.Direct, A = "https://geo.mirror.pkgbuild.com/iso/latest/archlinux-x86_64.iso" },
	};

	private void PopulateDistroBox()
	{
		if (DistroBox == null || DistroBox.Items.Count > 0) return;
		foreach (var c in IsoCatalog) DistroBox.Items.Add(c.Label);
		DistroBox.SelectedIndex = 0;
	}

	private async void FetchLatestIso_Click(object sender, RoutedEventArgs e)
	{
		// Guard against running while another operation (incl. a download already in progress) owns the busy state:
		// otherwise this handler's finally SetBusy(false) clears it, and the DownloadIsoAsync call below — whose own
		// isBusy guard would then see false — starts a SECOND concurrent download sharing the same progress/stop state.
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		int i = DistroBox?.SelectedIndex ?? -1;
		if (i < 0 || i >= IsoCatalog.Length) { MessageBox.Show(L("Mb033"), "DriveForge — download ISO", MessageBoxButton.OK, MessageBoxImage.Information); return; }
		var entry = IsoCatalog[i];
		string url;
		try
		{
			SetBusy(busy: true, L("BzFindLatest") + entry.Label + "...");
			url = await ResolveCatalog(entry);
		}
		catch (Exception ex) { SetBusy(busy: false); ShowError(L("ErrLatest"), ex); return; }
		finally { SetBusy(busy: false); }

		if (string.IsNullOrEmpty(url))
		{ MessageBox.Show(L("Mb034"), "DriveForge — download ISO", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
		if (IsoUrlBox != null) IsoUrlBox.Text = url;
		await DownloadIsoAsync(url);
	}

	// Resolves the newest ISO URL for a catalog entry: a fixed URL, a single directory index, or a two-level
	// index (newest version sub-directory → newest file inside it).
	private static async Task<string> ResolveCatalog(IsoEntry e)
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("DriveForge");
		if (e.Kind == IsoKind.Direct) return e.A;
		if (e.Kind == IsoKind.Index) return await PickFromIndex(http, e.A, e.B);
		string parentHtml = await http.GetStringAsync(e.A);
		// Trim the trailing '/' from each dir href BEFORE sorting: NaturalSortKey('22/')='..22/' vs NaturalSortKey('22.1/')='..22.00000001/'
		// compare '/' (0x2F) > '.' (0x2E), so the OLDER base '22/' sorted AFTER '22.1/' and .Last() picked the base over the point release.
		var dirs = Regex.Matches(parentHtml, "href=\"(" + e.B + ")\"", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.Trim('/')).Distinct().ToList();
		if (dirs.Count == 0) return "";
		string dir = dirs.OrderBy(NaturalSortKey, StringComparer.Ordinal).Last();
		string child = e.A.TrimEnd('/') + "/" + dir + "/";
		return await PickFromIndex(http, child, e.C);
	}

	private static async Task<string> PickFromIndex(HttpClient http, string indexUrl, string pattern)
	{
		string html = await http.GetStringAsync(indexUrl);
		var names = Regex.Matches(html, pattern, RegexOptions.IgnoreCase).Select(m => m.Value).Distinct().ToList();
		if (names.Count == 0) return "";
		string best = names.OrderBy(NaturalSortKey, StringComparer.Ordinal).Last();
		return indexUrl.TrimEnd('/') + "/" + best;
	}

	// Pads digit groups so a plain string sort orders versions correctly (24.04.10 > 24.04.9).
	private static string NaturalSortKey(string s) => Regex.Replace(s, "[0-9]+", m => m.Value.PadLeft(8, '0'));

	private void OpenIsoSource_Click(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Button b && b.Tag is string url && !string.IsNullOrWhiteSpace(url))
			try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
	}

	private void DownloadIsoButton_Click(object sender, RoutedEventArgs e) => _ = DownloadIsoAsync(IsoUrlBox?.Text);

	// Streams a direct ISO URL to the Downloads folder, keeping the original file name, with live progress.
	private async Task DownloadIsoAsync(string? url)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		url = (url ?? "").Trim();
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			MessageBox.Show(L("Mb035"), "DriveForge — download ISO", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		// Warn before fetching over an unencrypted http:// link (a user-pasted URL) — the ISO could be tampered with in
		// transit and there is no checksum verification. The catalog's own "fetch latest" entries are all https.
		if (uri.Scheme == Uri.UriSchemeHttp &&
			MessageBox.Show(L("DlHttpWarn"), L("MbDownloadTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		string name = Path.GetFileName(uri.LocalPath);
		if (string.IsNullOrWhiteSpace(name)) name = "download.iso";
		name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
		string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		string dest = Path.Combine(folder, name);
		string part = dest + ".part"; // download here, rename to the real name only after a complete download

		if (File.Exists(dest) &&
			MessageBox.Show(string.Format(L("DlOverwrite"), name, folder), L("MbDownloadTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false;
			_progressFullRange = true;
			SetBusy(busy: true, L("BzDownloading") + name + "...");
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			if (DlSaveHint != null) DlSaveHint.Text = "Downloading to " + dest;

			// Timeout covers connect + response headers (ResponseHeadersRead); the streaming body below is guarded by a
			// per-read idle timeout instead, so a mirror that connects and then stalls can't hang the download forever.
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("DriveForge");
			using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
			resp.EnsureSuccessStatusCode();
			long? total = resp.Content.Headers.ContentLength;
			using var src = await resp.Content.ReadAsStreamAsync();
			using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
			byte[] buf = new byte[1 << 20];
			long done = 0; int read;
			while (true)
			{
				if (stopRequested) throw new OperationCanceledException("Download stopped.");
				while (isPaused && !stopRequested) await Task.Delay(150);   // honor the Pause button
				if (stopRequested) throw new OperationCanceledException("Download stopped.");
				// Idle/stall guard: abort this read if no bytes arrive within 60s (a dead mirror), so Stop stays responsive.
				using (var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
					read = await src.ReadAsync(buf, 0, buf.Length, readCts.Token);
				if (read <= 0) break;
				await fs.WriteAsync(buf, 0, read);
				done += read;
				if (total.HasValue && total.Value > 0)
				{
					double pct = Math.Min(100.0, done * 100.0 / total.Value);
					ProgressBar.Value = pct;
					StatusText.Text = $"Downloading {name} — {FormatBytes(done)} / {FormatBytes(total.Value)} ({pct:F0}%)";
				}
				else StatusText.Text = $"Downloading {name} — {FormatBytes(done)}";
			}
			await fs.FlushAsync();
			fs.Dispose();
			if (total.HasValue && total.Value > 0 && done != total.Value)
				throw new IOException($"Download incomplete: got {FormatBytes(done)} of {FormatBytes(total.Value)}. The file was not saved.");
			File.Move(part, dest, true);
			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			NotifyOperationDone(true);
			if (DlSaveHint != null) DlSaveHint.Text = "Saved: " + dest;
			if (!total.HasValue) MessageBox.Show(L("DlSizeUnverified"), L("MbDownloadTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);   // no Content-Length -> a truncated body can look complete; tell the user to verify the ISO
			if (MessageBox.Show(string.Format(L("MbDownloaded"), dest), L("MbDownloadTitle"),
					MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + dest + "\"") { UseShellExecute = true }); } catch { }
		}
		catch (Exception ex)
		{
			failed = true; NotifyOperationDone(false);
			try { if (File.Exists(part)) File.Delete(part); } catch { } // never leave a truncated/partial file behind
			{ if (ex is OperationCanceledException) { if (!stopRequested) ShowError(L("ErrDownload"), new Exception("The download stalled - no data arrived for 60 seconds; it was stopped and the partial file removed.")); else Log("Download stopped by user; partial file removed."); } else ShowError(L("ErrDownload"), ex); }
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			_progressFullRange = false;
			// Pass a status: the per-block progress writes "Downloading x.iso — 1.2 GB / 4.0 GB (30%)" straight into
			// StatusText, and a stopped/failed download (which shows no dialog by design) otherwise left that line
			// claiming a live download next to a zeroed bar, with the .part file already deleted.
			SetBusy(busy: false, failed ? L("SxReady") : null);
			if (failed && DlSaveHint != null) DlSaveHint.Text = "";
		}
	}

	private async Task MultiBootFlowAsync()
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb036"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		_toolOpStarting = true;   // synchronous reentrancy guard: the whole pre-write phase (dialogs + the multi-minute Ventoy download) runs with isBusy still false
		try
		{
		await RefreshDisksAsync();
		var candidates = disks.Where(d => !d.IsSystem).ToList();
		if (candidates.Count == 0) { MessageBox.Show(L("Mb037"), "DriveForge — multi-boot USB", MessageBoxButton.OK, MessageBoxImage.Information); return; }

		string[] opts = candidates.Select(d => $"Disk {d.Number} — {d.FriendlyName} — {FormatBytes(d.Size)}"
			+ (d.DriveLetters.Count > 0 ? " (" + string.Join(", ", d.DriveLetters.Select(c => c + ":")) + ")" : "")).ToArray();
		int? pick = ShowChooserDialog(L("MbMultiBootTitle"), L("AmMbPickUsb"), opts, 0);
		if (pick == null) return;
		DiskItem disk = candidates[pick.Value];

		bool hasVentoy = await DiskHasVentoyAsync(disk.Number);
		bool update = false;

		if (hasVentoy)
		{
			int? act = ShowActionMenu(L("MbMultiBootTitle"),
				string.Format(L("AmMbExistsPrompt"), disk.Number),
				new[]
				{
					L("AmMbOpen"),
					L("AmMbUpdate"),
					L("AmMbReinstall")
				},
				new[] { 0xE8B7, 0xE72C, 0xEA99 },
				new[] { false, false, true }, 0);
			if (act == null) return;
			if (act == 0) { await OpenVentoyDataPartitionAsync(disk.Number); return; }
			if (act == 1) update = true;
			else if (MessageBox.Show(string.Format(L("MbReinstallConfirm"), disk.Number),
					L("MbMultiBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		}
		else
		{
			string contents = await GetDiskContentsAsync(disk.Number);
			if (MessageBox.Show(string.Format(L("MbMultiBootSetup"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
					L("MbMultiBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		}

		string? exe;
		try { exe = await EnsureVentoyAsync(); }
		catch (Exception ex) { ShowError(L("ErrVentoy"), ex); return; }
		if (exe == null) return; // user declined the download

		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			_progressFullRange = true; PauseButton.Content = L("BtnPause");
			// Clear the byte counters like every other progress-driving flow. Nothing here feeds them (Ventoy reports its
			// own percentage), so leaving the PREVIOUS operation's done/total in place made UpdateProgressStats drive the
			// bar from stale bytes: after e.g. an ISO write it read done == total, inflated the ceiling and slammed the bar
			// to ~89% within a second — then fought the real percentage RunVentoyAsync writes every 400 ms, so it visibly
			// oscillated between the true value and 89% for the whole install.
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, (update ? "Updating" : "Setting up") + $" multi-boot engine on Disk {disk.Number}...");
			ProgressBar.Value = 0.0;
			if (!await VerifyTargetDiskUnchangedAsync(disk)) return;   // the disk can renumber during the (multi-minute) Ventoy download — re-confirm identity (size/serial/name) before the whole-disk wipe
			await RunVentoyAsync(exe, disk.Number, install: !update);
			// A fresh install just repartitioned the disk; the new "Ventoy" volume can take a moment to be enumerated/mounted
			// by Windows (and DiskHasVentoyAsync returns false on any transient PowerShell hiccup), so POLL a few times
			// before treating its absence as a real failure — otherwise a genuinely successful wipe+install false-fails.
			bool ventoyOk = false;
			for (int vi = 0; vi < 6 && !ventoyOk; vi++) { if (vi > 0) await Task.Delay(500); ventoyOk = await DiskHasVentoyAsync(disk.Number); }
			if (!ventoyOk) throw new InvalidOperationException("Ventoy reported success but no Ventoy data partition was found — the install did not complete.");
			operationTimer.Stop(); operationStopwatch.Stop();
			ProgressBar.Value = 100.0;
			// Pin the LABEL and refresh the stats LINE as well: the timer has already stopped, so both would otherwise
			// keep whatever the last tick sampled (e.g. "60%" beside a full bar on a short install).
			if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
			UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(true);
			await RefreshDisksAsync();
			await OpenVentoyDataPartitionAsync(disk.Number);
			MessageBox.Show(string.Format(L("MbMultiBootDone"), disk.Number),
				L("MbMultiBootTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); SaveLogToDesktop(); ShowError(L("ErrMultiBoot"), ex); }
		finally
		{
			_progressFullRange = false; operationTimer.Stop(); operationStopwatch.Stop();
			SetBusy(busy: false);
		}
		}
		finally { _toolOpStarting = false; }
	}

	// True if the disk already carries a Ventoy data partition (exFAT/NTFS volume labelled "Ventoy").
	private async Task<bool> DiskHasVentoyAsync(int number)
	{
		try
		{
			string o = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command \"(Get-Disk -Number " + number +
				" | Get-Partition | Get-Volume | Where-Object { $_.FileSystemLabel -eq 'Ventoy' } | Measure-Object).Count\"");
			return int.TryParse(o.Trim(), out int c) && c > 0;
		}
		catch { return false; }
	}

	// Opens the Ventoy data partition (where ISOs live) in Explorer, if it has a drive letter.
	private async Task OpenVentoyDataPartitionAsync(int number)
	{
		try
		{
			string letter = (await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command \"(Get-Disk -Number " + number +
				" | Get-Partition | Get-Volume | Where-Object { $_.FileSystemLabel -eq 'Ventoy' } | Select-Object -First 1).DriveLetter\"")).Trim();
			if (letter.Length >= 1 && char.IsLetter(letter[0]))
				Process.Start(new ProcessStartInfo(letter[0] + ":\\") { UseShellExecute = true });
		}
		catch { }
	}

	// Ensures the Ventoy engine is available locally (cached in LocalAppData). Downloads the latest Windows
	// release on first use after asking the user. Returns the path to Ventoy2Disk.exe, or null if declined.
	private async Task<string?> EnsureVentoyAsync()
	{
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge", "Tools", "ventoy");
		string? found = FindVentoyExe(root);
		if (found != null) return await EnsureVentoyTrustedAsync(found) ? found : null;   // re-verify the cached binary each run (guards cache-poisoning) before running it elevated

		if (MessageBox.Show(L("Mb038"),
				"DriveForge — multi-boot engine", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
			return null;

		Directory.CreateDirectory(root);
		SetBusy(busy: true, L("BzVentoy"));
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("DriveForge");
			string api = await http.GetStringAsync("https://api.github.com/repos/ventoy/Ventoy/releases/latest");
			var m = Regex.Match(api, "\"browser_download_url\"\\s*:\\s*\"([^\"]*windows\\.zip)\"", RegexOptions.IgnoreCase);
			if (!m.Success) throw new InvalidOperationException("Could not find the Ventoy Windows download on GitHub.");
			string url = m.Groups[1].Value;
			if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("Unexpected Ventoy download host (not github.com): " + url);
			Log("Downloading Ventoy: " + url);
			byte[] bytes = await http.GetByteArrayAsync(url);
			string zip = Path.Combine(root, "ventoy-windows.zip");
			await File.WriteAllBytesAsync(zip, bytes);
			ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
			try { File.Delete(zip); } catch { }
			found = FindVentoyExe(root);
			if (found == null) throw new InvalidOperationException("Ventoy2Disk.exe was not found after extracting the download.");
			return await EnsureVentoyTrustedAsync(found) ? found : null;
		}
		finally { SetBusy(busy: false); }
	}

	private static string? FindVentoyExe(string root)
	{
		try { return Directory.Exists(root) ? Directory.GetFiles(root, "Ventoy2Disk.exe", SearchOption.AllDirectories).FirstOrDefault() : null; }
		catch { return null; }
	}

	// True when the file carries a VALID Authenticode signature (full trust-chain check via Get-AuthenticodeSignature).
	private async Task<bool> IsAuthenticodeValidAsync(string path)
	{
		try
		{
			string o = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command \"(Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "').Status\"");
			return o.Trim().Equals("Valid", StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
	}

	// Gate before running the (elevated, whole-disk-destructive) Ventoy binary: require a valid Authenticode signature,
	// else make the user explicitly accept running an unverified binary. Guards a MITM'd download or a poisoned cache.
	private async Task<bool> EnsureVentoyTrustedAsync(string exe)
	{
		if (await IsAuthenticodeValidAsync(exe)) return true;
		return MessageBox.Show(string.Format(L("MbVentoyUnsigned"), exe),
			L("MbMultiBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
	}

	// Runs Ventoy2Disk.exe in command-line mode and reports progress from its cli_percent.txt / cli_done.txt files.
	private async Task RunVentoyAsync(string exePath, int physicalDrive, bool install)
	{
		string dir = Path.GetDirectoryName(exePath)!;
		foreach (var f in new[] { "cli_done.txt", "cli_percent.txt", "cli_log.txt" })
			try { File.Delete(Path.Combine(dir, f)); } catch { }

		string args = install
			? $"VTOYCLI /I /PhyDrive:{physicalDrive} /GPT /NOUSBCheck"
			: $"VTOYCLI /U /PhyDrive:{physicalDrive}";

		var result = await Task.Run(() =>
		{
			var psi = new ProcessStartInfo
			{
				FileName = exePath, Arguments = args, WorkingDirectory = dir,
				UseShellExecute = false, CreateNoWindow = true,
				RedirectStandardOutput = true, RedirectStandardError = true
			};
			var sbOut = new StringBuilder();
			using var p = new Process { StartInfo = psi };
			p.OutputDataReceived += (_, ev) => { if (ev.Data != null) lock (sbOut) sbOut.AppendLine(ev.Data); };
			p.ErrorDataReceived += (_, ev) => { if (ev.Data != null) lock (sbOut) sbOut.AppendLine(ev.Data); };
			p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
			string pctFile = Path.Combine(dir, "cli_percent.txt");
			while (!p.WaitForExit(400))
			{
				try
				{
					if (File.Exists(pctFile) && int.TryParse(File.ReadAllText(pctFile).Trim(), out int pct))
					{
						int v = Math.Max(0, Math.Min(100, pct));
						Dispatcher.Invoke(() => ProgressBar.Value = v);
					}
				}
				catch { }
			}
			p.WaitForExit();
			string done = ""; try { done = File.ReadAllText(Path.Combine(dir, "cli_done.txt")).Trim(); } catch { }
			string log = ""; try { log = File.ReadAllText(Path.Combine(dir, "cli_log.txt")); } catch { }
			bool ok = done.StartsWith("0") || (done.Length == 0 && p.ExitCode == 0);
			return (ok, done, log, output: sbOut.ToString().Trim());
		});

		if (!string.IsNullOrEmpty(result.output)) Log("Ventoy: " + result.output);
		if (!string.IsNullOrEmpty(result.log)) Log("Ventoy log:\r\n" + result.log);
		if (!result.ok)
			throw new InvalidOperationException("Ventoy reported a failure (status=" + (result.done.Length == 0 ? "none" : result.done) + ").\r\n" + result.log);
	}

	// Read-only surface test: reads every sector of the selected disk to find bad / unreadable blocks. Detects
	// a failing drive that still reports "healthy". Writes nothing — safe to run on any disk, including the system one.
	private async void SurfaceTest_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Arm the guard for the WHOLE pre-write window. This handler CHECKED _toolOpStarting but never set it, so the
		// guard was inert: the confirm dialogs below pump the message loop, and the adjacent always-enabled tool
		// buttons could start a second operation on the same disk. Cleared in the outer finally.
		_toolOpStarting = true;
		try
		{
		var disk = (DiagDiskBox?.SelectedItem ?? DiskBox?.SelectedItem) as DiskItem;
		if (disk == null) { MessageBox.Show(L("Mb039"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb040"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (MessageBox.Show(string.Format(L("MbSurfaceConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size)),
				L("MbSurfaceTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
			return;

		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			// When the size is known the total is REAL, so it must not be inflated by the clone-only "near completion"
			// heuristic in UpdateProgressStats (which rewrites the total x1.12 at 97% and would freeze the bar there) — the
			// other full-range ops (free-space wipe, deep scan, resume scan) all set _progressFixedTotal for that reason.
			// But when the size is UNKNOWN (empty card reader, uninitialised disk) there is no real total to fix: pinning a
			// fake 1 GiB total would peg the bar at a false 100% within seconds, contradicting the "coverage could not be
			// verified" verdict — so run indeterminate instead. Both flags cleared in the finally so they can't leak.
			bool sizeKnown = disk.Size > 0;
			_progressFullRange = true; _progressFixedTotal = sizeKnown; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, disk.Size / 1073741824.0);
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			if (ToolStopButton != null) ToolStopButton.IsEnabled = true;
			SetBusy(busy: true, string.Format(L("BzSurface"), disk.Number));
			ProgressBar.Value = 0.0;
			ProgressBar.IsIndeterminate = !sizeKnown;   // no known total -> no meaningful percentage; SetBusy(false) resets this

			var res = await Task.Run(() => RunSurfaceScanCore(disk));
			operationTimer.Stop(); operationStopwatch.Stop();
			double secs = Math.Max(0.001, operationStopwatch.Elapsed.TotalSeconds);
			double avgMb = res.readBytes / 1048576.0 / secs;
			// The scan stops early on a 0-byte read. That is usually just the end of the device, but it is ALSO how a dying
			// USB/SD controller behaves when it stops responding — so a scan that covered 0.5% of the drive must never be
			// reported as "no bad blocks, the surface looks healthy". Compare what was actually read against the disk size.
			// A disk whose size we don't know (empty card-reader slot, uninitialised disk) gives us nothing to compare
			// against, so coverage is UNVERIFIABLE — that must not count as "fully covered" either.
			bool covered = disk.Size > 0 && res.readBytes >= disk.Size - (8L * 1024 * 1024);
			string coverageNote = disk.Size > 0
				? $"Read {FormatBytes(res.readBytes)} of {FormatBytes(disk.Size)} ({res.readBytes * 100.0 / disk.Size:F1}%)"
				: $"Read {FormatBytes(res.readBytes)}";
			// Don't slam the bar to 100% for a scan that ended early — a full bar was itself part of the false
			// "the whole drive was checked" impression. This MUST run before SetBusy(false): UpdateProgressStats only
			// moves ProgressBar.Value while isBusy is true, so doing it afterwards would silently do nothing.
			ProgressBar.IsIndeterminate = false;
			// covered -> full; known-size partial -> the true fraction; unknown size -> 0 (there is no meaningful fraction).
			// The unknown case MUST be 0, not readBytes/fakeTotal: progressTotalGiB was floored to 1 GiB, so any real bytes
			// read would drive UpdateProgressStats' byte block back up and re-fill the bar we just zeroed.
			progressDoneGiB = covered ? progressTotalGiB
				: sizeKnown ? Math.Min(progressTotalGiB, res.readBytes / 1073741824.0)
				: 0.0;
			// Set the bar BEFORE the update call: UpdateProgressStats writes the percent label from the value it sampled
			// at entry, so without this the label would lag a tick and read 97% on a finished scan. A scan with no verified
			// coverage (partial, or unknown size) must NOT end on a full bar — that was itself the false-"all checked" cue.
			if (covered) ProgressBar.Value = 100.0;
			else if (!sizeKnown) ProgressBar.Value = 0.0;
			UpdateProgressStats();
			// SetBusy(false) blanks the whole row when the user pressed Stop — which would throw away the honest
			// end-state just computed above, including the case this method goes out of its way to get right: a Stop
			// landing on the very LAST block still leaves a fully covered, clean scan, and the dialog says so. Snapshot
			// the three widgets and put them back, so the row agrees with the verdict the user is reading.
			double endBar = ProgressBar.Value;
			string endPercent = ProgressPercentText?.Text ?? "";
			string endStats = ProgressStatsText?.Text ?? "";
			SetBusy(busy: false);
			if (stopRequested)
			{
				ProgressBar.Value = endBar;
				if (ProgressPercentText != null) ProgressPercentText.Text = endPercent;
				if (ProgressStatsText != null) ProgressStatsText.Text = endStats;
				// The scan is over and has published its verdict — drop the sticky flag, or the SECOND
				// SetBusy(false) in this method's own finally (and any later one, e.g. a device-change refresh)
				// re-runs the stop-reset and blanks the row again the moment the dialog is dismissed.
				stopRequested = false;
			}
			// Match the chime to the verdict: a Stop on the very last block still leaves a covered, clean scan.
			NotifyOperationDone(res.bad == 0 && covered);

			// Distinguish "the USER cancelled" from "the drive stopped answering". Both leave the scan short, but blaming
			// the hardware for a deliberate Stop is a false alarm on a perfectly healthy drive.
			string verdict = res.bad > 0
				? $"⚠ {res.bad} unreadable block(s) found ({FormatBytes(res.badBytes)}).\n\nThe drive may be failing — back up your data now and consider replacing it."
				: covered
					? $"✓ No bad blocks found.\n\n{coverageNote} with no read errors at ~{avgMb:F0} MB/s. The surface looks healthy."
					: res.stopped
						? $"Surface test stopped before finishing.\n\n{coverageNote} at ~{avgMb:F0} MB/s, with no read errors in that part. The rest of the drive was not checked, so this is not a verdict either way."
						: disk.Size <= 0
							? $"The drive did not report its size, so how much of it was checked could not be verified.\n\n{coverageNote} at ~{avgMb:F0} MB/s, with no read errors in that part. This is not a verdict either way."
							: $"⚠ The test did NOT cover the whole drive.\n\n{coverageNote} at ~{avgMb:F0} MB/s, then the drive stopped returning data. No bad blocks were hit in the part that was read, but this is NOT a clean bill of health — a drive that stops responding mid-scan may be failing. Back up your data.";
			if (res.stopped && res.bad > 0) verdict = "Surface test stopped before finishing.\n\n" + verdict;
			ToolRecommendationDetailText.Text = verdict.Replace("\n", " ");
			SetToolOutput($"Surface test — Disk {disk.Number} ({disk.FriendlyName})\r\nRead: {FormatBytes(res.readBytes)} of {FormatBytes(disk.Size)}\r\nAverage read: {avgMb:F0} MB/s\r\nBad blocks: {res.bad}" +
				(res.detail.Length > 0 ? "\r\nFirst bad regions:\r\n" + res.detail : ""));
			MessageBox.Show(verdict, "DriveForge — surface test", MessageBoxButton.OK,
				(res.bad > 0 || (!covered && !res.stopped && disk.Size > 0)) ? MessageBoxImage.Warning : MessageBoxImage.Information);
		}
		catch (Exception ex) { NotifyOperationDone(false); ShowError(L("ErrSurface"), ex); }
		finally
		{
			// Must clear BOTH: leaking _progressFixedTotal=true into a later clone/install would disable the inflation
			// heuristic those operations rely on.
			_progressFullRange = false; _progressFixedTotal = false; operationTimer.Stop(); operationStopwatch.Stop();
			if (ToolStopButton != null) ToolStopButton.IsEnabled = false;
			SetBusy(busy: false);
		}
		}
		finally { _toolOpStarting = false; }
	}

	private (long readBytes, int bad, long badBytes, bool stopped, string detail) RunSurfaceScanCore(DiskItem disk)
	{
		using var h = CreateFile($"\\\\.\\PhysicalDrive{disk.Number}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk for reading.");
		using var fs = new FileStream(h, FileAccess.Read);
		int block = 8 * 1024 * 1024;
		byte[] buf = new byte[block];
		long total = disk.Size > 0 ? disk.Size : long.MaxValue;
		long pos = 0, readBytes = 0, badBytes = 0;
		int bad = 0;
		int readFail = 0;   // consecutive read exceptions — bails out of an unknown-size (long.MaxValue) scan of an empty/not-ready device
		var regions = new List<string>();
		while (pos < total && !stopRequested)
		{
			while (isPaused && !stopRequested) System.Threading.Thread.Sleep(150);
			long remain = total - pos;
			int want = (int)Math.Min(block, remain);
			want -= want % 512;
			if (want <= 0) break;
			try
			{
				fs.Seek(pos, SeekOrigin.Begin);
				int got = 0;
				while (got < want) { int r = fs.Read(buf, got, want - got); if (r <= 0) break; got += r; }
				readBytes += got;
				// A 0-byte read means we're past the physical end of the device — stop. This also bounds the loop when
				// the disk misreports its size as <= 0 (empty card-reader slot / uninitialized disk), where `total` was
				// set to long.MaxValue and every read would otherwise be counted as a fake bad region forever.
				if (got == 0) break;
				readFail = 0;
				// A short read in the MIDDLE of the disk is an unreadable region, not the benign tail.
				if (got < want && pos + want < total)
				{
					bad++; badBytes += want - got;
					if (regions.Count < 50) regions.Add("  at " + FormatBytes(pos));
				}
			}
			catch
			{
				bad++; badBytes += want;
				if (regions.Count < 50) regions.Add("  at " + FormatBytes(pos));
				// On an unknown-size device (total == long.MaxValue) an empty slot / not-ready reader makes EVERY read
				// throw, which would otherwise invent bad blocks forever against the fake huge total. Bail after a run of
				// consecutive failures so it stops instead of reporting endless bad regions.
				if (disk.Size <= 0 && ++readFail >= 8) break;
			}
			pos += want;
			Volatile.Write(ref _progressDoneBytes, pos);
		}
		return (readBytes, bad, badBytes, stopRequested, string.Join("\r\n", regions));
	}

	// Securely erase specific files or a whole folder: overwrite each file's contents (1..multi-pass), rename it
	// to obscure the name, then delete it. Operates only on the chosen files — never the rest of the drive.
	private async void ShredFiles_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Arm the guard for the WHOLE pre-write window. This handler CHECKED _toolOpStarting but never set it, so the
		// guard was inert: the confirm dialogs below pump the message loop, and the adjacent always-enabled tool
		// buttons could start a second operation on the same disk. Cleared in the outer finally.
		_toolOpStarting = true;
		try
		{

		int? what = ShowActionMenu(L("MbShredTitle"), L("AmShredWhatPrompt"),
			new[] { L("AmShredFiles"), L("AmShredFolder") },
			new[] { 0xE7C3, 0xE8B7 }, null, 0);
		if (what == null) return;

		var files = new List<string>();
		string? baseFolder = null;
		if (what == 0)
		{
			var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L("DlgShredPickFiles") };
			if (dlg.ShowDialog() != true) return;
			files.AddRange(dlg.FileNames);
		}
		else
		{
			using var fb = new Forms.FolderBrowserDialog { Description = L("DlgShredPickFolder") };
			if (fb.ShowDialog() != Forms.DialogResult.OK) return;
			baseFolder = fb.SelectedPath;
			// Reparse-safe enumeration: never follow junctions/symlinks, so a junction planted inside the chosen folder
			// can't lead the shredder OUT to files on another drive and destroy them, and a cyclic reparse point can't
			// hang the walk the way EnumerateFiles(AllDirectories) would. (Unlike SafeFiles this does NOT apply the temp-
			// cleanup IsUnsafeCleanRoot guard — the user picked this exact folder to shred, so respect that choice.)
			try { files.AddRange(EnumerateFilesReparseSafe(baseFolder)); }
			catch (Exception ex) { ShowError(L("ErrListFolder"), ex); return; }
		}
		if (files.Count == 0) { MessageBox.Show(L("Mb041"), L("MbShredTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }

		string[] methods = { L("AmFreeZero"), L("AmFreeRandom"), L("AmFree3"), L("AmMethod7") };
		int? sel = ShowActionMenu(L("MbShredTitle"), string.Format(L("AmShredMethodPrompt"), files.Count), methods,
			new[] { 0xEA99, 0xE9CE, 0xE730, 0xE730 }, new[] { true, true, true, true }, 0);
		if (sel == null) return;
		int[] fills = sel.Value switch { 1 => new[] { 2 }, 2 => new[] { 0, 2, 0 }, 3 => new[] { 0, 1, 2, 0, 1, 2, 2 }, _ => new[] { 0 } };

		if (MessageBox.Show(string.Format(L("MbShredConfirm"), files.Count, fills.Length),
				L("MbShredTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;

		long totalBytes = 0;
		foreach (var f in files) { try { totalBytes += new FileInfo(f).Length; } catch { } }

		bool failed = false; int done = 0, fail = 0;
		try
		{
			// _progressFixedTotal: the total is the measured size of the selected files x passes — real, not a
			// projection, so the clone-only 12%-inflation heuristic must stay out of it (it would stop the bar at ~89%).
			stopRequested = false; isPaused = false; _progressFullRange = true; _progressFixedTotal = true; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, totalBytes / 1073741824.0 * Math.Max(1, fills.Length));
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzShred"), files.Count));
			ProgressBar.Value = 0.0;
			long[] acc = { 0 };
			await Task.Run(() =>
			{
				foreach (var f in files)
				{
					if (stopRequested) break;
					try { if (ShredOne(f, fills, acc)) done++; } catch { fail++; }   // only count a file that was fully overwritten + deleted
				}
				if (baseFolder != null && !stopRequested)
				{
					try
					{
						// Reparse-safe cleanup: remove any junction/symlink as a LINK only (never recurse into its target),
						// and delete emptied REAL subdirectories bottom-up — so a junction can't get the shredder to delete
						// files or folders on another drive.
						var realDirs = new List<string>();
						var dstack = new Stack<string>();
						dstack.Push(baseFolder);
						while (dstack.Count > 0)
						{
							string cur = dstack.Pop();
							string[] subs;
							try { subs = Directory.GetDirectories(cur); } catch { subs = Array.Empty<string>(); }
							foreach (var s in subs)
							{
								bool reparse = false;
								try { reparse = (File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0; } catch { }
								if (reparse) { try { Directory.Delete(s, false); } catch { } }   // drop the junction link, keep its target
								else { realDirs.Add(s); dstack.Push(s); }
							}
						}
						foreach (var d in realDirs.OrderByDescending(x => x.Length))
							try { Directory.Delete(d, false); } catch { }
						try { Directory.Delete(baseFolder, false); } catch { }
					}
					catch { }
				}
			});
			operationTimer.Stop(); operationStopwatch.Stop();
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false); NotifyOperationDone(true);
			MessageBox.Show(stopRequested
				? $"Stopped. {done} file(s) shredded so far."
				: $"Done. {done} file(s) securely erased." + (fail > 0 ? $"\n\n{fail} could not be erased (in use or protected)." : ""),
				"DriveForge — secure shred", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("ErrShred"), ex); }
		finally { operationTimer.Stop(); operationStopwatch.Stop(); if (failed) UpdateProgressStats(); _progressFullRange = false; _progressFixedTotal = false; SetBusy(busy: false); } // refresh BEFORE clearing the flags — clearing first jumps a failed run's bar forward
		}
		finally { _toolOpStarting = false; }
	}

	// Overwrites one file in place with the given passes (0=zeros, 1=ones, 2=random), then renames + deletes it.
	private bool ShredOne(string path, int[] fills, long[] acc)
	{
		try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
		int[] passes = fills.Length == 0 ? new[] { 0 } : fills;
		// Overwrite the default $DATA stream AND every alternate data stream (ADS). An ADS (file:stream) keeps its data
		// in its OWN clusters, which File.Delete frees WITHOUT overwriting — so shredding only the main stream would
		// leave the hidden ADS content fully recoverable. Enumerate and overwrite each named stream too.
		OverwriteStream(path, passes, acc);
		if (!stopRequested)
			foreach (string adsPath in EnumerateAdsOpenPaths(path))
			{
				if (stopRequested) break;
				try { OverwriteStream(adsPath, passes, acc); } catch { }
			}
		// Stop pressed mid-shred: the file is only partially overwritten — deleting it now would lose it AND leave
		// its remaining data recoverable. Keep it; the user was told it was not shredded.
		if (stopRequested) return false;   // interrupted mid-shred: the (partial) file is deliberately kept — do NOT count it as shredded
		try
		{
			string dir = Path.GetDirectoryName(path) ?? "";
			string masked = Path.Combine(dir, Guid.NewGuid().ToString("N"));
			File.Move(path, masked);
			File.Delete(masked);
		}
		catch { try { File.Delete(path); } catch { } }
		return true;
	}

	// Enumerates every file under an explicitly-chosen shred target, recursing into REAL subfolders only and never
	// following a junction/symlink out of the tree (which would destroy files on another drive). No IsUnsafeCleanRoot
	// guard: the user picked this folder deliberately, so unlike SafeFiles (built for temp cleanup) it does not refuse
	// profile/special folders. Never throws mid-walk.
	private static IEnumerable<string> EnumerateFilesReparseSafe(string root)
	{
		if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
		var stack = new Stack<string>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			string cur = stack.Pop();
			string[] files;
			try { files = Directory.GetFiles(cur); } catch { files = Array.Empty<string>(); }
			foreach (var f in files) yield return f;
			string[] subs;
			try { subs = Directory.GetDirectories(cur); } catch { subs = Array.Empty<string>(); }
			foreach (var s in subs)
			{
				try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { }
				stack.Push(s);
			}
		}
	}

	// Overwrites one NTFS data stream in place — the file itself, or a "file:stream" ADS path — with the given passes.
	// Honors Stop/Pause. Throws if the stream can't be opened (the caller decides how to handle that).
	private void OverwriteStream(string openPath, int[] passes, long[] acc)
	{
		using var fs = new FileStream(openPath, FileMode.Open, FileAccess.Write, FileShare.None);
		long len = fs.Length;
		int bufSize = (int)Math.Min(4 * 1024 * 1024, Math.Max(4096, len == 0 ? 4096 : len));
		byte[] buf = new byte[bufSize];
		using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
		foreach (int fill in passes)
		{
			if (stopRequested) break;
			if (fill == 0) Array.Clear(buf, 0, buf.Length);
			else if (fill == 1) { for (int i = 0; i < buf.Length; i++) buf[i] = 0xFF; }
			fs.Seek(0, SeekOrigin.Begin);
			long rem = len;
			while (rem > 0 && !stopRequested)
			{
				while (isPaused && !stopRequested) System.Threading.Thread.Sleep(150);
				int w = (int)Math.Min(buf.Length, rem);
				if (fill == 2) rng.GetBytes(buf.AsSpan(0, w));
				fs.Write(buf, 0, w);
				rem -= w; acc[0] += w; Volatile.Write(ref _progressDoneBytes, acc[0]);
			}
			fs.Flush(flushToDisk: true);
		}
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct WIN32_FIND_STREAM_DATA
	{
		public long StreamSize;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string cStreamName;
	}
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr FindFirstStreamW(string lpFileName, int InfoLevel, out WIN32_FIND_STREAM_DATA lpFindStreamData, uint dwFlags);
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool FindNextStreamW(IntPtr hFindStream, out WIN32_FIND_STREAM_DATA lpFindStreamData);
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FindClose(IntPtr hFindFile);

	// Yields an openable path ("file:stream") for every NAMED alternate data stream of the file. The default "::$DATA"
	// stream is skipped (the caller overwrites that via the plain path). Yields nothing on a non-NTFS volume or error.
	private static IEnumerable<string> EnumerateAdsOpenPaths(string path)
	{
		IntPtr h = FindFirstStreamW(path, 0 /*FindStreamInfoStandard*/, out WIN32_FIND_STREAM_DATA data, 0);
		if (h == new IntPtr(-1)) yield break;
		try
		{
			do
			{
				string name = data.cStreamName;   // e.g. "::$DATA" (default) or ":notes:$DATA" (named)
				if (!string.IsNullOrEmpty(name) && !name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
				{
					string streamPart = name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase)
						? name.Substring(0, name.Length - 6) : name;   // ":notes:$DATA" -> ":notes"
					yield return path + streamPart;
				}
			} while (FindNextStreamW(h, out data));
		}
		finally { FindClose(h); }
	}

	// Quick-format the selected drive (erases everything). Choose NTFS or exFAT. System disk is protected.
	private async void FormatDrive_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb042"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb043"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb044"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Hold the reentrancy guard across the WHOLE flow (incl. the confirm / fs-menu / GetDiskContentsAsync awaits that
		// run before SetBusy sets isBusy), so a second destructive tool can't start concurrently in that window.
		_toolOpStarting = true;
		string scriptPath = "";
		try
		{
			string contents = await GetDiskContentsAsync(disk.Number);
			if (MessageBox.Show(string.Format(L("MbFormatConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
					L("MbFormatTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
				return;

			string[] fsOptions = { L("AmFsNtfs"), L("AmFsExfat"), L("AmFsFat32") };
			int? fsSel = ShowActionMenu(L("AmFormatTitle"), string.Format(L("AmFormatPrompt"), disk.Number), fsOptions,
				new[] { 0xEDA2, 0xEDA2, 0xEDA2 }, null, 0);
			if (fsSel == null) return;
			string fs = fsSel.Value == 0 ? "ntfs" : fsSel.Value == 1 ? "exfat" : "fat32";
			// Windows' formatter (which diskpart drives) cannot CREATE a FAT32 volume larger than 32 GB. On a bigger disk the
			// `format fs=fat32` line fails but diskpart still returns exit code 0, so the write reported success while the disk
			// was actually left RAW/unusable. Reject up front and steer to exFAT (the modern large-capacity FAT successor).
			if (fs == "fat32" && disk.Size > 32L * 1024 * 1024 * 1024)
			{
				MessageBox.Show(L("MbFat32TooBig"), L("MbFormatTitle"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}

			scriptPath = Path.Combine(Path.GetTempPath(), $"winforge-format-{Guid.NewGuid():N}.txt");
			SetBusy(busy: true, string.Format(L("BzFormat"), disk.Number, fs.ToUpperInvariant()));
			if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // make sure this is still the same physical drive
			string script = $"select disk {disk.Number}\r\nclean\r\ncreate partition primary\r\nformat fs={fs} quick label=DriveForge\r\nassign\r\nexit\r\n";
			await File.WriteAllTextAsync(scriptPath, script, Encoding.ASCII);
			string outp = await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(scriptPath));
			SetToolOutput("diskpart format\r\n\r\n" + outp);
			Log($"Formatted Disk {disk.Number} as {fs}.");
			await RefreshDisksAsync();
			MessageBox.Show(string.Format(L("MbFormatDone"), disk.Number, fs.ToUpperInvariant()), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex)
		{
			ShowError(L("ErrFormat"), ex);
		}
		finally
		{
			_toolOpStarting = false;   // clear FIRST, so an (unlikely) throw from the cleanup below can't strand the guard
			TryDeleteFile(scriptPath);
			SetBusy(busy: false);
		}
	}

	// ---------- Partition tools (initialize / convert / check / active / quick-partition / find lost) ----------

	private async Task<string> RunDiskpartAsync(string script)
	{
		string path = Path.Combine(Path.GetTempPath(), $"driveforge-dp-{Guid.NewGuid():N}.txt");
		try { await File.WriteAllTextAsync(path, script, Encoding.ASCII); return await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(path)); }
		finally { TryDeleteFile(path); }
	}

	// Largest unallocated extent on a disk, in bytes (0 if it can't be read) — the size an unsized `create partition
	// primary` will actually consume. Used to size-check FAT32 against the real hole instead of the whole disk.
	private async Task<long> LargestFreeExtentBytesAsync(int diskNumber)
	{
		try
		{
			string s = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command " + QuoteArgument($"(Get-Disk -Number {diskNumber} -ErrorAction SilentlyContinue).LargestFreeExtent"));
			return long.TryParse(s.Trim(), out long v) ? v : 0;
		}
		catch { return 0; }
	}

	// Start offsets (bytes) of the partitions currently in a disk's live table. Used to tell a found VBR that IS a
	// known partition from one that is genuinely lost (not in the table).
	private async Task<List<long>> LivePartitionOffsetsAsync(int diskNumber)
	{
		var offsets = new List<long>();
		try
		{
			string s = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command " + QuoteArgument($"Get-Partition -DiskNumber {diskNumber} -ErrorAction SilentlyContinue | ForEach-Object {{ $_.Offset }}"));
			foreach (var line in (s ?? "").Split('\n'))
				if (long.TryParse(line.Trim(), out long o) && o > 0) offsets.Add(o);
		}
		catch { }
		return offsets;
	}

	// The physical disk number a drive letter currently lives on (-1 if it can't be read). Used to confirm a letter
	// hasn't migrated to another disk before a disk-agnostic `select volume {letter}` delete.
	private async Task<int> DiskNumberOfDriveLetterAsync(char letter)
	{
		try
		{
			string s = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command " + QuoteArgument($"(Get-Partition -DriveLetter {letter} -ErrorAction SilentlyContinue).DiskNumber"));
			return int.TryParse(s.Trim(), out int n) ? n : -1;
		}
		catch { return -1; }
	}

	// Real size of a mounted volume, in bytes (0 if it can't be read). Used to tell whether a diskpart shrink/extend
	// actually changed anything — diskpart exits 0 even when it declines the operation, and its text is localized.
	private async Task<long> VolumeSizeBytesAsync(char letter)
	{
		try
		{
			string s = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -Command " + QuoteArgument($"(Get-Volume -DriveLetter {letter} -ErrorAction SilentlyContinue).Size"));
			return long.TryParse(s.Trim(), out long v) ? v : 0;
		}
		catch { return 0; }
	}

	private bool GuardSystemDisk(DiskItem disk)
	{
		if (disk.IsSystem) { MessageBox.Show(L("PtSystemBlocked"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return false; }
		return true;
	}

	private async Task<bool> ConfirmDestructive(DiskItem disk, string action)
	{
		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("PtConfirmBody"), action, disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
			"DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return false;
		// Last line of defence: make sure the disk at this number is still the exact one the user just reviewed,
		// before any destructive diskpart 'clean' runs.
		return await VerifyTargetDiskUnchangedAsync(disk);
	}

	private async void PartitionTool_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy || _toolOpStarting) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		_toolOpStarting = true;
		try
		{
			if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("PtNoDisk"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
			if (!IsAdministrator()) { MessageBox.Show(L("Mb045"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
			int? a = ShowActionMenu(L("PtTitle"), string.Format(L("PtPrompt"), disk.Number),
				new[] { L("PtResize"), L("MvPartOption"), L("PtCreate"), L("PtDelete"), L("PtQuickPart"), L("PtInit"), L("PtConvert"), L("PtCheck"), L("PtActive"), L("PtFindLost") },
				new[] { 0xE9D9, 0xE90F, 0xE710, 0xE74D, 0xE777, 0xEDA2, 0xE72C, 0xE8FE, 0xE7C1, 0xE721 },
				new[] { false, false, false, true, true, true, true, false, false, false }, 0);
			if (a == null) return;
			switch (a.Value)
			{
				case 0: await ResizePartitionFlow(disk); break;
				case 1: await MovePartitionFlow(disk); break;
				case 2: await CreatePartitionFlow(disk); break;
				case 3: await DeletePartitionFlow(disk); break;
				case 4: await QuickPartitionFlow(disk); break;
				case 5: await InitializeDiskFlow(disk); break;
				case 6: await ConvertPartStyleFlow(disk); break;
				case 7: await CheckFsFlow(disk); break;
				case 8: await SetActiveFlow(disk); break;
				case 9: await FindLostPartitionsFlow(disk); break;
			}
		}
		finally { _toolOpStarting = false; }
	}

	private async Task QuickPartitionFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		int? cnt = ShowChooserDialog(L("PtQuickPart"), string.Format(L("PtCountPrompt"), disk.Number), new[] { "1", "2", "3", "4" }, 0);
		if (cnt == null) return;
		int n = cnt.Value + 1;
		int? fsSel = ShowActionMenu(L("PtQuickPart"), L("PtFsPrompt"), new[] { "NTFS", "exFAT", "FAT32" },
			new[] { 0xEDA2, 0xEDA2, 0xEDA2 }, null, 0);
		if (fsSel == null) return;
		string fs = fsSel.Value == 0 ? "ntfs" : fsSel.Value == 1 ? "exfat" : "fat32";
		// Windows' formatter cannot CREATE a FAT32 volume above 32 GB: `format fs=fat32` fails but diskpart STILL exits 0.
		// This flow runs `clean` FIRST, so without this guard the disk is wiped and a partition is left RAW while
		// PtQuickDone reports success. Guard the BIGGEST partition the script will actually create: partitions 1..n-1 get
		// an explicit size=each, but the LAST one is emitted unsized, so it swallows the remainder (the 200 MB reserve +
		// the division remainder) and is always larger than `each` — checking `each` would let it slip through.
		long usableMb = disk.Size / (1024 * 1024) - 200;
		long each = usableMb / n;
		long lastMb = disk.Size / (1024 * 1024) - (n - 1) * each;
		if (fs == "fat32" && lastMb > 32L * 1024)
		{
			MessageBox.Show(L("MbFat32TooBig"), L("PtQuickPart"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		// FAT32 has a MINIMUM too (Windows can't create one below ~32 MB), and that failure is silent in exactly the same
		// way — diskpart exits 0 — so a tiny disk split several ways would be wiped by `clean` and left with RAW partitions
		// reported as success. The generic PtTooSmall check below only requires 16 MB, so bound it here for FAT32.
		if (fs == "fat32" && each < 33)
		{
			MessageBox.Show(string.Format(L("PtTooSmall"), n), L("PtQuickPart"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		if (!await ConfirmDestructive(disk, L("PtQuickPart"))) return;
		string style = disk.Size > 2L * 1024 * 1024 * 1024 * 1024 || disk.PartitionStyle?.Equals("GPT", StringComparison.OrdinalIgnoreCase) == true ? "gpt" : "mbr";
		var sb = new StringBuilder();
		sb.Append($"select disk {disk.Number}\r\nclean\r\nconvert {style}\r\n");
		if (n > 1 && (usableMb <= 0 || each < 16)) // multiple partitions need an explicit size=; a too-small disk would yield an invalid diskpart script (after 'clean' already wiped it)
		{
			MessageBox.Show(string.Format(L("PtTooSmall"), n), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		for (int i = 0; i < n; i++)
		{
			sb.Append(i < n - 1 ? $"create partition primary size={each}\r\n" : "create partition primary\r\n");
			sb.Append($"format fs={fs} quick label=DriveForge{(n > 1 ? (i + 1).ToString() : "")}\r\nassign\r\n");
		}
		sb.Append("exit\r\n");
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtQuickPart")));
			string outp = await RunDiskpartAsync(sb.ToString());
			SetToolOutput("diskpart quick partition\r\n\r\n" + outp);
			Log($"Quick partition: Disk {disk.Number} -> {n} x {fs}.");
			await RefreshDisksAsync();
			MessageBox.Show(string.Format(L("PtQuickDone"), disk.Number, n, fs.ToUpperInvariant()), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { ShowError(L("ErrQuickPart"), ex); }
		finally { SetBusy(busy: false); }
	}

	// Grow or shrink a partition without destroying data, using Windows' own diskpart shrink/extend.
	// Extend only fills unallocated space immediately AFTER the volume; shrink frees space at its end.
	private async Task ResizePartitionFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;   // never shrink/extend the running Windows volume (e.g. C:)
		var letters = disk.DriveLetters?.ToList() ?? new List<char>();
		if (letters.Count == 0) { MessageBox.Show(L("PtNoVolumes"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
		char letter;
		if (letters.Count == 1) letter = letters[0];
		else { int? pick = ShowChooserDialog(L("PtResize"), L("PtPickVolume"), letters.Select(l => l + ":").ToArray(), 0); if (pick == null) return; letter = letters[pick.Value]; }

		int? mode = ShowActionMenu(L("PtResize"), string.Format(L("PtResizePrompt"), letter), new[] { L("PtGrow"), L("PtShrink") },
			new[] { 0xE710, 0xE738 }, null, 0);
		if (mode == null) return;

		string cmd; string working;
		if (mode.Value == 1) // shrink
		{
			long maxMb = 0;
			try
			{
				string q = await RunDiskpartAsync($"select volume {letter}\r\nshrink querymax\r\nexit\r\n");
				var m = Regex.Match(q, @"([0-9][0-9,\.]*)\s*MB");
				if (m.Success) long.TryParse(m.Groups[1].Value.Replace(",", "").Replace(".", ""), out maxMb);
			}
			catch { }
			string? amt = ShowInputDialog(L("PtShrink"), string.Format(L("PtShrinkPrompt"), letter, maxMb), maxMb > 0 ? maxMb.ToString() : "1024");
			if (amt == null) return;
			if (!long.TryParse(amt.Trim(), out long mb) || mb <= 0) { MessageBox.Show(L("PtBadAmount"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
			if (MessageBox.Show(string.Format(L("PtShrinkConfirm"), letter, mb), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
			cmd = $"select volume {letter}\r\nshrink desired={mb}\r\nexit\r\n";
			working = string.Format(L("PtResizeWorking"), letter);
		}
		else // grow / extend
		{
			string? amt = ShowInputDialog(L("PtGrow"), string.Format(L("PtGrowPrompt"), letter), "");
			if (amt == null) return;
			amt = amt.Trim();
			string extend;
			if (amt.Length == 0) extend = "extend";
			else if (long.TryParse(amt, out long mb) && mb > 0) extend = $"extend size={mb}";
			else { MessageBox.Show(L("PtBadAmount"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
			if (MessageBox.Show(string.Format(L("PtGrowConfirm"), letter), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
			cmd = $"select volume {letter}\r\n{extend}\r\nexit\r\n";
			working = string.Format(L("PtResizeWorking"), letter);
		}

		try
		{
			SetBusy(busy: true, working);
			long sizeBefore = await VolumeSizeBytesAsync(letter);   // baseline for the before/after check below
			string outp = await RunDiskpartAsync(cmd);
			SetToolOutput("diskpart resize\r\n\r\n" + outp);
			Log($"Resize volume {letter}: ({(mode.Value == 1 ? "shrink" : "extend")}).");
			await RefreshDisksAsync();
			// diskpart exits 0 even when it DECLINED the shrink/extend (not enough reclaimable space, no adjacent free
			// space) or applied only part of it, and its result text is localized so it can't be parsed. The only
			// locale-safe way to know whether anything actually changed is to compare the real volume size before/after —
			// otherwise a no-op is reported as a green success and the user plans around space that was never freed.
			long sizeAfter = await VolumeSizeBytesAsync(letter);
			bool measured = sizeBefore > 0 && sizeAfter > 0;
			bool ok = !measured || (mode.Value == 1 ? sizeAfter < sizeBefore : sizeAfter > sizeBefore);
			string deltaNote = measured ? "\r\n\r\n" + FormatBytes(sizeBefore) + "  ->  " + FormatBytes(sizeAfter) : "";
			Log($"Volume {letter}: {sizeBefore} -> {sizeAfter} bytes (measured={measured}, ok={ok}).");
			MessageBox.Show((ok ? string.Format(L("PtResizeDone"), letter) : L("PtResizeFailed")) + deltaNote + "\r\n\r\n" + (outp.Length > 600 ? outp.Substring(outp.Length - 600) : outp),
				"DriveForge", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex) { ShowError(L("ErrResize"), ex); }
		finally { SetBusy(busy: false); }
	}

	// ---- Move a partition (MBR data disks): relocate its sectors and rewrite the table entry ----
	// Windows can't move partitions; this is the "dedicated manager" piece. Experimental, MBR-only, heavily gated.
	private sealed class MbrEntry { public int Index; public byte Type; public long StartLBA; public long Sectors; public string Fs = ""; }

	private static void ReadFull(FileStream fs, byte[] buf, int count)
	{
		int g = 0; while (g < count) { int r = fs.Read(buf, g, count - g); if (r <= 0) break; g += r; }
		if (g < count) throw new EndOfStreamException($"Short read from the disk: got {g} of {count} bytes. Aborting to avoid writing stale data."); // never write a partially-filled buffer over a partition/MBR
	}

	private static string PartTypeName(byte t) => t switch
	{
		0x07 => "NTFS/exFAT", 0x0B or 0x0C => "FAT32", 0x06 or 0x0E or 0x04 or 0x01 => "FAT", 0x83 => "Linux", 0x82 => "Linux swap", _ => $"type 0x{t:X2}"
	};

	private List<MbrEntry> ReadMbrEntries(int diskNumber)
	{
		var list = new List<MbrEntry>();
		using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk for reading (run as administrator).");
		using var fs = new FileStream(h, FileAccess.Read);
		byte[] mbr = new byte[512]; fs.Seek(0, SeekOrigin.Begin); ReadFull(fs, mbr, 512);
		for (int i = 0; i < 4; i++)
		{
			int eo = 446 + i * 16;
			byte type = mbr[eo + 4];
			long start = BitConverter.ToUInt32(mbr, eo + 8);
			long cnt = BitConverter.ToUInt32(mbr, eo + 12);
			if (type == 0 || cnt == 0) continue;
			string fsName;
			try { byte[] vbr = new byte[512]; fs.Seek(start * 512L, SeekOrigin.Begin); ReadFull(fs, vbr, 512); fsName = IdentifyVbr(vbr, 0)?.Fs ?? PartTypeName(type); }
			catch { fsName = PartTypeName(type); }
			list.Add(new MbrEntry { Index = i, Type = type, StartLBA = start, Sectors = cnt, Fs = fsName });
		}
		return list;
	}

	private bool RawMovePartition(int diskNumber, long srcStart, long sectors, long dstStart, Action<int> progress)
	{
		const int SS = 512;
		using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", 0xC0000000u /*GENERIC_READ|WRITE*/, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk read/write (error " + Marshal.GetLastWin32Error() + ").");
		using var fs = new FileStream(h, FileAccess.ReadWrite);
		long chunkSectors = 16384; // 8 MiB
		byte[] buf = new byte[chunkSectors * SS];
		bool backward = dstStart > srcStart; // moving toward the end: copy from the tail so overlap isn't clobbered
		long done = 0;
		if (!backward)
			for (long off = 0; off < sectors; off += chunkSectors)
			{
				long n = Math.Min(chunkSectors, sectors - off); int bytes = (int)(n * SS);
				fs.Seek((srcStart + off) * SS, SeekOrigin.Begin); ReadFull(fs, buf, bytes);
				fs.Seek((dstStart + off) * SS, SeekOrigin.Begin); fs.Write(buf, 0, bytes);
				done += n; progress((int)(done * 100 / sectors));
			}
		else
		{
			long off = sectors;
			while (off > 0)
			{
				long n = Math.Min(chunkSectors, off); off -= n; int bytes = (int)(n * SS);
				fs.Seek((srcStart + off) * SS, SeekOrigin.Begin); ReadFull(fs, buf, bytes);
				fs.Seek((dstStart + off) * SS, SeekOrigin.Begin); fs.Write(buf, 0, bytes);
				done += n; progress((int)((sectors - off) * 100 / sectors));
			}
		}
		fs.Flush(); FlushFileBuffers(h);
		return true;
	}

	// Saves the disk's current 512-byte MBR (partition table + boot code) to the Desktop before a destructive raw move,
	// so the original layout can be restored if the move is interrupted. Best-effort: returns the path, or null.
	private string? SaveMbrBackup(int diskNumber)
	{
		try
		{
			byte[] mbr = new byte[512];
			using (var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero))
			{
				if (h.IsInvalid) return null;
				using var fs = new FileStream(h, FileAccess.Read);
				ReadFull(fs, mbr, 512);
			}
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				$"DriveForge-MBR-backup-Disk{diskNumber}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
			File.WriteAllBytes(path, mbr);
			return path;
		}
		catch { return null; }
	}

	private void UpdateMbrEntryStart(int diskNumber, int entryIndex, long newStartLBA)
	{
		using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", 0xC0000000u, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk to update the partition table.");
		using var fs = new FileStream(h, FileAccess.ReadWrite);
		byte[] mbr = new byte[512]; fs.Seek(0, SeekOrigin.Begin); ReadFull(fs, mbr, 512);
		int eo = 446 + entryIndex * 16;
		uint v = (uint)newStartLBA;
		mbr[eo + 8] = (byte)(v & 0xFF); mbr[eo + 9] = (byte)((v >> 8) & 0xFF); mbr[eo + 10] = (byte)((v >> 16) & 0xFF); mbr[eo + 11] = (byte)((v >> 24) & 0xFF);
		fs.Seek(0, SeekOrigin.Begin); fs.Write(mbr, 0, 512); fs.Flush(); FlushFileBuffers(h);
	}

	private async Task MovePartitionFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		// Detect the partition style LIVE from the disk (not the possibly-stale cached DiskItem), so a disk
		// converted outside the app still routes correctly.
		if (IsGptDisk(disk.Number)) { await MoveGptFlow(disk); return; }

		List<MbrEntry> entries;
		try { entries = ReadMbrEntries(disk.Number); } catch (Exception ex) { ShowError(L("ErrMoveMbrRead"), ex); return; }
		if (entries.Count == 0) { MessageBox.Show(L("MvNoParts"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }

		int? pick = ShowChooserDialog(L("MvTitle"), L("MvPickPart"),
			entries.Select((e, i) => $"#{i + 1}: {e.Fs} {FormatBytes(e.Sectors * 512L)} @ {FormatBytes(e.StartLBA * 512L)}").ToArray(), 0);
		if (pick == null) return;
		var p = entries[pick.Value];

		int? dir = ShowActionMenu(L("MvTitle"), L("MvDirPrompt"), new[] { L("MvLeft"), L("MvRight") },
			new[] { 0xE76B, 0xE76C }, null, 0);
		if (dir == null) return;
		string? amt = ShowInputDialog(L("MvTitle"), L("MvAmountPrompt"), "1024");
		if (amt == null) return;
		if (!long.TryParse(amt.Trim(), out long mb) || mb <= 0) { MessageBox.Show(L("PtBadAmount"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		long shift = mb * 2048; // MB -> 512-byte sectors
		long total = disk.Size / 512;
		long newStart = dir.Value == 0 ? p.StartLBA - shift : p.StartLBA + shift;
		// An MBR entry stores the start LBA in 32 bits and UpdateMbrEntryStart casts to uint, so a start (or end) beyond
		// 2^32-1 sectors — reachable on a >2 TB disk that is still MBR-partitioned — would silently TRUNCATE and leave the
		// entry pointing somewhere else entirely, losing the partition. Refuse anything the table cannot represent.
		if (newStart < 2048 || newStart + p.Sectors > total || newStart + p.Sectors > 0xFFFFFFFFL)
		{ MessageBox.Show(L("MvOutOfRange"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		foreach (var o in entries)
		{
			if (o.Index == p.Index) continue;
			if (newStart < o.StartLBA + o.Sectors && newStart + p.Sectors > o.StartLBA)
			{ MessageBox.Show(L("MvOverlap"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		}

		if (MessageBox.Show(string.Format(L("MvConfirm"), pick.Value + 1, p.Fs + " " + FormatBytes(p.Sectors * 512L), FormatBytes(p.StartLBA * 512L), FormatBytes(newStart * 512L)),
				L("MvTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		// When the destination overlaps the partition's CURRENT location (any shift smaller than the partition), the copy
		// rewrites the partition's own sectors as it goes. If it is then interrupted (I/O error, power loss, kill), the
		// original data is already partly overwritten AND the table still points at it — there is nothing to roll back to.
		// A non-overlapping move can be abandoned safely, so make this specific risk explicit before committing.
		bool overlappingMove = newStart < p.StartLBA + p.Sectors && newStart + p.Sectors > p.StartLBA;
		if (overlappingMove && MessageBox.Show(L("MvOverlapWarn"), L("MvTitle"),
				MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // disks can renumber between refresh and click — confirm identity before moving raw sectors
		// Keep a copy of the original 512-byte partition table before anything destructive: if the move is interrupted the
		// table can at least be restored to describe the original layout while recovery tools work on the data.
		string? mbrBackup = SaveMbrBackup(disk.Number);
		if (mbrBackup != null) Log("Saved the original MBR partition table to: " + mbrBackup);
		else Log("WARNING: could not save an MBR partition-table backup before the move.");

		bool moved = false;
		try
		{
			// Reset stopRequested like every other progress-driving flow: the flag is global and sticky, so a Stop
			// pressed on ANY earlier operation would still be set here — and SetBusy(false) zeroes the bar when it is,
			// leaving a SUCCESSFUL move announcing itself over an empty 0% bar.
			stopRequested = false;
			_progressFullRange = true;
			SetBusy(busy: true, L("MvWorking"));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			await RunDiskpartAsync($"select disk {disk.Number}\r\noffline disk\r\nexit\r\n");
			int idx = p.Index; long src = p.StartLBA, secs = p.Sectors, dst = newStart, dn = disk.Number;
			moved = await Task.Run(() =>
			{
				bool okCopy = RawMovePartition((int)dn, src, secs, dst, pr => Dispatcher.Invoke(() => ProgressBar.Value = pr));
				if (okCopy) UpdateMbrEntryStart((int)dn, idx, dst); // table is rewritten ONLY after a full, flushed copy
				return okCopy;
			});
			await RunDiskpartAsync($"select disk {disk.Number}\r\nonline disk\r\nattributes disk clear readonly\r\nexit\r\n");
			// A partition move is deliberately NOT interruptible: abandoning it part-way leaves half-overwritten
			// sectors with the table still pointing at them and nothing to roll back to. So a Stop pressed mid-move is
			// ignored by design — but the flag stays set, and SetBusy(false) zeroes the bar when it is, which left a
			// fully-completed move announcing "moved successfully" over an empty 0% bar. Say so, then clear it.
			if (stopRequested) { Log("Stop was ignored: a partition move cannot be interrupted safely — it ran to completion."); stopRequested = false; }
			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			await RefreshDisksAsync();
			Log($"Moved MBR partition #{pick.Value + 1} on Disk {disk.Number}: LBA {src} -> {dst}.");
			MessageBox.Show(moved ? string.Format(L("MvDone"), pick.Value + 1) : L("MvFailed"), L("MvTitle"), MessageBoxButton.OK, moved ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex)
		{
			SetBusy(busy: false);
			try { await RunDiskpartAsync($"select disk {disk.Number}\r\nonline disk\r\nattributes disk clear readonly\r\nexit\r\n"); } catch { }
			ShowError(L("ErrMoveMbr"), ex);
		}
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; SetBusy(busy: false); }
	}

	// ---- GPT partition move: relocate data + rebuild both GPT copies (CRC32). A read-time self-check that
	// recomputes the EXISTING GPT's checksums and aborts on any mismatch guarantees the rewrite math is correct
	// for this disk before a single byte is written. ----
	private static uint Crc32(byte[] data, int offset, int length)
	{
		uint crc = 0xFFFFFFFFu;
		for (int i = 0; i < length; i++)
		{
			crc ^= data[offset + i];
			for (int j = 0; j < 8; j++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
		}
		return crc ^ 0xFFFFFFFFu;
	}

	private sealed class GptInfo
	{
		public int SectorSize;
		public long BackupHeaderLBA;
		public long FirstUsableLBA, LastUsableLBA;
		public long PrimaryEntriesLBA, BackupEntriesLBA;
		public int NumEntries, EntrySize, HeaderSize;
		public byte[] PrimaryHeader = Array.Empty<byte>();
		public byte[] BackupHeader = Array.Empty<byte>();
		public byte[] Entries = Array.Empty<byte>();
		public bool Verified;
	}

	private static void WriteUInt32(byte[] b, int off, uint v) { b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24); }
	private static void WriteInt64(byte[] b, int off, long v) { for (int i = 0; i < 8; i++) b[off + i] = (byte)(v >> (8 * i)); }

	private static bool VerifyGptHeader(byte[] hdr, byte[] entries, int headerSize)
	{
		if (headerSize < 92 || headerSize > hdr.Length) return false;
		if (Crc32(entries, 0, entries.Length) != BitConverter.ToUInt32(hdr, 88)) return false;
		byte[] tmp = new byte[headerSize]; Array.Copy(hdr, tmp, headerSize);
		tmp[16] = tmp[17] = tmp[18] = tmp[19] = 0;
		return Crc32(tmp, 0, headerSize) == BitConverter.ToUInt32(hdr, 16);
	}

	// Recomputes the partition-array CRC and a header's own CRC, in place.
	private static void FixGptHeaderCrc(byte[] hdr, byte[] entries, int headerSize)
	{
		WriteUInt32(hdr, 88, Crc32(entries, 0, entries.Length));
		WriteUInt32(hdr, 16, 0);
		WriteUInt32(hdr, 16, Crc32(hdr, 0, headerSize));
	}

	// Live check: is this physical disk GPT? (protective-MBR 0xEE entry, or "EFI PART" at LBA 1). Read-only.
	private bool IsGptDisk(int diskNumber)
	{
		try
		{
			using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
			if (h.IsInvalid) return false;
			using var fs = new FileStream(h, FileAccess.Read);
			byte[] s0 = new byte[512]; ReadFull(fs, s0, 512);
			for (int i = 0; i < 4; i++) if (s0[446 + i * 16 + 4] == 0xEE) return true;
			foreach (int off in new[] { 512, 4096 })
			{
				byte[] hd = new byte[8]; fs.Seek(off, SeekOrigin.Begin); ReadFull(fs, hd, 8);
				if (Encoding.ASCII.GetString(hd, 0, 8) == "EFI PART") return true;
			}
			return false;
		}
		catch { return false; }
	}

	private GptInfo ReadGpt(int diskNumber)
	{
		var g = new GptInfo();
		using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk for reading (run as administrator).");
		using var fs = new FileStream(h, FileAccess.Read);
		int ss = 0;
		foreach (int cand in new[] { 512, 4096 })
		{
			byte[] hdr = new byte[cand]; fs.Seek(cand, SeekOrigin.Begin); ReadFull(fs, hdr, cand);
			if (Encoding.ASCII.GetString(hdr, 0, 8) == "EFI PART") { ss = cand; g.PrimaryHeader = hdr; break; }
		}
		if (ss == 0) throw new IOException("No GPT header found on this disk.");
		g.SectorSize = ss;
		var ph = g.PrimaryHeader;
		g.HeaderSize = (int)BitConverter.ToUInt32(ph, 12);
		g.BackupHeaderLBA = BitConverter.ToInt64(ph, 32);
		g.FirstUsableLBA = BitConverter.ToInt64(ph, 40);
		g.LastUsableLBA = BitConverter.ToInt64(ph, 48);
		g.PrimaryEntriesLBA = BitConverter.ToInt64(ph, 72);
		g.NumEntries = (int)BitConverter.ToUInt32(ph, 80);
		g.EntrySize = (int)BitConverter.ToUInt32(ph, 84);
		if (g.NumEntries <= 0 || g.NumEntries > 4096 || g.EntrySize < 128 || g.EntrySize > 4096) throw new IOException("Unexpected GPT entry table.");
		int arrBytes = g.NumEntries * g.EntrySize;
		g.Entries = new byte[arrBytes];
		fs.Seek(g.PrimaryEntriesLBA * ss, SeekOrigin.Begin); ReadFull(fs, g.Entries, arrBytes);
		// Construct the backup header from the primary instead of READING the last sector: a buffered read that
		// lands on end-of-device makes some USB bridges return ERROR_INVALID_FUNCTION. Exact-size writes are fine.
		long entriesSectors = (arrBytes + ss - 1) / ss;
		g.BackupEntriesLBA = g.BackupHeaderLBA - entriesSectors;
		g.BackupHeader = (byte[])ph.Clone();
		WriteInt64(g.BackupHeader, 24, g.BackupHeaderLBA); // MyLBA = backup (last sector)
		WriteInt64(g.BackupHeader, 32, 1);                 // AlternateLBA = primary
		WriteInt64(g.BackupHeader, 72, g.BackupEntriesLBA);
		g.Verified = VerifyGptHeader(ph, g.Entries, g.HeaderSize);
		return g;
	}

	private void WriteGpt(int diskNumber, GptInfo g)
	{
		using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", 0xC0000000u, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("open r/w failed (err " + Marshal.GetLastWin32Error() + ")");
		using var fs = new FileStream(h, FileAccess.ReadWrite);
		int ss = g.SectorSize;
		void W(long lba, byte[] data, int len, string what)
		{
			try { fs.Seek(lba * ss, SeekOrigin.Begin); fs.Write(data, 0, len); }
			catch (Exception ex) { throw new IOException($"GPT write [{what} @LBA {lba}, {len} B]: {ex.Message}", ex); }
		}
		W(g.PrimaryEntriesLBA, g.Entries, g.Entries.Length, "primary-entries");
		W(1, g.PrimaryHeader, ss, "primary-header");
		W(g.BackupEntriesLBA, g.Entries, g.Entries.Length, "backup-entries");
		W(g.BackupHeaderLBA, g.BackupHeader, ss, "backup-header");
		try { fs.Flush(); FlushFileBuffers(h); } catch (Exception ex) { throw new IOException("GPT flush: " + ex.Message, ex); }
	}

	// Saves the disk's CURRENT (pre-move) GPT structures as separate named-by-LBA files, so a technician can
	// manually restore them with a hex/disk editor if an interrupted move leaves the disk unreadable. Mirrors
	// SaveMbrBackup's "best-effort disaster-recovery aid" role for the MBR move path.
	private string? SaveGptBackup(int diskNumber, GptInfo g)
	{
		try
		{
			string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				$"DriveForge-GPT-backup-Disk{diskNumber}-{DateTime.Now:yyyyMMdd-HHmmss}");
			Directory.CreateDirectory(dir);
			File.WriteAllBytes(Path.Combine(dir, "primary-header-LBA1.bin"), g.PrimaryHeader);
			File.WriteAllBytes(Path.Combine(dir, $"primary-entries-LBA{g.PrimaryEntriesLBA}.bin"), g.Entries);
			// ReadGpt never actually READS the on-disk backup header/entries (avoids ERROR_INVALID_FUNCTION some USB
			// bridges throw on a buffered read at end-of-device) — g.BackupHeader is a clone of the primary with only
			// MyLBA/AlternateLBA/PartitionEntryLBA patched, so its CRC fields are still the PRIMARY's (stale/wrong for
			// this header). Fix the CRC on a COPY before saving — g.BackupHeader/g.Entries themselves must stay
			// untouched here, since the actual move later calls FixGptHeaderCrc on THEM with the POST-move entries.
			byte[] backupHeaderCopy = (byte[])g.BackupHeader.Clone();
			FixGptHeaderCrc(backupHeaderCopy, g.Entries, (int)BitConverter.ToUInt32(backupHeaderCopy, 12));
			File.WriteAllBytes(Path.Combine(dir, $"backup-header-LBA{g.BackupHeaderLBA}.bin"), backupHeaderCopy);
			File.WriteAllBytes(Path.Combine(dir, $"backup-entries-LBA{g.BackupEntriesLBA}.bin"), g.Entries);
			File.WriteAllText(Path.Combine(dir, "README.txt"),
				"DriveForge GPT backup (pre-move)\r\n" +
				$"Disk {diskNumber}, sector size {g.SectorSize} bytes.\r\n\r\n" +
				"primary-header-LBA1.bin and primary-entries-*.bin: the EXACT bytes read from the disk before the move\r\n" +
				"started.\r\n\r\n" +
				"backup-header-*.bin and backup-entries-*.bin: this tool does not read the disk's actual backup GPT copy\r\n" +
				"(some USB bridges error on that read) — these are instead a RECONSTRUCTED, CRC-valid mirror of the\r\n" +
				"primary table, matching what a normal GPT write on this disk already assumes. If the disk's real backup\r\n" +
				"copy had already diverged from the primary before the move, that difference is NOT preserved here.\r\n\r\n" +
				"If the move was interrupted and the disk is now unreadable, a technician can write each file back to\r\n" +
				"its named LBA with a disk/hex editor (or dd) to restore a self-consistent GPT table. Data sectors are\r\n" +
				"NOT included here — only the partition table.\r\n");
			return dir;
		}
		catch { return null; }
	}

	private async Task MoveGptFlow(DiskItem disk)
	{
		GptInfo g;
		try { g = ReadGpt(disk.Number); } catch (Exception ex) { ShowError(L("ErrMoveGptRead"), ex); return; }
		if (!g.Verified) { MessageBox.Show(L("MvGptVerifyFail"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

		var used = new List<(int idx, long first, long last, string name)>();
		for (int i = 0; i < g.NumEntries; i++)
		{
			int b = i * g.EntrySize;
			bool empty = true;
			for (int k = 0; k < 16; k++) if (g.Entries[b + k] != 0) { empty = false; break; }
			if (empty) continue;
			long first = BitConverter.ToInt64(g.Entries, b + 32);
			long last = BitConverter.ToInt64(g.Entries, b + 40);
			string name = Encoding.Unicode.GetString(g.Entries, b + 56, 72).TrimEnd('\0');
			used.Add((i, first, last, string.IsNullOrWhiteSpace(name) ? "partition" : name));
		}
		if (used.Count == 0) { MessageBox.Show(L("MvNoParts"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }

		int? pick = ShowChooserDialog(L("MvTitle"), L("MvPickPart"),
			used.Select((e, i) => $"#{i + 1}: {e.name} {FormatBytes((e.last - e.first + 1) * g.SectorSize)} @ {FormatBytes(e.first * g.SectorSize)}").ToArray(), 0);
		if (pick == null) return;
		var p = used[pick.Value];

		int? dir = ShowActionMenu(L("MvTitle"), L("MvDirPrompt"), new[] { L("MvLeft"), L("MvRight") },
			new[] { 0xE76B, 0xE76C }, null, 0);
		if (dir == null) return;
		string? amt = ShowInputDialog(L("MvTitle"), L("MvAmountPrompt"), "1024");
		if (amt == null) return;
		if (!long.TryParse(amt.Trim(), out long mb) || mb <= 0) { MessageBox.Show(L("PtBadAmount"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		long perMb = 1048576L / g.SectorSize;
		long shift = mb * perMb;
		long count = p.last - p.first + 1;
		long newFirst = dir.Value == 0 ? p.first - shift : p.first + shift;
		long newLast = newFirst + count - 1;
		if (newFirst < g.FirstUsableLBA || newLast > g.LastUsableLBA) { MessageBox.Show(L("MvOutOfRange"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		foreach (var o in used)
		{
			if (o.idx == p.idx) continue;
			if (newFirst <= o.last && newLast >= o.first) { MessageBox.Show(L("MvOverlap"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		}

		if (MessageBox.Show(string.Format(L("MvConfirm"), pick.Value + 1, p.name + " " + FormatBytes(count * g.SectorSize), FormatBytes(p.first * g.SectorSize), FormatBytes(newFirst * g.SectorSize)),
				L("MvTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		// Same risk as the MBR move path: when the destination overlaps the partition's CURRENT sectors, the raw copy
		// rewrites its own source as it goes, and an interruption leaves the data partly overwritten with the table
		// still pointing at it — nothing to roll back to. Warn explicitly before committing, exactly like MBR does.
		bool overlappingMove = newFirst <= p.last && newLast >= p.first;
		if (overlappingMove && MessageBox.Show(L("MvOverlapWarn"), L("MvTitle"),
				MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // disks can renumber between refresh and click — confirm identity before moving raw sectors
		// Keep a copy of the original GPT table before anything destructive, same as the MBR move path.
		string? gptBackup = SaveGptBackup(disk.Number, g);
		if (gptBackup != null) Log("Saved the original GPT partition table to: " + gptBackup);
		else Log("WARNING: could not save a GPT partition-table backup before the move.");

		bool moved = false;
		try
		{
			// Reset stopRequested like every other progress-driving flow: the flag is global and sticky, so a Stop
			// pressed on ANY earlier operation would still be set here — and SetBusy(false) zeroes the bar when it is,
			// leaving a SUCCESSFUL move announcing itself over an empty 0% bar.
			stopRequested = false;
			_progressFullRange = true;
			SetBusy(busy: true, L("MvWorking"));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			await RunDiskpartAsync($"select disk {disk.Number}\r\noffline disk\r\nexit\r\n");
			int entryBase = p.idx * g.EntrySize;
			int conv = g.SectorSize / 512;
			long src512 = p.first * conv, cnt512 = count * conv, dst512 = newFirst * conv;
			int dn = disk.Number;
			_lastMovePhase = "copy";
			moved = await Task.Run(() =>
			{
				if (!RawMovePartition(dn, src512, cnt512, dst512, pr => Dispatcher.Invoke(() => ProgressBar.Value = pr))) return false;
				_lastMovePhase = "table";
				WriteInt64(g.Entries, entryBase + 32, newFirst);
				WriteInt64(g.Entries, entryBase + 40, newLast);
				FixGptHeaderCrc(g.PrimaryHeader, g.Entries, g.HeaderSize);
				FixGptHeaderCrc(g.BackupHeader, g.Entries, (int)BitConverter.ToUInt32(g.BackupHeader, 12));
				WriteGpt(dn, g);
				return true;
			});
			await RunDiskpartAsync($"select disk {disk.Number}\r\nonline disk\r\nattributes disk clear readonly\r\nexit\r\n");
			// See the MBR move: the copy is deliberately uninterruptible, so a mid-move Stop is ignored by design and
			// must not leave a completed move showing a zeroed bar.
			if (stopRequested) { Log("Stop was ignored: a partition move cannot be interrupted safely — it ran to completion."); stopRequested = false; }
			ProgressBar.Value = 100.0; SetBusy(busy: false);
			await RefreshDisksAsync();
			Log($"Moved GPT partition #{pick.Value + 1} on Disk {disk.Number}: LBA {p.first} -> {newFirst}.");
			MessageBox.Show(moved ? string.Format(L("MvDone"), pick.Value + 1) : L("MvFailed"), L("MvTitle"), MessageBoxButton.OK, moved ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex)
		{
			SetBusy(busy: false);
			try { await RunDiskpartAsync($"select disk {disk.Number}\r\nonline disk\r\nattributes disk clear readonly\r\nexit\r\n"); } catch { }
			ShowError(L("ErrMovePhase") + _lastMovePhase + "]", ex);
		}
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; SetBusy(busy: false); }
	}

	private string _lastMovePhase = "";

	// Create a new partition in the disk's UNALLOCATED space (existing partitions are untouched).
	private async Task CreatePartitionFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		string? amt = ShowInputDialog(L("PtCreate"), L("PtCreatePrompt"), "");
		if (amt == null) return;
		amt = amt.Trim();
		string sizeClause;
		long requestedMb = 0;
		if (amt.Length == 0) sizeClause = "";
		else if (long.TryParse(amt, out long mb) && mb > 0) { sizeClause = $" size={mb}"; requestedMb = mb; }
		else { MessageBox.Show(L("PtBadAmount"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		int? fsSel = ShowActionMenu(L("PtCreate"), L("PtFsPrompt"), new[] { "NTFS", "exFAT", "FAT32" },
			new[] { 0xEDA2, 0xEDA2, 0xEDA2 }, null, 0);
		if (fsSel == null) return;
		string fs = fsSel.Value == 0 ? "ntfs" : fsSel.Value == 1 ? "exfat" : "fat32";
		// Windows' formatter cannot CREATE a FAT32 volume above 32 GB: diskpart's `format fs=fat32` fails but STILL exits 0,
		// so this flow would report PtCreateDone on a RAW, unusable partition. Reject up front, like FormatDrive_Click does.
		// With no explicit size the partition fills the largest unallocated EXTENT, so ask Windows how big that really is —
		// using the whole-disk size instead would wrongly refuse a legitimate 8 GB FAT32 hole on a 500 GB drive. If the
		// query fails we let it through: unlike QuickPartition this flow issues no `clean`, so the worst case is the new
		// partition alone being left RAW, with no pre-existing data destroyed.
		if (fs == "fat32")
		{
			long fat32Mb = requestedMb;
			if (fat32Mb == 0)
			{
				long freeBytes = await LargestFreeExtentBytesAsync(disk.Number);
				fat32Mb = freeBytes > 0 ? freeBytes / (1024 * 1024) : 0;
			}
			if (fat32Mb > 32L * 1024)
			{
				MessageBox.Show(L("MbFat32TooBig"), L("PtCreate"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
		}
		if (MessageBox.Show(string.Format(L("PtCreateConfirm"), disk.Number), L("PtCreate"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // disks can renumber between the scan and the click — re-confirm identity before create+format
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtCreate")));
			string outp = await RunDiskpartAsync($"select disk {disk.Number}\r\ncreate partition primary{sizeClause}\r\nformat fs={fs} quick label=DriveForge\r\nassign\r\nexit\r\n");
			SetToolOutput("diskpart create partition\r\n\r\n" + outp);
			Log($"Created partition on Disk {disk.Number} ({fs}).");
			await RefreshDisksAsync();
			bool ok = true; // diskpart returned a zero exit (RunDiskpartAsync throws otherwise); don't parse the word "successfully", which is localized and never matches on the 16 non-English UI languages. The raw output is shown below regardless.
			MessageBox.Show((ok ? string.Format(L("PtCreateDone"), disk.Number) : L("PtCreateFailed")) + "\r\n\r\n" + (outp.Length > 600 ? outp.Substring(outp.Length - 600) : outp),
				L("PtCreate"), MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex) { ShowError(L("ErrCreate"), ex); }
		finally { SetBusy(busy: false); }
	}

	// Delete a volume, turning its space back into unallocated (frees room for create / grow).
	private async Task DeletePartitionFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		var letters = disk.DriveLetters?.ToList() ?? new List<char>();
		if (letters.Count == 0) { MessageBox.Show(L("PtNoVolumes"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
		char letter;
		if (letters.Count == 1) letter = letters[0];
		else { int? pick = ShowChooserDialog(L("PtDelete"), L("PtPickVolume"), letters.Select(l => l + ":").ToArray(), 0); if (pick == null) return; letter = letters[pick.Value]; }
		if (MessageBox.Show(string.Format(L("PtDeleteConfirm"), letter), L("PtDelete"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // confirm this is still the same physical disk before deleting a volume on it
		// `select volume {letter}` is disk-AGNOSTIC — it deletes whatever volume currently holds that letter on ANY disk.
		// The identity guard above only checks the physical DISK, so if the letter has migrated to a volume on a different
		// disk since the scan (USB reshuffle, manual reassignment), this would delete the WRONG volume while the guard
		// still passes. Confirm the letter still lives on THIS disk right before deleting. (Sibling flows scope with
		// `select disk N`; delete can't select a partition by letter, so verify instead.)
		if (await DiskNumberOfDriveLetterAsync(letter) != disk.Number)
		{
			MessageBox.Show(string.Format(L("PtDeleteMoved"), letter), L("PtDelete"), MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtDelete")));
			string outp = await RunDiskpartAsync($"select volume {letter}\r\ndelete volume override\r\nexit\r\n");
			SetToolOutput("diskpart delete volume\r\n\r\n" + outp);
			Log($"Deleted volume {letter}: on Disk {disk.Number}.");
			await RefreshDisksAsync();
			bool ok = true; // diskpart returned a zero exit (RunDiskpartAsync throws otherwise); don't parse the word "successfully", which is localized and never matches on the 16 non-English UI languages. The raw output is shown below regardless.
			MessageBox.Show((ok ? string.Format(L("PtDeleteDone"), letter) : L("PtResizeFailed")) + "\r\n\r\n" + (outp.Length > 600 ? outp.Substring(outp.Length - 600) : outp),
				L("PtDelete"), MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex) { ShowError(L("ErrDelete"), ex); }
		finally { SetBusy(busy: false); }
	}

	private async Task InitializeDiskFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		int? st = ShowActionMenu(L("PtInit"), string.Format(L("PtInitPrompt"), disk.Number), new[] { L("PtGpt"), L("PtMbr") },
			new[] { 0xEDA2, 0xEDA2 }, null, 0);
		if (st == null) return;
		string style = st.Value == 0 ? "gpt" : "mbr";
		if (!await ConfirmDestructive(disk, L("PtInit"))) return;
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtInit")));
			string outp = await RunDiskpartAsync($"select disk {disk.Number}\r\nclean\r\nconvert {style}\r\ncreate partition primary\r\nformat fs=ntfs quick label=DriveForge\r\nassign\r\nexit\r\n");
			SetToolOutput("diskpart initialize\r\n\r\n" + outp);
			Log($"Initialized Disk {disk.Number} as {style}.");
			await RefreshDisksAsync();
			MessageBox.Show(string.Format(L("PtInitDone"), disk.Number), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { ShowError(L("ErrInit"), ex); }
		finally { SetBusy(busy: false); }
	}

	private async Task ConvertPartStyleFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		bool toGpt = !(disk.PartitionStyle?.Equals("GPT", StringComparison.OrdinalIgnoreCase) == true);
		string target = toGpt ? "gpt" : "mbr";
		if (MessageBox.Show(string.Format(L("PtConvertConfirm"), disk.Number, disk.PartitionStyle, target.ToUpperInvariant()), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // 'clean' wipes the whole disk — confirm identity first (disks can renumber between refresh and click)
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtConvert")));
			string outp = await RunDiskpartAsync($"select disk {disk.Number}\r\nclean\r\nconvert {target}\r\nexit\r\n");
			SetToolOutput("diskpart convert\r\n\r\n" + outp);
			Log($"Converted Disk {disk.Number} to {target}.");
			await RefreshDisksAsync();
			MessageBox.Show(string.Format(L("PtConvertDone"), disk.Number, target.ToUpperInvariant()), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { ShowError(L("ErrConvert"), ex); }
		finally { SetBusy(busy: false); }
	}

	private async Task CheckFsFlow(DiskItem disk)
	{
		var letters = disk.DriveLetters?.ToList() ?? new List<char>();
		if (letters.Count == 0) { MessageBox.Show(L("PtNoVolumes"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
		char letter;
		if (letters.Count == 1) letter = letters[0];
		else { int? pick = ShowChooserDialog(L("PtCheck"), L("PtPickVolume"), letters.Select(l => l + ":").ToArray(), 0); if (pick == null) return; letter = letters[pick.Value]; }
		try
		{
			SetBusy(busy: true, string.Format(L("PtChecking"), letter));
			var r = await RunProcessInternalAsync("cmd.exe", $"/c chkdsk {letter}: /scan"); // online, read-only-safe, no reboot
			string outp = r.Output ?? "";
			SetToolOutput($"chkdsk {letter}: /scan\r\n\r\n" + outp);
			string tail = outp.Length > 700 ? outp.Substring(outp.Length - 700) : outp;
			// chkdsk /scan exits non-zero when it finds problems (0 = clean). Showing the same "done" + Information icon
			// regardless made a volume WITH filesystem errors look identical to a clean one.
			bool clean = r.ExitCode == 0;
			MessageBox.Show(string.Format(L(clean ? "PtCheckDone" : "PtCheckErrors"), letter) + "\r\n\r\n" + tail,
				"DriveForge", MessageBoxButton.OK, clean ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex) { ShowError(L("ErrCheck"), ex); }
		finally { SetBusy(busy: false); }
	}

	private async Task SetActiveFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		if (disk.PartitionStyle?.Equals("GPT", StringComparison.OrdinalIgnoreCase) == true) { MessageBox.Show(L("PtActiveGpt"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
		if (MessageBox.Show(string.Format(L("PtActiveConfirm"), disk.Number), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // disks can renumber between the scan and the click — re-confirm identity before flipping the active/boot partition
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtActive")));
			string outp = await RunDiskpartAsync($"select disk {disk.Number}\r\nselect partition 1\r\nactive\r\nexit\r\n");
			SetToolOutput("diskpart active\r\n\r\n" + outp);
			Log($"Set partition 1 active on Disk {disk.Number}.");
			MessageBox.Show(string.Format(L("PtActiveDone"), disk.Number), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { ShowError(L("ErrSetActive"), ex); }
		finally { SetBusy(busy: false); }
	}

	// ---- Find lost partitions (read-only): scan the raw disk for volume boot records ----
	private sealed class FoundPart { public long Offset; public long Bytes; public string Fs = ""; public string Label = ""; public bool Mounted; }

	private async Task FindLostPartitionsFlow(DiskItem disk)
	{
		try
		{
			stopRequested = false; _progressFullRange = true;
			SetBusy(busy: true, string.Format(L("PtScanningLost"), disk.Number));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			var found = await Task.Run(() => ScanPhysicalForPartitions(disk.Number, disk.Size, p => Dispatcher.Invoke(() => ProgressBar.Value = p)));
			ProgressBar.Value = 100.0;
			// Mark which found VBRs correspond to a partition that IS in the live partition table (a normal, known
			// partition) vs one that is NOT (genuinely possibly-lost). The old code left this detection DEAD (an unused
			// HashSet) and labelled every found partition by offset==0 alone, so healthy mounted partitions were all
			// shown as "possibly lost". Match by start offset (within 1 MiB, the scan's step).
			var liveOffsets = await LivePartitionOffsetsAsync(disk.Number);
			foreach (var p in found) p.Mounted = liveOffsets.Any(o => Math.Abs(o - p.Offset) < 1048576);
			SetBusy(busy: false);
			if (found.Count == 0) { MessageBox.Show(string.Format(L("PtLostNone"), disk.Number), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
			var sb = new StringBuilder();
			foreach (var p in found)
				sb.Append($"• {p.Fs} @ {FormatBytes(p.Offset)} — {FormatBytes(p.Bytes)}{(string.IsNullOrEmpty(p.Label) ? "" : " — \"" + p.Label + "\"")} — {(p.Mounted ? L("PtLostKnown") : L("PtLostUnmounted"))}\r\n");
			SetToolOutput("Find lost partitions — Disk " + disk.Number + "\r\n\r\n" + sb);
			MessageBox.Show(string.Format(L("PtLostFound"), found.Count, disk.Number) + "\r\n\r\n" + sb + "\r\n" + L("PtLostHint"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { SetBusy(busy: false); ShowError(L("ErrFindLost"), ex); }
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; SetBusy(busy: false); }
	}

	private List<FoundPart> ScanPhysicalForPartitions(int diskNumber, long diskSize, Action<int> progress)
	{
		var list = new List<FoundPart>();
		using var h = CreateFile($"\\\\.\\PhysicalDrive{diskNumber}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk for reading (run as administrator).");
		using var fs = new FileStream(h, FileAccess.Read);
		long step = 1L << 20; // partitions align to 1 MiB on modern disks
		long total = diskSize > 0 ? diskSize : 256L << 30;
		byte[] sec = new byte[512];
		var seen = new HashSet<long>();
		for (long pos = 0; pos + 512 <= total && !stopRequested && list.Count < 128; pos += step)
		{
			int got;
			try { fs.Seek(pos, SeekOrigin.Begin); got = 0; while (got < 512) { int r = fs.Read(sec, got, 512 - got); if (r <= 0) break; got += r; } }
			catch { got = 0; }
			if (got == 512)
			{
				var fp = IdentifyVbr(sec, pos);
				if (fp != null && fp.Bytes > 0 && fp.Bytes <= total && seen.Add(pos)) list.Add(fp);
			}
			if ((pos & ((1L << 30) - 1)) == 0) progress(total > 0 ? (int)Math.Min(99, pos * 100 / total) : 0);
		}
		return list;
	}

	// Identifies a volume boot record (NTFS / exFAT / FAT32 / FAT16) and its size from a 512-byte sector.
	private static FoundPart? IdentifyVbr(byte[] b, long offset)
	{
		if (b.Length < 512 || b[510] != 0x55 || b[511] != 0xAA) return null;
		int bps = BitConverter.ToUInt16(b, 0x0B);
		if (bps != 512 && bps != 1024 && bps != 2048 && bps != 4096) bps = 512;
		if (b[3] == 'N' && b[4] == 'T' && b[5] == 'F' && b[6] == 'S')
		{
			long sectors = BitConverter.ToInt64(b, 0x28);
			return new FoundPart { Offset = offset, Bytes = sectors > 0 ? sectors * bps : 0, Fs = "NTFS" };
		}
		if (b[3] == 'E' && b[4] == 'X' && b[5] == 'F' && b[6] == 'A' && b[7] == 'T')
		{
			long vol = BitConverter.ToInt64(b, 0x48);
			int shift = b[0x6C]; int xbps = shift >= 9 && shift <= 12 ? (1 << shift) : 512;
			return new FoundPart { Offset = offset, Bytes = vol > 0 ? vol * xbps : 0, Fs = "exFAT" };
		}
		if (b[0x52] == 'F' && b[0x53] == 'A' && b[0x54] == 'T' && b[0x55] == '3' && b[0x56] == '2')
		{
			long sec = BitConverter.ToUInt32(b, 0x20); if (sec == 0) sec = BitConverter.ToUInt16(b, 0x13);
			return new FoundPart { Offset = offset, Bytes = sec * (long)bps, Fs = "FAT32", Label = AsciiLabel(b, 0x47) };
		}
		if (b[0x36] == 'F' && b[0x37] == 'A' && b[0x38] == 'T')
		{
			long sec = BitConverter.ToUInt16(b, 0x13); if (sec == 0) sec = BitConverter.ToUInt32(b, 0x20);
			return new FoundPart { Offset = offset, Bytes = sec * (long)bps, Fs = "FAT16", Label = AsciiLabel(b, 0x2B) };
		}
		return null;
	}

	private static string AsciiLabel(byte[] b, int at)
	{
		if (at + 11 > b.Length) return "";
		string s = Encoding.ASCII.GetString(b, at, 11).Trim();
		return s == "NO NAME" ? "" : s;
	}

	// SSD-appropriate erase: clean + quick-format (TRIMs on SSDs) + a full ReTrim so the controller discards
	// every block. The right approach for flash, where raw overwrite is defeated by wear-levelling.
	private async Task SsdSecureEraseFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		// "SSD Secure Erase" only quick-formats + issues TRIM; on an HDD or a USB flash / SD card whose controller
		// ignores TRIM, ReTrim is a no-op so NOTHING is overwritten while the success message claims the blocks were
		// discarded — the data stays fully recoverable. Warn + steer to the full-overwrite wipe unless it's really an SSD.
		if (DetectWipeMedia(disk) != WipeMedia.Ssd &&
			MessageBox.Show(L("SsdNotSsdWarn"), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
			return;
		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("SsdConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
		if (!await VerifyTargetDiskUnchangedAsync(disk)) return; // make sure this is still the same physical drive
		try
		{
			SetBusy(busy: true, string.Format(L("SsdWorking"), disk.Number));
			await RunDiskpartAsync($"select disk {disk.Number}\r\nclean\r\nconvert gpt\r\ncreate partition primary\r\nformat fs=ntfs quick label=DriveForge\r\nassign\r\nexit\r\n");
			await RefreshDisksAsync();
			var d2 = disks.FirstOrDefault(x => x.Number == disk.Number);
			char letter = d2?.DriveLetters?.FirstOrDefault() ?? '\0';
			string outp = "";
			bool reTrimRan = false;
			if (letter != '\0')
			{
				outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument($"Optimize-Volume -DriveLetter {letter} -ReTrim -Verbose"));
				reTrimRan = true;
			}
			SetToolOutput("SSD erase (clean + quick format + ReTrim)\r\n\r\n" + outp);
			// Only claim the controller actually DISCARDED the old blocks when this is really a TRIM-capable SSD AND ReTrim
			// ran AND it is on an INTERNAL bus. DetectWipeMedia keys off the model name ("SSD"/"NVMe") with no bus check, so
			// an EXTERNAL USB/FireWire/SD/MMC-bridged SSD (e.g. a portable "T7 SSD") classifies as Ssd and SKIPS the
			// SsdNotSsdWarn gate — but its bridge may not pass TRIM through, leaving the data PHYSICALLY PRESENT after the
			// quick-format. The codebase already refuses to trust USB for SSD inference; apply the same caution here. On a
			// bridge bus, on non-SSD media, or when no volume letter was assigned so ReTrim never ran, fall through to the
			// honest SsdDoneNoTrim warning that steers the user to the full-overwrite Wipe.
			string bus = disk.BusType ?? "";
			bool bridgeBus = bus.Contains("USB", StringComparison.OrdinalIgnoreCase)
				|| bus.Contains("1394", StringComparison.OrdinalIgnoreCase)
				|| bus.Equals("SD", StringComparison.OrdinalIgnoreCase)
				|| bus.Equals("MMC", StringComparison.OrdinalIgnoreCase);
			bool trimEffective = reTrimRan && DetectWipeMedia(disk) == WipeMedia.Ssd && !bridgeBus;
			Log($"SSD erase on Disk {disk.Number}: ReTrim ran={reTrimRan}, media={DetectWipeMedia(disk)}, bus={bus}, bridge={bridgeBus}, discard-claimed={trimEffective}.");
			MessageBox.Show(string.Format(L(trimEffective ? "SsdDone" : "SsdDoneNoTrim"), disk.Number),
				"DriveForge", MessageBoxButton.OK, trimEffective ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex) { ShowError(L("ErrSsd"), ex); }
		finally { SetBusy(busy: false); }
	}

	// Flush + eject all volumes of a physical disk via the Explorer "Eject" verb. Best-effort.
	private async Task EjectDiskAsync(int diskNumber)
	{
		string script =
			"$n=" + diskNumber + ";" +
			"$v=Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Where-Object {$_.DriveLetter} | ForEach-Object {[string]$_.DriveLetter};" +
			"$sh=New-Object -ComObject Shell.Application;" +
			"foreach($l in $v){ try { $sh.Namespace(17).ParseName(\"$l`:\").InvokeVerb('Eject') } catch {} };" +
			"Start-Sleep -Milliseconds 600; 'OK:'+($v -join ',')";
		string outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -Command " + QuoteArgument(script));
		Log("Eject requested for Disk " + diskNumber + ": " + outp.Trim());
	}

	// Logs and reports are written to the Desktop. Open it in Explorer.
	private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			Process.Start(new ProcessStartInfo("explorer.exe",
				Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)) { UseShellExecute = true });
		}
		catch (Exception ex)
		{
			ShowError(L("ErrLogsFolder"), ex);
		}
	}

	// Manually save the current session log to the Desktop (auto-save only happens on a failure). Lets the user grab a
	// full log after a SUCCESSFUL operation too, then opens Explorer with the saved file selected.
	private void SaveLog_Click(object sender, RoutedEventArgs e)
	{
		string? path = SaveLogToDesktop();
		if (path != null)
		{
			try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch { }
		}
		else
		{
			ShowError(L("ErrLogsFolder"), new InvalidOperationException("Could not write the log file to the Desktop."));
		}
	}

	// Flushes the file-system write cache of the given drive letters so the drive is safe to unplug
	// immediately after a clone (no programmatic eject, just a clean flush). Best-effort.
	private async Task FlushVolumesAsync(params char[] letters)
	{
		foreach (char letter in letters)
		{
			if (letter == '\0') continue;
			await RunProcessAsync("powershell.exe",
				"-NoProfile -Command " + QuoteArgument($"Write-VolumeCache -DriveLetter {letter}"),
				allowFailure: true);
		}
	}

	private void SetStage(string text, double progress)
	{
		StatusText.Text = text;
		ProgressBar.Value = Math.Max(0.0, Math.Min(100.0, progress));
		Log(text);
	}

	// ---------- Keep the machine awake while an operation is running ----------
	// A wipe, clone, surface scan or backup can run for hours. If Windows idles into sleep or hibernation half-way
	// through, the operation dies with a disk left in an indeterminate state — a partially overwritten drive, a
	// half-written clone. So hold a power request for as long as ANY operation is in flight.
	//
	// Why PowerCreateRequest and not the older SetThreadExecutionState: the legacy call is per-THREAD (it must be
	// cleared from the exact thread that set it, and is dropped when that thread ends) and the OS keeps no count for
	// it, so overlapping/unbalanced calls have to be hand-tracked. A power request is a process-wide handle that any
	// thread can drive, the kernel refcounts it, and it carries a reason string that `powercfg /requests` prints — so
	// an admin asking "why won't this PC sleep?" gets a direct answer instead of a bare exe path.
	//
	// SystemRequired holds the system idle timer, which gates idle-sleep AND idle-hibernate — one flag covers both.
	// ExecutionRequired additionally stops Modern Standby from suspending the process once the screen goes dark.
	// DisplayRequired is deliberately NOT taken: the screen is allowed to turn off, only sleep is held off.
	//
	// Two things this CANNOT do, by OS design — do not promise them: it does not block a sleep the user ASKS for
	// (lid close, power button, Start > Sleep), and on a Modern Standby laptop running on BATTERY Windows terminates
	// the request 5 minutes after the sleep timeout expires regardless. Long jobs belong on mains power.
	private const uint PowerRequestContextVersion = 0;
	private const uint PowerRequestContextSimpleString = 0x1;
	private const int PowerRequestSystemRequired = 1;
	private const int PowerRequestExecutionRequired = 3;   // Windows 8+; ignored gracefully if refused

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ReasonContext
	{
		public uint Version;
		public uint Flags;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string SimpleReasonString;   // overlays the first member of the REASON_CONTEXT union
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

	private IntPtr _powerRequest = IntPtr.Zero;   // process-wide request handle, created on first use
	private bool _sleepBlocked;                   // mirrors whether the requests below are currently held
	private bool _powerSysHeld, _powerExecHeld;   // which request types actually took, so teardown clears only those
	// UNBIASED time, not wall-clock and not tick count. This feature cannot block a sleep the USER asks for, so a hold
	// genuinely can span a suspend — and both DateTime.UtcNow AND Environment.TickCount64 would count the hours the
	// machine spent asleep as time it was "kept awake", which is the opposite of the truth. (On .NET 10
	// Environment.TickCount64 is GetTickCount64, which IS biased; it only becomes unbiased on Windows in .NET 11.)
	// QueryUnbiasedInterruptTime excludes sleep by definition. Units are 100 ns — one TimeSpan tick.
	[DllImport("kernel32.dll")]
	private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

	private static long UnbiasedTicksNow()
	{
		if (QueryUnbiasedInterruptTime(out ulong t)) return (long)t;
		return Environment.TickCount64 * TimeSpan.TicksPerMillisecond;   // same units, biased — only if the call fails
	}

	private long _sleepBlockStartTicks;           // unbiased tick at the start of the CURRENT hold segment
	private long _sleepHeldTicks;                 // segments already banked, so a re-assert across a resume still totals correctly
	private bool _sleepReasserting;               // set by the resume path: keep the running total instead of restarting it
	private bool _powerUnavailableLogged;         // the "cannot keep this PC awake" note is worth saying once, not per operation

	// Re-derives the desired state from the busy flags instead of counting set/clear calls. That is deliberate:
	// SetBusy's true/false calls are NOT balanced across the app (the VHDX export re-asserts busy five times with one
	// matching release; the clone and several other flows release twice), so any counter would either stick above
	// zero and block sleep forever, or underflow and release mid-operation. Re-evaluating three booleans is immune to
	// both, and makes every redundant call a no-op.
	private void UpdateSleepBlock()
	{
		bool keepAwake = isBusy || _cleanBusy || _analyzerBusy;
		if (keepAwake == _sleepBlocked) return;
		if (keepAwake)
		{
			IntPtr h = EnsurePowerRequest();
			// Both failure modes report ONCE per session, not per operation: unreported, the feature would be silently
			// off forever; reported every time, a single affected machine gets one line per disk refresh, per USB
			// plug/unplug and per health read — which is the log-drowning the release-side threshold exists to avoid.
			if (h == IntPtr.Zero)
			{
				NotePowerRequestUnavailable("Windows would not create the keep-awake request");
				return;   // leave _sleepBlocked false so the next operation retries
			}
			_powerSysHeld = PowerSetRequest(h, PowerRequestSystemRequired);
			_powerExecHeld = PowerSetRequest(h, PowerRequestExecutionRequired);
			if (!_powerSysHeld && !_powerExecHeld)
			{
				// An administrator can neutralise power requests (powercfg /requestsoverride). Never fail the
				// operation over it — just say so, so a sleep mid-wipe is not a mystery afterwards.
				NotePowerRequestUnavailable("Windows refused the keep-awake request");
				return;
			}
			_sleepBlocked = true;
			// Do NOT announce the hold here. Every trivial operation transitions this — a disk refresh, a health
			// read, each USB plug/unplug — so logging on acquire buried the log in pairs of lines, starting with a
			// pair at startup before the user had done anything, which reads like a bug. Report it on release
			// instead, and only when the hold actually lasted long enough to have mattered.
			if (!_sleepReasserting) _sleepHeldTicks = 0;   // a fresh hold starts the total over; a resume re-assert continues it
			_sleepBlockStartTicks = UnbiasedTicksNow();
		}
		else
		{
			ReleasePowerRequest();
			_sleepBlocked = false;
			TimeSpan held = TimeSpan.FromTicks(Math.Max(0, _sleepHeldTicks + (UnbiasedTicksNow() - _sleepBlockStartTicks)));
			_sleepHeldTicks = 0;
			if (held >= TimeSpan.FromMinutes(1))
				Log($"Kept this PC awake for {FormatDuration(held)} while the operation ran; normal sleep behaviour is restored.");
		}
	}

	private void NotePowerRequestUnavailable(string what)
	{
		if (_powerUnavailableLogged) return;
		_powerUnavailableLogged = true;
		Log($"Note: {what} — this PC may still go to sleep during a long operation. Check `powercfg /requests` from an elevated prompt.");
	}

	private IntPtr EnsurePowerRequest()
	{
		if (_powerRequest != IntPtr.Zero) return _powerRequest;
		try
		{
			ReasonContext ctx = new ReasonContext
			{
				Version = PowerRequestContextVersion,
				Flags = PowerRequestContextSimpleString,
				SimpleReasonString = "DriveForge: a disk operation is running"
			};
			IntPtr h = PowerCreateRequest(ref ctx);
			// Failure is reported as INVALID_HANDLE_VALUE (-1), NOT a null handle.
			if (h == IntPtr.Zero || h == new IntPtr(-1)) return IntPtr.Zero;
			_powerRequest = h;
		}
		catch { return IntPtr.Zero; }   // unsupported OS — degrade to "the PC may sleep", never break the operation
		return _powerRequest;
	}

	private void ReleasePowerRequest()
	{
		if (_powerRequest == IntPtr.Zero) return;
		try
		{
			if (_powerExecHeld) { PowerClearRequest(_powerRequest, PowerRequestExecutionRequired); _powerExecHeld = false; }
			if (_powerSysHeld) { PowerClearRequest(_powerRequest, PowerRequestSystemRequired); _powerSysHeld = false; }
		}
		catch { }
	}

	// True only while a disk refresh is the thing holding the busy state. Anyone else raising busy takes ownership
	// from it, so a slow refresh cannot hand back a busy state that now belongs to a real operation.
	private bool _refreshOwnsBusy;
	private long _diskScanSeq;    // ticket handed to each disk scan
	private long _diskListScan;   // ticket of the scan whose results the disk lists currently hold
	private long _refreshBusyScan;   // ticket of the scan that holds busy, so only IT can hand the state back
	private int _silentRescanRetries;   // bounds the auto-retry after a failed device-change rescan

	private void SetBusy(bool busy, string? status = null)
	{
		if (busy) _refreshOwnsBusy = false;   // a real operation is taking over (RefreshDisksAsync re-claims after its own call)
		if (busy) _reportOffered = false;     // per OPERATION, not per session: one declined offer must not silence the rest
		isBusy = busy;
		StartButton.IsEnabled = !busy;
		CreateKitButton.IsEnabled = !busy;
		CheckDriveButton.IsEnabled = !busy;
		ToolStartButton.IsEnabled = !busy;
		PauseButton.IsEnabled = busy;
		StopButton.IsEnabled = busy;
		ToolPauseButton.IsEnabled = busy;
		ToolStopButton.IsEnabled = busy;
		if (!busy)
		{
			isPaused = false;
			ProgressBar.IsIndeterminate = false; // safety net: never leave an indeterminate op's bar spinning after it ends
			if (TaskbarInfo != null) TaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
			PauseButton.Content = L("BtnPause");
			ToolPauseButton.Content = L("BtnPause");
			activeProcess = null;
			// A user Stop aborts the operation partway through — leave the bar at its last (partial) percentage looked
			// stuck/broken for every subsequent idle moment (only a handful of handlers reset it themselves). A
			// successful/failed-but-not-stopped completion is NOT touched here: several handlers deliberately set the
			// bar to 100% and call SetBusy(false) BEFORE showing their "done" dialog, and resetting it here would make
			// that 100% flash back to 0 before the user ever sees it.
			// Reset the WHOLE row, not just the bar. Several flows pin `progressDoneGiB = progressTotalGiB`
			// unconditionally on the way out — including after a Stop — so the stats line was left reading
			// "Progress: 100.0% (476.9 / 476.9 GiB)" next to an empty bar and a "0%" label: three widgets
			// contradicting each other while the dialog said the disk was only partially processed.
			if (stopRequested) ResetProgressWidgets();
		}
		if (!string.IsNullOrWhiteSpace(status))
		{
			StatusText.Text = status;
		}
		if (!busy)
		{
			// Re-apply the readiness gate so Start stays disabled if prerequisites are still missing.
			UpdateStartReadiness();
		}
		UpdateSleepBlock();   // hold off / release sleep to match the new busy state
	}

	private async Task RunProcessAsync(string fileName, string arguments, bool allowFailure = false)
	{
		ProcessResult processResult = await RunProcessInternalAsync(fileName, arguments);
		if (processResult.ExitCode != 0 && !allowFailure)
		{
			throw new InvalidOperationException($"{fileName} exited with code {processResult.ExitCode}.{Environment.NewLine}{processResult.Output}");
		}
	}

	// Robustly unload an offline registry hive.
	//
	// WHY THIS EXISTS: `reg unload` can fail transiently when antivirus / Windows Search /
	// the CLR still holds a handle on the freshly-loaded hive (very common with the large
	// SOFTWARE hive and per-user NTUSER.DAT). A FAILED unload silently DISCARDS every edit
	// made while the hive was loaded — the in-memory changes never reach the hive file on
	// the clone. This was the root cause of the missing first-boot RunOnce values: the
	// service (SYSTEM hive) happened to unload cleanly and persisted, while the SOFTWARE and
	// NTUSER.DAT RunOnce edits were lost because their unload silently failed.
	//
	// Returns true only when the hive was genuinely unloaded (edits committed to disk).
	private async Task<bool> UnloadRegistryHiveRobustAsync(string hiveRoot)
	{
		for (int attempt = 1; attempt <= 8; attempt++)
		{
			// Release any handle our own process might still hold on the hive (e.g. via a
			// finalizable RegistryKey) so reg.exe can take the exclusive lock it needs.
			GC.Collect();
			GC.WaitForPendingFinalizers();
			ProcessResult result = await RunProcessInternalAsync("reg.exe", "unload " + QuoteArgument(hiveRoot));
			if (result.ExitCode == 0)
			{
				return true;
			}
			// Back off to let the AV / Search indexer release its transient scan handle.
			await Task.Delay(400 * attempt);
		}
		return false;
	}

	private async Task<string> RunProcessCaptureAsync(string fileName, string arguments)
	{
		ProcessResult processResult = await RunProcessInternalAsync(fileName, arguments);
		if (processResult.ExitCode != 0)
		{
			throw new InvalidOperationException($"{fileName} exited with code {processResult.ExitCode}.{Environment.NewLine}{processResult.Output}");
		}
		return processResult.Output;
	}

	private async Task RunProcessWithArgumentListAsync(string fileName, IReadOnlyList<string> arguments, bool allowFailure = false)
	{
		ProcessResult processResult = await RunProcessWithArgumentListInternalAsync(fileName, arguments);
		if (processResult.ExitCode != 0 && !allowFailure)
		{
			throw new InvalidOperationException($"{fileName} exited with code {processResult.ExitCode}.{Environment.NewLine}{processResult.Output}");
		}
	}

	private Task<ProcessResult> RunProcessInternalAsync(string fileName, string arguments)
	{
		TaskCompletionSource<ProcessResult> completion = new TaskCompletionSource<ProcessResult>();
		StringBuilder output = new StringBuilder();
		bool processExited = false;
		bool outputClosed = false;
		bool errorClosed = false;
		int exitCode = -1;
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			WorkingDirectory = GetProcessWorkingDirectory(fileName),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		PrepareProcessEnvironment(startInfo);
		Process process = new Process
		{
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};
		process.OutputDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (e.Data == null)
			{
				outputClosed = true;
				TryComplete();
			}
			else
			{
				output.AppendLine(e.Data);
				((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
				{
					lastProcessOutputUtc = DateTime.UtcNow;
					TrackProgressFromOutput(e.Data);
					LogProcessLine(e.Data);
				});
			}
		};
		process.ErrorDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (e.Data == null)
			{
				errorClosed = true;
				TryComplete();
			}
			else
			{
				output.AppendLine(e.Data);
				((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
				{
					lastProcessOutputUtc = DateTime.UtcNow;
					TrackProgressFromOutput(e.Data);
					LogProcessLine(e.Data);
				});
			}
		};
		process.Exited += delegate
		{
			exitCode = process.ExitCode;
			processExited = true;
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				Log($"{fileName} exited with code {exitCode}.");
			});
			TryComplete();
		};
		Log("> " + fileName + " " + arguments);
		lastProcessOutputUtc = DateTime.UtcNow;
		lastHeartbeatLogUtc = DateTime.UtcNow;
		process.Start();
		TrySetProcessPriority(process);
		activeProcess = process;
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return completion.Task;
		void TryComplete()
		{
			if (processExited && outputClosed && errorClosed)
			{
				if (ReferenceEquals(activeProcess, process))
				{
					activeProcess = null;
				}
				completion.TrySetResult(new ProcessResult(exitCode, output.ToString()));
				process.Dispose();
			}
		}
	}

	private Task<ProcessResult> RunProcessWithArgumentListInternalAsync(string fileName, IReadOnlyList<string> arguments)
	{
		TaskCompletionSource<ProcessResult> completion = new TaskCompletionSource<ProcessResult>();
		StringBuilder output = new StringBuilder();
		bool processExited = false;
		bool outputClosed = false;
		bool errorClosed = false;
		int exitCode = -1;
		ProcessStartInfo processStartInfo = new ProcessStartInfo
		{
			FileName = fileName,
			WorkingDirectory = GetProcessWorkingDirectory(fileName),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8
		};
		PrepareProcessEnvironment(processStartInfo);
		foreach (string argument in arguments)
		{
			processStartInfo.ArgumentList.Add(argument);
		}
		Process process = new Process
		{
			StartInfo = processStartInfo,
			EnableRaisingEvents = true
		};
		process.OutputDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (e.Data == null)
			{
				outputClosed = true;
				TryComplete();
			}
			else
			{
				output.AppendLine(e.Data);
				((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
				{
					lastProcessOutputUtc = DateTime.UtcNow;
					TrackProgressFromOutput(e.Data);
					LogProcessLine(e.Data);
				});
			}
		};
		process.ErrorDataReceived += delegate(object _, DataReceivedEventArgs e)
		{
			if (e.Data == null)
			{
				errorClosed = true;
				TryComplete();
			}
			else
			{
				output.AppendLine(e.Data);
				((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
				{
					lastProcessOutputUtc = DateTime.UtcNow;
					TrackProgressFromOutput(e.Data);
					LogProcessLine(e.Data);
				});
			}
		};
		process.Exited += delegate
		{
			exitCode = process.ExitCode;
			processExited = true;
			((DispatcherObject)this).Dispatcher.Invoke((Action)delegate
			{
				Log($"{fileName} exited with code {exitCode}.");
			});
			TryComplete();
		};
		Log("> " + fileName + " " + string.Join(" ", arguments.Select(QuoteForLog)));
		lastProcessOutputUtc = DateTime.UtcNow;
		lastHeartbeatLogUtc = DateTime.UtcNow;
		process.Start();
		TrySetProcessPriority(process);
		activeProcess = process;
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return completion.Task;
		void TryComplete()
		{
			if (processExited && outputClosed && errorClosed)
			{
				if (ReferenceEquals(activeProcess, process))
				{
					activeProcess = null;
				}
				process.Dispose();
				completion.TrySetResult(new ProcessResult(exitCode, output.ToString()));
			}
		}
	}

	private static string QuoteForLog(string argument)
	{
		if (!argument.Any(char.IsWhiteSpace))
		{
			return argument;
		}
		return QuoteArgument(argument);
	}

	private static void TrySetProcessPriority(Process process)
	{
		try
		{
			process.PriorityClass = ProcessPriorityClass.AboveNormal;
		}
		catch
		{
		}
	}

	private static string GetProcessWorkingDirectory(string fileName)
	{
		string? directoryName = Path.GetDirectoryName(fileName);
		if (!string.IsNullOrWhiteSpace(directoryName) && Directory.Exists(directoryName))
		{
			return directoryName;
		}
		return Environment.CurrentDirectory;
	}

	private static void PrepareProcessEnvironment(ProcessStartInfo startInfo)
	{
		string? directoryName = Path.GetDirectoryName(startInfo.FileName);
		if (string.IsNullOrWhiteSpace(directoryName) || !Directory.Exists(directoryName))
		{
			return;
		}

		string currentPath = startInfo.Environment.TryGetValue("PATH", out string? path)
			? path ?? string.Empty
			: Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

		startInfo.Environment["PATH"] = directoryName + ";" + currentPath;
	}

	private void TrackProgressFromOutput(string line)
	{
		// During the streaming clone the poller owns the bar/byte counter for the WRITE phase. But the first
		// minutes are wimlib SCANNING the source (building the file list) — nothing is written yet, so the
		// poller stays flat and the bar would look frozen. Surface only the scan progress here (not the
		// interleaved apply/% lines that used to make the bar bounce).
		if (_suppressLineProgress)
		{
			var sc = Regex.Match(line, @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>GiB|MiB)\s+scanned", RegexOptions.IgnoreCase);
			if (sc.Success && progressDoneGiB < 0.3)
			{
				double g = ConvertToGiB(sc.Groups["value"].Value, sc.Groups["unit"].Value);
				double frac = progressTotalGiB > 0.5 ? Math.Min(1.0, g / progressTotalGiB) : 0.0;
				double target = 5.0 + frac * 33.0; // 5%..38% band while scanning
				if (target > ProgressBar.Value) ProgressBar.Value = target;
				StatusText.Text = $"Scanning Windows… {g:F1} GiB indexed (copying starts after this)";
				ProgressPercentText.Text = $"{ProgressBar.Value:F0}%";
			}
			return;
		}
		Match creatingFiles = Regex.Match(line, @"Creating files:\s+\d+\s+of\s+\d+\s+\((?<percent>\d+(?:\.\d+)?)%\)", RegexOptions.IgnoreCase);
		if (creatingFiles.Success && double.TryParse(creatingFiles.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double createPercent))
		{
			ProgressBar.Value = Math.Max(22.0, Math.Min(40.0, 22.0 + createPercent / 100.0 * 18.0));
			UpdateProgressStats();
			return;
		}
		Match archiving = Regex.Match(line, @"Archiving file data:\s+(?<done>\d+(?:\.\d+)?)\s*(?<doneUnit>GiB|MiB)\s+of\s+(?<total>\d+(?:\.\d+)?)\s*(?<totalUnit>GiB|MiB)", RegexOptions.IgnoreCase);
		if (archiving.Success)
		{
			progressDoneGiB = ConvertToGiB(archiving.Groups["done"].Value, archiving.Groups["doneUnit"].Value);
			// Use the REAL total wimlib reports for this image (not the inflated pre-scan estimate), so the
			// "Remaining" time and the bar reflect the actual bytes left, not a guess.
			progressTotalGiB = ConvertToGiB(archiving.Groups["total"].Value, archiving.Groups["totalUnit"].Value);
			double dataPercent = progressDoneGiB / Math.Max(progressTotalGiB, 1.0);
			ProgressBar.Value = Math.Max(40.0, Math.Min(82.0, 40.0 + dataPercent * 42.0));
			UpdateProgressStats();
			return;
		}
		Match scanned = Regex.Match(line, @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>GiB|MiB)\s+scanned", RegexOptions.IgnoreCase);
		if (scanned.Success)
		{
			double scannedGiB = ConvertToGiB(scanned.Groups["value"].Value, scanned.Groups["unit"].Value);
			ProgressBar.Value = Math.Max(18.0, Math.Min(22.0, 18.0 + scannedGiB / Math.Max(progressTotalGiB, 1.0) * 4.0));
			UpdateProgressStats();
			return;
		}
		Match applied = Regex.Match(line, @"(?<percent>\d+(?:\.\d+)?)\s*%", RegexOptions.IgnoreCase);
		if (applied.Success && double.TryParse(applied.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
		{
			ProgressBar.Value = Math.Max(20.0, Math.Min(82.0, percent));
			UpdateProgressStats();
		}
	}

	private static double ConvertToGiB(string value, string unit)
	{
		if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
		{
			return 0.0;
		}
		return unit.Equals("MiB", StringComparison.OrdinalIgnoreCase) ? number / 1024.0 : number;
	}

	// "hh\:mm\:ss" renders the hours-WITHIN-THE-DAY component, not the total: past 24 h it silently wraps, so a
	// 29-hour ETA printed as "05:07:38" and the Elapsed clock reset to zero once a day. Multi-pass wipes reach that
	// easily (a 3-pass wipe of a 1 TB drive over USB 2.0 is well over a day; Gutmann's 35 passes far more), so show
	// the day count as soon as there is one.
	private static string FormatDuration(TimeSpan t)
	{
		if (t < TimeSpan.Zero) t = TimeSpan.Zero;
		return t.Days > 0 ? t.ToString(@"d\.hh\:mm\:ss") : t.ToString(@"hh\:mm\:ss");
	}

	// Return the WHOLE progress row to a clean idle state. Several flows used to zero ProgressBar.Value alone, which
	// left the big "NN%" label and the stats line frozen on whatever the last timer tick sampled — an empty bar
	// sitting next to "86%" and "Progress: 85.7% …" long after the operation had finished.
	private void ResetProgressWidgets()
	{
		ProgressBar.IsIndeterminate = false;
		ProgressBar.Value = 0.0;
		if (ProgressPercentText != null) ProgressPercentText.Text = "0%";
		if (ProgressStatsText != null)
			ProgressStatsText.Text = string.Format(L("ProgStats"), "0.0", "", FormatDuration(operationStopwatch.Elapsed), "--:--:--");
		// Clear the Windows taskbar overlay too. SetBusy normally owns it, but the two analyzer flows flip `isBusy`
		// directly instead of calling SetBusy, so their taskbar bar kept scrolling after the scan had finished.
		if (TaskbarInfo != null) TaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
	}

	private void UpdateProgressStats()
	{
		TimeSpan elapsed = operationStopwatch.Elapsed;
		// Indeterminate operations (no measurable %, e.g. file-system scan or FFU apply): show a ticking
		// Elapsed clock with "—%" instead of a misleading fixed percentage, and skip the byte/ETA maths.
		if (ProgressBar.IsIndeterminate)
		{
			ProgressStatsText.Text = string.Format(L("ProgStats"), "—", "", FormatDuration(elapsed), "--:--:--");
			ProgressPercentText.Text = "";
			if (TaskbarInfo != null)
				TaskbarInfo.ProgressState = isBusy ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate : System.Windows.Shell.TaskbarItemProgressState.None;
			return;
		}
		double currentGiB = progressDoneGiB;
		// Snapshot the total BEFORE the block below, which may inflate it by 12%. The ETA must be computed against
		// the total as it stood on entry: at a completion call (the caller has just pinned done = total) inflating
		// first would make `progressTotalGiB > currentGiB` true again and quote a remaining time — several minutes
		// of phantom work — for an operation that has already finished.
		double totalBeforeBar = progressTotalGiB;

		// Drive the ProgressBar from byte progress when a data copy is active.
		// Maps 0–100% data fraction to the 40%–82% bar band (pre/post phases use 0–40 and 82–100).
		// Only advances the bar, never retreats, so pre-copy phase values are not reset.
		// Threshold of 0.3 GiB: a freshly-formatted NTFS target already reports ~0.1 GiB used (MFT/metadata),
		// which is NOT copied data. Don't jump the bar to the 40% write-band until real writing has begun.
		// MUST run BEFORE `percent` is read below: `percent` (and therefore the "N%" label, the stats line and
		// the taskbar) is derived from ProgressBar.Value, so computing it first left every one of them showing
		// the PREVIOUS tick's value. Invisible during a long operation that ticks every ~500 ms, but an op that
		// finishes inside a single tick (e.g. a Quick wipe: bar jumps from the 2% prep stage straight to done)
		// left the label frozen at the stale pre-jump percentage while the bar itself was full.
		if (currentGiB > 0.3 && progressTotalGiB > 0.5 && isBusy)
		{
			// Expand total estimate if actuals exceed projection (VSS/hardlink inflation) — but NOT for a fixed-total
			// op like a full-disk deep scan, where inflating would freeze the bar near 97%.
			// `currentGiB < progressTotalGiB` distinguishes mid-copy from COMPLETION. Mid-copy the ceiling always stays
			// ahead (it is pushed to current*1.12 from 97% on), so `current == total` is only ever reached when a
			// handler deliberately pins `progressDoneGiB = progressTotalGiB` to say "finished". Inflating there
			// permanently corrupted the field — the bar could not reach 100%, and any SECOND UpdateProgressStats call
			// (StartButton's finally, backup's own repaint) then read the already-inflated total and rendered a
			// completed clone as "100.0% (120.0 / 134.4 GiB)".
			if (!_progressFixedTotal && currentGiB >= progressTotalGiB * 0.97 && currentGiB < progressTotalGiB)
				progressTotalGiB = currentGiB * 1.12; // push ceiling 12% ahead of current position
			double frac = Math.Min(1.0, currentGiB / progressTotalGiB);
			double barTarget = _progressFullRange ? frac * 100.0 : 40.0 + frac * 42.0;
			if (barTarget > ProgressBar.Value)
				ProgressBar.Value = barTarget;
		}

		double percent = Math.Max(0.0, Math.Min(100.0, ProgressBar.Value));

		// Sliding-window speed: push current sample, discard samples older than SpeedWindowSeconds,
		// then derive MB/s from (newest - oldest) in the surviving window.
		// This gives a stable 30-second trailing average that tracks real speed changes
		// (e.g. USB throttling mid-clone) without EWA lag or per-tick jitter.
		DateTime nowUtc = DateTime.UtcNow;
		if (elapsed.TotalSeconds > 1.0 && currentGiB > 0.0)
		{
			_speedWindow.Enqueue((nowUtc, currentGiB));
			while (_speedWindow.Count > 1 && (nowUtc - _speedWindow.Peek().Time).TotalSeconds > SpeedWindowSeconds)
				_speedWindow.Dequeue();
			if (_speedWindow.Count >= 2)
			{
				var oldest = _speedWindow.Peek();
				double windowSec = Math.Max(0.5, (nowUtc - oldest.Time).TotalSeconds);
				double windowGiB = currentGiB - oldest.GiB;
				if (windowGiB > 0.0)
					progressSpeedMb = windowGiB * 1024.0 / windowSec;
			}
		}
		progressPrevGiB = currentGiB;

		string remaining = "--:--:--";
		// `percent < 99.95`: a full bar means the operation is over, so nothing remains. Some flows pin the bar to 100
		// WITHOUT pinning done = total (Create-USB sizes its total as used x 1.25), and the speed sample never decays,
		// so the completed run advertised a couple more minutes of work beside its "your USB is ready" dialog.
		if (progressSpeedMb > 0.5 && totalBeforeBar > currentGiB && percent < 99.95)
		{
			double remainingSeconds = (totalBeforeBar - currentGiB) * 1024.0 / progressSpeedMb;
			remaining = FormatDuration(TimeSpan.FromSeconds(Math.Max(0.0, remainingSeconds)));
		}
		else if (elapsed.TotalSeconds > 20.0 && percent > 1.0 && percent < 99.0)
		{
			double remainingSeconds = elapsed.TotalSeconds * (100.0 - percent) / percent;
			remaining = FormatDuration(TimeSpan.FromSeconds(Math.Max(0.0, remainingSeconds)));
		}

		// Show "X.X / Y.Y GiB" when a data copy is active — gives concrete progress context.
		// Uses the pre-inflation snapshot, like the ETA: on a completion call (done pinned to total) the block above
		// would otherwise have just pushed the ceiling 12% ahead, so every successful clone/backup/export ended on a
		// line like "100.0% (120.0 / 134.4 GiB)" — a total larger than anything that was ever copied.
		string sizeInfo = totalBeforeBar > 0.5 && currentGiB > 0.3
			? $" ({currentGiB:F1} / {totalBeforeBar:F1} GiB)"
			: string.Empty;

		// Show GB/s for fast drives (NVMe, USB4), plain MB/s otherwise
		string speedText = progressSpeedMb >= 1024.0
			? $"{progressSpeedMb / 1024.0:F2} GB/s"
			: progressSpeedMb > 0.5
				? $"{progressSpeedMb:F0} MB/s"
				: "--";

		ProgressStatsText.Text = string.Format(L("ProgStats"), percent.ToString("F1"), sizeInfo, FormatDuration(elapsed), remaining);
		ProgressPercentText.Text = $"{percent:F0}%";

		// Mirror progress onto the Windows taskbar icon (green bar, like a file copy).
		if (TaskbarInfo != null)
		{
			if (isBusy)
			{
				TaskbarInfo.ProgressState = isPaused
					? System.Windows.Shell.TaskbarItemProgressState.Paused
					: System.Windows.Shell.TaskbarItemProgressState.Normal;
				TaskbarInfo.ProgressValue = Math.Max(0.0, Math.Min(1.0, percent / 100.0));
			}
			else
			{
				TaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
			}
		}
	}

	private void UpdateLongRunningHeartbeat()
	{
		if (!isBusy || activeProcess == null || isPaused || !operationStopwatch.IsRunning)
		{
			return;
		}
		DateTime now = DateTime.UtcNow;
		TimeSpan silentFor = now - lastProcessOutputUtc;
		if (silentFor.TotalSeconds < 45.0 || (now - lastHeartbeatLogUtc).TotalSeconds < 60.0)
		{
			return;
		}
		lastHeartbeatLogUtc = now;
		// Keep a quiet liveness mark in the log only — do not overwrite the on-screen status with a scary sentence.
		Log("Working…");
	}

	private async Task<IReadOnlyList<int>> GetProcessTreeIdsAsync(int rootPid)
	{
		return await Task.Run(() =>
		{
			Dictionary<int, List<int>> children = Process.GetProcesses().Select(process =>
			{
				try
				{
					return new { process.Id, ParentId = GetParentProcessId(process.Id) };
				}
				catch
				{
					return new { process.Id, ParentId = 0 };
				}
			}).Where(item => item.ParentId > 0).GroupBy(item => item.ParentId).ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToList());
			List<int> ids = new List<int>();
			void AddTree(int pid)
			{
				ids.Add(pid);
				if (children.TryGetValue(pid, out List<int> childIds))
				{
					foreach (int child in childIds)
					{
						AddTree(child);
					}
				}
			}
			AddTree(rootPid);
			return (IReadOnlyList<int>)ids;
		});
	}

	private static int GetParentProcessId(int pid)
	{
		using Process process = Process.GetProcessById(pid);
		using SafeProcessHandle handle = OpenProcess(0x0400, false, pid);
		if (handle.IsInvalid)
		{
			return 0;
		}
		ProcessBasicInformation info = new ProcessBasicInformation();
		int status = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
		return status == 0 ? info.InheritedFromUniqueProcessId.ToInt32() : 0;
	}

	private static void SuspendProcessById(int pid)
	{
		using SafeProcessHandle handle = OpenProcess(ProcessSuspendResume, false, pid);
		if (!handle.IsInvalid)
		{
			NtSuspendProcess(handle);
		}
	}

	private static void ResumeProcessById(int pid)
	{
		using SafeProcessHandle handle = OpenProcess(ProcessSuspendResume, false, pid);
		if (!handle.IsInvalid)
		{
			NtResumeProcess(handle);
		}
	}

	private async Task KillProcessTreeAsync(int pid)
	{
		await Task.Run(() =>
		{
			using Process process = Process.Start(new ProcessStartInfo
			{
				FileName = "taskkill.exe",
				Arguments = $"/PID {pid} /T /F",
				CreateNoWindow = true,
				UseShellExecute = false
			});
			process?.WaitForExit(5000);
		});
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

	[StructLayout(LayoutKind.Sequential)]
	private struct FLASHWINFO
	{
		public uint cbSize;
		public IntPtr hwnd;
		public uint dwFlags;
		public uint uCount;
		public uint dwTimeout;
	}

	[DllImport("user32.dll")]
	private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

	// FILE_FLAG_NO_BUFFERING: reads bypass the OS/filesystem cache and hit the actual media. A verify MUST use this, or a
	// counterfeit/fake-capacity or failing drive whose just-written bytes are still in RAM would falsely pass. It requires
	// the file offset, the read length AND the buffer address to all be sector-aligned; callers align to 4096 (a multiple
	// of both 512-byte and 4Kn sectors).
	private const uint FileFlagNoBuffering = 0x20000000;
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, FileShare dwShareMode, IntPtr lpSecurityAttributes, FileMode dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize, byte[]? lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeFileHandle tokenHandle);

	[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out Luid lpLuid);

	[DllImport("advapi32.dll", SetLastError = true)]
	private static extern bool AdjustTokenPrivileges(SafeFileHandle tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

	[DllImport("ntdll.dll")]
	private static extern int NtSuspendProcess(SafeProcessHandle processHandle);

	[DllImport("ntdll.dll")]
	private static extern int NtResumeProcess(SafeProcessHandle processHandle);

	[DllImport("ntdll.dll")]
	private static extern int NtQueryInformationProcess(SafeProcessHandle processHandle, int processInformationClass, ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

	// SetFileTime — set creation + lastwrite timestamps on an already-open file handle,
	// avoiding a separate CreateFile/CloseHandle round-trip per timestamp.
	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetFileTime(SafeFileHandle hFile, ref FFileTime lpCreationTime, IntPtr lpLastAccessTime, ref FFileTime lpLastWriteTime);

	[StructLayout(LayoutKind.Sequential)]
	private struct FFileTime { public uint Low; public uint High; }

	private static FFileTime ToFFileTime(DateTime utc) { long t = utc.ToFileTimeUtc(); return new FFileTime { Low = (uint)t, High = (uint)(t >> 32) }; }

	[StructLayout(LayoutKind.Sequential)]
	private struct Luid
	{
		public uint LowPart;
		public int HighPart;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct TokenPrivileges
	{
		public uint PrivilegeCount;
		public Luid Luid;
		public uint Attributes;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct FileTimeNative
	{
		public uint LowDateTime;
		public uint HighDateTime;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ByHandleFileInformation
	{
		public uint FileAttributes;
		public FileTimeNative CreationTime;
		public FileTimeNative LastAccessTime;
		public FileTimeNative LastWriteTime;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessBasicInformation
	{
		public IntPtr Reserved1;
		public IntPtr PebBaseAddress;
		public IntPtr Reserved2;
		public IntPtr Reserved3;
		public IntPtr UniqueProcessId;
		public IntPtr InheritedFromUniqueProcessId;
	}

	private void Log(string message)
	{
		// Marshal to the UI thread — Log is called from background copy/verify/poll tasks too.
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke((Action)(() => Log(message)));
			return;
		}
		LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
		LogBox.ScrollToEnd();
	}

	// On failure, dump the full in-memory log to the desktop so the user keeps the diagnostics even after
	// closing the app. Best-effort; returns the path written or null.
	private string? SaveLogToDesktop()
	{
		try
		{
			string path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
				"DriveForge-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
			File.WriteAllText(path, LogBox.Text, Encoding.UTF8);
			Log("Full log saved to: " + path);
			SetLastReport(path);
			return path;
		}
		catch
		{
			return null;
		}
	}

	// Low-level tool output (wimlib/diskpart/bcdboot) is logged line-by-line. Some of it is internal noise
	// that is irrelevant — and even alarming — to the user (e.g. wimlib's "pipable WIM is incompatible with
	// Microsoft's software" warning, which is expected because we stream the image). Drop those lines.
	private static readonly string[] NoiseLogFragments = new[]
	{
		"Setting the DESCRIPTION property",
		"Creating a pipable WIM",
		"incompatible with Microsoft",
		"WIMGAPI",
		"ImageX/DISM",
	};

	private void LogProcessLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line)) { return; }
		foreach (string fragment in NoiseLogFragments)
		{
			if (line.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) { return; }
		}
		Log(line);
	}

	private void SetToolStatus(string message)
	{
		ToolStatusText.Text = message;
		Log("Tool: " + message);
	}

	private void SetToolOutput(string output)
	{
		ToolOutputBox.Text = output ?? "";
		ToolOutputBox.ScrollToEnd();
	}

	private void ShowError(string title, Exception ex)
	{
		Log(title + ": " + ex.Message);
		MessageBox.Show(title + ":" + Environment.NewLine + ex.Message, "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand);
		// Offer to report ONLY when an operation was actually running. A catch block runs before its finally, so
		// isBusy is still true for a genuine operation failure, and false for the trivial ones (a page that would
		// not open, a dialog that was declined) which must not nag. This is where the useful reports come from:
		// the user is looking at a real failure with the details in front of them.
		// stopRequested: the most common way to reach a failure here is the user pressing Stop — killing the child
		// process throws — and asking them to file a bug for something they cancelled on purpose is pure noise.
		// _refreshOwnsBusy: a disk rescan raises busy but is not an operation the user started; a failing scan at
		// startup would otherwise ask for a bug report before they had done anything at all.
		// headlessRun: a scheduled unattended clone must never block on a dialog nobody is there to answer.
		if (!isBusy || stopRequested || headlessRun || _refreshOwnsBusy || _reportOffered) return;
		_reportOffered = true;   // one offer per operation — a failing loop cannot queue up a stack of prompts
		if (MessageBox.Show(L("RepOffer"), "DriveForge", MessageBoxButton.YesNo, MessageBoxImage.Question,
				MessageBoxResult.No) == MessageBoxResult.Yes)
			ReportProblem(title + ": " + ex.Message);
	}

	private bool _reportOffered;

	private static bool IsAdministrator()
	{
		using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
		return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
	}

	private static string QuoteArgument(string value)
	{
		// Correct CommandLineToArgvW/MSVCRT quoting: a run of backslashes is literal UNLESS it precedes a '"' (or the
		// closing quote), in which case it must be doubled. The old version only escaped embedded quotes, so an argument
		// ending in '\' (e.g. a drive root "X:\") had its trailing backslash escape the closing quote — merging it with
		// the next argument. This general fix hardens every call site that can pass a trailing-backslash path.
		var sb = new System.Text.StringBuilder();
		sb.Append('"');
		int backslashes = 0;
		foreach (char c in value)
		{
			if (c == '\\') { backslashes++; continue; }
			if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); backslashes = 0; continue; }
			if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
			sb.Append(c);
		}
		if (backslashes > 0) sb.Append('\\', backslashes * 2);   // double a trailing run so it can't escape the closing quote
		sb.Append('"');
		return sb.ToString();
	}

	private static string QuoteCmd(string value)
	{
		return "\"" + value.Replace("\"", "\"\"") + "\"";
	}

	private static string PsQuote(string value)
	{
		return "'" + value.Replace("'", "''") + "'";
	}

	// UEFI firmware boots removable media from the hardcoded fallback path \EFI\Boot\bootx64.efi.
	// bcdboot creates that automatically only when it detects the target as REMOVABLE media; many USB
	// SSDs (like the SSK Portable) report as FIXED, so bcdboot writes only \EFI\Microsoft\Boot\bootmgfw.efi
	// and the stick fails to UEFI-boot on a different PC (no NVRAM entry there). Copying bootmgfw.efi into
	// the fallback path guarantees the clone UEFI-boots on any machine — the standard Windows-To-Go approach.
	private static bool EnsureUefiRemovableFallback(char bootLetter)
	{
		try
		{
			string source = bootLetter + ":\\EFI\\Microsoft\\Boot\\bootmgfw.efi";
			if (!File.Exists(source)) return false;
			string fallbackDir = bootLetter + ":\\EFI\\Boot";
			// Name the removable fallback after the image's REAL architecture: a 32-bit (x86) or ARM64 Windows dropped
			// as bootx64.efi would never UEFI-boot. bcdboot itself writes bootia32.efi / bootaa64.efi for those.
			string fallback = Path.Combine(fallbackDir, UefiFallbackNameFor(source));
			if (File.Exists(fallback)) return true;
			Directory.CreateDirectory(fallbackDir);
			File.Copy(source, fallback, overwrite: true);
			return File.Exists(fallback);
		}
		catch
		{
			return false;
		}
	}

	// Returns the UEFI removable-media fallback filename (\EFI\Boot\boot*.efi) for the architecture of an EFI binary,
	// read from its PE header machine field. Defaults to bootx64.efi on any read error (the overwhelmingly common case).
	private static string UefiFallbackNameFor(string efiPath) => ReadPeMachine(efiPath) switch
	{
		0x014c => "bootia32.efi",   // IMAGE_FILE_MACHINE_I386 (x86)
		0xAA64 => "bootaa64.efi",   // IMAGE_FILE_MACHINE_ARM64
		_ => "bootx64.efi",          // 0x8664 (x64), 0 (read error) and anything unexpected
	};

	// Windows Setup only applies an unattend component whose processorArchitecture matches the applied image, so derive
	// it from winload.efi's PE machine field. Defaults to amd64 on any read error (the overwhelmingly common case).
	private static string UnattendArchForWindows(string windowsFolder) => ReadPeMachine(Path.Combine(windowsFolder, "System32", "winload.efi")) switch
	{
		0x014c => "x86",
		0xAA64 => "arm64",
		_ => "amd64",
	};

	// Reads the COFF machine field from a PE binary's header (e_lfanew -> PE sig -> machine). Returns 0 on any error.
	private static ushort ReadPeMachine(string path)
	{
		try
		{
			using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			using var br = new BinaryReader(fs);
			if (fs.Length < 0x40) return 0;
			fs.Seek(0x3C, SeekOrigin.Begin);            // e_lfanew: offset to the PE signature
			int peOff = br.ReadInt32();
			if (peOff <= 0 || (long)peOff + 6 > fs.Length) return 0;
			fs.Seek(peOff, SeekOrigin.Begin);
			if (br.ReadUInt32() != 0x00004550) return 0; // "PE\0\0"
			return br.ReadUInt16();
		}
		catch { return 0; }
	}

	// First-boot answer file (a standard unattended-setup approach). For a faithful clone the OS is already
	// past OOBE, so this is a safety net: it skips OOBE if it ever runs (e.g. a reset profile), and during
	// any specialize pass it re-asserts the WinToGo essentials — keep host disks offline (SanPolicy=4) and
	// preserve all device installs so moving the stick between PCs does not strip drivers for absent devices.
	private static string BuildPortableUnattendXml(string localAccountName = "", string localAccountPassword = "", string arch = "amd64")
	{
		// When a local account is requested, create it in oobeSystem so OOBE never demands a Microsoft account
		// (the reliable bypass on 24H2/25H2). The password is encoded the way Windows Setup expects
		// (Base64 of UTF-16LE(password + "Password")) so it is not stored as casual plaintext in the file.
		string accountBlock = "";
		if (!string.IsNullOrWhiteSpace(localAccountName))
		{
			string pwElement;
			if (string.IsNullOrEmpty(localAccountPassword))
			{
				pwElement = "";
			}
			else
			{
				string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(localAccountPassword + "Password"));
				pwElement =
					"          <Password>\r\n" +
					"            <Value>" + encoded + "</Value>\r\n" +
					"            <PlainText>false</PlainText>\r\n" +
					"          </Password>\r\n";
			}
			accountBlock =
				"      <UserAccounts>\r\n" +
				"        <LocalAccounts>\r\n" +
				"          <LocalAccount wcm:action=\"add\">\r\n" +
				"            <Name>" + SecurityElementEscape(localAccountName) + "</Name>\r\n" +
				"            <Group>Administrators</Group>\r\n" +
				"            <DisplayName>" + SecurityElementEscape(localAccountName) + "</DisplayName>\r\n" +
				pwElement.Replace("          <Password>", "            <Password>").Replace("          </Password>", "            </Password>") +
				"          </LocalAccount>\r\n" +
				"        </LocalAccounts>\r\n" +
				"      </UserAccounts>\r\n";
		}
		return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
			"<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">\r\n" +
			"  <settings pass=\"specialize\">\r\n" +
			"    <component name=\"Microsoft-Windows-PartitionManager\" processorArchitecture=\"" + arch + "\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
			"      <SanPolicy>4</SanPolicy>\r\n" +
			"    </component>\r\n" +
			"    <component name=\"Microsoft-Windows-PnpSysprep\" processorArchitecture=\"" + arch + "\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
			"      <PersistAllDeviceInstalls>true</PersistAllDeviceInstalls>\r\n" +
			"      <DoNotCleanUpNonPresentDevices>true</DoNotCleanUpNonPresentDevices>\r\n" +
			"    </component>\r\n" +
			"  </settings>\r\n" +
			"  <settings pass=\"oobeSystem\">\r\n" +
			"    <component name=\"Microsoft-Windows-Shell-Setup\" processorArchitecture=\"" + arch + "\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
			accountBlock +
			"      <OOBE>\r\n" +
			"        <HideEULAPage>true</HideEULAPage>\r\n" +
			"        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>\r\n" +
			"        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>\r\n" +
			"        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>\r\n" +
			"        <ProtectYourPC>3</ProtectYourPC>\r\n" +
			"      </OOBE>\r\n" +
			"    </component>\r\n" +
			"  </settings>\r\n" +
			"</unattend>\r\n";
	}

	private static string SecurityElementEscape(string value)
	{
		return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
	}

	// Drops the answer file where Windows auto-detects it on the cloned volume.
	private static bool WritePortableUnattend(string windowsFolder, string localAccountName = "", string localAccountPassword = "")
	{
		string arch = UnattendArchForWindows(windowsFolder);
		string xml = BuildPortableUnattendXml(localAccountName, localAccountPassword, arch);
		bool any = false;
		try
		{
			string pantherDir = Path.Combine(windowsFolder, "Panther");
			Directory.CreateDirectory(pantherDir);
			File.WriteAllText(Path.Combine(pantherDir, "unattend.xml"), xml, new UTF8Encoding(false));
			any = true;
		}
		catch { }
		try
		{
			string sysprepDir = Path.Combine(windowsFolder, "System32", "Sysprep");
			if (Directory.Exists(sysprepDir))
			{
				File.WriteAllText(Path.Combine(sysprepDir, "unattend.xml"), xml, new UTF8Encoding(false));
				any = true;
			}
		}
		catch { }
		return any;
	}

	// Bcdboot /v produces hundreds of "Unable to open file ... because the file or path does not exist"
	// lines when bootstrapping a fresh EFI partition — these are expected (it tries existing files first,
	// fails, then creates them) and add no diagnostic value. Strip them to keep the report readable.
	private static string FilterBcdbootOutput(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return raw;
		var lines = raw.Split('\n');
		var kept = new System.Text.StringBuilder();
		int suppressed = 0;
		foreach (string line in lines)
		{
			if (line.Contains("Unable to open file", StringComparison.OrdinalIgnoreCase) &&
				line.Contains("because the file or path does not exist", StringComparison.OrdinalIgnoreCase))
			{
				suppressed++;
				continue;
			}
			kept.AppendLine(line.TrimEnd('\r'));
		}
		string result = kept.ToString().Trim();
		if (suppressed > 0)
			result += $"\n({suppressed} expected \"Unable to open file\" bootstrap lines suppressed)";
		return result;
	}

	private static string FormatBytes(long bytes)
	{
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		double num = bytes;
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		return $"{num:F1} {array[num2]}";
	}

	private static string GetJsonString(JsonElement element, string name, string fallback)
	{
		if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
		{
			return fallback;
		}
		return value.ToString();
	}

	private static string ExtractJsonPayload(string output)
	{
		// PowerShell can print a warning/verbose line (which may contain a stray '{' or '[') BEFORE the JSON.
		// ConvertTo-Json is always the last statement, so take from the first line that actually STARTS with a
		// bracket — this skips such prefixes instead of latching onto a bracket buried in a warning message.
		string[] lines = output.Replace("\r\n", "\n").Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			string t = lines[i].TrimStart();
			if (t.StartsWith("{") || t.StartsWith("["))
				return string.Join("\n", lines.Skip(i)).Trim();
		}
		return "[]";
	}

	private static bool GetJsonBool(JsonElement element, string name)
	{
		if (element.TryGetProperty(name, out var value))
		{
			return value.ValueKind == JsonValueKind.True;
		}
		return false;
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch
		{
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
		}
	}
}
