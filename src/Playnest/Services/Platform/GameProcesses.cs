using System.ComponentModel;
using System.Diagnostics;

namespace Playnest.Services.Platform;

/// <summary>One program that belongs to a running game.</summary>
public sealed record GameProcess(int Id, string Name, string? Path, DateTime? StartTime);

/// <summary>
/// Finds and force-stops the programs of one game. The rules keep this from ever touching anything else:
/// <list type="bullet">
/// <item>Only programs whose exe lives inside the game's own install folder are ended.</item>
/// <item>That folder must be specific to the game: not a drive root, a Windows/system folder, a store's
///       library folder (e.g. steamapps\common) or a folder that contains a store client or Playnest itself.</item>
/// <item>Store clients, Windows processes and Playnest are never ended, wherever they are.</item>
/// <item>Each program is checked again just before it is ended, so a reused process id can't hit a different program.</item>
/// <item>Only the listed programs are ended (not their whole process tree).</item>
/// </list>
/// </summary>
public static class GameProcesses
{
    private static readonly HashSet<string> NeverStop = new(StringComparer.OrdinalIgnoreCase)
    {
        // Windows
        "explorer", "dwm", "csrss", "winlogon", "wininit", "services", "lsass", "svchost", "smss", "system", "idle",
        "conhost", "taskmgr", "sihost", "fontdrvhost", "ctfmon", "runtimebroker", "searchhost", "startmenuexperiencehost",
        // Store clients and their helpers
        "steam", "steamwebhelper", "steamservice", "gameoverlayui", "epicgameslauncher", "epicwebhelper", "epiconlineservices",
        "epiconlineserviceshost", "galaxyclient", "galaxyclient helper", "goggalaxy", "eadesktop", "eabackgroundservice",
        "ealauncher", "origin", "upc", "uplaywebcore", "ubisoftconnect", "battle.net", "agent", "riotclientservices",
        "riotclientux", "riotclientuxrender", "rockstarservice", "socialclubhelper", "xboxpcapp", "gamingservices",
        // Ourselves
        "playnest",
    };

    /// <summary>Folder names that hold many games (a library), never a single game.</summary>
    private static readonly HashSet<string> LibraryFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "common", "steamapps", "steamlibrary", "steam", "epic games", "xboxgames", "windowsapps", "games", "gog games", "gog galaxy",
        "ubisoft game launcher", "ubisoft", "ea games", "origin games", "electronic arts", "battle.net", "riot games",
        "rockstar games", "amazon games", "program files", "program files (x86)", "programdata", "users",
    };

    /// <summary>Common shared user folders (also when they sit somewhere other than the profile, e.g. inside OneDrive or on D:).</summary>
    private static readonly HashSet<string> UserLibraryFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "downloads", "desktop", "documents", "my documents", "pictures", "music", "videos", "onedrive", "saved games",
        "dropbox", "google drive", "icloud drive", "temp", "tmp",
    };

    /// <summary>Store client exes: a folder that contains one of these is a launcher's folder, not a game's.</summary>
    private static readonly string[] ClientExes =
    [
        "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "EADesktop.exe", "UbisoftConnect.exe", "upc.exe",
        "Battle.net.exe", "RiotClientServices.exe", "LauncherPatcher.exe" /* Rockstar Games Launcher */,
    ];

    /// <summary>True when ending every program inside <paramref name="dir"/> can only affect one game.</summary>
    public static bool IsSafeGameFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return false; }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || PathUtil.Key(full) == PathUtil.Key(root)) return false; // a whole drive
        if (LibraryFolderNames.Contains(Path.GetFileName(full))) return false;

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonProgramFiles,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                     Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.StartMenu,
                 })
        {
            string path;
            try { path = Environment.GetFolderPath(special); } catch { continue; }
            if (string.IsNullOrEmpty(path)) continue;
            // The folder itself, or anything that contains it (e.g. C:\Users), is never a game folder.
            if (PathUtil.IsUnder(path, full)) return false;
            // Nothing inside Windows' own folders is a game.
            if (special is Environment.SpecialFolder.Windows or Environment.SpecialFolder.System && PathUtil.IsUnder(full, path)) return false;
        }

        // The user's own top-level folders (Downloads, Desktop, Documents, OneDrive, Saved Games…) hold many
        // unrelated things, so they're never treated as one game's folder, even if a game exe was added from there.
        string profile = "";
        try { profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
        if (profile.Length > 0 && Path.GetDirectoryName(full) is { } parent && PathUtil.Key(parent) == PathUtil.Key(profile)) return false;
        if (UserLibraryFolderNames.Contains(Path.GetFileName(full))) return false;

        // Playnest itself must never be inside the folder.
        if (PathUtil.IsUnder(AppContext.BaseDirectory, full)) return false;

        // A store client's own folder (e.g. Steam's) is not a game folder.
        foreach (var exe in ClientExes)
        {
            try { if (File.Exists(Path.Combine(full, exe))) return false; } catch { }
        }
        return true;
    }

    /// <summary>Process name without ".exe" (Linux keeps the extension in the name).</summary>
    internal static string NormalizeName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Names (without .exe) of every exe inside the folder.</summary>
    internal static HashSet<string> AllExeNames(string dir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
            };
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", opts))
            {
                names.Add(Path.GetFileNameWithoutExtension(f));
                if (names.Count >= 2000) break;
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// The game's running programs. A program counts when its exe is inside <paramref name="dir"/>. When Windows
    /// won't say where a program's exe is (it runs as administrator), its name must be one that only this game uses.
    /// </summary>
    public static List<GameProcess> Find(string dir, IReadOnlySet<string> namesOnlyThisGameUses)
    {
        var result = new List<GameProcess>();
        if (!IsSafeGameFolder(dir)) return result;
        var names = AllExeNames(dir);
        if (names.Count == 0) return result;
        int self = Environment.ProcessId;

        Process[] procs;
        try { procs = Process.GetProcesses(); } catch { return result; }
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == self) continue;
                    var name = NormalizeName(p.ProcessName);
                    if (!names.Contains(name) || NeverStop.Contains(name)) continue;
                    var path = Native.ProcessPath(p);
                    bool belongs = path is not null ? PathUtil.IsUnder(path, dir) : namesOnlyThisGameUses.Contains(name);
                    if (!belongs) continue;
                    DateTime? started = null;
                    try { started = p.StartTime; } catch { }
                    result.Add(new GameProcess(p.Id, name, path, started));
                }
                catch { /* the process ended meanwhile */ }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
        return result;
    }

    /// <summary>Ends the given programs. Returns how many were ended and how many Windows refused to end.</summary>
    public static (int Stopped, int Denied) Stop(IEnumerable<GameProcess> list)
    {
        int stopped = 0, denied = 0;
        foreach (var gp in list)
        {
            Process p;
            try { p = Process.GetProcessById(gp.Id); }
            catch (ArgumentException) { stopped++; continue; } // already gone
            using (p)
            {
                try
                {
                    // Same program as the one the user confirmed? (process ids get reused)
                    if (!string.Equals(NormalizeName(p.ProcessName), gp.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (gp.StartTime is DateTime t)
                    {
                        DateTime now;
                        try { now = p.StartTime; } catch { now = t; }
                        if (now != t) continue;
                    }
                    p.Kill(entireProcessTree: false);
                    p.WaitForExit(3000);
                    stopped++;
                }
                catch (InvalidOperationException) { stopped++; }     // exited meanwhile
                catch (Win32Exception) { denied++; }                  // access denied (admin / anti-cheat)
                catch (NotSupportedException) { denied++; }
            }
        }
        return (stopped, denied);
    }
}
