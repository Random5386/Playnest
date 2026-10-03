using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

// ============================================================================
// Epic Games Store — reads the launcher's *.item manifests.
// ============================================================================
public sealed class EpicScanner : IGameScanner
{
    public GameSource Source => GameSource.Epic;
    public bool IsEnabled(AppSettings s) => s.ScanEpic;

    public static string? ManifestDir
    {
        get
        {
            var data = PathUtil.Clean(Reg.Get(RegistryHive.LocalMachine, @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath"));
            var dir = data is not null ? Path.Combine(data, "Manifests") : null;
            if (dir is null || !Directory.Exists(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            return Directory.Exists(dir) ? dir : null;
        }
    }

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        var dir = ManifestDir;
        if (dir is null) yield break;
        foreach (var file in SafeFiles(dir, "*.item"))
        {
            var e = Parse(file, ctx);
            if (e is not null) yield return e;
        }
    }

    internal static GameEntry? Parse(string file, ScanContext ctx)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool Bool(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

            var appName = Str("AppName");
            var mainApp = Str("MainGameAppName");
            var loc = PathUtil.Clean(Str("InstallLocation"));
            if (loc is null || !Directory.Exists(loc) || Bool("bIsIncompleteInstall")) return null;
            if (mainApp.Length > 0 && !mainApp.Equals(appName, StringComparison.OrdinalIgnoreCase)) return null; // DLC

            var cats = new List<string>();
            if (r.TryGetProperty("AppCategories", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var x in c.EnumerateArray())
                    if (x.GetString() is { } s) cats.Add(s.ToLowerInvariant());

            var name = Str("DisplayName");
            bool isGame = cats.Contains("games") || cats.Contains("game");
            bool isSoftware = cats.Any(x => x is "applications" or "engines" or "plugins" or "software" or "digitalextras" or "addons");
            if (!isGame && isSoftware) { ctx.Claim(loc); return null; }
            if (!isGame && cats.Count == 0 && !GameClassifier.Inspect(loc, name, ctx.Token).IsGame) { ctx.Claim(loc); return null; }
            if (name.StartsWith("Unreal Engine", StringComparison.OrdinalIgnoreCase) || GameClassifier.IsExcludedName(name, ctx.Settings.ExtraExcludeKeywords))
            { ctx.Claim(loc); return null; }

            var ns = Str("CatalogNamespace");
            var item = Str("CatalogItemId");
            var exeRel = Str("LaunchExecutable");
            var exe = exeRel.Length > 0 ? Path.Combine(loc, exeRel) : null;
            // Escaped so a malformed manifest can't add extra parameters to the launcher link.
            var id = $"{Uri.EscapeDataString(ns)}%3A{Uri.EscapeDataString(item)}%3A{Uri.EscapeDataString(appName)}";
            long size = r.TryGetProperty("InstallSize", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var szv) ? szv : 0;
            ctx.Claim(loc);
            return new GameEntry
            {
                Id = "epic:" + appName,
                Name = PathUtil.CleanTitle(name),
                Kind = ItemKind.Game,
                Source = GameSource.Epic,
                SourceId = appName,
                SizeBytes = size,
                InstallDir = loc,
                ExePath = exe is not null && File.Exists(exe) ? exe : null,
                LaunchUri = $"com.epicgames.launcher://apps/{id}?action=launch&silent=true",
                // Epic has no public uninstall link; the library page is opened instead.
                UninstallUri = "com.epicgames.launcher://store/library",
            };
        }
        catch (Exception ex)
        {
            Log.Error("epic manifest " + file, ex);
            return null;
        }
    }

    internal static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return []; }
    }
}

// ============================================================================
// GOG — registry entries written by GOG installers / GOG Galaxy.
// ============================================================================
public sealed class GogScanner : IGameScanner
{
    public GameSource Source => GameSource.Gog;
    public bool IsEnabled(AppSettings s) => s.ScanGog;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var (key, v) in Reg.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\Games"))
        {
            v.TryGetValue("path", out var path);
            path = PathUtil.Clean(path);
            if (path is null || !Directory.Exists(path)) continue;
            if (v.TryGetValue("dependsOn", out var dep) && !string.IsNullOrWhiteSpace(dep)) continue; // DLC
            var gameId = v.TryGetValue("gameID", out var gid) ? gid : key;
            var name = v.TryGetValue("gameName", out var gn) ? gn : Path.GetFileName(path);
            if (GameClassifier.IsExcludedName(name, ctx.Settings.ExtraExcludeKeywords)) continue;

            string? exe = null, args = null, work = null;
            var info = ReadPlayTask(path, gameId);
            if (info is not null) (exe, args, work) = info.Value;
            if (exe is null && v.TryGetValue("exe", out var e) && File.Exists(PathUtil.Clean(e))) exe = PathUtil.Clean(e);
            if (args is null && v.TryGetValue("launchParam", out var lp) && !string.IsNullOrWhiteSpace(lp)) args = lp;

            string? uninstaller = null;
            foreach (var u in new[] { "unins000.exe", "unins001.exe" })
            {
                var p = Path.Combine(path, u);
                if (File.Exists(p)) { uninstaller = $"\"{p}\""; break; }
            }

            ctx.Claim(path);
            yield return new GameEntry
            {
                Id = "gog:" + gameId,
                Name = PathUtil.CleanTitle(name),
                Kind = ItemKind.Game,
                Source = GameSource.Gog,
                SourceId = gameId,
                InstallDir = path,
                ExePath = exe,
                Arguments = args,
                WorkingDir = work,
                UninstallCommand = uninstaller ?? ctx.FindUninstallFor(path)?.UninstallString,
            };
        }
    }

    private static (string? Exe, string? Args, string? Work)? ReadPlayTask(string dir, string id)
    {
        var file = Path.Combine(dir, $"goggame-{id}.info");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("playTasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return null;
            foreach (var t in tasks.EnumerateArray())
            {
                bool primary = t.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True;
                if (!primary) continue;
                if (!t.TryGetProperty("path", out var pathEl)) continue;
                var exe = Path.Combine(dir, pathEl.GetString() ?? "");
                var args = t.TryGetProperty("arguments", out var a) ? a.GetString() : null;
                var work = t.TryGetProperty("workingDir", out var w) ? Path.Combine(dir, w.GetString() ?? "") : null;
                return (File.Exists(exe) ? exe : null, args, work);
            }
        }
        catch { }
        return null;
    }
}

// ============================================================================
// Ubisoft Connect
// ============================================================================
public sealed class UbisoftScanner : IGameScanner
{
    public GameSource Source => GameSource.Ubisoft;
    public bool IsEnabled(AppSettings s) => s.ScanUbisoft;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var (id, v) in Reg.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Ubisoft\Launcher\Installs"))
        {
            if (!id.All(char.IsAsciiDigit) || !v.TryGetValue("InstallDir", out var dir)) continue; // ids are numbers
            dir = PathUtil.Clean(dir)?.Replace('/', '\\');
            if (dir is null || !Directory.Exists(dir)) continue;

            var un = ctx.Uninstall.FirstOrDefault(u => u.KeyName.Equals("Uplay Install " + id, StringComparison.OrdinalIgnoreCase))
                     ?? ctx.FindUninstallFor(dir);
            var name = un?.DisplayName ?? Path.GetFileName(dir);
            if (GameClassifier.IsExcludedName(name, ctx.Settings.ExtraExcludeKeywords)) continue;

            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = "ubisoft:" + id,
                Name = PathUtil.CleanTitle(name),
                Kind = ItemKind.Game,
                Source = GameSource.Ubisoft,
                SourceId = id,
                InstallDir = dir,
                ExePath = PathUtil.ExeFromIconSpec(un?.DisplayIcon) is { } icon && !PathUtil.LooksLikeUninstaller(icon) ? icon : null,
                LaunchUri = $"uplay://launch/{id}/0",
                UninstallUri = $"uplay://uninstall/{id}",
            };
        }
    }
}

// ============================================================================
// EA app (and legacy Origin) — EA games register uninstallers that point to EAInstaller / Touchup.
// ============================================================================
public sealed class EaScanner : IGameScanner
{
    public GameSource Source => GameSource.EA;
    public bool IsEnabled(AppSettings s) => s.ScanEA;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var u in ctx.Uninstall)
        {
            var un = u.UninstallString ?? "";
            bool ea = un.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase)
                      || un.Contains("Touchup.exe", StringComparison.OrdinalIgnoreCase)
                      || un.Contains(@"EA Desktop\", StringComparison.OrdinalIgnoreCase)
                      || un.Contains("__Installer", StringComparison.OrdinalIgnoreCase);
            if (!ea) continue;
            var dir = u.Folder;
            if (dir is null || ctx.IsClaimed(dir)) continue;
            if (GameClassifier.IsExcludedName(u.DisplayName, ctx.Settings.ExtraExcludeKeywords) || u.DisplayName is "EA app" or "Origin") continue;

            var exe = PathUtil.ExeFromIconSpec(u.DisplayIcon);
            if (exe is not null && PathUtil.LooksLikeUninstaller(exe)) exe = null;
            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = "ea:" + PathUtil.NormalizeName(u.DisplayName),
                Name = PathUtil.CleanTitle(u.DisplayName),
                Kind = ItemKind.Game,
                Source = GameSource.EA,
                SourceId = u.KeyName,
                Publisher = u.Publisher,
                InstallDir = dir,
                ExePath = exe ?? GameClassifier.Inspect(dir, u.DisplayName, ctx.Token).BestExe,
                UninstallCommand = u.UninstallString,
            };
        }
    }
}

// ============================================================================
// Battle.net
// ============================================================================
public sealed partial class BattleNetScanner : IGameScanner
{
    // Uninstaller --uid → Battle.net launch code
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prometheus"] = "Pro", ["fenris"] = "Fen", ["diablo3"] = "D3", ["osi"] = "OSI", ["wow"] = "WoW",
        ["wow_classic"] = "WoWC", ["wow_classic_era"] = "WoWC", ["hs_beta"] = "WTCG", ["heroes"] = "Hero",
        ["s2"] = "S2", ["s1"] = "S1", ["w3"] = "W3", ["rtro"] = "RTRO", ["anbs"] = "ANBS", ["odin"] = "ODIN",
        ["viper"] = "VIPR", ["lazarus"] = "LAZR", ["zeus"] = "ZEUS", ["fore"] = "FORE", ["auks"] = "AUKS",
        ["wlby"] = "WLBY", ["gryphon"] = "GRY", ["aqua"] = "AQUA",
    };

    public GameSource Source => GameSource.BattleNet;
    public bool IsEnabled(AppSettings s) => s.ScanBattleNet;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        string? bnet = LauncherCatalog.Resolve(LauncherCatalog.All.First(d => d.Id == "battlenet"), ctx.Uninstall)?.ExePath;
        foreach (var u in ctx.Uninstall)
        {
            var un = u.UninstallString ?? "";
            if (!un.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase) &&
                !un.Contains(@"Battle.net\Agent", StringComparison.OrdinalIgnoreCase)) continue;
            if (u.DisplayName.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = u.Folder;
            if (dir is null || ctx.IsClaimed(dir)) continue;

            var uid = UidRegex().Match(un) is { Success: true } m ? m.Groups[1].Value : null;
            string? uri = null, exe = null, args = null;
            if (uid is not null && Codes.TryGetValue(uid, out var code) && bnet is not null)
            {
                exe = bnet;
                args = $"--exec=\"launch {code}\"";
            }
            var mainExe = PathUtil.ExeFromIconSpec(u.DisplayIcon) ?? GameClassifier.Inspect(dir, u.DisplayName, ctx.Token).BestExe;
            if (exe is null) exe = mainExe;

            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = "battlenet:" + (uid ?? PathUtil.NormalizeName(u.DisplayName)),
                Name = PathUtil.CleanTitle(u.DisplayName),
                Kind = ItemKind.Game,
                Source = GameSource.BattleNet,
                SourceId = uid,
                InstallDir = dir,
                ExePath = exe,
                Arguments = args,
                LaunchUri = uri,
                UninstallCommand = u.UninstallString,
            };
        }
    }

    [GeneratedRegex(@"--uid=([A-Za-z0-9_]+)")]
    private static partial Regex UidRegex();
}

// ============================================================================
// Rockstar Games Launcher
// ============================================================================
public sealed class RockstarScanner : IGameScanner
{
    public GameSource Source => GameSource.Rockstar;
    public bool IsEnabled(AppSettings s) => s.ScanRockstar;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var (key, v) in Reg.SubKeys(RegistryHive.LocalMachine, @"SOFTWARE\Rockstar Games"))
        {
            if (key.Contains("Launcher", StringComparison.OrdinalIgnoreCase) || key.Contains("Social Club", StringComparison.OrdinalIgnoreCase)) continue;
            if (!v.TryGetValue("InstallFolder", out var dir)) continue;
            dir = PathUtil.Clean(dir);
            if (dir is null || !Directory.Exists(dir) || ctx.IsClaimed(dir)) continue;
            var un = ctx.FindUninstallFor(dir);
            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = "rockstar:" + PathUtil.NormalizeName(key),
                Name = un?.DisplayName is { Length: > 0 } dn ? PathUtil.CleanTitle(dn) : key,
                Kind = ItemKind.Game,
                Source = GameSource.Rockstar,
                SourceId = key,
                InstallDir = dir,
                ExePath = GameClassifier.Inspect(dir, key, ctx.Token).BestExe,
                UninstallCommand = un?.UninstallString,
            };
        }
    }
}

// ============================================================================
// Riot Games — reads ProgramData\Riot Games metadata.
// ============================================================================
public sealed partial class RiotScanner : IGameScanner
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT", ["league_of_legends"] = "League of Legends", ["bacon"] = "Legends of Runeterra", ["lion"] = "2XKO",
    };

    public GameSource Source => GameSource.Riot;
    public bool IsEnabled(AppSettings s) => s.ScanRiot;

    public static string RiotData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");

    public static string? ClientPath
    {
        get
        {
            var f = Path.Combine(RiotData, "RiotClientInstalls.json");
            if (!File.Exists(f)) return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var k in new[] { "rc_default", "rc_live", "rc_beta" })
                    if (doc.RootElement.TryGetProperty(k, out var v) && PathUtil.Clean(v.GetString())?.Replace('/', '\\') is { } p && File.Exists(p))
                        return p;
            }
            catch { }
            return null;
        }
    }

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        var meta = Path.Combine(RiotData, "Metadata");
        var client = ClientPath;
        if (!Directory.Exists(meta) || client is null) yield break;
        string[] dirs;
        try { dirs = Directory.GetDirectories(meta); } catch { yield break; }
        foreach (var d in dirs)
        {
            var folder = Path.GetFileName(d); // e.g. valorant.live
            var dot = folder.IndexOf('.');
            if (dot <= 0) continue;
            var product = folder[..dot];
            var patchline = folder[(dot + 1)..];
            if (product.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) continue;
            // These end up as command-line arguments, so only plain names are accepted.
            if (!product.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !patchline.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) continue;
            var yaml = Path.Combine(d, $"{folder}.product_settings.yaml");
            if (!File.Exists(yaml)) continue;
            string? install = null;
            try
            {
                var m = InstallPathRegex().Match(File.ReadAllText(yaml));
                if (m.Success) install = PathUtil.Clean(m.Groups[1].Value)?.Replace('/', '\\');
            }
            catch { }
            if (install is null || !Directory.Exists(install)) continue;

            ctx.Claim(install);
            yield return new GameEntry
            {
                Id = $"riot:{product}",
                Name = Names.TryGetValue(product, out var n) ? n : Path.GetFileName(install),
                Kind = ItemKind.Game,
                Source = GameSource.Riot,
                SourceId = product,
                InstallDir = install,
                ExePath = client,
                Arguments = $"--launch-product={product} --launch-patchline={patchline}",
                UninstallCommand = $"\"{client}\" --uninstall-product={product} --uninstall-patchline={patchline}",
            };
        }
    }

    [GeneratedRegex(@"product_install_full_path:\s*""?([^""\r\n]+)""?")]
    private static partial Regex InstallPathRegex();
}

// ============================================================================
// Amazon Games — entries whose uninstaller is "Amazon Game Remover".
// ============================================================================
public sealed partial class AmazonScanner : IGameScanner
{
    public GameSource Source => GameSource.Amazon;
    public bool IsEnabled(AppSettings s) => s.ScanAmazon;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var u in ctx.Uninstall)
        {
            var un = u.UninstallString ?? "";
            if (!un.Contains("Amazon Game Remover", StringComparison.OrdinalIgnoreCase)) continue;
            var id = IdRegex().Match(un) is { Success: true } m ? m.Groups[1].Value : null;
            var dir = u.Folder;
            if (id is null || dir is null || ctx.IsClaimed(dir)) continue;
            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = "amazon:" + id,
                Name = PathUtil.CleanTitle(u.DisplayName),
                Kind = ItemKind.Game,
                Source = GameSource.Amazon,
                SourceId = id,
                InstallDir = dir,
                ExePath = GameClassifier.Inspect(dir, u.DisplayName, ctx.Token).BestExe,
                LaunchUri = $"amazon-games://play/{id}",
                UninstallCommand = u.UninstallString,
            };
        }
    }

    [GeneratedRegex(@"-p\s+(amzn1\.[A-Za-z0-9.\-]+)")]
    private static partial Regex IdRegex();
}
