using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinUsbMaker;

// UI customization: multi-language localization + live theming (accent colour + base theme).
// Kept in a separate partial-class file so the cloning engine in MainWindow.cs is untouched.
public partial class MainWindow
{
	private sealed record LanguageOption(string Code, string Display);

	private static readonly LanguageOption[] Languages = new[]
	{
		new LanguageOption("en", "English"),
		new LanguageOption("ro", "Romana"),
		new LanguageOption("de", "Deutsch"),
		new LanguageOption("fr", "Francais"),
		new LanguageOption("es", "Espanol"),
		new LanguageOption("it", "Italiano"),
		new LanguageOption("pt", "Portugues"),
		new LanguageOption("nl", "Nederlands"),
		new LanguageOption("ru", "Russkij"),
		new LanguageOption("pl", "Polski"),
		new LanguageOption("tr", "Turkce"),
		new LanguageOption("uk", "Ukrayinska"),
		new LanguageOption("zh", "中文"),
		new LanguageOption("ja", "日本語"),
		new LanguageOption("hi", "हिन्दी"),
		new LanguageOption("id", "Indonesia"),
		new LanguageOption("ar", "العربية"),
	};

	// Accent presets: label + hex.
	private static readonly (string Name, string Hex)[] AccentPresets = new[]
	{
		("Blue", "2563EB"), ("Indigo", "4F46E5"), ("Violet", "7C3AED"), ("Fuchsia", "C026D3"),
		("Pink", "DB2777"), ("Red", "DC2626"), ("Orange", "EA580C"), ("Amber", "D97706"),
		("Emerald", "059669"), ("Teal", "0D9488"), ("Cyan", "0891B2"), ("Sky", "0284C7"),
	};

	private string currentLanguage = "en";
	private bool uiCustomizationReady;

	private string SettingsFilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"DriveForge", "ui-settings.ini");

	private void InitializeUiCustomization()
	{
		// Language dropdown
		LanguageBox.Items.Clear();
		foreach (LanguageOption lang in Languages)
		{
			LanguageBox.Items.Add(lang.Display);
		}

		// Accent swatches
		AccentPresetsPanel.Children.Clear();
		foreach ((string name, string hex) in AccentPresets)
		{
			Button swatch = new Button
			{
				Width = 40,
				Height = 40,
				Margin = new Thickness(0, 0, 8, 8),
				Tag = hex,
				ToolTip = name,
				Background = new SolidColorBrush(HexToColor(hex)),
				BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
			};
			swatch.Click += AccentPreset_Click;
			AccentPresetsPanel.Children.Add(swatch);
		}

		(string savedLang, string? savedAccent, string? savedBase, string savedTheme) = LoadSettings();
		int langIndex = Array.FindIndex(Languages, l => l.Code == savedLang);
		uiCustomizationReady = true;
		LanguageBox.SelectedIndex = langIndex >= 0 ? langIndex : 0;
		ApplyAppTheme(savedTheme, persist: false);
		// Base-theme presets are dark variants — only apply them when not in Light mode.
		if (!IsLightTheme(savedTheme) && !string.IsNullOrEmpty(savedBase)) { ApplyBaseTheme(HexToColor(savedBase!), persist: false); }
		if (!string.IsNullOrEmpty(savedAccent)) { ApplyAccent(HexToColor(savedAccent!), persist: false); }
	}

	private string currentThemeMode = "dark";

	private void Theme_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button b && b.Tag is string mode) { ApplyAppTheme(mode, persist: true); }
	}

	private static bool SystemUsesLightTheme()
	{
		try
		{
			using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
			return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
		}
		catch { return false; }
	}

	private bool IsLightTheme(string mode) => mode == "light" || (mode == "system" && SystemUsesLightTheme());

	// Swaps the whole color palette for Dark or Light (System follows Windows).
	private void ApplyAppTheme(string mode, bool persist = true)
	{
		currentThemeMode = (mode == "light" || mode == "system") ? mode : "dark";
		bool light = IsLightTheme(currentThemeMode);
		if (light)
		{
			MutateBrush("NavyBrush", HexToColor("EEF2F7"));
			MutateBrush("PanelBrush", HexToColor("FFFFFF"));
			MutateBrush("PanelAltBrush", HexToColor("F1F5F9"));
			MutateBrush("TextBrush", HexToColor("0F172A"));
			MutateBrush("MutedBrush", HexToColor("475569"));
			MutateBrush("BoxBrush", HexToColor("E8EEF5"));
			MutateBrush("DividerBrush", HexToColor("CBD5E1"));
			MutateBrush("Border2Brush", HexToColor("D1D5DB"));
		}
		else
		{
			MutateBrush("NavyBrush", HexToColor("0F172A"));
			MutateBrush("PanelBrush", HexToColor("111827"));
			MutateBrush("PanelAltBrush", HexToColor("172033"));
			MutateBrush("TextBrush", HexToColor("F8FAFC"));
			MutateBrush("MutedBrush", HexToColor("CBD5E1"));
			MutateBrush("BoxBrush", HexToColor("0B1220"));
			MutateBrush("DividerBrush", HexToColor("1E3A5F"));
			MutateBrush("Border2Brush", HexToColor("334155"));
		}
		if (persist) { SaveSettings(); }
	}

	// ---------- Localization ----------

	private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!uiCustomizationReady || LanguageBox.SelectedIndex < 0) { return; }
		currentLanguage = Languages[LanguageBox.SelectedIndex].Code;
		ApplyLanguage(currentLanguage);
		SaveSettings();
	}

	// Looks up a localized string for the current language, falling back to English then the key itself.
	private string L(string key)
	{
		if (Strings.TryGetValue(currentLanguage, out Dictionary<string, string>? d) && d.TryGetValue(key, out string? v))
			return v;
		if (Strings["en"].TryGetValue(key, out string? e))
			return e;
		return key;
	}

	private void ApplyLanguage(string lang)
	{
		if (!Strings.TryGetValue(lang, out Dictionary<string, string>? dict))
		{
			dict = Strings["en"];
		}
		Dictionary<string, string> fallback = Strings["en"];
		foreach (string key in fallback.Keys)
		{
			string text = dict.TryGetValue(key, out string? v) ? v : fallback[key];
			object? control = FindName(key);
			switch (control)
			{
				case TextBlock tb: tb.Text = text; break;
				case CheckBox cb: cb.Content = text; break;
				case System.Windows.Controls.Expander ex: ex.Header = text; break;
				case System.Windows.Controls.TabItem ti: ti.Header = text; break;
				case System.Windows.Controls.GroupBox gb: gb.Header = text; break;
				case Button btn: btn.Content = text; break;
			}
		}
		// Right-to-left languages (Arabic) flip the whole layout.
		FlowDirection = lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
		// Re-apply the texts that are set dynamically in code so they follow the language too.
		RefreshLocalizedDynamicText();
		ApplyAccessibilityNames();
		ApplyToolTips();
		RefreshCleanLabels(); // data-driven clean categories follow the language too
	}

	// Localizes hover tooltips. ApplyLanguage only sets .Text/.Content/.Header, never .ToolTip, so the
	// tooltips are handled here: each named control gets its tooltip from the "<Name>Tip" key. Controls
	// whose key isn't translated yet fall back to English via L(); a missing element is skipped.
	private static readonly string[] _toolTipNames =
	{
		"HelpButton", "SettingsButton", "VerifyIsoButton", "DiskBox", "EjectDriveButton",
		"BypassRequirementsCheck", "BypassAccountCheck", "DebloatCheck", "AddNetworkDriversCheck",
		"AddAllDriversCheck", "BitLockerCheck", "BitLockerResumeCheck", "DataPartitionCheck",
		"CloneOtherPartitionsCheck", "VerifyContentCheck", "CompactImageCheck", "EjectWhenDoneCheck",
		"ScheduleCloneButton", "HealthToolButton", "SpeedToolButton", "ScanToolButton", "SurfaceToolButton",
		"CapacityToolButton", "FormatToolButton", "PartitionToolButton", "TestBootToolButton",
		"WipeToolButton", "ShredToolButton", "DiagDiskBox", "RecoverDeepScanButton", "RecoverMoreButton",
		"RecoverZipButton", "DonateButton", "AnalyzeMasterBox", "AnalyzeSimilarButton", "AnalyzeGalleryButton",
		"RecoverRecycleButton", "RecoverGalleryButton",
	};

	private void ApplyToolTips()
	{
		var en = Strings["en"];
		foreach (string name in _toolTipNames)
		{
			if (FindName(name) is System.Windows.FrameworkElement el)
			{
				string key = name + "Tip";
				// Only override if we actually have a string for it (keeps the XAML English tooltip otherwise).
				if (en.ContainsKey(key)) el.ToolTip = L(key);
			}
		}
	}

	// Gives icon-only buttons and label-less inputs a localized screen-reader name (AutomationProperties.Name),
	// so the app is usable with Narrator / NVDA. Re-runs on language change so the names stay localized.
	private void ApplyAccessibilityNames()
	{
		void Set(System.Windows.DependencyObject? c, string key)
		{
			if (c != null) System.Windows.Automation.AutomationProperties.SetName(c, L(key));
		}
		Set(FindName("HelpButton") as System.Windows.DependencyObject, "A11yHelp");
		Set(FindName("SettingsButton") as System.Windows.DependencyObject, "SbSettingsT");
		Set(FindName("NavCreate") as System.Windows.DependencyObject, "SbCreateT");
		Set(FindName("NavClonePortable") as System.Windows.DependencyObject, "SbClonePT");
		Set(FindName("NavCloneInternal") as System.Windows.DependencyObject, "SbCloneIT");
		Set(FindName("NavBackup") as System.Windows.DependencyObject, "SbBackupT");
		Set(FindName("NavRestore") as System.Windows.DependencyObject, "SbRestoreT");
		Set(FindName("NavLinux") as System.Windows.DependencyObject, "SbLinuxT");
		Set(FindName("NavDownloadIso") as System.Windows.DependencyObject, "SbDownloadT");
		Set(FindName("NavMultiBoot") as System.Windows.DependencyObject, "SbMultiBootT");
		Set(FindName("NavTools") as System.Windows.DependencyObject, "SbToolsT");
		Set(FindName("NavRecover") as System.Windows.DependencyObject, "SbRecoverT");
		Set(FindName("NavClean") as System.Windows.DependencyObject, "SbCleanT");
		Set(FindName("RecoverVolumeBox") as System.Windows.DependencyObject, "RecVolumeLabel");
		Set(FindName("RecoverSearchBox") as System.Windows.DependencyObject, "A11ySearch");
		Set(FindName("RecoverTypeBox") as System.Windows.DependencyObject, "A11yTypeFilter");
		Set(FindName("RecoverGrid") as System.Windows.DependencyObject, "RecPanelTitle");
		Set(FindName("AnalyzePathBox") as System.Windows.DependencyObject, "AnalyzeFolderLabel");
		Set(FindName("AnalyzeTreemap") as System.Windows.DependencyObject, "A11yTreemap");
		Set(FindName("BigFilesGrid") as System.Windows.DependencyObject, "AnalyzeBigFilesTab");
		Set(FindName("DupesGrid") as System.Windows.DependencyObject, "AnalyzeDupesTab");
		Set(FindName("IsoUrlBox") as System.Windows.DependencyObject, "DlUrlHint");
		Set(FindName("DistroBox") as System.Windows.DependencyObject, "DlAutoLabel");
	}

	// Re-applies localized text for controls whose content is chosen at runtime (mode descriptions, Start
	// button, drive verdict, readiness hint). Safe to call after the window is built.
	private void RefreshLocalizedDynamicText()
	{
		if (ModeBox == null) return;
		int mode = ModeBox.SelectedIndex;
		bool cloneMode = mode == ModeCloneCurrentWindows || mode == ModeCloneInternal;
		bool backupMode = mode == ModeBackupImage;
		bool isoWriteMode = mode == ModeWriteIsoImage;
		bool restoreMode = mode == ModeRestoreSavedClone;
		if (SourceHelpText != null)
		{
			SourceHelpText.Text = isoWriteMode ? L("DescIsoWrite") : backupMode ? L("DescBackup") : cloneMode ? L("DescClone")
				: restoreMode ? L("DescRestore") : L("DescInstall");
		}
		// Step-2 heading: keep the per-mode override alive after a language switch (the generic FindName loop resets it).
		if (Step2Title != null) Step2Title.Text = isoWriteMode ? L("Step2IsoImage") : restoreMode ? L("Step2ImageFile") : L("Step2Title");
		if (BootModeText != null) BootModeText.Text = L("BootModeText");
		if (StartButton != null)
		{
			StartButton.Content = backupMode ? L("StartBackup") : cloneMode ? L("StartClone")
				: mode == ModeRestoreSavedClone ? L("StartRestore") : L("StartInstall");
		}
		UpdateDriveVerdict();
		UpdateStartReadiness();
		// SMART table: localize the column headers, and relabel its rows in the new language if a
		// report is already loaded (BuildSmartRows is side-effect-free, so this is safe to re-run).
		if (SmartGrid != null && SmartGrid.Columns.Count >= 3)
		{
			SmartGrid.Columns[0].Header = L("SmColAttr");
			SmartGrid.Columns[1].Header = L("SmColValue");
			SmartGrid.Columns[2].Header = L("SmColStatus");
			if (_diagDisk != null && _diagReport != null) SmartGrid.ItemsSource = BuildSmartRows(_diagDisk, _diagReport);
		}
		// Recover-deleted-files table: localize its column headers (the row Status text is localized at scan time).
		if (RecoverGrid != null && RecoverGrid.Columns.Count >= 6)
		{
			RecoverGrid.Columns[0].Header = L("RfColRecover");
			RecoverGrid.Columns[1].Header = L("RfColName");
			RecoverGrid.Columns[2].Header = L("RfColFolder");
			RecoverGrid.Columns[3].Header = L("RfColSize");
			RecoverGrid.Columns[4].Header = L("RfColModified");
			RecoverGrid.Columns[5].Header = L("RfColStatus");
		}
		// Disk-space analyzer grids.
		if (BigFilesGrid != null && BigFilesGrid.Columns.Count >= 5)
		{
			BigFilesGrid.Columns[0].Header = L("AnDel");
			BigFilesGrid.Columns[1].Header = L("AnFileName");
			BigFilesGrid.Columns[2].Header = L("AnFolder");
			BigFilesGrid.Columns[3].Header = L("AnModified");
			BigFilesGrid.Columns[4].Header = L("AnSize");
		}
		if (DupesGrid != null && DupesGrid.Columns.Count >= 6)
		{
			DupesGrid.Columns[0].Header = L("AnDel");
			DupesGrid.Columns[1].Header = L("AnGroup");
			DupesGrid.Columns[2].Header = L("AnFileName");
			DupesGrid.Columns[3].Header = L("AnFolder");
			DupesGrid.Columns[4].Header = L("AnModified");
			DupesGrid.Columns[5].Header = L("AnSize");
		}
		// Recover "⋯ More" overflow menu items (ContextMenu is a separate namescope, so set by index).
		if (RecoverMoreButton?.ContextMenu != null && RecoverMoreButton.ContextMenu.Items.Count >= 5)
		{
			if (RecoverMoreButton.ContextMenu.Items[0] is System.Windows.Controls.MenuItem mi0) mi0.Header = L("CreateImageButton");
			if (RecoverMoreButton.ContextMenu.Items[1] is System.Windows.Controls.MenuItem mi1) mi1.Header = L("OpenImageButton");
			if (RecoverMoreButton.ContextMenu.Items[3] is System.Windows.Controls.MenuItem mi3) mi3.Header = L("RecoverSaveSessionButton");
			if (RecoverMoreButton.ContextMenu.Items[4] is System.Windows.Controls.MenuItem mi4) mi4.Header = L("RecoverOpenSessionButton");
		}
	}

	// ---------- Theming ----------

	private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Visible;
	private void SettingsClose_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

	private void AccentPreset_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button b && b.Tag is string hex) { ApplyAccent(HexToColor(hex)); }
	}

	private void BaseTheme_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button b && b.Tag is string hex) { ApplyBaseTheme(HexToColor(hex)); }
	}

	private void ColorSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		// Fires during XAML load as each slider gets its initial value — the other sliders and the
		// preview brush may not exist yet, so null-guard every field before touching them.
		if (CustomPreviewBrush == null || RedSlider == null || GreenSlider == null || BlueSlider == null) { return; }
		CustomPreviewBrush.Color = Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
	}

	private void ApplyCustomAccent_Click(object sender, RoutedEventArgs e)
	{
		ApplyAccent(Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value));
	}

	private void ResetTheme_Click(object sender, RoutedEventArgs e)
	{
		ApplyAccent(HexToColor("2563EB"));
		ApplyBaseTheme(HexToColor("0F172A"));
	}

	private void ApplyAccent(Color color, bool persist = true)
	{
		MutateBrush("BlueBrush", color);
		if (RedSlider != null) { RedSlider.Value = color.R; GreenSlider.Value = color.G; BlueSlider.Value = color.B; }
		if (CustomPreviewBrush != null) { CustomPreviewBrush.Color = color; }
		if (persist) { SaveSettings(); }
	}

	private void ApplyBaseTheme(Color baseColor, bool persist = true)
	{
		// Derive panel shades a few steps lighter than the base background for depth.
		MutateBrush("NavyBrush", baseColor);
		MutateBrush("PanelBrush", Lighten(baseColor, 0.06));
		MutateBrush("PanelAltBrush", Lighten(baseColor, 0.10));
		HighlightThemeButtons();
		if (persist) { SaveSettings(); }
	}

	// Fills the active Appearance button so the choice (incl. System) is visible even if colors don't change.
	private void HighlightThemeButtons()
	{
		var map = new (Button b, string mode)[] { (ThemeDarkButton, "dark"), (ThemeLightButton, "light"), (ThemeSystemButton, "system") };
		var accent = Resources["BlueBrush"] as Brush;
		foreach (var (b, mode) in map)
		{
			if (b == null) continue;
			bool active = mode == currentThemeMode;
			b.Background = active ? accent : Brushes.Transparent;
			b.BorderBrush = accent;
			b.Foreground = active ? Brushes.White : (Resources["TextBrush"] as Brush);
		}
	}

	private void MutateBrush(string key, Color color)
	{
		// XAML resource brushes get FROZEN once the styles that reference them are sealed, so their .Color
		// can no longer be mutated. Instead replace the resource entry with a fresh brush — every themeable
		// usage references it via DynamicResource, so the swap propagates live across the whole window.
		Resources[key] = new SolidColorBrush(color);
	}

	private static Color Lighten(Color c, double amount)
	{
		byte L(byte v) => (byte)Math.Min(255, v + (255 - v) * amount);
		return Color.FromRgb(L(c.R), L(c.G), L(c.B));
	}

	private static Color HexToColor(string hex)
	{
		hex = hex.TrimStart('#');
		if (hex.Length == 6)
		{
			return Color.FromRgb(
				Convert.ToByte(hex.Substring(0, 2), 16),
				Convert.ToByte(hex.Substring(2, 2), 16),
				Convert.ToByte(hex.Substring(4, 2), 16));
		}
		return Color.FromRgb(0x25, 0x63, 0xEB);
	}

	private static string ColorToHex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

	// ---------- Persistence ----------

	private (string lang, string? accent, string? baseTheme, string theme) LoadSettings()
	{
		try
		{
			if (File.Exists(SettingsFilePath))
			{
				string lang = "en"; string? accent = null; string? baseTheme = null; string theme = "dark";
				foreach (string line in File.ReadAllLines(SettingsFilePath))
				{
					int eq = line.IndexOf('=');
					if (eq <= 0) { continue; }
					string k = line.Substring(0, eq).Trim();
					string val = line.Substring(eq + 1).Trim();
					if (k == "lang") { lang = val; }
					else if (k == "accent") { accent = val; }
					else if (k == "base") { baseTheme = val; }
					else if (k == "theme") { theme = val; }
				}
				return (lang, accent, baseTheme, theme);
			}
		}
		catch { }
		return ("en", null, null, "dark");
	}

	private void SaveSettings()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
			string accent = Resources["BlueBrush"] is SolidColorBrush a ? ColorToHex(a.Color) : "2563EB";
			string baseTheme = Resources["NavyBrush"] is SolidColorBrush n ? ColorToHex(n.Color) : "0F172A";
			File.WriteAllLines(SettingsFilePath, new[]
			{
				"lang=" + currentLanguage,
				"accent=" + accent,
				"base=" + baseTheme,
				"theme=" + currentThemeMode,
			});
		}
		catch { }
	}

	// ---------- Live diagnostic progress ----------

	private DispatcherTimer? liveTestTimer;
	private string liveTestLabel = "";

	// Start an animated progress bar on the Speed tab so the user sees the HDD/USB test is working.
	// The real measurement is a black box, so we ease toward 95% and snap to 100% when it finishes.
	private void StartLiveTest(string label)
	{
		liveTestLabel = label;
		if (LiveTestBar == null) { return; }
		MutateBrushLocal(LiveTestBar, Resources["BlueBrush"] as SolidColorBrush);
		LiveTestBar.Value = 0;
		LiveTestPercentText.Text = label + " 0%";
		liveTestTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
		liveTestTimer.Tick -= LiveTestTick;
		liveTestTimer.Tick += LiveTestTick;
		liveTestTimer.Start();
	}

	private void LiveTestTick(object? sender, EventArgs e)
	{
		if (LiveTestBar == null) { return; }
		double v = LiveTestBar.Value;
		double next = v + (95.0 - v) * 0.06 + 0.4;
		if (next > 95.0) { next = 95.0; }
		LiveTestBar.Value = next;
		LiveTestPercentText.Text = $"{liveTestLabel} {next:F0}%";
	}

	private void StopLiveTest(bool success)
	{
		liveTestTimer?.Stop();
		if (LiveTestBar == null) { return; }
		LiveTestBar.Value = 100;
		LiveTestBar.Foreground = new SolidColorBrush(success ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0xDC, 0x26, 0x26));
		LiveTestPercentText.Text = success ? L("LiveCompleted") : L("LiveStopped");
	}

	private static void MutateBrushLocal(ProgressBar bar, SolidColorBrush? brush)
	{
		if (brush != null) { bar.Foreground = new SolidColorBrush(brush.Color); }
	}
}
