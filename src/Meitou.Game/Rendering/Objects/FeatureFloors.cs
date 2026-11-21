using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// One placed map feature's collision for the camera: where it stands, how far it reaches and its surfaces, made on a worker when first needed.
/// </summary>
sealed class FeatureFloor
{
    /// <summary>
    /// The feature's position in the world.
    /// </summary>
    public required Vector3 Position { get; init; }

    /// <summary>
    /// How far (units, X/Z) from <see cref="Position"/> its collision can reach: its farthest collision vertex at its largest scale, and
    /// <see cref="BuildingFloors.ReachSlack"/>.
    /// </summary>
    public required float Reach { get; init; }

    /// <summary>
    /// Makes the surfaces (run on a worker on the first <see cref="Request"/>).
    /// </summary>
    public required Func<FloorSurfaces> Make { get; init; }

    /// <summary>
    /// The job making the surfaces (null: not asked for yet).
    /// </summary>
    Task<FloorSurfaces>? _job;

    /// <summary>
    /// The surfaces, or null while they are being made.
    /// </summary>
    public FloorSurfaces? Surfaces => _job is { IsCompletedSuccessfully: true } j ? j.Result : null;

    /// <summary>
    /// Starts making the surfaces on a worker, once.
    /// </summary>
    public void Request() => _job ??= BackgroundWork.Run(Make);
}

/// <summary>
/// The map features' collision near a point (<c>features.dat</c>: rocks, cliff blocks; collision group 14) for the strategy camera's ground
/// (docs/formats/camera.md "Height"). Display only. A zone's features are listed on a worker on first need and a feature's surfaces made when a
/// point first comes within its reach; until then the answer is "no feature" and the caller keeps to the terrain. The features are fixed, so a
/// zone is listed once.
/// </summary>
sealed class FeatureFloors(WorldObjects objects)
{
    /// <summary>
    /// The listing jobs of the zones asked about, by zone.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, Task<FeatureFloor[]>> _zones = [];

    /// <summary>
    /// The height of the highest feature surface at (<paramref name="x"/>, <paramref name="z"/>) at or under <paramref name="top"/>, or null:
    /// what a ray cast down from <paramref name="top"/> hits among the features placed in the point's zone and its eight neighbours (engine choice,
    /// the zones the navmesh gathers features from; a feature can reach past a building's reach).
    /// </summary>
    public float? HighestBelow(float x, float z, float top)
    {
        var centre = WorldLayout.ZoneOf(x, z);
        float? best = null;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var zone = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (!zone.IsInsideGrid) continue;
                foreach (var feature in Listed(zone))
                    if (HeightBelow(feature, x, z, top) is { } h && (best is null || h > best)) best = h;
            }
        return best;
    }

    /// <summary>
    /// The height of <paramref name="feature"/>'s highest surface at the point at or under <paramref name="top"/>; null when the point is out of
    /// its reach, its surfaces are not made yet (asking for them) or none is there.
    /// </summary>
    static float? HeightBelow(FeatureFloor feature, float x, float z, float top)
    {
        var offset = new Vector2(feature.Position.X - x, feature.Position.Z - z);
        if (offset.LengthSquared() > feature.Reach * feature.Reach) return null;

        if (feature.Surfaces is { } surfaces) return surfaces.HighestBelow(x, z, top);

        feature.Request();
        return null;
    }

    /// <summary>
    /// A zone's features, listing them on a worker on the first ask (none until that is done).
    /// </summary>
    FeatureFloor[] Listed(ZoneCoordinate zone)
    {
        if (!_zones.TryGetValue(zone, out var job)) _zones[zone] = job = BackgroundWork.Run(() => objects.FeatureFloorsOf(zone));
        return job.IsCompletedSuccessfully ? job.Result : [];
    }
}
