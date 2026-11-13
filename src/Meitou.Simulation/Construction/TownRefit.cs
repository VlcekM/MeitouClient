using System.Numerics;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Simulation.Construction;

/// <summary>
/// A player town's reassessment after a placement or a dismantle (<c>FUN_14054d080</c>, <c>FUN_1405488f0</c> <c>Town::reassessTownPosition</c>,
/// docs/game/buildings-production.md "Outposts"): the touched town is refitted, every other player-faction town is refitted, then the first
/// overlapping town is merged into the larger.
/// </summary>
static class TownRefit
{
    /// <summary>
    /// The building function keys the centre sets read (BF_REFINERY 4, BF_FLUFF 18, BF_SHELL_WITH_INTERIOR 19, WALL 20, GATE 21; the last four come
    /// from the building's constructor, <b>Verified</b>).
    /// </summary>
    public const int Refinery = 4, Fluff = 18, Shell = 19, Wall = 20, Gate = 21;
    /// <summary>
    /// The weight of a house or gate in the centre against one top-level refinery-function building (<b>Verified</b>, disassembly of
    /// <c>FUN_1405488f0</c>).
    /// </summary>
    const float HouseWeight = 4;
    /// <summary>
    /// The half size of the box the centre looks for the closest navmesh point in (<b>Verified</b>, <c>FUN_1403a3ac0</c>).
    /// </summary>
    public const float SnapReach = 500;

    /// <summary>
    /// Reassesses <paramref name="touched"/> (null: the town the building belongs to is not a player outpost, so only the others are refitted).
    /// </summary>
    public static void Reassess(World world, PlayerTown? touched, ConstructionSystem? kinds)
    {
        bool removed = touched is not null && Refit(world, touched, kinds);
        foreach (var other in world.Buildings.Towns.Where(t => t != touched && t.Owner == world.Player.Faction).ToList()) Refit(world, other, kinds);

        if (touched is not null && !removed) Merge(world, touched, kinds);
    }

    /// <summary>
    /// Moves the town's centre to its buildings and sizes its radius to them: the centre is the mean of the houses and wall gates (weight 4) and the
    /// top-level refinery-function buildings (weight 1), put on the navmesh by the two-step 3D snap (<see cref="RefitSnap"/>, box <see cref="SnapReach"/>) at the terrain height (slot 32
    /// <c>FUN_140933b90</c>; the town keeps its old position when there is none); the radius is <see cref="ConstructionSystem.TownRadiusMargin"/> beyond the farthest building that is not a wall
    /// piece, at least <see cref="ConstructionSystem.TownRadiusStart"/>, within [<see cref="ConstructionSystem.TownRadiusMin"/>,
    /// <see cref="ConstructionSystem.TownRadiusMax"/>]. A town with no such building keeps its centre and radius, and is removed when it has no
    /// building at all. Returns true when the town was removed.
    /// </summary>
    static bool Refit(World world, PlayerTown town, ConstructionSystem? kinds)
    {
        var members = world.Buildings.All.Where(s => TownLookup.KeyOf(world, s) == town.Number).ToList();
        var (sum, weight) = Weigh(members, kinds);
        if (weight == 0)
        {
            if (members.Count > 0) return false;

            world.Buildings.RemoveTown(town);
            return true;
        }

        if (Snap(world.Walkability, sum / weight) is { } spot)
            town.Position = new Vector3(spot.X, world.Walkability.GroundHeight(spot.X, spot.Y), spot.Y);

        town.Override = Math.Clamp(RadiusOf(town, members, kinds), ConstructionSystem.TownRadiusMin, ConstructionSystem.TownRadiusMax);
        return false;
    }

    /// <summary>
    /// Where the centre (X, Y and Z the weighted means of the members' world positions) lands on the navmesh: the two-step 3D snap
    /// (<see cref="RefitSnap"/>) where the walkability knows its faces, else the 2D closest walkable point (<see cref="WalkableSpots.Closest"/>);
    /// null when there is none (the town keeps its position).
    /// </summary>
    internal static Vector2? Snap(IWalkability walk, Vector3 centre)
    {
        if (walk.SurfaceFaces(centre, SnapReach) is { } faces)
            return RefitSnap.Snap(faces, centre) is { } snapped ? new Vector2(snapped.X, snapped.Z) : null;

        return WalkableSpots.Closest(walk, new Vector2(centre.X, centre.Z), SnapReach, out var spot) ? spot : null;
    }

    /// <summary>
    /// The weighted sum of the building positions and the total weight (0: no house, gate or top-level refinery-function building).
    /// </summary>
    static (Vector3 Sum, float Weight) Weigh(List<BuildSite> members, ConstructionSystem? kinds)
    {
        var sum = Vector3.Zero;
        float weight = 0;
        foreach (var s in members)
        {
            int key = kinds?.FunctionKey(s.Record) ?? Fluff;
            float w = key is Shell or Gate ? HouseWeight : key == Refinery && s.Parent is null ? 1 : 0;
            sum += s.Position * w;
            weight += w;
        }

        return (sum, weight);
    }

    /// <summary>
    /// The radius round the town's centre: the farthest building that is not a wall piece (a gate is none) plus the margin, from the start radius.
    /// </summary>
    static float RadiusOf(PlayerTown town, List<BuildSite> members, ConstructionSystem? kinds)
    {
        float radius = ConstructionSystem.TownRadiusStart;
        foreach (var s in members)
        {
            if (kinds?.IsWallPiece(s.Record) == true) continue;

            radius = MathF.Max(radius, ConstructionSystem.DistanceXz(s.Position, town.Position) + ConstructionSystem.TownRadiusMargin);
        }

        return radius;
    }

    /// <summary>
    /// Merges the first overlapping player-faction outpost into the larger town, retaining this town on ties and reassessing the survivor.
    /// </summary>
    static void Merge(World world, PlayerTown town, ConstructionSystem? kinds)
    {
        var other = world.Buildings.Towns
            .FirstOrDefault(t => t != town && t.Owner == world.Player.Faction &&
                ConstructionSystem.DistanceXzSquared(t.Position, town.Position) < (t.Radius + town.Radius) * (t.Radius + town.Radius));
        if (other is null) return;

        var survivor = town.Radius >= other.Radius ? town : other;
        var removed = survivor == town ? other : town;
        world.Buildings.MoveTownBuildings(removed, survivor);
        world.Buildings.RemoveTown(removed);
        Reassess(world, survivor, kinds);
    }
}
