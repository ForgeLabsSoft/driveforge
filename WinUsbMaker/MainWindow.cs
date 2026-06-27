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

namespace WinUsbMaker;

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
			string value2 = (IsSystem ? " - BLOCKED SYSTEM" : "");
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

	private bool stopRequested;

	private bool isPaused;

	private bool internalOperationStopped;

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
			query = await RunProcessCaptureAsync("schtasks.exe", "/Query /TN " + QuoteArgument("DriveForge Auto Clone") + " /FO LIST /V");
		}
		catch { return; } // no scheduled clone task → nothing to offer
		if (string.IsNullOrWhiteSpace(query)) return;
		var run = Regex.Match(query, @"Task To Run:\s*(?<cmd>.+)", RegexOptions.IgnoreCase);
		if (!run.Success) return;
		string cmd = run.Groups["cmd"].Value;
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
		long.TryParse(GetArg("--disksize"), out long size);
		// Match by friendly name (+ size when given) among the non-system disks.
		disk = disks.FirstOrDefault(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase)
			&& (size == 0 || Math.Abs(d.Size - size) < 1024L * 1024 * 1024));
		disk ??= disks.FirstOrDefault(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase));
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
			return ext is ".iso" or ".wim" or ".esd" or ".ffu";
		});
		if (file == null)
		{
			Log("Dropped item ignored — drop a Windows .iso / .wim / .esd (or .ffu) file.");
			return;
		}
		// Switch to a matching mode automatically.
		if (Path.GetExtension(file).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
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

		return MessageBox.Show(sb.ToString(), "Confirm — please review", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
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
			string outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script));
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
		string trElem = $"\"{exe}\" --auto-clone --diskname=\"{disk.FriendlyName}\" --disksize={disk.Size} --mode={(internalMode ? "internal" : "portable")}";
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
		if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
		{
			MessageBox.Show(L("Mb003"), "Verify checksum", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		string path = sourcePath;
		try
		{
			SetBusy(busy: true, L("BzSha"));
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
		catch (Exception ex)
		{
			ShowError(L("ErrChecksum"), ex);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			SetBusy(busy: false);
			ProgressBar.Value = 0;
			ProgressPercentText.Text = "0%";
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
		panel.Children.Add(new TextBlock { Text = "SHA-256 of " + fileName + ":", Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 6) });
		var hashBox = new TextBox { Text = sha256, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 10) };
		panel.Children.Add(hashBox);
		panel.Children.Add(new TextBlock { Text = "Compare this with the SHA-256 on the official download page. Or paste the expected value below to check automatically:", Foreground = (Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
		var expectedBox = new TextBox { FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 8) };
		panel.Children.Add(expectedBox);
		var resultText = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		panel.Children.Add(resultText);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
		var compareBtn = new Button { Content = "Check match", Width = 110, Margin = new Thickness(0, 0, 8, 0) };
		var closeBtn = new Button { Content = "Close", Width = 90 };
		buttons.Children.Add(compareBtn);
		buttons.Children.Add(closeBtn);
		panel.Children.Add(buttons);
		dialog.Content = panel;
		compareBtn.Click += delegate
		{
			string expected = new string(expectedBox.Text.Where(c => !char.IsWhiteSpace(c)).ToArray());
			if (expected.Length == 0) { resultText.Text = L("ChkPasteFirst"); resultText.Foreground = (Brush)FindResource("MutedBrush"); return; }
			bool match = string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase);
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
		if (isBusy || _cleanBusy || _analyzerBusy)
		{
			MessageBoxResult messageBoxResult = MessageBox.Show(L("Mb004"), "DriveForge", MessageBoxButton.YesNo, MessageBoxImage.Exclamation);
			e.Cancel = messageBoxResult != MessageBoxResult.Yes;
			// If the user confirms close, signal the background workers to stop before teardown.
			if (!e.Cancel) { _analyzerStop = true; stopRequested = true; _recoverPaused = false; }
		}
		if (!e.Cancel)
		{
			SaveUserSettings();
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

	private async void RefreshDisks_Click(object sender, RoutedEventArgs e)
	{
		await RefreshDisksAsync();
	}

	private async Task RefreshDisksAsync()
	{
		try
		{
			int? selectedDiskNumber = (DiskBox.SelectedItem as DiskItem)?.Number;
			SetBusy(busy: true, L("BzScanDisks"));
			disks.Clear();
			_syncingDisk = true;
			DiskBox.Items.Clear();
			if (DiagDiskBox != null) DiagDiskBox.Items.Clear();
			foreach (DiskItem item in await GetDisksAsync())
			{
				disks.Add(item);
				DiskBox.Items.Add(item);
				if (DiagDiskBox != null) DiagDiskBox.Items.Add(item);
			}
			_syncingDisk = false;
			if (DiskBox.Items.Count > 0)
			{
				DiskItem? previousSelection = selectedDiskNumber.HasValue ? disks.FirstOrDefault((DiskItem disk) => disk.Number == selectedDiskNumber.Value) : null;
				DiskBox.SelectedItem = previousSelection ?? DiskBox.Items[0];
				if (DiagDiskBox != null) DiagDiskBox.SelectedItem = DiskBox.SelectedItem;
			}
			Log($"Disks found: {disks.Count}");
			StatusText.Text = L("SxReady");
		}
		catch (Exception ex)
		{
			ShowError(L("ErrDiskScan"), ex);
		}
		finally
		{
			SetBusy(busy: false);
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
		if (DownloadIsoPanel != null) DownloadIsoPanel.Visibility = Visibility.Collapsed;
		if (RecoverPanel != null) RecoverPanel.Visibility = Visibility.Collapsed;
		if (CleanPanel != null) CleanPanel.Visibility = Visibility.Collapsed;
		// Multi-boot has its own button; hide the workflow footer controls.
		StartButton.Visibility = Visibility.Collapsed;
		PauseButton.Visibility = Visibility.Collapsed;
		StopButton.Visibility = Visibility.Collapsed;
		StartHintText.Visibility = Visibility.Collapsed;
	}

	private void ShowWorkflowView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Visible;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
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
		var all = new[] { NavCreate, NavClonePortable, NavCloneInternal, NavBackup, NavRestore, NavLinux, NavDownloadIso, NavMultiBoot, NavTools, NavRecover, NavClean };
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
		if (process == null || process.HasExited)
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
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = ((ModeBox.SelectedIndex == ModeRestoreSavedClone) ? "Backup / clone image (*.wim;*.ffu)|*.wim;*.ffu|WIM backup (*.wim)|*.wim|Full clone file (*.ffu)|*.ffu|All files (*.*)|*.*" : "Windows image (*.iso;*.wim;*.esd)|*.iso;*.wim;*.esd|ISO image (*.iso)|*.iso|WIM image (*.wim)|*.wim|ESD image (*.esd)|*.esd|All files (*.*)|*.*"),
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
					EditionBox.Items.Add(new EditionItem(1, "Windows image index 1"));
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
			outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(ps));
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
			SetToolOutput("Diagnostic kit created:\r\n" + path + "\r\n\r\nOpen the README.txt inside it for the recommended tools and workflow.");
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
		if ((object)disk == null || disk.IsSystem)
		{
			return;
		}
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
			operationTimer.Stop(); operationStopwatch.Stop(); ProgressBar.Value = 0.0;
			SetBusy(busy: false);
		}
	}

	private async void CreateKitButton_Click(object sender, RoutedEventArgs e)
	{
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
						if (cloneFailed)
						{
							ProgressStatsText.Text = string.Format(L("ProgFailed"), operationStopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
						}
						SetBusy(busy: false);
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
				else
				{
					await ApplyFfuAsync(sourcePath, diskItem);
				}
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
				MessageBox.Show(L("MbUsbDone") + bootHelp + (bitLockerEncrypting ? L("MbBitLockerNote") : "") + BuildDriverDebloatSummary(), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				if (EjectWhenDoneCheck.IsChecked == true && !bitLockerEncrypting) await EjectDiskAsync(diskItem.Number);
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
				if (operationFailed)
				{
					ProgressStatsText.Text = string.Format(L("ProgFailed"), operationStopwatch.Elapsed.ToString(@"hh\:mm\:ss"));
				}
				SetBusy(busy: false);
			}
		}
	}

	private static bool IsExperimentalNtfsMode(int mode)
	{
		return mode == ModeExperimentalNtfsFullRootUsbClone || mode == ModeCloneInternal;
	}

	private async Task RunChkdskForSelectedDriveAsync(bool repair)
	{
		if (!(DiskBox.SelectedItem is DiskItem disk) || disk.IsSystem)
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
		if (repair && MessageBox.Show(string.Format(L("DScanRepairConfirm"), driveLetter), L("DScanRepairTitle"), MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
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
			bool offlineRepairRequired = !repair && (result.Output.Contains("offline scan and fix", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("snapshot error", StringComparison.OrdinalIgnoreCase) || result.Output.Contains("Shadow copying the specified volume is not supported", StringComparison.OrdinalIgnoreCase));
			if (result.ExitCode == 0)
			{
				ScanStatusText.Text = string.Format(L("DScanOk"), driveLetter);
				ScanAdviceText.Text = L("DScanOkAdvice");
				SetToolStatus(L("DScanOkTool"));
			}
			else if (offlineRepairRequired)
			{
				ScanStatusText.Text = string.Format(L("DScanOffline"), driveLetter);
				ScanAdviceText.Text = string.Format(L("DScanOfflineAdvice"), driveLetter);
				SetToolStatus(L("DScanOfflineTool"));
			}
			else
			{
				ScanStatusText.Text = string.Format(L("DScanIssues"), driveLetter, result.ExitCode);
				ScanAdviceText.Text = L("DScanIssuesAdvice");
				SetToolStatus(L("DScanIssuesTool"));
			}
			Log((repair ? "Repair scan finished for " : "Error scan finished for ") + driveLetter + ":");
			MessageBox.Show(offlineRepairRequired
				? string.Format(L("DScanMsgOffline"), driveLetter)
				: ScanStatusText.Text,
				L("DScanMsgTitle"), MessageBoxButton.OK,
				result.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Exclamation);
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
			"if ($physical) { $physical | Format-List FriendlyName,MediaType,BusType,HealthStatus,OperationalStatus,Usage,Size,SpindleSpeed,CanPool | Out-String } else { 'No matching PhysicalDisk entry found.' }";
		return await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script));
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
		return await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script));
	}

	// Exports this PC's drivers (network-only, or ALL third-party drivers) and injects them into the freshly-
	// applied Windows on the USB, so they work on first boot (e.g. Wi-Fi during OOBE). Best-effort; never fatal.
	private async Task InjectCurrentPcDriversAsync(char windowsLetter, bool allDrivers)
	{
		string dest = null;
		try
		{
			SetStage(allDrivers ? "Adding this PC's drivers (all)..." : "Adding this PC's network / Wi-Fi drivers...", 84.0);
			dest = Path.Combine(Path.GetTempPath(), $"winforge-drv-{Guid.NewGuid():N}");
			Directory.CreateDirectory(dest);
			string ps;
			if (allDrivers)
			{
				// Export-WindowsDriver dumps every third-party (OEM) driver, each into its own subfolder.
				ps = "$ErrorActionPreference='SilentlyContinue';" +
					"Export-WindowsDriver -Online -Destination '" + dest + "' | Out-Null;" +
					"'EXPORTED:' + (@(Get-ChildItem -Path '" + dest + "' -Recurse -Filter *.inf).Count)";
			}
			else
			{
				ps = "$ErrorActionPreference='SilentlyContinue';" +
					"$d='" + dest + "';" +
					"$nets=Get-WindowsDriver -Online | Where-Object { $_.ClassName -eq 'Net' };" +
					"$i=0;" +
					"foreach($n in $nets){ $sub=Join-Path $d ('net'+$i); New-Item -ItemType Directory -Force $sub | Out-Null;" +
					" pnputil /export-driver $n.Driver $sub | Out-Null; $i++ };" +
					"'EXPORTED:'+$i";
			}
			string outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(ps));
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
		char bootLetter = GetFreeDriveLetter();
		char windowsLetter = GetFreeDriveLetter(bootLetter);
		string diskpartPath = null;
		try
		{
			string imageFile = path;
			if (Path.GetExtension(path).Equals(".iso", StringComparison.OrdinalIgnoreCase))
			{
				SetStage("Mounting ISO...", 4.0);
				mountedIso = path;
				imageFile = FindInstallImage(await MountIsoAsync(path));
			}
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
				SetStage("Cancelled (target drive health).", 0.0);
				return;
			}

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
				long leftover = disk.Size - winBytes - 300L * 1024 * 1024 - 200L * 1024 * 1024;
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

			SetStage("Partitioning target disk...", 10.0);
			diskpartPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-diskpart-{Guid.NewGuid():N}.txt");
			await File.WriteAllTextAsync(diskpartPath, BuildWindowsToGoDiskpartScript(disk.Number, bootLetter, windowsLetter, windowsSizeMb, dataLetter), Encoding.ASCII);
			await RunProcessAsync("diskpart.exe", "/s \"" + diskpartPath + "\"");
			SetStage("Applying Windows image with DISM...", 20.0);
			string compactArg = CompactImageCheck.IsChecked == true ? " /Compact" : "";
			await RunProcessAsync("dism.exe", $"/Apply-Image /ImageFile:\"{imageFile}\" /Index:{index} /ApplyDir:{windowsLetter}:\\{compactArg}");

			// Post-apply integrity check: confirm DISM produced a complete, bootable Windows root.
			SetStage("Verifying applied image...", 84.0);
			bool imageOk = File.Exists($"{windowsLetter}:\\Windows\\System32\\winload.efi")
				&& File.Exists($"{windowsLetter}:\\Windows\\System32\\config\\SYSTEM")
				&& Directory.Exists($"{windowsLetter}:\\Windows\\System32\\drivers");
			if (!imageOk)
			{
				throw new InvalidOperationException("DISM apply did not produce a complete Windows root (winload.efi / SYSTEM hive / drivers missing). The drive was formatted; re-run with a valid image.");
			}
			Log("Applied image verified: Windows boot loader, SYSTEM hive and driver store present.");

			SetStage("Marking Windows as portable...", 86.0);
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
			SetStage("Creating boot files...", 92.0);
			await RunProcessAsync("bcdboot.exe", $"{windowsLetter}:\\Windows /s {bootLetter}: /f ALL /v");
			// Guarantee the UEFI removable fallback \EFI\Boot\bootx64.efi so the stick UEFI-boots on any PC
			// (bcdboot only writes it for media it detects as removable; many USB SSDs report as fixed).
			EnsureUefiRemovableFallback(bootLetter);
			if (BitLockerCheck.IsChecked == true)
			{
				await EnableBitLockerAsync(windowsLetter);
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
			"format quick fs=ntfs label=\"Windows\"",
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

	// Backup: capture the running Windows to a compressed .wim image file (VSS snapshot + wimlib). No disk is
	// formatted. The resulting file can later be restored to a drive with "Advanced: restore full disk image".
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
				"This image already exists.\n\nAppend an INCREMENTAL backup (adds a new restore point, storing only what changed — fast and small)?\n\n" +
				"Yes = append incremental\nNo = overwrite with a fresh full backup\nCancel = stop",
				"DriveForge backup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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

			SetStage("Creating VSS snapshot of Windows...", 4.0);
			string systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
			shadowCopy = await CreateShadowCopyAsync(systemDrive);
			shadowDosTarget = GetDosDeviceTarget(shadowCopy.DeviceObject);
			MapSnapshotDrive(shadowLetter, shadowDosTarget);
			string sourceRoot = shadowLetter + ":\\";

			SetStage(incremental ? "Adding an incremental restore point..." : "Compressing Windows into the image file...", 12.0);
			string wimlibPath = await EnsureWimlibAsync();
			string captureConfigPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-backup-config-{Guid.NewGuid():N}.ini");
			await File.WriteAllTextAsync(captureConfigPath, BuildCaptureConfig(), Encoding.ASCII);
			if (!incremental) TryDeleteFile(outPath);
			int threads = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
			string imageName = (incremental ? "Backup " : "DriveForge backup ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
			// "append" deduplicates against the existing images (true incremental); "capture" makes a fresh file.
			string verb = incremental ? "append" : "capture";
			string args = verb + " " + QuoteArgument(sourceRoot.TrimEnd('\\') + "\\.") + " " + QuoteArgument(outPath) +
				" " + QuoteArgument(imageName) + " " + QuoteArgument("Created by DriveForge") +
				(incremental ? "" : " --compress=LZX") + " --threads=" + threads + " --config=" + QuoteArgument(captureConfigPath) + " --check";
			using (var pollCts = new CancellationTokenSource())
			{
				Task poll = PollFileSizeProgressAsync(outPath, pollCts.Token);
				try { await RunProcessAsync(wimlibPath, args); }
				finally { pollCts.Cancel(); try { await poll; } catch { } TryDeleteFile(captureConfigPath); }
			}

			bool ok = File.Exists(outPath) && new FileInfo(outPath).Length > 0;
			progressDoneGiB = progressTotalGiB;
			operationTimer.Stop();
			operationStopwatch.Stop();
			UpdateProgressStats();
			SetStage(ok ? "Backup image created." : "Backup did not produce a file.", 100.0);
			SetBusy(busy: false);
			NotifyOperationDone(ok);
			if (ok)
			{
				SetLastReport(outPath);
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
			StatusText.Text = L("SxBackupFailed");
			NotifyOperationDone(false);
			SaveLogToDesktop();
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

	private async Task RunExperimentalFullRootUsbCloneAsync(DiskItem targetDisk)
	{
		char currentTargetLetter = GetFirstUsableDriveLetter(targetDisk);
		char bootLetter = GetFreeDriveLetter(currentTargetLetter);
		char windowsLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter);
		char shadowLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter, windowsLetter);
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		string runId = DateTime.Now.ToString("yyyyMMdd-HHmmss");
		string reportRoot = Path.Combine(desktop, "DriveForge-NTFS-FullRootClone-" + runId);
		string diskpartPath = Path.Combine(reportRoot, "01-real-usb-layout-diskpart-ran.txt");
		string realRoot = windowsLetter + ":\\";
		string realWindowsFolder = Path.Combine(realRoot, "Windows");
		string bcdStore = bootLetter + ":\\EFI\\Microsoft\\Boot\\BCD";
		string diskpartOutput = "";
		string registryOutput = "";
		string bcdbootOutput = "";
		string bcdEnumOutput = "";
		NtfsCopyTestResult? copyResult = null;
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
		Directory.CreateDirectory(reportRoot);

		try
		{
			bool backupPrivilege = TryEnablePrivilege("SeBackupPrivilege");
			bool restorePrivilege = TryEnablePrivilege("SeRestorePrivilege");
			Log($"Full root USB clone privileges: SeBackupPrivilege={(backupPrivilege ? "enabled" : "not available")}, SeRestorePrivilege={(restorePrivilege ? "enabled" : "not available")}");

			SetStage("Preparing to clone Windows (creating snapshot)...", 4.0);
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
				SetStage("Clone cancelled (target drive health).", 0.0);
				return;
			}

			SetStage("Formatting target and creating partitions...", 10.0);
			int windowsSizeMb = 0;
			var extraPartitions = new List<ExtraPartitionSpec>();
			var dataCloneJobs = new List<(char Source, char Target, string Label)>();
			long winBytesPlan = Math.Max(64L * 1024 * 1024 * 1024, (long)(GetCurrentWindowsUsedBytes() * 1.4) + 12L * 1024 * 1024 * 1024);
			long bootSlack = 350L * 1024 * 1024 + 200L * 1024 * 1024;
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
					long need = winBytesPlan + bootSlack;
					var sized = new List<(SourceDataPartition Src, long Bytes)>();
					foreach (SourceDataPartition sp in srcParts)
					{
						long b = Math.Max(1L * 1024 * 1024 * 1024, (long)(sp.UsedBytes * 1.3) + 2L * 1024 * 1024 * 1024);
						sized.Add((sp, b));
						need += b;
					}
					if (need > targetDisk.Size)
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
				long leftover = targetDisk.Size - winBytesPlan - bootSlack;
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
			await File.WriteAllTextAsync(diskpartPath, BuildRealNtfsUsbLayoutDiskpartScript(targetDisk.Number, bootLetter, windowsLetter, windowsSizeMb, extraPartitions), Encoding.ASCII);
			diskpartOutput = await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(diskpartPath));
			diskpartOk = true;

			SetStage("Cloning Windows to the target drive...", 18.0);
			Log("Faithful clone engine: wimlib captures the VSS snapshot and applies it onto the USB Windows");
			Log("partition. A WIM image apply preserves ACLs, owners, hardlinks, reparse points AND the AppX");
			Log("state EXACTLY (the standard faithful-clone approach), so the clone needs no first-boot AppX repair.");
			Log("Source root: " + sourceRoot);
			Log("Target root: " + realRoot);
			string wimlibPath = await EnsureWimlibAsync();
			string captureConfigPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-fullroot-config-{Guid.NewGuid():N}.ini");
			await File.WriteAllTextAsync(captureConfigPath, BuildCaptureConfig(), Encoding.ASCII);
			// wimlib runs as an external piped process with no progress hook, so the clone phase used to sit
			// frozen. Re-point the bar/ETA at this phase (total = source used bytes) and poll the target
			// partition's growing used-space while the apply runs, so the bar and speed move in real time.
			progressDoneGiB = 0.0;
			progressTotalGiB = Math.Max(1.0, GetCurrentWindowsUsedBytes() / 1073741824.0);
			_speedWindow.Clear();
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
			if (dataCloneJobs.Count > 0)
			{
				string dataConfigPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-data-config-{Guid.NewGuid():N}.ini");
				await File.WriteAllTextAsync(dataConfigPath, "[ExclusionList]\r\n\\System Volume Information\r\n\\$Recycle.Bin\r\n\\pagefile.sys\r\n\\hiberfil.sys\r\n\\swapfile.sys\r\n", Encoding.ASCII);
				try
				{
					int jobNum = 0;
					foreach (var job in dataCloneJobs)
					{
						jobNum++;
						SetStage($"Cloning data partition {job.Source}: -> {job.Target}: ({jobNum}/{dataCloneJobs.Count})...", 70.0);
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

			if (VerifyContentCheck.IsChecked == true)
			{
				SetStage("Verifying cloned data (content)...", 74.0);
				Log("Content verification: re-reading every file on the USB and byte-comparing it against the VSS snapshot.");
				Log("Files <= 64 MB are compared in full; larger files are spot-checked on their first/middle/last 4 MB.");
				// Re-point the live progress bar/ETA at the verification phase: total = bytes actually on the
				// Windows partition, done resets to 0 so the timer shows verify GiB, speed and remaining time.
				long verifyTotalBytes = 0;
				try { var di = new DriveInfo(realRoot); verifyTotalBytes = Math.Max(0L, di.TotalSize - di.TotalFreeSpace); } catch { }
				progressDoneGiB = 0.0;
				progressTotalGiB = Math.Max(0.5, verifyTotalBytes / 1073741824.0);
				_speedWindow.Clear();
				await Task.Run(() => VerifyCloneContent(realRoot, sourceRoot, IsNtfsCloneExcluded, out verifyVerifiedFiles, out verifyVerifiedBytes, out verifyMismatches, out verifyUnverifiable, verifySamples, verifyUnverifiableSamples));
				verifyRan = !stopRequested && !internalOperationStopped;
				Log(verifyRan
					? $"Content verification finished: {verifyVerifiedFiles:N0} files OK ({FormatBytes(verifyVerifiedBytes)}), {verifyMismatches:N0} mismatches, {verifyUnverifiable:N0} unverifiable (protected source files)."
					: "Content verification was interrupted before completing.");
			}
			else
			{
				Log("Content verification skipped (disabled in Options).");
			}

			SetStage("Applying portable Windows settings...", 80.0);
			registryOutput = await ApplyPortableRegistrySettingsToRealCloneAsync(realWindowsFolder, BypassRequirementsCheck.IsChecked == true, BypassAccountCheck.IsChecked == true, faithfulMode: true, portableMode: ModeBox.SelectedIndex != ModeCloneInternal);
			registryOk = !registryOutput.Contains("FAILED", StringComparison.OrdinalIgnoreCase);
			if (!registryOk)
			{
				throw new InvalidOperationException("Portable registry preparation failed. See the Step 20 report.");
			}

			SetStage("Writing first-boot answer file (unattend.xml)...", 86.0);
			unattendWritten = WritePortableUnattend(realWindowsFolder);
			Log(unattendWritten
				? "First-boot answer file written (unattend.xml) — SanPolicy=4, PersistAllDeviceInstalls, OOBE skip."
				: "WARNING: could not write the first-boot answer file (unattend.xml).");

			SetStage("Making the clone bootable (BIOS + UEFI)...", 88.0);
			// /f ALL writes BOTH the BIOS boot files (bootmgr + \Boot\BCD) and the UEFI boot files onto the
			// active FAT32 partition, so the single stick boots on legacy-BIOS PCs AND on UEFI PCs.
			bcdbootOutput = await RunProcessCaptureAsync("bcdboot.exe", QuoteArgument(realWindowsFolder) + $" /s {bootLetter}: /f ALL /v");
			bcdbootOk = true;
			bcdStoreOk = File.Exists(bcdStore);
			EnsureUefiRemovableFallback(bootLetter);
			bootx64Ok = File.Exists(bootLetter + ":\\EFI\\Boot\\bootx64.efi");
			if (bcdStoreOk)
			{
				bcdEnumOutput = await RunProcessCaptureAsync("bcdedit.exe", "/store " + QuoteArgument(bcdStore) + " /enum all");
				loaderPathOk = bcdEnumOutput.Contains(@"path                    \Windows\system32\winload.efi", StringComparison.OrdinalIgnoreCase)
					&& bcdEnumOutput.Contains("osdevice                partition=" + windowsLetter + ":", StringComparison.OrdinalIgnoreCase)
					&& bcdEnumOutput.Contains(@"systemroot              \Windows", StringComparison.OrdinalIgnoreCase);
			}

			if (BitLockerCheck.IsChecked == true)
			{
				try
				{
					await EnableBitLockerAsync(windowsLetter);
				}
				catch (Exception blEx)
				{
					Log("WARNING: BitLocker step failed on the clone: " + blEx.Message + " (the clone is still usable; encryption was not applied).");
				}
			}

			SetStage("Writing clone report...", 96.0);
			string reportPath = WriteFullRootUsbCloneReport(targetDisk, reportRoot, diskpartPath, shadowLetter, sourceRoot, realRoot, realWindowsFolder, bootLetter, windowsLetter, diskpartOk, copyOk, registryOk, bcdbootOk, bcdStoreOk, bootx64Ok, loaderPathOk, copyResult, diskpartOutput, registryOutput, bcdbootOutput, bcdEnumOutput, verifyRan, verifyVerifiedFiles, verifyVerifiedBytes, verifyMismatches, verifyUnverifiable, verifySamples, verifyUnverifiableSamples, unattendWritten);
			string reportText = File.ReadAllText(reportPath, Encoding.UTF8);
			bool ok = diskpartOk && copyOk && registryOk && bcdbootOk && bcdStoreOk && bootx64Ok && loaderPathOk && (!verifyRan || verifyMismatches == 0);
			progressDoneGiB = ok ? progressTotalGiB : Math.Max(progressTotalGiB * 0.85, 0.85);
			SetStage(ok ? "Faithful clone completed." : "Faithful clone completed - some checks need review.", 100.0);
			SetToolOutput(reportText);
			SelectDriveTool(ToolSmart, 5, "Faithful clone complete. Open the report for details.");
			Log(reportText);
			SetLastReport(reportPath);
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
			MessageBox.Show((ok ? "Faithful clone completed.\n\nRequired checks passed." : "Faithful clone completed, but some checks need review.") + "\n\nThe selected disk was formatted. DriveForge cloned the full current Windows root faithfully (WIM image apply), preserving apps, ACLs and the AppX state.\n\n" + (bitLockerEncrypting ? "BitLocker is STILL ENCRYPTING in the background — keep the drive connected until 'manage-bde -status' shows 100%." : "The drive has been flushed and is safe to remove.") + "\n\nHow to start from it: plug it into the target PC, power on, open the boot menu (F12 / F9 / Esc / F2 right after power-on) and pick this drive. The first boot takes a few minutes.\n\nReport:\n" + reportPath, "DriveForge", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Exclamation);
			if (cloneDialogOk) MaybeOfferDonation();
			if (EjectWhenDoneCheck.IsChecked == true && !bitLockerEncrypting) await EjectDiskAsync(targetDisk.Number);
		}
		finally
		{
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
  # Repair finished. In SYSTEM mode (elevated, via the boot service) restore the antivirus we disabled
  # on the clone so the user ends up with WORKING protection and no 'communication failure' from the AV UI.
  if (-not $UserMode) {
    $reenable = Join-Path $repairRoot 'Re-Enable-Antivirus.cmd'
    if (Test-Path $reenable) {
      try { & $reenable | Out-Null } catch {}
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

	private string WriteFullRootUsbCloneReport(DiskItem targetDisk, string reportRoot, string diskpartPath, char shadowLetter, string sourceRoot, string realRoot, string realWindowsFolder, char bootLetter, char windowsLetter, bool diskpartOk, bool copyOk, bool registryOk, bool bcdbootOk, bool bcdStoreOk, bool bootx64Ok, bool loaderPathOk, NtfsCopyTestResult? copyResult, string diskpartOutput, string registryOutput, string bcdbootOutput, string bcdEnumOutput, bool verifyRan, long verifyVerifiedFiles, long verifyVerifiedBytes, long verifyMismatches, long verifyUnverifiable, List<string> verifySamples, List<string> verifyUnverifiableSamples, bool unattendWritten)
	{
		string reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DriveForge-NTFS-FullRootClone-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
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
		report.AppendLine("Report folder: " + reportRoot);
		report.AppendLine("DiskPart script that was executed: " + diskpartPath);
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
			report.AppendLine("(copy did not start)");
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
		File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
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
				await NeutralizeAntivirusInHiveAsync(hiveRoot, controlSets, realCloneRoot);
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

	private void VerifyCloneContent(string targetRoot, string sourceRoot, Func<string, bool, bool>? shouldExclude,
		out long verifiedFiles, out long verifiedBytes, out long mismatches, out long unverifiable, List<string> sampleMismatches, List<string> sampleUnverifiable)
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
						else if (!FileContentMatches(sourcePath, entry.FullName, targetFile.Length))
						{
							mismatches++;
							AddSampleError(sampleMismatches, entry.FullName + " -> content/size differs from source");
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
				AddSampleError(sampleMismatches, currentTarget + " verify-enumerate -> " + ex.Message);
			}
		}
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

	private static char GetFirstUsableDriveLetter(DiskItem disk)
	{
		char letter = disk.DriveLetters.Select(char.ToUpperInvariant).FirstOrDefault(value => value >= 'A' && value <= 'Z');
		if (letter == '\0')
		{
			throw new InvalidOperationException("The selected disk has no drive letter. Assign a drive letter before running the NTFS test copy.");
		}
		return letter;
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
		if (lower.StartsWith("\\$windows.~bt\\", StringComparison.Ordinal) ||
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
		char currentTargetLetter = GetFirstUsableDriveLetter(disk);
		char bootLetter = GetFreeDriveLetter(currentTargetLetter);
		char windowsLetter = GetFreeDriveLetter(currentTargetLetter, bootLetter);
		string realRoot = windowsLetter + ":\\";
		string realWindowsFolder = Path.Combine(realRoot, "Windows");
		string diskpartPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-restore-diskpart-{Guid.NewGuid():N}.txt");

		TryEnablePrivilege("SeBackupPrivilege");
		TryEnablePrivilege("SeRestorePrivilege");

		// Inspect the image BEFORE touching the disk: pick the latest restore point and learn how much
		// data it holds, so we can refuse a too-small target instead of formatting and then failing.
		SetStage("Reading the image file...", 6.0);
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

		// Capacity gate (uncompressed content + ~5% NTFS/metadata headroom).
		if (imageBytes > 0 && disk.Size < (long)(imageBytes * 1.05))
		{
			throw new InvalidOperationException(
				"The selected drive is too small for this image.\n\nImage content: " + FormatBytes(imageBytes) +
				"\nSelected drive: " + FormatBytes(disk.Size) +
				"\n\nNo changes were made — the drive was not formatted.");
		}

		// Target health gate before the destructive format.
		if (!await ConfirmTargetHealthAsync(disk))
		{
			Log("WIM restore cancelled by user after target health warning.");
			SetStage("Cancelled (target drive health).", 0.0);
			return;
		}

		SetStage("Formatting target and creating partitions...", 10.0);
		await File.WriteAllTextAsync(diskpartPath, BuildRealNtfsUsbLayoutDiskpartScript(disk.Number, bootLetter, windowsLetter), Encoding.ASCII);
		await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(diskpartPath));
		TryDeleteFile(diskpartPath);

		SetStage("Restoring the image to the drive...", 20.0);
		progressDoneGiB = 0.0;
		progressTotalGiB = Math.Max(1.0, new FileInfo(wimPath).Length / 1073741824.0 * 1.8);
		_speedWindow.Clear();
		using (var pollCts = new CancellationTokenSource())
		{
			Task poll = PollPartitionUsedSpaceAsync(realRoot, pollCts.Token);
			try { await RunProcessAsync(wimlibPath, "apply " + QuoteArgument(wimPath) + " " + imageIndex + " " + QuoteArgument(realRoot) + " --recover-data"); }
			finally { pollCts.Cancel(); try { await poll; } catch { } }
		}
		bool ok = File.Exists(Path.Combine(realWindowsFolder, "System32", "winload.efi")) &&
			File.Exists(Path.Combine(realWindowsFolder, "System32", "config", "SYSTEM"));
		if (!ok)
		{
			throw new InvalidOperationException("The image did not restore a complete Windows root (winload.efi / SYSTEM missing). The drive was formatted.");
		}

		SetStage("Applying portable Windows settings...", 80.0);
		await ApplyPortableRegistrySettingsToRealCloneAsync(realWindowsFolder, BypassRequirementsCheck.IsChecked == true, BypassAccountCheck.IsChecked == true, faithfulMode: true, portableMode: true);

		SetStage("Making the restored drive bootable (BIOS + UEFI)...", 90.0);
		await RunProcessCaptureAsync("bcdboot.exe", QuoteArgument(realWindowsFolder) + $" /s {bootLetter}: /f ALL /v");
		EnsureUefiRemovableFallback(bootLetter);
		await FlushVolumesAsync(bootLetter, windowsLetter);
		Log("Image restored to Disk " + disk.Number + " and made bootable (BIOS + UEFI).");
	}

	private async Task ApplyFfuAsync(string path, DiskItem disk)
	{
		if (!Path.GetExtension(path).Equals(".ffu", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Restore clone mode requires a full clone file with .ffu extension.");
		}

		// Capacity gate: an FFU is a fixed-size block image; the target must be at least as large.
		long requiredBytes = EstimateRequiredBytes();
		if (requiredBytes > 0 && disk.Size < requiredBytes)
		{
			throw new InvalidOperationException(
				"The selected drive is too small for this clone image.\n\nRequired: " + FormatBytes(requiredBytes) +
				"\nSelected drive: " + FormatBytes(disk.Size) +
				"\n\nNo changes were made — the drive was not written.");
		}

		// Target health gate before the destructive write.
		if (!await ConfirmTargetHealthAsync(disk))
		{
			Log("FFU restore cancelled by user after target health warning.");
			SetStage("Cancelled (target drive health).", 0.0);
			return;
		}

		SetStage("Restoring full computer clone...", 8.0);
		// DISM /Apply-FFU emits no line-based progress, so show an honest moving (indeterminate) bar with a ticking Elapsed during the write.
		ProgressBar.IsIndeterminate = true;
		await RunProcessAsync("dism.exe", $"/Apply-FFU /ImageFile:\"{path}\" /ApplyDrive:\\\\.\\PhysicalDrive{disk.Number}");
		ProgressBar.IsIndeterminate = false;

		// Post-restore check: rescan and confirm the image produced partitions on the target.
		SetStage("Verifying restored clone...", 90.0);
		string partCheck = await RunProcessCaptureAsync("powershell.exe",
			"-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(
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
		SetStage("Preparing streaming clone engine...", 5.0);
		const string ExpectedWimlibSha256 = "6D99E242BFBC6D36FC987D433D63772180551B7F2D8DE43E9561535A3E2C16D8";

		// Offline-first: wimlib ships embedded inside the app, so no download is needed. Extract it from the
		// embedded resource; only fall back to a one-time download if the embedded copy is somehow missing.
		bool fromEmbedded = false;
		using (Stream? res = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("WinUsbMaker.wimlib.zip"))
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
		string scriptPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-wimlib-stream-{Guid.NewGuid():N}.cmd");
		int threadCount = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
		string command = QuoteCmd(wimlibPath) +
			" capture " + QuoteCmd(source) +
			" - " + QuoteCmd("Current Windows") +
			" " + QuoteCmd("Captured by DriveForge") +
			" --pipable --compress=none --threads=" + threadCount + " --config=" + QuoteCmd(configPath) +
			" | " + QuoteCmd(wimlibPath) +
			" apply - 1 " + QuoteCmd(target) + " --recover-data";
		try
		{
			await File.WriteAllTextAsync(scriptPath, "@echo off\r\n" + command + "\r\nexit /b %ERRORLEVEL%\r\n", Encoding.ASCII);
			await RunProcessAsync("cmd.exe", "/d /c " + QuoteArgument(scriptPath));
		}
		finally
		{
			if (File.Exists(scriptPath))
			{
				TryDeleteFile(scriptPath);
			}
		}
	}

	// Polls a partition's used space (TotalSize - free) and publishes it as copy progress while an external
	// tool (wimlib) writes to it. The dispatcher timer turns this into a live bar, speed and ETA.
	private async Task PollPartitionUsedSpaceAsync(string root, CancellationToken token)
	{
		long lastUsed = 0;
		DateTime lastAdvanceUtc = DateTime.UtcNow;
		DateTime lastWarnUtc = DateTime.MinValue;
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
				if (used > lastUsed + AdvanceThreshold)
				{
					lastUsed = used;
					lastAdvanceUtc = now;
				}
				else if (used > 0
					&& (now - lastAdvanceUtc).TotalSeconds > 150
					&& (now - lastWarnUtc).TotalSeconds > 180)
				{
					lastWarnUtc = now;
					double mbPerSec = used / 1024.0 / 1024.0 / Math.Max(1.0, operationStopwatch.Elapsed.TotalSeconds);
					Log($"WARNING: write speed is very low (~{mbPerSec:F1} MB/s overall) — less than 200 MB written in the last 2.5 minutes.");
					Log("Likely causes: a USB 2.0 port/hub, a slow QLC/SMR USB drive, or Windows Defender scanning the target.");
					Log("Tips: plug the drive directly into a blue USB 3.x port (no hub); or add a manual Defender folder exclusion for the target drive (Virus & threat protection > Exclusions).");
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
			"$shadow = Get-WmiObject Win32_ShadowCopy | Where-Object { $_.ID -eq $result.ShadowID } | Select-Object -First 1\n" +
			"if ($null -eq $shadow) { throw 'VSS snapshot was created but could not be found.' }\n" +
			"[pscustomobject]@{ Id = $shadow.ID; DeviceObject = $shadow.DeviceObject } | ConvertTo-Json -Compress";
		string json = ExtractJsonPayload(await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script)));
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
			await RunProcessAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script), allowFailure: true);
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
		try { json = (await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script))).Trim(); }
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
			return true; // cannot read SMART — do not block the clone
		}

		var warnings = new List<string>();
		string health = ExtractReportValue(report, "HealthStatus");
		if (!string.IsNullOrWhiteSpace(health)
			&& !health.Contains("Healthy", StringComparison.OrdinalIgnoreCase)
			&& !health.Contains("Unknown", StringComparison.OrdinalIgnoreCase))
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
			return true;
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
			MessageBoxImage.Warning);
		return choice == MessageBoxResult.Yes;
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
			Text = "Create a local account for the new Windows so first boot never asks for a Microsoft account.\nLeave the password empty for a no-password account.",
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("TextBrush"),
			Margin = new Thickness(0, 0, 0, 10)
		});
		panel.Children.Add(new TextBlock { Text = "Account name:", Foreground = (Brush)FindResource("TextBrush") });
		var nameBox = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		panel.Children.Add(nameBox);
		panel.Children.Add(new TextBlock { Text = "Password (optional):", Foreground = (Brush)FindResource("TextBrush") });
		var pw1 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw1Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw1);
		panel.Children.Add(pw1Plain);
		panel.Children.Add(new TextBlock { Text = "Confirm password:", Foreground = (Brush)FindResource("TextBrush") });
		var pw2 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw2Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw2);
		panel.Children.Add(pw2Plain);
		var showCheck = new CheckBox { Content = "Show password", Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 4) };
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
		var okButton = new Button { Content = "OK", Width = 110 };
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
			Text = "Enter a BitLocker unlock password for the clone.\nLeave both fields empty to protect it with only the recovery key.",
			TextWrapping = TextWrapping.Wrap,
			Foreground = (Brush)FindResource("TextBrush"),
			Margin = new Thickness(0, 0, 0, 10)
		});
		// Each field has a masked PasswordBox + a plain TextBox stacked; "Show password" toggles which one
		// is visible, on BOTH fields, keeping their values in sync.
		panel.Children.Add(new TextBlock { Text = "Password:", Foreground = (Brush)FindResource("TextBrush") });
		var pw1 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw1Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw1);
		panel.Children.Add(pw1Plain);
		panel.Children.Add(new TextBlock { Text = "Confirm password:", Foreground = (Brush)FindResource("TextBrush") });
		var pw2 = new PasswordBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26 };
		var pw2Plain = new TextBox { Margin = new Thickness(0, 2, 0, 8), MinHeight = 26, Visibility = Visibility.Collapsed };
		panel.Children.Add(pw2);
		panel.Children.Add(pw2Plain);
		var showCheck = new CheckBox { Content = "Show password", Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 4) };
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
		var okButton = new Button { Content = "OK", Width = 110 };
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
			SetStage("Applying Windows 11 compatibility bypass...", 88.0);
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
			SetStage("Applying Microsoft account bypass...", 89.0);
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
		SetStage("Removing bloatware (Copilot, Teams, ads, telemetry)...", 90.0);
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
			Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script),
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
		SetStage("Enabling BitLocker...", 96.0);
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
		SetStage("Starting BitLocker encryption...", 97.0);
		string onOutput = "";
		try { onOutput = await RunProcessCaptureAsync("manage-bde.exe", $"-on {windowsLetter}: -UsedSpaceOnly"); }
		catch (Exception ex) { onOutput = "manage-bde -on failed: " + ex.Message; }
		Log("BitLocker enable output: " + onOutput.Trim());

		// Verify encryption actually started by reading the conversion status.
		await Task.Delay(2500);
		string status = "";
		try { status = await RunProcessCaptureAsync("manage-bde.exe", $"-status {windowsLetter}:"); }
		catch (Exception ex) { status = "manage-bde -status failed: " + ex.Message; }
		Log("BitLocker status:\r\n" + status.Trim());

		bool encrypting =
			status.IndexOf("Encryption in Progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
			status.IndexOf("Fully Encrypted", StringComparison.OrdinalIgnoreCase) >= 0 ||
			status.IndexOf("Used Space Only Encrypted", StringComparison.OrdinalIgnoreCase) >= 0;
		bool inProgress = status.IndexOf("Encryption in Progress", StringComparison.OrdinalIgnoreCase) >= 0;
		Match pct = Regex.Match(status, @"Percentage Encrypted:\s*([\d\.]+%)", RegexOptions.IgnoreCase);
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
			"format quick fs=ntfs label=\"Windows\"",
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
			"format quick fs=ntfs label=\"Windows\"",
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
		string detachScriptPath = Path.Combine(Path.GetTempPath(), $"winusbmaker-detach-vhdx-{Guid.NewGuid():N}.txt");
		try
		{
			await File.WriteAllTextAsync(detachScriptPath, BuildDetachVhdxDiskpartScript(vhdPath), Encoding.ASCII);
			await RunProcessAsync("diskpart.exe", "/s \"" + detachScriptPath + "\"", allowFailure: true);
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
	private async Task NeutralizeAntivirusInHiveAsync(string hiveRoot, IReadOnlyList<string> controlSets, string cloneRoot)
	{
		var restored = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // service -> original Start
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
				if (m.Success && !restored.ContainsKey(service))
				{
					restored[service] = Convert.ToInt32(m.Groups[1].Value, 16);
				}
				// 4 = SERVICE_DISABLED — the service/driver will not load on the clone.
				await RunProcessAsync("reg.exe", "add " + QuoteArgument(serviceKey) + " /v Start /t REG_DWORD /d 4 /f", allowFailure: true);
			}
		}
		if (restored.Count == 0)
		{
			Log("Antivirus neutralization: no third-party AV services found on the image (nothing to disable).");
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
		}
		catch (Exception ex)
		{
			Log("Antivirus neutralization: restore script not written: " + ex.Message);
		}
		Log($"Antivirus neutralization: disabled {restored.Count} third-party AV service(s) on the clone so first-boot repair is not blocked. Re-Enable-Antivirus.cmd placed on the clone Desktop.");
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
			await NeutralizeAntivirusInHiveAsync(hiveRoot, new[] { "ControlSet001", "ControlSet002" }, $"{windowsLetter}:\\");
			Log("PortableOperatingSystem, SAN policy, portable sleep, service timeout, and crash-dump settings applied.");
		}
		finally
		{
			if (loaded)
			{
				await RunProcessAsync("reg.exe", "unload \"" + hiveRoot + "\"", allowFailure: true);
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
				await RunProcessAsync("reg.exe", "unload \"" + hiveRoot + "\"", allowFailure: true);
			}
		}
	}

	private string CreateWinPeCloneKit()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DriveForge-FullCloneKit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
		Directory.CreateDirectory(text);
		File.WriteAllText(Path.Combine(text, "README.txt"), "DriveForge Current Computer Clone Helper\n\nWhy this exists:\nThe safest complete copy of the current computer must be captured outside the Windows that is currently running.\nThese helper files create a full clone file (.ffu) from Windows PE.\n\nRecommended workflow:\n1. Boot into Windows PE.\n2. Run 01-create-full-clone-file.cmd.\n3. Save the full clone file to a separate external drive with enough free space.\n4. Boot back into Windows.\n5. Open DriveForge, choose \"Restore a full computer clone\", select the .ffu file, and restore it to the target USB/SSD.\n\nWarning:\nThe restore script and DriveForge restore mode format the selected destination disk.", Encoding.UTF8);
		File.WriteAllText(Path.Combine(text, "01-create-full-clone-file.cmd"), "@echo off\nsetlocal\ntitle DriveForge - Create full clone file\necho.\necho This script must be run from Windows PE as administrator.\necho It creates a complete clone file. Do not save it to the same disk you are cloning.\necho.\necho Available disks:\nwmic diskdrive get index,model,size\necho.\nset /p SRC=Enter source disk number to capture:\nset /p OUT=Enter full clone file path, for example E:\\CurrentWindows.ffu:\necho.\necho Capturing PhysicalDrive%SRC% to %OUT%\ndism /Capture-FFU /ImageFile:\"%OUT%\" /CaptureDrive:\\\\.\\PhysicalDrive%SRC% /Name:\"DriveForge Full Disk Clone\" /Description:\"Captured by DriveForge from WinPE\"\nif errorlevel 1 goto failed\necho.\necho Optimizing full clone file...\ndism /Optimize-FFU /ImageFile:\"%OUT%\"\nif errorlevel 1 goto failed\necho.\necho Capture completed.\npause\nexit /b 0\n:failed\necho.\necho Capture failed.\npause\nexit /b 1", Encoding.ASCII);
		File.WriteAllText(Path.Combine(text, "02-restore-full-clone-to-disk.cmd"), "@echo off\nsetlocal\ntitle DriveForge - Restore full clone to disk\necho.\necho WARNING: This will format the destination disk.\necho.\nwmic diskdrive get index,model,size\necho.\nset /p FFU=Enter full clone file path:\nset /p DST=Enter destination disk number:\necho.\necho You are about to restore %FFU% to PhysicalDrive%DST%.\nchoice /m \"Format destination disk and continue\"\nif errorlevel 2 exit /b 1\ndism /Apply-FFU /ImageFile:\"%FFU%\" /ApplyDrive:\\\\.\\PhysicalDrive%DST%\nif errorlevel 1 goto failed\necho.\necho Apply completed.\npause\nexit /b 0\n:failed\necho.\necho Apply failed.\npause\nexit /b 1", Encoding.ASCII);
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
		File.WriteAllText(Path.Combine(root, "Benchmark", "run-basic-write-test.cmd"), "@echo off\r\necho This is a simple Windows write test. For a professional benchmark, use a dedicated third-party benchmark tool.\r\nset /p DRIVE=Enter drive letter to test, for example E:\r\npowershell -NoProfile -ExecutionPolicy Bypass -Command \"$p='%DRIVE%\\winusbmaker-test.bin'; $b=New-Object byte[] (1MB); (New-Object Random).NextBytes($b); $sw=[Diagnostics.Stopwatch]::StartNew(); $fs=[IO.File]::Open($p,[IO.FileMode]::Create,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); for($i=0;$i -lt 512;$i++){ $fs.Write($b,0,$b.Length) }; $fs.Flush($true); $fs.Close(); $sw.Stop(); Remove-Item $p -Force; '{0:N1} MB/s' -f (512/$sw.Elapsed.TotalSeconds)\"\r\npause\r\n", Encoding.ASCII);
		File.WriteAllText(Path.Combine(root, "Surface Scan", "run-chkdsk-scan.cmd"), "@echo off\r\nset /p DRIVE=Enter drive letter to scan, for example E:\r\nchkdsk %DRIVE% /scan\r\npause\r\n", Encoding.ASCII);
		File.WriteAllText(Path.Combine(root, "Repair Recovery", "run-chkdsk-repair.cmd"), "@echo off\r\necho WARNING: This can take a long time and may lock the drive.\r\nset /p DRIVE=Enter drive letter to repair, for example E:\r\nchoice /m \"Run CHKDSK repair on %DRIVE%\"\r\nif errorlevel 2 exit /b 1\r\nchkdsk %DRIVE% /r /x\r\npause\r\n", Encoding.ASCII);
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

	private void UpdateDriveToolOverview()
	{
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
			return;
		}
		// If we already hold a health/SMART report for this disk (e.g. a Refresh or re-selection rebuilt the
		// combo), re-render the full detailed view instead of the placeholder — otherwise the SMART table
		// gets wiped every time the disk list refreshes.
		if (_diagDisk != null && _diagReport != null && _diagDisk.Number == disk.Number)
		{
			UpdateHealthVisuals(disk, _diagReport, recordTrend: false);
			if (speedResults.TryGetValue(disk.Number, out SpeedResult cachedSpeed)) UpdateSpeedVisuals(cachedSpeed);
			return;
		}
		// No report yet for this disk — clear any stale SMART rows left over from a previously selected disk.
		SmartGrid.ItemsSource = Array.Empty<SmartRow>();
		ToolDriveTitleText.Text = $"Disk {disk.Number} - {disk.FriendlyName}";
		ToolHealthText.Text = LHealth(disk.HealthText);
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
		string h = (disk.HealthText ?? "").ToLowerInvariant();
		System.Windows.Media.Color card =
			IsHealthy(disk.HealthText) ? System.Windows.Media.Color.FromRgb(22, 163, 74)
			: (h.Contains("warn") || h.Contains("caution")) ? System.Windows.Media.Color.FromRgb(180, 120, 10)
			: (string.IsNullOrWhiteSpace(h) || h.Contains("unknown")) ? System.Windows.Media.Color.FromRgb(71, 85, 105)
			: System.Windows.Media.Color.FromRgb(180, 40, 40);
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
		long realloc = Get("ReallocatedSectors"), pending = Get("PendingSectors");
		int wear = (int)Get("Wear"), temp = (int)Get("Temperature");
		string h = (disk.HealthText ?? "").ToLowerInvariant();
		bool anyData = ruc >= 0 || wuc >= 0 || rtot >= 0 || wtot >= 0 || wear >= 0 || temp >= 0 || !string.IsNullOrWhiteSpace(h);
		var reasons = new List<string>();
		bool replace = false, watch = false;

		if (h.Contains("unhealthy") || h.Contains("warn") || h.Contains("caution") || h.Contains("fail"))
		{ replace = true; reasons.Add(string.Format(L("PredReasonHealth"), disk.HealthText)); }
		if (ruc > 0 || wuc > 0) { replace = true; reasons.Add(string.Format(L("PredReasonUncorrected"), Math.Max(ruc, 0), Math.Max(wuc, 0))); }
		if (realloc > 0 || pending > 0) { replace = true; reasons.Add(string.Format(L("PredReasonSectors"), Math.Max(realloc, 0), Math.Max(pending, 0))); }
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
		AddReliabilityRow(rows, report, "PowerCycleCount", L("SmPowerCycles"));
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
		return healthText != null && (healthText.Contains("OK", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Healthy", StringComparison.OrdinalIgnoreCase) || healthText.Contains("Good", StringComparison.OrdinalIgnoreCase));
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
					long position = (long)(num3 * 7919 % 32768) * 4096L;
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
		string value = "$disks = Get-Disk | Sort-Object Number | ForEach-Object {\n  $parts = @(Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue)\n  [pscustomobject]@{\n    Number = $_.Number\n    FriendlyName = if ($null -ne $_.FriendlyName) { $_.FriendlyName.ToString() } else { ('Disk ' + $_.Number) }\n    SerialNumber = $_.SerialNumber\n    BusType = if ($null -ne $_.BusType) { $_.BusType.ToString() } else { 'Unknown' }\n    MediaType = if ($null -ne $_.MediaType) { $_.MediaType.ToString() } else { 'Unknown' }\n    HealthStatus = if ($null -ne $_.HealthStatus) { $_.HealthStatus.ToString() } else { 'Unknown' }\n    OperationalStatus = ($_.OperationalStatus | ForEach-Object { if ($null -ne $_) { $_.ToString() } }) -join ', '\n    Size = [int64]$_.Size\n    IsBoot = [bool]$_.IsBoot\n    IsSystem = [bool]$_.IsSystem\n    PartitionStyle = if ($null -ne $_.PartitionStyle) { $_.PartitionStyle.ToString() } else { 'Unknown' }\n    DriveLetters = @($parts | Where-Object DriveLetter | ForEach-Object { $_.DriveLetter.ToString() })\n  }\n}\n$disks | ConvertTo-Json -Depth 4";
		string text = ExtractJsonPayload(await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(value)));
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
				list.Add(new DiskItem(@int, jsonString, jsonString2, jsonString3, jsonString4, jsonString5, int2, jsonString6, IsSystem: true, list2));
			}
			else
			{
				list.Add(new DiskItem(@int, jsonString, jsonString2, jsonString3, jsonString4, jsonString5, int2, jsonString6, IsSystem: false, list2));
			}
		}
		return (from disk in list
			where !disk.IsSystem
			orderby disk.IsLikelyUsbOrExternal descending, disk.Number
			select disk).ToList();
	}

	private async Task<string> MountIsoAsync(string path)
	{
		string value = "Mount-DiskImage -ImagePath " + PsQuote(path) + " -PassThru | Get-Volume | Where-Object DriveLetter | Select-Object -First 1 -ExpandProperty DriveLetter";
		string text = (await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(value))).Split(new char[2] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
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
			await RunProcessAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(value), allowFailure: true);
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
		if (media == WipeMedia.Ssd && MessageBox.Show(L("WipeSsdWarn"), L("MbWipeFreeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
		if (media == WipeMedia.Unknown && MessageBox.Show(L("WipeUnknownWarn"), L("MbWipeFreeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

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
		string confirmBody = string.Format(L("MbWipeFreeConfirm"), letter, FormatBytes(free), fills.Length) + (disk.IsSystem ? "\n\n" + L("WipeVssWarn") : "");
		if (MessageBox.Show(confirmBody, L("MbWipeFreeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
			return;

		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; _progressFullRange = true; PauseButton.Content = L("BtnPause");
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
			MessageBox.Show(stopRequested
				? $"Stopped. Free space on {letter}: was only partially wiped."
				: $"Done. Free space on {letter}: was wiped — files previously deleted from it can no longer be recovered.",
				"DriveForge — wipe free space", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("ErrFreeWipe"), ex); }
		finally { _progressFullRange = false; operationTimer.Stop(); operationStopwatch.Stop(); if (failed) UpdateProgressStats(); SetBusy(busy: false); }
	}

	private void WipeFreeSpaceCore(char letter, int[] fills, long capBytes)
	{
		string dir = letter + ":\\__driveforge_freespace__";
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
		finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
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
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb020"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb021"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb022"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

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
				L("MbWipeTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
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

		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			_progressFullRange = true;
			PauseButton.Content = L("BtnPause");
			int passCount = Math.Max(1, fills.Length);
			progressTotalGiB = Math.Max(1.0, disk.Size / 1073741824.0 * passCount);
			progressDoneGiB = 0.0; progressPrevGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			DateTime wipeStarted = DateTime.Now;
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzWiping"), disk.Number, label));
			ProgressBar.Value = 0.0;
			await RawWipeDiskAsync(disk, fills);
			operationTimer.Stop(); operationStopwatch.Stop();
			TimeSpan wipeDuration = operationStopwatch.Elapsed;
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(true);
			await RefreshDisksAsync();
			MessageBox.Show(stopRequested
				? $"Wipe stopped. Disk {disk.Number} was partially overwritten."
				: $"Done. Disk {disk.Number} was wiped ({label}).\n\nUse Format to make it usable again.",
				"DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);

			// Offer a tamper-evident wipe certificate after a real overwrite (not after a Quick clear).
			// Optional + on-demand: a single "Save certificate" button — not everyone wants one, and the
			// user picks where it goes (Save As dialog). Cancel = no certificate.
			if (!stopRequested && fills.Length > 0)
			{
				int? certChoice = ShowActionMenu(L("WipeCertTitle"), L("WipeCertPrompt"),
					new[] { L("WipeCertSaveBtn") }, new[] { 0xE74E }, new[] { false }, 0);
				if (certChoice == 0)
				{
					try { await GenerateWipeCertificateAsync(disk, label, fills, wipeStarted, wipeDuration); }
					catch (Exception cex) { ShowError(L("ErrCert"), cex); }
				}
			}
		}
		catch (Exception ex)
		{
			failed = true; NotifyOperationDone(false); SaveLogToDesktop(); ShowError(L("ErrWipe"), ex);
		}
		finally
		{
			_progressFullRange = false;
			operationTimer.Stop(); operationStopwatch.Stop();
			if (failed) UpdateProgressStats();
			SetBusy(busy: false);
		}
	}

	// Overwrites the whole physical disk N times. Removes partitions first (diskpart clean) so the raw sectors
	// are writable, then writes directly to \\.\PhysicalDriveN. Honours Stop/Pause and reports live progress.
	// fills: list of passes — 0 = zeros, 1 = ones (0xFF), 2 = random. Empty = Quick (clean only).
	private long _lastWipeCoveredBytes; // bytes overwritten in the final pass — surfaced on the erasure certificate
	private async Task RawWipeDiskAsync(DiskItem disk, int[] fills)
	{
		// Remove partitions/volumes so the physical sectors are free to overwrite (and = Quick wipe).
		string dp = Path.Combine(Path.GetTempPath(), $"driveforge-wipe-{Guid.NewGuid():N}.txt");
		try
		{
			SetStage("Preparing disk (removing partitions)...", 2.0);
			await File.WriteAllTextAsync(dp, $"select disk {disk.Number}\r\nclean\r\nexit\r\n", Encoding.ASCII);
			await RunProcessCaptureAsync("diskpart.exe", "/s " + QuoteArgument(dp));
		}
		finally { TryDeleteFile(dp); }

		if (fills == null || fills.Length == 0) { Volatile.Write(ref _progressDoneBytes, (long)(progressTotalGiB * 1073741824.0)); return; } // Quick

		long size = disk.Size;
		int chunk = 8 * 1024 * 1024; // 8 MiB, multiple of 512 — fewer syscalls, faster on quick drives
		long doneTotal = 0;
		var rng = System.Security.Cryptography.RandomNumberGenerator.Create();

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
						// The reported capacity often exceeds the addressable byte count; hitting the device end on
						// the final chunk means the drive is fully overwritten, not a failure.
						written = size; break;
					}
					written += toWrite; doneTotal += toWrite;
					Volatile.Write(ref _progressDoneBytes, doneTotal);
				}
				_lastWipeCoveredBytes = written;
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
		// The raw writer sector-pads the final chunk (up to +511 bytes), so reject anything whose padded size
		// would run past the device end — otherwise the last write throws after diskpart already wiped the drive.
		if ((isoSize + 511) / 512 * 512 > disk.Size)
		{ MessageBox.Show(string.Format(L("MbIsoTooBig"), FormatBytes(isoSize), FormatBytes(disk.Size)), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		// A Windows / WinPE ISO is not "isohybrid" — writing it raw usually won't boot. Warn and point to the
		// proper task.
		if (await LooksLikeWindowsIsoAsync(sourcePath))
		{
			if (MessageBox.Show(L("Mb025"),
					"DriveForge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
				return;
		}

		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("MbWriteIsoConfirm"), Path.GetFileName(sourcePath), FormatBytes(isoSize), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
				L("MbWriteIsoTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
			return;

		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			_progressFullRange = true; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, isoSize / 1073741824.0);
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, string.Format(L("BzWriteIso"), disk.Number));
			ProgressBar.Value = 0.0;
			await RawWriteImageToDiskAsync(disk, sourcePath, isoSize);
			operationTimer.Stop(); operationStopwatch.Stop();
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(!stopRequested);
			string verifyNote = "";
				if (!stopRequested &&
					MessageBox.Show(L("Mb026"),
						"DriveForge — verify write", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
				{
					stopRequested = false; _progressFullRange = true;
					progressTotalGiB = Math.Max(1.0, isoSize / 1073741824.0);
					progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
					operationStopwatch.Restart(); operationTimer.Start();
					SetBusy(busy: true, string.Format(L("BzVerify"), disk.Number));
					ProgressBar.Value = 0.0;
					var (vok, mismatchAt) = await Task.Run(() => VerifyRawWrite(disk, sourcePath, isoSize));
					operationTimer.Stop(); operationStopwatch.Stop();
					progressDoneGiB = progressTotalGiB; UpdateProgressStats();
					SetBusy(busy: false);
					verifyNote = vok ? "\n\n✓ Verified — the data on the drive matches the ISO exactly."
						: stopRequested ? "\n\n(Verification was stopped.)"
						: $"\n\n⚠ Verification FAILED at {FormatBytes(mismatchAt)} — the drive did not write correctly. Try writing again, or use a different USB stick / port.";
				}

				if (EjectWhenDoneCheck.IsChecked == true && !stopRequested) await EjectDiskAsync(disk.Number);
			await RefreshDisksAsync();
			MessageBox.Show(stopRequested
				? $"Stopped. The ISO was only partially written to Disk {disk.Number}; it may not boot."
				: $"Done. The ISO was written to Disk {disk.Number}. Plug it into the target PC and pick it from the boot menu (F12 / F9 / Esc / F2)." + verifyNote,
				"DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); SaveLogToDesktop(); ShowError(L("ErrWriteIso"), ex); }
		finally
		{
			_progressFullRange = false;
			operationTimer.Stop(); operationStopwatch.Stop();
			if (failed) UpdateProgressStats();
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
			string o = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(ps));
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
			SetStage("Preparing disk...", 2.0);
			await File.WriteAllTextAsync(dp, $"select disk {disk.Number}\r\nclean\r\nexit\r\n", Encoding.ASCII);
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
			long done = 0; int read;
			while ((read = src.Read(buffer, 0, chunk)) > 0 && !stopRequested)
			{
				while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
				int toWrite = read;
				if (toWrite % 512 != 0) { int pad = 512 - (toWrite % 512); Array.Clear(buffer, toWrite, pad); toWrite += pad; } // sector-align tail
				dst.Write(buffer, 0, toWrite);
				done += read;
				Volatile.Write(ref _progressDoneBytes, done);
			}
			dst.Flush();
			FlushFileBuffers(h); // push the OS cache to the actual media, so write-verify reads real flash, not cache
		});
	}

	// Reads the written image back from the raw disk and compares it byte-for-byte with the source ISO.
	// Returns (ok, mismatchByteOffset). ok is false on the first differing byte (or a short read).
	private (bool ok, long mismatchAt) VerifyRawWrite(DiskItem disk, string isoPath, long isoSize)
	{
		using SafeFileHandle h = CreateFile($"\\\\.\\PhysicalDrive{disk.Number}", GenericRead, 0x3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
		if (h.IsInvalid) throw new IOException("Could not open the disk for verification (error " + Marshal.GetLastWin32Error() + ").");
		using var diskFs = new FileStream(h, FileAccess.Read);
		using var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 8 * 1024 * 1024);
		int block = 8 * 1024 * 1024;
		byte[] a = new byte[block];
		byte[] b = new byte[block + 512];
		long pos = 0;
		while (pos < isoSize && !stopRequested)
		{
			while (isPaused && !stopRequested) System.Threading.Thread.Sleep(150);
			int want = (int)Math.Min(block, isoSize - pos);
			int ar = 0; while (ar < want) { int r = src.Read(a, ar, want - ar); if (r <= 0) break; ar += r; }
			if (ar <= 0) break;
			int aligned = ((ar + 511) / 512) * 512;
			diskFs.Seek(pos, SeekOrigin.Begin);
			int br = 0; while (br < aligned) { int r = diskFs.Read(b, br, aligned - br); if (r <= 0) break; br += r; }
			int cmp = Math.Min(ar, br);
			for (int i = 0; i < cmp; i++) if (a[i] != b[i]) return (false, pos + i);
			if (br < ar) return (false, pos + br);
			pos += ar;
			Volatile.Write(ref _progressDoneBytes, pos);
		}
		return (!stopRequested && pos >= isoSize, pos);
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
				L("MbCapacityTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
			return;

		string dir = letter + ":\\__driveforge_captest__";
		bool failed = false;
		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			_progressFullRange = true; PauseButton.Content = L("BtnPause");
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
			if (stopRequested) verdict = "Test stopped before finishing.";
			else if (fake)
				verdict = $"⚠ FAKE or FAULTY DRIVE.\n\nVerified only {FormatBytes(verifiedOk)} of the {FormatBytes(written)} written before the data became corrupted. The drive's real usable capacity is about {FormatBytes(verifiedOk)}, not {FormatBytes(disk.Size)}. Do not store important data on it.";
			else
				verdict = $"✓ Drive is genuine.\n\nWrote and verified {FormatBytes(verifiedOk)} of free space with no errors — the capacity is real.";
			ToolRecommendationDetailText.Text = verdict.Replace("\n", " ");
			SetToolOutput($"Capacity test on {letter}: — claimed {FormatBytes(disk.Size)}\r\nWritten: {FormatBytes(written)}\r\nVerified OK: {FormatBytes(verifiedOk)}\r\nResult: {(fake ? "FAKE/FAULTY" : "GENUINE")}");
			MessageBox.Show(verdict, "DriveForge — capacity test", MessageBoxButton.OK, fake ? MessageBoxImage.Warning : MessageBoxImage.Information);
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("ErrCapacity"), ex); }
		finally
		{
			_progressFullRange = false;
			operationTimer.Stop(); operationStopwatch.Stop();
			if (failed) UpdateProgressStats();
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
				while (fw < thisFile && !stopRequested)
				{
					while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
					int toW = (int)Math.Min(buf, thisFile - fw);
					Stamp(globalOffset, toW);
					fs.Write(b, 0, toW);
					fw += toW; globalOffset += toW; remaining -= toW;
					Volatile.Write(ref _progressDoneBytes, globalOffset);
				}
				fs.Flush(true);
				fileIdx++;
			}
		}
		catch (IOException) { /* disk full earlier than claimed = the real boundary; verify what we wrote */ }

		long written = globalOffset;
		long verified = 0;
		foreach (var f in Directory.GetFiles(dir, "cap*.bin").OrderBy(x => x))
		{
			if (stopRequested) break;
			using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None, buf);
			int r;
			while ((r = fs.Read(b, 0, buf)) > 0 && !stopRequested)
			{
				while (isPaused && !stopRequested) System.Threading.Thread.Sleep(200);
				for (int o = 0; o + page <= r; o += page)
					if (BitConverter.ToInt64(b, o) != (verified + o) / page)
						return (written, verified + o, true); // mismatch → fake/faulty
				verified += r;
				Volatile.Write(ref _progressDoneBytes, written + verified);
			}
		}
		return (written, verified, false);
	}

	// "Test boot": spin up a throw-away Hyper-V VM that boots straight from the selected physical drive so the
	// user can SEE it boot, without rebooting their own PC. The disk is taken offline for the duration (required
	// for a Hyper-V pass-through disk) and put back online when the test is cleaned up. Nothing here installs or
	// formats anything — it just boots the drive in a VM. Requires Hyper-V.
	private const string TestBootVmName = "DriveForge-BootTest";

	private async void TestBoot_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb027"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb031"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb032"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		// Hyper-V must be installed (cmdlets present).
		bool hyperV = false;
		try
		{
			string probe = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -ExecutionPolicy Bypass -Command \"if (Get-Command New-VM -ErrorAction SilentlyContinue) { 'OK' }\"");
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
				L("MbTestBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
			return;

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

			// Open the VM console so the user can watch it boot.
			try
			{
				Process.Start(new ProcessStartInfo("vmconnect.exe", "localhost \"" + TestBootVmName + "\"") { UseShellExecute = true });
			}
			catch { /* console is optional; the VM still runs */ }

			SetBusy(busy: false);
			MessageBox.Show(L("MbVmRunning"),
				L("MbTestBootTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
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
			}
			catch (Exception cex) { Log("Test-boot cleanup error: " + cex.Message); }
			SetBusy(busy: false);
			if (started) { try { await RefreshDisksAsync(); } catch { } }
		}
	}

	// Runs a small PowerShell script (written to a temp .ps1) and returns its exit code + combined output.
	private async Task<ProcessResult> RunPowerShellScriptAsync(string script)
	{
		string path = Path.Combine(Path.GetTempPath(), $"driveforge-ps-{Guid.NewGuid():N}.ps1");
		await File.WriteAllTextAsync(path, script, new UTF8Encoding(false));
		try
		{
			return await RunProcessInternalAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + QuoteArgument(path));
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
	}

	// ---------- Recover deleted files (native NTFS undelete) ----------

	private void ShowRecoverView()
	{
		if (LeftPanelScroll == null) return;
		_toolsView = false;
		LeftPanelScroll.Visibility = Visibility.Collapsed;
		DiagnosticPanel.Visibility = Visibility.Collapsed;
		if (MultiBootPanel != null) MultiBootPanel.Visibility = Visibility.Collapsed;
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

	// Enumerates files safely (no throw) under a folder, optionally recursing, optionally by pattern.
	private static IEnumerable<string> SafeFiles(string dir, string pattern = "*", bool recurse = true)
	{
		if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) yield break;
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
				foreach (var f in SafeFiles(Path.Combine(roaming, @"Microsoft\Windows\Recent"))) yield return f;
				break;
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
				foreach (var f in SafeFiles(Path.Combine(local, "FontCache"), "*", false)) yield return f;
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

	private static (long Bytes, int Count) DeleteTargets(string key, bool toRecycle)
	{
		long bytes = 0; int count = 0;
		foreach (var f in StaticCleanTargets(key))
		{
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
		_cleanBusy = true;
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
			_cleanBusy = false;
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
		if (MessageBox.Show(this, L("CleanConfirmBody") + "\n\n" + sel + warn, L("CleanConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		bool toRecycle = CleanToRecycleCheck?.IsChecked == true;
		_cleanBusy = true; isBusy = true;
		if (CleanAnalyzeButton != null) CleanAnalyzeButton.IsEnabled = false;
		if (CleanRunButton != null) CleanRunButton.IsEnabled = false;
		try
		{
			long freed = 0; int filesDeleted = 0;
			ProgressBar.Value = 0.0; progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			int doneCats = 0;
			if (cats.Any(c => c.Clipboard)) { try { Clipboard.Clear(); } catch { } }
			foreach (var c in cats)
			{
				ProgressBar.Value = 100.0 * doneCats++ / cats.Count;
				if (CleanStatusText != null) CleanStatusText.Text = string.Format(L("CleanCleaningX"), c.Label);
				if (c.RecycleBin)
				{
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
				freed += res.Bytes; filesDeleted += res.Count;
				c.Size = 0; c.SizeText = FormatBytes(0);
			}
			ProgressBar.Value = 100.0;
			RecomputeCleanTotal();
			if (CleanStatusText != null) CleanStatusText.Text = string.Format(L("CleanDone"), FormatBytes(freed), filesDeleted);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop(); ProgressBar.Value = 0.0;
			_cleanBusy = false; isBusy = false;
			if (CleanAnalyzeButton != null) CleanAnalyzeButton.IsEnabled = true;
			if (CleanRunButton != null) CleanRunButton.IsEnabled = true;
		}
	}

	// Clears the values + subkeys under HKCU MRU keys (Run history, recent docs, typed paths, search terms). The
	// shell rebuilds them; per-user so no elevation. Pinned items (CustomDestinations) are deliberately NOT touched.
	private static void ClearRegistryValues(string[] subkeys)
	{
		foreach (var sk in subkeys)
		{
			try
			{
				using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sk, writable: true);
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
			char letter = char.ToUpperInvariant(Path.GetPathRoot(path)?.FirstOrDefault() ?? '\0');
			if (letter == '\0') return WipeMedia.Unknown;
			foreach (var it in DiskBox.Items)
				if (it is DiskItem d && d.DriveLetters.Any(c => char.ToUpperInvariant(c) == letter)) return DetectWipeMedia(d);
		}
		catch { }
		return WipeMedia.Unknown;
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
		string body = string.Format(L("SecureDelBody"), Path.GetFileName(path)) + (media != WipeMedia.Hdd ? "\n\n" + L("SecureDelSsdNote") : "");
		if (MessageBox.Show(this, body, L("SecureDelTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
		_cleanBusy = true; isBusy = true;
		try
		{
			if (CleanStatusText != null) CleanStatusText.Text = L("SecureDelWorking");
			bool overwrite = media == WipeMedia.Hdd;
			await Task.Run(() => SecureDeleteFile(path, overwrite));
			if (CleanStatusText != null) CleanStatusText.Text = L("SecureDelDone");
		}
		catch (Exception ex) { ShowError(L("SecureDelTitle"), ex); }
		finally { _cleanBusy = false; isBusy = false; }
	}

	private static void SecureDeleteFile(string path, bool overwrite)
	{
		if (overwrite && File.Exists(path))
		{
			try
			{
				File.SetAttributes(path, FileAttributes.Normal);
				long len = new FileInfo(path).Length;
				byte[] buf = new byte[1 << 20];
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
				long rem = len;
				while (rem > 0) { int chunk = (int)Math.Min(buf.Length, rem); fs.Write(buf, 0, chunk); rem -= chunk; }
				fs.Flush(flushToDisk: true);
			}
			catch { }
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
		_analyzerBusy = true; isBusy = true; _analyzerStop = false;
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
			ProgressBar.IsIndeterminate = false; ProgressBar.Value = 0.0;
			_analyzerBusy = false; isBusy = false;
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
	private static bool UnderFolder(string filePath, string folderFull)
	{
		try
		{
			string p = Path.GetFullPath(filePath).TrimEnd('\\');
			return p.Equals(folderFull, StringComparison.OrdinalIgnoreCase)
				|| p.StartsWith(folderFull + "\\", StringComparison.OrdinalIgnoreCase);
		}
		catch { return false; }
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
	private void AnalyzeKeepFirst_Click(object sender, RoutedEventArgs e)
	{
		if (DupesGrid?.ItemsSource is not IEnumerable<DupRow> rows || !rows.Any()) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnSmartNoDupes"); return; }
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
		_analyzerBusy = true; isBusy = true; _analyzerStop = false;
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
			if (AnalyzeDupesTab != null) AnalyzeDupesTab.IsSelected = true;
			int groups = dupes.Select(d => d.Group).Distinct().Count();
			if (AnalyzeStatusText != null)
				AnalyzeStatusText.Text = groups > 0 ? string.Format(L("AnSimResult"), groups, dupes.Count) + "  " + L("AnSimReview") : L("AnSimNone");
		}
		catch (Exception ex) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = ex.Message; }
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			ProgressBar.IsIndeterminate = false; ProgressBar.Value = 0.0;
			_analyzerBusy = false; isBusy = false;
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
				if (src != null) lock (_thumbCache) _thumbCache[path] = src;
			}
			if (ct.IsCancellationRequested) return;
			if (src != null) { var s = src; img.Dispatcher.Invoke(() => img.Source = s); }
		}
	}

	private async void AnalyzeDelete_Click(object sender, RoutedEventArgs e)
	{
		if (_analyzerBusy) return;
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		// Safety: never tick away the last copy of a set — if a whole set is ticked, un-tick its newest (keeper).
		int protectedSets = 0;
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
					keeper.Selected = false; keeper.Keep = true; protectedSets++;
				}
			}
			DupesGrid.Items.Refresh();
		}
		var paths = new List<(string Path, long Size)>();
		if (BigFilesGrid?.ItemsSource is IEnumerable<BigFileRow> bf) paths.AddRange(bf.Where(x => x.Selected).Select(x => (x.FullPath, x.Size)));
		if (DupesGrid?.ItemsSource is IEnumerable<DupRow> dr) paths.AddRange(dr.Where(x => x.Selected && !x.IsReference).Select(x => (x.FullPath, x.Size)));
		paths = paths.GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
		if (paths.Count == 0) { if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("CleanNothingSelected"); return; }
		long totalSel = paths.Sum(p => p.Size);
		string confirmMsg = string.Format(L("AnRecycleConfirm"), paths.Count, FormatBytes(totalSel)) + (protectedSets > 0 ? "\n\n" + string.Format(L("AnKeepProtected"), protectedSets) : "");
		if (MessageBox.Show(this, confirmMsg, L("AnalyzeDeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
		_analyzerBusy = true; isBusy = true;
		try
		{
			var res = await Task.Run(() =>
			{
				long freed = 0; int n = 0;
				var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var (p, s) in paths)
				{
					try
					{
						Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
						freed += s; n++; gone.Add(p);
					}
					catch { }
				}
				return (freed, n, gone);
			});
			if (AnalyzeStatusText != null) AnalyzeStatusText.Text = string.Format(L("AnalyzeDeleteDone"), res.n, FormatBytes(res.freed));
			if (res.n > 0) { _lastDeletedBatch = res.gone.ToList(); ShowUndoToast(res.n, res.freed); }
			// Filter against the deleted set (no per-row File.Exists stat on the UI thread).
			if (BigFilesGrid?.ItemsSource is IEnumerable<BigFileRow> bf2) BigFilesGrid.ItemsSource = bf2.Where(x => !res.gone.Contains(x.FullPath)).ToList();
			if (DupesGrid?.ItemsSource is IEnumerable<DupRow> dr2)
			{
				// Drop deleted rows, then drop duplicate groups that no longer have 2+ members.
				var kept = dr2.Where(x => !res.gone.Contains(x.FullPath)).ToList();
				DupesGrid.ItemsSource = kept.GroupBy(x => x.Group).Where(g => g.Count() >= 2).SelectMany(g => g).ToList();
			}
		}
		finally { _analyzerBusy = false; isBusy = false; }
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
		var batch = _lastDeletedBatch;
		if (batch.Count == 0) return;
		if (AnalyzeStatusText != null) AnalyzeStatusText.Text = L("AnUndoWorking");
		int restored = await Task.Run(() => RestoreFromRecycleBin(batch));
		if (AnalyzeStatusText != null)
			AnalyzeStatusText.Text = restored > 0 ? string.Format(L("AnUndoDone"), restored) : L("AnUndoNone");
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
			var want = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
			dynamic items = recycler.Items();
			int cnt = (int)items.Count;
			for (int i = cnt - 1; i >= 0 && want.Count > 0; i--)
			{
				dynamic item = items.Item(i);
				string name = (string)item.Name;
				string origLoc = (string)recycler.GetDetailsOf(item, 1); // column 1 = "Original Location" (Vista+)
				if (string.IsNullOrEmpty(origLoc)) continue;
				string full = Path.Combine(origLoc, name);
				if (!want.Contains(full)) continue;
				dynamic verbs = item.Verbs();
				int vc = (int)verbs.Count;
				for (int v = 0; v < vc; v++)
				{
					dynamic verb = verbs.Item(v);
					if (IsRestoreVerb((string)verb.Name)) { verb.DoIt(); restored++; want.Remove(full); break; }
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
			progressDoneGiB = progressTotalGiB;
			if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepFound"), scan.Files.Count); if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
			ProgressBar.Value = 100.0;
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

	private void RecoverSelectAll_Click(object sender, RoutedEventArgs e)
	{
		foreach (var f in VisibleRecoverFiles()) if (f.Recoverable) f.Selected = true;
		UpdateRecoverSelectionInfo();
	}

	private void RecoverSelectNone_Click(object sender, RoutedEventArgs e)
	{
		foreach (var f in VisibleRecoverFiles()) f.Selected = false;
		UpdateRecoverSelectionInfo();
	}

	private void UpdateRecoverSelectionInfo()
	{
		if (_lastScan == null || RecoverStatusText == null) return;
		var sel = _lastScan.Files.Where(f => f.Selected && f.Recoverable).ToList();
		int del = _lastScan.Files.Count(f => f.Deleted);
		int onDrive = _lastScan.Files.Count - del;
		RecoverButton.IsEnabled = sel.Count > 0 && !isBusy;
		RecoverStatusText.Text = sel.Count > 0
			? string.Format(L("RfSelInfo"), sel.Count, FormatBytes(sel.Sum(f => f.Size)))
			: onDrive > 0 ? string.Format(L("RfSelDelOnDrive"), del, onDrive) : string.Format(L("RfSelDel"), del);
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
			progressTotalGiB = Math.Max(1.0, total / 1073741824.0); progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			RecoverStopButton.IsEnabled = true;
			SetBusy(busy: true, string.Format(L("RfImgBusy"), letter, Path.GetFileName(dest)));
			ProgressBar.Value = 0.0;
			await Task.Run(() => CreateDiskImage(letter, dest, total, p => Dispatcher.Invoke(() => ProgressBar.Value = p)));
			operationTimer.Stop(); operationStopwatch.Stop();
			ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
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
			ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
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

	private static int PhysicalDiskOfPath(string path)
	{
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
		if (src >= 0 && src == dst)
			return MessageBox.Show(L("RfSameDiskBlocked"), L("RfFilesTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK;
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
			SetBusy(busy: true, string.Format(L("RfRecBusy"), picked.Count));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			Directory.CreateDirectory(outDir);
			var scan = _lastScan;

			var rr = await Task.Run(() => RecoverPickedToDir(picked, scan, outDir));
			ok = rr.Ok; fail = rr.Fail;

			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			NotifyOperationDone(ok > 0);
			if (MessageBox.Show(string.Format(L("RfRecDoneHead"), ok, picked.Count, outDir) + (fail > 0 ? string.Format(L("RfRecFailNote"), fail) : "") + L("RfRecOpenFolder"),
					L("RfFilesTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				try { Process.Start(new ProcessStartInfo(outDir) { UseShellExecute = true }); } catch { }
		}
		catch (Exception ex) { failed = true; NotifyOperationDone(false); ShowError(L("RfRecFailed"), ex); }
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; SetBusy(busy: false); }
	}

	// Recovers the picked files into outDir, rebuilding their original folder structure. Runs on a worker thread.
	private (int Ok, int Fail) RecoverPickedToDir(List<DeletedFile> picked, NtfsScanResult scan, string outDir)
	{
		int ok = 0, fail = 0;
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
			string outPath = Path.Combine(targetDir, safe);
			int dup = 1;
			while (used.Contains(outPath) || File.Exists(outPath))
			{
				string baseName = Path.GetFileNameWithoutExtension(safe);
				string ext = Path.GetExtension(safe);
				outPath = Path.Combine(targetDir, $"{baseName} ({dup++}){ext}");
			}
			used.Add(outPath);
			try { RecoverOne(vr, f, scan, outPath); ok++; }
			catch { fail++; try { if (File.Exists(outPath)) File.Delete(outPath); } catch { } }
			int pct = (int)((i + 1) * 100.0 / picked.Count);
			Dispatcher.Invoke(() => ProgressBar.Value = pct);
		}
		return (ok, fail);
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
							L("RfFilesTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
						return;
				}
			}
		}
		catch { }

		string temp = Path.Combine(Path.GetDirectoryName(zipPath) ?? Path.GetTempPath(), "DriveForge-zip-" + Guid.NewGuid().ToString("N"));
		int ok = 0, fail = 0;
		try
		{
			stopRequested = false; _progressFullRange = true;
			SetBusy(busy: true, string.Format(L("RfZipBusy"), picked.Count));
			ProgressBar.Value = 0.0;
			progressTotalGiB = 0.0; progressDoneGiB = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			Directory.CreateDirectory(temp);
			var scan = _lastScan;
			var rr = await Task.Run(() => RecoverPickedToDir(picked, scan, temp));
			ok = rr.Ok; fail = rr.Fail;
			if (ok > 0 && !stopRequested)
			{
				SetBusy(busy: true, L("RfZipCompress"));
				await Task.Run(() => { if (File.Exists(zipPath)) File.Delete(zipPath); System.IO.Compression.ZipFile.CreateFromDirectory(temp, zipPath, System.IO.Compression.CompressionLevel.Optimal, false); });
			}
			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			NotifyOperationDone(ok > 0);
			if (MessageBox.Show(string.Format(L("RfZipDoneHead"), ok, picked.Count, zipPath) + (fail > 0 ? string.Format(L("RfRecFailNote"), fail) : "") + L("RfZipShowExplorer"),
					L("RfFilesTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = true }); } catch { }
		}
		catch (Exception ex) { NotifyOperationDone(false); ShowError(L("RfZipFailed"), ex); }
		finally { operationTimer.Stop(); operationStopwatch.Stop(); _progressFullRange = false; SetBusy(busy: false); try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
	}

	// Save the current scan results to a .dfscan file so they can be reopened later without re-scanning.
	private void RecoverSaveSession_Click(object sender, RoutedEventArgs e)
	{
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
				: await Task.Run(() => DeepScan(loaded.Letter, token, _ => { }, startOffset, startCount));
			operationTimer.Stop(); operationStopwatch.Stop();
			loaded.Files.AddRange(more.Files);
			loaded.ResumeOffset = more.ResumeOffset; loaded.DeepPartial = more.DeepPartial;
			_lastScan = loaded;
			RecoverGrid.ItemsSource = loaded.Files;
			ApplyRecoverFilter();
			foreach (var f in more.Files) f.PropertyChanged += (_, __) => UpdateRecoverSelectionInfo();
			progressDoneGiB = progressTotalGiB;
			if (RecoverStatusText != null) RecoverStatusText.Text = string.Format(L("RfDeepFound"), loaded.Files.Count);
			ProgressBar.Value = 100.0; if (ProgressPercentText != null) ProgressPercentText.Text = "100%";
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
		var dirs = Regex.Matches(parentHtml, "href=\"(" + e.B + ")\"", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value).Distinct().ToList();
		if (dirs.Count == 0) return "";
		string dir = dirs.OrderBy(NaturalSortKey, StringComparer.Ordinal).Last().Trim('/');
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

		string name = Path.GetFileName(uri.LocalPath);
		if (string.IsNullOrWhiteSpace(name)) name = "download.iso";
		name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
		string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
		if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		string dest = Path.Combine(folder, name);
		string part = dest + ".part"; // download here, rename to the real name only after a complete download

		if (File.Exists(dest) &&
			MessageBox.Show(name + " already exists in your Downloads folder. Download it again and overwrite?",
				"DriveForge — download ISO", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
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

			using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("DriveForge");
			using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
			resp.EnsureSuccessStatusCode();
			long? total = resp.Content.Headers.ContentLength;
			using var src = await resp.Content.ReadAsStreamAsync();
			using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
			byte[] buf = new byte[1 << 20];
			long done = 0; int read;
			while ((read = await src.ReadAsync(buf, 0, buf.Length)) > 0)
			{
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
			if (MessageBox.Show(string.Format(L("MbDownloaded"), dest), L("MbDownloadTitle"),
					MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
				try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + dest + "\"") { UseShellExecute = true }); } catch { }
		}
		catch (Exception ex)
		{
			failed = true; NotifyOperationDone(false);
			try { if (File.Exists(part)) File.Delete(part); } catch { } // never leave a truncated/partial file behind
			ShowError(L("ErrDownload"), ex);
		}
		finally
		{
			operationTimer.Stop(); operationStopwatch.Stop();
			_progressFullRange = false;
			SetBusy(busy: false);
			if (failed && DlSaveHint != null) DlSaveHint.Text = "";
		}
	}

	private async Task MultiBootFlowAsync()
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb036"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

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
					L("MbMultiBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
		}
		else
		{
			string contents = await GetDiskContentsAsync(disk.Number);
			if (MessageBox.Show(string.Format(L("MbMultiBootSetup"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
					L("MbMultiBootTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
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
			operationStopwatch.Restart(); operationTimer.Start();
			SetBusy(busy: true, (update ? "Updating" : "Setting up") + $" multi-boot engine on Disk {disk.Number}...");
			ProgressBar.Value = 0.0;
			await RunVentoyAsync(exe, disk.Number, install: !update);
			operationTimer.Stop(); operationStopwatch.Stop();
			ProgressBar.Value = 100.0;
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

	// True if the disk already carries a Ventoy data partition (exFAT/NTFS volume labelled "Ventoy").
	private async Task<bool> DiskHasVentoyAsync(int number)
	{
		try
		{
			string o = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -ExecutionPolicy Bypass -Command \"(Get-Disk -Number " + number +
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
				"-NoProfile -ExecutionPolicy Bypass -Command \"(Get-Disk -Number " + number +
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
		if (found != null) return found;

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
			Log("Downloading Ventoy: " + url);
			byte[] bytes = await http.GetByteArrayAsync(url);
			string zip = Path.Combine(root, "ventoy-windows.zip");
			await File.WriteAllBytesAsync(zip, bytes);
			ZipFile.ExtractToDirectory(zip, root, overwriteFiles: true);
			try { File.Delete(zip); } catch { }
			found = FindVentoyExe(root);
			if (found == null) throw new InvalidOperationException("Ventoy2Disk.exe was not found after extracting the download.");
			return found;
		}
		finally { SetBusy(busy: false); }
	}

	private static string? FindVentoyExe(string root)
	{
		try { return Directory.Exists(root) ? Directory.GetFiles(root, "Ventoy2Disk.exe", SearchOption.AllDirectories).FirstOrDefault() : null; }
		catch { return null; }
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

	// Builds a self-contained HTML "Certificate of Data Erasure" for a completed wipe and opens it in the
	// default browser, where the user can Print -> Save as PDF. No external libraries; needs no network.
	private async Task GenerateWipeCertificateAsync(DiskItem disk, string methodLabel, int[] fills, DateTime started, TimeSpan duration)
	{
		// Try to read the drive's serial number for the record (best-effort).
		string serial = "—";
		try
		{
			string s = await RunProcessCaptureAsync("powershell.exe",
				"-NoProfile -ExecutionPolicy Bypass -Command \"(Get-Disk -Number " + disk.Number + ").SerialNumber\"");
			s = (s ?? "").Trim();
			if (!string.IsNullOrWhiteSpace(s)) serial = s;
		}
		catch { }

		string PassName(int f) => f == 0 ? "zeros (0x00)" : f == 1 ? "ones (0xFF)" : "random data";
		var passSb = new StringBuilder();
		for (int i = 0; i < fills.Length; i++)
			passSb.Append("<li>Pass " + (i + 1) + " of " + fills.Length + " — " + PassName(fills[i]) + "</li>");

		string certId = Guid.NewGuid().ToString("N").ToUpperInvariant();
		certId = certId.Substring(0, 4) + "-" + certId.Substring(4, 4) + "-" + certId.Substring(8, 4) + "-" + certId.Substring(12, 4);
		DateTime ended = started.Add(duration);
		string H(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
		string durTxt = (duration.TotalHours >= 1 ? (int)duration.TotalHours + "h " : "") + duration.Minutes + "m " + duration.Seconds + "s";
		// Report what was actually overwritten — only claim full coverage when essentially the whole drive was written.
		long coveredBytes = _lastWipeCoveredBytes > 0 ? _lastWipeCoveredBytes : disk.Size;
		bool fullCoverage = disk.Size <= 0 || coveredBytes >= (long)(disk.Size * 0.999);
		string resultHtml = fullCoverage
			? "<b style=\"color:#16a34a\">Every sector overwritten &mdash; data is not recoverable</b>"
			: "<b style=\"color:#b45309\">Drive overwritten (" + fills.Length + " pass(es)) &mdash; see byte count below</b>";

		string html =
			"<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>DriveForge — Certificate of Data Erasure</title>" +
			"<style>" +
			"*{box-sizing:border-box}body{margin:0;background:#e2e8f0;font-family:'Segoe UI',Arial,sans-serif;color:#0f172a;padding:24px}" +
			".cert{max-width:820px;margin:0 auto;background:#fff;border:1px solid #cbd5e1;box-shadow:0 10px 30px rgba(2,6,23,.15);border-radius:10px;overflow:hidden}" +
			".hd{background:linear-gradient(135deg,#0f172a,#1e3a5f);color:#f8fafc;padding:26px 32px;display:flex;align-items:center;justify-content:space-between}" +
			".hd h1{margin:0;font-size:22px;letter-spacing:.5px}.hd .sub{color:#cbd5e1;font-size:13px;margin-top:4px}" +
			".brand{font-size:26px;font-weight:700}.brand span{color:#60a5fa}" +
			".banner{background:#16a34a;color:#fff;text-align:center;font-size:18px;font-weight:700;padding:12px;letter-spacing:.5px}" +
			".body{padding:28px 32px}" +
			"table{width:100%;border-collapse:collapse;margin:6px 0 18px}" +
			"th,td{text-align:left;padding:9px 10px;border-bottom:1px solid #e2e8f0;font-size:14px;vertical-align:top}" +
			"th{width:40%;color:#475569;font-weight:600}" +
			".sec{font-size:13px;font-weight:700;text-transform:uppercase;letter-spacing:.6px;color:#1e3a5f;margin:18px 0 4px}" +
			"ul{margin:6px 0 16px 0;padding-left:22px}li{font-size:14px;margin:2px 0}" +
			".sign{display:flex;gap:40px;margin-top:30px}.sign div{flex:1}" +
			".fld{width:100%;border:none;background:transparent;font:600 16px 'Segoe UI',Arial;color:#0f172a;padding:14px 2px 6px;outline:none}.fld:focus{background:#f1f5f9;border-radius:4px}" +
			".line{border-top:1px solid #94a3b8;margin-top:0;padding-top:6px;font-size:12px;color:#64748b}" +
			".foot{font-size:11px;color:#64748b;padding:16px 32px;border-top:1px solid #e2e8f0;line-height:1.5}" +
			".toolbar{max-width:820px;margin:0 auto 14px;display:flex;align-items:center;gap:14px;flex-wrap:wrap}" +
			".btn{background:#2563eb;color:#fff;border:none;border-radius:7px;padding:10px 18px;font-size:14px;font-weight:600;cursor:pointer;box-shadow:0 2px 6px rgba(37,99,235,.3)}.btn:hover{background:#1d4ed8}" +
			".tip{font-size:12.5px;color:#475569}" +
			"@media print{body{background:#fff;padding:0}.cert{box-shadow:none;border:none}.no-print{display:none!important}.fld{padding-top:14px}}" +
			"</style></head><body>" +
			"<div class=\"toolbar no-print\"><button class=\"btn\" onclick=\"window.print()\">&#128424;&nbsp; Print / Save as PDF</button>" +
			"<span class=\"tip\">Fill in the signature &amp; date below, then click <b>Print / Save as PDF</b> and pick <b>Save as PDF</b> as the printer.</span></div>" +
			"<div class=\"cert\">" +
			"<div class=\"hd\"><div><div class=\"brand\">Drive<span>Forge</span></div><div class=\"sub\">Certificate ID " + H(certId) + "</div></div>" +
			"<div style=\"text-align:right\"><h1>Certificate of<br>Data Erasure</h1></div></div>" +
			"<div class=\"banner\">DATA SANITIZED &mdash; NOT RECOVERABLE</div>" +
			"<div class=\"body\">" +
			"<div class=\"sec\">Drive</div><table>" +
			"<tr><th>Model</th><td>" + H(disk.FriendlyName) + "</td></tr>" +
			"<tr><th>Serial number</th><td>" + H(serial) + "</td></tr>" +
			"<tr><th>Capacity</th><td>" + H(FormatBytes(disk.Size)) + "</td></tr>" +
			"<tr><th>Bus / media</th><td>" + H(disk.BusType + " / " + disk.MediaType) + "</td></tr>" +
			"<tr><th>Physical disk #</th><td>" + disk.Number + "</td></tr>" +
			"</table>" +
			"<div class=\"sec\">Erasure</div><table>" +
			"<tr><th>Method</th><td>" + H(methodLabel) + "</td></tr>" +
			"<tr><th>Overwrite passes</th><td>" + fills.Length + "</td></tr>" +
			"<tr><th>Started</th><td>" + H(started.ToString("yyyy-MM-dd HH:mm:ss")) + "</td></tr>" +
			"<tr><th>Finished</th><td>" + H(ended.ToString("yyyy-MM-dd HH:mm:ss")) + "</td></tr>" +
			"<tr><th>Duration</th><td>" + H(durTxt) + "</td></tr>" +
			"<tr><th>Data overwritten</th><td>" + H(FormatBytes(coveredBytes)) + " of " + H(FormatBytes(disk.Size)) + "</td></tr>" +
			"<tr><th>Result</th><td>" + resultHtml + "</td></tr>" +
			"</table>" +
			"<div class=\"sec\">Pass detail</div><ul>" + passSb + "</ul>" +
			"<div class=\"sec\">Performed on</div><table>" +
			"<tr><th>Computer</th><td>" + H(Environment.MachineName) + "</td></tr>" +
			"<tr><th>Operator</th><td>" + H(Environment.UserName) + "</td></tr>" +
			"<tr><th>Software</th><td>DriveForge (raw physical-sector overwrite)</td></tr>" +
			"</table>" +
			"<div class=\"sign\">" +
			"<div><input class=\"fld\" type=\"text\" placeholder=\"\" aria-label=\"Operator signature\"><div class=\"line\">Operator signature</div></div>" +
			"<div><input class=\"fld\" type=\"text\" value=\"" + H(ended.ToString("yyyy-MM-dd")) + "\" aria-label=\"Date\"><div class=\"line\">Date</div></div></div>" +
			"</div>" +
			"<div class=\"foot\">This certificate records a destructive overwrite of " + (fullCoverage ? "all addressable sectors" : "the drive (see byte count above)") + " on the drive named above, performed with DriveForge. " +
			"A multi-pass overwrite renders previously stored data unrecoverable by software means. Note: on some solid-state drives, wear-levelling and over-provisioning may retain a small number of inaccessible blocks; for the highest assurance on SSDs, combine this with the drive's built-in Secure Erase or physical destruction. Keep this certificate for your records.</div>" +
			"</div></body></html>";

		// Let the user choose where to save it (defaults to the Desktop with a descriptive name).
		// If they cancel the dialog, nothing is written — the certificate is entirely optional.
		var dlg = new Microsoft.Win32.SaveFileDialog
		{
			Title = L("WipeCertDlgTitle"),
			Filter = L("WipeCertFilter") + " (*.html)|*.html",
			FileName = "DriveForge-Wipe-Certificate-Disk" + disk.Number + "-" + started.ToString("yyyyMMdd-HHmmss") + ".html",
			InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			AddExtension = true,
			OverwritePrompt = true
		};
		if (dlg.ShowDialog(this) != true) return;
		string file = dlg.FileName;
		await File.WriteAllTextAsync(file, html, new UTF8Encoding(false));
		Log("Wipe certificate saved: " + file);
		try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { }
	}

	// Read-only surface test: reads every sector of the selected disk to find bad / unreadable blocks. Detects
	// a failing drive that still reports "healthy". Writes nothing — safe to run on any disk, including the system one.
	private async void SurfaceTest_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		var disk = (DiagDiskBox?.SelectedItem ?? DiskBox?.SelectedItem) as DiskItem;
		if (disk == null) { MessageBox.Show(L("Mb039"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb040"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (MessageBox.Show(string.Format(L("MbSurfaceConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size)),
				L("MbSurfaceTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
			return;

		try
		{
			stopRequested = false; isPaused = false; bitLockerEncrypting = false;
			_progressFullRange = true; PauseButton.Content = L("BtnPause");
			progressTotalGiB = Math.Max(1.0, disk.Size / 1073741824.0);
			progressDoneGiB = 0.0; progressSpeedMb = 0.0; _speedWindow.Clear();
			operationStopwatch.Restart(); operationTimer.Start();
			if (ToolStopButton != null) ToolStopButton.IsEnabled = true;
			SetBusy(busy: true, string.Format(L("BzSurface"), disk.Number));
			ProgressBar.Value = 0.0;

			var res = await Task.Run(() => RunSurfaceScanCore(disk));
			operationTimer.Stop(); operationStopwatch.Stop();
			double secs = Math.Max(0.001, operationStopwatch.Elapsed.TotalSeconds);
			double avgMb = res.readBytes / 1048576.0 / secs;
			progressDoneGiB = progressTotalGiB; UpdateProgressStats();
			SetBusy(busy: false);
			NotifyOperationDone(res.bad == 0 && !res.stopped);

			string verdict = res.bad == 0
				? $"✓ No bad blocks found.\n\nRead {FormatBytes(res.readBytes)} with no read errors at ~{avgMb:F0} MB/s. The surface looks healthy."
				: $"⚠ {res.bad} unreadable block(s) found ({FormatBytes(res.badBytes)}).\n\nThe drive may be failing — back up your data now and consider replacing it.";
			if (res.stopped) verdict = "Surface test stopped before finishing.\n\n" + verdict;
			ToolRecommendationDetailText.Text = verdict.Replace("\n", " ");
			SetToolOutput($"Surface test — Disk {disk.Number} ({disk.FriendlyName})\r\nRead: {FormatBytes(res.readBytes)} of {FormatBytes(disk.Size)}\r\nAverage read: {avgMb:F0} MB/s\r\nBad blocks: {res.bad}" +
				(res.detail.Length > 0 ? "\r\nFirst bad regions:\r\n" + res.detail : ""));
			MessageBox.Show(verdict, "DriveForge — surface test", MessageBoxButton.OK, res.bad == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
		}
		catch (Exception ex) { NotifyOperationDone(false); ShowError(L("ErrSurface"), ex); }
		finally
		{
			_progressFullRange = false; operationTimer.Stop(); operationStopwatch.Stop();
			if (ToolStopButton != null) ToolStopButton.IsEnabled = false;
			SetBusy(busy: false);
		}
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
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

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
			try { files.AddRange(Directory.EnumerateFiles(baseFolder, "*", SearchOption.AllDirectories)); }
			catch (Exception ex) { ShowError(L("ErrListFolder"), ex); return; }
		}
		if (files.Count == 0) { MessageBox.Show(L("Mb041"), L("MbShredTitle"), MessageBoxButton.OK, MessageBoxImage.Information); return; }

		string[] methods = { L("AmFreeZero"), L("AmFreeRandom"), L("AmFree3"), L("AmMethod7") };
		int? sel = ShowActionMenu(L("MbShredTitle"), string.Format(L("AmShredMethodPrompt"), files.Count), methods,
			new[] { 0xEA99, 0xE9CE, 0xE730, 0xE730 }, new[] { true, true, true, true }, 0);
		if (sel == null) return;
		int[] fills = sel.Value switch { 1 => new[] { 2 }, 2 => new[] { 0, 2, 0 }, 3 => new[] { 0, 1, 2, 0, 1, 2, 2 }, _ => new[] { 0 } };

		if (MessageBox.Show(string.Format(L("MbShredConfirm"), files.Count, fills.Length),
				L("MbShredTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
			return;

		long totalBytes = 0;
		foreach (var f in files) { try { totalBytes += new FileInfo(f).Length; } catch { } }

		bool failed = false; int done = 0, fail = 0;
		try
		{
			stopRequested = false; isPaused = false; _progressFullRange = true; PauseButton.Content = L("BtnPause");
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
					try { ShredOne(f, fills, acc); done++; } catch { fail++; }
				}
				if (baseFolder != null && !stopRequested)
				{
					try
					{
						foreach (var d in Directory.EnumerateDirectories(baseFolder, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
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
		finally { _progressFullRange = false; operationTimer.Stop(); operationStopwatch.Stop(); if (failed) UpdateProgressStats(); SetBusy(busy: false); }
	}

	// Overwrites one file in place with the given passes (0=zeros, 1=ones, 2=random), then renames + deletes it.
	private void ShredOne(string path, int[] fills, long[] acc)
	{
		try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
		int[] passes = fills.Length == 0 ? new[] { 0 } : fills;
		using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
		{
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
		try
		{
			string dir = Path.GetDirectoryName(path) ?? "";
			string masked = Path.Combine(dir, Guid.NewGuid().ToString("N"));
			File.Move(path, masked);
			File.Delete(masked);
		}
		catch { try { File.Delete(path); } catch { } }
	}

	// Quick-format the selected drive (erases everything). Choose NTFS or exFAT. System disk is protected.
	private async void FormatDrive_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (!(DiskBox.SelectedItem is DiskItem disk)) { MessageBox.Show(L("Mb042"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		if (disk.IsSystem) { MessageBox.Show(L("Mb043"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return; }
		if (!IsAdministrator()) { MessageBox.Show(L("Mb044"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }

		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("MbFormatConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
				L("MbFormatTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
			return;

		string[] fsOptions = { L("AmFsNtfs"), L("AmFsExfat"), L("AmFsFat32") };
		int? fsSel = ShowActionMenu(L("AmFormatTitle"), string.Format(L("AmFormatPrompt"), disk.Number), fsOptions,
			new[] { 0xEDA2, 0xEDA2, 0xEDA2 }, null, 0);
		if (fsSel == null) return;
		string fs = fsSel.Value == 0 ? "ntfs" : fsSel.Value == 1 ? "exfat" : "fat32";

		string scriptPath = Path.Combine(Path.GetTempPath(), $"winforge-format-{Guid.NewGuid():N}.txt");
		try
		{
			SetBusy(busy: true, string.Format(L("BzFormat"), disk.Number, fs.ToUpperInvariant()));
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

	private bool GuardSystemDisk(DiskItem disk)
	{
		if (disk.IsSystem) { MessageBox.Show(L("PtSystemBlocked"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Hand); return false; }
		return true;
	}

	private async Task<bool> ConfirmDestructive(DiskItem disk, string action)
	{
		string contents = await GetDiskContentsAsync(disk.Number);
		return MessageBox.Show(string.Format(L("PtConfirmBody"), action, disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents),
			"DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
	}

	private async void PartitionTool_Click(object sender, RoutedEventArgs e)
	{
		if (isBusy) { MessageBox.Show(L("MsgBusyWait"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
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
		if (!await ConfirmDestructive(disk, L("PtQuickPart"))) return;
		string style = disk.Size > 2L * 1024 * 1024 * 1024 * 1024 || disk.PartitionStyle?.Equals("GPT", StringComparison.OrdinalIgnoreCase) == true ? "gpt" : "mbr";
		var sb = new StringBuilder();
		sb.Append($"select disk {disk.Number}\r\nclean\r\nconvert {style}\r\n");
		long usableMb = disk.Size / (1024 * 1024) - 200;
		long each = usableMb / n;
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
			if (MessageBox.Show(string.Format(L("PtShrinkConfirm"), letter, mb), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
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
			if (MessageBox.Show(string.Format(L("PtGrowConfirm"), letter), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
			cmd = $"select volume {letter}\r\n{extend}\r\nexit\r\n";
			working = string.Format(L("PtResizeWorking"), letter);
		}

		try
		{
			SetBusy(busy: true, working);
			string outp = await RunDiskpartAsync(cmd);
			SetToolOutput("diskpart resize\r\n\r\n" + outp);
			Log($"Resize volume {letter}: ({(mode.Value == 1 ? "shrink" : "extend")}).");
			await RefreshDisksAsync();
			bool ok = outp.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0;
			MessageBox.Show((ok ? string.Format(L("PtResizeDone"), letter) : L("PtResizeFailed")) + "\r\n\r\n" + (outp.Length > 600 ? outp.Substring(outp.Length - 600) : outp),
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
		if (newStart < 2048 || newStart + p.Sectors > total) { MessageBox.Show(L("MvOutOfRange"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		foreach (var o in entries)
		{
			if (o.Index == p.Index) continue;
			if (newStart < o.StartLBA + o.Sectors && newStart + p.Sectors > o.StartLBA)
			{ MessageBox.Show(L("MvOverlap"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		}

		if (MessageBox.Show(string.Format(L("MvConfirm"), pick.Value + 1, p.Fs + " " + FormatBytes(p.Sectors * 512L), FormatBytes(p.StartLBA * 512L), FormatBytes(newStart * 512L)),
				L("MvTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

		bool moved = false;
		try
		{
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
				L("MvTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

		bool moved = false;
		try
		{
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
		if (amt.Length == 0) sizeClause = "";
		else if (long.TryParse(amt, out long mb) && mb > 0) sizeClause = $" size={mb}";
		else { MessageBox.Show(L("PtBadAmount"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Exclamation); return; }
		int? fsSel = ShowActionMenu(L("PtCreate"), L("PtFsPrompt"), new[] { "NTFS", "exFAT", "FAT32" },
			new[] { 0xEDA2, 0xEDA2, 0xEDA2 }, null, 0);
		if (fsSel == null) return;
		string fs = fsSel.Value == 0 ? "ntfs" : fsSel.Value == 1 ? "exfat" : "fat32";
		if (MessageBox.Show(string.Format(L("PtCreateConfirm"), disk.Number), L("PtCreate"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtCreate")));
			string outp = await RunDiskpartAsync($"select disk {disk.Number}\r\ncreate partition primary{sizeClause}\r\nformat fs={fs} quick label=DriveForge\r\nassign\r\nexit\r\n");
			SetToolOutput("diskpart create partition\r\n\r\n" + outp);
			Log($"Created partition on Disk {disk.Number} ({fs}).");
			await RefreshDisksAsync();
			bool ok = outp.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0;
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
		if (MessageBox.Show(string.Format(L("PtDeleteConfirm"), letter), L("PtDelete"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
		try
		{
			SetBusy(busy: true, string.Format(L("PtWorking"), L("PtDelete")));
			string outp = await RunDiskpartAsync($"select volume {letter}\r\ndelete volume override\r\nexit\r\n");
			SetToolOutput("diskpart delete volume\r\n\r\n" + outp);
			Log($"Deleted volume {letter}: on Disk {disk.Number}.");
			await RefreshDisksAsync();
			bool ok = outp.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) >= 0;
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
		if (MessageBox.Show(string.Format(L("PtConvertConfirm"), disk.Number, disk.PartitionStyle, target.ToUpperInvariant()), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
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
			MessageBox.Show(string.Format(L("PtCheckDone"), letter) + "\r\n\r\n" + tail, "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
		}
		catch (Exception ex) { ShowError(L("ErrCheck"), ex); }
		finally { SetBusy(busy: false); }
	}

	private async Task SetActiveFlow(DiskItem disk)
	{
		if (!GuardSystemDisk(disk)) return;
		if (disk.PartitionStyle?.Equals("GPT", StringComparison.OrdinalIgnoreCase) == true) { MessageBox.Show(L("PtActiveGpt"), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
		if (MessageBox.Show(string.Format(L("PtActiveConfirm"), disk.Number), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
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
			var mounted = new HashSet<char>(disk.DriveLetters ?? Array.Empty<char>());
			var found = await Task.Run(() => ScanPhysicalForPartitions(disk.Number, disk.Size, p => Dispatcher.Invoke(() => ProgressBar.Value = p)));
			ProgressBar.Value = 100.0;
			SetBusy(busy: false);
			if (found.Count == 0) { MessageBox.Show(string.Format(L("PtLostNone"), disk.Number), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information); return; }
			var sb = new StringBuilder();
			foreach (var p in found)
				sb.Append($"• {p.Fs} @ {FormatBytes(p.Offset)} — {FormatBytes(p.Bytes)}{(string.IsNullOrEmpty(p.Label) ? "" : " — \"" + p.Label + "\"")} — {(p.Offset == 0 ? L("PtLostMounted") : L("PtLostUnmounted"))}\r\n");
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
		string contents = await GetDiskContentsAsync(disk.Number);
		if (MessageBox.Show(string.Format(L("SsdConfirm"), disk.Number, disk.FriendlyName, FormatBytes(disk.Size), contents), "DriveForge", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
		try
		{
			SetBusy(busy: true, string.Format(L("SsdWorking"), disk.Number));
			await RunDiskpartAsync($"select disk {disk.Number}\r\nclean\r\nconvert gpt\r\ncreate partition primary\r\nformat fs=ntfs quick label=DriveForge\r\nassign\r\nexit\r\n");
			await RefreshDisksAsync();
			var d2 = disks.FirstOrDefault(x => x.Number == disk.Number);
			char letter = d2?.DriveLetters?.FirstOrDefault() ?? '\0';
			string outp = "";
			if (letter != '\0')
				outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument($"Optimize-Volume -DriveLetter {letter} -ReTrim -Verbose"));
			SetToolOutput("SSD erase (clean + quick format + ReTrim)\r\n\r\n" + outp);
			Log($"SSD erase (TRIM) on Disk {disk.Number}.");
			MessageBox.Show(string.Format(L("SsdDone"), disk.Number), "DriveForge", MessageBoxButton.OK, MessageBoxImage.Information);
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
		string outp = await RunProcessCaptureAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(script));
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

	// Flushes the file-system write cache of the given drive letters so the drive is safe to unplug
	// immediately after a clone (no programmatic eject, just a clean flush). Best-effort.
	private async Task FlushVolumesAsync(params char[] letters)
	{
		foreach (char letter in letters)
		{
			if (letter == '\0') continue;
			await RunProcessAsync("powershell.exe",
				"-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument($"Write-VolumeCache -DriveLetter {letter}"),
				allowFailure: true);
		}
	}

	private void SetStage(string text, double progress)
	{
		StatusText.Text = text;
		ProgressBar.Value = Math.Max(0.0, Math.Min(100.0, progress));
		Log(text);
	}

	private void SetBusy(bool busy, string? status = null)
	{
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
		if (creatingFiles.Success && double.TryParse(creatingFiles.Groups["percent"].Value, out double createPercent))
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
		if (applied.Success && double.TryParse(applied.Groups["percent"].Value, out double percent))
		{
			ProgressBar.Value = Math.Max(20.0, Math.Min(82.0, percent));
			UpdateProgressStats();
		}
	}

	private static double ConvertToGiB(string value, string unit)
	{
		if (!double.TryParse(value, out double number))
		{
			return 0.0;
		}
		return unit.Equals("MiB", StringComparison.OrdinalIgnoreCase) ? number / 1024.0 : number;
	}

	private void UpdateProgressStats()
	{
		TimeSpan elapsed = operationStopwatch.Elapsed;
		// Indeterminate operations (no measurable %, e.g. file-system scan or FFU apply): show a ticking
		// Elapsed clock with "—%" instead of a misleading fixed percentage, and skip the byte/ETA maths.
		if (ProgressBar.IsIndeterminate)
		{
			ProgressStatsText.Text = string.Format(L("ProgStats"), "—", "", elapsed.ToString(@"hh\:mm\:ss"), "--:--:--");
			ProgressPercentText.Text = "";
			if (TaskbarInfo != null)
				TaskbarInfo.ProgressState = isBusy ? System.Windows.Shell.TaskbarItemProgressState.Indeterminate : System.Windows.Shell.TaskbarItemProgressState.None;
			return;
		}
		double percent = Math.Max(0.0, Math.Min(100.0, ProgressBar.Value));
		double currentGiB = progressDoneGiB;

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
		if (progressSpeedMb > 0.5 && progressTotalGiB > currentGiB)
		{
			double remainingSeconds = (progressTotalGiB - currentGiB) * 1024.0 / progressSpeedMb;
			remaining = TimeSpan.FromSeconds(Math.Max(0.0, remainingSeconds)).ToString(@"hh\:mm\:ss");
		}
		else if (elapsed.TotalSeconds > 20.0 && percent > 1.0 && percent < 99.0)
		{
			double remainingSeconds = elapsed.TotalSeconds * (100.0 - percent) / percent;
			remaining = TimeSpan.FromSeconds(Math.Max(0.0, remainingSeconds)).ToString(@"hh\:mm\:ss");
		}

		// Drive the ProgressBar from byte progress when a data copy is active.
		// Maps 0–100% data fraction to the 40%–82% bar band (pre/post phases use 0–40 and 82–100).
		// Only advances the bar, never retreats, so pre-copy phase values are not reset.
		// Threshold of 0.3 GiB: a freshly-formatted NTFS target already reports ~0.1 GiB used (MFT/metadata),
		// which is NOT copied data. Don't jump the bar to the 40% write-band until real writing has begun.
		if (currentGiB > 0.3 && progressTotalGiB > 0.5 && isBusy)
		{
			// Expand total estimate if actuals exceed projection (VSS/hardlink inflation) — but NOT for a fixed-total
			// op like a full-disk deep scan, where inflating would freeze the bar near 97%.
			if (!_progressFixedTotal && currentGiB >= progressTotalGiB * 0.97)
				progressTotalGiB = currentGiB * 1.12; // push ceiling 12% ahead of current position
			double frac = Math.Min(1.0, currentGiB / progressTotalGiB);
			double barTarget = _progressFullRange ? frac * 100.0 : 40.0 + frac * 42.0;
			if (barTarget > ProgressBar.Value)
				ProgressBar.Value = barTarget;
		}

		// Show "X.X / Y.Y GiB" when a data copy is active — gives concrete progress context
		string sizeInfo = progressTotalGiB > 0.5 && currentGiB > 0.3
			? $" ({currentGiB:F1} / {progressTotalGiB:F1} GiB)"
			: string.Empty;

		// Show GB/s for fast drives (NVMe, USB4), plain MB/s otherwise
		string speedText = progressSpeedMb >= 1024.0
			? $"{progressSpeedMb / 1024.0:F2} GB/s"
			: progressSpeedMb > 0.5
				? $"{progressSpeedMb:F0} MB/s"
				: "--";

		ProgressStatsText.Text = string.Format(L("ProgStats"), percent.ToString("F1"), sizeInfo, elapsed.ToString(@"hh\:mm\:ss"), remaining);
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
	}

	private static bool IsAdministrator()
	{
		using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
		return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
	}

	private static string QuoteArgument(string value)
	{
		return "\"" + value.Replace("\"", "\\\"") + "\"";
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
			string fallbackDir = bootLetter + ":\\EFI\\Boot";
			string fallback = Path.Combine(fallbackDir, "bootx64.efi");
			if (File.Exists(fallback)) return true;
			string source = bootLetter + ":\\EFI\\Microsoft\\Boot\\bootmgfw.efi";
			if (!File.Exists(source)) return false;
			Directory.CreateDirectory(fallbackDir);
			File.Copy(source, fallback, overwrite: true);
			return File.Exists(fallback);
		}
		catch
		{
			return false;
		}
	}

	// First-boot answer file (a standard unattended-setup approach). For a faithful clone the OS is already
	// past OOBE, so this is a safety net: it skips OOBE if it ever runs (e.g. a reset profile), and during
	// any specialize pass it re-asserts the WinToGo essentials — keep host disks offline (SanPolicy=4) and
	// preserve all device installs so moving the stick between PCs does not strip drivers for absent devices.
	private static string BuildPortableUnattendXml(string localAccountName = "", string localAccountPassword = "")
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
			"    <component name=\"Microsoft-Windows-PartitionManager\" processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
			"      <SanPolicy>4</SanPolicy>\r\n" +
			"    </component>\r\n" +
			"    <component name=\"Microsoft-Windows-PnpSysprep\" processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
			"      <PersistAllDeviceInstalls>true</PersistAllDeviceInstalls>\r\n" +
			"      <DoNotCleanUpNonPresentDevices>true</DoNotCleanUpNonPresentDevices>\r\n" +
			"    </component>\r\n" +
			"  </settings>\r\n" +
			"  <settings pass=\"oobeSystem\">\r\n" +
			"    <component name=\"Microsoft-Windows-Shell-Setup\" processorArchitecture=\"amd64\" publicKeyToken=\"31bf3856ad364e35\" language=\"neutral\" versionScope=\"nonSxS\" xmlns:wcm=\"http://schemas.microsoft.com/WMIConfig/2002/State\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
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
		string xml = BuildPortableUnattendXml(localAccountName, localAccountPassword);
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
		string text = output.Trim();
		int num = text.IndexOf('[');
		int num2 = text.IndexOf('{');
		int num3 = ((num >= 0 && num2 >= 0) ? Math.Min(num, num2) : Math.Max(num, num2));
		if (num3 < 0)
		{
			return "[]";
		}
		return text.Substring(num3);
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
