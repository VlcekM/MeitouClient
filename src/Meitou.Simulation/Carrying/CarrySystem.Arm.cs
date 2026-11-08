using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Carrying;

// Goal 42 "Don't be unarmed" and its step 8 FIND_WEAPON (docs/game/ai-tasks.md "Finders of the jobs", docs/simulation.md "Carrying, rescue and looting").
public sealed partial class CarrySystem
{
    /// <summary>How near a weapon must be for <c>AI::findWeapon</c> (<b>Verified</b>: squared distance 10000 in each pass).</summary>
    public const float FindWeaponRange = 100;

    /// <summary>
    /// Every <see cref="CarrySettings.AutoEveryTicks"/> ticks (serial, slot order): each NPC member whose running package lists goal 42 for its role, that has no weapon and a working
    /// arm and that the AI runs now, goes for the weapon <see cref="FindWeapon"/> picks: a ground item it picks up, else a body it loots (<see cref="NpcLoot"/>, which takes weapons
    /// first while it has none equipped; engine choice: the original's row 3 "Collecting item" takes the weapon alone).
    /// </summary>
    void Arm(World world, CharacterTable table)
    {
        if (world.SystemOf<PopulationSystem>() is not { } population) return;
        HashSet<int>? taken = null;
        HashSet<int>? claimed = null;
        Span<int> duty = stackalloc int[5];
        foreach (var squad in world.Squads.All)
        {
            if (squad.Members.Count == 0) continue;
            duty.Fill(-1);
            foreach (var member in squad.Members)
            {
                if (!table.TryResolveNext(member, out int slot) || table.Cold(slot) is not { IsPlayer: false, Inventory: not null, PickingUpItem: null } cold
                    || !Ai.AiSystem.Holds(table.Next[slot], cold) || HasWeapon(cold) || cold.Medical is { } m && !m.ArmWorks(left: true) && !m.ArmWorks(left: false)) continue;
                int role = (int)Ai.AiSystem.RoleOf(squad, member, cold);
                if (duty[role] < 0) duty[role] = population.GoalDuty(world, squad, (AiRole)role, TaskType.NOT_BE_UNARMED) ? 1 : 0;
                if (duty[role] == 0) continue;
                taken ??= Looting(table);
                claimed ??= Claimed(table);
                var (item, body) = FindWeapon(world, population.Data, table, slot, cold, taken, claimed);
                if (item >= 0)
                {
                    claimed.Add(item);
                    cold.PickingUpItem = item;
                    var at = world.GroundItems.Find(item)!.Position;
                    if (Vector2.Distance(new Vector2(table.Next[slot].Position.X, table.Next[slot].Position.Z), new Vector2(at.X, at.Z)) > PlayerSystem.PickUpReach)
                        TaskStart.Walk(world, slot, at, SpeedMode.Run, TaskType.NOT_BE_UNARMED, TaskSource.Npc);
                }
                else if (body >= 0)
                {
                    var task = (CharacterTask)table.Next[slot].Task;
                    if (Start(world, slot, CharacterTask.Loot, TaskType.NOT_BE_UNARMED, TaskSource.Npc, table.IdOf(body), task is CharacterTask.Wander or CharacterTask.Follow ? task : CharacterTask.Idle))
                        taken.Add(body);
                }
            }
        }
    }

    /// <summary>The ground items characters are walking to pick up.</summary>
    static HashSet<int> Claimed(CharacterTable table)
    {
        var claimed = new HashSet<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i)?.PickingUpItem is { } id) claimed.Add(id);
        return claimed;
    }

    /// <summary>
    /// <c>AI::findWeapon</c> (<c>FUN_14099b1d0</c>, docs/game/ai-tasks.md), first pass with a match wins, each within <see cref="FindWeaponRange"/>: the nearest free weapon on the ground (in a
    /// town only one nobody owns or an enemy of the looter does); else a dead body with a weapon; else a downed living one not of an allied faction, not of the player's side unless
    /// <see cref="PlayerState.BanditsLoot"/>, that it does not love. Engine choices: the nearest of the body passes (the original takes its sensed-list order); the accessibility tests
    /// are left to the walk. (-1, -1): none.
    /// </summary>
    (int Item, int Body) FindWeapon(World world, PopulationData data, CharacterTable table, int slot, CharacterCold cold, HashSet<int> taken, HashSet<int> claimed)
    {
        var next = table.Next;
        var at = new Vector2(next[slot].Position.X, next[slot].Position.Z);
        float range2 = FindWeaponRange * FindWeaponRange;
        bool inTown = !Ai.AiSystem.TownAt(data, world, next[slot].Position).IsNone;
        var sight = world.Sight;
        int item = -1;
        float best = range2;
        foreach (var g in world.GroundItems.All)
        {
            if (g.Item.Type != FcsRecordType.WEAPON || g.Slot is not null || claimed.Contains(g.Id)) continue;
            float d2 = Vector2.DistanceSquared(at, new Vector2(g.Position.X, g.Position.Z));
            if (d2 > best) continue;
            // In a town it must lie where the character is (inside the same building, or both outdoors), and be nobody's or an enemy's (FUN_14099b1d0).
            if (inTown && (sight.PlaceAt(g.Position) != sight.PlaceOf(slot)
                || g.OwnedBy >= 0 && !(data.Relations.Contains(cold.Faction) && AggroSystem.IsEnemy(data.Relations, cold.Faction, g.OwnedBy)))) continue;
            (item, best) = (g.Id, d2);
        }
        if (item >= 0) return (item, -1);
        var memory = world.SystemOf<Dialogue.DialogueSystem>()?.Memory;
        int dead = -1, down = -1;
        float dead2 = range2, down2 = range2;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { } body || !CanBeLooted(body) || !body.CarriedBy.IsNone || body.IsImprisoned || !HasWeapon(body)
                || body.Fighter is { IsAnimal: true }) continue;
            float d2 = Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z));
            if (body.Medical!.Dead) { if (d2 <= dead2) (dead, dead2) = (s, d2); continue; }
            if (d2 > down2 || Friendly(cold, body) || PlayerSide(world, body) && !world.Player.BanditsLoot) continue;
            if (memory?.Find(world, cold.SquadId, table.IdOf(s)) is { } known && (known.Long & Dialogue.MemoryTag.LoveMask) != 0) continue;
            (down, down2) = (s, d2);
        }
        return (-1, dead >= 0 ? dead : down);
    }
}
