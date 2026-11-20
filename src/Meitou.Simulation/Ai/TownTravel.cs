using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// What a travel step asks of the serial step (<see cref="AiMind.TravelAsk"/>).
/// </summary>
public enum TravelAsk : byte
{
    /// <summary>
    /// Nothing.
    /// </summary>
    None = 0,
    /// <summary>
    /// The squad has no town: the running package starts again (vtable <c>+0x30</c>).
    /// </summary>
    Restart = 1,
    /// <summary>
    /// No path to the town: the running package's path-impossible call (vtable <c>+0x10</c>).
    /// </summary>
    Unreachable = 2,
}

/// <summary>
/// How an engine goal's travel step stands (<see cref="TownTravel.StepGoal"/>).
/// </summary>
internal enum GoalTravel : byte
{
    /// <summary>
    /// Walking to the town, or standing at its waypoint.
    /// </summary>
    Walking,
    /// <summary>
    /// In the town (state 36).
    /// </summary>
    Arrived,
    /// <summary>
    /// The squad has no town (the running package starts again).
    /// </summary>
    NoTown,
    /// <summary>
    /// The walk ended with no path to the town.
    /// </summary>
    NoPath,
}

/// <summary>
/// <c>Task_TravelToTargetTown</c>, rows 53 Travelling and 130 Travelling quickly (start <c>FUN_14033ad80</c>, update <c>FUN_14033ae40</c>, speed
/// functor <c>FUN_140342a30</c>, <b>Verified</b>; docs/game/ai-tasks.md "Town goals"): the walk to the squad's town (<see cref="SquadTown.Of"/>) or its
/// detour nest at the functor's speed, and the task's two exits. The think acts it out on the character's own mind; the package calls of the exits
/// are made by the serial step in slot order (<see cref="Step"/>).
/// </summary>
internal sealed class TownTravel(PopulationData data)
{
    #region The walk

    /// <summary>
    /// How the last walk stands (<see cref="WalkOver"/>).
    /// </summary>
    enum WalkOutcome : byte
    {
        /// <summary>
        /// Still walking, or not walking for this task.
        /// </summary>
        Walking,
        /// <summary>
        /// Over: at its goal, or stopped by another system.
        /// </summary>
        Over,
        /// <summary>
        /// Over with no path to the goal.
        /// </summary>
        NoPath,
    }

    /// <summary>
    /// The worker thread's functionality cache for the town's gates (<see cref="TownExit.Of"/>), of the records of <see cref="s_gatesOf"/>.
    /// </summary>
    [ThreadStatic] static Dictionary<string, GameRecord?>? s_gates;

    /// <summary>
    /// The database <see cref="s_gates"/> holds records of: a thread outlives a world, and another world's data may differ.
    /// </summary>
    [ThreadStatic] static GameDatabase? s_gatesOf;

    /// <summary>
    /// The functor's caps of modes 1 (<c>DAT_1416b3ac4</c>) and 2 (<c>DAT_1416b9a58</c>), and the share of a slower member's speed it lowers to
    /// (<c>0x14168a25c</c>).
    /// </summary>
    const float JogCap = 55, RunCap = 999, SlowerShare = 0.9f;

    /// <summary>
    /// Whether the character acts out a travel step (53, 130): one the AI walks in full, or the one 192's act-out carries (<see cref="StepGoal"/>, its
    /// walk on row 192). The movement's carrier jog does not apply to it (<see cref="MovementSystem.CarriesOutAtWalk"/>) and its pace cap does
    /// (<see cref="CharacterCold.PaceCap"/>). Any thread.
    /// </summary>
    internal static bool Runs(CharacterCold cold) =>
        cold.Ai is { Duty: null, Step: TaskType.TravelToTargetTown or TaskType.TravelToTargetTownFast }
        || cold.Ai is { Duty.Task.Type: TaskType.GoToSomewhereForDeliveringSlaves }
            && cold.Running is { Row: TaskType.GoToSomewhereForDeliveringSlaves };

    /// <summary>
    /// The update on the think (own slot only): the town, else the restart exit; the walk's end read (the detour or the path-impossible exit); then
    /// the walk to the town's waypoint at the functor's mode and cap, standing once there.
    /// </summary>
    public void Act(AiSystem system, World world, int slot, AiMind mind, Blackboard board, TaskType row)
    {
        var cold = world.Characters.Cold(slot)!;
        cold.PaceCap = null;
        if (Town(world, mind, board) is not { } town)
        {
            Exit(world, slot, mind, TravelAsk.Restart);
            return;
        }

        if (WalkOver(world, slot, mind, cold) == WalkOutcome.NoPath && !Detoured(world, slot, mind, town)) return;
        if (Town(world, mind, board) is not { } aim) return;

        Walk(system, world, slot, mind, cold, board, aim, row);
    }

    /// <summary>
    /// The travel row an engine goal's act-out carries (192's 53; serial, the same task as <see cref="Act"/>): <see cref="GoalTravel.Arrived"/> once
    /// state 36 holds (<see cref="SquadTown.Reached"/>); <see cref="GoalTravel.NoTown"/> when the squad has none; <see cref="GoalTravel.NoPath"/> when
    /// a walk it started (<paramref name="walked"/>) ended with no path to the town; else it walks to the town's waypoint on <paramref name="row"/>
    /// with the goal's provenance at the functor's mode and cap, and stands once there.
    /// </summary>
    internal GoalTravel StepGoal(AiSystem system, World world, int slot, Squad squad, Blackboard board, TaskType row, bool walked)
    {
        var cold = world.Characters.Cold(slot)!;
        var town = SquadTown.Of(world, squad, board, homeFallback: true);
        if (town.IsNone || AiSystem.TownCircle(data, world, town) is null) return GoalTravel.NoTown;
        if (SquadTown.Reached(world, data, slot, squad, board)) return GoalTravel.Arrived;

        ref var n = ref world.Characters.Next[slot];
        var (start, cap) = Speed(world, slot, cold, squad, board, TaskType.TravelToTargetTown);
        byte mode = UnderThreat(world, slot, cold, start);
        cold.PaceCap = cap;
        if (n.Task == (byte)CharacterTask.GoTo && cold.Running?.Row == row)
        {
            n.Mode = mode;
            return GoalTravel.Walking;
        }

        if (walked && NoPath(cold)) return GoalTravel.NoPath;

        var point = Waypoint(system, world, town);
        if (Vector2.DistanceSquared(new Vector2(n.Position.X, n.Position.Z), point) <= system.Settings.PostSlack * system.Settings.PostSlack)
        {
            TaskStart.Stand(world.Characters, slot);
            return GoalTravel.Walking;
        }

        cold.PathFailed = false;   // a failure from before is not this walk's
        TaskStart.Walk(world, slot, point, mode, row, TaskSource.Goal);
        return GoalTravel.Walking;
    }

    /// <summary>
    /// The town walked to: the detour once set, else the squad's town with the home fallback; null when it is none or not in the world
    /// (<c>FUN_1409f9100</c>, <c>FUN_140792980</c> resolve nothing).
    /// </summary>
    CampaignTown? Town(World world, AiMind mind, Blackboard board)
    {
        var town = mind.TravelDetour.IsNone ? SquadTown.Of(world, mind.Squad, board, homeFallback: true) : mind.TravelDetour;
        return !town.IsNone && AiSystem.TownCircle(data, world, town) is not null ? town : null;
    }

    /// <summary>
    /// How the last walk stands: one with no path (CharMovement slots 0x50 and 0x48, path state 3) is <see cref="WalkOutcome.NoPath"/>; one
    /// finished at its goal marks the waypoint reached (<see cref="AiMind.ArrivedAt"/>).
    /// </summary>
    static WalkOutcome WalkOver(World world, int slot, AiMind mind, CharacterCold cold)
    {
        ref var n = ref world.Characters.Next[slot];
        if (!mind.Walking || n.Task == (byte)CharacterTask.GoTo) return WalkOutcome.Walking;

        mind.Walking = false;
        if (NoPath(cold)) return WalkOutcome.NoPath;

        if (cold.WalkEnd == WalkEnd.Finished)
        {
            mind.ArrivedAt = n.Goal;
            mind.ArrivedFrom = new Vector2(n.Position.X, n.Position.Z);
        }

        return WalkOutcome.Over;
    }

    /// <summary>
    /// Whether the movement found no way to the goal for good: no path at all (<see cref="CharacterCold.PathFailed"/>), or a goal the navmesh does
    /// not reach, whose route ends in a provisional tail (docs/game/pathfinding.md "Long routes") the walker gave up on after its retries
    /// (<see cref="MovementSystem.ProvisionalBlockedLimit"/>; engine choice for the original's retries past the goal offsets).
    /// </summary>
    static bool NoPath(CharacterCold cold) =>
        cold.PathFailed || cold.WalkEnd == WalkEnd.Failed && cold.ProvisionalBlocked > MovementSystem.ProvisionalBlockedLimit;

    /// <summary>
    /// The walk to the town's waypoint (<c>FUN_1405c68a0</c> with <c>getPosWithinChoppingRange</c> <c>FUN_14059bf30</c>: a town's vfunc 0x38,
    /// <see cref="TownExit"/>; a nest's centre) at the functor's mode and cap; standing once its walk ended there.
    /// </summary>
    void Walk(AiSystem system, World world, int slot, AiMind mind, CharacterCold cold, Blackboard board, CampaignTown town, TaskType row)
    {
        ref var n = ref world.Characters.Next[slot];
        var point = Waypoint(system, world, town);
        var here = new Vector2(n.Position.X, n.Position.Z);
        if (mind.ArrivedThere(point, here) || Vector2.DistanceSquared(here, point) <= system.Settings.PostSlack * system.Settings.PostSlack)
        {
            AiSystem.Become(world, slot, CharacterTask.Idle);
            return;
        }

        var (start, cap) = Speed(world, slot, cold, mind.Squad, board, row);
        cold.PaceCap = cap;
        if (n.Task != (byte)CharacterTask.GoTo) cold.PathFailed = false;   // a failure from before is not this walk's

        AiSystem.WalkTo(world, slot, point, UnderThreat(world, slot, cold, start));
        mind.ArrivedAt = null;
        mind.Walking = n.Task == (byte)CharacterTask.GoTo;
    }

    /// <summary>
    /// The walk's run under threat (the tail of <c>FUN_1405c68a0</c>, <b>Verified</b>): the mover keeps the start's mode, but runs (mode 2,
    /// <see cref="SpeedMode.Free"/>) while the sensed others' aggression (<see cref="Perception.SenseBlock.Aggression"/>, AI <c>+0xb8</c>), its
    /// own goal's (<see cref="TaskAggression.OfGoal"/>, status <c>+0xac</c>) or the threat to its faction
    /// (<see cref="Perception.SenseBlock.ThreatToAllies"/>, AI <c>+0xb4</c>) is above 0. The pace cap stays the functor's.
    /// </summary>
    static byte UnderThreat(World world, int slot, CharacterCold cold, byte mode)
    {
        var senses = Bodies.CharacterFacts.Senses(world, slot);
        bool threat = senses is { Aggression: > 0 } or { ThreatToAllies: > 0 } || TaskAggression.OfGoal(cold) > 0;

        return threat ? SpeedMode.Free : mode;
    }

    /// <summary>
    /// The town's waypoint: a nest's centre, any other town's point outside its gates (Town vfunc 0x38, <see cref="TownExit.Of"/>).
    /// </summary>
    Vector2 Waypoint(AiSystem system, World world, CampaignTown town)
    {
        var (centre, _) = AiSystem.TownCircle(data, world, town)!.Value;
        if (town.Nest > 0) return centre;

        return TownExit.Of(world, data, system.GateRegions, town, Gates());
    }

    /// <summary>
    /// This thread's gate cache for the world's data, made anew for another database.
    /// </summary>
    Dictionary<string, GameRecord?> Gates()
    {
        if (s_gates is null || !ReferenceEquals(s_gatesOf, data.Db)) (s_gates, s_gatesOf) = (new(StringComparer.Ordinal), data.Db);

        return s_gates;
    }

    #endregion

    #region Speed

    /// <summary>
    /// The functor <c>FUN_140342a30</c> on the start's mode (<see cref="Mode"/>): 1 a jog capped at 55, 2 a run at 999, any other the walk at the
    /// mover's walk cap (<c>+0xc0</c>, <see cref="CharacterHot.WalkSpeed"/>); then over the squad's members, the traveller included, in member
    /// order (engine reading of the platoon's ordered set), each one in the world and not carried (<c>+0x3d4</c>) whose stats' top speed
    /// (CharStats <c>+0x17c</c>, <see cref="CharacterCold.StatsTopSpeed"/>) is below the cap lowers it to 0.9 of that speed. The mode is the
    /// movement's (walk, jog, run); the cap its pace (<see cref="CharacterCold.PaceCap"/>).
    /// </summary>
    static (byte Mode, float Cap) Speed(World world, int slot, CharacterCold cold, Squad squad, Blackboard board, TaskType row)
    {
        int mode = Mode(cold, squad, board, row);
        float cap = mode switch
        {
            1 => JogCap,
            2 => RunCap,
            _ => world.Characters.Next[slot].WalkSpeed,
        };

        var table = world.Characters;
        foreach (var member in squad.Members)
        {
            if (!table.TryResolveNext(member, out int m) || table.Cold(m) is not { CarriedBy.IsNone: true } c) continue;
            if (c.StatsTopSpeed is { } top && top < cap) cap = top * SlowerShare;
        }

        byte moves = mode switch
        {
            1 => SpeedMode.Run,
            2 => SpeedMode.Free,
            _ => SpeedMode.Walk,
        };
        return (moves, cap);
    }

    /// <summary>
    /// The mode: the start keeps the running package's speed mode (package slot 3, <see cref="AiSystem.PackageSpeed"/>), even its 4, else the row's
    /// own (0 for 53, 1 for 130); the functor then has a carrier's 0 become 1, the squad template's force speed replace it when not 4
    /// (<see cref="Squad.ForceSpeed"/>, platoon <c>+0xb0</c>), and an escaped slave (slave state 2, <c>FUN_1405c8aa0</c>) or a squad that is not
    /// intact (<c>FUN_1407a5be0</c>) run (2).
    /// </summary>
    static int Mode(CharacterCold cold, Squad squad, Blackboard board, TaskType row)
    {
        int mode = board.Current is { } running ? AiSystem.PackageSpeed(running) : row == TaskType.TravelToTargetTownFast ? 1 : 0;
        if (mode == 0 && !cold.Carrying.IsNone) mode = 1;
        if (squad.ForceSpeed != Squad.NoForceSpeed) mode = squad.ForceSpeed;
        if (cold.SlaveState == 2 || !board.Intact) mode = 2;

        return mode;
    }

    #endregion

    #region Exits

    /// <summary>
    /// Set by a think that left a <see cref="AiMind.TravelAsk"/>; the serial step looks for them only then.
    /// </summary>
    int _asked;

    /// <summary>
    /// A walk that ended with no path to the town (<see cref="WalkOutcome.NoPath"/>): to a town of type 0 (a nest, Town <c>+0xd8</c> the TOWN
    /// record's <c>type</c>, <c>FUN_1409353c0</c>) with no detour yet it takes the nearest nest of its faction for one (<see cref="Detour"/>; none:
    /// it stands and tries again); otherwise the path-impossible exit. True when the walk goes on.
    /// </summary>
    bool Detoured(World world, int slot, AiMind mind, CampaignTown town)
    {
        if (mind.TravelDetour.IsNone && IsNest(world, town))
        {
            mind.TravelDetour = Detour(world, slot, town);
            if (mind.TravelDetour.IsNone) AiSystem.Become(world, slot, CharacterTask.Idle);
            return !mind.TravelDetour.IsNone;
        }

        Exit(world, slot, mind, TravelAsk.Unreachable);
        return false;
    }

    /// <summary>
    /// Whether the town's TOWN record is of type 0 (<see cref="TownType.Nest"/>).
    /// </summary>
    bool IsNest(World world, CampaignTown town)
    {
        if (town.Site >= 0) return town.Site < data.Sites.Count && data.Sites[town.Site].Town.Type == TownType.Nest;
        if (town.Nest <= 0 || world.SystemOf<PopulationSystem>() is not { } population) return false;

        return population.WildSites[town.Nest - 1].Nest is { Type: TownType.Nest };
    }

    /// <summary>
    /// The detour: the nest nearest the character (<see cref="NearestNest"/>) of its faction (AI slot 1), the target left out.
    /// </summary>
    static CampaignTown Detour(World world, int slot, CampaignTown target)
    {
        int faction = world.Characters.Cold(slot)!.Faction;
        var at = new Vector2(world.Characters.Previous[slot].Position.X, world.Characters.Previous[slot].Position.Z);

        return NearestNest(world, at, faction, target);
    }

    /// <summary>
    /// <c>FUN_140927ad0</c> (<b>Verified</b>): over the town list's second list, the nests (<see cref="PopulationSystem.PlacedNests"/>), the one
    /// nearest <paramref name="at"/> (X/Z) whose faction (Town vfunc 0x58) is <paramref name="faction"/>, <paramref name="skip"/> left out; none
    /// without one.
    /// </summary>
    internal static CampaignTown NearestNest(World world, Vector2 at, int faction, CampaignTown skip)
    {
        if (world.SystemOf<PopulationSystem>() is not { } population) return CampaignTown.None;

        var best = CampaignTown.None;
        float bestD = float.MaxValue;
        foreach (var nest in population.PlacedNests)
        {
            var town = CampaignTown.OfNest(nest.Site);
            if (town == skip || population.NestFaction(nest.Site) != faction) continue;

            float d = Vector2.DistanceSquared(at, nest.Centre);
            if (d < bestD) (best, bestD) = (town, d);
        }

        return best;
    }

    /// <summary>
    /// The task ends asking a replan (CharBody slot 5, <c>+0x26d</c>): the detour goes with it (a new task starts without one), the plan is reset, it
    /// stands, and the serial step makes the package call <paramref name="ask"/> names.
    /// </summary>
    void Exit(World world, int slot, AiMind mind, TravelAsk ask)
    {
        mind.TravelDetour = CampaignTown.None;
        mind.TravelAsk = ask;
        mind.ResetPlan();
        AiSystem.Become(world, slot, CharacterTask.Idle);
        Interlocked.Exchange(ref _asked, 1);
    }

    /// <summary>
    /// The serial step: the asks the thinks left, in slot order. A restart starts the running package again (its start, vtable <c>+0x30</c>); a path
    /// impossible calls the running package's vtable <c>+0x10</c>, through <c>notifyPathImpossible</c> (<c>FUN_140796ed0</c> → <c>FUN_140268a20</c>,
    /// with the town) and again with an empty hand, which only the trader's (<c>FUN_140286730</c>, a new start: a new town) changes anything for
    /// (<see cref="AiSystem.PathImpossible"/>).
    /// </summary>
    public void Step(AiSystem system, World world)
    {
        if (Interlocked.Exchange(ref _asked, 0) == 0) return;

        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Cold(s)?.Ai is not { TravelAsk: not TravelAsk.None } mind) continue;

            var ask = mind.TravelAsk;
            mind.TravelAsk = TravelAsk.None;
            if (ask == TravelAsk.Restart) system.RestartPackage(world, mind.Squad);
            else system.PathImpossible(world, mind.Squad);
        }
    }

    /// <summary>
    /// Forgets the detour of a mind that no longer acts out a travel step (its task ended).
    /// </summary>
    public static void Leave(AiMind mind) => mind.TravelDetour = CampaignTown.None;

    #endregion
}
