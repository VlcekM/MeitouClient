namespace Meitou.Game.Host;

/// <summary>
/// The player's user name kept out of the logs: the home folder (<c>Environment.SpecialFolder.UserProfile</c>) is written with its last part as
/// <c>&lt;username&gt;</c> (<c>C:\Users\&lt;username&gt;</c>), in either slash direction and any case. Players share their logs in public, by
/// hand as well as through a report.
/// </summary>
static class UserName
{
    /// <summary>
    /// The player's home folder as paths show it and the same with <c>&lt;username&gt;</c>, each also with forward slashes; null when there is none.
    /// </summary>
    static readonly (string Home, string Hidden, string HomeSlashed, string HiddenSlashed)? HomeFolder =
        HomeOf(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// The shortest text that can hold the home folder; 0 when there is none.
    /// </summary>
    public static int Shortest { get; } = HomeFolder is var (home, _, _, _) ? home.Length : 0;

    /// <summary>
    /// <paramref name="text"/> with the home folder written with <c>&lt;username&gt;</c>; the same string when it holds none.
    /// </summary>
    public static string Hide(string text)
    {
        if (HomeFolder is not var (home, hidden, homeSlashed, hiddenSlashed) || text.Length < home.Length) return text;

        text = text.Replace(home, hidden, StringComparison.OrdinalIgnoreCase);
        return text.Replace(homeSlashed, hiddenSlashed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <paramref name="home"/> and the same path with its last part as <c>&lt;username&gt;</c>, each also with forward slashes; null for an empty
    /// path or a root.
    /// </summary>
    static (string Home, string Hidden, string HomeSlashed, string HiddenSlashed)? HomeOf(string home)
    {
        string trimmed = home.TrimEnd('\\', '/');
        if (trimmed.Length == 0 || Path.GetDirectoryName(trimmed) is not { Length: > 0 } parent) return null;

        string hidden = Path.Combine(parent, "<username>");
        return (trimmed, hidden, trimmed.Replace('\\', '/'), hidden.Replace('\\', '/'));
    }
}
