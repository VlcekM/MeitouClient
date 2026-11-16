using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Game.Audio;

/// <summary>
/// The foliage's sound emitters (docs/audio.md "Point emitters", foliage): a FOLIAGE_MESH with <c>sounds</c> gives its placed copies emitters as the
/// zone's foliage is placed, at most 100 a zone in placement order (<see cref="FoliageLayout.MaxEmitters"/>). The exe makes them when a zone's
/// foliage object is built and ends them with it; Meitou places, on a worker, the zones that lie within the farthest foliage emitter's range of the
/// eye (a full placement of its own, since the renderer's layouts may be partial or cached), and drops them when the eye moves away. Nothing runs
/// when no FOLIAGE_MESH lists <c>sounds</c> (none in the base game).
/// </summary>
sealed class FoliageEmitters(GameHost host)
{
    /// <summary>
    /// How much farther than the reach a placed zone is kept before its emitters go (units), so that a zone on the edge is not placed again and
    /// again.
    /// </summary>
    const float DropMargin = 1000;

    /// <summary>
    /// The world and the emitters the zones were placed for; another of either starts over.
    /// </summary>
    (object? World, WorldEmitters? Emitters) _for;

    /// <summary>
    /// The foliage catalog, read on a worker; null in the result when no mesh has a sound.
    /// </summary>
    Task<FoliageCatalog?>? _catalog;

    /// <summary>
    /// The placer (no layout cache: its zones are placed in full); used by one job at a time.
    /// </summary>
    FoliageWorld? _world;

    /// <summary>
    /// The farthest range of a foliage emitter (cutoff × 10): the zones within it of the eye are placed.
    /// </summary>
    float _reach;

    /// <summary>
    /// The zones placed, with the keys of the emitters added for each.
    /// </summary>
    readonly Dictionary<ZoneCoordinate, List<string>> _zones = [];

    /// <summary>
    /// The zone being placed; null while none is. One started before the zones were forgotten still runs (it holds the placer) but is not taken.
    /// </summary>
    Task<FoliageZone>? _job;

    /// <summary>
    /// How many times the zones were forgotten, and the count <see cref="_job"/> was started at.
    /// </summary>
    int _generation, _jobGeneration;

    /// <summary>
    /// The zone <see cref="_job"/> places.
    /// </summary>
    ZoneCoordinate _jobZone;

    /// <summary>
    /// Once a frame, after the building emitters: the placed zones' emitters join <paramref name="emitters"/>, the zones far from
    /// <paramref name="eye"/> leave, and the nearest zone within reach not placed yet is started.
    /// </summary>
    internal void Update(WorldEmitters emitters, Vector3 eye)
    {
        if (!Ready(emitters)) return;

        AcceptPlaced(emitters);
        DropFar(emitters, eye);
        StartNearest(eye);
    }

    /// <summary>
    /// The main menu cleared the emitters: the zones are placed again when a world needs them.
    /// </summary>
    internal void Forget()
    {
        _zones.Clear();
        _for = default;
        _generation++;
    }

    /// <summary>
    /// Follows the world and the emitters (a new one of either forgets the zones) and reads the catalog once; true when there are foliage sounds to
    /// place.
    /// </summary>
    bool Ready(WorldEmitters emitters)
    {
        var world = host.Session.World;
        if (!ReferenceEquals(_for.World, world) || !ReferenceEquals(_for.Emitters, emitters))
        {
            if (ReferenceEquals(_for.Emitters, emitters)) RemoveAll(emitters);
            _zones.Clear();
            _for = (world, emitters);
            _generation++;
        }

        if (_catalog is null && host.Scene.Objects is { } objects) _catalog = Task.Run(() => SoundingCatalog(objects.Database));
        if (_catalog is not { IsCompletedSuccessfully: true, Result: { } catalog } || host.Scene.Objects is not { } scene) return false;

        _world ??= new FoliageWorld(scene.Install, scene.Database, scene.Levels, catalog);
        return true;
    }

    /// <summary>
    /// The catalog when one of its meshes has a sound (and the reach set from the farthest), else null.
    /// </summary>
    FoliageCatalog? SoundingCatalog(GameDatabase db)
    {
        var catalog = FoliageCatalog.Load(db);
        _reach = 0;
        foreach (var mesh in catalog.Meshes.Values)
            if (mesh.Sound is { } entry) _reach = MathF.Max(_reach, entry.Sound.Range);

        return _reach > 0 ? catalog : null;
    }

    /// <summary>
    /// A finished placement's emitters are added (those with no <c>emitter</c> name count towards the zone's cap but make no sound); a failed one
    /// counts as placed with none.
    /// </summary>
    void AcceptPlaced(WorldEmitters emitters)
    {
        if (_job is not { IsCompleted: true } job) return;

        _job = null;
        if (_jobGeneration != _generation) return;

        var keys = new List<string>();
        _zones[_jobZone] = keys;
        if (!job.IsCompletedSuccessfully)
        {
            Console.WriteLine($"audio     foliage emitters of zone {_jobZone}: {job.Exception?.GetBaseException().Message}");
            return;
        }

        foreach (var emitter in job.Result.Emitters)
        {
            if (emitter.Sound.Emitter.Length == 0) continue;
            emitters.Add(emitter);
            keys.Add(emitter.Key);
        }
    }

    /// <summary>
    /// The zones that lie farther than the reach and the margin from the eye lose their emitters.
    /// </summary>
    void DropFar(WorldEmitters emitters, Vector3 eye)
    {
        foreach (var (zone, keys) in _zones.ToArray())
        {
            if (DistanceTo(zone, eye) <= _reach + DropMargin) continue;

            foreach (string key in keys) emitters.Remove(key);
            _zones.Remove(zone);
        }
    }

    /// <summary>
    /// Starts placing the nearest zone of the grid within reach of the eye that is not placed yet, while no placement runs.
    /// </summary>
    void StartNearest(Vector3 eye)
    {
        if (_job is not null) return;

        var at = WorldLayout.ZoneOf(eye.X, eye.Z);
        int around = (int)MathF.Ceiling(_reach / WorldLayout.ZoneSize) + 1;
        ZoneCoordinate? best = null;
        float bestDistance = _reach;
        for (int dx = -around; dx <= around; dx++)
            for (int dz = -around; dz <= around; dz++)
            {
                var zone = new ZoneCoordinate(at.X + dx, at.Y + dz);
                if (!zone.IsInsideGrid || _zones.ContainsKey(zone)) continue;

                float d = DistanceTo(zone, eye);
                if (d > bestDistance) continue;
                (best, bestDistance) = (zone, d);
            }

        if (best is not { } next) return;

        var world = _world!;
        world.ExtraTowns = host.ActiveFoliage?.PlayerTowns.Towns ?? [];
        _jobGeneration = _generation;
        _jobZone = next;
        _job = Task.Run(() => world.Zone(next));
    }

    /// <summary>
    /// Removes every placed zone's emitters.
    /// </summary>
    void RemoveAll(WorldEmitters emitters)
    {
        foreach (var keys in _zones.Values)
            foreach (string key in keys) emitters.Remove(key);
    }

    /// <summary>
    /// The horizontal distance from the eye to the zone's square (0 inside it).
    /// </summary>
    static float DistanceTo(ZoneCoordinate zone, Vector3 eye)
    {
        var (x0, z0) = WorldLayout.ZoneOrigin(zone);
        float dx = MathF.Max(MathF.Max((float)x0 - eye.X, eye.X - (float)(x0 + WorldLayout.ZoneSize)), 0);
        float dz = MathF.Max(MathF.Max((float)z0 - eye.Z, eye.Z - (float)(z0 + WorldLayout.ZoneSize)), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
