# Contributing to DriveForge

Thanks for your interest! DriveForge is free and open source (GPL-3.0). Contributions of
all sizes are welcome.

## The easiest first contribution: translations

DriveForge ships in 17 languages. The strings live in one file:
`DriveForge/UiStrings.cs` (a dictionary per language). If a phrase reads awkwardly in your
language, fixing it is a great first PR — find the key in your language's block and improve
the text. English (`en`) is the fallback for any missing key.

## Building from source

Requirements: the **.NET 10 SDK** and Windows.

```
dotnet restore DriveForge.csproj
dotnet build   DriveForge.csproj -c Release
```

To produce the shipped single-file exe exactly as CI does:

```
dotnet publish DriveForge.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

The output is `bin/Release/net10.0-windows/win-x64/publish/DriveForge.exe`
(shipped as `DriveForge.exe`).

## Ground rules

- **Safety first.** DriveForge writes whole disks. Any change to a destructive path
  (wipe/format/clone/restore/partition) must keep the confirmation + target-disk
  re-verification (`VerifyTargetDiskUnchangedAsync`) intact, and default dialogs to the
  safe button. Test destructive changes only on a spare/test drive.
- **No telemetry, no ads, no accounts.** Please don't add anything that phones home.
- **Localization:** if you add user-facing text, add the key to the `en` (and ideally `ro`)
  blocks in `UiStrings.cs`; other languages fall back to English.
- Coding style: `.cs` files use **tabs**; `MainWindow.xaml` uses spaces. Match the
  surrounding code.

## Pull requests

Keep PRs focused. Describe what changed and why, and how you tested it (especially for
disk-writing changes). CI builds every PR from source on a clean Windows runner.

## Reporting bugs

Open an issue with your Windows version, the drive model, what task you ran, and the exact
error text (the in-app log helps). For security issues, see `SECURITY.md` instead.
