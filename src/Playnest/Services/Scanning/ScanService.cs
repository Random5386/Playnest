using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

public sealed class ScanResult
{
    public List<GameEntry> Items { get; } = new();
    /// <summary>Sources whose scan completed. Items from other sources are left untouched when merging.</summary>
    public HashSet<GameSource> CompletedSources { get; } = new();
    public List<string> Errors { get; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>Runs every scanner in priority order. Store scanners run first so their folders are not re-detected by the generic scanners.</summary>
public static class ScanService
{
    public static IGameScanner[] StoreScanners { get; } =
    [
        new SteamScanner(), new EpicScanner(), new GogScanner(), new UbisoftScanner(), new XboxScanner(),
        new RiotScanner(), new EaScanner(), new BattleNetScanner(), new AmazonScanner(), new RockstarScanner(),
    ];

    public static ScanResult Run(AppSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var result = new ScanResult();
        progress?.Report("Reading installed programs…");
        var uninstall = UninstallIndex.Read();
        var ctx = new ScanContext(settings, uninstall, ct);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RunScanner(IGameScanner s)
        {
            if (!s.IsEnabled(settings)) return;
            progress?.Report($"Scanning {GameSourceInfo.DisplayName(s.Source)}…");
            try
            {
                foreach (var e in s.Scan(ctx))
                    if (ids.Add(e.Id)) result.Items.Add(e);
                result.CompletedSources.Add(s.Source);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("scanner " + s.Source, ex);
                result.Errors.Add($"{GameSourceInfo.DisplayName(s.Source)}: {ex.Message}");
            }
        }

        foreach (var s in StoreScanners) RunScanner(s);

        // Launchers and tools — claimed before the generic scanners so they are never mistaken for games.
        progress?.Report("Detecting launchers and tools…");
        try
        {
            var lt = LauncherCatalog.Detect(uninstall, settings.DetectLaunchers, settings.DetectTools);
            if (settings.DetectLaunchers && lt.All(x => x.SourceId != "riot") && RiotScanner.ClientPath is { } rc)
            {
                var def = LauncherCatalog.All.First(d => d.Id == "riot");
                lt.Add(new GameEntry
                {
                    Id = "launcher:riot", Name = def.Name, Kind = ItemKind.Launcher, Source = GameSource.System,
                    SourceId = "riot", ExePath = rc, InstallDir = Path.GetDirectoryName(rc),
                });
            }
            foreach (var e in lt)
            {
                // Launcher folders contain their games for some stores (e.g. Steam\steamapps), so only the exe folder is claimed.
                if (e.InstallDir is not null && e.SourceId != "steam") ctx.Claim(e.InstallDir);
                if (ids.Add(e.Id)) result.Items.Add(e);
            }
            result.CompletedSources.Add(GameSource.System);
        }
        catch (Exception ex)
        {
            Log.Error("launchers", ex);
            result.Errors.Add("Launchers: " + ex.Message);
        }

        RunScanner(new InstalledProgramsScanner());
        RunScanner(new FolderScanner());

        // Generic scanners can't fail as a whole on a missing feature; mark them completed only when enabled.
        result.Duration = DateTime.UtcNow - started;
        Log.Info($"Scan finished: {result.Items.Count} items in {result.Duration.TotalSeconds:0.0}s");
        return result;
    }
}
