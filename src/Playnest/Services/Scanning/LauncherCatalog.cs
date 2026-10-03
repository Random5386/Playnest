using System.Text.RegularExpressions;
using Microsoft.Win32;
using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

/// <summary>A known store launcher or gaming tool and the ways to find it.</summary>
public sealed record LauncherDef(
    string Id,
    string Name,
    ItemKind Kind,
    string[] Paths,
    string? UninstallNamePattern = null,
    string? ExeName = null,
    (RegistryHive Hive, string Key, string Value, string? RelativeExe)[]? Registry = null,
    string? LaunchUri = null,
    string? PackageFolder = null);

/// <summary>Finds installed store launchers (Steam, Epic, ...) and gaming tools (mod managers, emulators, overlays).</summary>
public static class LauncherCatalog
{
    private const RegistryHive HKLM = RegistryHive.LocalMachine;
    private const RegistryHive HKCU = RegistryHive.CurrentUser;

    public static readonly LauncherDef[] All =
    [
        // ---------------- Store launchers ----------------
        new("steam", "Steam", ItemKind.Launcher, [@"%ProgramFiles(x86)%\Steam\steam.exe"],
            Registry: [(HKCU, @"Software\Valve\Steam", "SteamExe", null), (HKLM, @"SOFTWARE\Valve\Steam", "InstallPath", "steam.exe")]),
        new("epic", "Epic Games Launcher", ItemKind.Launcher,
            [@"%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", @"%ProgramFiles%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe"],
            "^Epic Games Launcher$", @"Portal\Binaries\Win64\EpicGamesLauncher.exe"),
        new("gog", "GOG Galaxy", ItemKind.Launcher, [@"%ProgramFiles(x86)%\GOG Galaxy\GalaxyClient.exe"], "^GOG GALAXY",
            "GalaxyClient.exe", [(HKLM, @"SOFTWARE\GOG.com\GalaxyClient\paths", "client", "GalaxyClient.exe")]),
        new("ea", "EA app", ItemKind.Launcher, [@"%ProgramFiles%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe"], "^EA app$",
            @"EA Desktop\EADesktop.exe", [(HKLM, @"SOFTWARE\Electronic Arts\EA Desktop", "DesktopAppPath", null)]),
        new("origin", "Origin", ItemKind.Launcher, [@"%ProgramFiles(x86)%\Origin\Origin.exe"], "^Origin$", "Origin.exe"),
        new("ubisoft", "Ubisoft Connect", ItemKind.Launcher, [@"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe"],
            "^(Ubisoft Connect|Uplay)$", "UbisoftConnect.exe", [(HKLM, @"SOFTWARE\Ubisoft\Launcher", "InstallDir", "UbisoftConnect.exe")]),
        new("battlenet", "Battle.net", ItemKind.Launcher, [@"%ProgramFiles(x86)%\Battle.net\Battle.net.exe", @"%ProgramFiles%\Battle.net\Battle.net.exe"], "^Battle\\.net$", "Battle.net.exe"),
        new("xbox", "Xbox app", ItemKind.Launcher, [], LaunchUri: @"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App",
            PackageFolder: @"%LocalAppData%\Packages\Microsoft.GamingApp_8wekyb3d8bbwe"),
        new("rockstar", "Rockstar Games Launcher", ItemKind.Launcher, [@"%ProgramFiles%\Rockstar Games\Launcher\Launcher.exe"],
            "^Rockstar Games Launcher$", "Launcher.exe", [(HKLM, @"SOFTWARE\Rockstar Games\Launcher", "InstallFolder", "Launcher.exe")]),
        new("riot", "Riot Client", ItemKind.Launcher, [@"C:\Riot Games\Riot Client\RiotClientServices.exe"]),
        new("amazon", "Amazon Games", ItemKind.Launcher, [@"%LocalAppData%\Amazon Games\App\Amazon Games.exe"], "^Amazon Games$", "Amazon Games.exe"),
        new("itch", "itch", ItemKind.Launcher, [@"%LocalAppData%\itch\itch.exe"], "^itch$", "itch.exe"),
        new("heroic", "Heroic Games Launcher", ItemKind.Launcher, [@"%LocalAppData%\Programs\heroic\Heroic.exe"], "^Heroic", "Heroic.exe"),
        new("playnite", "Playnite", ItemKind.Launcher, [@"%LocalAppData%\Playnite\Playnite.DesktopApp.exe"], "^Playnite", "Playnite.DesktopApp.exe"),
        new("humble", "Humble App", ItemKind.Launcher, [@"%ProgramFiles%\Humble App\Humble App.exe"], "^Humble App", "Humble App.exe"),
        new("legacygames", "Legacy Games Launcher", ItemKind.Launcher, [@"%ProgramFiles%\Legacy Games\Legacy Games Launcher\Legacy Games Launcher.exe"], "^Legacy Games Launcher", "Legacy Games Launcher.exe"),
        new("paradox", "Paradox Launcher", ItemKind.Launcher, [@"%LocalAppData%\Programs\Paradox Interactive\launcher\bootstrapper-v2.exe"], "^Paradox Launcher", "bootstrapper-v2.exe"),
        new("wargaming", "Wargaming.net Game Center", ItemKind.Launcher, [@"%ProgramData%\Wargaming.net\GameCenter\wgc.exe"], "^Wargaming\\.net Game Center", "wgc.exe"),
        new("hoyoplay", "HoYoPlay", ItemKind.Launcher, [@"%ProgramFiles%\HoYoPlay\launcher.exe"], "^HoYoPlay", "launcher.exe"),
        new("plarium", "Plarium Play", ItemKind.Launcher, [@"%LocalAppData%\PlariumPlay\PlariumPlay.exe"], "^Plarium Play", "PlariumPlay.exe"),
        new("arc", "Arc", ItemKind.Launcher, [@"%ProgramFiles(x86)%\Arc\Arc.exe"], "^Arc$", "Arc.exe"),
        new("minecraft", "Minecraft Launcher", ItemKind.Launcher, [@"%ProgramFiles(x86)%\Minecraft Launcher\MinecraftLauncher.exe"], "^Minecraft Launcher", "MinecraftLauncher.exe"),
        new("curseforge", "CurseForge", ItemKind.Launcher, [@"%LocalAppData%\Programs\CurseForge Windows\CurseForge.exe"], "^CurseForge", "CurseForge.exe"),
        new("gamejolt", "Game Jolt Client", ItemKind.Launcher, [@"%LocalAppData%\GameJoltClient\GameJoltClient.exe"], "^Game Jolt", "GameJoltClient.exe"),
        new("bigfish", "Big Fish Games", ItemKind.Launcher, [@"%ProgramFiles(x86)%\bfgclient\bfgclient.exe"], "^Big Fish Games", "bfgclient.exe"),

        // ---------------- Gaming tools: mod managers ----------------
        new("vortex", "Vortex", ItemKind.Tool, [@"%ProgramFiles%\Black Tree Gaming Ltd\Vortex\Vortex.exe"], "^Vortex$", "Vortex.exe"),
        new("mo2", "Mod Organizer 2", ItemKind.Tool, [@"%ProgramFiles%\Mod Organizer\ModOrganizer.exe"], "^Mod Organizer", "ModOrganizer.exe"),
        new("nmm", "Nexus Mod Manager", ItemKind.Tool, [@"%ProgramFiles%\Nexus Mod Manager\NexusClient.exe"], "^Nexus Mod Manager", "NexusClient.exe"),
        new("me3tweaks", "ME3Tweaks Mod Manager", ItemKind.Tool, [], "^ME3Tweaks", "ME3TweaksModManager.exe"),
        new("r2modman", "r2modman", ItemKind.Tool, [@"%LocalAppData%\Programs\r2modman\r2modman.exe"], "^r2modman", "r2modman.exe"),
        new("thunderstore", "Thunderstore Mod Manager", ItemKind.Tool, [], "^Thunderstore Mod Manager", "Thunderstore Mod Manager.exe"),
        new("wemod", "Wand (WeMod)", ItemKind.Tool, [@"%LocalAppData%\WeMod\WeMod.exe", @"%LocalAppData%\Wand\Wand.exe"], "^(WeMod|Wand)$"),
        new("openiv", "OpenIV", ItemKind.Tool, [@"%LocalAppData%\New Technology Studio\Apps\OpenIV\OpenIV.exe"], "^OpenIV", "OpenIV.exe"),

        // ---------------- Gaming tools: emulators ----------------
        new("retroarch", "RetroArch", ItemKind.Tool, [@"C:\RetroArch-Win64\retroarch.exe"], "^RetroArch", "retroarch.exe"),
        new("dolphin", "Dolphin", ItemKind.Tool, [@"%ProgramFiles%\Dolphin\Dolphin.exe"], "^Dolphin", "Dolphin.exe"),
        new("pcsx2", "PCSX2", ItemKind.Tool, [@"%ProgramFiles%\PCSX2\pcsx2-qt.exe"], "^PCSX2", "pcsx2-qt.exe"),
        new("rpcs3", "RPCS3", ItemKind.Tool, [], "^RPCS3", "rpcs3.exe"),
        new("cemu", "Cemu", ItemKind.Tool, [], "^Cemu", "Cemu.exe"),
        new("duckstation", "DuckStation", ItemKind.Tool, [@"%LocalAppData%\Programs\DuckStation\duckstation-qt-x64-ReleaseLTCG.exe"], "^DuckStation", "duckstation-qt-x64-ReleaseLTCG.exe"),
        new("ppsspp", "PPSSPP", ItemKind.Tool, [@"%ProgramFiles%\PPSSPP\PPSSPPWindows64.exe"], "^PPSSPP", "PPSSPPWindows64.exe"),
        new("ryujinx", "Ryujinx", ItemKind.Tool, [], "^Ryujinx", "Ryujinx.exe"),
        new("xemu", "xemu", ItemKind.Tool, [], "^xemu", "xemu.exe"),
        new("xenia", "Xenia", ItemKind.Tool, [], "^Xenia", "xenia_canary.exe"),
        new("shadps4", "shadPS4", ItemKind.Tool, [], "^shadPS4", "shadPS4.exe"),

        // ---------------- Gaming tools: performance & overlays ----------------
        new("afterburner", "MSI Afterburner", ItemKind.Tool, [@"%ProgramFiles(x86)%\MSI Afterburner\MSIAfterburner.exe"], "^MSI Afterburner", "MSIAfterburner.exe"),
        new("rtss", "RivaTuner Statistics Server", ItemKind.Tool, [@"%ProgramFiles(x86)%\RivaTuner Statistics Server\RTSS.exe"], "^RivaTuner Statistics Server", "RTSS.exe"),
        new("nvidiaapp", "NVIDIA App", ItemKind.Tool, [@"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe"]),
        new("frameview", "FrameView", ItemKind.Tool, [@"%ProgramFiles%\NVIDIA Corporation\FrameView\FrameView_x64.exe"], "^FrameView", "FrameView_x64.exe"),
        new("capframex", "CapFrameX", ItemKind.Tool, [@"%ProgramFiles%\CapFrameX\CapFrameX.exe"], "^CapFrameX", "CapFrameX.exe"),
        new("specialk", "Special K", ItemKind.Tool, [@"%UserProfile%\Documents\My Mods\SpecialK\SKIF.exe", @"%ProgramFiles%\Special K\SKIF.exe"], "^Special K", "SKIF.exe"),
        new("ds4windows", "DS4Windows", ItemKind.Tool, [@"%ProgramFiles%\DS4Windows\DS4Windows.exe"], "^DS4Windows", "DS4Windows.exe"),
    ];

    /// <summary>Detects every launcher/tool that is installed.</summary>
    public static List<GameEntry> Detect(IReadOnlyList<UninstallEntry> uninstall, bool launchers, bool tools)
    {
        var list = new List<GameEntry>();
        foreach (var def in All)
        {
            if (def.Kind == ItemKind.Launcher && !launchers) continue;
            if (def.Kind == ItemKind.Tool && !tools) continue;
            var found = Resolve(def, uninstall);
            if (found is null) continue;
            list.Add(found);
        }
        return list;
    }

    public static GameEntry? Resolve(LauncherDef def, IReadOnlyList<UninstallEntry> uninstall)
    {
        string? exe = null;
        UninstallEntry? un = null;

        if (def.Registry is not null)
            foreach (var (hive, key, value, rel) in def.Registry)
            {
                var v = PathUtil.Clean(Reg.Get(hive, key, value));
                if (v is null) continue;
                var candidate = rel is null ? v : Path.Combine(v, rel);
                if (File.Exists(candidate)) { exe = candidate; break; }
            }

        if (exe is null)
            foreach (var p in def.Paths)
            {
                var c = Environment.ExpandEnvironmentVariables(p);
                if (File.Exists(c)) { exe = c; break; }
            }

        if (def.UninstallNamePattern is not null)
        {
            var rx = new Regex(def.UninstallNamePattern, RegexOptions.IgnoreCase);
            un = uninstall.FirstOrDefault(u => rx.IsMatch(u.DisplayName));
            if (exe is null && un is not null)
            {
                var folder = un.Folder;
                if (folder is not null && def.ExeName is not null)
                {
                    var c = Path.Combine(folder, def.ExeName);
                    if (File.Exists(c)) exe = c;
                }
                exe ??= PathUtil.ExeFromIconSpec(un.DisplayIcon) is { } icon && !PathUtil.LooksLikeUninstaller(icon) ? icon : null;
            }
        }

        string? uri = null;
        if (exe is null && def.LaunchUri is not null && def.PackageFolder is not null &&
            Directory.Exists(Environment.ExpandEnvironmentVariables(def.PackageFolder)))
            uri = def.LaunchUri;

        if (exe is null && uri is null) return null;

        return new GameEntry
        {
            Id = (def.Kind == ItemKind.Launcher ? "launcher:" : "tool:") + def.Id,
            Name = def.Name,
            Kind = def.Kind,
            Source = GameSource.System,
            SourceId = def.Id,
            ExePath = exe,
            InstallDir = exe is null ? null : Path.GetDirectoryName(exe),
            LaunchUri = uri,
            UninstallCommand = un?.UninstallString,
            Publisher = un?.Publisher,
        };
    }

    /// <summary>True when an uninstall entry belongs to a known launcher/tool (so the generic scan does not add it as a game).</summary>
    public static bool MatchesAny(UninstallEntry u)
    {
        foreach (var def in All)
            if (def.UninstallNamePattern is not null && Regex.IsMatch(u.DisplayName, def.UninstallNamePattern, RegexOptions.IgnoreCase))
                return true;
        return false;
    }
}
