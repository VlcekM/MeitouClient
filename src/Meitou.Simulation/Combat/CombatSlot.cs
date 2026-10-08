namespace Meitou.Simulation.Combat;

/// <summary>
/// One character's combat state, small enough to copy by block: the target, the attack in progress, the reaction in progress, timers and counters. The
/// <see cref="CombatSystem"/> keeps two arrays of these (the last tick's, which every phase reads, and the one being computed, where a phase writes only its own
/// characters), like <see cref="CharacterTable"/>. Times are simulation ticks; progress is (now - start) / ticks.
/// </summary>
public struct CombatSlot
{
    /// <summary>The generation of the character the slot describes (a reused slot starts clean); -1 before the first sync.</summary>
    public int Generation;
    /// <summary>The target of the fight (slot and generation), -1 for none.</summary>
    public int TargetSlot, TargetGeneration;
    /// <summary>The attack technique (index into the system's list), -1 when not attacking.</summary>
    public int AttackTech;
    /// <summary>The tick the attack started, its length in ticks, and the tick after which a new action may start.</summary>
    public int AttackStart, AttackTicks, AttackEnd;
    /// <summary>The blow (1 or 2) that arrives next, 0 when all have; the attack number (raised per attack, part of the roll keys); the reach the attack was chosen at.</summary>
    public int NextBlow, AttackSeq;
    public float AttackReach;
    /// <summary>The attacker's rating at the start of the attack (melee attack x injury multiplier + weapon mod), what defenders compare their defence with.</summary>
    public float AttackSkill;
    /// <summary>The reaction technique (index), -1 when not reacting; its start tick and length in ticks.</summary>
    public int ReactTech, ReactStart, ReactTicks;
    /// <summary>The last blow the character made a reaction decision about (attacker slot, attack number, blow), so each blow is decided once.</summary>
    public int ReactedSlot, ReactedSeq, ReactedBlow;
    /// <summary>The earliest tick a new attack may start, and the tick until which a heavy hit holds the character helpless.</summary>
    public int ReadyTick, StunUntil;
    /// <summary>Knocked out or dead (a copy of <c>MedicalState.Incapacitated</c> that other characters can read).</summary>
    public bool Down;
    /// <summary>Counters: blows thrown, hits landed, blows blocked or dodged by the target, hits taken, blocks, dodges made.</summary>
    public int Thrown, Landed, Parried, Evaded, Taken, Blocks, Dodges;

    public static CombatSlot Fresh(int generation) => new()
    {
        Generation = generation, TargetSlot = -1, TargetGeneration = 0, AttackTech = -1, ReactTech = -1, ReactedSlot = -1, ReactedSeq = -1,
    };

    public readonly bool Fighting => TargetSlot >= 0;

    internal readonly void Hash(ref StateHasher h)
    {
        h.Add(Generation);
        h.Add(TargetSlot);
        h.Add(TargetGeneration);
        h.Add(AttackTech);
        h.Add(AttackStart);
        h.Add(AttackTicks);
        h.Add(AttackEnd);
        h.Add(NextBlow);
        h.Add(AttackSeq);
        h.Add(AttackReach);
        h.Add(AttackSkill);
        h.Add(ReactTech);
        h.Add(ReactStart);
        h.Add(ReactTicks);
        h.Add(ReactedSlot);
        h.Add(ReactedSeq);
        h.Add(ReactedBlow);
        h.Add(ReadyTick);
        h.Add(StunUntil);
        h.Add(Down);
        h.Add(Thrown);
        h.Add(Landed);
        h.Add(Parried);
        h.Add(Evaded);
        h.Add(Taken);
        h.Add(Blocks);
        h.Add(Dodges);
    }
}
