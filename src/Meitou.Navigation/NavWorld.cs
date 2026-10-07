using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Navigation;

/// <summary>A polygon of a loaded zone: the zone's slot in the <see cref="NavWorld"/> and the polygon's index.</summary>
public readonly record struct NavRef(int Zone, int Polygon)
{
    public long Key => ((long)Zone << 32) | (uint)Polygon;
}

/// <summary>A link across a zone border: the edge of a polygon and the polygon of the neighbouring zone behind it.</summary>
public readonly record struct NavLink(int Edge, NavRef To);

/// <summary>
/// An immutable set of loaded zone meshes with the links across zone borders. A new snapshot is made when a zone is added or removed, so readers on
/// other threads never see a half-linked world.
/// </summary>
public sealed class NavWorld
{
    /// <summary>The empty world.</summary>
    public static readonly NavWorld Empty = new([], [], []);

    readonly ZoneNavMesh[] meshes;
    readonly Dictionary<ZoneCoordinate, int> slots;
    readonly Dictionary<long, NavLink[]> links;

    NavWorld(ZoneNavMesh[] meshes, Dictionary<ZoneCoordinate, int> slots, Dictionary<long, NavLink[]> links)
    {
        this.meshes = meshes;
        this.slots = slots;
        this.links = links;
    }

    public int ZoneCount => meshes.Length;
    public ZoneNavMesh Mesh(int slot) => meshes[slot];
    public IEnumerable<ZoneCoordinate> Zones => slots.Keys;
    public bool Contains(ZoneCoordinate zone) => slots.ContainsKey(zone);
    public bool TryGetSlot(ZoneCoordinate zone, out int slot) => slots.TryGetValue(zone, out slot);
    public ZoneNavMesh? Find(ZoneCoordinate zone) => slots.TryGetValue(zone, out var s) ? meshes[s] : null;

    /// <summary>The links of a polygon across zone borders (empty for nearly all).</summary>
    public ReadOnlySpan<NavLink> LinksOf(NavRef p) => links.TryGetValue(p.Key, out var l) ? l : [];

    /// <summary>A new world with <paramref name="mesh"/> added (or replacing the zone's), linked to its loaded neighbours.</summary>
    public NavWorld With(ZoneNavMesh mesh, float maxStep = 5.5f)
    {
        var zone = new ZoneCoordinate(mesh.ZoneX, mesh.ZoneZ);
        var list = meshes.ToList();
        var newSlots = new Dictionary<ZoneCoordinate, int>(slots);
        int slot;
        if (newSlots.TryGetValue(zone, out slot)) list[slot] = mesh;
        else { slot = list.Count; list.Add(mesh); newSlots[zone] = slot; }

        // Links that touch the replaced zone are rebuilt; the rest carry over.
        var newLinks = new Dictionary<long, NavLink[]>();
        foreach (var (key, value) in links)
        {
            int zoneOfKey = (int)(key >> 32);
            if (zoneOfKey == slot) continue;
            var kept = value.Where(l => l.To.Zone != slot).ToArray();
            if (kept.Length > 0) newLinks[key] = kept;
        }
        var world = new NavWorld([.. list], newSlots, newLinks);
        foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            if (newSlots.TryGetValue(new ZoneCoordinate(zone.X + dx, zone.Y + dz), out int other))
                world.LinkZones(slot, other, dx, dz, maxStep);
        return world;
    }

    /// <summary>A new world without the zone.</summary>
    public NavWorld Without(ZoneCoordinate zone)
    {
        if (!slots.TryGetValue(zone, out int removed)) return this;
        var kept = slots.Where(kv => kv.Key != zone).OrderBy(kv => kv.Value).ToList();
        var remap = new Dictionary<int, int>();
        var newMeshes = new List<ZoneNavMesh>();
        var newSlots = new Dictionary<ZoneCoordinate, int>();
        foreach (var (z, old) in kept) { remap[old] = newMeshes.Count; newSlots[z] = newMeshes.Count; newMeshes.Add(meshes[old]); }
        var newLinks = new Dictionary<long, NavLink[]>();
        foreach (var (key, value) in links)
        {
            int zoneOfKey = (int)(key >> 32);
            if (zoneOfKey == removed) continue;
            var ls = value.Where(l => l.To.Zone != removed).Select(l => l with { To = new NavRef(remap[l.To.Zone], l.To.Polygon) }).ToArray();
            if (ls.Length > 0) newLinks[new NavRef(remap[zoneOfKey], (int)(key & 0xffffffff)).Key] = ls;
        }
        return new NavWorld([.. newMeshes], newSlots, newLinks);
    }

    /// <summary>Links the border edges of two adjacent zones where they overlap. (dx, dz) is the direction from <paramref name="a"/> to <paramref name="b"/>.</summary>
    void LinkZones(int a, int b, int dx, int dz, float maxStep)
    {
        var ma = meshes[a];
        var mb = meshes[b];
        var edgesA = BorderEdges(ma, dx, dz);
        var edgesB = BorderEdges(mb, -dx, -dz);
        bool alongX = dz != 0; // the shared border line runs along X when the zones are stacked in Z
        foreach (var ea in edgesA)
            foreach (var eb in edgesB)
            {
                var (a0, a1, ay0, ay1) = Span(ma, ea, alongX);
                var (b0, b1, by0, by1) = Span(mb, eb, alongX);
                float lo = Math.Max(a0, b0), hi = Math.Min(a1, b1);
                if (hi - lo < 0.01f) continue;
                float mid = (lo + hi) / 2;
                if (Math.Abs(Lerp(a0, a1, ay0, ay1, mid) - Lerp(b0, b1, by0, by1, mid)) > maxStep) continue;
                Add(new NavRef(a, ea.Polygon), new NavLink(ea.Edge, new NavRef(b, eb.Polygon)));
                Add(new NavRef(b, eb.Polygon), new NavLink(eb.Edge, new NavRef(a, ea.Polygon)));
            }
    }

    readonly record struct BorderEdge(int Polygon, int Edge);

    static List<BorderEdge> BorderEdges(ZoneNavMesh m, int dx, int dz)
    {
        var result = new List<BorderEdge>();
        float line = dx > 0 ? m.BoundsMax.X : dx < 0 ? m.BoundsMin.X : dz > 0 ? m.BoundsMax.Y : m.BoundsMin.Y;
        for (int p = 0; p < m.PolygonCount; p++)
        {
            if (!m.Kept[p]) continue;
            var poly = m.Polygons[p];
            for (int k = 0; k < poly.Length; k++)
            {
                if (m.Neighbours[p][k] >= 0) continue;
                var va = m.Vertices[poly[k]];
                var vb = m.Vertices[poly[(k + 1) % poly.Length]];
                float ca = dx != 0 ? va.X : va.Z, cb = dx != 0 ? vb.X : vb.Z;
                if (Math.Abs(ca - line) < 0.05f && Math.Abs(cb - line) < 0.05f) result.Add(new BorderEdge(p, k));
            }
        }
        return result;
    }

    static (float, float, float, float) Span(ZoneNavMesh m, BorderEdge e, bool alongX)
    {
        var poly = m.Polygons[e.Polygon];
        var a = m.Vertices[poly[e.Edge]];
        var b = m.Vertices[poly[(e.Edge + 1) % poly.Length]];
        float ta = alongX ? a.X : a.Z, tb = alongX ? b.X : b.Z;
        return ta <= tb ? (ta, tb, a.Y, b.Y) : (tb, ta, b.Y, a.Y);
    }

    static float Lerp(float a0, float a1, float y0, float y1, float at) => a1 - a0 < 1e-5f ? y0 : y0 + (y1 - y0) * (at - a0) / (a1 - a0);

    void Add(NavRef from, NavLink link)
    {
        var existing = links.TryGetValue(from.Key, out var l) ? l : [];
        links[from.Key] = [.. existing, link];
    }

    // ---- point queries over all zones ----

    /// <summary>Walkable surface heights at (x, z) over the loaded zones, highest first.</summary>
    public bool TryGroundHeight(float x, float z, out float height)
    {
        height = float.MinValue;
        var zone = WorldLayout.ZoneOf(x, z);
        if (!slots.TryGetValue(zone, out int slot)) return false;
        var polys = new List<int>(4);
        meshes[slot].PolygonsAt(x, z, polys);
        if (polys.Count == 0) return false;
        foreach (int p in polys) height = Math.Max(height, meshes[slot].HeightAt(p, x, z));
        return true;
    }

    /// <summary>The polygon under or nearest to the point (nearest within <paramref name="maxDistance"/> on the XZ plane, height as the tie-break).</summary>
    public bool TryFindPolygon(Vector3 p, float maxDistance, out NavRef polygon, out Vector3 snapped)
    {
        polygon = default;
        snapped = p;
        var zone = WorldLayout.ZoneOf(p.X, p.Z);
        float bestScore = float.MaxValue;
        bool found = false;
        // The zone itself, and the neighbours when the point is near a border.
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!slots.TryGetValue(new ZoneCoordinate(zone.X + dx, zone.Y + dz), out int slot)) continue;
                var m = meshes[slot];
                int poly = m.Nearest(p.X, p.Y, p.Z, maxDistance, out float d);
                if (poly < 0) continue;
                float y = m.HeightAt(poly, Math.Clamp(p.X, m.BoundsMin.X, m.BoundsMax.X), Math.Clamp(p.Z, m.BoundsMin.Y, m.BoundsMax.Y));
                float score = d * 4 + Math.Abs(y - p.Y);
                if (score >= bestScore) continue;
                bestScore = score;
                found = true;
                polygon = new NavRef(slot, poly);
                snapped = ClosestPoint(m, poly, p);
            }
        return found;
    }

    /// <summary>The point of the polygon (seen from above) nearest to p, with the polygon's height there.</summary>
    public static Vector3 ClosestPoint(ZoneNavMesh m, int polygon, Vector3 p)
    {
        float x = p.X, z = p.Z;
        if (!m.Contains(polygon, x, z))
        {
            var poly = m.Polygons[polygon];
            float best = float.MaxValue;
            for (int k = 0; k < poly.Length; k++)
            {
                var a = m.Vertices[poly[k]];
                var b = m.Vertices[poly[(k + 1) % poly.Length]];
                float dx = b.X - a.X, dz = b.Z - a.Z;
                float len2 = dx * dx + dz * dz;
                float t = len2 < 1e-9f ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Z - a.Z) * dz) / len2, 0, 1);
                float cx = a.X + t * dx, cz = a.Z + t * dz;
                float d = (cx - p.X) * (cx - p.X) + (cz - p.Z) * (cz - p.Z);
                if (d < best) { best = d; x = cx; z = cz; }
            }
        }
        return new Vector3(x, m.HeightAt(polygon, x, z), z);
    }
}
