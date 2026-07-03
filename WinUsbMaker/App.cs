using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WinUsbMaker;

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
		app.Run();
	}

	// Persist the crash so a user can send it to us, then show a friendly dialog instead of a hard close.
	private static void ReportCrash(string source, Exception? ex, bool terminating)
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
		try
		{
			string msg = "DriveForge hit an unexpected error and had to stop the current operation.";
			if (path.Length > 0) msg += "\n\nA crash log was saved to:\n" + path;
			msg += "\n\nDetails:\n" + (ex?.Message ?? "unknown error");
			MessageBox.Show(msg, "DriveForge", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		catch { }
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
