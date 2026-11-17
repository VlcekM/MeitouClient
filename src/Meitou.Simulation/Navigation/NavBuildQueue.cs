using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The zones waiting for the navmesh worker (<see cref="NavMeshService"/>), served in the order the player's characters reach them (engine choice):
/// first the zones their paths enter, the nearest along the path first, then the others by their distance from those characters, the oldest of equal
/// ones first. In request order a zone a fast walker was about to enter waited behind zones asked for earlier (the camera's block, other walkers'
/// routes), and the walker stood frozen at the border meanwhile (#824, docs/game/pathfinding.md "Long routes").
/// </summary>
internal sealed class NavBuildQueue
{
    /// <summary>
    /// How far (units) along a player character's path the zones it enters are ranked by that distance; farther ones count as off its path.
    /// </summary>
    const float RouteReach = 20000;

    /// <summary>
    /// The spacing (units) of the points along a path whose zone is looked up.
    /// </summary>
    const float RouteStep = 150;

    /// <summary>
    /// Added to the distance of a zone no path enters, so it is served after every zone one does.
    /// </summary>
    const float OffRoute = 1e7f;

    /// <summary>
    /// How often (s) the ranking is made again while no zone waits, so the first zone of the next batch is taken by a recent one (with a zone
    /// waiting, at every <see cref="Favour"/>).
    /// </summary>
    const double IdleRankSeconds = 1;

    /// <summary>
    /// The queued zones in request order (a zone may be in it twice; the worker skips one no longer pending). Guarded by itself.
    /// </summary>
    readonly List<ZoneCoordinate> _zones = [];

    /// <summary>
    /// Counts the queued zones, plus one once closed, for the worker to wait on.
    /// </summary>
    readonly SemaphoreSlim _count = new(0);

    /// <summary>
    /// What the zones are ranked by, replaced whole by <see cref="Favour"/>.
    /// </summary>
    volatile Ranking _ranking = new([], []);

    /// <summary>
    /// When the ranking was last made (a <see cref="System.Diagnostics.Stopwatch"/> timestamp; the caller's thread only).
    /// </summary>
    long _rankedAt;

    /// <summary>
    /// Set by <see cref="Close"/>: <see cref="Take"/> returns null.
    /// </summary>
    volatile bool _closed;

    /// <summary>
    /// The zones the player's characters' paths enter with the distance along the path to the first point in each (<see cref="Reach"/>), and where
    /// those characters stand (X, Z; <see cref="Places"/>).
    /// </summary>
    sealed record Ranking(Dictionary<ZoneCoordinate, float> Reach, List<Vector2> Places);

    /// <summary>
    /// Queues a zone.
    /// </summary>
    public void Add(ZoneCoordinate zone)
    {
        lock (_zones) _zones.Add(zone);
        _count.Release();
    }

    /// <summary>
    /// Ranks the queued zones by the player's characters of <paramref name="world"/> as they stand now and the paths they follow; read between ticks,
    /// before the zones of the frame are asked for. While no zone waits, at most every <see cref="IdleRankSeconds"/>.
    /// </summary>
    public void Favour(World world)
    {
        bool empty;
        lock (_zones) empty = _zones.Count == 0;
        if (empty && System.Diagnostics.Stopwatch.GetElapsedTime(_rankedAt).TotalSeconds < IdleRankSeconds) return;

        _rankedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        var ranking = new Ranking([], []);
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref readonly var n = ref table.Previous[i];
            if (!n.Alive || table.Cold(i) is not { IsPlayer: true } cold) continue;

            ranking.Places.Add(new Vector2(n.Position.X, n.Position.Z));
            if (((MoveFlags)n.Flags & MoveFlags.HasPath) != 0) AddRoute(ranking.Reach, n.Position, cold.Path, n.PathCursor);
        }

        _ranking = ranking;
    }

    /// <summary>
    /// Waits for a queued zone and takes the first in the ranking; null once closed.
    /// </summary>
    public ZoneCoordinate? Take()
    {
        _count.Wait();
        if (_closed) return null;

        lock (_zones)
        {
            int index = First(_zones, _ranking);
            var zone = _zones[index];
            _zones.RemoveAt(index);
            return zone;
        }
    }

    /// <summary>
    /// Wakes the worker for good: <see cref="Take"/> returns null.
    /// </summary>
    public void Close()
    {
        _closed = true;
        _count.Release();
    }

    /// <summary>
    /// Records in <paramref name="reach"/> each zone the way from <paramref name="from"/> along <paramref name="path"/> (from
    /// <paramref name="cursor"/>) enters within <see cref="RouteReach"/>, with the distance to its first point there, the least of every path.
    /// </summary>
    static void AddRoute(Dictionary<ZoneCoordinate, float> reach, Vector3 from, Vector3[] path, int cursor)
    {
        var a = new Vector2(from.X, from.Z);
        float along = 0;
        for (int i = Math.Max(cursor, 0); i < path.Length && along <= RouteReach; i++)
        {
            var b = new Vector2(path[i].X, path[i].Z);
            float length = Vector2.Distance(a, b);
            for (float d = 0; d < length && along + d <= RouteReach; d += RouteStep)
                Reached(reach, Vector2.Lerp(a, b, d / length), along + d);

            Reached(reach, b, along + length);
            along += length;
            a = b;
        }
    }

    /// <summary>
    /// Keeps <paramref name="along"/> as the zone of <paramref name="at"/>'s distance when it is the least yet.
    /// </summary>
    static void Reached(Dictionary<ZoneCoordinate, float> reach, Vector2 at, float along)
    {
        var zone = WorldLayout.ZoneOf(at.X, at.Y);
        if (!reach.TryGetValue(zone, out float known) || along < known) reach[zone] = along;
    }

    /// <summary>
    /// The index of the zone ranked first: the least distance along a path, else the least distance from a player character plus
    /// <see cref="OffRoute"/>; the first of equal ones (with no player character, the first at all).
    /// </summary>
    static int First(List<ZoneCoordinate> zones, Ranking ranking)
    {
        int best = 0;
        float bestScore = float.MaxValue;
        for (int i = 0; i < zones.Count && ranking.Places.Count > 0; i++)
        {
            float score = ranking.Reach.TryGetValue(zones[i], out float along) ? along : OffRoute + DistanceTo(zones[i], ranking.Places);
            if (score >= bestScore) continue;

            best = i;
            bestScore = score;
        }

        return best;
    }

    /// <summary>
    /// The distance from the nearest of <paramref name="places"/> to the zone's square (0 inside it).
    /// </summary>
    static float DistanceTo(ZoneCoordinate zone, List<Vector2> places)
    {
        var (x, z) = WorldLayout.ZoneOrigin(zone);
        var min = new Vector2((float)x, (float)z);
        var max = min + new Vector2(WorldLayout.ZoneSize);
        float best = float.MaxValue;
        foreach (var p in places)
            best = MathF.Min(best, Vector2.Distance(p, Vector2.Clamp(p, min, max)));

        return best;
    }
}
