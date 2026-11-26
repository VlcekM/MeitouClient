using System.Diagnostics;

namespace Meitou.Steam;

/// <summary>
/// One sync's watch over the subscribed items (<see cref="WorkshopSync"/>): it reads the subscriptions and every item's state at each poll, asks
/// for each item that is not current, and ends once every item is current and nothing has changed for <see cref="SettleSeconds"/>. The client
/// answers a process that has just started as Kenshi from its cache and refreshes it moments later: the subscriptions and the updates made since
/// Steam last ran as Kenshi appear only then (docs/launcher.md "Workshop sync").
/// </summary>
sealed class WorkshopSyncWatch(SteamUgc ugc)
{
    /// <summary>
    /// How often the subscriptions and the item states are read.
    /// </summary>
    const int PollMilliseconds = 500;

    /// <summary>
    /// The seconds without a change, counted from the start, before a sync with every item current ends: ten times the half second the client
    /// took to refresh (an engine choice).
    /// </summary>
    const double SettleSeconds = 5;

    /// <summary>
    /// The items asked for while not current; one is asked again once it was current and then is not.
    /// </summary>
    readonly HashSet<ulong> _requested = [];

    /// <summary>
    /// The items the client would not download.
    /// </summary>
    readonly HashSet<ulong> _refused = [];

    /// <summary>
    /// Each item's state at the last poll.
    /// </summary>
    readonly Dictionary<ulong, WorkshopItemState> _states = [];

    /// <summary>
    /// The time since the subscriptions or a state last changed (from the start).
    /// </summary>
    readonly Stopwatch _quiet = Stopwatch.StartNew();

    /// <summary>
    /// The subscriptions at the last poll.
    /// </summary>
    ulong[] _subscribed = [];

    /// <summary>
    /// Polls until every wanted item is current and the subscriptions have settled, or <paramref name="stop"/> is cancelled, calling
    /// <paramref name="report"/> with the installed and the total count whenever either changes.
    /// </summary>
    public WorkshopSyncResult Run(Action<int, int> report, CancellationToken stop)
    {
        (int Installed, int Total) reported = (-1, -1);
        while (true)
        {
            Poll();
            var counts = Counts();
            if (counts != reported) report((reported = counts).Installed, counts.Total);

            if (Settled(counts)) return Result(stopped: false);
            if (stop.WaitHandle.WaitOne(PollMilliseconds)) return Result(stopped: true);
        }
    }

    /// <summary>
    /// Whether every wanted item is current and nothing has changed for <see cref="SettleSeconds"/>.
    /// </summary>
    bool Settled((int Installed, int Total) counts) => counts.Installed == counts.Total && _quiet.Elapsed.TotalSeconds >= SettleSeconds;

    /// <summary>
    /// Reads the subscriptions and the states after the client's callbacks, and asks for the items that are not current.
    /// </summary>
    void Poll()
    {
        ugc.RunCallbacks();
        ulong[] subscribed = ugc.SubscribedItems();
        if (!subscribed.ToHashSet().SetEquals(_subscribed)) Changed();
        _subscribed = subscribed;

        foreach (ulong id in subscribed)
            Track(id, ugc.State(id));
    }

    /// <summary>
    /// Notes the item's state and asks for it when it is not current and was not asked for since it last was.
    /// </summary>
    void Track(ulong id, WorkshopItemState state)
    {
        if (!_states.TryGetValue(id, out var before) || before != state) Changed();
        _states[id] = state;

        if (WorkshopSync.IsCurrent(state))
        {
            _requested.Remove(id);
            return;
        }

        if (_refused.Contains(id) || !_requested.Add(id)) return;
        if (!ugc.Download(id)) _refused.Add(id);
    }

    /// <summary>
    /// Something changed: the quiet time starts again.
    /// </summary>
    void Changed() => _quiet.Restart();

    /// <summary>
    /// The current and the wanted item counts (the subscriptions but the refused ones).
    /// </summary>
    (int Installed, int Total) Counts()
    {
        var wanted = _subscribed.Where(id => !_refused.Contains(id)).ToList();
        return (wanted.Count(id => WorkshopSync.IsCurrent(_states[id])), wanted.Count);
    }

    /// <summary>
    /// The sync's result from the last poll, with the folders of the items no longer subscribed: the client does not delete them while a process
    /// runs as Kenshi (docs/launcher.md "Workshop sync"), so they outlast the sync.
    /// </summary>
    WorkshopSyncResult Result(bool stopped)
    {
        var (installed, _) = Counts();
        int refused = _subscribed.Count(_refused.Contains);
        ulong[] unsubscribed = [.. ItemFolders().Except(_subscribed).Order()];

        return new WorkshopSyncResult(null, _subscribed.Length, installed, refused, stopped, unsubscribed);
    }

    /// <summary>
    /// The item ids of the folders the client keeps the Kenshi items in (<see cref="MacSteam.WorkshopContentDirectory"/>); none when they cannot
    /// be listed.
    /// </summary>
    static HashSet<ulong> ItemFolders()
    {
        if (MacSteam.WorkshopContentDirectory is not { } content || !Directory.Exists(content)) return [];

        try
        {
            var ids = new HashSet<ulong>();
            foreach (string folder in Directory.EnumerateDirectories(content))
                if (ulong.TryParse(Path.GetFileName(folder), out ulong id)) ids.Add(id);

            return ids;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
