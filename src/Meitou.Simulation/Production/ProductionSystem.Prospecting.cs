using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Production;

public sealed partial class ProductionSystem
{
    /// <summary>
    /// The seconds of game time a prospecting takes (<b>Verified</b>: the progress adds the character's update time up to 1, <c>Task_Prospecting</c>
    /// update, docs/game/ai-tasks.md "Prospecting").
    /// </summary>
    public const float ProspectSeconds = 1;

    /// <summary>
    /// The results window's resources in its order, with the names of <c>FUN_140a0c300</c> (<b>Verified</b>; carbon is not listed).
    /// </summary>
    static readonly (int Resource, string Name)[] ProspectResources =
    [
        (MachineData.WaterResource, "Water"), (MachineData.GroundResource, "Fertility"), (ResourceField.StoneResource, "Stone"),
        (ResourceField.IronResource, "Iron"), (ResourceField.CopperResource, "Copper"),
    ];

    /// <summary>
    /// Characters mid-prospecting; <see cref="ProspectStep"/> looks for them only while this is above 0.
    /// </summary>
    int _prospectors;

    /// <summary>
    /// The order: the character stops where it stands and starts the task (serial phase). A character already at it keeps its progress.
    /// </summary>
    void ProspectBegin(World world, ProspectOrder order)
    {
        if (world.Characters.TryResolveNext(order.Character, out int slot))
            PlayerOrders.Give(world, slot, new OrderEntry(TaskType.Prospecting, this), order.Queued);
    }

    /// <summary>
    /// The prospecting's start: the character stops where it stands.
    /// </summary>
    void ProspectStart(World world, int slot)
    {
        var table = world.Characters;
        ref var n = ref table.Next[slot];
        TaskStart.Stand(table, slot);
        n.Task = (byte)CharacterTask.Prospect;
        n.TaskTime = 0;
        table.Cold(slot)!.Prospecting = 0;
        _prospectors++;
    }

    /// <summary>
    /// Once a tick (serial): each prospector adds the tick's game seconds; at <see cref="ProspectSeconds"/> the task ends and the results are published (<see cref="World.Prospected"/>). Another
    /// order (a changed task) or a downed, carried or caged character ends it without a result.
    /// </summary>
    void ProspectStep(World world)
    {
        if (_prospectors <= 0) return;
        var table = world.Characters;
        int found = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref table.Next[i];
            if (!n.Alive || table.Cold(i) is not { Prospecting: { } progress } cold) continue;
            if (n.Task != (byte)CharacterTask.Prospect || !Able(cold))
            {
                if (n.Task == (byte)CharacterTask.Prospect) TaskStart.End(world, i, TaskEnd.Done);
                cold.Prospecting = null;
                continue;
            }

            progress += world.TickSeconds;
            if (progress < ProspectSeconds)
            {
                cold.Prospecting = progress;
                found++;
                continue;
            }

            TaskStart.End(world, i, TaskEnd.Done);
            PublishProspect(world, cold, n.Position);
        }

        _prospectors = found;
    }

    /// <summary>
    /// The window's content for a finished prospecting (<b>Verified</b>/<b>Observed</b> per docs/game/ui-screens.md "Prospecting"), read from the ground at the prospector's feet; the wind is
    /// the spot's weather region's at this moment (<c>FUN_140785f50</c>, read once when the window fills).
    /// </summary>
    void PublishProspect(World world, CharacterCold prospector, System.Numerics.Vector3 at)
    {
        float skill = prospector.Stats?[StatsEnumerated.Science] ?? 0;
        var environment = new List<ProspectLine>(3);
        var (green, arid, swamp) = _resources.Coverage(at.X, at.Z);
        if (green > 0) environment.Add(new("Green", green));
        if (arid > 0) environment.Add(new("Arid", arid));
        if (swamp > 0) environment.Add(new("Swamp", swamp));
        var found = new ProspectLine[ProspectResources.Length];
        for (int k = 0; k < found.Length; k++) found[k] = new(ProspectResources[k].Name, _resources.Yield(ProspectResources[k].Resource, at));
        world.Prospected = new ProspectReport((world.Prospected?.Serial ?? 0) + 1, at, prospector.Name, (int)skill,
            _resources.ZoneName(at.X, at.Z) ?? "", (int)at.Y,
            (int)((skill * 0.01f * 6000 + 2000) * 0.2f), environment, found, SampleProspectMap(world, at, skill * 0.01f),
            world.Weather?.At(at.X, at.Z)?.WindSpeed ?? 0);
    }

    /// <summary>
    /// The map's samples (<b>Verified</b>, <c>FUN_14049d850</c>): <c>int(s x 246 + 6)</c> cells a side over the square of half-side <c>s x 6000 + 2000</c> round the spot, s = science / 100. The field resources
    /// are read at every cell's centre by the spot's resources record (<c>uses_resources</c>), the rock ones list the rocks in range (<c>FUN_14049cd60</c>).
    /// </summary>
    ProspectMap SampleProspectMap(World world, System.Numerics.Vector3 at, float s)
    {
        int size = Math.Max(1, (int)(s * 246 + 6));
        float half = s * 6000 + 2000, extent = 2 * half, x0 = at.X - half, z0 = at.Z - half, cell = extent / size;
        var walk = world.Walkability;
        var heights = new float[size * size];
        var fields = new float[]?[ProspectResources.Length];
        var rocks = new IReadOnlyList<ProspectRock>[ProspectResources.Length];
        for (int k = 0; k < fields.Length; k++)
        {
            int resource = ProspectResources[k].Resource;
            if (resource is MachineData.WaterResource or MachineData.GroundResource or ResourceField.StoneResource)
            {
                fields[k] = new float[size * size];
                rocks[k] = [];
                continue;
            }

            var near = _nodes?.Near(resource, at.X, at.Z, extent * ProspectMap.RockRange) ?? [];
            rocks[k] = near.Select(n => new ProspectRock(n.Position.X, n.Position.Z, _resources.NodeYield(resource, n.Position))).ToArray();
        }

        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < size; col++)
            {
                float x = x0 + (col + 0.5f) * cell, z = z0 + (row + 0.5f) * cell, y = walk.TerrainHeight(x, z);
                int i = row * size + col;
                heights[i] = y;
                for (int k = 0; k < fields.Length; k++)
                    if (fields[k] is { } field) field[i] = _resources.FieldYield(ProspectResources[k].Resource, new(x, y, z), at);
            }
        }

        return new ProspectMap(size, x0, z0, extent, heights, fields, rocks);
    }
}