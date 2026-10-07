namespace Meitou.Simulation.Bodies;

/// <summary>
/// Random numbers for the body code, from the world's seeded hash (<see cref="Rng"/>): a roll depends only on (world seed, character key, a counter), so
/// it is the same whichever thread runs the character. The key is XORed with a constant of this module so the rolls never coincide with another
/// system's stream of the same character (the purpose enum belongs to the simulation core and is not extended here).
/// </summary>
public static class BodyRolls
{
    /// <summary>XORed into the character key: the stats module's rolls (starting-stat randomisation).</summary>
    public const ulong StatsDomain = 0x57A7_5000_0000_0001UL;

    /// <summary>XORed into the character key: the medical module's rolls (hit location, blood lost to a severed limb, KO timers).</summary>
    public const ulong MedicalDomain = 0x3ED1_CA10_0000_0002UL;

    /// <summary>A roll in [0, 1) for a (seed, key, domain, counter).</summary>
    public static float Float(ulong seed, ulong characterKey, ulong domain, ulong counter) =>
        Rng.Float(Rng.Hash(seed, characterKey ^ domain, RngPurpose.Test, counter));
}
