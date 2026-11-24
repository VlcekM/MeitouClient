using System.Numerics;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Items;

/// <summary>
/// The loose-item pass of the source finder <c>FUN_14059d000</c> (<b>Verified</b>; docs/game/buildings-production.md "Material delivery"), shared by
/// its callers: the building-materials finder (115) and the fill finder (94). Pure reads, safe in the parallel think.
/// </summary>
internal static class LooseSource
{
    /// <summary>
    /// Loose items are looked for within this far of the character (<c>FUN_14059d000</c>, <b>Verified</b>).
    /// </summary>
    public const float Radius = 1200;

    /// <summary>
    /// The nearest loose item of <paramref name="record"/> within <see cref="Radius"/> (3D, inclusive) of <paramref name="at"/>: with some in it and
    /// no contents, not in the character's ignore map (<see cref="Key"/>, asked before the ownership test) and passing the ownership test
    /// (<see cref="ItemOwnership.MayTake"/>, <c>FUN_140997d80</c>); null when none. Engine choice: the nearest, where the original takes the first
    /// its grid query meets. The tests are pure, so their order changes no answer: the record set (<see cref="GroundItems.Holds"/>), the item's own
    /// fields and the distance come first, so the ignore-map key is made only for a nearer item of the record. With <paramref name="ownOnly"/> an
    /// item another faction owns is skipped even where the ownership test would let the character take it (a player hauler's pick, so it steals
    /// nothing: <see cref="Production.ProductionSystem"/> "Hauling to").
    /// </summary>
    public static GroundItem? Nearest(World world, PopulationData? data, CharacterCold cold, Vector3 at, string record, bool ownOnly = false) =>
        world.GroundItems.Holds(record) ? Nearest(world, data, cold, at, g => g.Item.Record == record, ownOnly) : null;

    /// <summary>
    /// <see cref="Nearest(World, PopulationData?, CharacterCold, Vector3, string, bool)"/> for the items <paramref name="wanted"/> answers yes
    /// for (a pure test, asked before the distance) in place of one record.
    /// </summary>
    public static GroundItem? Nearest(World world, PopulationData? data, CharacterCold cold, Vector3 at, Func<GroundItem, bool> wanted,
        bool ownOnly = false)
    {
        var all = world.GroundItems.All;
        GroundItem? loose = null;
        float best = Radius * Radius;
        for (int k = 0; k < all.Count; k++)
        {
            var g = all[k];
            if (g.Item.Quantity <= 0 || g.Item.Contents is not null || !wanted(g)) continue;
            if (ownOnly && g.OwnedBy >= 0 && g.OwnedBy != cold.Faction) continue;

            float d = Vector3.DistanceSquared(g.Position, at);
            if (!(d <= best && (loose is null || d < best))) continue;
            if (cold.IsUnreachable(Key(g.Id), world.Tick) || !ItemOwnership.MayTake(world, data, cold, at, g)) continue;

            (loose, best) = (g, d);
        }

        return loose;
    }

    /// <summary>
    /// The ignore-map key of a loose item (<see cref="CharacterCold.NoteUnreachable"/>): a walk to it that ended short leaves it out for 20 s.
    /// </summary>
    public static string Key(int id) => "item:" + id;
}
