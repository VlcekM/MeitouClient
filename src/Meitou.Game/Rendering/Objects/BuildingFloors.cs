using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// One placed building's floors for the footstep surface and the camera: its two ground materials and its floor collision, made on a worker when
/// first needed.
/// </summary>
sealed class BuildingFloor
{
    /// <summary>
    /// The placement's key (the interior view's id for it).
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// The placement it was made from: a new listing with the same one keeps this floor and its collision.
    /// </summary>
    public required BuildingPlacement Placement { get; init; }

    /// <summary>
    /// The building's position in the world.
    /// </summary>
    public required Vector3 Position { get; init; }

    /// <summary>
    /// The building's <c>interior material</c> and <c>exterior material</c> (GroundType).
    /// </summary>
    public required int Interior { get; init; }

    /// <summary>
    /// The building's <c>exterior material</c> (GroundType), taken when the character is outside.
    /// </summary>
    public required int Exterior { get; init; }

    /// <summary>
    /// Makes the floor collision (run on a worker on the first <see cref="Request"/>).
    /// </summary>
    public required Func<FloorSurfaces> Make { get; init; }

    /// <summary>
    /// The job making the collision (null: not asked for yet).
    /// </summary>
    Task<FloorSurfaces>? _job;

    /// <summary>
    /// The floor collision, or null while it is being made.
    /// </summary>
    public FloorSurfaces? Surfaces => _job is { IsCompletedSuccessfully: true } j ? j.Result : null;

    /// <summary>
    /// Starts making the floor collision on a worker, once.
    /// </summary>
    public void Request() => _job ??= BackgroundWork.Run(Make);
}

/// <summary>
/// The building floors near a point: for <c>Surface_Material</c> (docs/audio.md "Surface"; the game casts a ray down from 20 units above the
/// character and, when the closest hit is a building, takes that building's <c>exterior material</c> or, with the character inside a building, its
/// <c>interior material</c>) and for the strategy camera's ground (docs/formats/camera.md "Height"). Display only: the floors are read from the
/// placements and their collision files, nothing of it reaches the simulation. A zone's buildings are listed on a worker on first need, a building's
/// floor collision when a point first comes within <see cref="Reach"/>; until it is there the answer is "no building" and the caller keeps to the
/// terrain. A runtime placement added or removed (a dropped item too) lists the zone again, while the listing before it still answers, and an
/// unchanged building keeps its collision: dropping the known floors for a few frames dipped the camera's pivot to the terrain (#204).
/// </summary>
sealed class BuildingFloors(WorldObjects objects)
{
    /// <summary>
    /// How far (units, X/Z) from a building's position its floors are looked for (engine choice: the largest buildings reach about this far).
    /// </summary>
    public const float Reach = 400;

    /// <summary>
    /// How far above the feet the ray starts (<c>FUN_14065f160</c>: 20), and how far below the feet a floor still counts as the one stood on (engine
    /// choice: the heights characters stand at come from the navigation mesh).
    /// </summary>
    public const float RayStart = 20, Tolerance = 4;

    /// <summary>
    /// The floor listings of the zones asked about, by zone.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, ZoneListing> _zones = [];

    /// <summary>
    /// The scratch list of the floors near the point asked about, with their collision (cleared at each call).
    /// </summary>
    readonly List<(BuildingFloor Floor, FloorSurfaces Surfaces)> _near = [];

    /// <summary>
    /// The GroundType of the building floor under <paramref name="feet"/> (the closest of the buildings' floors within the tolerance), or -1 for
    /// none.
    /// </summary>
    public int MaterialUnder(Vector3 feet, bool inside)
    {
        BuildingFloor? hit = null;
        float best = float.NegativeInfinity;
        foreach (var (floor, surfaces) in Near(feet.X, feet.Z))
            if (surfaces.HighestBelow(feet.X, feet.Z, feet.Y + RayStart) is { } h && h > best)
            {
                best = h;
                hit = floor;
            }
        return hit is null || best < feet.Y - Tolerance ? -1 : inside ? hit.Interior : hit.Exterior;
    }

    /// <summary>
    /// The highest ordinary floor part (no stairs) of any building at (<paramref name="x"/>, <paramref name="z"/>) on floors up to
    /// <paramref name="floorOf"/> of its key, or null for none.
    /// </summary>
    public float? HighestFloorAt(float x, float z, Func<string, int> floorOf)
    {
        float? best = null;
        foreach (var (floor, surfaces) in Near(x, z))
            if (surfaces.HighestUpTo(x, z, floorOf(floor.Key)) is { } h && (best is null || h > best)) best = h;
        return best;
    }

    /// <summary>
    /// The buildings within <see cref="Reach"/> whose floors are made (asking for the rest); the list is reused by the next call.
    /// </summary>
    List<(BuildingFloor, FloorSurfaces)> Near(float x, float z)
    {
        _near.Clear();

        // The zones the reach touches: its four corners.
        Span<ZoneCoordinate> touched = stackalloc ZoneCoordinate[4];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            var zone = WorldLayout.ZoneOf(x + ((i & 1) == 0 ? -Reach : Reach), z + ((i & 2) == 0 ? -Reach : Reach));
            if (!zone.IsInsideGrid || touched[..count].Contains(zone)) continue;
            touched[count++] = zone;
        }
        foreach (var zone in touched[..count])
            foreach (var floor in Listed(zone))
            {
                if (Vector2.DistanceSquared(new(floor.Position.X, floor.Position.Z), new(x, z)) > Reach * Reach) continue;
                if (floor.Surfaces is { } surfaces) _near.Add((floor, surfaces));
                else floor.Request();
            }
        return _near;
    }

    /// <summary>
    /// A zone's buildings as last listed, listing them again on a worker when the placements changed since (the old listing answers until then; one
    /// job per zone at a time, so placements changing every frame still get listed).
    /// </summary>
    BuildingFloor[] Listed(ZoneCoordinate zone)
    {
        if (!_zones.TryGetValue(zone, out var listing)) _zones[zone] = listing = new ZoneListing();
        if (listing.Job is { IsCompletedSuccessfully: true } done && listing.Adopted != done)
        {
            listing.Floors = KeepUnchanged(listing.Floors, done.Result);
            listing.Adopted = done;
        }
        int version = objects.PlacementVersion;
        if (listing.Job is null || (listing.Version != version && listing.Job.IsCompleted))
        {
            listing.Job = BackgroundWork.Run(() => objects.FloorsOf(zone));
            listing.Version = version;
        }
        return listing.Floors;
    }

    /// <summary>
    /// The new listing, each building whose placement did not change replaced by its floor from the old one (so its collision is not made again).
    /// </summary>
    static BuildingFloor[] KeepUnchanged(BuildingFloor[] old, BuildingFloor[] listed)
    {
        if (old.Length == 0) return listed;
        var byKey = new Dictionary<string, BuildingFloor>(old.Length, StringComparer.Ordinal);
        foreach (var floor in old) byKey.TryAdd(floor.Key, floor);
        for (int i = 0; i < listed.Length; i++)
            if (byKey.TryGetValue(listed[i].Key, out var kept) && kept.Placement == listed[i].Placement) listed[i] = kept;
        return listed;
    }

    /// <summary>
    /// One zone's listing: the floors in use, the job listing them again and the placement version it was started at.
    /// </summary>
    sealed class ZoneListing
    {
        /// <summary>
        /// The floors in use (an unchanged building keeps its floor and collision across listings).
        /// </summary>
        public BuildingFloor[] Floors = [];

        /// <summary>
        /// The job listing the zone's floors again (null: none started).
        /// </summary>
        public Task<BuildingFloor[]>? Job;

        /// <summary>
        /// The listing job whose result <see cref="Floors"/> holds.
        /// </summary>
        public Task<BuildingFloor[]>? Adopted;

        /// <summary>
        /// The placement version <see cref="Job"/> was started at.
        /// </summary>
        public int Version;
    }
}