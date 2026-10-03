using Microsoft.Win32;
using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

public sealed class SteamScanner : IGameScanner
{
    // Tools/runtimes that are installed like games but are not games.
    private static readonly HashSet<uint> NonGameIds =
    [
        228980,  // Steamworks Common Redistributables
        1070560, // Steam Linux Runtime
        1391110, // Steam Linux Runtime - Soldier
        1628350, // Steam Linux Runtime - Sniper
        1493710, // Proton Experimental
        250820,  // SteamVR
        1826330, // Proton EasyAntiCheat Runtime
        1161040, // Proton BattlEye Runtime
        431960,  // Wallpaper Engine
        1007,    // Steam client
    ];

    public GameSource Source => GameSource.Steam;
    public bool IsEnabled(AppSettings s) => s.ScanSteam;

    /// <summary>Steam's install folder, or null when Steam is not installed.</summary>
    public static string? SteamPath
    {
        get
        {
            var p = PathUtil.Clean(Reg.Get(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"))
                    ?? PathUtil.Clean(Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
            p = p?.Replace('/', Path.DirectorySeparatorChar);
            return p is not null && Directory.Exists(p) ? p : null;
        }
    }

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        var steam = SteamPath;
        if (steam is null) yield break;

        var manifests = new List<(VdfNode App, string Library)>();
        foreach (var lib in LibraryFolders(steam))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(apps, "appmanifest_*.acf").ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    var node = Vdf.ParseFile(f)["AppState"];
                    if (node is not null) manifests.Add((node, lib));
                }
                catch (Exception ex) { Log.Error("steam manifest " + f, ex); }
            }
        }

        var ids = manifests.Select(m => (uint)m.App.Long("appid")).Where(i => i != 0).ToHashSet();
        Dictionary<uint, SteamAppMeta> meta = new();
        try { meta = SteamAppInfo.Read(Path.Combine(steam, "appcache", "appinfo.vdf"), ids); }
        catch (Exception ex) { Log.Error("steam appinfo", ex); }

        var playtime = ctx.Settings.ImportSteamPlaytime ? ReadPlaytime(steam) : new();

        foreach (var (app, lib) in manifests)
        {
            ctx.Token.ThrowIfCancellationRequested();
            uint id = (uint)app.Long("appid");
            if (id == 0 || NonGameIds.Contains(id)) continue;
            var name = app.Str("name") ?? $"Steam app {id}";
            var installDirName = app.Str("installdir");
            if (string.IsNullOrEmpty(installDirName)) continue;
            var dir = Path.Combine(lib, "steamapps", "common", installDirName);
            long flags = app.Long("StateFlags");
            bool installed = (flags & 4) != 0 && Directory.Exists(dir);
            if (!installed) continue;

            meta.TryGetValue(id, out var m);
            var type = m?.Type?.ToLowerInvariant();
            ItemKind kind = ItemKind.Game;
            if (type is "tool" or "application" or "config")
            {
                if (!ctx.Settings.IncludeSteamSoftwareAsTools) { ctx.Claim(dir); continue; }
                kind = ItemKind.Tool;
            }
            else if (type is "music" or "video" or "dlc" or "advertising" or "series" or "episode")
            {
                ctx.Claim(dir);
                continue;
            }
            else if (type is null && GameClassifier.IsExcludedName(name, ctx.Settings.ExtraExcludeKeywords))
            {
                ctx.Claim(dir);
                continue;
            }

            string? exe = null;
            if (m?.Executable is { } rel)
            {
                var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) exe = full;
            }

            playtime.TryGetValue(id, out var pt);
            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = $"steam:{id}",
                Name = PathUtil.CleanTitle(m?.Name ?? name),
                Kind = kind,
                Source = GameSource.Steam,
                SourceId = id.ToString(),
                SteamAppId = id,
                SteamClientIcon = m?.ClientIcon,
                SizeBytes = app.Long("SizeOnDisk"),
                InstallDir = dir,
                ExePath = exe,
                LaunchUri = $"steam://rungameid/{id}",
                UninstallUri = $"steam://uninstall/{id}",
                ImportedPlaytimeSeconds = pt.Minutes * 60,
                LastPlayedUtc = pt.LastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(pt.LastPlayed).UtcDateTime : null,
            };
        }
    }

    public static List<string> LibraryFolders(string steam)
    {
        var list = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) vdf = Path.Combine(steam, "config", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return list;
        try
        {
            var root = Vdf.ParseFile(vdf);
            var lf = root["libraryfolders"] ?? root["LibraryFolders"];
            if (lf is null) return list;
            foreach (var (_, node) in lf.Children)
            {
                // New format: "0" { "path" "D:\\SteamLibrary" }, old format: "1" "D:\\SteamLibrary"
                var path = node.Value ?? node.Str("path");
                path = PathUtil.Clean(path)?.Replace('/', Path.DirectorySeparatorChar);
                if (path is not null && Directory.Exists(path) && !list.Any(p => PathUtil.Key(p) == PathUtil.Key(path)))
                    list.Add(path);
            }
        }
        catch (Exception ex) { Log.Error("steam libraryfolders", ex); }
        return list;
    }

    /// <summary>Reads per-user playtime (minutes) and last played time from Steam's localconfig.vdf. Takes the max over all users.</summary>
    public static Dictionary<uint, (long Minutes, long LastPlayed)> ReadPlaytime(string steam)
    {
        var result = new Dictionary<uint, (long, long)>();
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return result;
        try
        {
            foreach (var user in Directory.EnumerateDirectories(userdata))
            {
                var cfg = Path.Combine(user, "config", "localconfig.vdf");
                if (!File.Exists(cfg)) continue;
                try
                {
                    // Keys are case-insensitive, so "valve"/"Valve" both match.
                    var apps = Vdf.ParseFile(cfg).Path("UserLocalConfigStore/Software/Valve/Steam/apps");
                    if (apps is null) continue;
                    foreach (var (key, node) in apps.Children)
                    {
                        if (!uint.TryParse(key, out var id)) continue;
                        long minutes = node.Long("Playtime");
                        long last = node.Long("LastPlayed");
                        if (result.TryGetValue(id, out var cur))
                            result[id] = (Math.Max(cur.Item1, minutes), Math.Max(cur.Item2, last));
                        else
                            result[id] = (minutes, last);
                    }
                }
                catch (Exception ex) { Log.Error("steam localconfig", ex); }
            }
        }
        catch { }
        return result;
    }
}
