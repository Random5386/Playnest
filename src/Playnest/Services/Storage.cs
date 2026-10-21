using System.Text.Json;
using System.Text.Json.Serialization;
using Playnest.Models;

namespace Playnest.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(LibraryData))]
internal partial class JsonCtx : JsonSerializerContext;

/// <summary>Where Playnest keeps its files. A "portable.txt" next to the exe keeps everything beside it.</summary>
public static class AppPaths
{
    public static string DataDir { get; private set; } = DefaultDataDir();
    public static bool IsPortable { get; private set; }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LibraryFile => Path.Combine(DataDir, "library.json");
    public static string ArtDir => Path.Combine(DataDir, "cache", "art");
    public static string CustomArtDir => Path.Combine(DataDir, "custom-art");
    public static string LogFile => Path.Combine(DataDir, "playnest.log");

    private static string DefaultDataDir()
    {
        var baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "portable.txt")))
        {
            IsPortable = true;
            return Path.Combine(baseDir, "Data");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Playnest");
    }

    /// <summary>Used by tests and the --data argument.</summary>
    public static void Override(string dir) => DataDir = dir;

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ArtDir);
        Directory.CreateDirectory(CustomArtDir);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    public static void Info(string message) => Write("INFO " + message);

    private static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var file = AppPaths.LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}

public static class Storage
{
    private static readonly object WriteGate = new();

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                using var fs = File.OpenRead(AppPaths.SettingsFile);
                return JsonSerializer.Deserialize(fs, JsonCtx.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("load settings", ex);
            TryBackupCorrupt(AppPaths.SettingsFile);
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s) =>
        AtomicWrite(AppPaths.SettingsFile, () => JsonSerializer.SerializeToUtf8Bytes(s, JsonCtx.Default.AppSettings));

    public static LibraryData LoadLibrary()
    {
        try
        {
            if (File.Exists(AppPaths.LibraryFile))
            {
                using var fs = File.OpenRead(AppPaths.LibraryFile);
                return JsonSerializer.Deserialize(fs, JsonCtx.Default.LibraryData) ?? new LibraryData();
            }
        }
        catch (Exception ex)
        {
            Log.Error("load library", ex);
            TryBackupCorrupt(AppPaths.LibraryFile);
        }
        return new LibraryData();
    }

    public static void SaveLibrary(LibraryData d) =>
        AtomicWrite(AppPaths.LibraryFile, () => JsonSerializer.SerializeToUtf8Bytes(d, JsonCtx.Default.LibraryData));

    /// <summary>Settings as JSON for sharing/backups. Secrets (the SteamGridDB key) are left out.</summary>
    public static string ExportSettings(AppSettings s)
    {
        var json = JsonSerializer.Serialize(s, JsonCtx.Default.AppSettings);
        var copy = JsonSerializer.Deserialize(json, JsonCtx.Default.AppSettings)!;
        copy.SteamGridDbApiKey = "";
        return JsonSerializer.Serialize(copy, JsonCtx.Default.AppSettings);
    }

    public static AppSettings? ImportSettings(string json)
    {
        try { return JsonSerializer.Deserialize(json, JsonCtx.Default.AppSettings); }
        catch { return null; }
    }

    /// <summary>
    /// Serializes and writes via a temp file. Saves run on timer threads, so serializing happens inside the try:
    /// an exception there (e.g. a list changed mid-save) must be logged, not crash the app.
    /// </summary>
    private static void AtomicWrite(string path, Func<byte[]> serialize)
    {
        try
        {
            var data = serialize();
            lock (WriteGate) // a timer save and the save on exit can overlap; they share the .tmp file
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("save " + Path.GetFileName(path), ex);
        }
    }

    private static void TryBackupCorrupt(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { }
    }
}
