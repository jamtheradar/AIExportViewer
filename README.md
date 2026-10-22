# AI Export Viewer

A desktop app for reviewing a Claude Team or Enterprise organisation data export, offline. It is made for managers who
need to look through staff usage without technical tools: people, conversations, projects, memories and artifacts,
with search, a sensitive data check, watchlists, flags and notes, and PDF or CSV reports.

Export data is highly sensitive, so the app is built around a few firm rules:

- **Offline.** It only connects to the internet to download an export from claude.ai when you ask, and to check GitHub
  for a newer version of the app. The update check can be turned off in Settings. There is no telemetry or crash
  reporting.
- **Read only.** Export files and folders are never changed, moved or renamed.
- **Nothing runs from the export.** Chat text, attachments and artifacts are shown through a sanitiser, and artifact
  code never runs.
- **Stays on your computer.** The app keeps a searchable copy of each export in one local folder. Settings shows its
  size and can delete everything.

## Install (Windows)

1. Download `AIExportViewer-win-Setup.exe` from the [latest release](https://github.com/jamtheradar/AIExportViewer/releases/latest).
2. Run it. It installs for your Windows account only (no administrator rights needed) and adds a Start menu shortcut.

Windows may say "Windows protected your PC" because the installer isn't code signed yet. Choose **More info**, then
**Run anyway**.

The app needs the Microsoft Edge WebView2 Runtime, which Windows 10 and 11 normally already have. If it's missing,
the app says so and explains how to get it.

There is also `AIExportViewer-win-Portable.zip` for running from a folder without installing.

### Updates

When it starts, the installed app checks the latest GitHub release for a newer version, downloads it in the
background and shows **Update ready**. The update is installed next time the app starts, or straight away from
Settings → Updates → **Restart and update**. Nothing about you or your exports is sent; it is a plain download of
public release files from `github.com` (and GitHub's file host).

### Uninstall

Use Windows Settings → Apps. Uninstalling removes the app but leaves its local data folder. To remove that too, use
Settings → **Delete all local data** in the app first.

## Mac

The Mac app (Mac Catalyst) builds from source on a Mac. There is no released Mac build yet. See `CLAUDE.md` for the
commands.

## Build from source

Needs the .NET 10 SDK (see `global.json`) and the MAUI workload (`dotnet workload install maui-windows`).

```powershell
dotnet build
dotnet test tests/ExportViewer.Core.Tests --filter "Category!=Performance"
dotnet build src/ExportViewer.App -f net10.0-windows10.0.19041.0 -t:Run
```

The tests use made-up data only. Never add a real export, or anything copied from one, to the repository.

## Releasing

Push a version tag. The [release workflow](.github/workflows/release.yml) builds, tests, packs with Velopack and
publishes a GitHub release with the installer and update packages.

```powershell
git tag v0.2.0
git push origin v0.2.0
```

## Licence

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Security problems: see
[SECURITY.md](SECURITY.md).

This project is not made by, or affiliated with, Anthropic.
