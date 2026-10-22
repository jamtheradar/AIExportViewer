# AI Export Viewer

Offline desktop viewer for Claude Team/Enterprise organisation data exports (.NET 10 MAUI Blazor Hybrid, Windows + Mac Catalyst). Audience: non-technical managers reviewing staff usage. The data is highly sensitive.

- Design direction (audience, tone, references): `.impeccable.md`
- Observed export format: `docs/export-schema.md`. **Build parsers from this, not from memory.** Update it when a new export shows something different.

## Privacy rules (non-negotiable)

- **No network code** outside `src/ExportViewer.Download`. `BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` fail the build, and `PrivacyGuardTests` scans compiled assemblies (Download may use `System.Net.Http` only). Never suppress these. No telemetry, CDN assets, remote fonts or images.
- **The only network uses** (both in `ExportViewer.Download`; adding a host needs the owner's approval):
  - Downloading export zips from a manifest, when the user clicks Download. HTTPS to `ExportDownloader.AllowedHosts` (`claude.ai`) only; redirects are checked hop by hop; no cookies or credentials. Never log or store a download link.
  - App updates (owner approved 2026-10-22): `AppUpdater` (Velopack) checks `AppUpdater.ReleasesUrl` (this repo's latest GitHub release) at start-up unless the user turns it off in Settings, downloads in the background and offers a restart. All its requests go through `UpdateDownloader`: HTTPS to `AppUpdater.AllowedHosts` only, hop-by-hop redirect checks, no cookies, credentials or Velopack-supplied headers, and the query string Velopack adds is dropped. Only the installed Windows app checks. Only `ExportViewer.Download` may reference Velopack; `BannedSymbols.txt` bans its network types elsewhere and `PrivacyGuardTests` checks it.
- **Export files are read-only.** Open with `FileAccess.Read`; never write, move or rename anything in an export folder or zip.
- **All rendered content is untrusted**, including chat text, tool output, attachments, project docs, memories and artifact HTML. Use `SafeHtmlRenderer` only. Never inject raw HTML with `MarkupString` unless it came from the sanitiser. Never load `http(s)` resources. Links open only through the confirmation dialog. Artifact HTML goes only into `<iframe sandbox="">` with its own CSP. Never run artifact JavaScript.
- **Local storage**: everything goes under the single app data folder. It must appear in Settings with its size and be removed by "Delete all local data". The owner's goal is "offline, never on shared infrastructure": keeping the library database on the local PC is fine and is the default. Data is deleted only when the user chooses in Settings.
- **Logging** is off by default. Log file names, counts and timings only. Never log message text, titles, names, emails or file contents.
- **Real export data never enters the repo**: no samples, no copied snippets in tests or docs, no real names, emails or IDs in fixtures. Test fixtures come from the synthetic builders in `tests/ExportViewer.Core.Tests/Fixtures`. `samples/` is git-ignored and holds the owner's real export for local manual testing only.
- Ask before adding any NuGet package not already listed in `Directory.Packages.props`.

## Architecture

```
src/ExportViewer.Core          net10.0, no MAUI refs. Sources (folder/zip), collection detection,
                               streaming parsers, SQLite store + FTS5, search, message tree,
                               integrity checks, safe renderer, Audit/ (sensitive data check, watchlists),
                               Review/ (flags and notes store), Reports/ (PDF + CSV), Sources/ (zip unpacking)
src/ExportViewer.App           MAUI Blazor Hybrid UI, file pickers, settings, platform glue
src/ExportViewer.Download      manifest download (ExportDownloader) and app updates (AppUpdater, Velopack);
                               the only network-enabled project
tests/ExportViewer.Core.Tests  xUnit; synthetic fixtures; demo export; large-file generator; network-ban test
```

Key facts about the data (details in `docs/export-schema.md`):
- The user opens an **unzipped library folder** that may combine several exports. Discovery scans it recursively and recognises files by path pattern, then JSON shape, never by folder name alone. Records from overlapping exports merge by ID (SQLite upserts in `LibraryWriter`).
- **Basic / Expert view modes**: every page reads the `ViewMode` service. Basic shows what a manager needs; Expert shows every field, including an "All fields" panel rendered from stored raw JSON. New pages must support both.
- `conversations.json` is one huge array. Stream it one element at a time (`DeserializeAsyncEnumerable<JsonElement>`) and never load it whole.
- Render messages from `content[]`, never from `text`. `text` is a lossy flattened copy with tool-block placeholders.
- The branch tree comes from `parent_message_uuid`. Root sentinel is `00000000-0000-4000-8000-000000000000`. Latest branch = path to the newest leaf.
- There is no conversation → project link and no model field.

## Commands

Run from the repo root. Windows is the primary dev platform.

```powershell
dotnet build                                     # whole solution (Windows TFM on Windows)
dotnet test tests/ExportViewer.Core.Tests        # all tests
dotnet test tests/ExportViewer.Core.Tests --filter "Category!=Performance"   # quick run
dotnet test tests/ExportViewer.Core.Tests --filter "Category=Performance"    # 500 MB load + search timing

# Run the app (Windows)
dotnet build src/ExportViewer.App -f net10.0-windows10.0.19041.0 -t:Run
```

### Windows distribution

The app needs the WebView2 runtime; it detects a missing runtime at startup and shows instructions.

**Releases** are Velopack packages on GitHub Releases, built by `.github/workflows/release.yml` when a `v1.2.3` tag is pushed: build, test, self-contained publish with `ApplicationDisplayVersion` from the tag, `vpk pack` (pack ID `AIExportViewer`, so it installs per user to `%LOCALAPPDATA%\AIExportViewer`), `vpk upload github`. Output: `AIExportViewer-win-Setup.exe`, `-Portable.zip`, `.nupkg` full and delta packages and `releases.win.json`. The `vpk` tool version is pinned in `dotnet-tools.json` and must match the `Velopack` package version. Installed copies can only update once the repo is public. Not code signed yet (vpk supports `--signParams` / `--azureTrustedSignFile` when a certificate exists).

`Platforms/Windows/Program.cs` replaces the generated WinUI `Main` (`DISABLE_XAML_GENERATED_MAIN`) so `AppUpdater.RunStartupHooks()` runs first. Keep it in step with the generated one if WinUI changes it.

```powershell
# Self-contained single folder (no installer, xcopy deploy)
dotnet publish src/ExportViewer.App -f net10.0-windows10.0.19041.0 -c Release -r win-x64 `
  -p:WindowsPackageType=None -p:SelfContained=true -p:WindowsAppSDKSelfContained=true

# MSIX (needs a signing certificate)
dotnet publish src/ExportViewer.App -f net10.0-windows10.0.19041.0 -c Release -r win-x64 `
  -p:WindowsPackageType=MSIX -p:PackageCertificateThumbprint=<thumbprint>
```

### macOS (build on the Mac host; not verifiable on Windows)

The `net10.0-maccatalyst` TFM is only added when building on macOS.

```bash
dotnet build src/ExportViewer.App -f net10.0-maccatalyst
dotnet publish src/ExportViewer.App -f net10.0-maccatalyst -c Release -p:CreatePackage=true \
  -p:EnableCodeSigning=true -p:CodesignKey="Developer ID Application: <name> (<team>)" \
  -p:CodesignEntitlements=Platforms/MacCatalyst/Entitlements.plist
xcrun notarytool submit <pkg> --keychain-profile <profile> --wait
xcrun stapler staple <pkg>
```

Entitlements: `com.apple.security.app-sandbox` = true, `com.apple.security.files.user-selected.read-write` = true (saving downloads and reports), `com.apple.security.network.client` = true (used only by `ExportViewer.Download`).

> Verified on Windows (2026-10-22): `dotnet build`, `dotnet build -c Release`, all `dotnet test` commands. Not yet verified: the `dotnet publish` commands and everything on macOS.

### Local data location

Unpackaged Windows build: `%LOCALAPPDATA%\JamTheRadar\au.com.jamtheradar.aiexportviewer\Data` (follows `ApplicationId`). Settings → "Delete all local data" removes it. Contents:

- `settings.json`: view mode, colours (Light/Dark), recent list, watchlists, the update check switch.
- `libraries/library-<hash>.db`: the rebuildable library copy, including the last sensitive data check (`scan_findings`, positions and masked values only).
- `reviews/review-<hash>.db`: the reviewer's flags and notes. Kept apart from the library copy so a rebuild never loses them; same `<hash>` as the library.
- `unzipped/<hash>/`: working copy of zip files while open. Removed on close, on exit and at the next start.
- `exports-temp/`: Mac only, a report waiting for the save dialog. Removed straight after.
- `logs/`.

Outside the data folder, written by the installer rather than the app (no export data): the install folder `%LOCALAPPDATA%\AIExportViewer` (app, downloaded update packages, `velopack.log` with versions and GitHub URLs), and `%LOCALAPPDATA%\velopack\velopack.log` from uninstalled runs. Uninstalling removes the install folder but not the data folder.

### Driving the app for manual checks (dev only)

- Launch with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, then use the DevTools protocol at `http://127.0.0.1:9222/json` (Node 24's built-in `WebSocket` is enough) for `Runtime.evaluate` and `Page.captureScreenshot`. Never ship with that variable set.
- Use the made-up demo export, not real data: `$env:AIEV_DEMO_OUT="<empty folder>"; dotnet test tests/ExportViewer.Core.Tests --filter "Category=DemoData"`, then open that folder.
- Opening any export writes a library copy under the local data folder and adds it to the recent list. Delete both afterwards (Settings, and "Remove from list" on Home).
- `wwwroot` files are bundled into the app: changes need a rebuild and restart.

### Notes

- `index.html` CSP uses `connect-src 'self'` (not `'none'`) to leave room for Blazor's own framework requests; nothing remote is allowed. WebView2 additionally blocks every non-app request in `MainPage.xaml.cs`.
- When tree or integrity rules change, bump `LibraryLoader.AnalysisVersion` so existing libraries re-check conversations on open.
- When sensitive data rules change, bump `AuditScanner.RulesVersion` so the screen suggests checking again.
- PDF reports use PDFsharp-MigraDoc **6.2.4, the last MIT release** (6.5+ is AGPL). Don't upgrade without the owner's approval. `Directory.Build.targets` strips `PdfSharp.Cryptography`, `PdfSharp.Quality` and `PdfSharp.Snippets` (they use HttpClient); `PrivacyGuardTests` checks they stay out. PDFs use the computer's installed fonts with per-character fallback; right-to-left scripts are not shaped.
- The findings list shows **masked** values; a full value is read back from the index only when the reviewer clicks Show, and context snippets and titles mask every other value they contain (`Masking.MaskAll`). Saved reports are masked by default too; only conversation, search and flag reports offer "Include full values". New reports must follow the same default.
- Test secrets live in `tests/.../Fixtures/FakeSecrets.cs` and key-like values are assembled at run time so secret scanners don't flag the repo. Keep it that way.

## Conventions

- Nullable on, warnings as errors, no compiler warnings left behind.
- Parsers are tolerant: missing field → null, unknown field → kept in raw JSON, bad record → skipped and reported in the load summary. Never throw out of a load because of one record.
- Long work (load, index, scan) runs off the UI thread, reports `IProgress<T>` and honours a `CancellationToken`.
- UI copy is plain English for managers: no jargon, no stack traces shown to users.
- UI: light theme by default (Dark is a Settings choice via `data-theme`). Colours, text sizes and spacing come only from the tokens at the top of `app.css`; no hard-coded colours. Monospace is for code, IDs and masked values; file and folder names use `.path`.
- Screens that need an export show `<NoExport />` when none is open. Detail pages put `<BackButton Fallback="…" />` (goes back in history) above the page heading.
- Lists that link to a detail page keep the reviewer's place: restore page, tab and filters with `Recall` in `OnInitialized` and save them in `SavePlace` (both on `PageBase`).
- Before reporting a phase complete: run all tests and a Windows build, and say what couldn't be tested (e.g. the macOS build).
