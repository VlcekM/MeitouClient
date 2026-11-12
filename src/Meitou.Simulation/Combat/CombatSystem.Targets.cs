using System.Numerics;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Target choice: whom a fighter attacks and when it chooses again, the best attacker of a character for one joining its fight, and the threat a
/// fight poses.
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// How long a fighter keeps its target before it chooses again (CombatClassAI +0x2f0, docs/game/combat.md "Attack slots and circling"): 1 s,
    /// 0.3 s on Hold.
    /// </summary>
    public const float PickSeconds = 1, HoldPickSeconds = 0.3f;

    /// <summary>
    /// The score a candidate of the lists must beat (<c>chooseAttackTarget</c>'s floor <c>DAT_1416abef4</c> = -100, <b>Verified</b>).
    /// </summary>
    const float PickFloor = -100;

    /// <summary>
    /// The protect finders cap their value at this (<c>DAT_1416852f0</c>, <b>Verified</b>).
    /// </summary>
    public const float ProtectCap = 1.2f;

    /// <summary>
    /// How far apart in height two characters inside one building may stand and still be on the same floor (engine reading of the floor test).
    /// </summary>
    public const float SameFloorHeight = 20;

    /// <summary>
    /// The height of the eyes above the feet for the alert's line test (engine choice, near <see cref="RangedTuning.BarrelHeight"/>).
    /// </summary>
    const float EyeHeight = 15;

    /// <summary>
    /// The spacing of the points the steering's line test stands on (engine choice: under a footprint, so a wall's gap of a body width is
    /// not skipped).
    /// </summary>
    const float SteerLineStep = 2;

    /// <summary>
    /// The radius of the circle the steering's line test sweeps against the walkable surface's walls (engine choice: near a point, so the line
    /// itself is tested; a bar line a step thin is crossed between two of the <see cref="SteerLineStep"/> points, #439).
    /// </summary>
    const float SteerLineRadius = 0.1f;

    /// <summary>
    /// How far inside its polygon's edge the line test puts a fighter that stands off it (<see cref="IWalkability.SurfacePoint"/>): clear of the
    /// swept circle, so the edge it stands by does not stop the sweep at any angle.
    /// </summary>
    const float SteerLineInset = 2 * SteerLineRadius;

    /// <summary>
    /// The chooser's side of a target choice and the best candidate so far.
    /// </summary>
    struct Choice
    {
        /// <summary>
        /// The chooser's slot and its current target's (-1: none).
        /// </summary>
        public int Self, Current;
        /// <summary>
        /// The chooser.
        /// </summary>
        public CharacterCold Cold;
        /// <summary>
        /// Whether the chooser is the player's, holds, is down (prone) and is an animal.
        /// </summary>
        public bool Player, Hold, Prone, Animal;
        /// <summary>
        /// Where a holding chooser holds.
        /// </summary>
        public Vector3 HoldPoint;

        /// <summary>
        /// The AI task's target whose attackers the chooser protects (a bodyguard's), -1: none.
        /// </summary>
        public int Protectee;
        /// <summary>
        /// The best candidate so far (-1: none).
        /// </summary>
        public int Best;
        /// <summary>
        /// The best candidate's score.
        /// </summary>
        public float BestScore;

        /// <summary>
        /// Fights started this tick by target, counted as attackers (the AI's serial step, <see cref="BestAttackerOf"/>; null: none).
        /// </summary>
        public IReadOnlyDictionary<int, int>? Pending;
    }

    #region Target choice

    /// <summary>
    /// The ticks a fighter keeps its target: <see cref="HoldPickSeconds"/> on Hold, else <see cref="PickSeconds"/>, at least 1.
    /// </summary>
    static int PickTicks(World world, CharacterCold cold)
    {
        float seconds = Orders.Holding(cold) ? HoldPickSeconds : PickSeconds;
        return Math.Max(1, (int)MathF.Round(seconds / world.TickSeconds));
    }

    /// <summary>
    /// A fighter's target choice (<c>CombatClassAI::chooseAttackTarget</c> <c>FUN_140666ee0</c>, <b>Verified</b>; docs/game/combat.md "Target
    /// selection"), scored by <see cref="TargetScore"/>: the best over its lists that scores above <see cref="PickFloor"/>. The lists: the attackers
    /// of its AI task's target, which the original holds only for the goals 45 Bodyguard, 17, 87, 152 and 124 (<c>FUN_14050ca70</c>; Meitou: a
    /// bodyguard's protectee), the chooser's own attackers and its threats (<see cref="SenseBlock.Threats"/>); a candidate must be sensed by the
    /// chooser or the protectee (<see cref="CharacterFacts.Knows"/>) and not crippled. When none qualifies, the sensed-enemy finder
    /// (<c>FUN_14059fa70</c>, gated on <c>numEnemies</c>; the aggro mode it also tests is 0 by construction): the entries tagged enemy, not down or
    /// imprisoned, seen or heard at the last look. The current target is scored as any other (its stubbornness keeps it). Reads the last tick's state
    /// and the senses of the last look; -1 when none qualifies.
    /// </summary>
    int ChooseTarget(World world, int i, in CombatSlot s, CharacterCold cold)
    {
        var table = world.Characters;
        var prev = table.Previous;
        bool hold = Orders.Holding(cold) && !s.Ranged;
        int protectee = cold.Guarding && table.TryResolveNext(cold.FollowOrder, out int p) && p != i ? p : -1;
        var q = new Choice
        {
            Self = i,
            Current = s.TargetSlot,
            Cold = cold,
            Player = cold.IsPlayer,
            Hold = hold,
            Prone = cold.Medical!.Prone,
            Animal = cold.Fighter!.IsAnimal,
            HoldPoint = hold ? Orders.HoldPointOf(cold, prev[i].Position) : default,
            Protectee = protectee,
            Best = -1,
            BestScore = PickFloor,
        };

        bool Known(int a)
        {
            var id = new CharacterId(a, table.Previous[a].Generation);
            return CharacterFacts.Knows(world, i, id) || protectee >= 0 && CharacterFacts.Knows(world, protectee, id);
        }

        if (protectee >= 0)
        {
            int pg = prev[protectee].Generation;
            foreach (var (_, _, _, a) in _index.AttackersOf(protectee))
                if (_read[a].TargetGeneration == pg && !Crippled(table, a) && Known(a)) Consider(world, ref q, a, false);
        }

        int generation = prev[i].Generation;
        foreach (var (_, _, _, a) in _index.AttackersOf(i))
            if (_read[a].TargetGeneration == generation && !Crippled(table, a) && Known(a)) Consider(world, ref q, a, false);

        var senses = CharacterFacts.Senses(world, i);
        if (senses is not null)
            foreach (var t in senses.Threats)
                if ((uint)t.Slot < (uint)prev.Length && prev[t.Slot].Generation == t.Generation && !Crippled(table, t.Slot))
                    Consider(world, ref q, t.Slot, false);

        if (q.Best >= 0 || senses is not { Enemies: > 0 }) return q.Best;

        q.BestScore = float.NegativeInfinity;
        foreach (var e in senses.Entries)
            if (e.Now && (e.Tags & (SenseTag.Enemy | SenseTag.Ko | SenseTag.Imprisoned)) == SenseTag.Enemy
                && (uint)e.Target < (uint)prev.Length && prev[e.Target].Generation == e.Generation)
                Consider(world, ref q, e.Target, false);

        return q.Best;
    }

    /// <summary>
    /// The chooser's lists leave out a crippled candidate (Character vslot 0x360, the crippled flag: <see cref="Bodies.MedicalState.Crawling"/>).
    /// </summary>
    static bool Crippled(CharacterTable table, int c) => table.Cold(c)?.Medical is { Crawling: true };

    /// <summary>
    /// Whether a fighter without a target leaves its fight (<c>Task_MeleeAttack</c> update <c>FUN_14034ca40</c>, <b>Verified</b>; docs/game/combat.md
    /// "Leaving a fight"): its senses count no enemy (<see cref="SenseBlock.Enemies"/>) and no threat to its allies
    /// (<see cref="SenseBlock.ThreatToAllies"/>), nobody fights it (<see cref="AttackerCount"/>, the engaged list), and its task target
    /// (<see cref="CombatSlot.TaskSlot"/>) is gone or down (dead or knocked out; carried too, engine reading). Engine choice: a task target its
    /// senses hold no entry of counts as gone, so a fighter does not wait for ever on a live target it lost (the entry decays 30 to 120 sense units
    /// after it was last sensed); a world without senses never holds it. Reads the last tick's fights and the senses of the last look.
    /// </summary>
    bool NothingLeft(World world, int i, in CombatSlot s)
    {
        if (AttackerCount(world, i) > 0) return false;

        var senses = CharacterFacts.Senses(world, i);
        if (senses is { Enemies: > 0 } or { ThreatToAllies: > 0 }) return false;

        var table = world.Characters;
        var prev = table.Previous;
        int t = s.TaskSlot;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive || prev[t].Generation != s.TaskGeneration || _read[t].Down
            || table.Cold(t) is not { Medical.Incapacitated: false, CarriedBy.IsNone: true })
            return true;

        return senses is null || !senses.Knows(new CharacterId(t, s.TaskGeneration));
    }

    /// <summary>
    /// <c>AI::findAttackerOf</c> (<c>FUN_14099c3b0</c>, <b>Verified</b>) for a character joining <paramref name="subject"/>'s fight: of those
    /// fighting the subject, and the subject's own target while it fights, the best by <see cref="TargetScore"/> for <paramref name="looker"/>; for a
    /// looker other than the subject only those within <see cref="RetaliationSystem.ProtectReach"/> of the subject, and never one the looker counts
    /// as an ally (the subject's own target also only when either side is the other's enemy). -1 when none. The finder's sense tests and its fallback
    /// to the subject's threats are left out (an attacker is put in the senses of whom it attacks). <paramref name="pending"/>: fights started this
    /// tick by target, counted as attackers of theirs (the AI's serial step).
    /// </summary>
    public int BestAttackerOf(World world, int subject, int looker, IReadOnlyDictionary<int, int>? pending = null)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if ((uint)subject >= (uint)prev.Length || table.Cold(looker) is not { Fighter: { } fighter, Medical: { } medical } cold) return -1;

        var q = new Choice
        {
            Self = looker,
            Current = -1,
            Cold = cold,
            Player = cold.IsPlayer,
            Prone = medical.Prone,
            Animal = fighter.IsAnimal,
            Protectee = -1,
            Best = -1,
            BestScore = float.NegativeInfinity,
            Pending = pending,
        };

        bool Joinable(int c)
        {
            if (table.Cold(c) is not { } tc) return false;

            if (looker != subject)
            {
                var gap = table.Previous[c].Position - table.Previous[subject].Position;
                if (gap.X * gap.X + gap.Z * gap.Z > RetaliationSystem.ProtectReach * RetaliationSystem.ProtectReach) return false;
            }

            return Relations is not { } r || !(r.Contains(tc.Faction) && r.Contains(cold.Faction) && r.IsAlly(cold.Faction, tc.Faction));
        }

        int generation = prev[subject].Generation;
        foreach (var (_, _, _, a) in _index.AttackersOf(subject))
            if (_read[a].TargetGeneration == generation && Joinable(a)) Consider(world, ref q, a, looker != subject);

        int own = _read[subject].TargetSlot;
        if (own >= 0 && own != looker && (uint)own < (uint)prev.Length && prev[own].Generation == _read[subject].TargetGeneration && Joinable(own)
            && table.Cold(own) is { } oc && Relations is { } rel
            && (AggroSystem.IsEnemy(rel, cold.Faction, oc.Faction) || AggroSystem.IsEnemy(rel, oc.Faction, cold.Faction)))
            Consider(world, ref q, own, looker != subject);

        return q.Best;
    }

    /// <summary>
    /// The crippled flag of the target score (<c>FUN_14059e300</c> :249-252): vslot 0x360 (<c>isCrippled</c>: crawling, or unable to attack,
    /// stand-in) or the maimed test <c>FUN_140644170</c> (<see cref="Bodies.MedicalState.Maimed"/>).
    /// </summary>
    public static bool IsCrippled(CharacterCold c) => c.Medical is { Crawling: true } or { Maimed: true } || c.Fighter is not { CanAttack: true };

    /// <summary>
    /// Scores candidate <paramref name="c"/> for the chooser (<see cref="TargetScore"/>) and keeps it when it beats the best; one down, caged,
    /// carried, of the chooser's faction or squad (but the current target), off its hold post or found unreachable lately is left out.
    /// </summary>
    void Consider(World world, ref Choice q, int c, bool protect)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int i = q.Self;
        if (c == i || (uint)c >= (uint)prev.Length || !prev[c].Alive || _read[c].Down) return;
        if (table.Cold(c) is not { Medical.Incapacitated: false, IsImprisoned: false, CarriedBy.IsNone: true } tc) return;

        // A carried one is dropped by Act's target test. One it found no path to lately is left out (AITaskSytem::isPathImpossible).
        if (PursuitSystem.PathImpossible(q.Cold, new CharacterId(c, prev[c].Generation), world.Tick)) return;

        // The current target is whoever the order named, of any faction.
        if (c != q.Current && (tc.Faction == q.Cold.Faction || q.Cold.SquadId >= 0 && tc.SquadId == q.Cold.SquadId)) return;

        ref readonly var o = ref _read[c];
        bool downed = tc.Medical is { Crawling: true } || o.AttackTech >= 0;
        if (q.Hold && !Orders.HoldPost(prev[i].Position, q.HoldPoint, prev[c].Position, prev[c].Velocity, prev[c].Yaw, downed, o.TargetSlot >= 0))
            return;

        int attackers = AttackerCount(world, c) + (q.Pending is { } pending ? pending.GetValueOrDefault(c) : 0);
        float dx = prev[c].Position.X - prev[i].Position.X;
        float dz = prev[c].Position.Z - prev[i].Position.Z;
        var candidate = new TargetCandidate
        {
            Current = c == q.Current,
            Staggered = o.StumbleClip >= 0 && world.Tick < o.StunUntil,
            Attackers = attackers,
            ChooserAttacks = c == q.Current,
            Taunt = Orders.Has(tc, CharacterOrders.Taunt),
            Passive = Orders.Has(tc, CharacterOrders.Passive),

            // +10 for one on the AI task's target (the protectee) that does not fight it back, with under two attackers (FUN_14059e300).
            Protect = (protect || q.Protectee >= 0 && o.TargetSlot == q.Protectee && _read[q.Protectee].TargetSlot != c) && attackers < 2,
            Distance = MathF.Sqrt(dx * dx + dz * dz),
            Fighting = o.TargetSlot >= 0,
            Gunner = tc.Gunning is not null,
            Crippled = IsCrippled(tc),
            Peaceful = o.TargetSlot < 0 && Relations is { } relations && !AggroSystem.IsEnemy(relations, tc.Faction, q.Cold.Faction),
            GettingUp = CharacterFacts.GettingUp(tc, world.Tick),
        };
        if (TargetScore.Score(candidate, q.Player, q.Hold, q.Prone, q.Animal, q.Cold.Manoeuvre) is { } score && score > q.BestScore)
        {
            q.Best = c;
            q.BestScore = score;
        }
    }

    #endregion

    #region Threat and fight counts

    /// <summary>
    /// The fight threat of a character (<c>FUN_14099a190</c> on its <c>CombatClassAI</c>, <b>Verified</b>; docs/game/ai-tasks.md "Combat goals"), the
    /// value the protect finders give an ally or squad mate (143, 15, 288): 0 when it is down; 0.01 when exactly one attacker fights it and someone
    /// else already fights that attacker; else 0.25 n - 0.2 for n attackers (at most 5; none: 0), raised to 0.1 x the threat sum of its senses
    /// (<see cref="SenseBlock.ThreatToMe"/>, <paramref name="senses"/>, its own); and 0.01 at least while it is in a fight. Engine reading: n is
    /// every standing fighter whose target it is (<see cref="AttackerCount"/>; the original counts its engaged list of askers, at most 5), and
    /// "someone else fights the attacker" is that attacker's own count above 1 (the original adds its refused askers, an approximation); Meitou's
    /// threat sum adds 1 per threat where the original adds its aggression. As of the last tick; any thread after Schedule.
    /// </summary>
    public float FightThreat(World world, int slot, SenseBlock? senses)
    {
        var table = world.Characters;
        var prev = table.Previous;
        if ((uint)slot >= (uint)table.HighWater || !prev[slot].Alive || table.Cold(slot) is not { } c) return 0;
        if (CharacterFacts.Down(c) || c.Medical is { Dead: true }) return 0;

        int n = 0, only = -1, generation = prev[slot].Generation;
        foreach (var (_, _, _, a) in _index.AttackersOf(slot))
            if (_read[a].TargetGeneration == generation && !_read[a].Down)
            {
                n++;
                only = a;
            }

        if (n == 1 && AttackerCount(world, only) > 1) return 0.01f;

        float v = n > 0 ? 0.25f * Math.Min(n, 5) - 0.2f : 0;
        if (senses is not null) v = MathF.Max(v, 0.1f * senses.ThreatToMe);

        return v <= 0 && c.InCombat ? 0.01f : v;
    }

    /// <summary>
    /// How many fight the character in <paramref name="slot"/> (standing fighters whose target it is), as of the last tick; any thread
    /// after Schedule.
    /// </summary>
    public int AttackerCount(World world, int slot)
    {
        int n = 0, generation = world.Characters.Previous[slot].Generation;
        foreach (var (_, _, _, a) in _index.AttackersOf(slot))
            if (_read[a].TargetGeneration == generation && !_read[a].Down) n++;

        return n;
    }

    #endregion

    #region Alerts and steering

    /// <summary>
    /// Whether attacker <paramref name="a"/> taking <paramref name="t"/> as its target alerts it (<c>AI::underMeleeAttack</c> from the targeting
    /// path, docs/game/combat.md "Fight lifecycle and orders"): always, unless the target is the player's, or the attacker is and the target is not
    /// aware of it (fighting it, or hit by it); then only when both stand in the same place (<see cref="SightMap.PlaceOf"/>), on the same floor
    /// (<see cref="SameFloorHeight"/>) and with a clear line between their eyes (terrain and buildings, <see cref="SolidHits"/>). A blow landed by
    /// the attacker alerts in any case (the hit path). Reads the last tick's state; any thread after Schedule.
    /// </summary>
    public bool Alerts(World world, int a, int t)
    {
        var table = world.Characters;
        if (table.Cold(a) is not { } ac || table.Cold(t) is not { } tc) return false;

        ref readonly var o = ref _read[t];
        if (o.HitFrom == a) return true;
        if (!tc.IsPlayer && !(ac.IsPlayer && o.TargetSlot != a)) return true;

        var prev = table.Previous;
        if (world.Sight.PlaceOf(a) != world.Sight.PlaceOf(t)) return false;

        var from = prev[a].Position + new Vector3(0, EyeHeight, 0);
        var seg = prev[t].Position + new Vector3(0, EyeHeight, 0) - from;
        if (world.Sight.Inside(t) && MathF.Abs(seg.Y) > SameFloorHeight) return false;

        return SolidHits.Hit(world.Walkability, Buildings, from, seg, RangedTuning.TerrainStep, out _) < 0;
    }

    /// <summary>
    /// Whether <paramref name="c"/> counts as an enemy of <paramref name="me"/> for the steering's repulsion (its <c>isEnemy</c>, engine reading): of
    /// a hostile faction (another faction when there is no relation table), or fighting <paramref name="me"/>, or fought by it. Last tick's state;
    /// any thread after Schedule.
    /// </summary>
    public bool IsEnemyOf(World world, int c, CharacterCold oc, int me, int myFaction)
    {
        var prev = world.Characters.Previous;
        if (_read[c].TargetSlot == me && _read[c].TargetGeneration == prev[me].Generation) return true;
        if (_read[me].TargetSlot == c && _read[me].TargetGeneration == prev[c].Generation) return true;

        return CharacterFacts.RegardsEnemy(Relations, oc.Faction, myFaction);
    }

    /// <summary>
    /// Whether fighter <paramref name="a"/> may steer at <paramref name="t"/> rather than walk a path (<c>CombatClassAI::vfunc_9</c>'s chase test,
    /// docs/game/combat.md "Combat stance"): the same place (<see cref="SightMap.PlaceOf"/>), within <see cref="SameFloorHeight"/> in height, and
    /// every <see cref="SteerLineStep"/> of the straight line between them walkable from the last, and no wall of the walkable surface across it
    /// (<see cref="IWalkability.Slide"/>; engine stand-in for the navmesh line query <c>FUN_1403a56e0</c>). The walls are tested between where the
    /// two stand on the surface (<see cref="IWalkability.SurfacePoint"/>): for one standing off the surface's edge that edge was a wall across every
    /// line to it, so nobody steered or swung at it (#559). Last tick's places; any thread after Sight's update.
    /// </summary>
    public bool SteersAt(World world, int a, int t)
    {
        if (world.Sight.PlaceOf(a) != world.Sight.PlaceOf(t)) return false;

        var prev = world.Characters.Previous;
        Vector3 from = prev[a].Position;
        Vector3 to = prev[t].Position;
        if (MathF.Abs(to.Y - from.Y) > SameFloorHeight) return false;

        var leg = new Vector2(to.X - from.X, to.Z - from.Z);
        float length = leg.Length(), y = from.Y;
        for (float d = SteerLineStep; d < length; d += SteerLineStep)
            if (!world.Walkability.TryStand(from.X + leg.X * (d / length), from.Z + leg.Y * (d / length), y, out y)) return false;

        var start = world.Walkability.SurfacePoint(from, SteerLineInset);
        var end = world.Walkability.SurfacePoint(to, SteerLineInset);
        var line = new Vector2(end.X - start.X, end.Z - start.Z);
        return world.Walkability.Slide(start, line, SteerLineRadius) == line;
    }

    /// <summary>
    /// Whether fighter <paramref name="cold"/> is on a path chase after <paramref name="t"/> (<see cref="SteerState.ChaseTarget"/>: its line to it was
    /// blocked, or the chase's timer still runs). The original's fight is then in state 11, whose update never reaches the attack start
    /// (<c>FUN_14060d6a0</c>): it neither stands in reach nor swings, so a target across bars or a wall is walked round to, or dropped (#439).
    /// </summary>
    public static bool PathChasing(CharacterCold cold, int t) => cold.Steer is { } steer && steer.ChaseTarget == t;

    /// <summary>
    /// Whether <paramref name="a"/> at <paramref name="from"/> and <paramref name="t"/> at <paramref name="to"/> stand on different floors (engine
    /// reading, #292): either is in or on a building (<see cref="SightMap.Outdoors"/>) and they are over <see cref="SameFloorHeight"/> apart in
    /// height. A fighter does not swing through a floor or stop its chase there; outdoors a ledge is reached by the 3D gap alone
    /// (<see cref="CombatTuning.Gap"/>). Last tick's places; any thread after Sight's update.
    /// </summary>
    public static bool OtherFloor(World world, int a, Vector3 from, int t, Vector3 to) =>
        (!world.Sight.Outdoors(a) || !world.Sight.Outdoors(t)) && MathF.Abs(to.Y - from.Y) > SameFloorHeight;

    /// <summary>
    /// Turns a fighter on a new target: it asks its target again for a place and weighs its techniques afresh. A crossbowman closing in on the old
    /// one has its closing timer set to <see cref="RangedTuning.NewTargetClear"/> (<c>FUN_1404371f0</c>), so the first closing ray to pass stops it.
    /// </summary>
    static void Retarget(ref CombatSlot s, int target, int generation, int now)
    {
        if (s.RangedState == (byte)RangedStates.Closing && (s.TargetSlot != target || s.TargetGeneration != generation))
            s.ClearTime = RangedTuning.NewTargetClear;

        s.TargetSlot = target;
        s.TargetGeneration = generation;
        s.Engaged = false;
        s.LastTech = -1;
        s.Picked = false;
    }

    #endregion
}
