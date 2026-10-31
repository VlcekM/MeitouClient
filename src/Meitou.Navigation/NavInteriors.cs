using System.Runtime.InteropServices;
using System.Numerics;

namespace Meitou.Navigation;

/// <summary>
/// Building interiors: each building with an interior mask is built as its own small mesh (<see cref="ZoneGeometryGatherer.GatherInterior"/>) and put into
/// the zone's mesh as extra polygons, joined to the exterior along the door polygons (docs/game/pathfinding.md, "Interiors"). To the queries an interior
/// is part of the zone: the door is an ordinary link.
/// </summary>
internal static class NavInteriors
{
    /// <summary>How close (units) two door edges have to be to be joined, and how far apart in height (engine choices).</summary>
    public const float JoinDistance = 6, JoinHeight = 12;
    /// <summary>How far (units) round a door's polygons its approach reaches when the door itself joins nothing (engine choice: a flight of steps).</summary>
    public const float ApproachReach = 32;
    /// <summary>How close (units) a doorless interior's open edges and the exterior's have to be to be joined: both are cut on the hull's line, a cell or so apart (engine choice).</summary>
    public const float HullJoinDistance = 2.5f;
    /// <summary>
    /// The same for a ruin (a destroyed interior): the original stitches edges up to 2 apart (FUN_1403c5830, maxSeparation 0.2 Havok), both cut exactly on
    /// the hull; ours come out off that line by up to a cell and the contour error (1.3 cells) each (engine choice; docs/game/pathfinding.md "Destroyed interiors").
    /// </summary>
    public const float RuinHullJoinDistance = 5f;

    /// <summary>
    /// The zone's exterior mesh with the interior meshes appended and joined; both kept and pruned polygons are carried over. A doorless interior (an intact
    /// building without doors, or a ruin) is joined along its hull instead of at its doors, a ruin within <see cref="RuinHullJoinDistance"/>.
    /// </summary>
    public static ZoneNavMesh Combine(ZoneNavMesh exterior, IReadOnlyList<InteriorMesh> interiors, Action<string>? log = null)
    {
        if (interiors.Count == 0) return exterior;
        var vertices = new List<Vector3>(exterior.Vertices);
        var polygons = new List<int[]>(exterior.Polygons);
        var neighbours = new List<int[]>(exterior.Neighbours);
        var areas = new List<byte>(exterior.Areas);
        var kept = new List<bool>(exterior.Kept);
        var links = new List<List<int>?>();
        for (int p = 0; p < exterior.PolygonCount; p++) links.Add(exterior.LinksOf(p).Length > 0 ? [.. exterior.LinksOf(p).ToArray()] : null);
        var doorIds = new List<string>(exterior.DoorIds ?? []);
        var doorOf = new List<int>(exterior.DoorOf ?? Enumerable.Repeat(-1, exterior.PolygonCount));
        // The zone keeps its own bounds: cross-zone linking finds the borders by them (an interior's padded box may reach past them).
        var lo = exterior.BoundsMin;
        var hi = exterior.BoundsMax;
        var offsets = new List<int>();

        foreach (var m in interiors.Select(i => i.Mesh))
        {
            int vo = vertices.Count, po = polygons.Count;
            offsets.Add(po);
            vertices.AddRange(m.Vertices);
            var remap = (m.DoorIds ?? []).Select(id => { int i = doorIds.IndexOf(id); if (i < 0) { i = doorIds.Count; doorIds.Add(id); } return i; }).ToArray();
            for (int p = 0; p < m.PolygonCount; p++)
            {
                polygons.Add(m.Polygons[p].Select(i => i + vo).ToArray());
                neighbours.Add(m.Neighbours[p].Select(n => n >= 0 ? n + po : -1).ToArray());
                areas.Add(m.Areas[p]);
                kept.Add(m.Kept[p]);
                var l = m.LinksOf(p);
                List<int>? list = null;
                if (l.Length > 0)
                {
                    list = [];
                    for (int i = 0; i < l.Length; i += 2) { list.Add(l[i]); list.Add(l[i + 1] + po); }
                }
                links.Add(list);
                doorOf.Add(m.DoorOf is { } d && d[p] >= 0 ? remap[d[p]] : -1);
            }
        }

        int exteriorCount = exterior.PolygonCount;
        for (int k = 0; k < interiors.Count; k++)
            Join(vertices, polygons, neighbours, areas, kept, links, doorOf, doorIds, exteriorCount, offsets[k], interiors[k].Mesh.PolygonCount, interiors[k].Doorless,
                interiors[k].Key ?? $"interior {k}", log, interiors[k].Ruin ? RuinHullJoinDistance : HullJoinDistance);

        CarryKeep(polygons.Count, neighbours, links, areas, kept);

        var heights = new List<HeightGrid?>();
        foreach (var m in interiors.Select(i => i.Mesh).Prepend(exterior))
            for (int p = 0; p < m.PolygonCount; p++) heights.Add(m.Heights?[p]);
        return new ZoneNavMesh
        {
            ZoneX = exterior.ZoneX, ZoneZ = exterior.ZoneZ, Vertices = [.. vertices], Polygons = [.. polygons], Neighbours = [.. neighbours], Areas = [.. areas],
            Links = links.Select(l => l is { Count: > 0 } ? l.ToArray() : null).ToArray(), Kept = [.. kept], BoundsMin = lo, BoundsMax = hi,
            DoorIds = doorIds.Count > 0 ? [.. doorIds] : null, DoorOf = doorIds.Count > 0 ? [.. doorOf] : null,
            Surfaces = new NavSurfaces([.. (exterior.Surfaces?.Corners ?? []).Concat(interiors.SelectMany(i => i.Mesh.Surfaces?.Corners ?? []))]),
            Terrain = exterior.Terrain,
            Heights = ZoneNavMesh.HeightsOf(heights),
        };
    }

    /// <summary>
    /// An interior is joined whatever its own pruning kept (it may have no seed: an Ancient Lab or Ash Dome without doors or nodes), to exterior polygons
    /// the exterior's pruning kept, and the regions joined to a kept polygon are kept too (engine choice; the original connects these interiors,
    /// docs/game/pathfinding.md "Door joins").
    /// </summary>
    static void CarryKeep(int count, List<int[]> neighbours, List<List<int>?> links, List<byte> areas, List<bool> kept)
    {
        var queue = new Queue<int>();
        for (int p = 0; p < count; p++) if (kept[p]) queue.Enqueue(p);
        while (queue.Count > 0)
        {
            int p = queue.Dequeue();
            foreach (int n in neighbours[p]) Keep(n);
            if (links[p] is { } l) for (int i = 1; i < l.Count; i += 2) Keep(l[i]);
        }

        void Keep(int n)
        {
            if (n < 0 || kept[n] || areas[n] == NavArea.Null) return;
            kept[n] = true;
            queue.Enqueue(n);
        }
    }

    /// <summary>Links the door polygons of one interior (polygons [first, first + count)) to the exterior's door polygons of the same door, or a doorless one along its hull.</summary>
    static void Join(List<Vector3> v, List<int[]> polygons, List<int[]> neighbours, List<byte> areas, List<bool> kept, List<List<int>?> links, List<int> doorOf, List<string> doorIds,
        int exteriorCount, int first, int count, bool doorless, string name, Action<string>? log, float hullDistance)
    {
        bool Free(int p, int e) => neighbours[p][e] < 0 && !NavGeometry.HasLink(CollectionsMarshal.AsSpan(links[p]), e);
        for (int door = 0; door < doorIds.Count; door++)
        {
            var inside = Enumerable.Range(first, count).Where(p => areas[p] == NavArea.Door && doorOf[p] == door).ToList();
            if (inside.Count == 0) continue;
            var outside = Enumerable.Range(0, exteriorCount).Where(p => kept[p] && areas[p] == NavArea.Door && doorOf[p] == door).ToList();
            // An edge that faces several polygons links to all of them, so which edges are open is decided before any link is added.
            var openInside = inside.SelectMany(p => Enumerable.Range(0, polygons[p].Length).Where(e => Free(p, e)).Select(e => (p, e))).ToList();
            if (Link(openInside, OpenEdges(outside), doorIds[door]) > 0) continue;
            // A door leaf set back inside the interior hull paints nothing outside it (the exterior is carved to the hull): the doorway's interior
            // polygons meet the exterior's ground along the hull's edge (Observed: the L-House 13-rebirth-INGAME, zone 18.35).
            var lo = new Vector2(float.MaxValue); var hi = new Vector2(float.MinValue);
            foreach (int p in inside)
                foreach (int i in polygons[p]) { lo = Vector2.Min(lo, new(v[i].X, v[i].Z)); hi = Vector2.Max(hi, new(v[i].X, v[i].Z)); }
            lo -= new Vector2(JoinDistance); hi += new Vector2(JoinDistance);
            var near = Enumerable.Range(0, exteriorCount).Where(q => kept[q] && areas[q] != NavArea.Null && polygons[q].Any(i => v[i].X >= lo.X && v[i].X <= hi.X && v[i].Z >= lo.Y && v[i].Z <= hi.Y)).ToList();
            if (Link(openInside, OpenEdges(near), $"{doorIds[door]} (doorway edge)") > 0) continue;
            // A raised sill reached by steps that cross the hull: the steps' upper part is the interior's (joined to the door polygons), the lower part the
            // exterior's, and the two meet on the hull's edge below the door (Observed: 29- and 27-rebirth-INGAME, zone 18.35). So the interior polygons
            // connected to the door within the approach box are joined too.
            lo -= new Vector2(ApproachReach); hi += new Vector2(ApproachReach);
            bool InBox(int p) => polygons[p].All(i => v[i].X >= lo.X && v[i].X <= hi.X && v[i].Z >= lo.Y && v[i].Z <= hi.Y);
            var approach = new HashSet<int>(inside);
            var queue = new Queue<int>(inside);
            while (queue.Count > 0)
                foreach (int n in neighbours[queue.Dequeue()])
                    if (n >= 0 && areas[n] != NavArea.Null && InBox(n) && approach.Add(n)) queue.Enqueue(n);
            var openApproach = approach.SelectMany(p => Enumerable.Range(0, polygons[p].Length).Where(e => Free(p, e)).Select(e => (p, e))).ToList();
            near = [.. Enumerable.Range(0, exteriorCount).Where(q => kept[q] && areas[q] != NavArea.Null && polygons[q].Any(i => v[i].X >= lo.X && v[i].X <= hi.X && v[i].Z >= lo.Y && v[i].Z <= hi.Y))];
            if (Link(openApproach, OpenEdges(near), $"{doorIds[door]} (approach)") == 0)
                log?.Invoke($"    door {doorIds[door]} joins nothing: {inside.Count} interior door polygons ({openInside.Count} open edges), {outside.Count} exterior door polygons, {near.Count} kept exterior polygons in the approach box");
        }

        // A doorless interior (an open doorway): the hull cuts the exterior and the interior along the same line, so their edges meet there.
        if (doorless)
        {
            var lo = new Vector2(float.MaxValue); var hi = new Vector2(float.MinValue);
            var inside = Enumerable.Range(first, count).Where(p => areas[p] != NavArea.Null).ToList();
            foreach (int p in inside)
                foreach (int i in polygons[p]) { lo = Vector2.Min(lo, new(v[i].X, v[i].Z)); hi = Vector2.Max(hi, new(v[i].X, v[i].Z)); }
            lo -= new Vector2(JoinDistance); hi += new Vector2(JoinDistance);
            var near = Enumerable.Range(0, exteriorCount).Where(q => kept[q] && areas[q] != NavArea.Null && polygons[q].Any(i => v[i].X >= lo.X && v[i].X <= hi.X && v[i].Z >= lo.Y && v[i].Z <= hi.Y)).ToList();
            var openInside = inside.SelectMany(p => Enumerable.Range(0, polygons[p].Length).Where(e => Free(p, e)).Select(e => (p, e))).ToList();
            Link(openInside, OpenEdges(near), $"{name} (doorless hull edge)", hullDistance);
        }

        List<(int, int)> OpenEdges(IEnumerable<int> outside) => [.. outside.SelectMany(q => Enumerable.Range(0, polygons[q].Length).Where(f => Free(q, f)).Select(f => (q, f)))];

        int Link(List<(int, int)> openInside, List<(int, int)> openOutside, string kind, float distance = JoinDistance)
        {
            int joined = 0;
            foreach (var (p, e) in openInside)
                foreach (var (q, f) in openOutside)
                {
                    var a0 = v[polygons[p][e]]; var a1 = v[polygons[p][(e + 1) % polygons[p].Length]];
                    var b0 = v[polygons[q][f]]; var b1 = v[polygons[q][(f + 1) % polygons[q].Length]];
                    if (!Facing(a0, a1, b0, b1, distance)) continue;
                    log?.Invoke($"    door join {kind}: interior polygon {p - first} edge {e} to exterior {q} edge {f}");
                    (links[p] ??= []).AddRange([e, q]);
                    (links[q] ??= []).AddRange([f, p]);
                    joined++;
                }
            return joined;
        }
    }

    /// <summary>Two edges that run along each other: their midpoints and both ends within <paramref name="distance"/> in plan and <see cref="JoinHeight"/> in height, overlapping.</summary>
    static bool Facing(Vector3 a0, Vector3 a1, Vector3 b0, Vector3 b1, float distance = JoinDistance)
    {
        var ma = (a0 + a1) * 0.5f; var mb = (b0 + b1) * 0.5f;
        if (MathF.Abs(ma.Y - mb.Y) > JoinHeight) return false;
        var d = new Vector2(a1.X - a0.X, a1.Z - a0.Z);
        float len = d.Length();
        if (len < 1e-4f) return false;
        d /= len;
        float Along(Vector3 p) => (p.X - a0.X) * d.X + (p.Z - a0.Z) * d.Y;
        float Off(Vector3 p) => MathF.Abs((p.X - a0.X) * d.Y - (p.Z - a0.Z) * d.X);
        if (Off(b0) > distance || Off(b1) > distance) return false;
        float lo = Math.Min(Along(b0), Along(b1)), hi = Math.Max(Along(b0), Along(b1));
        return Math.Min(hi, len) - Math.Max(lo, 0) > 0.5f;
    }
}

/// <summary>
/// One built interior for <see cref="NavInteriors.Combine"/>: its mesh, its building's placement key (labels the join log and names it in the tool), whether it is
/// joined along its hull (an intact building without doors, or a ruin) and whether it is a ruin (joined within <see cref="NavInteriors.RuinHullJoinDistance"/>).
/// </summary>
internal sealed record InteriorMesh(ZoneNavMesh Mesh, string? Key = null, bool Doorless = false, bool Ruin = false);
