using System.Diagnostics;
using Playnest.Models;
using Playnest.Services.Platform;
using Playnest.Services.Scanning;

namespace Playnest.Services;

public enum UninstallOutcome { Started, OpenedStore, NoUninstaller, Failed }

/// <summary>Starting games, opening folders and running uninstallers.</summary>
public static class LaunchService
{
    public static void Launch(GameEntry e)
    {
        var uri = e.EffectiveUri;
        var exe = e.EffectiveExe;
        // A user-chosen exe beats the store URI; otherwise stores launch through their client (keeps DRM/cloud saves happy).
        if (!string.IsNullOrWhiteSpace(e.CustomExePath)) uri = e.CustomLaunchUri;

        if (!string.IsNullOrWhiteSpace(uri))
        {
            if (uri.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Start(new ProcessStartInfo("explorer.exe", uri) { UseShellExecute = true });
            else
                Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return;
        }

        if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("No program or link is set for this item. Use Edit to choose one.");
        if (!File.Exists(exe)) throw new FileNotFoundException("The program could not be found. It may have been moved or uninstalled.", exe);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Arguments = e.EffectiveArgs ?? "",
            WorkingDirectory = !string.IsNullOrWhiteSpace(e.WorkingDir) && Directory.Exists(e.WorkingDir) ? e.WorkingDir : Path.GetDirectoryName(exe)!,
        };
        if (e.RunAsAdmin) psi.Verb = "runas";
        Start(psi);
    }

    public static void OpenFolder(GameEntry e)
    {
        var dir = e.EffectiveDir;
        if (dir is null || !Directory.Exists(dir)) throw new DirectoryNotFoundException("The install folder no longer exists.");
        var exe = e.EffectiveExe;
        if (exe is not null && File.Exists(exe) && PathUtil.IsUnder(exe, dir) && Path.GetDirectoryName(exe) == dir)
            Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true });
        else
            Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public static void OpenPath(string path) => Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static bool CanUninstall(GameEntry e) =>
        !string.IsNullOrWhiteSpace(e.UninstallUri) || !string.IsNullOrWhiteSpace(e.UninstallCommand) ||
        (e.EffectiveDir is { } d && FolderScanner.FindLocalUninstaller(d) is not null);

    /// <summary>Starts the proper uninstaller. Returns the process when one was started directly (to wait for it).</summary>
    public static (UninstallOutcome Outcome, Process? Process) Uninstall(GameEntry e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(e.UninstallUri))
            {
                Start(new ProcessStartInfo(e.UninstallUri) { UseShellExecute = true });
                bool store = e.Source is GameSource.Epic or GameSource.Xbox;
                return (store ? UninstallOutcome.OpenedStore : UninstallOutcome.Started, null);
            }

            var cmd = e.UninstallCommand;
            if (string.IsNullOrWhiteSpace(cmd) && e.EffectiveDir is { } d) cmd = FolderScanner.FindLocalUninstaller(d);
            if (string.IsNullOrWhiteSpace(cmd)) return (UninstallOutcome.NoUninstaller, null);

            var (file, args) = PathUtil.SplitCommand(Environment.ExpandEnvironmentVariables(cmd));
            var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
            return (UninstallOutcome.Started, p);
        }
        catch (Exception ex)
        {
            Log.Error("uninstall " + e.DisplayName, ex);
            return (UninstallOutcome.Failed, null);
        }
    }

    private static void Start(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi);
    }
}

/// <summary>
/// Tracks how long games run — both games started from Playnest and (optionally) games started any other way
/// (Steam, desktop shortcuts, the game's own launcher). It matches running processes against the exe files inside each
/// game's install folder. Polling is light: every 30 s when only watching, every 5 s right after a launch, and it stops
/// completely when nothing needs watching.
/// </summary>
public sealed class SessionTracker : IDisposable
{
    private sealed class Session
    {
        public required GameEntry Game;
        public required string Dir;
        public DateTime LaunchedUtc = DateTime.UtcNow;
        public DateTime? StartedUtc;
        public int MissedPolls;
        public bool FromPlaynest;
    }

    private readonly object _gate = new();
    private readonly List<Session> _sessions = new();
    // exe name (without .exe) → games whose folder contains it
    private Dictionary<string, List<(GameEntry Game, string Dir)>> _index = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private int _periodMs;
    private bool _watchAll;
    private int _indexVersion;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(4);

    // Exe names that say nothing about which game is running.
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher", "game", "start", "play", "client", "app", "main", "run", "bootstrap", "setup", "install", "update", "updater",
        "unitycrashhandler64", "unitycrashhandler32", "crashreportclient", "crashpad_handler", "dxsetup", "dxwebsetup",
        "ue4prereqsetup_x64", "ue5prereqsetup_x64", "easyanticheat", "easyanticheat_setup", "easyanticheat_eos_setup", "beservice",
        "vc_redist.x64", "vc_redist.x86", "vcredist_x64", "vcredist_x86", "dotnetfx", "oalinst", "physxupdateloader",
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "cefprocess", "subprocess", "helper", "notification_helper",
        "renderer", "server", "editor", "uninstall", "unins000", "unins001", "7z", "7za", "python", "pythonw", "java", "javaw", "node",
    };

    public event Action<GameEntry>? GameStarted;
    public event Action<GameEntry, TimeSpan>? GameExited;

    public bool HasRunningGames
    {
        get { lock (_gate) return _sessions.Any(s => s.StartedUtc is not null && s.FromPlaynest); }
    }

    public bool IsRunning(GameEntry e)
    {
        lock (_gate) return _sessions.Any(s => s.Game == e && s.StartedUtc is not null);
    }

    /// <summary>Turns watching for games started outside Playnest on or off.</summary>
    public void SetWatchAll(bool on)
    {
        lock (_gate) _watchAll = on;
        Reschedule();
    }

    /// <summary>Rebuilds the exe-name index in the background (call after the library changes).</summary>
    public void UpdateIndex(IReadOnlyList<GameEntry> games)
    {
        int version = Interlocked.Increment(ref _indexVersion);
        var snapshot = games.Where(g => g.EffectiveKind == ItemKind.Game && g.EffectiveDir is not null)
                            .Select(g => (Game: g, Dir: g.EffectiveDir!)).ToList();
        Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false); // debounce bursts of changes
            if (version != Volatile.Read(ref _indexVersion)) return;
            var index = new Dictionary<string, List<(GameEntry, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (game, dir) in snapshot)
            {
                foreach (var name in ExeNames(dir))
                {
                    if (!index.TryGetValue(name, out var list)) index[name] = list = new();
                    if (!list.Any(x => x.Item1 == game)) list.Add((game, dir));
                }
            }
            // Names shared by many games (redistributables, engine tools) can't identify a game.
            foreach (var k in index.Where(kv => kv.Value.Count >= 3).Select(kv => kv.Key).ToList()) index.Remove(k);
            if (version != Volatile.Read(ref _indexVersion)) return;
            lock (_gate) _index = index;
            Reschedule();
        });
    }

    internal static IEnumerable<string> ExeNames(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
        };
        int n = 0;
        IEnumerator<string>? it = null;
        try { it = Directory.EnumerateFiles(dir, "*.exe", opts).GetEnumerator(); } catch { }
        if (it is null) yield break;
        using (it)
        {
            while (true)
            {
                string f;
                try
                {
                    if (!it.MoveNext()) break;
                    f = it.Current;
                }
                catch { break; }
                var name = Path.GetFileNameWithoutExtension(f);
                if (GenericNames.Contains(name) || PathUtil.LooksLikeUninstaller(f) || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("redist", StringComparison.OrdinalIgnoreCase) || name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return name;
                if (++n >= 300) break;
            }
        }
    }

    /// <summary>Called right after Playnest launches a game: watch closely until it shows up.</summary>
    public void Track(GameEntry e)
    {
        var dir = e.EffectiveDir;
        if (dir is null || !Directory.Exists(dir)) return;
        lock (_gate)
        {
            var existing = _sessions.FirstOrDefault(x => x.Game == e);
            if (existing is not null) { existing.FromPlaynest = true; return; }
            _sessions.Add(new Session { Game = e, Dir = dir, FromPlaynest = true });
        }
        Reschedule();

        // Make sure the launched game is matchable even if the index hasn't been built yet (off the UI thread).
        Task.Run(() =>
        {
            var names = ExeNames(dir).Take(100).ToList();
            lock (_gate)
            {
                var copy = new Dictionary<string, List<(GameEntry Game, string Dir)>>(_index, StringComparer.OrdinalIgnoreCase);
                foreach (var name in names)
                {
                    var list = copy.TryGetValue(name, out var l) ? new List<(GameEntry Game, string Dir)>(l) : new();
                    if (!list.Any(x => x.Game == e)) list.Add((e, dir));
                    copy[name] = list;
                }
                _index = copy; // replaced, never mutated, so a running Poll keeps a consistent view
            }
        });
    }

    private void Reschedule()
    {
        lock (_gate)
        {
            int period;
            if (_sessions.Any(s => s.StartedUtc is null)) period = 5000;              // a launch is pending
            else if (_sessions.Count > 0) period = 15000;                            // something is running
            else if (_watchAll && _index.Count > 0) period = 30000;                  // just watching
            else period = 0;                                                         // nothing to do

            if (period == _periodMs && (_timer is not null || period == 0)) return;
            _periodMs = period;
            _timer?.Dispose();
            _timer = period == 0 ? null : new Timer(_ => Poll(), null, Math.Min(period, 3000), period);
        }
    }

    private void Poll()
    {
        Dictionary<string, List<(GameEntry Game, string Dir)>> index;
        List<Session> sessions;
        bool watchAll;
        lock (_gate)
        {
            index = _index;
            sessions = _sessions.ToList();
            watchAll = _watchAll;
        }

        var running = new HashSet<GameEntry>();
        Process[] procs;
        try { procs = Process.GetProcesses(); } catch { return; }
        try
        {
            foreach (var p in procs)
            {
                string pname;
                try { pname = p.ProcessName; } catch { continue; }
                if (!index.TryGetValue(pname, out var candidates)) continue;
                string? path = null;
                bool pathRead = false;
                foreach (var (game, dir) in candidates)
                {
                    if (running.Contains(game)) continue;
                    bool watched = watchAll || sessions.Any(s => s.Game == game);
                    if (!watched) continue;
                    if (!pathRead) { path = Native.ProcessPath(p); pathRead = true; }
                    // Path unreadable (e.g. game runs as admin): trust the name only when it identifies one game.
                    if (path is null ? candidates.Count == 1 : PathUtil.IsUnder(path, dir)) running.Add(game);
                }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }

        var started = new List<GameEntry>();
        var exited = new List<(GameEntry, TimeSpan)>();
        lock (_gate)
        {
            foreach (var g in running)
            {
                var s = _sessions.FirstOrDefault(x => x.Game == g);
                if (s is null)
                {
                    s = new Session { Game = g, Dir = g.EffectiveDir ?? "", StartedUtc = DateTime.UtcNow };
                    _sessions.Add(s);
                    started.Add(g);
                }
                else if (s.StartedUtc is null)
                {
                    s.StartedUtc = DateTime.UtcNow;
                    started.Add(g);
                }
                s.MissedPolls = 0;
            }
            foreach (var s in _sessions.ToList())
            {
                if (running.Contains(s.Game)) continue;
                if (s.StartedUtc is null)
                {
                    if (DateTime.UtcNow - s.LaunchedUtc > StartTimeout) _sessions.Remove(s); // never started
                }
                else if (++s.MissedPolls >= 2)
                {
                    _sessions.Remove(s);
                    exited.Add((s.Game, DateTime.UtcNow - s.StartedUtc.Value));
                }
            }
        }
        foreach (var g in started) GameStarted?.Invoke(g);
        foreach (var (g, d) in exited) GameExited?.Invoke(g, d);
        if (started.Count > 0 || exited.Count > 0) Reschedule();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
