namespace Meitou.Simulation;

/// <summary>
/// The constants mixed into the seed keys of the population's random streams, named in one place so two streams never share one by accident.
/// Changing any value re-rolls what it keys (and every state hash that follows), so they are pinned by the golden hashes.
/// </summary>
internal static class SeedSalts
{
    /// <summary>XORed into the key of a bar squad (a resident squad's key has none).</summary>
    public const ulong BarSquad = 0xBA45UL;
    /// <summary>XORed with the hash of the start-off's id into the player's start key.</summary>
    public const ulong PlayerStart = 0x57A27UL;
    /// <summary>XORed into the tick of a roaming squad's choice of its next town.</summary>
    public const ulong RoamNext = 0x7A6E7UL;
    /// <summary>XORed into the key of a roaming squad a town's pool makes.</summary>
    public const ulong RoamingPool = 0x20A4UL;
    /// <summary>Added to the count of slave squads in the key of the next slave squad.</summary>
    public const ulong SlaveSquad = 0x5A1AUL;

    /// <summary>Counter (under <see cref="RngPurpose.Spawn"/>) of the roll that decides whether a bar squad is present.</summary>
    public const ulong BarChance = 77;
    /// <summary>Counter of the roll that picks which squad template a town's roaming pool draws.</summary>
    public const ulong RoamingPick = 5;
    /// <summary>Counter base of the roll for the look of member n of a town squad (n is added).</summary>
    public const ulong MemberLook = 1000;
    /// <summary>Counter base of the roll for the look of member n of the player's squad (n is added).</summary>
    public const ulong PlayerLook = 5000;
}
