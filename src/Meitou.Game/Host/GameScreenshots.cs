using System.Globalization;

namespace Meitou.Game.Host;

/// <summary>
/// Where a screenshot goes (docs/game/ui-input.md "Screenshots", the frame-end callback <c>FUN_14082b6e0</c>, <b>Verified</b>):
/// <c>_screens/screenshot_&lt;n&gt;.&lt;format&gt;</c> in the install folder, <c>n</c> one more than the <c>count</c> of <c>screens.cfg</c> (a
/// <c>key=value</c> file, the count is written back), always PNG (a deliberate departure: the original also writes the <c>format</c> key's bmp or
/// jpg).
/// </summary>
static class GameScreenshots
{
    /// <summary>
    /// The <c>key=value</c> file in the install folder that holds the screenshot <c>count</c>.
    /// </summary>
    public const string ConfigFile = "screens.cfg";
    /// <summary>
    /// The folder in the install the screenshots go to.
    /// </summary>
    public const string Folder = "_screens";

    /// <summary>
    /// The next screenshot's path; the count in <paramref name="root"/>'s <c>screens.cfg</c> is raised (the original saves it before the picture is
    /// taken).
    /// </summary>
    public static string Next(string root)
    {
        string config = Path.Combine(root, ConfigFile);
        var lines = File.Exists(config) ? File.ReadAllLines(config).ToList() : [];
        int number = (short)AtoI(Get(lines, "count")) + 1;
        Set(lines, "count", number.ToString(CultureInfo.InvariantCulture));
        try
        {
            File.WriteAllLines(config, lines);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return Path.Combine(root, Folder, $"screenshot_{number.ToString(CultureInfo.InvariantCulture)}.png");
    }

    /// <summary>
    /// The <c>multiplier</c> key of <paramref name="root"/>'s <c>screens.cfg</c> (<c>atoi</c>, 1 when absent or below 2): the shot is n times the
    /// window in each direction.
    /// </summary>
    public static int Multiplier(string root)
    {
        string config = Path.Combine(root, ConfigFile);
        if (!File.Exists(config)) return 1;
        try
        {
            return Math.Max(1, AtoI(Get(File.ReadAllLines(config).ToList(), "multiplier")));
        }
        catch (IOException)
        {
            return 1;
        }
    }

    /// <summary>
    /// C's <c>atoi</c>: an optional sign and the leading digits, else 0.
    /// </summary>
    public static int AtoI(string? text)
    {
        if (text is null) return 0;
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        bool negative = i < text.Length && text[i] == '-';
        if (i < text.Length && text[i] is '-' or '+') i++;
        long value = 0;
        for (; i < text.Length && text[i] is >= '0' and <= '9' && value < int.MaxValue; i++) value = value * 10 + (text[i] - '0');
        return (int)(negative ? -value : value);
    }

    /// <summary>
    /// The value of <paramref name="key"/> in the <c>key=value</c> lines; null when it is not there.
    /// </summary>
    static string? Get(List<string> lines, string key)
    {
        foreach (var line in lines)
            if (line.Split('=', 2) is [var k, var v] && k.Trim() == key) return v.Trim();
        return null;
    }

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/> in the <c>key=value</c> lines, adding the line when it is not there.
    /// </summary>
    static void Set(List<string> lines, string key, string value)
    {
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Split('=', 2) is [var k, _] && k.Trim() == key)
            {
                lines[i] = $"{key}={value}";
                return;
            }
        lines.Add($"{key}={value}");
    }
}