namespace Meitou.Steam;

/// <summary>
/// Has the running Steam client download or update the account's subscribed Kenshi Workshop items and waits until they are installed. The client
/// does the downloading, into <see cref="MacSteam.WorkshopContentDirectory"/>; subscribing and unsubscribing stay in Steam (the client or the web).
/// A sync does nothing when Steam's library is missing and does not start Steam.
/// </summary>
public static class WorkshopSync
{
    /// <summary>
    /// The reason when Steam is not running.
    /// </summary>
    const string NotRunning = "Steam is not running. Start Steam and sign in first, then synchronise again.";

    /// <summary>
    /// How often the item states are read.
    /// </summary>
    const int PollMilliseconds = 500;

    /// <summary>
    /// Syncs, calling <paramref name="report"/> with the installed and the total item count whenever the installed count changes, until every item
    /// is installed or <paramref name="stop"/> is cancelled (the client then goes on downloading by itself). Blocks: run it off the UI thread.
    /// </summary>
    public static WorkshopSyncResult Run(Action<int, int> report, CancellationToken stop)
    {
        if (!MacSteam.CanSync) return Failed("Steam's library was not found, so the Workshop cannot be synchronised.");
        if (!MacSteam.IsRunning) return Failed(NotRunning);

        using var ugc = SteamUgc.TryOpen(out string? problem);
        if (ugc is null) return Failed(problem!);

        ulong[] subscribed = ugc.SubscribedItems();
        var refused = RequestDownloads(ugc, subscribed);
        ulong[] wanted = [.. subscribed.Except(refused)];
        var (installed, stopped) = Wait(ugc, wanted, report, stop);
        return new WorkshopSyncResult(null, subscribed.Length, installed, refused.Count, stopped);
    }

    /// <summary>
    /// A sync that could not run.
    /// </summary>
    static WorkshopSyncResult Failed(string problem) => new(problem, 0, 0, 0, false);

    /// <summary>
    /// Asks for every item that is not installed and current; the ids the client refuses.
    /// </summary>
    static List<ulong> RequestDownloads(SteamUgc ugc, ulong[] items)
    {
        var refused = new List<ulong>();
        foreach (ulong id in items)
            if (!IsCurrent(ugc.State(id)) && !ugc.Download(id)) refused.Add(id);
        return refused;
    }

    /// <summary>
    /// Polls until every item is current or the sync is stopped; how many are current then, and whether it was stopped.
    /// </summary>
    static (int Installed, bool Stopped) Wait(SteamUgc ugc, ulong[] items, Action<int, int> report, CancellationToken stop)
    {
        int reported = -1;
        while (true)
        {
            ugc.RunCallbacks();
            int installed = items.Count(id => IsCurrent(ugc.State(id)));
            if (installed != reported) report(reported = installed, items.Length);

            if (installed == items.Length) return (installed, false);
            if (stop.WaitHandle.WaitOne(PollMilliseconds)) return (installed, true);
        }
    }

    /// <summary>
    /// True for an item that is installed and has nothing left to download.
    /// </summary>
    static bool IsCurrent(WorkshopItemState state) =>
        state.HasFlag(WorkshopItemState.Installed)
        && (state & (WorkshopItemState.NeedsUpdate | WorkshopItemState.Downloading | WorkshopItemState.DownloadPending)) == 0;
}
