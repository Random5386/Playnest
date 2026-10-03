using Microsoft.Win32;

namespace Playnest.Services.Platform;

/// <summary>Small, exception-safe registry helpers. Every method returns null/empty on non-Windows or on error.</summary>
public static class Reg
{
    /// <summary>Reads a value, trying the 32-bit view first (where most launchers write) and then the 64-bit view.</summary>
    public static string? Get(RegistryHive hive, string subKey, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var view in Views(hive))
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(subKey);
                if (k?.GetValue(value) is { } v) return v.ToString();
            }
            catch { }
        }
        return null;
    }

    /// <summary>Lists sub keys of a key (both registry views, de-duplicated).</summary>
    public static IEnumerable<(string Name, Dictionary<string, string> Values)> SubKeys(RegistryHive hive, string subKey)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var view in Views(hive))
        {
            List<(string, Dictionary<string, string>)> items = new();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(subKey);
                if (k is null) continue;
                foreach (var name in k.GetSubKeyNames())
                {
                    if (!seen.Add(name)) continue;
                    try
                    {
                        using var sk = k.OpenSubKey(name);
                        if (sk is null) continue;
                        items.Add((name, ReadValues(sk)));
                    }
                    catch { }
                }
            }
            catch { }
            foreach (var i in items) yield return i;
        }
    }

    public static Dictionary<string, string> ReadValues(RegistryKey k)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in k.GetValueNames())
        {
            try
            {
                var v = k.GetValue(n);
                if (v is not null && v is not byte[]) d[n] = v.ToString() ?? "";
            }
            catch { }
        }
        return d;
    }

    private static RegistryView[] Views(RegistryHive hive) =>
        hive == RegistryHive.LocalMachine && Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry32, RegistryView.Registry64]
            : [RegistryView.Default];

    public static string? GetRunAtStartup(string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return k?.GetValue(name) as string;
        }
        catch { return null; }
    }

    public static void SetRunAtStartup(string name, string? command)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (command is null) k.DeleteValue(name, false);
            else k.SetValue(name, command);
        }
        catch (Exception ex) { Log.Error("startup registry", ex); }
    }
}

/// <summary>One entry of Windows' "Apps &amp; features" list.</summary>
public sealed record UninstallEntry(
    string KeyName,
    string DisplayName,
    string? InstallLocation,
    string? UninstallString,
    string? Publisher,
    string? DisplayIcon,
    bool SystemComponent)
{
    /// <summary>Install folder, falling back to the folder of the display icon.</summary>
    public string? Folder
    {
        get
        {
            var loc = PathUtil.Clean(InstallLocation);
            if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc)) return loc;
            var icon = PathUtil.ExeFromIconSpec(DisplayIcon);
            if (icon is not null && !PathUtil.LooksLikeUninstaller(icon))
            {
                var dir = Path.GetDirectoryName(icon);
                if (dir is not null && Directory.Exists(dir)) return dir;
            }
            return null;
        }
    }
}

public static class UninstallIndex
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<UninstallEntry> Read()
    {
        var list = new List<UninstallEntry>();
        if (!OperatingSystem.IsWindows()) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var (name, v) in Reg.SubKeys(hive, Key))
            {
                if (!v.TryGetValue("DisplayName", out var dn) || string.IsNullOrWhiteSpace(dn)) continue;
                if (!seen.Add(hive + name)) continue;
                v.TryGetValue("InstallLocation", out var loc);
                v.TryGetValue("UninstallString", out var un);
                v.TryGetValue("Publisher", out var pub);
                v.TryGetValue("DisplayIcon", out var icon);
                bool sys = v.TryGetValue("SystemComponent", out var sc) && sc == "1";
                if (v.ContainsKey("ParentKeyName")) sys = true; // updates/patches
                list.Add(new UninstallEntry(name, dn.Trim(), loc, un, pub, icon, sys));
            }
        }
        return list;
    }
}
