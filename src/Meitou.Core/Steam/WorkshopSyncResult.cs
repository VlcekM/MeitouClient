namespace Meitou.Steam;

/// <summary>
/// How a <see cref="WorkshopSync"/> ended: <paramref name="Problem"/> is the reason (in English) when it could not run at all, else
/// <paramref name="Total"/> subscribed items, <paramref name="Installed"/> of those now installed and up to date, <paramref name="Refused"/> that Steam
/// would not download, and whether it was <paramref name="Stopped"/> before it finished.
/// </summary>
public sealed record WorkshopSyncResult(string? Problem, int Total, int Installed, int Refused, bool Stopped);
