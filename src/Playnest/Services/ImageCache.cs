using Avalonia.Media.Imaging;

namespace Playnest.Services;

/// <summary>
/// Decodes artwork at the size it is shown (never at full resolution) and manages bitmap lifetimes explicitly.
/// Bitmaps hold native (Skia) memory that the .NET garbage collector cannot see, so instead of waiting for
/// finalizers every bitmap is reference-counted: tiles <see cref="Acquire"/> and <see cref="Release"/> them, a few
/// unused ones are kept for instant reuse, and the rest are disposed right away. This keeps memory flat no matter
/// how much of a large library is scrolled through.
/// </summary>
public static class ImageCache
{
    private sealed class Entry
    {
        public required string Key;
        public required string Path;
        public required Bitmap Bitmap;
        public int Refs;
        public LinkedListNode<Entry>? Unused; // in the reuse list while Refs == 0
        public bool Stale;                   // file changed; dispose as soon as nobody uses it
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> ByKey = new();
    private static readonly Dictionary<Bitmap, Entry> ByBitmap = new(ReferenceEqualityComparer.Instance);
    private static readonly LinkedList<Entry> UnusedList = new();
    private static int _keepUnused = 48;
    private static readonly SemaphoreSlim DecodeGate = new(2);

    /// <summary>How many currently unused bitmaps are kept for instant reuse (e.g. when scrolling back).</summary>
    public static void SetCapacity(int capacity)
    {
        lock (Gate)
        {
            _keepUnused = Math.Max(0, capacity);
            TrimUnused();
        }
    }

    /// <summary>Returns a bitmap for <paramref name="path"/> decoded to about <paramref name="decodeWidth"/> pixels. The caller owns one reference and must call <see cref="Release"/>.</summary>
    public static async Task<Bitmap?> Acquire(string path, int decodeWidth)
    {
        // Round sizes so small slider moves reuse the same decoded bitmap.
        decodeWidth = Math.Clamp((decodeWidth + 31) / 32 * 32, 32, 1600);
        // Never decode larger than the source: upscaling in memory only costs RAM (the UI scales smoothly anyway).
        if (Artwork.ImageInfo.SizeOfFile(path) is { Width: > 0 } src && src.Width < decodeWidth) decodeWidth = src.Width;
        var key = path + "|" + decodeWidth;

        lock (Gate)
        {
            if (ByKey.TryGetValue(key, out var hit) && !hit.Stale)
            {
                AddRef(hit);
                return hit.Bitmap;
            }
        }

        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (Gate)
            {
                // Another caller may have decoded it while we waited.
                if (ByKey.TryGetValue(key, out var hit) && !hit.Stale)
                {
                    AddRef(hit);
                    return hit.Bitmap;
                }
            }

            var bmp = await Task.Run(() =>
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                    return Bitmap.DecodeToWidth(fs, decodeWidth, BitmapInterpolationMode.HighQuality);
                }
                catch (Exception ex)
                {
                    Log.Error("decode " + path, ex);
                    return null;
                }
            }).ConfigureAwait(false);
            if (bmp is null) return null;

            lock (Gate)
            {
                if (ByKey.TryGetValue(key, out var old))
                {
                    // Replace a stale entry: it is disposed once its last user lets go.
                    ByKey.Remove(key);
                    old.Stale = true;
                    if (old.Refs == 0) Destroy(old);
                }
                var entry = new Entry { Key = key, Path = path, Bitmap = bmp, Refs = 1 };
                ByKey[key] = entry;
                ByBitmap[bmp] = entry;
            }
            return bmp;
        }
        finally { DecodeGate.Release(); }
    }

    /// <summary>Gives back a reference obtained from <see cref="Acquire"/>.</summary>
    public static void Release(Bitmap? bmp)
    {
        if (bmp is null) return;
        lock (Gate)
        {
            if (!ByBitmap.TryGetValue(bmp, out var e)) return;
            if (--e.Refs > 0) return;
            e.Refs = 0;
            if (e.Stale || _keepUnused == 0) { Destroy(e); return; }
            e.Unused = UnusedList.AddLast(e);
            TrimUnused();
        }
    }

    /// <summary>Disposes every bitmap nobody is using (window hidden to the tray, game started…).</summary>
    public static void ReleaseAll()
    {
        lock (Gate)
        {
            while (UnusedList.First is { } n) Destroy(n.Value);
        }
    }

    /// <summary>Forgets every decoded version of a file (after its artwork changed).</summary>
    public static void Invalidate(string path)
    {
        lock (Gate)
        {
            foreach (var e in ByKey.Values.Where(e => e.Path == path).ToList())
            {
                e.Stale = true;
                ByKey.Remove(e.Key);
                if (e.Refs == 0) Destroy(e);
            }
        }
    }

    /// <summary>Number of bitmaps alive (for tests and diagnostics).</summary>
    public static int LiveCount
    {
        get { lock (Gate) return ByBitmap.Count; }
    }

    private static void AddRef(Entry e)
    {
        e.Refs++;
        if (e.Unused is not null)
        {
            UnusedList.Remove(e.Unused);
            e.Unused = null;
        }
    }

    private static void TrimUnused()
    {
        while (UnusedList.Count > _keepUnused && UnusedList.First is { } n) Destroy(n.Value);
    }

    private static void Destroy(Entry e)
    {
        if (e.Unused is not null)
        {
            UnusedList.Remove(e.Unused);
            e.Unused = null;
        }
        if (ByKey.TryGetValue(e.Key, out var cur) && ReferenceEquals(cur, e)) ByKey.Remove(e.Key);
        ByBitmap.Remove(e.Bitmap);
        // Avalonia keeps its own reference while a frame that uses the bitmap is being drawn, so this is safe.
        e.Bitmap.Dispose();
    }
}
