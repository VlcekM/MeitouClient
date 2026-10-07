namespace Meitou.Simulation.Combat;

/// <summary>What a combat roll decides; part of the stream key so no two decisions of a character share a number.</summary>
public enum CombatRoll : ulong
{
    /// <summary>Which attack technique (counter: the character's attack number).</summary>
    Technique = 1,
    /// <summary>The pause before an attack starts.</summary>
    Pause = 2,
    /// <summary>The defender's block (or dodge) roll against one incoming blow.</summary>
    Block = 3,
    /// <summary>Which reaction technique among the candidates.</summary>
    Reaction = 4,
    /// <summary>Whether a piece of armour covers the part a blow hit.</summary>
    Cover = 5,
}

/// <summary>
/// Random numbers of the combat code: a roll depends only on (world seed, character key, kind, counter), so it is the same whichever thread or order handles the
/// character (docs/simulation.md "Threading": randomness). Built on <see cref="Rng"/> with the <see cref="RngPurpose.Combat"/> stream.
/// </summary>
public static class CombatRolls
{
    /// <summary>A roll in [0, 1).</summary>
    public static float Float(ulong seed, ulong characterKey, CombatRoll kind, ulong counter) =>
        Rng.Float(Rng.Hash(seed, characterKey ^ ((ulong)kind * 0xA24BAED4963EE407UL), RngPurpose.Combat, counter));

    /// <summary>A counter naming one blow: the attacker, its attack number, the blow within it and an index (the armour piece).</summary>
    public static ulong BlowCounter(int attackerSlot, int attackSeq, int blow, int index) =>
        ((ulong)(uint)attackerSlot << 40) ^ ((ulong)(uint)attackSeq << 12) ^ ((ulong)(uint)blow << 8) ^ (uint)index;
}
