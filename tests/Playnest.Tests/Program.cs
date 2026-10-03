// Dependency-free test runner for Playnest's core logic (scanners, parsers, artwork, library merging).
// Run with:  dotnet run --project tests/Playnest.Tests  [path-to-an-exe-with-an-icon]

using System.Buffers.Binary;
using System.Text;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Artwork;
using Playnest.Services.Net;
using Playnest.Services.Platform;
using Playnest.Services.Scanning;

int passed = 0, failed = 0;
var tmpRoot = Path.Combine(Path.GetTempPath(), "playnest-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmpRoot);
AppPaths.Override(Path.Combine(tmpRoot, "data"));
AppPaths.Ensure();

void Check(bool cond, string name, string? detail = null)
{
    if (cond) { passed++; Console.WriteLine($"  ok   {name}"); }
    else { failed++; Console.WriteLine($"  FAIL {name}{(detail is null ? "" : " — " + detail)}"); }
}

void Eq<T>(T expected, T actual, string name) =>
    Check(EqualityComparer<T>.Default.Equals(expected, actual), name, $"expected «{expected}», got «{actual}»");

void Section(string name) => Console.WriteLine($"\n== {name}");

string Dir(params string[] parts)
{
    var d = Path.Combine([tmpRoot, .. parts]);
    Directory.CreateDirectory(d);
    return d;
}

void Touch(string path, int bytes = 16)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, new byte[bytes]);
}

// ---------------------------------------------------------------------------------------------
Section("VDF text parser");
{
    var text = """
    "libraryfolders"
    {
        "0"
        {
            "path"      "C:\\Program Files (x86)\\Steam"
            "apps" { "228980" "123" "1245620" "456" }
        }
        // comment
        "1" { "path" "D:\\SteamLibrary" }
    }
    """;
    var root = Vdf.Parse(text);
    Eq(@"C:\Program Files (x86)\Steam", root.Path("libraryfolders/0")?.Str("path"), "nested path with escaped backslashes");
    Eq(@"D:\SteamLibrary", root.Path("LIBRARYFOLDERS/1")?.Str("PATH"), "case-insensitive keys");
    Eq("456", root.Path("libraryfolders/0/apps")?.Str("1245620"), "inner values");

    var acf = Vdf.Parse("\"AppState\" { \"appid\" \"1245620\" \"name\" \"ELDEN RING\" \"StateFlags\" \"4\" \"installdir\" \"ELDEN RING\" }");
    Eq(1245620L, acf["AppState"]!.Long("appid"), "appmanifest appid");
    Eq("ELDEN RING", acf["AppState"]!.Str("installdir"), "appmanifest installdir");
}

// ---------------------------------------------------------------------------------------------
Section("Steam appinfo.vdf (binary)");
{
    foreach (uint version in new uint[] { 0x07564428, 0x07564429 })
    {
        var file = Path.Combine(tmpRoot, $"appinfo_{version:x}.vdf");
        WriteAppInfo(file, version, [
            (10, "Counter-Strike", "Game", "hl.exe"),
            (431960, "Wallpaper Engine", "Application", "wallpaper32.exe"),
            (999, "Skip me", "Game", null),
        ]);
        var meta = SteamAppInfo.Read(file, new HashSet<uint> { 10, 431960 });
        var v = version == 0x07564429 ? "v29" : "v28";
        Eq(2, meta.Count, $"{v}: only requested apps returned");
        Eq("Game", meta.GetValueOrDefault(10u)?.Type, $"{v}: type of a game");
        Eq("Application", meta.GetValueOrDefault(431960u)?.Type, $"{v}: type of software");
        Eq("hl.exe", meta.GetValueOrDefault(10u)?.Executable, $"{v}: windows launch executable");
        Eq("Counter-Strike", meta.GetValueOrDefault(10u)?.Name, $"{v}: name");
    }
}

// ---------------------------------------------------------------------------------------------
Section("Xbox package family name");
{
    Eq("8wekyb3d8bbwe", XboxScanner.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"),
        "publisher id of Microsoft");
    var gr = Path.Combine(tmpRoot, ".GamingRoot");
    var bytes = new List<byte>();
    bytes.AddRange("RGBX"u8.ToArray());
    bytes.AddRange(BitConverter.GetBytes(1));
    bytes.AddRange(Encoding.Unicode.GetBytes("XboxGames\0"));
    File.WriteAllBytes(gr, bytes.ToArray());
    var roots = XboxScanner.ReadGamingRoot(gr);
    Eq(1, roots.Count, ".GamingRoot entry count");
    Eq("XboxGames", roots.FirstOrDefault(), ".GamingRoot folder");
}

// ---------------------------------------------------------------------------------------------
Section("Names and paths");
{
    Eq("witcher3wildhunt", PathUtil.NormalizeName("The Witcher® 3: Wild Hunt"), "normalize trademark + 'The'");
    Eq(PathUtil.NormalizeName("Cyberpunk 2077"), PathUtil.NormalizeName("Cyberpunk 2077: Ultimate Edition"), "edition words ignored");
    Eq("Hogwarts Legacy", PathUtil.CleanTitle("Hogwarts Legacy, v1.0.1234"), "version suffix removed");
    Eq("Some Game", PathUtil.CleanTitle("Some Game (x64)"), "arch suffix removed");
    Eq("Battlefield™ 6".Replace("™", ""), PathUtil.CleanTitle("Battlefield™ 6"), "trademark removed");
    var (f, a) = PathUtil.SplitCommand("\"C:\\Program Files\\Game\\unins000.exe\" /SILENT");
    Eq("C:\\Program Files\\Game\\unins000.exe", f, "split quoted command: file");
    Eq("/SILENT", a, "split quoted command: args");
    (f, a) = PathUtil.SplitCommand("MsiExec.exe /X{1234-5678}");
    Eq("MsiExec.exe", f, "split msiexec: file");
    Eq("/X{1234-5678}", a, "split msiexec: args");
    Check(PathUtil.LooksLikeUninstaller("C:/x/unins000.exe"), "unins000 is an uninstaller");
    Check(!PathUtil.LooksLikeUninstaller("C:/x/Game.exe"), "game exe is not an uninstaller");
    Check(PathUtil.IsUnder(Path.Combine(tmpRoot, "a", "b"), Path.Combine(tmpRoot, "a")), "IsUnder child");
    Check(!PathUtil.IsUnder(Path.Combine(tmpRoot, "ab"), Path.Combine(tmpRoot, "a")), "IsUnder sibling with same prefix");
}

// ---------------------------------------------------------------------------------------------
Section("Excluded names (apps that are never games)");
{
    foreach (var n in new[] { "Microsoft Visual C++ 2015-2022 Redistributable (x64)", "NVIDIA Graphics Driver 560.94", "Steamworks Common Redistributables",
                 "Python 3.12.1 (64-bit)", "Microsoft Edge WebView2 Runtime", "EasyAntiCheat", "Realtek Audio Driver", "Riot Vanguard", "Proton 9.0" })
        Check(GameClassifier.IsExcludedName(n), $"excluded: {n}");
    foreach (var n in new[] { "Call of Duty: Vanguard", "Driver: San Francisco", "Cyberpunk 2077", "ELDEN RING", "Assassin's Creed Shadows", "Battlefield 6" })
        Check(!GameClassifier.IsExcludedName(n), $"kept: {n}");
    Check(GameClassifier.IsExcludedName("My Benchmark Tool", ["benchmark"]), "user keyword excludes");
}

// ---------------------------------------------------------------------------------------------
Section("Game detection by folder contents");
{
    var unity = Dir("Games", "Hollow Thing");
    Touch(Path.Combine(unity, "Hollow Thing.exe"), 700_000);
    Touch(Path.Combine(unity, "UnityPlayer.dll"));
    Touch(Path.Combine(unity, "UnityCrashHandler64.exe"));
    Touch(Path.Combine(unity, "Hollow Thing_Data", "globalgamemanagers"));
    var ev = GameClassifier.Inspect(unity, "Hollow Thing");
    Check(ev.IsGame, "Unity game detected", string.Join(", ", ev.Reasons));
    Eq("Hollow Thing.exe", Path.GetFileName(ev.BestExe ?? ""), "Unity main exe (crash handler skipped)");

    var unreal = Dir("Games", "Stellar Run");
    Touch(Path.Combine(unreal, "StellarRun.exe"), 300_000);
    Touch(Path.Combine(unreal, "Engine", "Binaries", "ThirdParty", "x.dll"));
    Touch(Path.Combine(unreal, "StellarRun", "Binaries", "Win64", "StellarRun-Win64-Shipping.exe"), 2_000_000);
    Touch(Path.Combine(unreal, "StellarRun", "Content", "Paks", "pakchunk0-Windows.pak"));
    ev = GameClassifier.Inspect(unreal, "Stellar Run");
    Check(ev.IsGame, "Unreal game detected", string.Join(", ", ev.Reasons));
    Eq("StellarRun.exe", Path.GetFileName(ev.BestExe ?? ""), "Unreal root bootstrap exe preferred");

    var steamSdk = Dir("Games", "Indie Thing");
    Touch(Path.Combine(steamSdk, "game.exe"), 9_000_000);
    Touch(Path.Combine(steamSdk, "steam_api64.dll"));
    Touch(Path.Combine(steamSdk, "unins000.exe"));
    ev = GameClassifier.Inspect(steamSdk, "Indie Thing");
    Check(ev.IsGame, "Steam SDK game detected");
    Eq("game.exe", Path.GetFileName(ev.BestExe ?? ""), "uninstaller never picked");

    var app = Dir("Apps", "Chatter");
    Touch(Path.Combine(app, "Chatter.exe"), 120_000_000);
    Touch(Path.Combine(app, "resources", "app.asar"));
    Touch(Path.Combine(app, "libcef.dll"));
    Touch(Path.Combine(app, "ffmpeg.dll"));
    ev = GameClassifier.Inspect(app, "Chatter");
    Check(!ev.IsGame, "Electron app is not a game", $"score {ev.Score}: {string.Join(", ", ev.Reasons)}");

    var tool = Dir("Apps", "Photo Editor");
    Touch(Path.Combine(tool, "PhotoEditor.exe"), 40_000_000);
    Touch(Path.Combine(tool, "Qt6Core.dll"));
    Touch(Path.Combine(tool, "Qt6Gui.dll"));
    ev = GameClassifier.Inspect(tool, "Photo Editor");
    Check(!ev.IsGame, "Qt desktop app is not a game", $"score {ev.Score}");
}

// ---------------------------------------------------------------------------------------------
Section("Folder scanner (user game folders)");
{
    var s = new AppSettings { AutoDetectGameFolders = false, GameFolders = [Path.Combine(tmpRoot, "Games"), Path.Combine(tmpRoot, "Apps")] };
    // A publisher folder with a game one level deeper
    var nested = Dir("Games", "Ubisoft", "Far Away Game");
    Touch(Path.Combine(nested, "FarAway.exe"), 5_000_000);
    Touch(Path.Combine(nested, "uplay_r1_loader64.dll"));
    Touch(Path.Combine(nested, "data.forge"));
    // REDengine-style layout: no exe in the root, exe in bin\x64
    var red = Dir("Games", "Neon City 2077");
    Touch(Path.Combine(red, "bin", "x64", "NeonCity2077.exe"), 60_000_000);
    Touch(Path.Combine(red, "bin", "x64", "bink2w64.dll"));
    Touch(Path.Combine(red, "bin", "x64", "nvngx_dlss.dll"));
    Touch(Path.Combine(red, "bin", "x64", "amd_ags_x64.dll"));
    Touch(Path.Combine(red, "engine", "config", "base.ini"));
    var ctx = new ScanContext(s, [], CancellationToken.None);
    var found = new FolderScanner().Scan(ctx).ToList();
    var names = found.Select(x => x.Name).OrderBy(x => x).ToList();
    Check(names.Contains("Hollow Thing") && names.Contains("Stellar Run") && names.Contains("Indie Thing"), "games found", string.Join(", ", names));
    Check(names.Contains("Far Away Game") && !names.Contains("Ubisoft"), "game inside a publisher sub folder found (publisher folder itself not added)", string.Join(", ", names));
    Check(names.Contains("Neon City 2077") && !names.Contains("bin"), "exe in bin\\x64 detected as one game", string.Join(", ", names));
    Eq("NeonCity2077.exe", Path.GetFileName(found.FirstOrDefault(x => x.Name == "Neon City 2077")?.ExePath ?? ""), "deep main exe picked");
    Check(!names.Contains("Chatter") && !names.Contains("Photo Editor"), "apps skipped", string.Join(", ", names));
    Check(found.All(x => x.Kind == ItemKind.Game && x.Id.StartsWith("folder:")), "kind + id");
    var again = new FolderScanner().Scan(new ScanContext(s, [], CancellationToken.None)).Select(x => x.Id).OrderBy(x => x).ToList();
    Check(found.Select(x => x.Id).OrderBy(x => x).SequenceEqual(again), "ids are stable between scans");
    Eq(FolderScanner.FindLocalUninstaller(Path.Combine(tmpRoot, "Games", "Indie Thing")) is not null, true, "local uninstaller found");

    s.ExcludedFolders = [Path.Combine(tmpRoot, "Games", "Hollow Thing")];
    var withExclusion = new FolderScanner().Scan(new ScanContext(s, [], CancellationToken.None)).Select(x => x.Name).ToList();
    Check(!withExclusion.Contains("Hollow Thing"), "excluded folder skipped");
}

// ---------------------------------------------------------------------------------------------
Section("Epic manifest");
{
    var game = Dir("Epic", "Fortnite");
    var mDir = Dir("EpicManifests");
    var m1 = Path.Combine(mDir, "a.item");
    File.WriteAllText(m1, $$"""
    { "DisplayName": "Fortnite", "AppName": "Fortnite", "MainGameAppName": "Fortnite", "InstallLocation": "{{game.Replace("\\", "\\\\")}}",
      "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "CatalogNamespace": "fn", "CatalogItemId": "4fe7",
      "AppCategories": ["public", "games", "applications"] }
    """);
    var ue = Dir("Epic", "UE_5.4");
    var m2 = Path.Combine(mDir, "b.item");
    File.WriteAllText(m2, $$"""
    { "DisplayName": "Unreal Engine 5.4", "AppName": "UE_5.4", "MainGameAppName": "UE_5.4", "InstallLocation": "{{ue.Replace("\\", "\\\\")}}",
      "AppCategories": ["public", "engines"] }
    """);
    var dlc = Path.Combine(mDir, "c.item");
    File.WriteAllText(dlc, $$"""
    { "DisplayName": "Some DLC", "AppName": "dlc1", "MainGameAppName": "Fortnite", "InstallLocation": "{{game.Replace("\\", "\\\\")}}", "AppCategories": ["addons"] }
    """);
    var ctx = new ScanContext(new AppSettings(), [], CancellationToken.None);
    var g = EpicScanner.Parse(m1, ctx);
    Check(g is not null && g.Kind == ItemKind.Game, "Epic game parsed");
    Check(g?.LaunchUri?.StartsWith("com.epicgames.launcher://apps/fn%3A4fe7%3AFortnite?action=launch") == true, "Epic launch link", g?.LaunchUri);
    Check(EpicScanner.Parse(m2, ctx) is null, "Unreal Engine (engine category) skipped");
    Check(EpicScanner.Parse(dlc, ctx) is null, "DLC skipped");
}

// ---------------------------------------------------------------------------------------------
Section("Artwork helpers");
{
    Eq(1245620L, ArtworkService.BestNameMatch("ELDEN RING", [("ELDEN RING NIGHTREIGN", 2622380), ("ELDEN RING", 1245620)]), "exact name match wins");
    Eq(1091500L, ArtworkService.BestNameMatch("Cyberpunk 2077", [("Cyberpunk 2077", 1091500), ("Cyberpunk 2077: Phantom Liberty", 2138330)]), "base game over DLC");
    Eq(0L, ArtworkService.BestNameMatch("Hogwarts Legacy", [("Harry Potter: Quidditch Champions", 1)]), "no false match");
    Eq(292030L, ArtworkService.BestNameMatch("The Witcher 3: Wild Hunt", [("The Witcher® 3: Wild Hunt", 292030)]), "trademark-insensitive");

    // Steam librarycache: new (2024+) layout with hashed sub folders
    var steam = Dir("Steam");
    Touch(Path.Combine(steam, "appcache", "librarycache", "570", "abc123", "library_600x900.jpg"));
    Touch(Path.Combine(steam, "appcache", "librarycache", "730_library_600x900.jpg"));
    Check(ArtworkService.SteamLibraryCache(steam, 570, ArtKind.Cover)?.EndsWith("library_600x900.jpg") == true, "new librarycache layout");
    Check(ArtworkService.SteamLibraryCache(steam, 730, ArtKind.Cover)?.EndsWith("730_library_600x900.jpg") == true, "old librarycache layout");
    Check(ArtworkService.SteamLibraryCache(steam, 999, ArtKind.Cover) is null, "missing cover → null");

    // DIB → PNG conversion (32-bit icon with alpha)
    int w = 16, h = 16;
    var dib = new byte[40 + w * h * 4 + ((w + 31) / 32 * 4) * h];
    BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
    BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), w);
    BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), h * 2);
    BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
    BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 32);
    for (int i = 0; i < w * h; i++) { dib[40 + i * 4] = 255; dib[40 + i * 4 + 3] = 255; } // opaque blue
    var png = PeIcon.DibToPng(dib);
    Check(png is not null && png[0] == 0x89 && png[1] == (byte)'P', "DIB converted to PNG");
    if (png is not null) File.WriteAllBytes(Path.Combine(tmpRoot, "dib.png"), png);
}

// ---------------------------------------------------------------------------------------------
Section("Icon extraction from real executables");
{
    var exes = args.Length > 0 ? args : FindSampleExes();
    if (exes.Length == 0) Console.WriteLine("  (no sample .exe found — pass one as an argument)");
    foreach (var exe in exes.Take(4))
    {
        var png = PeIcon.ExtractLargestPng(exe);
        Check(png is not null && png.Length > 100, $"icon from {Path.GetFileName(exe)}");
        if (png is not null)
        {
            var outFile = Path.Combine(tmpRoot, Path.GetFileNameWithoutExtension(exe) + ".png");
            File.WriteAllBytes(outFile, png);
            int pw = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
            Console.WriteLine($"       → {pw}px  {outFile}");
        }
    }
}

// ---------------------------------------------------------------------------------------------
Section("Library merge");
{
    var lib = new LibraryService();
    var settings = new AppSettings();
    var scan1 = new ScanResult();
    scan1.CompletedSources.Add(GameSource.Steam);
    scan1.CompletedSources.Add(GameSource.Folder);
    scan1.Items.Add(new GameEntry { Id = "steam:1", Name = "Alpha", Source = GameSource.Steam, InstallDir = Dir("lib", "a") });
    scan1.Items.Add(new GameEntry { Id = "steam:2", Name = "Beta", Source = GameSource.Steam, InstallDir = Dir("lib", "b") });
    scan1.Items.Add(new GameEntry { Id = "folder:x", Name = "Gamma", Source = GameSource.Folder, InstallDir = Dir("lib", "c") });
    var r = lib.Merge(scan1, settings);
    Eq(3, r.Added, "first scan adds all");

    var alpha = lib.Data.Items.First(i => i.Id == "steam:1");
    alpha.CustomName = "Alpha (renamed)";
    alpha.Favorite = true;
    alpha.PlaytimeSeconds = 3600;

    var scan2 = new ScanResult();
    scan2.CompletedSources.Add(GameSource.Steam);
    scan2.Items.Add(new GameEntry { Id = "steam:1", Name = "Alpha", Source = GameSource.Steam, InstallDir = Dir("lib", "a"), ImportedPlaytimeSeconds = 60 });
    r = lib.Merge(scan2, settings);
    Eq(1, r.Removed, "uninstalled Steam game removed");
    Check(lib.Data.Items.Any(i => i.Id == "folder:x"), "items from sources that didn't run are kept");
    alpha = lib.Data.Items.First(i => i.Id == "steam:1");
    Eq("Alpha (renamed)", alpha.DisplayName, "user rename kept");
    Check(alpha.Favorite && alpha.PlaytimeSeconds == 3600, "favourite + playtime kept");

    lib.Ignore(alpha);
    r = lib.Merge(scan2, settings);
    Check(lib.Data.Items.All(i => i.Id != "steam:1"), "ignored item not re-added");
    lib.Unignore(lib.Data.Ignored.First());
    lib.Merge(scan2, settings);
    Check(lib.Data.Items.Any(i => i.Id == "steam:1"), "restored item comes back");

    settings.ScanSteam = false;
    lib.Merge(new ScanResult(), settings);
    Check(lib.Data.Items.All(i => i.Source != GameSource.Steam), "disabling a store removes its games");

    lib.SaveNow();
    var reloaded = Storage.LoadLibrary();
    Eq(lib.Data.Items.Count, reloaded.Items.Count, "library round-trips through JSON");
}

// ---------------------------------------------------------------------------------------------
Section("Manual add + shortcuts");
{
    var exe = Path.Combine(Dir("Manual", "Cool Game", "Binaries", "Win64"), "CoolGame.exe");
    Touch(exe);
    var e = LibraryService.CreateManual(exe, ItemKind.Game);
    Eq("Cool Game", e.Name, "name from game root folder");
    Check(e.InstallDir?.EndsWith("Cool Game") == true, "install dir walks up from Binaries\\Win64", e.InstallDir);

    var url = Path.Combine(tmpRoot, "Elden.url");
    File.WriteAllText(url, "[InternetShortcut]\nURL=steam://rungameid/1245620\nIconFile=C:\\nope.ico\n");
    var u = LibraryService.CreateManual(url, ItemKind.Game);
    Eq("steam://rungameid/1245620", u.LaunchUri, ".url launch link");
    Eq(1245620L, u.SteamAppId, ".url Steam id used for artwork");
}

// ---------------------------------------------------------------------------------------------
Section("Settings JSON");
{
    var s = new AppSettings { AccentColor = "#FF0000", ViewMode = LibraryViewMode.List, GameFolders = ["D:\\Games"] };
    Storage.SaveSettings(s);
    var json = File.ReadAllText(AppPaths.SettingsFile);
    Check(json.Contains("\"viewMode\": \"List\""), "enums saved as text");
    var back = Storage.LoadSettings();
    Eq("#FF0000", back.AccentColor, "accent round-trip");
    Eq(LibraryViewMode.List, back.ViewMode, "view mode round-trip");
    File.WriteAllText(AppPaths.SettingsFile, "{ not json");
    Eq(LibraryViewMode.Covers, Storage.LoadSettings().ViewMode, "corrupt settings fall back to defaults");
}

// ---------------------------------------------------------------------------------------------
Section("Real-library folder layouts (from Kishen's PC)");
{
    var root = Dir("Lib2");
    // Persona 3 Reload: Unreal root with Engine + project folder + a "_Crack" copy of the exe
    var p3 = Dir("Lib2", "Persona 3 Reload");
    Touch(Path.Combine(p3, "Engine", "Binaries", "ThirdParty", "x.dll"));
    Touch(Path.Combine(p3, "P3R", "Binaries", "Win64", "P3R.exe"), 90_000_000);
    Touch(Path.Combine(p3, "P3R", "Content", "Paks", "pakchunk0-Windows.pak"));
    Touch(Path.Combine(p3, "_Crack", "P3R", "Binaries", "Win64", "P3R.exe"), 90_000_000);
    // Soulframe: launcher layout, game in Downloaded\Public
    var sf = Dir("Lib2", "Soulframe");
    Touch(Path.Combine(sf, "Downloaded", "Public", "Soulframe.x64.exe"), 80_000_000);
    Touch(Path.Combine(sf, "Downloaded", "Public", "steam_api64.dll"));
    Touch(Path.Combine(sf, "Downloaded", "Public", "bink2w64.dll"));
    // Dotted scene-style folder name
    var dotted = Dir("Lib2", "Five.Hearts.Under.One.Roof.season.2");
    Touch(Path.Combine(dotted, "game", "FHUOR2.exe"), 9_000_000);
    Touch(Path.Combine(dotted, "UnityPlayer.dll"));
    Touch(Path.Combine(dotted, "FHUOR2.exe"), 700_000);
    // Electron DLSS manager that bundles DLSS dlls → a tool, not a game
    var dm = Dir("Lib2", "DLSS 5 MANAGER");
    Touch(Path.Combine(dm, "DLSS 5 MANAGER.exe"), 150_000_000);
    Touch(Path.Combine(dm, "resources", "app.asar"));
    Touch(Path.Combine(dm, "nvngx_dlss.dll"));
    Touch(Path.Combine(dm, "nvngx_dlssg.dll"));
    Touch(Path.Combine(dm, "libxess.dll"));
    Touch(Path.Combine(dm, "amd_fidelityfx_dx12.dll"));
    // Football Manager-like game: "Manager" in the name but a real game
    var fm = Dir("Lib2", "Football Manager 2024");
    Touch(Path.Combine(fm, "fm.exe"), 50_000_000);
    Touch(Path.Combine(fm, "fm_Data", "globalgamemanagers"));
    Touch(Path.Combine(fm, "UnityPlayer.dll"));

    var s = new AppSettings { AutoDetectGameFolders = false, GameFolders = [root] };
    var found = new FolderScanner().Scan(new ScanContext(s, [], CancellationToken.None)).ToList();
    var names = found.Select(f => $"{f.Name} [{f.Kind}]").OrderBy(x => x).ToList();
    var all = string.Join(", ", names);
    Check(names.Contains("Persona 3 Reload [Game]") && !all.Contains("P3R [") && !all.Contains("_Crack"), "Persona: one game named after its root folder", all);
    Eq("P3R.exe", Path.GetFileName(found.FirstOrDefault(f => f.Name == "Persona 3 Reload")?.ExePath ?? ""), "Persona exe (not the _Crack copy)");
    Check(!(found.FirstOrDefault(f => f.Name == "Persona 3 Reload")?.ExePath ?? "").Contains("_Crack"), "Persona exe path outside _Crack");
    Check(names.Contains("Soulframe [Game]") && !all.Contains("Downloaded"), "Soulframe: 'Downloaded' folder belongs to its parent", all);
    Check(names.Contains("Five Hearts Under One Roof season 2 [Game]"), "dotted folder name made readable", all);
    Check(names.Contains("DLSS 5 MANAGER [Tool]"), "Electron DLSS manager is a tool, not a game", all);
    Check(names.Contains("Football Manager 2024 [Game]"), "'Manager' in a real game's name doesn't make it a tool", all);
}

// ---------------------------------------------------------------------------------------------
Section("Artwork name matching (titles from Kishen's library)");
{
    var q = ArtworkService.SearchQueries("The Witcher 3: Wild Hunt - Remastered");
    Check(q.Contains("The Witcher 3: Wild Hunt"), "query drops '- Remastered'", string.Join(" | ", q));
    q = ArtworkService.SearchQueries("Tomb Raider GAME OF THE YEAR EDITION");
    Check(q.Contains("Tomb Raider"), "query drops 'GAME OF THE YEAR EDITION'", string.Join(" | ", q));
    q = ArtworkService.SearchQueries("Dishonored - Definitive Edition");
    Check(q.Contains("Dishonored"), "query drops '- Definitive Edition'", string.Join(" | ", q));

    Eq(2552430L, ArtworkService.BestNameMatch("Kingdom Hearts HD 2.8 FCP", [("KINGDOM HEARTS HD 2.8 Final Chapter Prologue", 2552430), ("KINGDOM HEARTS III + Re Mind (DLC)", 2552450)]), "KH 2.8 abbreviation matched");
    Eq(2552450L, ArtworkService.BestNameMatch("Kingdom Hearts III and Re-Mind", [("KINGDOM HEARTS -HD 1.5+2.5 ReMIX-", 2552430), ("KINGDOM HEARTS III + Re Mind (DLC)", 2552450)]), "KH III + Re Mind matched");
    Eq(2054970L, ArtworkService.BestNameMatch("Dragon Dogma 2", [("Dragon's Dogma 2", 2054970), ("Dragon's Dogma: Dark Arisen", 367500)]), "missing apostrophe tolerated");
    Eq(0L, ArtworkService.BestNameMatch("Halo", [("Halo Infinite Multiplayer Season Pass Bundle Extra Big", 1)]), "short name doesn't grab a long unrelated title");
    Eq(0L, ArtworkService.BestNameMatch("Spec Ops The Line", [("Spec Ops: Recon Warfare", 5)]), "different game in the same series not matched");
}

// ---------------------------------------------------------------------------------------------
Section("Epic catalog cache (format from Kishen's catcache.bin)");
{
    var json = """
    [{"id":"ff45","title":"Tomb Raider GAME OF THE YEAR EDITION","keyImages":[
        {"type":"DieselGameBox","url":"https://cdn1.epicgames.com/item/x/wide.jpg","width":2560,"height":1440},
        {"type":"DieselGameBoxTall","url":"https://cdn1.epicgames.com/item/x/tall.jpg","width":1200,"height":1600}],
      "releaseInfo":[{"appId":"d6264d56f5ba434e91d4b0a0b056c83a"}]},
     {"id":"evil","title":"Evil","keyImages":[{"type":"DieselGameBoxTall","url":"http://evil.example.com/a.jpg"}],"releaseInfo":[{"appId":"evil"}]}]
    """;
    var byApp = new Dictionary<string, EpicImages>(StringComparer.OrdinalIgnoreCase);
    var all = new List<EpicImages>();
    EpicCatalog.Parse(Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), byApp, all);
    Eq("https://cdn1.epicgames.com/item/x/tall.jpg", byApp.GetValueOrDefault("d6264d56f5ba434e91d4b0a0b056c83a")?.Tall, "tall cover by AppName");
    Eq("https://cdn1.epicgames.com/item/x/wide.jpg", byApp.GetValueOrDefault("d6264d56f5ba434e91d4b0a0b056c83a")?.Wide, "wide background by AppName");
    Check(!byApp.ContainsKey("evil"), "non-Epic / non-HTTPS image hosts ignored");
}

// ---------------------------------------------------------------------------------------------
Section("Image headers");
{
    var png = Png.Encode(new byte[37 * 21 * 4], 37, 21);
    Eq((37, 21), ImageInfo.Size(png) ?? (0, 0), "PNG size");
    // minimal JPEG header: SOI, APP0, SOF0 (height 450, width 300)
    byte[] jpg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                  0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xC2, 0x01, 0x2C, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01];
    Eq((300, 450), ImageInfo.Size(jpg) ?? (0, 0), "JPEG size (Steam's 300×450 local cover)");
    Check(ImageInfo.Size([1, 2, 3]) is null, "garbage → no size, no crash");
}

// ---------------------------------------------------------------------------------------------
Section("Sizes, playtime index, parser limits");
{
    var d = Dir("SizeTest");
    File.WriteAllBytes(Path.Combine(d, "a.bin"), new byte[1000]);
    Directory.CreateDirectory(Path.Combine(d, "sub"));
    File.WriteAllBytes(Path.Combine(d, "sub", "b.bin"), new byte[2345]);
    Eq(3345L, SizeCalculator.Measure(d), "folder size");
    Eq("1.5 GB", SizeCalculator.Format(1610612736L), "size formatting");

    var g = Dir("ExeIdx");
    Touch(Path.Combine(g, "MyGame.exe"));
    Touch(Path.Combine(g, "UnityCrashHandler64.exe"));
    Touch(Path.Combine(g, "Launcher.exe"));
    Touch(Path.Combine(g, "unins000.exe"));
    Touch(Path.Combine(g, "_CommonRedist", "vc_redist.x64.exe"));
    var names = SessionTracker.ExeNames(g).ToList();
    Check(names.SequenceEqual(["MyGame"]), "playtime index keeps only identifying exe names", string.Join(",", names));

    // appinfo.vdf: absurd string-table count must not allocate gigabytes
    var bad = Path.Combine(tmpRoot, "bad_appinfo.vdf");
    using (var bw = new BinaryWriter(File.Create(bad)))
    {
        bw.Write(0x07564429u); bw.Write(1u); bw.Write(16L); bw.Write(uint.MaxValue);
    }
    Check(SteamAppInfo.Read(bad, new HashSet<uint> { 1 }).Count == 0, "corrupt appinfo.vdf rejected safely");

    var settings = new AppSettings { SteamGridDbApiKey = "secret-key" };
    Check(!Storage.ExportSettings(settings).Contains("secret-key"), "exported settings never contain the API key");
}

// ---------------------------------------------------------------------------------------------
Section("Network safety (everything that touches the internet)");
{
    // Host allow-list
    Check(SafeHttp.IsAllowed("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/620/library_600x900_2x.jpg"), "Steam CDN allowed");
    Check(SafeHttp.IsAllowed("https://cdn1.epicgames.com/item/x/tall.jpg"), "Epic CDN allowed");
    Check(SafeHttp.IsAllowed("https://cdn2.steamgriddb.com/grid/abc.png"), "SteamGridDB CDN allowed");
    Check(!SafeHttp.IsAllowed("http://shared.akamai.steamstatic.com/a.jpg"), "plain HTTP refused");
    Check(!SafeHttp.IsAllowed("https://evil.com/a.jpg"), "unknown host refused");
    Check(!SafeHttp.IsAllowed("https://steamstatic.com.evil.com/a.jpg"), "look-alike host refused");
    Check(!SafeHttp.IsAllowed("https://evilsteamstatic.com/a.jpg"), "suffix trick refused");
    Check(!SafeHttp.IsAllowed("https://store.steampowered.com@evil.com/a"), "user-info trick refused");
    Check(!SafeHttp.IsAllowed("https://store.steampowered.com:8443/a"), "non-standard port refused");
    Check(!SafeHttp.IsAllowed("file:///C:/Windows/win.ini"), "file:// refused");
    Check(!SafeHttp.IsAllowed("https://cdn1.epicgames.com.attacker.net/x.jpg"), "Epic look-alike refused");

    // Fake network to test redirects, limits and the API key
    var handler = new FakeHandler();
    SafeHttp.TestHandler = handler;
    var png = Png.Encode(new byte[64 * 64 * 4], 64, 64);
    handler.Routes["https://shared.akamai.steamstatic.com/ok.png"] = _ => Ok(png);
    handler.Routes["https://shared.akamai.steamstatic.com/to-evil"] = _ => Redirect("https://evil.com/x.png");
    handler.Routes["https://shared.akamai.steamstatic.com/to-http"] = _ => Redirect("http://shared.akamai.steamstatic.com/ok.png");
    handler.Routes["https://shared.akamai.steamstatic.com/to-ok"] = _ => Redirect("/ok.png");
    handler.Routes["https://shared.akamai.steamstatic.com/loop"] = _ => Redirect("/loop");
    handler.Routes["https://shared.akamai.steamstatic.com/huge"] = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) };
    handler.Routes["https://www.steamgriddb.com/api/v2/x"] = r => Redirect("https://cdn2.steamgriddb.com/leak");
    handler.Routes["https://cdn2.steamgriddb.com/leak"] = r => Ok(System.Text.Encoding.UTF8.GetBytes(r.Headers.Authorization?.ToString() ?? "none"));

    Check(SafeHttp.GetBytesAsync("https://shared.akamai.steamstatic.com/ok.png", 1 << 20).Result?.Length == png.Length, "allowed download works");
    Check(SafeHttp.GetBytesAsync("https://shared.akamai.steamstatic.com/to-evil", 1 << 20).Result is null, "redirect to unknown host refused");
    Check(SafeHttp.GetBytesAsync("https://shared.akamai.steamstatic.com/to-http", 1 << 20).Result is null, "redirect to HTTP refused");
    Check(SafeHttp.GetBytesAsync("https://shared.akamai.steamstatic.com/to-ok", 1 << 20).Result is not null, "same-site redirect followed");
    Check(SafeHttp.GetBytesAsync("https://shared.akamai.steamstatic.com/loop", 1 << 20).Result is null, "redirect loop stopped");
    Check(SafeHttp.GetBytesAsync("https://shared.akamai.steamstatic.com/huge", 1 << 20).Result is null, "endless/oversized response cut off");
    Eq("none", System.Text.Encoding.UTF8.GetString(SafeHttp.GetBytesAsync("https://www.steamgriddb.com/api/v2/x", 1 << 20, "SECRETKEY123").Result ?? []), "API key never forwarded to another host");
    Check(handler.Requests.All(u => u.StartsWith("https://") && !u.Contains("evil.com")), "no request ever reached a non-allowed host", string.Join(", ", handler.Requests));

    // Update check (GitHub releases)
    var v100 = new Version(1, 0, 0);
    Check(SafeHttp.IsAllowed("https://api.github.com/repos/a/b/releases/latest"), "GitHub API allowed for update checks");
    Check(!SafeHttp.IsAllowed("https://github.com/a/b/releases") && !SafeHttp.IsAllowed("https://objects.githubusercontent.com/x"), "only GitHub's API host, not downloads");
    Eq(new Version(1, 2, 3, 0), UpdateService.ParseTag("v1.2.3"), "tag v1.2.3");
    Eq(new Version(2, 0, 0, 0), UpdateService.ParseTag("2.0"), "tag 2.0");
    foreach (var bad in new[] { "latest", "v1", "1.2.3-beta", "v1.2.3/../../x", "1.2.3 ", "", "v99999999.0" })
        Check(UpdateService.ParseTag(bad) is null, $"bad tag refused: «{bad}»");
    var rel = UpdateService.Evaluate("""{"tag_name":"v1.0.1","name":"Playnest 1.0.1","body":"Fixes\r\n- quick play\u202E evil","html_url":"https://evil.com/x","draft":false,"prerelease":false}""", v100);
    Check(rel.Status == UpdateStatus.Available, "newer release found");
    Eq($"https://github.com/{UpdateService.Repository}/releases/tag/v1.0.1", rel.Info?.PageUrl, "release page is built locally, html_url from the response is ignored");
    Check(rel.Info?.Notes == "Fixes\n- quick play evil", "release notes are plain text without control/format characters", rel.Info?.Notes);
    Eq(UpdateStatus.UpToDate, UpdateService.Evaluate("""{"tag_name":"v1.0.0"}""", new Version(1, 0, 0, 0)).Status, "same version is up to date");
    Eq("What changed\n- **Faster** scans".Replace("**", ""), UpdateService.TidyMarkdown("## What changed\n- **Faster** scans"), "markdown headings/bold tidied");
    Eq(UpdateStatus.UpToDate, UpdateService.Evaluate("""{"tag_name":"v0.9.5"}""", v100).Status, "older release ignored");
    Eq(UpdateStatus.UpToDate, UpdateService.Evaluate("""{"tag_name":"v2.0.0","prerelease":true}""", v100).Status, "pre-release ignored");
    Eq(UpdateStatus.Failed, UpdateService.Evaluate("""{"tag_name":"v2.0.0-rc"}""", v100).Status, "odd tag = failed check");
    Eq(UpdateStatus.Failed, UpdateService.Evaluate("not json", v100).Status, "garbage = failed check");
    Eq(UpdateStatus.Failed, UpdateService.Evaluate("[1,2]", v100).Status, "wrong shape = failed check");
    Check(UpdateService.Evaluate("{\"tag_name\":\"v9.0.0\",\"body\":\"" + new string('x', 50_000) + "\"}", v100).Info?.Notes.Length <= 1501, "long notes are shortened");
    handler.Routes[UpdateService.LatestReleaseApiUrl] = _ => Ok(System.Text.Encoding.UTF8.GetBytes("""{"tag_name":"v3.1.0","name":"Big update"}"""));
    var live = UpdateService.CheckAsync(v100).Result;
    Check(live.Status == UpdateStatus.Available && live.Info!.Version == new Version(3, 1, 0, 0), "update check end-to-end through SafeHttp");
    handler.Routes[UpdateService.LatestReleaseApiUrl] = _ => Redirect("https://evil.com/release.json");
    Eq(UpdateStatus.Failed, UpdateService.CheckAsync(v100).Result.Status, "update check refuses redirect to another host");

    // What is accepted from responses
    var bomb = Png.Encode(new byte[4 * 4 * 4], 4, 4);
    BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(16), 60000); // header claims 60 000 px wide
    var big = new byte[600];
    bomb.CopyTo(big, 0);
    Check(!ArtworkService.IsImage(big), "image claiming absurd dimensions refused before decoding");
    Check(!ArtworkService.IsImage(System.Text.Encoding.UTF8.GetBytes("<html>" + new string('x', 1000))), "HTML error page is not treated as an image");
    Check(ArtworkService.IsSafeAssetPath("steam/apps/620/library_600x900_2x.jpg?t=1745363004"), "normal Steam asset path accepted");
    Check(!ArtworkService.IsSafeAssetPath("../../etc/passwd"), "path traversal in API data refused");
    Check(!ArtworkService.IsSafeAssetPath("@evil.com/x.jpg"), "host trick in API data refused");
    Check(!ArtworkService.IsSafeAssetPath("a b\r\nHost: evil"), "header injection in API data refused");
    Check(ArtworkService.IsValidKey("abcdef0123456789abcdef0123456789"), "normal API key accepted");
    Check(!ArtworkService.IsValidKey("abc\r\nX-Evil: 1"), "API key with header characters refused");
    Check(PeIcon.FromIcoBytes([0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 32, 0, 255, 255, 255, 127, 22, 0, 0, 0]) is null, "malformed downloaded .ico handled safely");
    SafeHttp.TestHandler = null;
}

Console.WriteLine($"\n{passed} passed, {failed} failed");
try
{
    if (failed == 0 && Environment.GetEnvironmentVariable("PLAYNEST_KEEP_TEST_FILES") is null) Directory.Delete(tmpRoot, true);
    else Console.WriteLine("Test files kept in " + tmpRoot);
}
catch { }
return failed == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------
static string[] FindSampleExes()
{
    var candidates = new List<string>();
    foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), "/root/.cache/uv", "/usr/lib/python3/dist-packages" })
    {
        if (!Directory.Exists(root)) continue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true }))
            {
                candidates.Add(f);
                if (candidates.Count >= 4) return candidates.ToArray();
            }
        }
        catch { }
    }
    return candidates.ToArray();
}

static void WriteAppInfo(string path, uint version, (uint Id, string Name, string Type, string? Exe)[] apps)
{
    bool v29 = version == 0x07564429;
    var table = new List<string>();
    int Key(string k)
    {
        var i = table.IndexOf(k);
        if (i >= 0) return i;
        table.Add(k);
        return table.Count - 1;
    }

    using var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);
    bw.Write(version);
    bw.Write(1u);
    long tableOffsetPos = ms.Position;
    if (v29) bw.Write(0L);

    void WriteKey(BinaryWriter w, string k)
    {
        if (v29) w.Write(Key(k));
        else { w.Write(Encoding.UTF8.GetBytes(k)); w.Write((byte)0); }
    }
    void Str(BinaryWriter w, string k, string v) { w.Write((byte)1); WriteKey(w, k); w.Write(Encoding.UTF8.GetBytes(v)); w.Write((byte)0); }
    void Obj(BinaryWriter w, string k) { w.Write((byte)0); WriteKey(w, k); }
    void End(BinaryWriter w) => w.Write((byte)8);

    foreach (var (id, name, type, exe) in apps)
    {
        using var body = new MemoryStream();
        var b = new BinaryWriter(body);
        b.Write(0u); b.Write(0u); b.Write(0UL); b.Write(new byte[20]); b.Write(0u); b.Write(new byte[20]);
        Obj(b, "appinfo");
        b.Write((byte)2); WriteKey(b, "appid"); b.Write((int)id);
        Obj(b, "common"); Str(b, "name", name); Str(b, "type", type); End(b);
        if (exe is not null)
        {
            Obj(b, "config");
            Obj(b, "launch");
            Obj(b, "0"); Str(b, "executable", exe); Obj(b, "config"); Str(b, "oslist", "windows"); End(b); End(b);
            End(b);
            End(b);
        }
        End(b);
        End(b);
        bw.Write(id);
        bw.Write((uint)body.Length);
        bw.Write(body.ToArray());
    }
    bw.Write(0u);
    if (v29)
    {
        long tableOffset = ms.Position;
        bw.Write((uint)table.Count);
        foreach (var s in table) { bw.Write(Encoding.UTF8.GetBytes(s)); bw.Write((byte)0); }
        ms.Position = tableOffsetPos;
        bw.Write(tableOffset);
    }
    File.WriteAllBytes(path, ms.ToArray());
}


static HttpResponseMessage Ok(byte[] body) => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

static HttpResponseMessage Redirect(string location)
{
    var r = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
    r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
    return r;
}

sealed class FakeHandler : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
    public List<string> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);
        return Task.FromResult(Routes.TryGetValue(url, out var f) ? f(request) : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

sealed class EndlessStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set { } }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'A', offset, count); return count; }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
