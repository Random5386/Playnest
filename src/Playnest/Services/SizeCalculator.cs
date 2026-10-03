using System.IO.Enumeration;
using Playnest.Models;

namespace Playnest.Services;

/// <summary>
/// Measures install folder sizes on one low-priority background thread, one game at a time.
/// Steam and Epic sizes come from their own records during the scan, so most games never need measuring.
/// </summary>
public sealed class SizeCalculator : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromDays(7);

    /// <summary>Starts measuring every item that needs it. <paramref name="onMeasured"/> runs on the background thread.</summary>
    public void Start(IReadOnlyList<GameEntry> items, Action<GameEntry> onMeasured)
    {
        if (_thread is { IsAlive: true }) return;
        var todo = items.Where(NeedsMeasuring).ToList();
        if (todo.Count == 0) return;
        _thread = new Thread(() =>
        {
            foreach (var e in todo)
            {
                if (_cts.IsCancellationRequested) return;
                var dir = e.EffectiveDir;
                if (dir is null) continue;
                try
                {
                    long size = Measure(dir, _cts.Token);
                    if (size > 0)
                    {
                        e.SizeBytes = size;
                        e.SizeCheckedUtc = DateTime.UtcNow;
                        onMeasured(e);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Error("size " + dir, ex); }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Playnest size calculator",
        };
        _thread.Start();
    }

    public static bool NeedsMeasuring(GameEntry e) =>
        e.EffectiveKind == ItemKind.Game && e.EffectiveDir is { } d && Directory.Exists(d) &&
        (e.SizeBytes <= 0 || e.SizeCheckedUtc is null || DateTime.UtcNow - e.SizeCheckedUtc > RecheckAfter);

    /// <summary>Total size of all files under <paramref name="dir"/> (links/junctions are not followed).</summary>
    public static long Measure(string dir, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
        };
        var files = new FileSystemEnumerable<long>(dir, (ref FileSystemEntry entry) => entry.Length, options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
        };
        long total = 0;
        int n = 0;
        foreach (var len in files)
        {
            total += len;
            if ((++n & 1023) == 0) ct.ThrowIfCancellationRequested();
        }
        return total;
    }

    public static string Format(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024L * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
        _ => $"{bytes / 1024.0 / 1024 / 1024 / 1024:0.##} TB",
    };

    public void Dispose() => _cts.Cancel();
}
