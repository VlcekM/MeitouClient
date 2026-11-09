using System.Numerics;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Walks an attacker to its target through the path service (docs/simulation.md "Combat"). The combat system is run with <c>SelfApproach</c> off, so
/// a character with the <see cref="CharacterTask.Attack"/> task would stand still; this system, placed before the movement system, asks for a path to
/// the target, or to a point ahead of a fleeing target (a new one when the goal has moved more than <see cref="RepathDistance"/>, at most every
/// <see cref="RepathSeconds"/>) and drops the path once the target is in reach or a swing is on.
/// </summary>
public sealed class PursuitSystem(CombatSystem combat) : ITickSystem
{
    /// <summary>
    /// The goal moves this far before a new path is asked for.
    /// </summary>
    public const float RepathDistance = 12, RepathSeconds = 0.5f;

    /// <summary>
    /// How long a chase along a path lasts at least after a fighter met a blocked line (<b>Verified</b> literal 2.0, <c>FUN_14060c8b0</c>'s
    /// timer +0x14c).
    /// </summary>
    public const float PathChaseSeconds = 2;

    /// <summary>
    /// How long a target the fighter found no path to is left out of its target choice (<b>Verified</b> literal 20,
    /// <c>AITaskSytem::notifyPathImpossible</c>; read as seconds).
    /// </summary>
    public const float PathFailureSeconds = 20;

    /// <summary>
    /// Slots per job of the parallel schedule.
    /// </summary>
    const int SlotsPerJob = 32;

    /// <summary>
    /// The movement flags of a path held or asked for.
    /// </summary>
    const ushort PathFlags = (ushort)MoveFlags.AnyPath;

    #region Pursuit

    /// <summary>
    /// The pursuit's schedule (parallel by blocks of slots): each attacker writes only its own slot from its target's place and the last tick's
    /// fight states.
    /// </summary>
    public void Schedule(World world)
    {
        var table = world.Characters;
        int count = table.HighWater;
        combat.IndexAttackers(world);   // for FightersAt: this schedule runs before combat's

        // Each attacker writes only its own slot and reads its target's place and the combat state of the last tick, so the slots run in parallel in
        // any order. A far character pursues at its step, the step covering the ticks since its last (the far cadence, World.Steps).
        world.ParallelFor((count + SlotsPerJob - 1) / SlotsPerJob, job =>
        {
            for (int i = job * SlotsPerJob, end = Math.Min(count, i + SlotsPerJob); i < end; i++)
                if (world.Steps(i)) Pursue(world, table, i, world.StepSeconds(i));
        });
    }

    /// <summary>
    /// One character's pursuit step: off the attack task (or bashing a door) its path chase ends; with a living target it chases it
    /// (<see cref="Chase"/>).
    /// </summary>
    void Pursue(World world, CharacterTable table, int i, float dt)
    {
        var next = table.Next;
        ref var n = ref next[i];
        var self = table.Cold(i);
        if (self is not null) self.FaceSlot = -1;

        // a basher walks to its door (Locks.LockSystem)
        if (!n.Alive || n.Task != (byte)CharacterTask.Attack || self?.Bashing is not null)
        {
            // a path chase ends with the fight
            if (self?.Steer is { ChaseTarget: >= 0 } over) over.ChaseTarget = -1;
            return;
        }

        var st = combat.StateOf(new CharacterId(i, n.Generation));
        int t = st.TargetSlot;
        if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration) return;

        Chase(world, table, i, ref n, st, t, dt);
    }

    /// <summary>
    /// Fighter <paramref name="i"/> after its living target <paramref name="t"/>: runs or keeps to its footwork, faces the target, leads a fleeing
    /// one, is steered in its footwork, stands to fight in reach, mid-swing, staggered or holding, gives up a path that ends short, else asks a new
    /// path when the old one is stale.
    /// </summary>
    void Chase(World world, CharacterTable table, int i, ref CharacterHot n, in CombatSlot st, int t, float dt)
    {
        var next = table.Next;
        var d = next[t].Position - n.Position;
        var cold = table.Cold(i)!;
        var tc = table.Cold(t);

        // the longest gap a swing starts at (3D, CombatTuning.Gap); a path ending within it still brings it to a swing
        float endReach = st.Reach > 0 ? st.Reach : CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, false);
        NotePathFailure(world, i, cold, new CharacterId(t, next[t].Generation), endReach);
        var tv = next[t].Velocity;
        bool fleeing = UpdateRunning(cold, tc, d, tv, dt);

        // In its footwork it faces its target whichever way its path takes it, and side-steps (docs/animation.md "Side steps"; MovementSystem).
        if (!cold.CombatRunning) cold.FaceSlot = t;
        n.TaskTime -= dt;

        // Its swings are measured to where a target that is not fighting will be (CombatSystem.StartAttack's aim, AimLeadSeconds ahead), so standing
        // and the run past are too: stood within reach of the target itself, a runner's aim point would be out of reach and the chaser would stand
        // there idle while the runner got away.
        var aim = tc is { InCombat: false } ? next[t].Position + tv * CombatTuning.AimLeadSeconds : next[t].Position;
        var da = aim - n.Position;
        float aimGap = MathF.Sqrt(da.X * da.X + da.Z * da.Z) - CombatTuning.Footprint(cold) - CombatTuning.Footprint(tc);

        // After a fleeing target it aims ahead of it, and runs past it while behind (Lead).
        var goal = new Vector2(next[t].Position.X, next[t].Position.Z);
        float lead = Lead(world, i, cold, fleeing);
        var level = next[t].Position + tv * MathF.Min(lead, CombatTuning.AimLeadSeconds) - n.Position;
        bool behind = lead > 0 && tv.X * level.X + tv.Z * level.Z > 0;

        // A crossbowman walks only while closing in (out of range or the line of fire blocked); aiming and reloading it stands.
        bool shooting = st.Ranged && st.RangedState != (byte)RangedStates.Closing;
        bool holding = HoldsBack(cold, n.Position, next, t);

        // In its footwork with a clear line to the target, the fight steering moves it (docs/game/combat.md "Fight steering"): its state sets the
        // band, no paths.
        if (Steer(world, i, ref n, cold, st, t, holding)) return;

        float stand = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, cold.CombatRunning);
        bool inReach = !st.Ranged && !behind && aimGap <= stand && CombatTuning.Gap(n.Position, aim, cold, tc) <= endReach
            && !CombatSystem.OtherFloor(world, i, n.Position, t, next[t].Position); // a target on the floor below is walked to by the stairs (#292)
        if (st.Down || st.AttackTech >= 0 || st.ReactTech >= 0 || world.Tick < st.StunUntil || shooting || inReach || holding)
        {
            // In reach, mid-swing or staggered (the hit reaction carries it), or holding position: stand and fight.
            if ((n.Flags & PathFlags) != 0) DropPath(ref n, cold);

            return;
        }

        // A path that ends short of the target is no path to it (the walkability answers an unreachable goal with the way to the reachable point
        // nearest it, an engine choice): the way the original's chase reports it, the target goes into the path failures and the fighter stands to be
        // given another (FUN_14060d6a0 -> FUN_140796ed0, docs/game/combat.md "Target score").
        if (!behind && GaveUpShortPath(world, i, ref n, cold, tc, new CharacterId(t, next[t].Generation), endReach)) return;

        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || n.TaskTime > 0) return;

        // The point past the runner only now, at most once per RepathSeconds: the navmesh test is a point query per chaser.
        if (behind) goal = PassPoint(world, i, cold, tc, next[t].Position.Y, d, tv, lead, goal);

        bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > RepathDistance * RepathDistance;
        if (!stale) return;

        AskPath(ref n, cold, goal, next[t].Position.Y);
    }

    /// <summary>
    /// Whether the chaser runs (<see cref="CharacterCold.CombatRunning"/>): at the target out of the combat stance from RunGap, back to its footwork
    /// under FootworkGap, and after a target that keeps moving away (faster than FleeingSpeed, out of a fight) for FleeingSeconds at any gap.
    /// Returns whether the target counts as fleeing.
    /// </summary>
    static bool UpdateRunning(CharacterCold cold, CharacterCold? tc, Vector3 d, Vector3 tv, float dt)
    {
        float gap = MathF.Sqrt(d.X * d.X + d.Z * d.Z) - CombatTuning.Footprint(cold) - CombatTuning.Footprint(tc);
        bool away = tc is { InCombat: false } && tv.X * tv.X + tv.Z * tv.Z > CombatTuning.FleeingSpeed * CombatTuning.FleeingSpeed;
        cold.TargetFleeingTime = away ? cold.TargetFleeingTime + dt : 0;
        if (gap > CombatTuning.RunGap) cold.CombatRunning = true;
        else if (gap < CombatTuning.FootworkGap) cold.CombatRunning = false;

        bool fleeing = cold.TargetFleeingTime >= CombatTuning.FleeingSeconds;
        if (fleeing) cold.CombatRunning = true;

        return fleeing;
    }

    /// <summary>
    /// How far ahead of the target the chaser aims (seconds of the target's way, 0 unless it flees). After a fleeing target it aims ahead of it
    /// (<see cref="CombatTuning.LeadBase"/>), so a faster chaser runs past; the lead follows the target's velocity, so it swings round when the
    /// target turns. Engine rules: the lead and the run past hold only while the chaser is behind the nearer of the lead and aim points; once
    /// level with it it turns on the target and stops with the aim point within reach, so the runner comes into its swing. A path's goal is reached
    /// and stopped at, and a fighter's speed ramps up again slowly, so the goal is put RepathSeconds of the runner's way past the lead (the lead
    /// moves that far before the next path). Bodies are solid here (the original's chaser runs through its runner), so the goal is also put
    /// PassWidth beside the runner's track, on the chaser's side (by slot when dead behind), for the chaser to pass the runner rather than push it
    /// along (<see cref="PassPoint"/>).
    /// </summary>
    float Lead(World world, int i, CharacterCold cold, bool fleeing) =>
        fleeing && cold.Fighter is not null && cold.Stats is not null && cold.Medical is not null
            ? (1 - combat.AttackRate(world, i, cold)) * CombatTuning.LeadSlope + CombatTuning.LeadBase
            : 0;

    /// <summary>
    /// On Hold it walks only to a target within the post test's reach of the hold point (80, 140 when the target is in a fight and heads for it;
    /// Orders.HoldFar), else it stands (engine reading). True when it holds back from target <paramref name="t"/>.
    /// </summary>
    bool HoldsBack(CharacterCold cold, Vector3 here, Span<CharacterHot> next, int t)
    {
        if (!Orders.Holding(cold)) return false;

        bool targetFights = combat.StateOf(new CharacterId(t, next[t].Generation)).TargetSlot >= 0;
        float far = Orders.HoldFar(here, next[t].Position, next[t].Yaw, targetFights);

        return Vector3.DistanceSquared(Orders.HoldPointOf(cold, here), next[t].Position) > far * far;
    }

    /// <summary>
    /// Whether the path the chaser holds, its request answered, was asked for the target's present place and ends short of a swing
    /// (<see cref="EndsShort"/>).
    /// </summary>
    static bool PathEndsShort(in CharacterHot n, CharacterCold cold, CharacterCold? tc, float endReach) =>
        (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) == 0 && (n.Flags & (ushort)MoveFlags.HasPath) != 0
        && cold.Path.Length > 0 && cold.PursuitGoal.X == n.Goal.X && cold.PursuitGoal.Z == n.Goal.Y
        && EndsShort(cold.Path[^1], cold.PursuitGoal, cold, tc, endReach);

    /// <summary>
    /// A path held that ends short of <paramref name="target"/> (<see cref="PathEndsShort"/>) is dropped and noted as a path failure, the next
    /// request <see cref="RepathSeconds"/> away. True when it was.
    /// </summary>
    static bool GaveUpShortPath(World world, int i, ref CharacterHot n, CharacterCold cold, CharacterCold? tc, CharacterId target, float endReach)
    {
        if (!PathEndsShort(n, cold, tc, endReach)) return false;

        cold.PathFailed = true;
        NotePathFailure(world, i, cold, target, endReach);
        DropPath(ref n, cold);
        n.TaskTime = RepathSeconds;
        return true;
    }

    /// <summary>
    /// Drops the fighter's path: no path flags, the cursor back to the start, no request.
    /// </summary>
    static void DropPath(ref CharacterHot n, CharacterCold cold)
    {
        n.Flags = (ushort)(n.Flags & ~PathFlags);
        n.PathCursor = 0;
        cold.PathRequest = 0;
    }

    /// <summary>
    /// Asks a new path to <paramref name="goal"/> on the floor at <paramref name="targetHeight"/>; the next comes RepathSeconds later at
    /// the earliest.
    /// </summary>
    static void AskPath(ref CharacterHot n, CharacterCold cold, Vector2 goal, float targetHeight)
    {
        n.Goal = goal;

        // the floor the target stands on (a target upstairs is walked to up the stairs, not to the ground below it)
        (cold.GoalHeight, cold.GoalHeightAt) = (targetHeight, goal);
        cold.PursuitGoal = new Vector3(goal.X, targetHeight, goal.Y);
        n.Mode = SpeedMode.Free;
        n.Flags |= (ushort)MoveFlags.NeedPath;
        n.TaskTime = RepathSeconds;
    }

    /// <summary>
    /// The point past a runner that a chaser behind it aims at: beside the runner's track on the chaser's side (by slot when dead behind),
    /// RepathSeconds of the runner's way past the lead. <paramref name="goal"/> stays when that point is off the navmesh (a wall, a cliff): the
    /// chaser then goes for the target itself.
    /// </summary>
    static Vector2 PassPoint(World world, int i, CharacterCold cold, CharacterCold? tc, float targetHeight, Vector3 d, Vector3 tv, float lead,
        Vector2 goal)
    {
        var heading = Vector2.Normalize(new Vector2(tv.X, tv.Z));
        var across = new Vector2(-heading.Y, heading.X);
        float side = across.X * -d.X + across.Y * -d.Z;
        float pass = CombatTuning.PassWidth * (CombatTuning.Footprint(cold) + CombatTuning.Footprint(tc));
        float offset = side > 0 || side == 0 && (i & 1) == 0 ? pass : -pass;
        var ahead = goal + new Vector2(tv.X, tv.Z) * (lead + RepathSeconds) + across * offset;

        return world.Walkability.IsWalkable(ahead.X, ahead.Y, targetHeight) ? ahead : goal;
    }

    /// <summary>
    /// Hands fighter <paramref name="i"/> to the fight steering (<see cref="CombatSteering"/>) when it is in its footwork, melee, not holding and not
    /// down, and the line to its target is clear (<see cref="CombatSystem.SteersAt"/>, checked every <see cref="RepathSeconds"/>, every tick on a
    /// path chase, which a blocked line starts for <see cref="PathChaseSeconds"/> at least): its paths are dropped and its fight state sets the band
    /// (<c>combatMovementOffensive</c>'s callers, docs/game/combat.md "Fight steering"). A stumble holds it still (engine choice: the hit reaction
    /// carries it). False: it walks paths.
    /// </summary>
    bool Steer(World world, int i, ref CharacterHot n, CharacterCold cold, in CombatSlot st, int t, bool holding)
    {
        var steer = cold.Steer ??= new SteerState();
        bool was = steer.Active;
        steer.Active = false;
        steer.Face = LookAt(world, in st, t, steer.Face);

        // A swing has no steering: the movement is animation driven (the attack start and every swing tick set the movement's lock, FUN_14065e240;
        // CharMovement update FUN_14065ffa0 then moves it by the clip's root motion only), so it stands in its place (MovementSystem turns it,
        // CombatSystem.SwingAim).
        if (cold.CombatRunning || st.Ranged || holding || st.Down || st.AttackTech >= 0) return false;

        // A fighter on a path chase tests the line every tick (CombatClassAI update FUN_14060d6a0, in the chase states); one in its footwork only
        // when it decides again, about every RepathSeconds. A blocked line met at a new pick or in the footwork starts a chase of at least
        // PathChaseSeconds (FUN_14060c8b0, timer +0x14c), a holder's not.
        bool chasing = steer.ChaseTarget == t;
        int band = BandTarget(world, in st, t);
        if (chasing || world.Tick >= steer.LineTick || steer.Target != band)
        {
            bool clear = combat.SteersAt(world, i, t);
            if (!clear && !chasing)
                steer.PathUntil = Orders.Holding(cold) ? 0 : world.Tick + (long)MathF.Ceiling(PathChaseSeconds / world.TickSeconds);

            steer.LineClear = clear;
            steer.LineTick = world.Tick + (long)MathF.Ceiling(RepathSeconds / world.TickSeconds);
        }

        if (!steer.LineClear)
        {
            steer.ChaseTarget = t;
            return false;
        }

        // The line is clear, but the chase runs until its timer is out.
        if (chasing && world.Tick < steer.PathUntil) return false;

        steer.ChaseTarget = -1;
        float r = CombatTuning.Footprint(cold);
        float reach = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, false);

        // The wait after its own swing is state 6 too, whether or not it is engaged (FUN_14060d6a0 forces it while +0x144 is set).
        bool waiting = st.AttackTech < 0 && st.ReactTech < 0 && world.Tick < st.PauseUntil;
        int around = st.AttackTech < 0 && st.ReactTech < 0 && (!st.Engaged || waiting) ? combat.FightersAt(world, t) : 0;

        // Swinging (AttackState), defending (CombatClassAI slot 12), engaged (attack start, state 5), else waiting (state 6).
        var (near, far, through, limit) = st.ReactTech >= 0 ? (r + 7.5f, r + 19, st.Picked, st.Picked ? 18f : 16f)
            : st.Engaged && !waiting ? (1.4f * r, reach, !st.CircleState, 200f)
            : (around * 0.25f + CombatTuning.CircleNear, 3 * around + CombatTuning.CircleFar, false, 200f);
        (steer.Min, steer.Max) = CombatSteering.Band(near, far, r * (cold.Medical?.ScaleK ?? 1));
        (steer.PowerThrough, steer.Limit, steer.Target) = (through, limit, band);
        if ((n.Flags & PathFlags) != 0) DropPath(ref n, cold);

        if (world.Tick < st.StunUntil) return true;

        if (!was) steer.Reset();
        steer.Active = true;
        return true;
    }

    /// <summary>
    /// The look-at it faces (<see cref="SteerState.Face"/>): its target while it swings (the attack start, <c>FUN_140666400</c>; the swing update
    /// <c>FUN_14060a590</c>) and in its footwork (state 5, <c>FUN_1402b3180</c>), the swing its defence follows (the defence update, not during a
    /// block of a rear direction), else the last one (the wait, state 6, writes none).
    /// </summary>
    int LookAt(World world, in CombatSlot st, int t, int last)
    {
        bool waiting = st.AttackTech < 0 && st.ReactTech < 0 && world.Tick < st.PauseUntil;
        if (st.ReactTech >= 0)
            return combat.DefenceFaces(st.ReactTech) && (uint)st.ReactedSlot < (uint)world.Characters.Previous.Length ? st.ReactedSlot : last;

        if (st.AttackTech >= 0 || st.Engaged && !waiting) return t;

        return last;
    }

    /// <summary>
    /// Whom the band is kept to (<c>FUN_14060a190</c>): a defender's is the swing its defence follows, unless it has a focused target (the player's
    /// pick, CombatClassAI +0x2c8); in every other state the fight target.
    /// </summary>
    static int BandTarget(World world, in CombatSlot st, int t)
    {
        int followed = st.ReactedSlot;
        if (st.ReactTech < 0 || st.Picked || (uint)followed >= (uint)world.Characters.Previous.Length || !world.Characters.Previous[followed].Alive)
            return t;

        return followed;
    }

    /// <summary>
    /// Whether a path ending at <paramref name="end"/> leaves the fighter farther from <paramref name="goal"/> than a swing reaches
    /// (<paramref name="reach"/>, the 3D gap of <see cref="CombatTuning.Gap"/>, as the attack start measures it): the walkability answers an
    /// unreachable goal with the way to the reachable point nearest it, so such a path is no path to the target (engine reading).
    /// </summary>
    internal static bool EndsShort(Vector3 end, Vector3 goal, CharacterCold me, CharacterCold? target, float reach) =>
        CombatTuning.Gap(end, goal, me, target) > reach;

    #endregion

    #region Path failures

    /// <summary>
    /// Lapses the path failures and, after a path request that found no way, adds <paramref name="target"/> (<see cref="CharacterCold.Ignored"/>). A
    /// lapsed one is armed for another <see cref="PathFailureSeconds"/> while there is still no path to its target, else dropped
    /// (<c>AITaskSytem::update4Frame</c>, <b>Observed</b>; at most one dropped per call).
    /// </summary>
    internal static void NotePathFailure(World world, int i, CharacterCold cold, CharacterId target, float endReach)
    {
        var failures = cold.Ignored;
        long armed = world.Tick + (long)MathF.Ceiling(PathFailureSeconds / world.TickSeconds);
        bool dropped = false;
        if (failures is not null)
        {
            for (int k = failures.Count - 1; k >= 0; k--)
            {
                if (failures[k].Key.Name is not null) continue; // a site or item, not a fight target
                if (cold.PathFailed && failures[k].Key.Id == target)
                {
                    failures.RemoveAt(k);
                    continue;
                }

                if (failures[k].Until > world.Tick || dropped) continue;

                if (!PathExists(world, i, cold, failures[k].Key.Id, endReach)) failures[k] = (failures[k].Key, armed);
                else
                {
                    failures.RemoveAt(k);
                    dropped = true;
                }
            }
        }

        if (!cold.PathFailed) return;

        cold.PathFailed = false;
        (cold.Ignored ??= []).Add((IgnoreKey.Of(target), armed));
    }

    /// <summary>
    /// Whether fighter <paramref name="i"/> has a path to <paramref name="target"/> (<c>Character::pathExists</c>): a gone target counts as one (the
    /// entry is dropped). Engine reading: asked of the walkability on the spot, with the fighter's footprint and water costs, as its own path
    /// requests are.
    /// </summary>
    static bool PathExists(World world, int i, CharacterCold cold, CharacterId target, float endReach)
    {
        var table = world.Characters;
        int t = target.Slot;
        var prev = table.Previous;
        if ((uint)t >= (uint)prev.Length || !prev[t].Alive || prev[t].Generation != target.Generation) return true;

        var next = table.Next;
        var water = WaterCost.Of(cold.WaterFactor, next[i].MaxSpeed, cold.Race, cold.SwimSpeed);
        var path = world.Walkability is IAgentWalkability agent
            ? agent.FindPath(next[i].Position, prev[t].Position, cold.FootprintRadius, water, cold.RoadPreference)
            : world.Walkability.FindPath(next[i].Position, prev[t].Position);

        return path.Points.Count > 0 && !EndsShort(path.Points[^1], prev[t].Position, cold, table.Cold(t), endReach);
    }

    /// <summary>
    /// Puts <paramref name="target"/> on <paramref name="cold"/>'s path failures for <see cref="PathFailureSeconds"/> (<c>notifyPathImpossible</c>
    /// <c>FUN_140796ed0</c>, as a task walk that cannot get to its target reports it: the AI plan step <c>FUN_14050d970</c> and
    /// <c>AI::isWithinChoppingRange</c> <c>FUN_14059c6d0</c>, <b>Verified</b> callers). Lapsed entries are dropped, not tested again.
    /// </summary>
    internal static void GiveUp(World world, CharacterCold cold, CharacterId target)
    {
        var failures = cold.Ignored ??= [];
        failures.RemoveAll(f => f.Key == IgnoreKey.Of(target) || f.Until <= world.Tick);
        failures.Add((IgnoreKey.Of(target), world.Tick + (long)MathF.Ceiling(PathFailureSeconds / world.TickSeconds)));
    }

    /// <summary>
    /// Whether <paramref name="cold"/> found no path to <paramref name="target"/> within the last <see cref="PathFailureSeconds"/>.
    /// </summary>
    public static bool PathImpossible(CharacterCold cold, CharacterId target, long tick) => cold.IsIgnored(IgnoreKey.Of(target), tick);

    #endregion
}

/// <summary>
/// The answer to being attacked: at once when an attacker takes it as its target and that alerts it (<see cref="CombatSystem.Alerts"/>: not through a
/// wall or from another floor for the player's side, until a blow lands; the alert ends its sneaking, puts the attacker in its senses as a threat
/// (<see cref="AggroSystem.Force"/>) and, with the target's fight off, asks it to be decided again unless it gets up or is player-side and moving).
/// An NPC with a mind defends itself by its 32 Self preservation in the AI's chooser (row 13 on its own attacker,
/// <see cref="Ai.AiSystem.SelfDefencePick"/>), which the alert asks to think at the next tick. One without a mind (no squad: scripted and test
/// worlds) gets the engine's answer here: at the alert and again every <see cref="CheckEveryTicks"/> ticks, awake, armed with a
/// <see cref="Fighter"/>, not crawling and not fighting already, it fights its attacker at any distance (<b>engine choice</b>). Nobody else joins
/// here: squad mates and allies come by their own protect goals in the chooser (AiSystem.Fights.cs), which the alert asks to think
/// (<see cref="AskProtectors"/>). A player character's own defence is the decision step's 32 Self preservation (<see cref="DecisionSystem"/>; this
/// system offers the attacker, <see cref="SelfFoe"/>), except a bodyguard's against an attacker within 40 (row 45's <c>!39</c>); followers and
/// bodyguards fight their target's attacker within 40 of it (rows 31 / 45 / 289's <c>!16</c>, <see cref="Guards"/>). Passive keeps a character from
/// helping others, not from defending itself. The orders go into the world's deferred list for the next tick (<see cref="World.Defer"/>) and are
/// marked <see cref="AttackOrder.Defensive"/>. A player character's help for others (squad mates, with <see cref="SquadAi.Help"/> allied factions) is
/// the decision step's 15 Protect allies (<see cref="JoinFight"/>), not this pass.
/// </summary>
public sealed class RetaliationSystem(CombatSystem combat) : ITickSystem, IDecisionOwner
{
    /// <summary>
    /// The number of ticks between the looks at the attackers that did not just alert.
    /// </summary>
    public const int CheckEveryTicks = 15;

    /// <summary>
    /// A player's character defends itself only against an attacker within this (Self preservation's desire is 0 beyond squared 33000,
    /// docs/game/combat.md "Fight lifecycle and orders").
    /// </summary>
    public static readonly float SelfDefenceReach = MathF.Sqrt(33000);

    /// <summary>
    /// A threat this near is immediate (Character +0x2b0): it ends a Loot order.
    /// </summary>
    public const float ImmediateReach = 40;

    /// <summary>
    /// A mate is protected only against an attacker within this of it (<c>AI::findAttackerOf</c>, docs/game/combat.md "Fight lifecycle and orders").
    /// </summary>
    public const float ProtectReach = 150;

    /// <summary>
    /// The living bodyguards (characters with a follow order) in slot order, listed once per check for the guard search.
    /// </summary>
    readonly List<int> _guards = [];

    /// <summary>
    /// Per attacker slot: the target (slot, generation) it last took, the tick it was taken, and whether that target has been alerted
    /// (<see cref="CombatSystem.Alerts"/>, or a shot for a shooter).
    /// </summary>
    int[] _alertSlot = [], _alertGeneration = [];
    /// <summary>
    /// Per attacker slot: the tick its target was taken.
    /// </summary>
    long[] _alertTick = [];
    /// <summary>
    /// Per attacker slot: whether its target has been alerted, and whether that happened this tick.
    /// </summary>
    bool[] _alerted = [], _justAlerted = [];

    /// <summary>
    /// The joiners of a fight grouped by the attacker each takes on, in the order first chosen (scratch of the fight join).
    /// </summary>
    readonly List<(int Foe, List<CharacterId> Joiners)> _byFoe = [];

    /// <summary>
    /// This tick's attacker per attacked player character (target slot, attacker slot; <see cref="OfferSelf"/>), for the decision step's
    /// Self preservation.
    /// </summary>
    readonly Dictionary<int, int> _selfFoes = [];
    /// <summary>
    /// The tick <see cref="_selfFoes"/> was made at; it holds nothing on any other tick.
    /// </summary>
    long _selfFoesTick = -1;

    /// <summary>
    /// The towns, for the slave-defender gate (<see cref="SlaveHelpAllowed"/>); null: no gate.
    /// </summary>
    public PopulationData? Towns { get; set; }

    /// <summary>
    /// Registers the two goals the player characters fight by: 15 Protect allies and 32 Self preservation.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions)
    {
        decisions.Register(new JoinFight(this));
        decisions.Register(new SelfPreservation(this));
    }

    #region Protect allies

    /// <summary>
    /// 15 Protect allies of a player character (the player faction's <c>AI Goals</c>: priority 3, weight 1, so it comes after the orders and the
    /// urgent jobs and before every job; the chooser's step 4, docs/game/ai-tasks.md "Precedence"). Desire <c>FUN_140599b90</c>, <b>Verified</b>: 0
    /// while Passive or sneaking outside a fight and for a slave holding job 187; else the finder's best ally, x0.5 while carrying. The finder
    /// <c>FUN_14099ca10</c> (<see cref="BestAlly"/>) values the player's sensed allies by their fight threat, weighted by their distance; the plan is
    /// 13 Attacking attackers of (<c>AI::findAttackerOf</c>, <see cref="CombatSystem.BestAttackerOf"/>), so the goal needs an attacker within
    /// <see cref="ProtectReach"/> of the ally. Listed before Self preservation (a tie goes to the later goal). It runs while a defence (row 13) is
    /// its fight; with no ally left the desire stays at Self preservation's 0.7 (engine: the fight task ends by itself).
    /// </summary>
    sealed class JoinFight(RetaliationSystem owner) : IDecisionSource
    {
        /// <summary>
        /// The source's name in the decision log.
        /// </summary>
        public string Name => "15 Protect allies";

        /// <summary>
        /// A goal: it runs in the goals step.
        /// </summary>
        public DecisionStep Step => DecisionStep.Goals;

        /// <summary>
        /// Goal 15.
        /// </summary>
        public int Key => 15;

        /// <summary>
        /// The desire: 0 while Passive, else the best ally's value (<see cref="ProtectDesire"/>), else 0.7. Passive zeroes it even in a fight
        /// (FUN_140599b90, Verified).
        /// </summary>
        public float Desire(World world, int slot) =>
            (world.Characters.Cold(slot)!.Orders & CharacterOrders.Passive) != 0
                ? 0 : owner.ProtectDesire(world, slot) is var score && score > 0 ? score : 0.7f;

        /// <summary>
        /// Runs while its defence (row 13) is the character's fight; else while it may help (able, not Passive, not sneaking outside a fight, not an
        /// obedient slave) and an attacker of an ally is in reach.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            if (task == CharacterTask.Attack)
            {
                return cold.Running is { Row: Data.Gameplay.Ai.TaskType.AttackAttackersOf } run && run.Runs(task)
                    ? DecisionCheck.Runs
                    : DecisionCheck.No("fighting on another plan");
            }

            // Desire 0: Passive, or sneaking outside a fight (FUN_1405c9750), or an obedient slave (FUN_140597380 job 187).
            if (!Helps(table, slot, [])) return DecisionCheck.No("cannot fight, or desire 0 (Passive, or sneaking outside a fight)");
            if (cold.SlaveState == 1 && Kidnap.ObedientSlave.HasJob(cold)) return DecisionCheck.No("an obedient slave (desire 0)");

            if (owner.Plan(world, slot, out _) >= 0) return DecisionCheck.Run;

            return DecisionCheck.No(owner.BestAlly(world, slot, out _, out _) > 0 ? "no attacker of the ally in reach" : "no fighting ally in sight");
        }

        /// <summary>
        /// Gives a defensive attack order on the planned foe for the next tick.
        /// </summary>
        public void Start(World world, int slot)
        {
            var table = world.Characters;
            if (owner.Plan(world, slot, out _) is var foe && foe < 0) return;

            world.Defer(new AttackOrder([table.IdOf(slot)], table.IdOf(foe)) { Tick = world.Tick + 1, Defensive = true, Source = TaskSource.Goal });
        }
    }

    /// <summary>
    /// The finder of 15 / 288 for a player character (<c>FUN_14099ca10</c>, <b>Verified</b>): over the sensed entries tagged ALLY (not down or
    /// imprisoned) that were seen or heard at the last look, each ally is worth its fight threat (<c>FUN_14099a190</c>,
    /// <see cref="CombatSystem.FightThreat"/>: above 0 while it is attacked, threatened or fighting) x <c>clamp((1000 - d^2) / 990, 0.1, 1)</c> by
    /// the squared distance d^2 between the two (<c>FUN_1409b2380</c>: 1 within 3.2 units, 0.1 from 30); an ally of another faction counts only with
    /// <see cref="SquadAi.Help"/> and then the value is squared; a slave or escaped slave only where the looker may help one
    /// (<see cref="SlaveHelpAllowed(World, CharacterCold, Vector3)"/>, tested once, for the first slave met). The best, the first in slot order on a
    /// tie, capped at 1.2; x0.5 while the looker carries someone (<c>FUN_140599b90</c>). 0 when there is none; <paramref name="ally"/> is that one's
    /// slot (-1 for none).
    /// </summary>
    float BestAlly(World world, int slot, out int ally, out float first)
    {
        (ally, first) = (-1, 0);
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold || world.SystemOf<AggroSystem>() is not { } aggro || aggro.SensesOf(slot) is not { } senses) return 0;

        bool help = (world.Player.SquadAi & SquadAi.Help) != 0;
        float Weigh(int other, float threat)
        {
            float distanceSquared = Vector3.DistanceSquared(world.Characters.Previous[slot].Position, world.Characters.Previous[other].Position);
            float v = threat * Math.Clamp((distanceSquared - 1000f) / (10f - 1000f), 0.1f, 1f);
            return table.Cold(other)!.Faction == cold.Faction ? v : help ? v * v : 0;
        }

        var scan = ScanAllies(world, slot, combat, aggro, senses, SenseTag.Ko | SenseTag.Imprisoned,
            () => SlaveHelpAllowed(world, cold, world.Characters.Previous[slot].Position), Weigh);
        (ally, first) = (scan.Best, scan.First);

        return scan.Best < 0 ? 0 : Math.Min(scan.Value, CombatSystem.ProtectCap);
    }

    /// <summary>
    /// The score of 15 for a player character: the desire (the finder's first ally above 0, uncapped, x0.5 while carrying; <c>FUN_140599b90</c>)
    /// times the finder's factor (the best, capped).
    /// </summary>
    float ProtectDesire(World world, int slot)
    {
        float factor = BestAlly(world, slot, out _, out float first);
        return factor <= 0 ? 0 : first * factor * (world.Characters.Cold(slot)!.Carrying.IsNone ? 1 : 0.5f);
    }

    /// <summary>
    /// The finder <c>FUN_14099ca10</c> shared by both kinds of looker (<b>Verified</b>): over the sensed entries tagged ALLY and not
    /// <paramref name="exclude"/>, seen or heard at the last look, alive, a slave or escaped slave only where <paramref name="slaveOk"/> says (asked
    /// once, for the first), each worth <paramref name="weigh"/> of its fight threat (0 or less skips it). The best (the first on a tie), and the
    /// first above 0, which the desire <c>FUN_140599b90</c> takes (the finder called to stop at it, uncapped).
    /// </summary>
    internal static (int Best, float Value, float First) ScanAllies(
        World world,
        int slot,
        CombatSystem fights,
        AggroSystem look,
        SenseBlock senses,
        SenseTag exclude,
        Func<bool> slaveOk,
        Func<int, float, float> weigh)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int best = -1;
        float value = 0, first = 0;
        bool slaveChecked = false, slaveAllowed = false;
        foreach (var e in senses.Entries)
        {
            int a = e.Target;
            if (a == slot || !e.Now || (e.Tags & (SenseTag.Ally | exclude)) != SenseTag.Ally || (uint)a >= (uint)prev.Length || !prev[a].Alive
                || prev[a].Generation != e.Generation || table.Cold(a) is not { } ally)
                continue;

            if (ally.SlaveState is 1 or 2)
            {
                if (!slaveChecked) (slaveChecked, slaveAllowed) = (true, slaveOk());
                if (!slaveAllowed) continue;
            }

            float v = weigh(a, fights.FightThreat(world, a, look.SensesOf(a)));
            if (v <= 0) continue;

            if (first <= 0) first = v;
            if (v > value) (best, value) = (a, v);
        }

        return (best, value, first);
    }

    /// <summary>
    /// 13 Attacking attackers of for 15 (<c>AI::findAttackerOf</c>, <see cref="CombatSystem.BestAttackerOf"/>): the attacker of
    /// <see cref="BestAlly"/>'s ally the player character <paramref name="slot"/> fights, -1 for none. A character on Hold takes it only where its
    /// post test passes (<see cref="Orders.HoldPost"/>).
    /// </summary>
    int Plan(World world, int slot, out int ally)
    {
        if (BestAlly(world, slot, out ally, out _) <= 0) return -1;

        var table = world.Characters;
        int foe = combat.BestAttackerOf(world, ally, slot);
        if (foe < 0 || table.Cold(slot) is not { } cold) return -1;

        if (!Orders.Holding(cold)) return foe;

        var next = table.Next;
        var post = Orders.HoldPointOf(cold, next[slot].Position);
        bool foeCrawls = table.Cold(foe)?.Medical?.Crawling == true || combat.StateOf(table.IdOf(foe)).AttackTech >= 0;
        return Orders.HoldPost(next[slot].Position, post, next[foe].Position, next[foe].Velocity, next[foe].Yaw, foeCrawls, true) ? foe : -1;
    }

    #endregion

    #region Self defence

    /// <summary>
    /// The attacker player character <paramref name="slot"/> defends itself against this tick, -1 for none (a pure read): the first, in slot order,
    /// that took it as its target and alerted it, still awake, within <see cref="SelfDefenceReach"/> (Self preservation's desire is 0 beyond,
    /// docs/game/combat.md "Self-defence"), not shooting at a gunner (146 leaves its turret only for a melee attacker) and, on Hold, passing its post
    /// test (<see cref="Orders.HoldPost"/>).
    /// </summary>
    public int SelfFoe(World world, int slot)
    {
        if (_selfFoesTick != world.Tick || !_selfFoes.TryGetValue(slot, out int a)) return -1;

        return world.Characters.Next[a].Alive && world.Characters.Cold(a) is { Medical.Incapacitated: false } ? a : -1;
    }

    /// <summary>
    /// Records <paramref name="a"/> as this tick's attacker of player character <paramref name="t"/> (the first one only) when it targets it, is not
    /// incapacitated, is within <see cref="SelfDefenceReach"/> (a gunning character does not take a ranged attacker) and passes a holding character's
    /// post test.
    /// </summary>
    void OfferSelf(CharacterTable table, int a, int t)
    {
        var next = table.Next;
        if (table.Cold(t) is not { IsPlayer: true } tc || _selfFoes.ContainsKey(t)) return;

        var st = combat.StateOf(new CharacterId(a, next[a].Generation));
        if (!next[t].Alive || next[t].Generation != st.TargetGeneration || table.Cold(a) is not { Medical.Incapacitated: false } ac) return;
        if (st.Ranged && tc.Gunning is not null) return;

        var d = next[a].Position - next[t].Position;
        if (d.X * d.X + d.Z * d.Z > SelfDefenceReach * SelfDefenceReach) return;

        if (Orders.Holding(tc))
        {
            var post = Orders.HoldPointOf(tc, next[t].Position);
            bool attackerCrawls = ac.Medical?.Crawling == true || st.AttackTech >= 0;
            if (!Orders.HoldPost(next[t].Position, post, next[a].Position, next[a].Velocity, next[a].Yaw, attackerCrawls, true)) return;
        }

        _selfFoes.Add(t, a);
    }

    /// <summary>
    /// 32 Self preservation for a player character (priority 3, weight 1; desire <c>FUN_140599d20</c>: 1 with the attacker within
    /// <see cref="ImmediateReach"/>, else 0.7 within <see cref="SelfDefenceReach"/>): it fights its attacker (<see cref="SelfFoe"/>) as row 13, a
    /// defence. It runs while a defence (row 13) is its fight.
    /// </summary>
    sealed class SelfPreservation(RetaliationSystem owner) : IDecisionSource
    {
        /// <summary>
        /// The source's name in the decision log.
        /// </summary>
        public string Name => "32 Self preservation";

        /// <summary>
        /// A goal: it runs in the goals step.
        /// </summary>
        public DecisionStep Step => DecisionStep.Goals;

        /// <summary>
        /// Goal 32.
        /// </summary>
        public int Key => 32;

        /// <summary>
        /// Runs while its defence (row 13) is the character's fight; else for one that can fight, is free and awake, and has this tick's attacker.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var task = (CharacterTask)table.Next[slot].Task;
            if (task == CharacterTask.Attack && cold.Running is { Row: Data.Gameplay.Ai.TaskType.AttackAttackersOf } run && run.Runs(task))
                return DecisionCheck.Runs;
            if (cold is not { Fighter.CanAttack: true, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true })
                return DecisionCheck.No("cannot fight");

            return owner.SelfFoe(world, slot) >= 0 ? DecisionCheck.Run : DecisionCheck.No("no attacker in range");
        }

        /// <summary>
        /// 1 with the attacker within <see cref="ImmediateReach"/>, else 0.7 (also while its fight goes on).
        /// </summary>
        public float Desire(World world, int slot)
        {
            int a = owner.SelfFoe(world, slot);

            // running: its fight goes on
            if (a < 0) return 0.7f;

            var d = world.Characters.Next[a].Position - world.Characters.Next[slot].Position;
            return d.X * d.X + d.Z * d.Z <= ImmediateReach * ImmediateReach ? 1 : 0.7f;
        }

        /// <summary>
        /// Gives a defensive attack order on this tick's attacker for the next tick.
        /// </summary>
        public void Start(World world, int slot)
        {
            var table = world.Characters;
            int a = owner.SelfFoe(world, slot);
            world.Defer(new AttackOrder([table.IdOf(slot)], table.IdOf(a)) { Tick = world.Tick + 1, Defensive = true, Source = TaskSource.Goal });
        }
    }

    #endregion

    #region Alerts

    /// <summary>
    /// The answer to being attacked (<c>AI::underMeleeAttack</c>, <see cref="Alert"/>): the first sweep's attacks that alert, then the periodic look
    /// at every attacker whose target was alerted, the defenders it has in the attacked character's squad and friends (<see cref="Guards"/>, the
    /// attacker's own defence in <see cref="SelfDefends"/>), each joining the fight of the attacker the finder scores best for it.
    /// </summary>
    public void SlowWorld(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        if (_alertSlot.Length < table.HighWater) GrowAlertArrays(table.HighWater);

        // A target taken (or a blow landed) alerts at once and is answered in the same pass; the rest is looked at every CheckEveryTicks.
        bool fresh = false;
        if (_selfFoesTick != world.Tick)
        {
            _selfFoes.Clear();
            _selfFoesTick = world.Tick;
        }

        for (int a = 0; a < table.HighWater; a++)
        {
            _justAlerted[a] = false;
            int t = next[a].Alive && next[a].Task == (byte)CharacterTask.Attack
                ? combat.StateOf(new CharacterId(a, next[a].Generation)).TargetSlot : -1;
            if (t < 0 || t >= table.HighWater)
            {
                _alertSlot[a] = -1;
                continue;
            }

            if (_alertSlot[a] != t || _alertGeneration[a] != next[t].Generation)
            {
                _alertSlot[a] = t;
                _alertGeneration[a] = next[t].Generation;
                _alertTick[a] = world.Tick;
                _alerted[a] = false;
            }

            if (!_alerted[a])
            {
                var st = combat.StateOf(new CharacterId(a, next[a].Generation));
                if (!AlertsNow(world, a, t, st)) continue;

                _alerted[a] = _justAlerted[a] = fresh = true;
                Alert(world, a, t, !st.Ranged);
            }

            OfferSelf(table, a, t);
        }

        bool periodic = world.Tick % CheckEveryTicks == 0;
        if (!periodic && !fresh) return;

        // The bodyguards, in slot order, for Guards (each looked through the whole table per attacker before).
        _guards.Clear();
        for (int s = 0; s < table.HighWater; s++)
            if (next[s].Alive && table.Cold(s) is { FollowOrder.IsNone: false }) _guards.Add(s);

        var taken = new HashSet<int>();
        if (fresh)
        {
            for (int a = 0; a < table.HighWater; a++)
                if (_justAlerted[a]) AskProtectors(world, table, a, _alertSlot[a]);
        }

        for (int a = 0; a < table.HighWater; a++) JoinAttack(world, table, a, periodic, taken);
    }

    /// <summary>
    /// Grows the per-slot alert arrays to at least <paramref name="highWater"/> (doubling).
    /// </summary>
    void GrowAlertArrays(int highWater)
    {
        int size = Math.Max(highWater, _alertSlot.Length * 2);
        Array.Resize(ref _alertSlot, size);
        Array.Resize(ref _alertGeneration, size);
        Array.Resize(ref _alertTick, size);
        Array.Resize(ref _alerted, size);
        Array.Resize(ref _justAlerted, size);
    }

    /// <summary>
    /// The defenders of one attacker's alerted target join its fight this pass: the target's own defence, its bodyguards and the Hold order's post
    /// test, then one attack order per attacker the finder scores best for each joiner (<c>FUN_14099c3b0</c>, grouped in the order first chosen).
    /// </summary>
    void JoinAttack(World world, CharacterTable table, int a, bool periodic, HashSet<int> taken)
    {
        var next = table.Next;
        if (!next[a].Alive || next[a].Task != (byte)CharacterTask.Attack) return;

        var attacker = new CharacterId(a, next[a].Generation);
        var st = combat.StateOf(attacker);
        int t = st.TargetSlot;
        if (t < 0 || t >= table.HighWater || !next[t].Alive || next[t].Generation != st.TargetGeneration
            || table.Cold(a) is not { Medical.Incapacitated: false })
            return;

        // An attacker its target was not alerted to (the wall and floor filter) is left alone until a blow lands.
        if (_alertSlot[a] != t || !_alerted[a] || !periodic && !_justAlerted[a]) return;

        // A gunner leaves its turret only for a melee attacker (task 146's "!iAmBeingMeleeAttacked"); bolts do not bring it down.
        if (st.Ranged && table.Cold(t)?.Gunning is not null) return;

        var defenders = new List<CharacterId>();

        // A player character's own defence is the decision step's (Self preservation, OfferSelf); it is taken here, so it joins no other fight of
        // this pass.
        if (SelfDefends(table, t, taken, next[a].Position))
        {
            defenders.Add(new(t, next[t].Generation));
            taken.Add(t);
        }
        else if (_selfFoes.TryGetValue(t, out int own) && own == a)
        {
            taken.Add(t);
        }

        // Squad mates and allies come only by their own goals (143, 15, 288 in the AI's chooser, AiSystem.Fights.cs; a player character's 15,
        // JoinFight). Bodyguards of the attacked character come too (Bodyguard, order 45).
        Guards(world, table, t, a, defenders, taken, _guards);

        // The Hold order takes on only an attacker its post test accepts (Orders.HoldPost).
        var from = next[a].Position;
        bool attackerCrawls = table.Cold(a)?.Medical?.Crawling == true || st.AttackTech >= 0;
        for (int k = defenders.Count - 1; k >= 0; k--)
        {
            int ds = defenders[k].Slot;
            if (table.Cold(ds) is { } dc && Orders.Holding(dc)
                && !Orders.HoldPost(next[ds].Position, Orders.HoldPointOf(dc, next[ds].Position), from, next[a].Velocity,
                    next[a].Yaw, attackerCrawls, true))
                defenders.RemoveAt(k);
        }

        if (defenders.Count == 0) return;

        // The engine started these this tick (a follow row's fight, a bodyguard's own defence, an NPC's): the decision step leaves the player's alone
        // this tick.
        foreach (var d in defenders)
        {
            if (table.Cold(d.Slot) is { IsPlayer: true }) world.Decisions.Claim(world, d.Slot);
        }

        // Each one joining the attacked character's fight takes the attacker AI::findAttackerOf scores best for it (FUN_14099c3b0, Verified): grouped
        // by that attacker, in the order first chosen. The attacked character itself answers this attacker.
        _byFoe.Clear();
        foreach (var d in defenders)
        {
            int foe = d.Slot == t ? a : combat.BestAttackerOf(world, t, d.Slot);
            if (foe < 0) foe = a;

            int at = _byFoe.FindIndex(g => g.Foe == foe);
            if (at < 0) _byFoe.Add((foe, [d]));
            else _byFoe[at].Joiners.Add(d);
        }

        foreach (var (foe, joiners) in _byFoe)
            world.Defer(new AttackOrder(joiners, new CharacterId(foe, next[foe].Generation)) { Tick = world.Tick + 1, Defensive = true });
    }

    /// <summary>
    /// Whether attacker <paramref name="a"/> alerts its target <paramref name="t"/> this pass: a melee fighter on taking it
    /// (<see cref="CombatSystem.Alerts"/>); a shooter only once it has fired at it since (its target setter alerts nobody; the shot calls the
    /// victim's shot handler, docs/game/combat.md "Being targeted"). The state read lags a tick, so a shot of the tick before the target was seen
    /// counts.
    /// </summary>
    bool AlertsNow(World world, int a, int t, in CombatSlot st)
    {
        if (!st.Ranged) return combat.Alerts(world, a, t);

        return st.Shots > 0 && st.ShotTick >= _alertTick[a] - 1;
    }

    /// <summary>
    /// The target's side of an alert (<c>AI::underMeleeAttack</c> for a melee attacker, the shot handler <c>FUN_140999d50</c> for a shooter): it
    /// senses the attacker (seen), stops sneaking, its squad tags the attacker TEMP_ENEMY, and with its fight off it replans. Only a melee attacker
    /// makes one staying low stand and rolls the knock-down of one getting up.
    /// </summary>
    void Alert(World world, int a, int t, bool melee)
    {
        var aggro = world.SystemOf<AggroSystem>();
        aggro?.Force(world, t, a);

        // The shot handler's forced sense carries the alarm flag (the bolt hit's FUN_140999d50 call does the same, CombatSystem's bolt hit).
        if (!melee) aggro?.Alarm(world, world.Characters.IdOf(t), world.Characters.IdOf(a));
        world.SystemOf<Dialogue.DialogueSystem>()?.TagEnemy(world, t, a);
        if (world.Characters.Cold(t) is not { } tc) return;

        tc.Orders &= ~CharacterOrders.Sneak;
        bool gettingUp = CharacterFacts.GettingUp(tc, world.Tick);
        if (melee)
        {
            // Under melee attack (Character +0x2b0) one staying low stands.
            if (tc.Medical is { } medical) medical.StayingLow = false;
            combat.UnderAttackGettingUp(world, t, tc, (ulong)(uint)a);
        }

        // With its fight off it replans, unless it is getting up or is player-side and moving (docs/game/combat.md "Being targeted"): an NPC's AI
        // thinks at the next tick, where its Self preservation takes the attacker up.
        if (!tc.InCombat && !gettingUp && !(tc.IsPlayer && world.Characters.Next[t].Velocity.LengthSquared() > 1))
        {
            DecisionSystem.Request(tc);
            if (tc.Ai is { } mind) mind.Replan = true;
        }
    }

    /// <summary>
    /// The characters whose follow row (31 Staying close, 45 Bodyguard, 289) runs on <paramref name="slot"/> defend it against
    /// <paramref name="attacker"/> within <see cref="ImmediateReach"/> of it, in slot order, never against a character of their own faction: the rows
    /// need state 16 (no threat within 40 of the followed character, <c>FUN_14059de10</c> on its <c>+0x2b0</c>), which 13 "Attacking attackers of"
    /// provides (docs/game/ai-tasks.md "Precedence"); the fight is a step of the row, which the decision step follows again after it
    /// (<see cref="MovementSystem.FollowAgain"/>). A bodyguard's own attacker within 40 (45's <c>!39</c>) is <see cref="SelfDefends"/>.
    /// </summary>
    static void Guards(World world, CharacterTable table, int slot, int attacker, List<CharacterId> defenders, HashSet<int> taken, List<int> guards)
    {
        var next = table.Next;
        var target = new CharacterId(slot, next[slot].Generation);
        var reach = next[attacker].Position - next[slot].Position;
        if (reach.X * reach.X + reach.Z * reach.Z > ImmediateReach * ImmediateReach) return;

        foreach (int s in guards)
        {
            if (!next[s].Alive || s == attacker || table.Cold(s) is not { } c || c.FollowOrder != target
                || table.Cold(attacker)?.Faction == c.Faction)
            {
                continue;
            }
            if (next[s].Task != (byte)CharacterTask.Follow || c.Running is not { } run || !Production.PermaJobs.Follows(run.Row)
                || !run.Runs(CharacterTask.Follow)
                || !Helps(table, s, taken))
                continue;

            defenders.Add(new CharacterId(s, next[s].Generation));
            taken.Add(s);
        }
    }

    /// <summary>
    /// An alert asks the squad mates of the attacked character and of the attacker that have a mind to think at the next tick
    /// (<see cref="Ai.AiMind.Replan"/>; a replan trigger, engine stand-in for the original's), whatever their goals: their protect goals then decide
    /// in the chooser (AiSystem.Fights.cs). Others that sense the fight (15, 288) weigh it at their next think. Not those fighting already.
    /// </summary>
    static void AskProtectors(World world, CharacterTable table, int attacker, int attacked)
    {
        void Mates(int slot)
        {
            if (table.Cold(slot) is not { SquadId: >= 0 and var id } || world.Squads.Find(id) is not { } squad) return;

            foreach (var m in squad.Members)
            {
                if (m.Slot != slot && table.TryResolveNext(m, out int s) && table.Cold(s) is { InCombat: false, Ai: { } mind }) mind.Replan = true;
            }
        }

        Mates(attacked);
        Mates(attacker);
    }

    /// <summary>
    /// Whether the attacked character itself answers here: an NPC without a mind at any distance (<b>engine choice</b>: the minimal answer of a
    /// character no chooser runs); Passive does not stop it. An NPC with a mind defends itself by its Self preservation in the AI's chooser
    /// (<see cref="Ai.AiSystem.SelfDefencePick"/>). A player character's own defence is the decision step's Self preservation (<see cref="SelfFoe"/>,
    /// docs/game/ai-tasks.md "Precedence": its orders outrank it, it outranks the jobs), but a bodyguard following (row 45 running) answers here as a
    /// step of its row against an attacker within <see cref="ImmediateReach"/> (45 needs <c>!39</c>, its own <c>+0x2b0</c>; farther ones are Self
    /// preservation's, which a Bodyguard order outranks and a Bodyguard job does not).
    /// </summary>
    static bool SelfDefends(CharacterTable table, int slot, HashSet<int> taken, Vector3 attacker)
    {
        if (!Able(table, slot, taken, out var cold, out byte task)) return false;
        if (!cold.IsPlayer) return cold.Ai is null;

        var d = attacker - table.Next[slot].Position;
        return cold.Guarding && task == (byte)CharacterTask.Follow && d.X * d.X + d.Z * d.Z <= ImmediateReach * ImmediateReach;
    }

    /// <summary>
    /// Able, not Passive, not sneaking outside a fight (<c>scoreProtectAllies</c>'s zero), for anyone.
    /// </summary>
    static bool Helps(CharacterTable table, int slot, HashSet<int> taken) =>
        Able(table, slot, taken, out var cold, out _) && (cold.Orders & CharacterOrders.Passive) == 0
            && ((cold.Orders & CharacterOrders.Sneak) == 0 || cold.InCombat);

    /// <summary>
    /// Whether the character can answer a fight: not taken yet, not already attacking, a fighter that is free, awake and not fleeing; an NPC also not
    /// crawling and not about to flee.
    /// </summary>
    static bool Able(CharacterTable table, int slot, HashSet<int> taken, out CharacterCold cold, out byte task)
    {
        task = table.Next[slot].Task;
        cold = table.Cold(slot)!;
        if (taken.Contains(slot) || task == (byte)CharacterTask.Attack) return false;

        // a carried mate does not answer; a carrier does (no attack goal of the original's tests carrying, docs/game/carry-loot.md "A carrier in a
        // fight"). A crippled NPC (a leg down, crawling) does not either (with a mind its Self preservation runs, FUN_1405a34e0 against
        // FUN_1405a4260, Verified; without one it neither fights nor runs); a player character still fights.
        return cold is { Fighter.CanAttack: true, Fleeing: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true }
            && (cold.IsPlayer || cold.Medical is not { Crawling: true } && !Ai.SelfPreservation.Flees(cold, 1));
    }

    #endregion

    /// <summary>
    /// The towns, for the slave-defender gate: whether a helper may come to the defence of a slave or escaped slave (<c>FUN_140997e80</c> in the
    /// protect-allies finder, <b>Verified</b>): its faction is a bandit one, or it stands in no town, or that town's owner books no bounties (a
    /// lawful town's people leave slaves to their fate).
    /// </summary>
    bool SlaveHelpAllowed(World world, CharacterCold helper, Vector3 at) => SlaveHelpAllowed(world, Towns, combat.Relations, helper, at);

    /// <inheritdoc cref="SlaveHelpAllowed(World, CharacterCold, Vector3)"/>
    internal static bool SlaveHelpAllowed(World world, PopulationData? towns, Data.Gameplay.FactionRelations? factions,
        CharacterCold helper, Vector3 at)
    {
        if (towns is not { } data || factions is not { } relations) return true;
        if (relations.Contains(helper.Faction) && relations.Factions[helper.Faction].FundamentalType == Data.Gameplay.FundamentalType.Bandit)
            return true;

        var town = AiSystem.TownAt(data, world, at);
        if (town.Site < 0) return true;

        int owner = town.Site < data.SiteFactions.Count ? data.SiteFactions[town.Site] : -1;
        return !(relations.Contains(owner) && relations.Factions[owner].BooksBounties);
    }
}
