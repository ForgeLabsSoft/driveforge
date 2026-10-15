using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DriveForge;

public class App : Application
{
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.8.0")]
	public void InitializeComponent()
	{
		base.StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
	}

	[STAThread]
	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.8.0")]
	public static void Main()
	{
		App app = new App();
		app.InitializeComponent();
		// Global safety net: a stray exception on the UI thread, a background task, or a native callback should
		// leave the user with a saved crash log and a clear message instead of a silent disappear mid-operation.
		app.DispatcherUnhandledException += app.OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		// Exit fires once the last window has closed and the app is on its way out. From then on there is no
		// "current operation" left to interrupt, so a fault thrown by the runtime's own teardown must not be
		// dressed up as one - see ReportCrash.
		app.Exit += (_, __) => System.Threading.Interlocked.Exchange(ref _shuttingDown, 1);
		app.Run();
	}

	// Reentrancy guard: a fault raised WHILE we are handling a fault (e.g. the dialog itself throws) must not loop.
	private static int _reporting;
	// Set when Application.Exit has fired: the app is closing and nothing of the user's is in flight.
	private static int _shuttingDown;

	// Persist the crash so a user can send it to us, then show a friendly dialog instead of a hard close.
	private static void ReportCrash(string source, Exception? ex, bool terminating)
	{
		if (System.Threading.Interlocked.Exchange(ref _reporting, 1) == 1) return; // already reporting a crash
		try
		{
			string details = ex?.ToString() ?? "(no exception object)";
			string path = "";
			try
			{
				string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveForge");
				Directory.CreateDirectory(dir);
				path = Path.Combine(dir, "crash.log");
				string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
				File.AppendAllText(path, "==== " + stamp + "  [" + source + "]  terminating=" + terminating + " ====" + Environment.NewLine + details + Environment.NewLine + Environment.NewLine, Encoding.UTF8);
			}
			catch { }
			// The LOG is written for every fault, always. The DIALOG is not. Once the app is on its way out there
			// is no operation left to interrupt, nothing for the person to act on, and no window to put a modal
			// over - and that is also the one moment a modal can hang the exit, because the UI pump may be gone.
			// A user met exactly this: the clean-traces sweep had emptied the single-file extraction folder this
			// process was RUNNING from, so a mixed-mode module's CRT teardown threw DllNotFoundException during
			// AppDomain unload, and what they were shown was "had to stop the current operation" - when the
			// operation had finished and the window was already closed. The sweep no longer does that (SafeFiles
			// skips the extraction base); this makes sure a teardown fault is never reported as a lost operation.
			// ONE condition, and it is the one that was measured: Application.Exit has fired. Environment.
			// HasShutdownStarted was in here too, and it came out - it is NOT documented to be false inside this
			// handler for a FATAL exception (IsTerminating=true), which is a genuine mid-operation crash that must
			// still be shown. A condition that could hide a real crash, and that I could not measure, is worse
			// than the one line it saves.
			if (_shuttingDown == 1) return;
			try
			{
				string msg = "DriveForge hit an unexpected error and had to stop the current operation.";
				if (path.Length > 0) msg += "\n\nA crash log was saved to:\n" + path;
				msg += "\n\nDetails:\n" + (ex?.Message ?? "unknown error");
				// The crash log is the single most useful thing a user can send, and this dialog is the only
				// moment they know it exists. Name the channels here rather than hoping they find Settings later.
				msg += "\n\nYou can report this at https://github.com/ForgeLabsSoft/driveforge/issues";
				// Only point at the log if one was actually written — the write above is best-effort and swallows
				// its own failure (full disk, locked profile), and telling someone to attach a file that does not
				// exist wastes the one report they were willing to send.
				msg += path.Length > 0
					? "\nor email the crash log above to support@forgelabssoft.com."
					: "\nor email support@forgelabssoft.com with what you were doing.";
				msg += "\n\nNothing is submitted until you send it yourself.";
				// These handlers can fire on a background/finalizer thread. Marshal the dialog to the UI thread
				// (unless the process is already tearing down, where the UI pump may be gone and Invoke would hang).
				Application? app = Current;
				if (!terminating && app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
					// BeginInvoke (non-blocking): a blocking Invoke here would stall the calling thread — and this can
					// fire on the finalizer thread (UnobservedTaskException) — freezing ALL finalizers (Process/FileStream/
					// RegistryKey handles never released) for as long as the modal sits open.
					app.Dispatcher.BeginInvoke(new Action(() => MessageBox.Show(msg, "DriveForge", MessageBoxButton.OK, MessageBoxImage.Error)));
				else
					MessageBox.Show(msg, "DriveForge", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			catch { }
		}
		finally { System.Threading.Interlocked.Exchange(ref _reporting, 0); }
	}

	private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		ReportCrash("UI", e.Exception, terminating: false);
		e.Handled = true; // keep the app alive; the failed operation is already aborted
	}

	private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		ReportCrash("AppDomain", e.ExceptionObject as Exception, e.IsTerminating);
	}

	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		ReportCrash("Task", e.Exception, terminating: false);
		e.SetObserved(); // don't let an ignored background task tear down the process
	}
}
