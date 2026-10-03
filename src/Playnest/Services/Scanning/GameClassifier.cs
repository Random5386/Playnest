using System.Text.RegularExpressions;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

/// <summary>Result of looking inside a folder.</summary>
public sealed record FolderEvidence(int Score, string? BestExe, IReadOnlyList<string> Reasons, bool HasEngineMarker = false, bool IsElectron = false)
{
    /// <summary>Electron apps (resources\app.asar) are desktop apps, never games, whatever DLLs they bundle.</summary>
    public bool IsGame => Score >= GameClassifier.GameThreshold && !IsElectron;
}

/// <summary>
/// Decides whether something is a game by looking for engine files, store SDKs, anti-cheat and game middleware.
/// Ordinary applications almost never ship these, which is what lets Playnest skip apps automatically.
/// </summary>
public static partial class GameClassifier
{
    public const int GameThreshold = 10;
    private const int MaxEntries = 6000;
    private const int MaxDepth = 4;

    private static readonly string[] SkipDirs =
    [
        "_commonredist", "commonredist", "redist", "redistributables", "directx", "vcredist", "__installer",
        "support", "dotnet", "prerequisites", "_redist", "installers", "$recycle.bin", "node_modules", ".git",
        "shadercache", "logs", "crashes", "screenshots", "saves", "savegames",
    ];

    // Definite: engines, store SDKs, anti-cheat (+10 each)
    private static readonly (string File, string Why)[] StrongFiles =
    [
        ("unityplayer.dll", "Unity engine"),
        ("gameassembly.dll", "Unity IL2CPP"),
        ("steam_api.dll", "Steam SDK"),
        ("steam_api64.dll", "Steam SDK"),
        ("eossdk-win64-shipping.dll", "Epic Online Services"),
        ("eossdk-win32-shipping.dll", "Epic Online Services"),
        ("galaxy64.dll", "GOG Galaxy SDK"),
        ("galaxy.dll", "GOG Galaxy SDK"),
        ("uplay_r1_loader64.dll", "Ubisoft SDK"),
        ("uplay_r1_loader.dll", "Ubisoft SDK"),
        ("upc_r2_loader64.dll", "Ubisoft SDK"),
        ("uplay_r164.dll", "Ubisoft SDK"),
        ("crysystem.dll", "CryEngine"),
        ("re_chunk_000.pak", "RE Engine"),
        ("data.win", "GameMaker"),
        ("game.rgss3a", "RPG Maker"),
        ("game.rgssad", "RPG Maker"),
        ("easyanticheat_x64.dll", "Easy Anti-Cheat"),
        ("easyanticheat_eos_setup.exe", "Easy Anti-Cheat"),
        ("beclient_x64.dll", "BattlEye"),
        ("layout.toc", "Frostbite"),
        ("denuvo64.dll", "Denuvo"),
        ("gfsdk_aftermath_lib.x64.dll", "NVIDIA game SDK"),
        ("discord_game_sdk.dll", "Discord Game SDK"),
        ("xgameruntime.dll", "Xbox GDK"),
        ("microsoftgame.config", "Xbox GDK"),
    ];

    // Middleware that is (almost) only used by games (+4 each, capped)
    private static readonly (string File, string Why)[] MediumFiles =
    [
        ("bink2w64.dll", "Bink video"), ("binkw64.dll", "Bink video"), ("binkw32.dll", "Bink video"), ("bink2w32.dll", "Bink video"),
        ("physx3_x64.dll", "PhysX"), ("physx_64.dll", "PhysX"), ("physxloader.dll", "PhysX"), ("physx3common_x64.dll", "PhysX"),
        ("fmod.dll", "FMOD audio"), ("fmodex64.dll", "FMOD audio"), ("fmodstudio.dll", "FMOD audio"), ("fmod64.dll", "FMOD audio"),
        ("nvngx_dlss.dll", "DLSS"), ("nvngx_dlssg.dll", "DLSS"), ("sl.interposer.dll", "NVIDIA Streamline"),
        ("libxess.dll", "XeSS"), ("amd_fidelityfx_dx12.dll", "FSR"), ("ffx_fsr2_api_x64.dll", "FSR"),
        ("amd_ags_x64.dll", "AMD AGS"), ("xinput1_3.dll", "XInput"), ("aksoundengine.dll", "Wwise"),
        ("oo2core_9_win64.dll", "Oodle"), ("oo2core_8_win64.dll", "Oodle"), ("oo2core_7_win64.dll", "Oodle"), ("oo2core_5_win64.dll", "Oodle"),
        ("gfsdk_ssao_d3d11.win64.dll", "NVIDIA GameWorks"), ("nvtt.dll", "NVIDIA texture tools"), ("havok.dll", "Havok"),
        ("d3dx9_43.dll", "DirectX 9 runtime"), ("x3daudio1_7.dll", "XAudio"), ("openal32.dll", "OpenAL"),
        ("sdl2.dll", "SDL"), ("sdl3.dll", "SDL"), ("mono-2.0-bdwgc.dll", "Unity Mono"), ("renpy.exe", "Ren'Py"),
        ("nw.dll", "NW.js (RPG Maker)"), ("libogg.dll", "Ogg"), ("discord-rpc.dll", "Discord RPC"),
    ];

    private static readonly string[] StrongExtensions = [".forge", ".cas", ".pck", ".rpa", ".vpk", ".bk2", ".bnk", ".wem", ".big", ".rpf", ".archive", ".ucas", ".utoc"];

    // Signs of a desktop application / launcher rather than a game
    private static readonly (string File, int Penalty)[] AppFiles =
    [
        ("resources\\app.asar", 8), ("resources/app.asar", 8), ("qt5core.dll", 3), ("qt6core.dll", 3), ("libcef.dll", 2),
        ("chrome_elf.dll", 2), ("electron.exe", 8),
    ];

    /// <summary>Scores a folder. Higher means "more game-like".</summary>
    public static FolderEvidence Inspect(string dir, string? nameHint = null, CancellationToken ct = default)
    {
        int score = 0, medium = 0, archives = 0;
        bool electron = false;
        var reasons = new List<string>();
        var exes = new List<(string Path, long Size, int Depth)>();
        if (!Directory.Exists(dir)) return new FolderEvidence(0, null, reasons);

        var seenStrong = new HashSet<string>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((dir, 0));
        int entries = 0;

        while (queue.Count > 0 && entries < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            var (d, depth) = queue.Dequeue();
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(d).EnumerateFileSystemInfos(); }
            catch { continue; }

            try
            {
                foreach (var fsi in items)
                {
                    if (++entries > MaxEntries) break;
                    var lower = fsi.Name.ToLowerInvariant();
                    if (fsi is DirectoryInfo di)
                    {
                        if ((di.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (lower.EndsWith("_data") && (File.Exists(Path.Combine(di.FullName, "globalgamemanagers")) || File.Exists(Path.Combine(di.FullName, "data.unity3d"))))
                            Add(ref score, seenStrong, reasons, "Unity engine", 10);
                        else if (lower == "engine" && Directory.Exists(Path.Combine(di.FullName, "Binaries")))
                            Add(ref score, seenStrong, reasons, "Unreal Engine", 10);
                        else if (lower == "paks" && di.Parent?.Name.Equals("Content", StringComparison.OrdinalIgnoreCase) == true)
                            Add(ref score, seenStrong, reasons, "Unreal Engine", 10);
                        else if (lower is "easyanticheat" or "battleye")
                            Add(ref score, seenStrong, reasons, "Anti-cheat", 10);
                        else if (lower == "renpy")
                            Add(ref score, seenStrong, reasons, "Ren'Py", 10);
                        if (depth < MaxDepth && !SkipDirs.Contains(lower)) queue.Enqueue((di.FullName, depth + 1));
                        continue;
                    }

                    var fi = (FileInfo)fsi;
                    if (lower.EndsWith(".exe"))
                    {
                        exes.Add((fi.FullName, SafeLength(fi), depth));
                        if (lower.Contains("-win64-shipping") || lower.Contains("-wingdk-shipping") || lower.Contains("-win32-shipping"))
                            Add(ref score, seenStrong, reasons, "Unreal Engine", 10);
                        continue;
                    }

                    foreach (var (f, why) in StrongFiles)
                        if (lower == f) { Add(ref score, seenStrong, reasons, why, 10); break; }

                    foreach (var (f, why) in MediumFiles)
                        if (lower == f && medium < 3 && seenStrong.Add("m:" + why)) { score += 4; medium++; reasons.Add(why); break; }

                    var ext = Path.GetExtension(lower);
                    if (archives < 2 && StrongExtensions.Contains(ext)) { score += 5; archives++; reasons.Add("game archive " + ext); }
                    else if (archives < 2 && ext is ".pak" or ".wad" or ".arc" or ".dat" or ".bin" && SafeLength(fi) > 200L * 1024 * 1024)
                    { score += 3; archives++; reasons.Add("large data archive"); }

                    if (depth <= 1)
                        foreach (var (f, pen) in AppFiles)
                        {
                            var rel = Path.GetRelativePath(dir, fi.FullName).ToLowerInvariant();
                            if (rel.EndsWith(f))
                            {
                                score -= pen;
                                reasons.Add("app framework " + f);
                                if (f.Contains("app.asar") || f == "electron.exe") electron = true;
                            }
                        }
                }
            }
            catch { }
        }

        string? best = ExePicker.Pick(exes, dir, nameHint);
        bool engine = seenStrong.Any(x => !x.StartsWith("m:"));
        return new FolderEvidence(score, best, reasons, engine, electron);
    }

    private static void Add(ref int score, HashSet<string> seen, List<string> reasons, string why, int pts)
    {
        if (!seen.Add(why)) return;
        score += pts;
        reasons.Add(why);
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; } catch { return 0; }
    }

    /// <summary>Names that suggest a gaming utility (mod manager, DLSS swapper, trainer…) rather than a game.</summary>
    public static bool LooksLikeToolName(string name) => ToolNameRegex().IsMatch(name);

    [GeneratedRegex(@"\b(mod ?manager|manager|modding|mod tool|tools?|utility|utilities|tweak(er|s)?|editor|patcher|updater|configurator|config tool|overlay|trainer|swapper|injector|unlocker|save editor|benchmark)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToolNameRegex();

    /// <summary>Names that are never games, whatever the folder contains.</summary>
    public static bool IsExcludedName(string name, IEnumerable<string>? extra = null)
    {
        if (ExcludeRegex().IsMatch(name)) return true;
        if (extra is not null)
            foreach (var w in extra)
                if (!string.IsNullOrWhiteSpace(w) && name.Contains(w.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [GeneratedRegex(@"redistributable|redist\b|\bruntime\b|visual c\+\+|\.net (framework|runtime|desktop)|directx|vcredist|\bsdk\b|(graphics|audio|chipset|printer|usb|bluetooth|wireless|network|display|realtek|ethernet|touchpad)\s+driver|nvidia (app|graphics|physx|geforce|control)|amd software|intel\(r\)|microsoft (edge|office|teams|onedrive|visual)|webview|anti-?cheat|battleye|riot vanguard|\bdedicated server\b|steamworks|steam linux runtime|^proton\b|easyanticheat|launcher prerequisites|uninstall|\bupdate for\b|hotfix|security update|language pack|\bsoundtrack\b|\bartbook\b|\bcodec\b|\bpython\b|\bjava\b|\bnode\.js\b|\bgit\b|\bvisual studio\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludeRegex();
}

/// <summary>Chooses the most likely main executable in a game folder.</summary>
public static partial class ExePicker
{
    public static string? Pick(IReadOnlyList<(string Path, long Size, int Depth)> exes, string root, string? nameHint)
    {
        if (exes.Count == 0) return null;
        var folderKey = PathUtil.NormalizeName(Path.GetFileName(root.TrimEnd('\\', '/')));
        var hintKey = nameHint is null ? "" : PathUtil.NormalizeName(nameHint);

        string? best = null;
        int bestScore = int.MinValue;
        foreach (var (path, size, depth) in exes)
        {
            var file = Path.GetFileNameWithoutExtension(path);
            var lower = file.ToLowerInvariant();
            if (BadExeRegex().IsMatch(lower)) continue;

            int s = 0;
            s += depth switch { 0 => 6, 1 => 3, 2 => 2, _ => 0 };
            var key = PathUtil.NormalizeName(file);
            if (key.Length >= 3)
            {
                if (hintKey.Length > 0 && (hintKey.Contains(key) || key.Contains(hintKey))) s += 8;
                if (folderKey.Length > 0 && (folderKey.Contains(key) || key.Contains(folderKey))) s += 6;
            }
            if (size > 50L * 1024 * 1024) s += 4;
            else if (size > 5L * 1024 * 1024) s += 2;
            else if (size < 200 * 1024) s -= 2;
            if (lower.Contains("-shipping")) s += 3;
            if (lower.EndsWith("_be") || lower.EndsWith("_eac")) s -= 1;
            if (lower.Contains("launcher")) s -= 3;
            var pl = path.ToLowerInvariant();
            if (pl.Contains("win64") || pl.Contains("x64")) s += 1;
            if (pl.Contains("\\tools\\") || pl.Contains("/tools/") || pl.Contains("\\editor")) s -= 4;

            if (s > bestScore) { bestScore = s; best = path; }
        }
        return best;
    }

    [GeneratedRegex(@"^(unins.*|.*uninstall.*|setup.*|install.*|.*redist.*|vc_?redist.*|dxsetup|dxwebsetup|.*crash.*|.*report.*|.*updater.*|.*patcher.*|.*helper.*|.*service.*|easyanticheat.*|battleye.*|beservice.*|.*cef.*|.*subprocess.*|dotnet.*|.*config.*|.*settings.*|.*editor.*|.*benchmark.*|.*server.*|unitycrashhandler.*|cleanup|touchup|activation.*|.*overlay.*|.*bootstrap.*installer.*|dowser|ndp.*|oalinst|physx.*|vcpp.*|quicksfv|.*register.*|.*repair.*|.*diagnos.*|.*debug.*|.*console.*|.*_cmd|.*-cmd|.*cli|.*verify.*)$")]
    private static partial Regex BadExeRegex();
}
