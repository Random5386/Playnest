# Playnest

A lightweight, customizable game launcher for Windows. It finds the games you have installed (and only games), shows them with high-resolution artwork, and keeps out of the way while you play.

Built with C# / .NET 10 and Avalonia, compiled ahead-of-time (NativeAOT) into a native exe.

## Features

- **Finds your games automatically** from each store's own install records:
  Steam, Epic Games, GOG, EA app, Ubisoft Connect, Battle.net, Xbox / Game Pass, Rockstar, Riot and Amazon Games.
- **Finds games installed outside the stores** too: standalone installs listed in Windows' installed programs,
  your own game folders (e.g. `D:\Games`), and a `Games` folder on any drive.
- **Games only, no apps.** Every candidate is checked for game evidence such as engine files (Unity, Unreal, RE Engine, Frostbite,
  Anvil, CryEngine, GameMaker, Ren'Py…), store SDKs, anti-cheat and game middleware. Electron/Qt apps, redistributables, drivers and
  runtimes are skipped. For Steam it reads Steam's own app database, so software like Wallpaper Engine is never shown as a game.
- **Launchers & tools page:** store launchers (Steam, Epic, GOG Galaxy, EA app, Ubisoft Connect, Battle.net, Xbox app, Rockstar,
  Riot, itch, Heroic, Playnite…) with game counts, plus gaming tools (mod managers, emulators, Afterburner/RTSS…).
- **Per game:** play, open install folder, uninstall (via the store or the game's own uninstaller), remove from library
  (it stays removed on rescans and can be restored), favourite, edit (name, exe, arguments, working folder, launch link,
  run as admin), and custom cover/icon/background.
- **High-resolution artwork, no accounts needed:**
  - Epic games use the official covers listed in the Epic launcher's own local catalog.
  - Other games use Steam's official covers, including games bought elsewhere that are also sold on Steam.
  - Low-resolution local copies are upgraded automatically.
  - Icons: the largest available version is chosen from the exe, `.ico` files in the game folder, Steam's own icon, or Xbox logos.
  - An optional SteamGridDB key adds art for games that aren't on Steam or Epic.
- **Game sizes:** Steam and Epic sizes are read instantly; other games are measured once in the background at low priority.
  Sizes are shown on every game tile (can be turned off in Settings), in the details panel, the list view and the header total, and are sortable.
- **Views:** cover grid (default), icon grid and list, with a "Continue playing" row and a details panel.
- **Quick play:** a small play button appears in the corner of a game when you point at it (or select it), so one click starts the game. It is unclickable while hidden, never covers the artwork, and can be set to always show or never show. Accidental double launches are ignored.
- **Playtime tracking** for games started from Playnest *and* games started any other way (Steam, shortcuts, the game's own
  launcher). It checks running programs every 30 s. Your existing Steam playtime is imported too.
- **Very customizable:** theme, accent colour, Mica/Acrylic/solid/custom-image background, corner roundness, font and text size,
  cover/icon size, tile spacing, titles/badges/playtime on tiles, sorting, double-click action, compact sidebar, and what happens
  when a game starts. Settings can be exported, imported and reset.

## Low resource usage

- Native AOT exe with workstation, non-concurrent GC and invariant globalization.
- Grid and list are **virtualized**: only visible tiles exist.
- Images are **decoded at the size they're shown**, not at full resolution, and only a small number stay cached.
- While you play (with "hide to tray" on), Playnest **unloads all artwork and trims its RAM**. While hidden, nothing renders.
- Playtime tracking only polls while a game you started is running. No background services and no polling when idle.
- Scanning runs once in the background a moment after start-up (can be turned off).
- **Low-memory mode** turns off translucency and animations and uses smaller images.

## Security & privacy

Everything that touches the internet goes through one place (`Services/Net/SafeHttp.cs`):

- **What it can contact:** HTTPS only, to a fixed list of hosts: Steam's store/API/CDN, Epic's image CDN and SteamGridDB. Any other host is refused, including redirects to one.
- **Redirects:** followed by hand (at most 3), with every hop checked again. Downgrades to HTTP are refused.
- **Responses:** size-limited while reading (15 MB for images, 4 MB for JSON), which also stops compressed "zip bombs".
- **Connections:** no cookies and short timeouts. The SteamGridDB key is sent only to `www.steamgriddb.com`.
- **Images:** a download is only accepted if its header says it's a real JPEG/PNG/WebP with sane dimensions. A tiny file claiming 60 000 px is refused before it reaches the decoder.
- **API data:** values from Steam's API (file names, ids, icon hashes) are pattern-checked before they're used in a URL.
- **Disk:** downloaded files are saved under fixed names in Playnest's cache folder; nothing from the network decides a file path.
- **What leaves your PC:** only game names (to find covers on Steam/Epic, or SteamGridDB if you add a key). There's no telemetry, and the build scripts turn Avalonia's build telemetry off.

On your PC:

- **Untrusted local files:** XML is parsed with DTDs and external entities disabled. The binary readers (Steam `appinfo.vdf`, exe icons, shortcuts) have size and depth limits.
- **Launch links:** values read from store records are escaped or validated before they go into a launch link or command line.
- **Uninstalls:** they always ask first and show the exact command that will run.
- **Exported settings:** these never include your SteamGridDB key.
- **No direct Windows API calls:** the app uses only standard .NET APIs.

## Build

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# Native build (smallest, fastest). Needs Visual Studio's "Desktop development with C++" workload.
.\publish.ps1

# Or, without the C++ tools: trimmed self-contained single exe
.\publish.ps1 -Portable
```

The result is written to `publish\Playnest\`. Copy that folder anywhere and run `Playnest.exe`. Nothing needs to be installed.

To work on it, open `Playnest.sln` in Visual Studio 2026, or in VS Code / Rider with the .NET 10 SDK, and run the `Playnest` project.
`dotnet run --project src/Playnest` also works.

### Tests

The core logic (scanners, parsers, classifier, artwork, library merging) has a dependency-free test runner:

```powershell
dotnet run --project tests/Playnest.Tests
```

## Where data is stored

`%LOCALAPPDATA%\Playnest\` holds `settings.json`, `library.json`, the artwork cache and `playnest.log`.
Put an empty `portable.txt` next to `Playnest.exe` to keep everything in a `Data` folder beside the exe instead.

## Project layout

```
src/Playnest/
  Models/              GameEntry, AppSettings
  Services/
    Scanning/          one scanner per store + FolderScanner, InstalledProgramsScanner,
                       GameClassifier (game-vs-app evidence), LauncherCatalog (launchers & tools),
                       Vdf / SteamAppInfo parsers, ScanService (runs them in order)
    Artwork/           ArtworkService (artwork sources in priority order), PeIcon (exe icon → PNG)
    Platform/          registry, Win32 helpers, .lnk/.url readers, path utilities
    LibraryService     merge scan results, ignore list, save
    LaunchService      launch / open folder / uninstall + SessionTracker (playtime)
    ImageCache, ThemeService, Storage
  ViewModels/          MainViewModel, GameViewModel, SettingsViewModel, dialogs
  Views/               MainWindow, LibraryView, DetailsPanel, LaunchersView, SettingsView, dialogs
  Themes/Styles.axaml
tests/Playnest.Tests/  test runner
```

## Keyboard

`Ctrl+F` search · `F5` scan · `Enter` play selected · `Esc` clear search / close details
