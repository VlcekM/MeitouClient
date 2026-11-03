using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Production;

// The machines of NPC towns (docs/game/buildings-production.md "NPC town machines"): a town building an NPC worker (goals 189 / 190, AiSystem Work) stands at is a machine like a rock or a
// training dummy is: no registry site, so neither drawn, saved nor built; a complete virtual site of the town's faction, made when the first worker arrives. The workers are the AI's: the
// decision step keeps AiMind.WorkOperate (Task_OperateMachine, row 87) or AiMind.CollectFrom (Task_EmptyMachine, row 92) while they are at the building, and this system applies them.
public sealed partial class ProductionSystem
{
    /// <summary>The prefix of a town building's machine site id (followed by the building's run-time key).</summary>
    public const string TownWorkPrefix = "townwork:";

    readonly Dictionary<string, BuildSite> townWorkSites = new(StringComparer.Ordinal);

    /// <summary>The site id of a town building's machine.</summary>
    public static string TownWorkId(TownBuilding building) => TownWorkPrefix + building.Key;

    /// <summary>Whether a site id names a town building's machine (<see cref="TownWorkId"/>).</summary>
    public static bool IsTownWork(string id) => id.StartsWith(TownWorkPrefix, StringComparison.Ordinal);

    /// <summary>The virtual site of a town building made by <see cref="TownWork"/>, else null.</summary>
    BuildSite? TownWorkSite(string id) => townWorkSites.GetValueOrDefault(id);

    /// <summary>
    /// The machine of a town building (made on the first call: empty, owned by the town's faction <paramref name="owner"/>), null for a building that neither works nor stores. Serial
    /// phases only.
    /// </summary>
    public Machine? TownWork(World world, TownBuilding building, int owner)
    {
        string id = TownWorkId(building);
        if (world.Machines.Find(id) is { } known) return known;
        if (!townWorkSites.TryGetValue(id, out var site))
            townWorkSites[id] = site = new BuildSite
            {
                Id = id, Record = building.BuildingId, Name = building.Name, Position = building.Position, Owner = owner, Materials = [], Complete = true,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, building.Yaw),
            };
        return Ensure(world, site);
    }

    /// <summary>The machine a town building has made, null before a worker came.</summary>
    public static Machine? TownWorkOf(World world, TownBuilding building) => world.Machines.Find(TownWorkId(building));

    /// <summary>
    /// The workers of town machines at their building this tick (serial, before the players' operators): the AI's workers that stand at their building doing row 87 operate it (a producer
    /// only: a research bench counts for the player's research alone, and a crafting bench with no queue refuses an operator, <c>dontNeedWorkRightNow</c>, so both are pretence), those
    /// doing row 92 empty its output into their inventory.
    /// </summary>
    void TownWorkers(World world, float dt)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Ai: { WorkOperate: not null } or { CollectFrom: not null } } cold) continue;
            ref var n = ref table.Next[i];
            var mind = cold.Ai!;
            var building = mind.WorkOperate ?? mind.CollectFrom!;
            if (!Able(cold) || mind.Behaviour != Ai.AiBehaviour.Work || !AtTownWork(n, building)) continue;
            if ((world.Machines.Find(TownWorkId(building)) ?? TownWork(world, building, mind.WorkOwner)) is not { } m || !Live(world, m)) continue;
            if (mind.CollectFrom is not null) TakeOutput(world, cold, m);
            else if (mind.WorkOperate is not null) TownOperate(world, i, cold, m, dt);
        }
    }

    bool AtTownWork(in CharacterHot n, TownBuilding building)
    {
        float dx = n.Position.X - building.Position.X, dz = n.Position.Z - building.Position.Z;
        return dx * dx + dz * dz <= Ai.AiSystem.WorkReach * Ai.AiSystem.WorkReach * 1.5f * 1.5f && (CharacterTask)n.Task is CharacterTask.Idle or CharacterTask.Rest;
    }

    /// <summary>One worker's frame at a town producer: <see cref="ProduceStep"/> at the worker's skill, and the operating XP (the same lines as a player's operator).</summary>
    void TownOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        if (!m.Data.IsProducer || m.Data.IsResearch || m.Data.IsCrafting || m.Farm is not null || m.Output is null) return;
        m.Operators++;
        var stat = (StatsEnumerated)m.Data.Stat;
        float skill = m.Data.Stat > 0 ? Effective(cold, stat) : -1;
        float s = skill < 0 ? 1 : Lerp(0.5f, 1.4f, skill / 100);
        cold.Operating = ProduceStep(world, m, s, dt);
        if (m.Data.Stat > 0) cold.UseSkill(stat, world.Tick);
        if (m.Data.Stat > 0 && cold.Stats is { } stats && cold.Race is { } race) xp.Continuous(stats, race, stat, dt, OperateXp(m.Data.Stat));
    }

    /// <summary>
    /// <c>Task_EmptyMachine</c> (rows 92 and 284, <c>FUN_14033cd10</c>, <b>Verified</b>) at a machine with an output: the output's items go into the character's inventory (the worn backpack
    /// when the main grid is full) while there is room, a unit at a time; a producer whose status was 1 (output full) then goes back to 2.
    /// </summary>
    void TakeOutput(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv || m.Output is not { Count: > 0 } output) return;
        bool moved = false;
        while (output.Count > 0 && items.Create(output.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit) && inv.Add(unit))
        {
            output.Count--;
            moved = true;
        }
        if (!moved) return;
        if (m.Status == 1) m.Status = 2;
        world.Machines.Touch();
    }
}
