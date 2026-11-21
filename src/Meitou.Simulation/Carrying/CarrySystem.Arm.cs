using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// Arming: Goal 42 "Don't be unarmed" and its step 8 FIND_WEAPON (docs/game/ai-tasks.md "Finders of the jobs", docs/simulation.md "Carrying, rescue
/// and looting").
/// </summary>
public sealed partial class CarrySystem
{
    #region Arming

    /// <summary>
    /// How near a weapon must be for <c>AI::findWeapon</c> (<b>Verified</b>: squared distance 10000 in each pass).
    /// </summary>
    public const float FindWeaponRange = 100;

    /// <summary>
    /// The ground items characters are walking to pick up.
    /// </summary>
    internal static HashSet<int> Claimed(CharacterTable table)
    {
        var claimed = new HashSet<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i)?.PickingUpItem is { } id) claimed.Add(id);
        return claimed;
    }

    /// <summary>
    /// <c>AI::findWeapon</c> (<c>FUN_14099b1d0</c>, docs/game/ai-tasks.md), first pass with a match wins, each within <see cref="FindWeaponRange"/>:
    /// the nearest free weapon on the ground (in a town only one nobody owns or an enemy of the looter does); else a dead body with a weapon; else a
    /// downed living one not of an allied faction, not of the player's side unless <see cref="PlayerState.BanditsLoot"/>, that it does not love.
    /// Engine choices: the nearest of the body passes (the original takes its sensed-list order); the accessibility tests are left to the walk. (-1,
    /// -1): none.
    /// </summary>
    internal (int Item, int Body) FindWeapon(World world, PopulationData data, CharacterTable table, int slot, CharacterCold cold,
        HashSet<int> taken, HashSet<int> claimed)
    {
        var next = table.Next;
        var at = new Vector2(next[slot].Position.X, next[slot].Position.Z);
        float range2 = FindWeaponRange * FindWeaponRange;
        bool inTown = !AiSystem.TownAt(data, world, next[slot].Position).IsNone;
        var sight = world.Sight;
        int item = -1;
        float best = range2;
        foreach (var g in world.GroundItems.All)
        {
            if (g.Item.Type != FcsRecordType.Weapon || g.Slot is not null || claimed.Contains(g.Id)) continue;
            float d2 = Vector2.DistanceSquared(at, new Vector2(g.Position.X, g.Position.Z));
            if (d2 > best) continue;

            // In a town it must lie where the character is (inside the same building, or both outdoors), and be nobody's or an enemy's
            // (FUN_14099b1d0).
            if (inTown && (sight.PlaceAt(g.Position) != sight.PlaceOf(slot)
                || g.OwnedBy >= 0
                    && !(data.Relations.Contains(cold.Faction) && AggroSystem.IsEnemy(data.Relations, cold.Faction, g.OwnedBy))))
                continue;
            (item, best) = (g.Id, d2);
        }
        if (item >= 0) return (item, -1);
        var memory = world.SystemOf<Dialogue.DialogueSystem>()?.Memory;
        int dead = -1, down = -1;
        float dead2 = range2, down2 = range2;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !next[s].Alive || taken.Contains(s) || table.Cold(s) is not { } body || !CanBeLooted(body)
                || !body.CarriedBy.IsNone || body.IsImprisoned || !HasWeapon(body) || body.Fighter is { IsAnimal: true })
                continue;
            float d2 = Vector2.DistanceSquared(at, new Vector2(next[s].Position.X, next[s].Position.Z));
            if (body.Medical!.Dead)
            {
                if (d2 <= dead2) (dead, dead2) = (s, d2);
                continue;
            }
            if (d2 > down2 || Friendly(cold, body) || PlayerSide(world, body) && !world.Player.BanditsLoot) continue;
            if (memory?.Find(world, cold.SquadId, table.IdOf(s)) is { } known && (known.Long & Dialogue.MemoryTag.LoveMask) != 0) continue;
            (down, down2) = (s, d2);
        }
        return (-1, dead >= 0 ? dead : down);
    }

    #endregion
}