namespace Meitou.Simulation.Combat;

/// <summary>
/// One character's combat state, small enough to copy by block: the target, the attack in progress, the reaction in progress, timers and counters.
/// The <see cref="CombatSystem"/> keeps two arrays of these (the last tick's, which every phase reads, and the one being computed, where a phase
/// writes only its own characters), like <see cref="CharacterTable"/>. Times are simulation ticks; progress is (now - start) / ticks.
/// </summary>
public struct CombatSlot
{
    /// <summary>
    /// The generation of the character the slot describes (a reused slot starts clean); -1 before the first sync.
    /// </summary>
    public int Generation;

    /// <summary>
    /// The target of the fight (slot and generation), -1 for none.
    /// </summary>
    public int TargetSlot, TargetGeneration;

    /// <summary>
    /// Whom the fight is about (slot and generation, -1: none): the fight task's own target (<c>Task_MeleeAttack</c>'s, the order's or the goal's),
    /// set at the fight's start and kept while the fighter turns on others. Losing it as the target ends the fight at once; a fight without a target
    /// lasts until nothing is left (docs/game/combat.md "Leaving a fight").
    /// </summary>
    public int TaskSlot, TaskGeneration;

    /// <summary>
    /// The attack technique (index into the system's list), -1 when not attacking.
    /// </summary>
    public int AttackTech;

    /// <summary>
    /// The tick the attack started, its length in ticks, and the tick after which a new action may start.
    /// </summary>
    public int AttackStart, AttackTicks, AttackEnd;

    /// <summary>
    /// The blow (1 or 2) that arrives next, 0 when all have; the attack number (raised per attack, part of the roll keys).
    /// </summary>
    public int NextBlow, AttackSeq;

    /// <summary>
    /// The tick a hit, knock-out or kill took the attack away (-1: never): a blow thrown on that same tick before it never lands. Derived, not
    /// hashed.
    /// </summary>
    public int CancelTick;

    /// <summary>
    /// Where the attack started (the original's +0x15c): the attack zone reaches the technique's attack distance less the way moved since.
    /// </summary>
    public System.Numerics.Vector3 AttackFrom;

    /// <summary>
    /// The attacker's rating at the start of the attack (melee attack x injury multiplier + weapon mod), what defenders compare their defence with.
    /// </summary>
    public float AttackSkill;

    /// <summary>
    /// The reaction technique (index), -1 when not reacting; its start tick and length in ticks.
    /// </summary>
    public int ReactTech, ReactStart, ReactTicks;

    /// <summary>
    /// The swing the defence follows (attacker slot, attack number, blow; the original's +0x1a8 and +0x1d0): a change of it makes the defence update
    /// look again.
    /// </summary>
    public int ReactedSlot, ReactedSeq, ReactedBlow;

    /// <summary>
    /// The tick the defence update looks at the followed blow's side again even when the swing has not changed (the original's 1 s timer +0x274).
    /// </summary>
    public int ReactCheckTick;

    /// <summary>
    /// The earliest tick a new attack may start, and the tick until which a hit reaction holds the character helpless.
    /// </summary>
    public int ReadyTick, StunUntil;

    /// <summary>
    /// Until when the post-swing pause holds it (the original's +0x144 wait in state 6, <c>FUN_14060d6a0</c>): it starts no attack and no defence,
    /// whoever swings at it, and only a hit reaction ends it. Set only when the swing ran to its end; a swing stopped at a stop frame decides again
    /// at once.
    /// </summary>
    public int PauseUntil;

    /// <summary>
    /// Knocked out or dead (a copy of <c>MedicalState.Incapacitated</c> that other characters can read).
    /// </summary>
    public bool Down;

    /// <summary>
    /// Crawling from a leg down (posture 2) the last tick: its end is a get-up (docs/game/ragdolls.md "Getting up"). Derived, not hashed.
    /// </summary>
    public bool Crawl;

    /// <summary>
    /// Counters: blows thrown, hits landed, blows blocked or dodged by the target, hits taken, blocks, dodges made.
    /// </summary>
    public int Thrown, Landed, Parried, Evaded, Taken, Blocks, Dodges;

    /// <summary>
    /// The last hit taken, for the hit reaction clip: its tick (-1: none yet), the body part (index into the medical state's parts), whether it was
    /// heavy (above the stumble threshold) and came from behind.
    /// </summary>
    public int HitTick, HitPart;

    /// <summary>
    /// The slot of the last character that landed a blow on it (-1: none yet): it is aware of that one (<see cref="CombatSystem.Alerts"/>).
    /// </summary>
    public int HitFrom;
    /// <summary>
    /// Whether the last hit was heavy (above the stumble threshold) and whether it came from behind.
    /// </summary>
    public bool HitHeavy, HitBehind;

    /// <summary>
    /// The hit reaction the last interrupting hit started (an animation library index, -1: none) and its tick; it holds the character helpless until
    /// <see cref="StunUntil"/>.
    /// </summary>
    public int StumbleClip, StumbleStart;

    /// <summary>
    /// The first tick a stumbling character may roll to break out (the original's +0x124 timer: 1.2 s from the stumble's start, again 1.2 s after a
    /// failed roll; <see cref="CombatSystem"/> rolls on the first tick a swing is in the window once it is due).
    /// </summary>
    public int BreakOutTick;

    /// <summary>
    /// How the last blow of the character's own attack ended (<see cref="BlowOutcome"/>), which sets the pause after the attack.
    /// </summary>
    public BlowOutcome LastOutcome;

    /// <summary>
    /// The fighter it lost the initiative roll to (slot, -1: none) and until when: that one may swing at it at once, even in its pause.
    /// </summary>
    public int YieldTo, YieldUntil;

    /// <summary>
    /// The tick before which it starts no defence (a failed defence roll, 1.2 s; docs/game/combat.md "Pacing").
    /// </summary>
    public int DefendReadyTick;

    /// <summary>
    /// The longest reach of its techniques at the last choice (<see cref="TechniqueChooser.ChooseAttack"/>, the original's CharStats +0x150; 0 before
    /// the first), which sets how close it walks in; its last technique (-1: none, or a defence since), which weighs less in the next choice.
    /// </summary>
    public float Reach;
    /// <summary>
    /// Its last technique (-1: none, or a defence since): it weighs less in the next choice.
    /// </summary>
    public int LastTech;

    /// <summary>
    /// One of the (at most five) fighters its target lets close in (the target's CombatClass +0x1f8 list, docs/game/combat.md "Attack slots and
    /// circling"); the rest circle.
    /// </summary>
    public bool Engaged;

    /// <summary>
    /// The engaged list's generation: raised when the character's own fight ends, which empties the list (<c>FUN_1406659b0</c> mode 2).
    /// <see cref="JoinedEpoch"/> is the target's <see cref="ListEpoch"/> when this fighter joined its list; it is out of it
    /// (<see cref="CombatSystem.Listed"/>) once the target's has moved on.
    /// </summary>
    public int ListEpoch, JoinedEpoch;

    /// <summary>
    /// In state 5 (<c>CircleStateAI</c>): engaged, its last ask failed while nothing was coming at it; its asks then need 1.2 x the body room
    /// (<see cref="CombatSystem.BodyRoom"/>, docs/game/combat.md "Attack slots and circling").
    /// </summary>
    public bool CircleState;

    /// <summary>
    /// The target is the player's pick (<see cref="AttackOrder.Picked"/>): it does not turn on a fighter that yields to it.
    /// </summary>
    public bool Picked;

    /// <summary>
    /// The tick of its next target choice (<see cref="CombatSystem.PickSeconds"/>); 0: at once.
    /// </summary>
    public int PickTick;

    /// <summary>
    /// The Block order's timer while engaged (the tick it runs out, 0: not running) and the tick it last ran out: swings at the character started
    /// before that hold no attack slot any more (<c>CircleStateAI</c> FUN_1402b3180 → FUN_1406658c0, docs/game/combat.md "Attack slots and
    /// circling").
    /// </summary>
    public int BlockTimerTick, SlotsCleared;

    // The ranged controller (docs/simulation.md "Combat", ranged; the original keeps it at Character +0x2f0). All zero for a character that never
    // used a crossbow.
    /// <summary>
    /// Fighting with the crossbow rather than the melee weapon; the controller's state (<see cref="RangedStates"/>).
    /// </summary>
    public bool Ranged;
    /// <summary>
    /// The ranged controller's state (<see cref="RangedStates"/>).
    /// </summary>
    public byte RangedState;

    /// <summary>
    /// Out of ammunition with an empty crossbow (no reload can start).
    /// </summary>
    public bool Dry;

    /// <summary>
    /// Shots loaded; shots fired (part of the roll keys).
    /// </summary>
    public int Loaded, Shots;

    /// <summary>
    /// Seconds of the reload left and its full length (0: not reloading); the steady-aim timer; the time the line of fire has been clear while
    /// closing in.
    /// </summary>
    public float ReloadLeft, ReloadTime, Steady, ClearTime;

    /// <summary>
    /// The tick of the last shot (0 with <see cref="Shots"/> 0).
    /// </summary>
    public int ShotTick;

    /// <summary>
    /// The state of a character entering the fight table: generation <paramref name="generation"/>, no target, no technique, no hit (every slot and
    /// tick field -1 where -1 means none).
    /// </summary>
    public static CombatSlot Fresh(int generation) => new()
    {
        Generation = generation,
        TargetSlot = -1,
        TargetGeneration = 0,
        TaskSlot = -1,
        AttackTech = -1,
        CancelTick = -1,
        ReactTech = -1,
        ReactedSlot = -1,
        ReactedSeq = -1,
        HitTick = -1,
        HitPart = -1,
        HitFrom = -1,
        StumbleClip = -1,
        YieldTo = -1,
        LastTech = -1,
    };

    /// <summary>
    /// Whether it has a target.
    /// </summary>
    public readonly bool Fighting => TargetSlot >= 0;

    /// <summary>
    /// Adds the slot's hashed fields to <paramref name="h"/>.
    /// </summary>
    internal readonly void Hash(ref StateHasher h)
    {
        h.Add(Generation);
        h.Add(TargetSlot);
        h.Add(TargetGeneration);
        if (TaskSlot >= 0)
        {
            h.Add(TaskSlot);
            h.Add(TaskGeneration);
        }   // only during a fight, so worlds without fights hash as before
        h.Add(AttackTech);
        h.Add(AttackStart);
        h.Add(AttackTicks);
        h.Add(AttackEnd);
        h.Add(NextBlow);
        h.Add(AttackSeq);
        h.Add(AttackFrom.X);
        h.Add(AttackFrom.Y);
        h.Add(AttackFrom.Z);
        h.Add(AttackSkill);
        h.Add(ReactTech);
        h.Add(ReactStart);
        h.Add(ReactTicks);
        h.Add(ReactedSlot);
        h.Add(ReactedSeq);
        h.Add(ReactedBlow);
        h.Add(ReactCheckTick);
        h.Add(ReadyTick);
        h.Add(StunUntil);
        h.Add(PauseUntil);
        h.Add(Down);
        h.Add(Thrown);
        h.Add(Landed);
        h.Add(Parried);
        h.Add(Evaded);
        h.Add(Taken);
        h.Add(Blocks);
        h.Add(Dodges);
        h.Add(HitTick);
        h.Add(HitPart);
        h.Add(HitFrom);
        h.Add(HitHeavy);
        h.Add(HitBehind);
        h.Add(StumbleClip);
        h.Add(StumbleStart);
        h.Add(BreakOutTick);
        h.Add((int)LastOutcome);
        h.Add(YieldTo);
        h.Add(YieldUntil);
        h.Add(DefendReadyTick);
        h.Add(Reach);
        h.Add(LastTech);
        h.Add(Engaged);
        if (ListEpoch != 0 || JoinedEpoch != 0)
        {
            h.Add(ListEpoch);
            h.Add(JoinedEpoch);
        }   // only once a list was emptied, so worlds without it hash as before
        if (CircleState) h.Add(0x43697263);   // only when set, so worlds without crowded fights hash as before
        if (Picked) h.Add(true);   // only when set, so worlds without player picks hash as before
        h.Add(PickTick);
        h.Add(BlockTimerTick);
        h.Add(SlotsCleared);

        // The ranged controller goes in only once it has been used, so worlds without crossbows hash as they did before it existed.
        if (!RangedUsed) return;
        h.Add(Ranged);
        h.Add(RangedState);
        h.Add(Dry);
        h.Add(Loaded);
        h.Add(Shots);
        h.Add(ReloadLeft);
        h.Add(ReloadTime);
        h.Add(Steady);
        h.Add(ClearTime);
        h.Add(ShotTick);
    }

    /// <summary>
    /// Any ranged field is set.
    /// </summary>
    public readonly bool RangedUsed =>
        Ranged || RangedState != 0 || Dry || Loaded != 0 || Shots != 0 || ReloadLeft != 0 || ReloadTime != 0
        || Steady != 0 || ClearTime != 0 || ShotTick != 0;
}

/// <summary>
/// The states of the ranged controller (<c>FUN_140438c70</c>, controller <c>+0</c>).
/// </summary>
public enum RangedStates : byte
{
    /// <summary>
    /// Loaded, target in range: holding the aim until the steady timer runs out, then firing when the line is clear.
    /// </summary>
    Aiming = 0,
    /// <summary>
    /// Out of range or the line of fire blocked: walking towards the target.
    /// </summary>
    Closing = 1,
    /// <summary>
    /// Empty: reloading (or unable to, with no ammunition).
    /// </summary>
    Reloading = 2,
    /// <summary>
    /// No target.
    /// </summary>
    NoTarget = 3,
}