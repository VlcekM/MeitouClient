using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>
/// The navigation of a running game in one object, for the simulation host: the <see cref="Walkability"/> to hand to the simulation (replacing the
/// open-ground stand-in), the <see cref="Doors"/> table, and the zone load/unload calls that feed the background builder.
/// <para>Thread-safety: <see cref="Walkability"/> (<c>IsWalkable</c>, <c>GroundHeight</c>, <c>FindPath</c>) and <see cref="Doors"/> may be used from any
/// thread at any time; a zone's mesh appears in a path query when its build finishes (never half), until then the stand-in answers for that zone.
/// <see cref="LoadZone"/>, <see cref="LoadRing"/> and <see cref="UnloadZone"/> may be called from any thread and return at once.</para>
/// </summary>
public sealed class NavSystem : IDisposable
{
    readonly NavMeshService service;

    public NavSystem(GameInstall install, GameDatabase db, WorldLevelData levels, Func<float, float, float> terrainHeight, NavBuildSettings? settings = null, NavMeshCache? cache = null)
    {
        Walkability = new NavmeshWalkability(terrainHeight);
        service = new NavMeshService(install, db, levels, Walkability, settings, cache);
    }

    public NavmeshWalkability Walkability { get; }

    /// <summary>Open or close a door by its building's instance id: <c>Doors.Close(id)</c>. No rebuild; the next query sees it.</summary>
    public NavDoors Doors => Walkability.Doors;

    /// <summary>Raised on the builder thread when a zone's mesh went live.</summary>
    public event Action<NavZoneReady>? ZoneReady
    {
        add => service.ZoneReady += value;
        remove => service.ZoneReady -= value;
    }

    /// <summary>Asks for a zone's mesh (from the cache, else built); the task completes when it is live. Asking twice is harmless.</summary>
    public Task<NavZoneReady> LoadZone(ZoneCoordinate zone) => service.Request(zone);

    /// <summary>Asks for a zone and its neighbours within <paramref name="radius"/> (1 = the 3 × 3 ring around the player), nearest first.</summary>
    public void LoadRing(ZoneCoordinate centre, int radius = 1)
    {
        var zones = new List<ZoneCoordinate>();
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                var z = new ZoneCoordinate(centre.X + dx, centre.Y + dz);
                if (z.IsInsideGrid) zones.Add(z);
            }
        foreach (var z in zones.OrderBy(z => Math.Abs(z.X - centre.X) + Math.Abs(z.Y - centre.Y))) _ = service.Request(z);
    }

    /// <summary>Drops a zone's mesh (its cache file stays). Paths already handed out stay valid as lists of points.</summary>
    public void UnloadZone(ZoneCoordinate zone) => service.Unload(zone);

    public void Dispose() => service.Dispose();
}
