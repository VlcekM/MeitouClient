using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Kidnap;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

/// <summary>
/// The <c>obey</c> mode of <c>meitou-tools probe</c>: an enslaved player character stands (its orders cleared), sneaks, or is ordered to walk to a
/// point, then every 20 ticks its slave state, owner, task, shackles and workplace with its stat, and how the owner faction's NPCs near it see it
/// (#813: guards who let an obedient slave walk off; #812: a slave's work XP).
/// </summary>
static class ObeyProbe
{
    /// <summary>
    /// The owner-faction NPCs listed each round: the nearest this many within their sight range.
    /// </summary>
    const int Lookers = 4;

    /// <summary>
    /// Runs the case <paramref name="how"/> (<c>stand</c>, <c>sneak</c> or <c>walk</c> to <paramref name="to"/>) on the first character named like
    /// <paramref name="name"/> and prints <paramref name="rounds"/> rounds.
    /// </summary>
    public static void Run(ProbeWorld probe, string name, string how, Vector2? to, int rounds)
    {
        if (probe.Named(name) is not [int slave, ..])
        {
            Console.WriteLine($"nobody named {name}");
            return;
        }

        Start(probe, slave, how, to);
        for (int round = 0; round < rounds; round++)
        {
            PrintSlave(probe, slave);
            PrintLookers(probe, slave);
            probe.PrintMessages();
            probe.Tick(20);
        }
    }

    /// <summary>
    /// Clears the slave's orders and sneak, then sets the case's: the Sneak order, or a move order to the point.
    /// </summary>
    static void Start(ProbeWorld probe, int slave, string how, Vector2? to)
    {
        var world = probe.World;
        var c = probe.Characters.Cold(slave)!;
        c.OrderQueue.Clear();
        c.Orders &= ~CharacterOrders.Sneak;
        Meitou.Simulation.Ai.TaskStart.Stand(probe.Characters, slave);

        if (how == "sneak") c.Orders |= CharacterOrders.Sneak;
        if (how != "walk" || to is not { } point) return;

        var target = new Vector3(point.X, world.Walkability.TerrainHeight(point.X, point.Y), point.Y);
        world.Commands.Enqueue(new MoveOrder([probe.Characters.IdOf(slave)], target) { Tick = world.Tick });
    }

    /// <summary>
    /// The slave's line: state, master squad, owner and its distance, task and row, the order in hand, place and shackles.
    /// </summary>
    static void PrintSlave(ProbeWorld probe, int slave)
    {
        var world = probe.World;
        var c = probe.Characters.Cold(slave)!;
        var owner = ObedientSlave.OwnerOf(world, c);
        var at = probe.Characters.Next[slave].Position;
        var ownerAt = owner is { } o ? probe.Characters.Next[o.Slot].Position : (Vector3?)null;
        string ownerText = ownerAt is { } p ? $"{owner!.Value.Slot} at {p:F0} gap={Vector3.Distance(at, p):F0}" : "none";
        var order = c.OrderQueue.Front;
        string orderText = order is null ? "none" : $"{order.Row} {order.Point:F0}";
        var shackles = c.Inventory?.Items.Find(i => i.Section == "boots") ?? c.Inventory?.Items.Find(i => i.Section == "armour");

        Console.WriteLine($"tick {world.Tick} {probe.Who(slave)} slave={c.SlaveState} master={c.SlaveMaster} owner={ownerText} "
            + $"row={c.Running?.Row} order={orderText} sneak={(c.Orders & CharacterOrders.Sneak) != 0} outdoors={world.Sight.Outdoors(slave)} "
            + $"shackles={shackles?.Record}/{shackles?.Lock is not null}{WorkText(world, c)}");
    }

    /// <summary>
    /// The workplace the slave's 189 / 190 holds, whether it operates (189) and the level of the functionality's <c>stat used</c> (#812).
    /// </summary>
    static string WorkText(World world, CharacterCold c)
    {
        if (c.Ai is not { Behaviour: Meitou.Simulation.Ai.AiBehaviour.Work, WorkBuilding: { } b } mind) return "";

        var stat = (StatsEnumerated)(world.SystemOf<ProductionSystem>()?.Data(b.BuildingId)?.Stat ?? 0);
        string level = stat > 0 && c.Stats is { } stats ? $" {stat}={stats[stat]:F4}" : "";
        bool real = mind.Goal?.Task.Type == Meitou.Data.Gameplay.Ai.TaskType.AutoLabouringMines;

        return $" work={b.Name}/{(real ? "real" : "pretend")}{level}";
    }

    /// <summary>
    /// The nearest NPCs of the slave's owner faction within their sight range: distance and the view factor of the sight test with the ray.
    /// </summary>
    static void PrintLookers(ProbeWorld probe, int slave)
    {
        var t = probe.Characters;
        var c = t.Cold(slave)!;
        var at = t.Next[slave].Position;
        float stealth = (c.Orders & CharacterOrders.Sneak) != 0 ? Orders.Stealth(c) : -1;
        var lookers = Enumerable.Range(0, t.HighWater)
            .Where(w => w != slave && t.Next[w].Alive && t.Cold(w) is { IsPlayer: false } wc && wc.Faction == c.SlaveOwner)
            .Select(w => (w, d: Vector3.Distance(t.Next[w].Position, at)))
            .Where(p => p.d <= AggroSystem.RangeOf(t.Cold(p.w)!, AggroSystem.OutdoorSight))
            .OrderBy(p => p.d)
            .Take(Lookers);

        foreach (var (w, d) in lookers)
        {
            float range = AggroSystem.RangeOf(t.Cold(w)!, AggroSystem.OutdoorSight);
            float view = SightRay.Sees(probe.World, w, slave, t.Next[w].Position, at, range, stealth);
            Console.WriteLine($"  {probe.Who(w)} gap={d:F0} view={view:F2}");
        }
    }
}
