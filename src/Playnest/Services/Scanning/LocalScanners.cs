using System.Security.Cryptography;
using System.Text;
using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

/// <summary>
/// Scans the user's game folders (e.g. "D:\Games"). Every sub folder is inspected and only added
/// when it contains game evidence (engine files, store SDKs, etc.).
/// </summary>
public sealed class FolderScanner : IGameScanner
{
    public GameSource Source => GameSource.Folder;
    public bool IsEnabled(AppSettings s) => true;

    private static readonly string[] AutoFolderNames = ["Games", "Game", "My Games", "PC Games", "Jeux", "Spiele"];

    /// <summary>Folders to scan: the user's list plus (optionally) X:\Games on every drive.</summary>
    public static List<string> FoldersToScan(AppSettings s)
    {
        var list = new List<string>();
        foreach (var f in s.GameFolders)
            if (!string.IsNullOrWhiteSpace(f) && Directory.Exists(f)) list.Add(f);
        if (s.AutoDetectGameFolders)
            foreach (var drive in XboxScanner.SafeDrives())
                foreach (var n in AutoFolderNames)
                {
                    var p = Path.Combine(drive, n);
                    if (Directory.Exists(p) && !list.Any(x => PathUtil.Key(x) == PathUtil.Key(p))) list.Add(p);
                }
        return list;
    }

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var root in FoldersToScan(ctx.Settings))
            foreach (var e in ScanRoot(root, ctx, depth: 0))
                yield return e;
    }

    // Sub folder names that belong to a game's own layout, never a separate game.
    private static readonly HashSet<string> InternalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binaries", "engine", "content", "data", "x64", "x86", "win64", "win32", "game", "retail",
        "common", "launcher", "redist", "_commonredist", "support", "plugins", "mods", "dlc", "movies", "video", "videos",
        "sound", "sounds", "audio", "localization", "shaders", "assets", "resources", "packages", "archive", "paks", "base",
        "main", "program", "system", "runtime", "tools", "config", "saves", "logs",
    };

    // Folder names used by scene releases / repacks for extra files — never a separate game.
    private static readonly HashSet<string> ExtraNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "crack", "cracks", "codex", "skidrow", "empress", "fitgirl", "dodi", "plaza", "cpy", "rune", "tenoke", "flt", "razor1911",
        "update", "updates", "patch", "patches", "dlc", "dlcs", "bonus", "extras", "soundtrack", "ost", "artbook", "manual", "_redist", "redist",
    };

    // A lone child folder with one of these names is part of its parent game (e.g. "Soulframe\Downloaded\Public\Soulframe.exe").
    private static readonly HashSet<string> GenericChildNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloaded", "public", "game", "client", "live", "retail", "release", "app", "files", "install", "installed", "build",
        "windows", "windowsnoeditor", "win64", "x64", "bin", "binaries", "pc", "steam", "epic", "gog",
    };

    private IEnumerable<GameEntry> ScanRoot(string root, ScanContext ctx, int depth)
    {
        string[] subs;
        try { subs = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var dir in subs)
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (ctx.IsClaimed(dir) || ctx.IsExcluded(dir)) continue;
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.StartsWith('$')) continue;
            if (depth > 0 && (name.StartsWith('_') || InternalNames.Contains(name) || ExtraNames.Contains(name))) continue;
            if (GameClassifier.IsExcludedName(name, ctx.Settings.ExtraExcludeKeywords)) continue;

            // Publisher/collection folders like "D:\Games\Ubisoft\<game>": no exe of their own, games one level down.
            if (depth == 0 && !HasExe(dir) && !LooksLikeGameRoot(dir))
            {
                var nested = ScanRoot(dir, ctx, depth + 1).ToList();
                if (nested.Count > 0)
                {
                    if (BelongsToParent(nested))
                    {
                        // e.g. "Soulframe\Downloaded" → the game is "Soulframe"
                        var first = nested[0];
                        ctx.Claim(dir);
                        var unP = ctx.FindUninstallFor(dir);
                        yield return Make(dir, name, first.ExePath!, unP, first.Kind);
                    }
                    else
                    {
                        foreach (var n in nested) yield return n;
                    }
                    continue;
                }
            }

            var ev = GameClassifier.Inspect(dir, name, ctx.Token);
            if (ev.BestExe is null) continue;
            ItemKind kind;
            if (ev.IsGame && !(GameClassifier.LooksLikeToolName(name) && !ev.HasEngineMarker)) kind = ItemKind.Game;
            else if ((ev.IsElectron || ev.Score >= GameClassifier.GameThreshold) && GameClassifier.LooksLikeToolName(name) && ctx.Settings.DetectTools) kind = ItemKind.Tool;
            else continue;

            ctx.Claim(dir);
            yield return Make(dir, name, ev.BestExe, ctx.FindUninstallFor(dir), kind);
        }
    }

    private static GameEntry Make(string dir, string folderName, string exe, UninstallEntry? un, ItemKind kind) => new()
    {
        Id = "folder:" + StableId(dir),
        Name = un is not null ? PathUtil.CleanTitle(un.DisplayName) : PathUtil.TitleFromFolder(folderName),
        Kind = kind,
        Source = GameSource.Folder,
        InstallDir = dir,
        ExePath = exe,
        UninstallCommand = un?.UninstallString ?? FindLocalUninstaller(dir),
    };

    /// <summary>Unreal/Unity/Source-style game roots keep their exe in sub folders but are one game.</summary>
    private static bool LooksLikeGameRoot(string dir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, "Engine"))) return true;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var n = Path.GetFileName(sub);
                if (n.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(sub, "globalgamemanagers"))) return true;
                if (n.Equals("bin", StringComparison.OrdinalIgnoreCase) || n.Equals("binaries", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>True when what was found one level down is really the parent folder's game.</summary>
    private static bool BelongsToParent(List<GameEntry> nested)
    {
        if (nested.Count == 1 && GenericChildNames.Contains(Path.GetFileName(nested[0].InstallDir ?? ""))) return true;
        // Several copies of the same exe (original + patched) → one game
        return nested.Count > 1 && nested.Select(n => Path.GetFileName(n.ExePath ?? "")).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
    }

    private static bool HasExe(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.exe").Any(); } catch { return false; }
    }

    public static string? FindLocalUninstaller(string dir)
    {
        foreach (var n in new[] { "unins000.exe", "uninstall.exe", "Uninstall.exe", "uninst.exe" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return $"\"{p}\"";
        }
        return null;
    }

    public static string StableId(string path)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(PathUtil.Key(path)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

/// <summary>
/// Looks through Windows' installed-programs list for things installed outside the stores
/// (standalone installers, older launchers, repacks) and keeps only entries whose folder looks like a game.
/// </summary>
public sealed class InstalledProgramsScanner : IGameScanner
{
    public GameSource Source => GameSource.Registry;
    public bool IsEnabled(AppSettings s) => s.ScanInstalledPrograms;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var u in ctx.Uninstall)
        {
            ctx.Token.ThrowIfCancellationRequested();
            if (u.SystemComponent) continue;
            if (GameClassifier.IsExcludedName(u.DisplayName, ctx.Settings.ExtraExcludeKeywords)) continue;
            if (LauncherCatalog.MatchesAny(u)) continue;
            var dir = u.Folder;
            if (dir is null || ctx.IsClaimed(dir) || ctx.IsExcluded(dir)) continue;
            if (IsSystemFolder(dir)) continue;

            var ev = GameClassifier.Inspect(dir, u.DisplayName, ctx.Token);
            bool toolName = GameClassifier.LooksLikeToolName(u.DisplayName);
            ItemKind kind;
            if (ev.IsGame && !(toolName && !ev.HasEngineMarker)) kind = ItemKind.Game;
            else if ((ev.IsElectron || ev.Score >= GameClassifier.GameThreshold) && toolName && ctx.Settings.DetectTools) kind = ItemKind.Tool;
            else continue;

            var exe = PathUtil.ExeFromIconSpec(u.DisplayIcon);
            if (exe is null || PathUtil.LooksLikeUninstaller(exe) || !PathUtil.IsUnder(exe, dir)) exe = ev.BestExe;
            if (exe is null) continue;

            ctx.Claim(dir);
            yield return new GameEntry
            {
                Id = "installed:" + FolderScanner.StableId(dir),
                Name = PathUtil.CleanTitle(u.DisplayName),
                Kind = kind,
                Source = GameSource.Registry,
                SourceId = u.KeyName,
                Publisher = u.Publisher,
                InstallDir = dir,
                ExePath = exe,
                UninstallCommand = u.UninstallString,
            };
        }
    }

    private static bool IsSystemFolder(string dir)
    {
        var k = PathUtil.Key(dir);
        var win = PathUtil.Key(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var pf = PathUtil.Key(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var pf86 = PathUtil.Key(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        // A program installed directly as "C:\Program Files" (no sub folder) or inside Windows is never a game.
        return k == pf || k == pf86 || (win.Length > 0 && k.StartsWith(win)) || k.Length <= 3;
    }
}
