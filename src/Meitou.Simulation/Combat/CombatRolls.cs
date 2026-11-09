namespace Meitou.Simulation.Combat;

/// <summary>
/// What a combat roll decides; part of the stream key so no two decisions of a character share a number.
/// </summary>
public enum CombatRoll : ulong
{
    /// <summary>
    /// Which attack technique (counter: the character's attack number).
    /// </summary>
    Technique = 1,
    /// <summary>
    /// The Block order's timer that frees the attack slots on the blocker (counter: the tick it is drawn).
    /// </summary>
    BlockTimer = 2,
    /// <summary>
    /// The defender's block (or dodge) roll against one incoming blow.
    /// </summary>
    Block = 3,
    /// <summary>
    /// Which reaction technique among the candidates.
    /// </summary>
    Reaction = 4,
    /// <summary>
    /// Whether a piece of armour covers the part a blow hit.
    /// </summary>
    Cover = 5,
    /// <summary>
    /// Which hit reaction clip a hit plays (counter: the attacker, its attack number and the blow).
    /// </summary>
    Stumble = 6,
    /// <summary>
    /// Who swings first when two fighters are ready at each other on the same tick (keyed by the pair, so both sides draw the same number).
    /// </summary>
    Initiative = 7,
    /// <summary>
    /// Whether a landed blow knocks a shackled defender out instead of staggering it (counter: the blow).
    /// </summary>
    Shackled = 9,
    /// <summary>
    /// A fighter's steering draws (ENEMY_DIST, ALLY_DIST, the dead zone; counter 0 to 3, drawn once: docs/game/combat.md "Fight steering").
    /// </summary>
    Steering = 10,
    /// <summary>
    /// Whether an attack on a character getting up knocks it back down for 10 s (counter: the tick and the attacker).
    /// </summary>
    KnockDown = 11,
}

/// <summary>
/// Random numbers of the combat code: a roll depends only on (world seed, character key, kind, counter), so it is the same whichever thread or order
/// handles the character (docs/simulation.md "Threading": randomness). Built on <see cref="Rng"/> with the <see cref="RngPurpose.Combat"/> stream.
/// </summary>
public static class CombatRolls
{
    /// <summary>
    /// A roll in [0, 1).
    /// </summary>
    public static float Float(ulong seed, ulong characterKey, CombatRoll kind, ulong counter) =>
        Rng.Float(Rng.Hash(seed, characterKey ^ ((ulong)kind * 0xA24BAED4963EE407UL), RngPurpose.Combat, counter));

    /// <summary>
    /// A counter naming one blow: the attacker, its attack number, the blow within it and an index (the armour piece).
    /// </summary>
    public static ulong BlowCounter(int attackerSlot, int attackSeq, int blow, int index) =>
        ((ulong)(uint)attackerSlot << 40) ^ ((ulong)(uint)attackSeq << 12) ^ ((ulong)(uint)blow << 8) ^ (uint)index;
}