using System.Diagnostics;

namespace Meitou.Steam;

/// <summary>
/// Has the running Steam client subscribe the account to Kenshi Workshop items and download them, as the Subscribe button of their Workshop
/// pages does. Steam is not started, and an item Steam refuses is reported with its reason (docs/launcher.md "Workshop subscribe").
/// </summary>
public static class WorkshopSubscribe
{
    /// <summary>
    /// Steam's <c>EResult</c> for success.
    /// </summary>
    const int Ok = 1;

    /// <summary>
    /// How long Steam gets to answer one subscribe call.
    /// </summary>
    static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the subscribed items get to arrive before the result says they are still downloading.
    /// </summary>
    static readonly TimeSpan InstallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often Steam's answers and the item states are read.
    /// </summary>
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Subscribes to <paramref name="items"/> through the library at <paramref name="libraryPath"/> (<see cref="SteamLibrary.Locate"/>) and
    /// waits, at most <see cref="InstallTimeout"/>, for the subscribed ones to be installed. Blocks: run it off the UI thread.
    /// </summary>
    public static WorkshopSubscribeResult Run(string libraryPath, IReadOnlyList<ulong> items)
    {
        using var ugc = SteamUgc.TryOpen(libraryPath, out string? problem);
        if (ugc is null) return new(problem, []);

        var problems = items.ToDictionary(id => id, id => Subscribe(ugc, id));
        ulong[] subscribed = [.. items.Where(id => problems[id] is null)];
        foreach (ulong id in subscribed)
            if (!WorkshopSync.IsCurrent(ugc.State(id))) ugc.Download(id);

        WaitForInstall(ugc, subscribed);
        return new(null, [.. items.Select(id => new WorkshopSubscription(id, problems[id], WorkshopSync.IsCurrent(ugc.State(id))))]);
    }

    /// <summary>
    /// Subscribes to one item and waits for Steam's answer: null once the account is subscribed (or was already), else why it is not.
    /// </summary>
    static string? Subscribe(SteamUgc ugc, ulong id)
    {
        if (IsSubscribed(ugc, id)) return null;

        ulong call = ugc.Subscribe(id);
        if (call == 0) return "Steam did not take the request.";

        int? result = Answer(ugc, call);

        // The item's state is the outcome; the call's result only says why not.
        ugc.RunCallbacks();
        if (result == Ok || IsSubscribed(ugc, id)) return null;

        return result is { } code ? Reason(code) : "Steam did not answer in time.";
    }

    /// <summary>
    /// Whether the client holds the account as subscribed to an item.
    /// </summary>
    static bool IsSubscribed(SteamUgc ugc, ulong id) => ugc.State(id).HasFlag(WorkshopItemState.Subscribed);

    /// <summary>
    /// Steam's answer to a subscribe call (<see cref="SteamUgc.SubscribeResult"/>); null when none came within <see cref="AnswerTimeout"/>.
    /// </summary>
    static int? Answer(SteamUgc ugc, ulong call)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < AnswerTimeout)
        {
            ugc.RunCallbacks();
            if (ugc.SubscribeResult(call) is { } result) return result;

            Thread.Sleep(Poll);
        }

        return null;
    }

    /// <summary>
    /// Polls until every item of <paramref name="items"/> is installed and current, or <see cref="InstallTimeout"/> has passed.
    /// </summary>
    static void WaitForInstall(SteamUgc ugc, ulong[] items)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < InstallTimeout && !items.All(id => WorkshopSync.IsCurrent(ugc.State(id))))
        {
            ugc.RunCallbacks();
            Thread.Sleep(Poll);
        }
    }

    /// <summary>
    /// What a refused subscribe call says, by Steam's <c>EResult</c> (the common ones named).
    /// </summary>
    static string Reason(int result) => result switch
    {
        SteamUgc.CallFailed => "Steam could not complete the request.",
        3 => "Steam has no connection.",
        9 => "Steam does not know this Workshop item (it may have been removed).",
        15 => "Steam denied access to this Workshop item.",
        16 => "Steam timed out.",
        20 => "The Steam Workshop is unavailable right now.",
        21 => "Steam is not signed in.",
        25 or 84 => "Steam says too many requests were made; try again later.",
        _ => $"Steam refused (result {result}).",
    };
}
