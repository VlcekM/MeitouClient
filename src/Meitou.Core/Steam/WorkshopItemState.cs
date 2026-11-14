namespace Meitou.Steam;

/// <summary>
/// The bits of Steamworks' <c>EItemState</c> that a sync reads. <b>Verified</b> on macOS Steam (19 subscribed Kenshi items): an item not yet
/// downloaded reads <c>Subscribed | NeedsUpdate</c> (9), a downloaded one <c>Subscribed | Installed</c> (5), a just unsubscribed one <c>Installed</c> (4).
/// </summary>
[Flags]
public enum WorkshopItemState : uint
{
    /// <summary>
    /// No bit set.
    /// </summary>
    None = 0,

    /// <summary>
    /// The account is subscribed to the item.
    /// </summary>
    Subscribed = 1,

    /// <summary>
    /// The item is installed on disk.
    /// </summary>
    Installed = 4,

    /// <summary>
    /// The item is not installed yet or has a newer version.
    /// </summary>
    NeedsUpdate = 8,

    /// <summary>
    /// The item is downloading now.
    /// </summary>
    Downloading = 16,

    /// <summary>
    /// A download was asked for and has not started.
    /// </summary>
    DownloadPending = 32
}
