using Meitou.Simulation;

namespace ProvingGrounds.Engine;

/// <summary>Character ids as scripts write them: <c>c:&lt;slot&gt;.&lt;generation&gt;</c> (the <c>c:</c> is optional); digits only, no signs or spaces.</summary>
static class CharacterIdText
{
    internal static bool TryParse(string s, out CharacterId id)
    {
        id = CharacterId.None;
        if (s.StartsWith("c:", StringComparison.Ordinal)) s = s[2..];
        var p = s.Split('.');
        if (p.Length != 2 || !int.TryParse(p[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int a)
            || !int.TryParse(p[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int b)) return false;
        id = new CharacterId(a, b);
        return true;
    }

    internal static string Format(CharacterId id) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"c:{id.Slot}.{id.Generation}");
}
