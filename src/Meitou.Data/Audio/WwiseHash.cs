namespace Meitou.Data.Audio;

/// <summary>
/// Wwise turns names (events, banks, game parameters, states, switches) into 32-bit ids with FNV-1 over the lowercased ASCII name
/// (docs/formats/wwise-bnk.md#ids-and-names). Banks hold only the ids, so names come from the game.
/// </summary>
public static class WwiseHash
{
    /// <summary>
    /// The Wwise id of <paramref name="name"/>: FNV-1 (32-bit) over each character lowercased and cut to a byte.
    /// </summary>
    public static uint Id(string name)
    {
        uint hash = 2166136261;

        foreach (char c in name)
        {
            hash *= 16777619;
            hash ^= (byte)char.ToLowerInvariant(c);
        }

        return hash;
    }
}