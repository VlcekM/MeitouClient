using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// Splicing a long route (docs/game/pathfinding.md "Long routes", engine choice): when zones along a walker's path go live, or it reaches the provisional part, it asks
/// for a path only to a point of its own path ahead and replaces the path up to that point, keeping the rest and following the old path until the answer comes.
/// Asking for the whole way each time turned walkers about and dropped orders whenever one answer failed.
/// </summary>
public sealed partial class MovementSystem
{
    /// <summary>How far along its path (units) a splice reaches at least: about the zones the long route loads ahead (engine choice, the navmesh's lookahead).</summary>
    public const float SpliceReach = 4000;

    /// <summary>
    /// Asks for a splice of the walker's path (partition-safe: its own state): to the point <see cref="SpliceReach"/> along it, or past the last of
    /// <paramref name="zones"/> it crosses when that is further.
    /// </summary>
    static void AskSplice(ref CharacterHot n, CharacterCold cold, IReadOnlySet<ZoneCoordinate>? zones = null)
    {
        if ((n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0 || n.PathCursor >= cold.Path.Length) return;
        int end = Math.Max(ReachEnd(n.Position, cold.Path, n.PathCursor), zones is null ? -1 : LastCrossing(n.Position, cold.Path, n.PathCursor, zones));
        cold.SplicePath = cold.Path;
        cold.SpliceEnd = end;
        cold.SpliceGoal = n.Goal;
        n.Flags |= (ushort)MoveFlags.NeedPath;
    }

    /// <summary>The last point of the path from <paramref name="cursor"/> within <see cref="SpliceReach"/> of <paramref name="at"/> along it (the next point at least).</summary>
    static int ReachEnd(Vector3 at, Vector3[] path, int cursor)
    {
        var a = new Vector2(at.X, at.Z);
        float along = 0;
        int end = cursor;
        for (int i = cursor; i < path.Length; i++)
        {
            var b = new Vector2(path[i].X, path[i].Z);
            along += Vector2.Distance(a, b);
            if (along > SpliceReach && i > cursor) break;
            end = i;
            a = b;
        }
        return end;
    }

    /// <summary>The end of the last leg from <paramref name="cursor"/> on that enters one of <paramref name="zones"/> (-1: none does).</summary>
    static int LastCrossing(Vector3 at, Vector3[] path, int cursor, IReadOnlySet<ZoneCoordinate> zones)
    {
        int last = -1;
        var a = at;
        for (int i = cursor; i < path.Length; i++)
        {
            if (Crosses(a, [path[i]], 0, zones)) last = i;
            a = path[i];
        }
        return last;
    }

    /// <summary>The point a splice asked for goes to; null when the request is for the whole way (no splice, or the path or goal changed since).</summary>
    static Vector3? SpliceTarget(in CharacterHot n, CharacterCold cold)
    {
        if (cold.SplicePath is not { } path || !ReferenceEquals(path, cold.Path) || cold.SpliceGoal != n.Goal) return null;
        if ((n.Flags & (ushort)MoveFlags.HasPath) == 0 || cold.SpliceEnd < n.PathCursor || cold.SpliceEnd >= path.Length) return null;
        return path[cold.SpliceEnd];
    }

    /// <summary>
    /// Takes a splice's answer (true), or asks for the whole way when it found none or the path changed meanwhile (false: the caller handles the answer as an
    /// ordinary one when it was not a splice at all).
    /// </summary>
    static bool TakeSpliceAnswer(IWalkability walk, ref CharacterHot n, CharacterCold cold, PathAnswer answer)
    {
        if (answer.Id != cold.SpliceRequest) return false;
        var old = cold.SplicePath;
        cold.SpliceRequest = 0;
        cold.SplicePath = null;
        if (answer.Path.Found && old is not null && ReferenceEquals(old, cold.Path) && (n.Flags & (ushort)MoveFlags.HasPath) != 0) Splice(walk, ref n, cold, old, answer.Path);
        else n.Flags |= (ushort)MoveFlags.NeedPath;
        return true;
    }

    /// <summary>The found path in place of the old one up to and with its point <see cref="CharacterCold.SpliceEnd"/>; the rest of the old one follows it.</summary>
    static void Splice(IWalkability walk, ref CharacterHot n, CharacterCold cold, Vector3[] old, PathResult head)
    {
        int end = cold.SpliceEnd, added = head.Points.Count - 1;
        cold.Path = [.. head.Points.Skip(1), .. old.AsSpan(end + 1)];
        cold.PathExact = head.IsProvisional ? head.Exact - 1
            : cold.PathExact == int.MaxValue ? int.MaxValue
            : added + Math.Max(cold.PathExact - (end + 1), 0);
        if (head.Exact > 1) cold.ProvisionalBlocked = 0;
        n.PathCursor = Join(walk, n.Position, head.Points[0], cold.Path);
    }
}
