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
    /// Syncs, calling <paramref name="report"/> with the installed and the total item count whenever either changes, until every item is installed
    /// and the subscriptions have settled (<see cref="WorkshopSyncWatch"/>) or <paramref name="stop"/> is cancelled (the client then goes on
    /// downloading by itself). Blocks: run it off the UI thread.
    /// </summary>
    public static WorkshopSyncResult Run(Action<int, int> report, CancellationToken stop)
    {
        if (!MacSteam.CanSync) return Failed("Steam's library was not found, so the Workshop cannot be synchronised.");
        if (!MacSteam.IsRunning) return Failed(NotRunning);

        using var ugc = SteamUgc.TryOpen(MacSteam.LibraryPath, out string? problem);
        if (ugc is null) return Failed(problem!);

        return new WorkshopSyncWatch(ugc).Run(report, stop);
    }

    /// <summary>
    /// A sync that could not run.
    /// </summary>
    static WorkshopSyncResult Failed(string problem) => new(problem, 0, 0, 0, false, []);

    /// <summary>
    /// True for an item that is installed and has nothing left to download.
    /// </summary>
    internal static bool IsCurrent(WorkshopItemState state) =>
        state.HasFlag(WorkshopItemState.Installed)
        && (state & (WorkshopItemState.NeedsUpdate | WorkshopItemState.Downloading | WorkshopItemState.DownloadPending)) == 0;
}
