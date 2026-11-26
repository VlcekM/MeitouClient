namespace Meitou.Steam;

/// <summary>
/// How a <see cref="WorkshopSync"/> ended: <paramref name="Problem"/> is the reason (in English) when it could not run at all, else
/// <paramref name="Total"/> subscribed items, <paramref name="Installed"/> of those now installed and up to date, <paramref name="Refused"/> that Steam
/// would not download, whether it was <paramref name="Stopped"/> before it finished, and the items still on disk though no longer subscribed
/// (<paramref name="Unsubscribed"/>: the client deletes them later, not while a process runs as Kenshi).
/// </summary>
public sealed record WorkshopSyncResult(string? Problem, int Total, int Installed, int Refused, bool Stopped, IReadOnlyList<ulong> Unsubscribed)
{
    /// <summary>
    /// The released constructor, kept for built mods (docs/modding.md "API compatibility"): no item left unsubscribed.
    /// </summary>
    public WorkshopSyncResult(string? problem, int total, int installed, int refused, bool stopped)
        : this(problem, total, installed, refused, stopped, [])
    {
    }

    /// <summary>
    /// The released deconstruction, kept for built mods: every part but <see cref="Unsubscribed"/>.
    /// </summary>
    public void Deconstruct(out string? problem, out int total, out int installed, out int refused, out bool stopped) =>
        (problem, total, installed, refused, stopped) = (Problem, Total, Installed, Refused, Stopped);
}
