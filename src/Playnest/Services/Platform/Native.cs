using System.Diagnostics;

namespace Playnest.Services.Platform;

/// <summary>
/// OS helpers. These deliberately use only standard .NET APIs (no direct Win32 imports): it keeps the code portable and
/// avoids the pattern of raw process/memory/shell calls that antivirus heuristics tend to flag in unsigned programs.
/// </summary>
public static class Native
{
    /// <summary>Releases unused memory (collects and compacts the heap so freed memory goes back to Windows).</summary>
    public static void TrimWorkingSet()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>Full exe path of another process, or null when Windows doesn't allow reading it (e.g. it runs as admin).</summary>
    public static string? ProcessPath(Process p)
    {
        try { return p.MainModule?.FileName; }
        catch { return null; }
    }

    /// <summary>Sends a folder to the Recycle Bin (undoable). Returns true on success, false on failure or when the user cancels.</summary>
    public static bool RecycleFolder(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // Windows' own dialogs must stay on: a game folder is often too big for the Recycle Bin, and with the
            // dialogs hidden Windows deletes such a folder permanently without asking. With them shown, it warns first.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            return !Directory.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Error("recycle " + path, ex);
            return false;
        }
    }
}

/// <summary>Makes sure only one Playnest runs. A second start just brings the first window forward.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Playnest.SingleInstance";
    private const string EventName = @"Local\Playnest.Activate";
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;

    public bool IsFirst { get; }

    public SingleInstance()
    {
        if (!OperatingSystem.IsWindows()) { IsFirst = true; return; }
        _mutex = new Mutex(true, MutexName, out bool created);
        IsFirst = created;
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
    }

    public void SignalFirst() => _event?.Set();

    public void ListenForActivation(Action onActivate)
    {
        if (_event is null) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        if (IsFirst) try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _event?.Dispose();
    }
}
