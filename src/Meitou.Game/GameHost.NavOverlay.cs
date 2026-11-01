using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Navigation;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The navmesh debug view (Meitou only, Shift+F11; docs/game/pathfinding.md "Meitou debug view"): each press steps off, Meitou's mesh, the original's cached tiles
/// (<see cref="OriginalNavTiles"/>), both. It covers the zones the walkers use now, drawn by <see cref="NavmeshOverlayRenderer"/>; the geometry is built on a worker
/// whenever the navigation world or the view changes.
/// </summary>
sealed partial class GameHost
{
    enum NavView { Off, Meitou, Original, Both }

    /// <summary>How far above the meshes the view is drawn, so the ground does not hide them (the original's a little higher, so the two do not fight).</summary>
    const float NavOverlayLift = 3, OriginalOverlayLift = 4;

    NavView navView;
    NavmeshOverlayRenderer? navOverlayRenderer;
    (NavWorld World, NavView View)? navOverlayShown;
    Task<(Vector3 Origin, float[] Fill, float[] Edges)>? navOverlayBuild;
    // The original's tiles read so far (null: the install has none for the zone).
    readonly ConcurrentDictionary<ZoneCoordinate, OriginalNavMesh?> originalTiles = new();

    void ToggleNavOverlay()
    {
        if (nav is null || navOverlayRenderer is not { } overlay) { Console.WriteLine("navmesh   no navmesh in this scene"); return; }
        navView = (NavView)(((int)navView + 1) % 4);
        overlay.Visible = navView != NavView.Off;
        if (navView == NavView.Off) { overlay.Clear(); navOverlayShown = null; }
        Console.WriteLine(navView switch
        {
            NavView.Meitou => "navmesh   Meitou's (Shift+F11: the original's)",
            NavView.Original => "navmesh   the original's cached tiles (Shift+F11: both)",
            NavView.Both => "navmesh   both: Meitou's cyan, the original's magenta (Shift+F11 hides them)",
            _ => "navmesh   hidden",
        });
    }

    /// <summary>Once a frame: a finished build goes to the renderer; a changed world or view starts the next.</summary>
    void UpdateNavOverlay()
    {
        if (nav is null || navOverlayRenderer is not { Visible: true } overlay) return;
        if (navOverlayBuild is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully) { var (origin, fill, edges) = done.Result; overlay.SetMesh(origin, fill, edges); }
            else Console.Error.WriteLine($"navmesh   view not built: {done.Exception?.GetBaseException().Message}");
            navOverlayBuild = null;
        }
        var shown = (nav.Walkability.World, navView);
        if (navOverlayBuild is null && navOverlayShown != shown)
        {
            navOverlayShown = shown;
            var (world, view) = shown;
            navOverlayBuild = Task.Run(() => BuildNavOverlay(world, view));
        }
    }

    (Vector3, float[], float[]) BuildNavOverlay(NavWorld world, NavView view)
    {
        var fill = new List<float>();
        var edges = new List<float>();
        Vector3? origin = null;
        if (view is NavView.Meitou or NavView.Both) AddMeitouMesh(world, fill, edges, ref origin);
        if (view is NavView.Original or NavView.Both)
            foreach (var zone in world.Zones)
                if (originalTiles.GetOrAdd(zone, z => OriginalNavTiles.LoadExterior(install, z)) is { } mesh) AddOriginalMesh(mesh, fill, edges, ref origin);
        return (origin ?? Vector3.Zero, [.. fill], [.. edges]);
    }

    /// <summary>
    /// Meitou's mesh: walkable polygons cyan (each a little lighter or darker, so neighbours tell apart), door polygons blue, polygons the seed pass removed red;
    /// edges where the mesh ends yellow, the rest faint.
    /// </summary>
    static void AddMeitouMesh(NavWorld world, List<float> fill, List<float> edges, ref Vector3? origin)
    {
        for (int z = 0; z < world.ZoneCount; z++)
        {
            var m = world.Mesh(z);
            if (m.Vertices.Length == 0) continue;
            var o = origin ??= Round(m.Vertices[0]);
            for (int p = 0; p < m.PolygonCount; p++)
            {
                var poly = m.Polygons[p];
                if (poly.Length < 3) continue;
                float shade = Shade(p);
                Vector4 colour = !m.Kept[p] ? new(0.95f, 0.2f, 0.15f, 0.22f)
                    : m.DoorOf is { } doors && doors[p] >= 0 ? new(0.3f, 0.45f, 1f, 0.35f)
                    : new(0.1f * shade, 0.85f * shade, 0.95f * shade, 0.2f);
                Fan(fill, m.Vertices, poly, o, colour, NavOverlayLift);
                var links = m.LinksOf(p);
                for (int k = 0; k < poly.Length; k++)
                {
                    int across = m.Neighbours[p][k];
                    if (across >= 0 && across < p) continue;   // drawn from the other side
                    bool linked = false;
                    for (int l = 0; l + 1 < links.Length; l += 2) linked |= links[l] == k;
                    bool end = across < 0 && !linked && world.LinksOf(new NavRef(z, p)).ToArray().All(n => n.Edge != k);
                    var edge = end ? new Vector4(1f, 0.85f, 0.2f, 0.9f) : new Vector4(0.1f, 0.9f, 1f, 0.35f);
                    Line(edges, m.Vertices[poly[k]] - o, m.Vertices[poly[(k + 1) % poly.Length]] - o, edge, NavOverlayLift);
                }
            }
        }
    }

    /// <summary>The original's mesh: polygons magenta (shaded like Meitou's), edges where it ends orange, edges joined to the next tile violet, the rest faint.</summary>
    static void AddOriginalMesh(OriginalNavMesh m, List<float> fill, List<float> edges, ref Vector3? origin)
    {
        if (m.Vertices.Length == 0) return;
        var o = origin ??= Round(m.Vertices[0]);
        for (int p = 0; p < m.Polygons.Length; p++)
        {
            var poly = m.Polygons[p];
            if (poly.Length < 3) continue;
            float shade = Shade(p);
            Fan(fill, m.Vertices, poly, o, new(0.95f * shade, 0.25f * shade, 0.85f * shade, 0.18f), OriginalOverlayLift);
            for (int k = 0; k < poly.Length; k++)
            {
                var kind = m.Edges[p][k];
                var edge = kind switch
                {
                    OriginalEdge.End => new Vector4(1f, 0.45f, 0.1f, 0.9f),
                    OriginalEdge.TileBorder => new Vector4(0.6f, 0.35f, 1f, 0.6f),
                    _ => new Vector4(0.95f, 0.3f, 0.85f, 0.3f),
                };
                Line(edges, m.Vertices[poly[k]] - o, m.Vertices[poly[(k + 1) % poly.Length]] - o, edge, OriginalOverlayLift);
            }
        }
    }

    static Vector3 Round(Vector3 v) => new(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z));

    static float Shade(int polygon) => 0.75f + 0.25f * ((polygon * 7919 % 5) / 4f);

    static void Fan(List<float> to, Vector3[] vertices, int[] poly, Vector3 o, Vector4 colour, float lift)
    {
        for (int k = 1; k + 1 < poly.Length; k++)
        {
            Add(to, vertices[poly[0]] - o, colour, lift);
            Add(to, vertices[poly[k]] - o, colour, lift);
            Add(to, vertices[poly[k + 1]] - o, colour, lift);
        }
    }

    static void Line(List<float> to, Vector3 a, Vector3 b, Vector4 colour, float lift)
    {
        Add(to, a, colour, lift);
        Add(to, b, colour, lift);
    }

    static void Add(List<float> to, Vector3 p, Vector4 c, float lift) => to.AddRange([p.X, p.Y + lift, p.Z, c.X, c.Y, c.Z, c.W]);
}
