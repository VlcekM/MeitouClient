using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The NPC orders the dialogue and a building purchase give (docs/game/dialogue.md "Effects", docs/simulation.md "Order queue"): 19 Going home,
/// 22 Patrolling, 67 Moving fast, 280 Running away (walks), and 101 Assault and 248 Attacking squad (fights on the member of the target's squad
/// the finder picks, <see cref="AiSystem.AssaultTargetFor"/>). They are checked and started here; the act-outs are the movement's walk and
/// follow and the combat system's fight. 22, 101 and 248 are planned again after each step (<see cref="IOrderOwner.Repeats"/>). Serial phases.
/// </summary>
sealed class EngineOrders(AiSystem ai, PopulationData data) : IOrderOwner
{
    #region Checks

    /// <summary>
    /// The order's check: a fight needs a fighter able to fight (else it waits queued) and a member its finder picks (else it leaves the queue); a
    /// move to a character needs it there and not the character itself.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        if (Fights(order.Row))
        {
            if (Pick(world, slot, order).IsNone) return OrderCheck.Gone;
            return Able(world.Characters.Cold(slot)!) ? OrderCheck.Run : OrderCheck.Blocked();
        }

        if (order.Row != TaskType.MoveOnFreeWillFast) return OrderCheck.Run;

        bool there = world.Characters.TryResolveNext(order.Target, out int t) && t != slot;
        return there ? OrderCheck.Run : OrderCheck.Gone;
    }

    /// <summary>
    /// 22 goes on until its list B ends it (the row never finishes); 101 and 248 while their finder picks someone.
    /// </summary>
    bool IOrderOwner.Repeats(World world, int slot, OrderEntry order) =>
        order.Row == TaskType.Patrol || Fights(order.Row) && !Pick(world, slot, order).IsNone;

    /// <summary>
    /// Whether the row is one of the Assault orders' fights.
    /// </summary>
    static bool Fights(TaskType row) => row is TaskType.DefeatSquad or TaskType.DefeatSquadLimitChaseRange;

    /// <summary>
    /// The member of the order's target's squad the character attacks now (<see cref="CharacterId.None"/>: none).
    /// </summary>
    CharacterId Pick(World world, int slot, OrderEntry order)
    {
        var squad = world.Squads.Find(world.Characters.Cold(slot)!.SquadId);
        return ai.AssaultTargetFor(world, slot, squad?.Ai, order.Target);
    }

    /// <summary>
    /// Whether the character can fight: a fighter with a body, awake and not carried.
    /// </summary>
    static bool Able(CharacterCold cold) =>
        cold is { Fighter.CanAttack: true, Stats: not null, Race: not null, Medical.Incapacitated: false } && cold.CarriedBy.IsNone;

    #endregion

    #region Starts

    /// <summary>
    /// Starts the order's step: the fight on the member picked, or the walk (22: the squad's patrol, its leader walking, the others following).
    /// </summary>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (Fights(order.Row))
        {
            var pick = Pick(world, slot, order);
            world.SystemOf<CombatSystem>()?.EngageOrder(world, slot, pick, order.Row, "its ordered assault (dialogue)");
            return;
        }

        if (order.Row == TaskType.Patrol && FollowLeader(world, slot)) return;

        if (Goal(world, slot, order) is not { } goal) return;
        byte mode = order.Row is TaskType.GoHomebuilding or TaskType.Patrol ? SpeedMode.Walk : SpeedMode.Free;
        var walk = new TaskRequest(CharacterTask.GoTo, order.Row, TaskSource.Order) { Target = order.Target, Goal = goal, Mode = mode };
        TaskStart.Start(world, slot, walk);
    }

    /// <summary>
    /// A patrolling member that is not its squad's leader follows the leader on the order's row; false for the leader.
    /// </summary>
    static bool FollowLeader(World world, int slot)
    {
        var table = world.Characters;
        if (world.Squads.Find(table.Cold(slot)!.SquadId) is not { } squad || squad.Leader.IsNone || squad.Leader == table.IdOf(slot)) return false;
        if (!table.TryResolveNext(squad.Leader, out _)) return false;

        var follow = new TaskRequest(CharacterTask.Follow, TaskType.Patrol, TaskSource.Order) { Target = squad.Leader };
        TaskStart.Start(world, slot, follow);
        return true;
    }

    /// <summary>
    /// Where the order walks: 280 to its point, 67 to its target, 19 home, 22 to the squad's patrol point (<see cref="Squad.PatrolPoint"/>, which
    /// the population's patrol step draws); null when there is nowhere (22 before its first point is drawn).
    /// </summary>
    Vector2? Goal(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var squad = world.Squads.Find(table.Cold(slot)!.SquadId);
        switch (order.Row)
        {
            case TaskType.RunAwayForced:
                return new Vector2(order.Point.X, order.Point.Z);
            case TaskType.MoveOnFreeWillFast:
                if (!table.TryResolveNext(order.Target, out int t)) return null;
                return new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
            case TaskType.GoHomebuilding:
                return Home(squad);
            default:
                return squad?.PatrolPoint;
        }
    }

    /// <summary>
    /// The squad's home: its home building, else its town's centre, else its home area's centre; null without a squad.
    /// </summary>
    Vector2? Home(Squad? squad)
    {
        if (squad is null) return null;
        if ((squad.Ai?.Home ?? squad.HomeBuilding) is { } home) return new Vector2(home.Position.X, home.Position.Z);
        if ((uint)squad.Town < (uint)data.Sites.Count) return new Vector2(data.Sites[squad.Town].Position.X, data.Sites[squad.Town].Position.Z);

        return squad.HomeCentre;
    }

    #endregion
}
