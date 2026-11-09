using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Carrying;

// The foraging job and the Rescue Allies option (docs/game/ai-tasks.md "Perma-jobs" and "Squad AI options").
public sealed partial class CarrySystem
{
    /// <summary>
    /// The foraging job's finder (238 "Foraging animals", <c>FUN_1408ce3a0</c>; a pure read; the decision step's <see cref="CarryJobs"/>): with no
    /// fight in the forager's squad, the nearest downed animal within its sight that is not carried, not looted by another (<paramref name="taken"/>)
    /// and holds an item one of the forager's faction's complete storages accepts (<see cref="Storable"/>); -1 for none. The finder's faction test (a
    /// character vfunc, or the animal's faction flag +0x1d0) is <b>Unknown</b>; Meitou leaves the forager's own faction's animals alone (engine
    /// choice). The forager loots it (<see cref="CharacterTask.Loot"/>: the walk, then <see cref="ForageTake"/>).
    /// </summary>
    int FindForage(World world, int i, HashSet<int> taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && CharacterFacts.SquadFighting(table, squad)) return -1;
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        var at = new Vector2(next[i].Position.X, next[i].Position.Z);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == i || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { } animal || animal.Faction == cold.Faction) continue;
            if (!(animal.Fighter?.IsAnimal ?? animal.Appearance?.Animal is not null) || !CanBeLooted(animal) || !animal.CarriedBy.IsNone
                || !animal.Inventory!.All().Any())
                continue;
            // the give-up list (AITaskSytem::isPathImpossible, FUN_1408ce3a0)
            if (PursuitSystem.PathImpossible(cold, table.IdOf(s), world.Tick)) continue;
            float d2 = Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z));
            if (d2 <= best2 && (best < 0 || d2 < best2) && Storable(world, cold, next[i].Position, animal) is not null)
            {
                best = s;
                best2 = d2;
            }
        }
        return best;
    }

    /// <summary>
    /// Seconds the forager works at the body before it takes (<b>Verified</b>, <c>Task_Loot_ResourcesWeHaveStorageFor</c> update
    /// <c>FUN_14034e2a0</c>: the timer must pass <c>DAT_14168a254</c> = 3).
    /// </summary>
    public const float ForageSeconds = 3;

    /// <summary>
    /// The forager at its body (<c>Task_Loot_ResourcesWeHaveStorageFor</c> update, <c>FUN_14034e2a0</c>, docs/game/carry-loot.md "Foraging take"): it
    /// works (the medic clip) for <see cref="ForageSeconds"/>, then takes the units of the one item kind <see cref="Storable"/> finds, and the task
    /// ends; no crime, theft mark or money. Butchering follows from the death items leaving the body.
    /// </summary>
    void ForageTake(World world, CharacterTable table, int i, int t, float dt)
    {
        var cold = table.Cold(i)!;
        var victim = table.Cold(t)!;
        if (cold.LiftTime < 0) cold.LiftTime = 0;
        cold.LiftTime += dt;
        if (cold.LiftTime <= ForageSeconds) return;
        int death = DeathUnits(victim.Inventory!);
        int moved = ForageMove(cold, table.Next[i].Position, victim, world);
        Butcher(world, i, t, death);
        Looted(world, table, i, t, moved, 0, crime: false);
        End(world, i);
    }

    /// <summary>
    /// Moves every unit of the first storable item's kind that fits from the animal's own inventory (not its backpack's: the original's pick by kind
    /// searches the inventory only) to the forager, one unit at a time: into its worn bag when that takes a unit, else its inventory, and stops when
    /// that place is full. Returns the units moved.
    /// </summary>
    int ForageMove(CharacterCold forager, Vector3 at, CharacterCold animal, World world)
    {
        if (Storable(world, forager, at, animal) is not { } first || forager.Inventory is not { } mine) return 0;
        var from = animal.Inventory!;
        bool? toBag = null;
        int moved = 0;
        foreach (var item in from.Items.Where(x => x.Record == first.Record && !x.LockedOn && InventoryCommands.IsMovableSection(x.Section)).ToList())
        {
            int units = 0;
            bool worn = false;
            if (toBag != false && (units = InventoryCommands.MoveInto(from, item, 0, mine, bagFirst: true, out _, forager, bagOnly: true)) > 0)
                toBag = true;
            if (toBag != true && from.Items.Contains(item)
                && (units = InventoryCommands.MoveInto(from, item, 0, mine, bagFirst: false, out worn, forager)) > 0)
                toBag = false;
            moved += units;
            if (worn) Assembly?.RefreshEquipment(forager);
            if (from.Items.Contains(item)) break;   // that place is full
        }
        return moved;
    }

    /// <summary>
    /// The first item of <paramref name="animal"/> (its inventory, then its backpack's) that has somewhere to go (<c>FUN_140998950</c> over
    /// <c>FUN_14059acd0</c>, <b>Verified</b> decompile): a complete storage building of the forager's faction within the AI's range
    /// (<see cref="Construction.ConstructionSystem.SourceRangeSquared"/>, 3D) that takes it
    /// (<see cref="Meitou.Data.Gameplay.Construction.MachineData.Accepts"/>, engine stand-in for its <c>canHaveSomeOfThese</c>: free room is not
    /// tested); null: none.
    /// </summary>
    static ItemInstance? Storable(World world, CharacterCold forager, Vector3 at, CharacterCold animal)
    {
        var inventory = animal.Inventory!;
        foreach (var item in inventory.Items.Concat(inventory.Backpack?.Contents?.Items ?? []))
            foreach (var m in world.Machines.All)
                if (m.Data.IsStorage && m.Storage is not null && m.Data.Accepts(item.Record, item.Type)
                    && Vector3.DistanceSquared(m.Position, at) <= Construction.ConstructionSystem.SourceRangeSquared
                    && world.Buildings.Find(m.Site) is { Complete: true } site && site.Owner == forager.Faction)
                    return item;
        return null;
    }

    /// <summary>
    /// The bodies being looted now (by slot).
    /// </summary>
    static HashSet<int> Looting(CharacterTable table)
    {
        var taken = new HashSet<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Loot
                && table.TryResolveNext(table.Cold(i)!.CarryTarget, out int busy))
                taken.Add(busy);
        return taken;
    }

    /// <summary>
    /// Whom a rescuer's rescue job takes in (the character finder <c>FUN_1408cd2d0</c>, <b>Verified</b>): the sensed entries tagged ALLY (the looker
    /// does not regard them as enemies and <see cref="Friendly"/> holds: its own or an allied faction), and for a player character also, unless
    /// <see cref="SquadAi.Rescue"/> (the global +0x112) is on, its own faction only. An NPC rescuer has no faction test, so allied factions' bodies
    /// count for it without the option.
    /// </summary>
    bool Rescues(World world, CharacterCold rescuer, CharacterCold body) =>
        body.Faction == rescuer.Faction || (!rescuer.IsPlayer || (world.Player.SquadAi & SquadAi.Rescue) != 0) && Friendly(rescuer, body);
}