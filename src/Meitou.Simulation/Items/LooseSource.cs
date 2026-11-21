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
    /// its grid query meets.
    /// </summary>
    public static GroundItem? Nearest(World world, PopulationData? data, CharacterCold cold, Vector3 at, string record)
    {
        GroundItem? loose = null;
        float best = Radius * Radius;
        foreach (var g in world.GroundItems.All)
        {
            if (cold.IsUnreachable(Key(g.Id), world.Tick)) continue;
            if (g.Item.Record != record || g.Item.Quantity <= 0 || g.Item.Contents is not null || !ItemOwnership.MayTake(world, data, cold, at, g))
                continue;

            float d = Vector3.DistanceSquared(g.Position, at);
            if (d <= best && (loose is null || d < best)) (loose, best) = (g, d);
        }

        return loose;
    }

    /// <summary>
    /// The ignore-map key of a loose item (<see cref="CharacterCold.NoteUnreachable"/>): a walk to it that ended short leaves it out for 20 s.
    /// </summary>
    public static string Key(int id) => "item:" + id;
}
