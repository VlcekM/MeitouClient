using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

public sealed partial class NavmeshWalkability
{
    /// <summary>
    /// How far (units) above or below the walker a wall's edge may lie and still stop it (engine choice: the mesh's own error on stairs, <see cref="SnapRange"/>;
    /// the original's agent takes edges within 1 Havok unit, 10, of its own height, docs/game/pathfinding.md "Walls").
    /// </summary>
    const float WallHeightWindow = SnapRange;

    [ThreadStatic] static List<Wall>? wallScratch;
    [ThreadStatic] static HashSet<int>? seenScratch;

    /// <summary>One edge of the walkable surface that leads nowhere, on the XZ plane, with its unit normal into the walkable side.</summary>
    readonly record struct Wall(Vector2 A, Vector2 B, Vector2 Inward);

    /// <summary>
    /// The part of a free move (root motion, combat footwork) that a body of <paramref name="radius"/> at <paramref name="from"/> can make: as the original's nav agent,
    /// a circle swept against the walls of the walkable surface round it (the polygon edges that lead nowhere: a wall, a drop, a hole furniture cuts, a closed door)
    /// stops where it would first touch one, and the rest of the step slides along that wall and is swept once more (docs/game/pathfinding.md "Walls").
    /// Only walls the move closes on stop it, so a body already nearer than its radius can still move along or away. Off the loaded zones: the whole step.
    /// </summary>
    public Vector2 Slide(Vector3 from, Vector2 step, float radius)
    {
        var w = world;
        if (step == Vector2.Zero || radius <= 0 || !w.Contains(WorldLayout.ZoneOf(from.X, from.Z))) return step;
        var walls = wallScratch ??= [];
        walls.Clear();
        GatherWalls(w, from, radius + step.Length() + 1, walls);
        if (walls.Count == 0) return step;
        var start = new Vector2(from.X, from.Z);
        float t = Sweep(walls, start, step, radius, out var normal);
        if (t >= 1) return step;
        var at = start + step * t;
        var rest = step * (1 - t);
        float into = Vector2.Dot(rest, normal);
        if (into < 0) rest -= normal * into;
        if (rest.LengthSquared() > 1e-8f) at += rest * Sweep(walls, at, rest, radius, out _);
        return at - start;
    }

    /// <summary>
    /// The fraction of <paramref name="step"/> a circle at <paramref name="from"/> moves before it touches a wall it is closing on (1: none), and the contact normal
    /// (from the wall to the circle).
    /// </summary>
    static float Sweep(List<Wall> walls, Vector2 from, Vector2 step, float radius, out Vector2 normal)
    {
        const float Approach = 1e-4f;
        float best = 1, length = step.Length();
        normal = Vector2.Zero;
        float r2 = radius * radius;
        foreach (var wall in walls)
        {
            var ab = wall.B - wall.A;
            float ab2 = ab.LengthSquared();
            // Already touching: stopped at once when it moves closer, free otherwise.
            var near = wall.A + ab * Math.Clamp(Vector2.Dot(from - wall.A, ab) / ab2, 0, 1);
            var off = from - near;
            float d2 = off.LengthSquared();
            if (d2 < r2)
            {
                var n = d2 > 1e-8f ? off / MathF.Sqrt(d2) : wall.Inward;
                if (Vector2.Dot(step, n) < -Approach * length && best > 0) (best, normal) = (0, n);
                continue;
            }
            // The face: where the circle's edge reaches the wall's line, when that point lies on the wall.
            float closing = -Vector2.Dot(step, wall.Inward);
            if (closing > Approach * length)
            {
                float side = Vector2.Dot(from - wall.A, wall.Inward);
                if (side >= radius)
                {
                    float t = (side - radius) / closing;
                    if (t < best)
                    {
                        float u = Vector2.Dot(from + step * t - wall.A, ab) / ab2;
                        if (u >= 0 && u <= 1)
                        {
                            (best, normal) = (t, wall.Inward);
                            continue;
                        }
                    }
                }
            }
            // The ends: where the circle first reaches A or B.
            Corner(wall.A, from, step, r2, ref best, ref normal);
            Corner(wall.B, from, step, r2, ref best, ref normal);
        }
        // Stop just short of the contact, so the next move does not start inside it.
        if (best < 1 && length > 0) best = MathF.Max(0, best - 1e-3f / length);
        return best;
    }

    static void Corner(Vector2 corner, Vector2 from, Vector2 step, float r2, ref float best, ref Vector2 normal)
    {
        var f = from - corner;
        float a = Vector2.Dot(step, step), b = Vector2.Dot(f, step), c = f.LengthSquared() - r2;
        if (b >= 0 || a < 1e-12f) return;   // moving away from the corner
        float disc = b * b - a * c;
        if (disc < 0) return;
        float t = (-b - MathF.Sqrt(disc)) / a;
        if (t < 0 || t >= best) return;
        best = t;
        normal = Vector2.Normalize(from + step * t - corner);
    }

    /// <summary>The walls within <paramref name="reach"/> of <paramref name="from"/> (XZ) at about its height, over the loaded zones round it.</summary>
    void GatherWalls(NavWorld w, Vector3 from, float reach, List<Wall> walls)
    {
        var seen = seenScratch ??= [];
        var doors = Doors;
        var lo = WorldLayout.ZoneOf(from.X - reach, from.Z - reach);
        var hi = WorldLayout.ZoneOf(from.X + reach, from.Z + reach);
        for (int zz = Math.Min(lo.Y, hi.Y); zz <= Math.Max(lo.Y, hi.Y); zz++)
            for (int zx = Math.Min(lo.X, hi.X); zx <= Math.Max(lo.X, hi.X); zx++)
            {
                if (!w.TryGetSlot(new ZoneCoordinate(zx, zz), out int slot)) continue;
                var m = w.Mesh(slot);
                var ix = m.Index;
                seen.Clear();
                int x0 = ix.Col(from.X - reach - m.BoundsMin.X), x1 = ix.Col(from.X + reach - m.BoundsMin.X);
                int z0 = ix.Row(from.Z - reach - m.BoundsMin.Y), z1 = ix.Row(from.Z + reach - m.BoundsMin.Y);
                for (int cz = z0; cz <= z1; cz++)
                    for (int cx = x0; cx <= x1; cx++)
                        foreach (int p in ix.Cells[cz * ix.Columns + cx])
                        {
                            if (!m.Kept[p] || !seen.Add(p) || ClosedDoor(m, p, doors)) continue;
                            if (from.X < ix.Min[p].X - reach || from.X > ix.Max[p].X + reach || from.Z < ix.Min[p].Y - reach || from.Z > ix.Max[p].Y + reach) continue;
                            AddWalls(m, p, from, reach, doors, walls);
                        }
            }
    }

    static void AddWalls(ZoneNavMesh m, int p, Vector3 from, float reach, NavDoors doors, List<Wall> walls)
    {
        var poly = m.Polygons[p];
        for (int k = 0; k < poly.Length; k++)
        {
            if (Open(m, p, k, doors)) continue;
            var a = m.Vertices[poly[k]];
            var b = m.Vertices[poly[(k + 1) % poly.Length]];
            var ab = new Vector2(b.X - a.X, b.Z - a.Z);
            float ab2 = ab.LengthSquared();
            if (ab2 < 1e-8f) continue;
            float u = Math.Clamp(((from.X - a.X) * ab.X + (from.Z - a.Z) * ab.Y) / ab2, 0, 1);
            float nx = a.X + ab.X * u - from.X, nz = a.Z + ab.Y * u - from.Z;
            if (nx * nx + nz * nz > reach * reach || MathF.Abs(a.Y + (b.Y - a.Y) * u - from.Y) > WallHeightWindow) continue;
            // Polygons wind so that the inside is left of each edge seen from above (ZoneNavMesh.Contains).
            walls.Add(new Wall(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), Vector2.Normalize(new Vector2(-ab.Y, ab.X))));
        }
    }

    /// <summary>Whether a walker can cross edge <paramref name="k"/> of polygon <paramref name="p"/>: into a kept polygon that is not a closed door, over a tile link, or over the zone's border.</summary>
    static bool Open(ZoneNavMesh m, int p, int k, NavDoors doors)
    {
        int q = m.Neighbours[p][k];
        if (q >= 0) return m.Kept[q] && !ClosedDoor(m, q, doors);
        if (m.HasLink(p, k)) return true;
        var poly = m.Polygons[p];
        var a = m.Vertices[poly[k]];
        var b = m.Vertices[poly[(k + 1) % poly.Length]];
        // The next zone continues there (linked when loaded; where it is not, the open-ground stand-in answers).
        return NavGeometry.OnLine(a.X, b.X, m.BoundsMin.X) || NavGeometry.OnLine(a.X, b.X, m.BoundsMax.X)
            || NavGeometry.OnLine(a.Z, b.Z, m.BoundsMin.Y) || NavGeometry.OnLine(a.Z, b.Z, m.BoundsMax.Y);
    }

    static bool ClosedDoor(ZoneNavMesh m, int p, NavDoors doors) => m.DoorOf is { } of && of[p] is var d and >= 0 && doors.IsClosed(m.DoorIds![d]);
}
