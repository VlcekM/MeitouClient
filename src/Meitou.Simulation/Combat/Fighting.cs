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
        bool holding = HoldsBack(cold, st, n.Position, next, t);

        // In its footwork with a clear line to the target, the fight steering moves it (docs/game/combat.md "Fight steering"): its state sets the
        // band, no paths.
        if (Steer(world, i, ref n, cold, st, t, holding)) return;

        float stand = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, cold.CombatRunning);
        bool inReach = !st.Ranged && !behind && aimGap <= stand && CombatTuning.Gap(n.Position, aim, cold, tc) <= endReach
            && !CombatSystem.OtherFloor(world, i, n.Position, t, next[t].Position) // a target on the floor below is walked to by the stairs (#292)
            && !CombatSystem.PathChasing(cold, t); // one behind bars or a wall is walked round to (#439)
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
    /// Orders.HoldFar), else it stands (engine reading). The player's own attack target (orders 5 and 61) is walked to wherever it is: the original's
    /// chooser returns that focused target without the post test (docs/game/combat.md "Target selection"), and the fight's decision reads Hold only
    /// to skip a path chase in one case, never to stand still ("Leaving a fight"); a crossbowman's closing in reads Hold (docs/game/combat.md), so it
    /// still holds back. True when it holds back from target <paramref name="t"/>.
    /// </summary>
    bool HoldsBack(CharacterCold cold, in CombatSlot st, Vector3 here, Span<CharacterHot> next, int t)
    {
        if (!Orders.Holding(cold)) return false;
        if (st.Picked && !st.Ranged && st.TaskSlot == t && st.TaskGeneration == next[t].Generation) return false;

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
    /// down, and the line to its target is clear (<see cref="OnPathChase"/>): its paths are dropped and its fight state sets the band
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
        if (st.Ranged || holding || st.Down || st.AttackTech >= 0) return false;

        int band = BandTarget(world, in st, t);
        if (OnPathChase(world, i, cold, steer, t, band)) return false;

        // A runner on a clear line runs its path to the target.
        if (cold.CombatRunning) return false;

        SetBand(world, cold, steer, in st, t, band);
        if ((n.Flags & PathFlags) != 0) DropPath(ref n, cold);

        if (world.Tick < st.StunUntil) return true;

        if (!was) steer.Reset();
        steer.Active = true;
        return true;
    }

    /// <summary>
    /// Whether the fighter is on a path chase after <paramref name="t"/> (<see cref="SteerState.ChaseTarget"/>, the original's states 10/11): the
    /// line to it (<see cref="CombatSystem.SteersAt"/>) is blocked, or a chase's timer still runs. The line is tested every tick on a chase
    /// (CombatClassAI update <c>FUN_14060d6a0</c>, in the chase states), else about every <see cref="RepathSeconds"/> as the fighter decides again,
    /// a runner too: a chaser never asks to join its target's fighters (the combat system's deciding step), so guards running in from outdoors or
    /// another floor do not fill the list while one beside the target waits (#776). A blocked line met at a new pick or in the footwork starts a
    /// chase of at least <see cref="PathChaseSeconds"/> (<c>FUN_14060c8b0</c>, timer +0x14c), a holder's not.
    /// </summary>
    bool OnPathChase(World world, int i, CharacterCold cold, SteerState steer, int t, int band)
    {
        bool chasing = steer.ChaseTarget == t;
        if (chasing || world.Tick >= steer.LineTick || steer.Target != band)
        {
            bool clear = combat.SteersAt(world, i, t);
            if (!clear && !chasing)
                steer.PathUntil = Orders.Holding(cold) ? 0 : world.Tick + (long)MathF.Ceiling(PathChaseSeconds / world.TickSeconds);

            // the band tested for, so a runner, which sets no band, tests again only on its cadence
            steer.LineClear = clear;
            steer.LineTick = world.Tick + (long)MathF.Ceiling(RepathSeconds / world.TickSeconds);
            steer.Target = band;
        }

        if (!steer.LineClear)
        {
            steer.ChaseTarget = t;
            return true;
        }

        // The line is clear, but the chase runs until its timer is out.
        if (chasing && world.Tick < steer.PathUntil) return true;

        steer.ChaseTarget = -1;
        return false;
    }

    /// <summary>
    /// The steering band of the fighter's state round <paramref name="t"/>: swinging (AttackState), defending (CombatClassAI slot 12), engaged
    /// (attack start, state 5), else waiting (state 6).
    /// </summary>
    void SetBand(World world, CharacterCold cold, SteerState steer, in CombatSlot st, int t, int band)
    {
        float r = CombatTuning.Footprint(cold);
        float reach = CombatTuning.StandGap(st.Reach, cold.Fighter?.WeaponLength ?? 0, false);
        bool waiting = InWait(world, in st);
        var held = WaitBand(world, t);

        var (near, far, through, limit) = st.ReactTech >= 0 ? (r + 7.5f, r + 19, st.Picked, st.Picked ? 18f : 16f)
            : st.Engaged && !waiting ? (1.4f * r, reach, !st.CircleState, 200f)
            : (held.Near, held.Far, false, 200f);
        (steer.Min, steer.Max) = CombatSteering.Band(near, far, r * (cold.Medical?.ScaleK ?? 1));
        (steer.PowerThrough, steer.Limit, steer.Target) = (through, limit, band);
    }

    /// <summary>
    /// Whether it is in the wait after its own swing: state 6 too, whether or not it is engaged (<c>FUN_14060d6a0</c> forces it while +0x144 is set).
    /// </summary>
    static bool InWait(World world, in CombatSlot st) => st.AttackTech < 0 && st.ReactTech < 0 && world.Tick < st.PauseUntil;

    /// <summary>
    /// The waiting band's near and far ends round target <paramref name="t"/> by the fighters at it (<see cref="CombatSystem.FightersAt"/>,
    /// <see cref="CombatTuning.CircleNear"/>, <see cref="CombatTuning.CircleFar"/>).
    /// </summary>
    (float Near, float Far) WaitBand(World world, int t)
    {
        int around = combat.FightersAt(world, t);
        return (around * 0.25f + CombatTuning.CircleNear, 3 * around + CombatTuning.CircleFar);
    }

    /// <summary>
    /// The look-at it faces (<see cref="SteerState.Face"/>): its target while it swings (the attack start, <c>FUN_140666400</c>; the swing update
    /// <c>FUN_14060a590</c>) and in its footwork (state 5, <c>FUN_1402b3180</c>), the swing its defence follows (the defence update, not during a
    /// block of a rear direction), else the last one (the wait, state 6, writes none).
    /// </summary>
    int LookAt(World world, in CombatSlot st, int t, int last)
    {
        bool waiting = InWait(world, in st);
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
    /// (<c>AITaskSytem::update4Frame</c>, <b>Observed</b>; at most one dropped per call; <see cref="StillNoPath"/>).
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

                if (StillNoPath(world, i, cold, failures[k].Key.Id, endReach)) failures[k] = (failures[k].Key, armed);
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
    /// Whether a lapsed path failure on <paramref name="target"/> is armed again. With synchronous paths (headless runs, tests) the walkability is
    /// asked on the spot (<see cref="PathExists"/>); with path threads (the interactive game) it is dropped, as <see cref="GiveUp"/> drops them: a
    /// search inside the tick stalled it for hundreds of milliseconds, and a chase after the target again notes its failure from the threads' answer.
    /// </summary>
    static bool StillNoPath(World world, int i, CharacterCold cold, CharacterId target, float endReach) =>
        world.SystemOf<MovementSystem>()?.Paths.IsSynchronous != false && !PathExists(world, i, cold, target, endReach);

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
        var water = WaterCost.Of(cold.WaterFactor);
        var path = world.Walkability is IAgentWalkability agent
            ? agent.FindPath(next[i].Position, prev[t].Position, CombatSteering.Radius(cold), water, cold.RoadPreference)
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
/// Everyone defends itself by its 32 Self preservation in the chooser (row 13 on its own attacker, <see cref="Ai.AiSystem.SelfDefencePick"/>),
/// which the alert asks to think at the next tick; a character without a mind waits for one (every character has goals in the original). Nobody
/// else joins here: squad mates and allies come by their own protect goals in the chooser (AiSystem.Fights.cs), which the alert asks to think
/// (<see cref="AskProtectors"/>), and followers and bodyguards by their follow rows' plans (<see cref="Ai.FollowDefence"/>: the alert reaches only
/// the attacked character, <c>FUN_140999f00</c>). A player character's help for others (squad mates, with <see cref="SquadAi.Help"/> allied
/// factions) is its chooser's 15 Protect allies, as an NPC's (AiSystem.Fights.cs), not this pass.
/// </summary>
public sealed class RetaliationSystem(CombatSystem combat) : ITickSystem
{
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
    /// Per attacker slot: the target (slot, generation) it last took, the tick it was taken, and whether that target has been alerted
    /// (<see cref="CombatSystem.Alerts"/>, or a shot for a shooter).
    /// </summary>
    int[] _alertSlot = [], _alertGeneration = [];
    /// <summary>
    /// Per attacker slot: the tick its target was taken.
    /// </summary>
    long[] _alertTick = [];
    /// <summary>
    /// Per attacker slot: whether its target has been alerted.
    /// </summary>
    bool[] _alerted = [];
    /// <summary>
    /// The attackers whose target this pass alerted, in slot order (scratch of <see cref="SlowWorld"/>).
    /// </summary>
    readonly List<int> _fresh = [];

    #region Alerts

    /// <summary>
    /// The answer to being attacked (<c>AI::underMeleeAttack</c>, <see cref="Alert"/>): each attack that alerts its target this pass, then the
    /// protectors those alerts ask to think (<see cref="AskProtectors"/>).
    /// </summary>
    public void SlowWorld(World world)
    {
        var table = world.Characters;
        if (_alertSlot.Length < table.HighWater) GrowAlertArrays(table.HighWater);

        _fresh.Clear();
        for (int a = 0; a < table.HighWater; a++)
        {
            if (TargetOf(table, a) is not { } t)
            {
                _alertSlot[a] = -1;
                continue;
            }

            Track(world, a, t);
            if (_alerted[a]) continue;

            var st = combat.StateOf(new CharacterId(a, table.Next[a].Generation));
            if (!AlertsNow(world, a, t, st)) continue;

            _alerted[a] = true;
            _fresh.Add(a);
            Alert(world, a, t, !st.Ranged);
        }

        foreach (int a in _fresh) AskProtectors(world, table, a, _alertSlot[a]);
    }

    /// <summary>
    /// The slot of the character <paramref name="a"/> attacks (its fight's target), null when it fights no one.
    /// </summary>
    int? TargetOf(CharacterTable table, int a)
    {
        var next = table.Next;
        if (!next[a].Alive || next[a].Task != (byte)CharacterTask.Attack) return null;

        int t = combat.StateOf(new CharacterId(a, next[a].Generation)).TargetSlot;
        return t >= 0 && t < table.HighWater ? t : null;
    }

    /// <summary>
    /// Notes the target <paramref name="t"/> attacker <paramref name="a"/> took: a new one (another slot or generation) is not alerted yet.
    /// </summary>
    void Track(World world, int a, int t)
    {
        int generation = world.Characters.Next[t].Generation;
        if (_alertSlot[a] == t && _alertGeneration[a] == generation) return;

        _alertSlot[a] = t;
        _alertGeneration[a] = generation;
        _alertTick[a] = world.Tick;
        _alerted[a] = false;
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
    /// <c>AI::underMeleeAttack</c> (<c>FUN_140999f00</c>) called from outside a fight, as a failed stealth knock-out calls it on its victim
    /// (<c>FUN_1405c9290</c>): the melee alert of <paramref name="target"/> by <paramref name="attacker"/> (<see cref="Alert"/>). Serial phases only.
    /// </summary>
    internal void UnderMeleeAttack(World world, int attacker, int target) => Alert(world, attacker, target, melee: true);

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
            // The attacker goes on the target's engaged list (FUN_140666e20), where its row 13 and its fight's target choice find it. The shot
            // handler pushes the shooter too and takes it off when it aims elsewhere (FUN_1404371f0), which the fighters whose target it is stand for.
            combat.PushAlerted(world, t, a);

            // Under melee attack (Character +0x2b0) one staying low stands.
            if (tc.Medical is { } medical) medical.StayingLow = false;
            combat.UnderAttackGettingUp(world, t, tc, (ulong)(uint)a);
        }

        // With its fight off it replans, unless it is getting up or is player-side and moving (docs/game/combat.md "Being targeted"): an NPC's AI
        // thinks at the next tick, where its Self preservation takes the attacker up.
        if (!tc.InCombat && !gettingUp && !(tc.IsPlayer && world.Characters.Next[t].Velocity.LengthSquared() > 1))
        {
            DecisionSystem.Replan(tc);
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

    #endregion
}
