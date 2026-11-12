using Meitou.Data.Fcs;

namespace Meitou.Data.Save;

/// <summary>
/// The STATS floats a record always has that the stat system does not write (the legacy ones), and how a new save fills them.
/// </summary>
public static class LegacyStats
{
    /// <summary>
    /// The STATS floats a record always has that the stat system does not write (the legacy ones): zero.
    /// </summary>
    public static readonly string[] Keys = ["xp", "free attribute points", "warrior spirit", "endurance", "ff", "hackers", "survival", "arrow defence"];

    /// <summary>
    /// Fills the legacy STATS floats a real record always has (zero unless already set); a new character's <c>warrior spirit</c> is the STATS
    /// template's (<paramref name="warriorSpirit"/>, <c>FUN_14064b6a0</c> reads it into CharStats, <c>FUN_14064a8f0</c> writes it back).
    /// </summary>
    public static void Fill(FcsRecord stats, float warriorSpirit = 0)
    {
        foreach (var k in Keys) stats.Floats.TryAdd(k, k == "warrior spirit" ? warriorSpirit : 0);
    }
}
