using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>How a blow ended (docs/game/combat.md "Hit outcome", <c>FUN_140665fa0</c>).</summary>
public enum BlowOutcome
{
    /// <summary>The blow reached the body.</summary>
    Hit,
    /// <summary>The defender's block technique faced the blow and was past half its animation.</summary>
    Blocked,
    /// <summary>The defender's dodge technique was within its window.</summary>
    Dodged,
    /// <summary>The target was no longer in reach (engine rule): nothing happens.</summary>
    Missed,
}

/// <summary>The outcome rule: which of a blow's endings the defender's reaction at that moment gives.</summary>
public static class HitOutcomes
{
    /// <summary>A block must be past this much of its animation (<b>Observed</b>, <c>FUN_140665fa0</c>).</summary>
    public const float BlockProgress = 0.5f;
    /// <summary>A dodge counts within this window of its animation (<b>Observed</b>).</summary>
    public const float DodgeFrom = 0.1f, DodgeTo = 0.98f;

    /// <summary>
    /// Blocked (code 5) if the defender's technique is a block, its direction equals the blow's and its animation is past 50 per cent; dodged (code 0) if it is a dodge and the progress is in
    /// [0.1, 0.98]; otherwise a hit (code 2). The order is the doc's: a technique flagged both (the base game's "Dodge back" rows are) counts as a block for a blow from the direction it faces.
    /// </summary>
    public static BlowOutcome Decide(CombatTechnique? reaction, float progress, int blowDirection)
    {
        if (reaction is null) return BlowOutcome.Hit;
        if (reaction.IsBlock && reaction.AttackDirection1 == blowDirection && progress > BlockProgress) return BlowOutcome.Blocked;
        if (reaction.IsDodge && progress >= DodgeFrom && progress <= DodgeTo) return BlowOutcome.Dodged;
        return BlowOutcome.Hit;
    }
}

/// <summary>Engine choices of the duel loop where the research stops (each is labelled <b>Unknown</b> in docs/simulation.md "Combat as built").</summary>
public static class CombatTuning
{
    /// <summary>Seconds a fighter waits before an attack after being ordered, and between attacks (a uniform draw between the two).</summary>
    public const float PauseMin = 0.15f, PauseMax = 0.6f;
    /// <summary>Seconds a heavy hit (a damage sum above the stumble threshold) staggers its target; a light hit does not.</summary>
    public const float HeavyHitStun = 0.5f;
    /// <summary>How far (world units) a defender looks for blows coming at it.</summary>
    public const float ReactionScanRadius = 60;
    /// <summary>A blow lands if the gap between the two bodies is still within the reach it started with plus this slack; else it misses.</summary>
    public const float ReachSlack = 6;
    /// <summary>Seconds a clip plays at when its length is not known.</summary>
    public const float DefaultClipSeconds = 1;
    /// <summary>A fighter closes in until the gap between bodies is this (units), when it moves by itself.</summary>
    public const float CloseInGap = 3;
}

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

/// <summary>
/// Picking a technique, pure given the rolls (docs/game/combat.md "Technique choice, block and dodge"). The attacker-side choice was not read in the original (<b>Unknown</b>); the
/// rules here are the engine's, built on the record fields: weapon type flags, the disabled / block / dodge / prone / animal fields, the skill range, and the distance fields.
/// </summary>
public static class TechniqueChooser
{
    /// <summary>
    /// The attack techniques usable now, by weighted random pick on <c>chance</c> (<paramref name="roll"/> in [0, 1)); -1 when none. A technique is a candidate when it is not disabled, not a
    /// block or dodge, its weapon type flags include the wielder's, it is for the wielder's kind of creature (<paramref name="animalCategory"/>, 0 for a person), the standing / prone flag
    /// matches, the skill is within <c>min skill</c> to <c>max skill</c>, and the gap between the two bodies is within both the weapon's reach and the technique's distance: <c>attack distance min
    /// vs static</c> against a target that stands still, <c>attack distance</c> against one that moves (a negative distance means the technique is not for that case:
    /// "Cut left static" has -999 for a moving target, "Cut left" has -10 for a static one).
    /// </summary>
    public static int ChooseAttack(IReadOnlyList<CombatTechnique> all, WeaponKinds weaponKind, int animalCategory, bool prone, float skill, float gap, bool targetMoving, float weaponReach, float roll)
    {
        float total = 0;
        for (int i = 0; i < all.Count; i++) total += Weight(all[i], weaponKind, animalCategory, prone, skill, gap, targetMoving, weaponReach);
        if (total <= 0) return -1;
        return Pick(all, roll * total, t => Weight(t, weaponKind, animalCategory, prone, skill, gap, targetMoving, weaponReach));
    }

    static float Weight(CombatTechnique t, WeaponKinds kind, int animal, bool prone, float skill, float gap, bool moving, float weaponReach)
    {
        if (t.Disabled || t.IsBlock || t.IsDodge || !FitsKind(t, kind) || t.Animal != animal || t.IsProne != prone) return 0;
        if (skill < t.MinSkill || skill > t.MaxSkill) return 0;
        float distance = moving ? t.AttackDistance : t.AttackDistanceMinVsStatic;
        if (distance < 0 || gap > MathF.Min(distance, weaponReach)) return 0;
        return MathF.Max(t.Chance, 0);
    }

    /// <summary>The weapon types a technique is valid for, matched on the type bits (the "1 handed" flag is not tested).</summary>
    public static bool FitsKind(CombatTechnique t, WeaponKinds kind) => (t.Kinds & kind & ~WeaponKinds.OneHanded) != 0;

    /// <summary>The distance the technique can strike at (gap between bodies), for the reach the attack is chosen at.</summary>
    public static float ReachOf(CombatTechnique t, bool targetMoving, float weaponReach) => MathF.Min(targetMoving ? t.AttackDistance : t.AttackDistanceMinVsStatic, weaponReach);

    /// <summary>The front directions (1 down, 2 left, 3 right, 4 thrust, 5 up, 6 pierced, 0 default) and the rear ones (7 to 9) are two classes; a wrong-direction block stays in the blow's class.</summary>
    public static bool SameClass(int a, int b) => (a >= 7) == (b >= 7);

    /// <summary>
    /// The defender's reaction (<c>FUN_140887970</c>, <b>Observed</b>), given the block roll and the pick roll. Candidates are the block or dodge techniques of the defender's weapon type,
    /// standing posture and creature kind. A fighter with a weapon that can block rolls to block: on success the candidates are the techniques whose direction equals the blow's (the
    /// base game's "Dodge back" rows are flagged as blocks too, so they are among them), on failure the block techniques of a different direction in the same class, never a dodge,
    /// so the blow passes. A fighter without one is dodge-capable only when the roll against <paramref name="dodgeChance"/> succeeds, and then picks among the dodge techniques. The pick is
    /// weighted on <c>chance</c>. -1 when there is no reaction.
    /// </summary>
    public static int ChooseReaction(IReadOnlyList<CombatTechnique> all, WeaponKinds weaponKind, int animalCategory, bool prone, bool canBlock, int blowDirection,
        float blockChancePercent, float dodgeChancePercent, float chanceRoll, float pickRoll)
    {
        Func<CombatTechnique, bool> pool;
        if (canBlock)
        {
            bool blocks = chanceRoll * 100 < blockChancePercent;
            pool = blocks
                ? t => t.IsBlock && t.AttackDirection1 == blowDirection
                : t => t.IsBlock && !t.IsDodge && t.AttackDirection1 != blowDirection && SameClass(t.AttackDirection1, blowDirection);
        }
        else
        {
            if (!(chanceRoll * 100 < dodgeChancePercent)) return -1;
            pool = t => t.IsDodge;
        }
        float W(CombatTechnique t) =>
            t.Disabled || t.IsStumbleDodge || !(t.IsBlock || t.IsDodge) || !FitsKind(t, weaponKind) || t.Animal != animalCategory || t.IsProne != prone || !pool(t) ? 0 : MathF.Max(t.Chance, 0);
        float total = 0;
        for (int i = 0; i < all.Count; i++) total += W(all[i]);
        if (total <= 0) return -1;
        return Pick(all, pickRoll * total, W);
    }

    static int Pick(IReadOnlyList<CombatTechnique> all, float target, Func<CombatTechnique, float> weight)
    {
        int last = -1;
        for (int i = 0; i < all.Count; i++)
        {
            float w = weight(all[i]);
            if (w <= 0) continue;
            last = i;
            if (target < w) return i;
            target -= w;
        }
        return last;
    }
}
