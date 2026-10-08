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

    /// <summary>
    /// A new world with <paramref name="mesh"/> added (or replacing the zone's), linked to its loaded neighbours where the borders' heights differ by at most
    /// <paramref name="maxStep"/> (engine choice: the builder's <c>MaxClimb</c> 5 plus half a unit, as between tiles).
    /// </summary>
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
        var edgesA = ma.BorderEdges(dx, dz, skipLinked: false);
        var edgesB = mb.BorderEdges(-dx, -dz, skipLinked: false);
        bool alongX = dz != 0; // the shared border line runs along X when the zones are stacked in Z
        foreach (var ea in edgesA)
            foreach (var eb in edgesB)
            {
                if (!NavGeometry.SpansMeet(Span(ma, ea, alongX), Span(mb, eb, alongX), maxStep)) continue;
                Add(new NavRef(a, ea.Polygon), new NavLink(ea.Edge, new NavRef(b, eb.Polygon)));
                Add(new NavRef(b, eb.Polygon), new NavLink(eb.Edge, new NavRef(a, ea.Polygon)));
            }
    }

    static (float From, float To, float YFrom, float YTo) Span(ZoneNavMesh m, (int Polygon, int Edge) e, bool alongX)
    {
        var poly = m.Polygons[e.Polygon];
        return NavGeometry.Span(m.Vertices[poly[e.Edge]], m.Vertices[poly[(e.Edge + 1) % poly.Length]], alongX);
    }

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
        return meshes[slot].TryHighestAt(x, z, out height);
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
