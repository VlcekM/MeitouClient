using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Simulation.Navigation;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug views drawn over the world (Meitou only): the character boxes, the navmesh view (Shift+F11) and F11's path view.
/// </summary>
sealed class DebugOverlays(GameHost host)
{
    #region Character boxes

    /// <summary>
    /// The character boxes (Meitou only; a checkbox on the F11 statistics' characters line, and the F10 Overlays row "Character boxes", one shared
    /// switch) that outline, over everything, each character its "drawn" count counts (CharacterRenderer.CountedBoxes: in the colour view's frustum
    /// and not hidden by fog, whether or not anything covers it).
    /// </summary>
    internal bool CharacterBoxes;

    /// <summary>
    /// Where the checkboxes were drawn last (x0, y0, x1, y1), null while the statistics are hidden.
    /// </summary>
    internal Vector4? CharacterBoxesToggle, WalkPathsToggle;

    /// <summary>
    /// The statistics panel's lines as last laid out, with their rectangles.
    /// </summary>
    internal readonly List<(string Line, Vector4 Rect)> StatsLaidOut = [];

    /// <summary>
    /// The counted characters' screen boxes (reused each frame).
    /// </summary>
    readonly List<Vector4> _countedBoxes = [];

    /// <summary>
    /// The character boxes' colour.
    /// </summary>
    static readonly Vector4 CharacterBoxColour = new(1f, 0.85f, 0.2f, 0.9f);

    /// <summary>
    /// Ticks the statistics' checkboxes as their switches stand (the lines are built once a second).
    /// </summary>
    internal void TickStatsToggles(List<string> stats)
    {
        TickStatsToggle(stats, CharacterBoxes, WorldStats.CharacterBoxesOff, WorldStats.CharacterBoxesOn);
        TickStatsToggle(stats, WalkPaths, WorldStats.PathsOff, WorldStats.PathsOn);
    }

    /// <summary>
    /// Swaps marker <paramref name="off"/> and <paramref name="ticked"/> in the lines as <paramref name="on"/> says.
    /// </summary>
    static void TickStatsToggle(List<string> stats, bool on, string off, string ticked)
    {
        string from = on ? off : ticked;
        string to = on ? ticked : off;
        for (int i = 0; i < stats.Count; i++)
        {
            if (stats[i].Contains(from, StringComparison.Ordinal))
                stats[i] = stats[i].Replace(from, to, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Where the checkboxes are in the panel just drawn (from its laid-out lines).
    /// </summary>
    internal void FindStatsToggles(float charWidth)
    {
        CharacterBoxesToggle = FindStatsToggle(CharacterBoxes ? WorldStats.CharacterBoxesOn : WorldStats.CharacterBoxesOff, charWidth);
        WalkPathsToggle = FindStatsToggle(WalkPaths ? WorldStats.PathsOn : WorldStats.PathsOff, charWidth);
    }

    /// <summary>
    /// A checkbox's rectangle in the panel just drawn, or null when it is not there.
    /// </summary>
    Vector4? FindStatsToggle(string marker, float charWidth)
    {
        foreach (var (line, rect) in StatsLaidOut)
        {
            int at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            float x0 = rect.X + at * charWidth;
            return new Vector4(x0, rect.Y, x0 + marker.Length * charWidth, rect.W);
        }
        return null;
    }

    /// <summary>
    /// A left click on a checkbox flips its switch; true when one took the click.
    /// </summary>
    internal bool StatsToggleClick(Vector2 at)
    {
        if (Inside(CharacterBoxesToggle, at))
        {
            CharacterBoxes = !CharacterBoxes;
            return true;
        }

        if (Inside(WalkPathsToggle, at))
        {
            WalkPaths = !WalkPaths;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="at"/> lies in <paramref name="rect"/> (x0, y0, x1, y1); false for null.
    /// </summary>
    static bool Inside(Vector4? rect, Vector2 at) => rect is { } t && at.X >= t.X && at.X <= t.Z && at.Y >= t.Y && at.Y <= t.W;

    /// <summary>
    /// The counted characters' outlines over the whole picture, seen through the world camera at the display size.
    /// </summary>
    internal void DrawCharacterBoxes(DebugOverlay overlay, CharacterRenderer characters, WorldCamera camera, int width, int height)
    {
        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        characters.CountedBoxes(viewProjection, width, height, _countedBoxes);
        const float line = 1.5f;
        foreach (var whole in _countedBoxes)
        {
            // A third of the bounding sphere's box each way, round its centre: the whole box spans weapons and ragdoll reach and hides the crowd.
            var mid = new Vector2(whole.X + whole.Z, whole.Y + whole.W) * 0.5f;
            var half = new Vector2(whole.Z - whole.X, whole.W - whole.Y) / 6;
            var b = new Vector4(mid - half, (mid + half).X, (mid + half).Y);
            overlay.Rect(b.X, b.Y, b.Z, b.Y + line, CharacterBoxColour);
            overlay.Rect(b.X, b.W - line, b.Z, b.W, CharacterBoxColour);
            overlay.Rect(b.X, b.Y, b.X + line, b.W, CharacterBoxColour);
            overlay.Rect(b.Z - line, b.Y, b.Z, b.W, CharacterBoxColour);
        }
        overlay.Text($"{_countedBoxes.Count} counted", 16, height - overlay.LineHeight - 8, CharacterBoxColour);
        overlay.Flush(width, height);
    }

    #endregion

    #region Navmesh view

    /// <summary>
    /// The navmesh debug view (Meitou only, Shift+F11; docs/game/pathfinding.md "Meitou debug view"): each press steps off, Meitou's mesh, the
    /// original's cached tiles (OriginalNavTiles), both. It covers the zones the walkers use now, drawn by NavmeshOverlayRenderer; the geometry is
    /// built on a worker whenever the navigation world or the view changes.
    /// </summary>
    internal enum NavView
    {
        /// <summary>
        /// Hidden.
        /// </summary>
        Off,

        /// <summary>
        /// Meitou's navmesh.
        /// </summary>
        Meitou,

        /// <summary>
        /// The original's cached tiles.
        /// </summary>
        Original,

        /// <summary>
        /// Meitou's and the original's.
        /// </summary>
        Both,
    }

    /// <summary>
    /// How far above the meshes the view is drawn, so the ground does not hide them (the original's a little higher, so the two do not fight).
    /// </summary>
    const float NavOverlayLift = 3, OriginalOverlayLift = 4;

    /// <summary>
    /// The navmesh view's step.
    /// </summary>
    internal NavView NavViewMode;

    /// <summary>
    /// The navmesh view's renderer; null without one.
    /// </summary>
    internal NavmeshOverlayRenderer? NavOverlayRenderer;

    /// <summary>
    /// The navigation world and view the shown geometry was built for; null when none is shown.
    /// </summary>
    internal (NavWorld World, NavView View)? NavOverlayShown;

    /// <summary>
    /// The geometry build running on a worker; null for none.
    /// </summary>
    internal Task<(Vector3 Origin, float[] Fill, float[] Edges)>? NavOverlayBuild;

    /// <summary>
    /// The original's tiles read so far (null: the install has none for the zone).
    /// </summary>
    readonly ConcurrentDictionary<ZoneCoordinate, OriginalNavMesh?> _originalTiles = new();

    /// <summary>
    /// Steps the navmesh view (off, Meitou's, the original's, both) and says which on the console; nothing without a navmesh.
    /// </summary>
    internal void ToggleNavOverlay()
    {
        if (host.Nav is null || NavOverlayRenderer is not { } overlay)
        {
            Console.WriteLine("navmesh   no navmesh in this scene");
            return;
        }

        NavViewMode = (NavView)(((int)NavViewMode + 1) % 4);
        overlay.Visible = NavViewMode != NavView.Off;
        if (NavViewMode == NavView.Off)
        {
            overlay.Clear();
            NavOverlayShown = null;
        }

        Console.WriteLine(NavViewMode switch
        {
            NavView.Meitou => "navmesh   Meitou's (Shift+F11: the original's)",
            NavView.Original => "navmesh   the original's cached tiles (Shift+F11: both)",
            NavView.Both => "navmesh   both: Meitou's cyan, the original's magenta (Shift+F11 hides them)",
            _ => "navmesh   hidden",
        });
    }

    /// <summary>
    /// Once a frame: a finished build goes to the renderer; a changed world or view starts the next.
    /// </summary>
    internal void UpdateNavOverlay()
    {
        if (host.Nav is null || NavOverlayRenderer is not { Visible: true } overlay) return;
        if (NavOverlayBuild is { IsCompleted: true } done)
        {
            if (done.IsCompletedSuccessfully)
            {
                var (origin, fill, edges) = done.Result;
                overlay.SetMesh(origin, fill, edges);
            }
            else Console.Error.WriteLine($"navmesh   view not built: {done.Exception?.GetBaseException().Message}");
            NavOverlayBuild = null;
        }
        var shown = (host.Nav.Walkability.World, NavViewMode);
        if (NavOverlayBuild is null && NavOverlayShown != shown)
        {
            NavOverlayShown = shown;
            var (world, view) = shown;
            NavOverlayBuild = Task.Run(() => BuildNavOverlay(world, view));
        }
    }

    /// <summary>
    /// The navmesh view's geometry for <paramref name="view"/>: Meitou's mesh and the original's tiles of the zones in use, as fill triangles and
    /// edge lines round one origin.
    /// </summary>
    (Vector3, float[], float[]) BuildNavOverlay(NavWorld world, NavView view)
    {
        var fill = new List<float>();
        var edges = new List<float>();
        Vector3? origin = null;
        if (view is NavView.Meitou or NavView.Both) AddMeitouMesh(world, fill, edges, ref origin);
        if (view is NavView.Original or NavView.Both)
        {
            foreach (var zone in world.Zones)
            {
                if (_originalTiles.GetOrAdd(zone, z => OriginalNavTiles.LoadExterior(host.Install, z)) is { } mesh)
                    AddOriginalMesh(mesh, fill, edges, ref origin);
            }
        }
        return (origin ?? Vector3.Zero, [.. fill], [.. edges]);
    }

    /// <summary>
    /// Meitou's mesh: walkable polygons cyan (each a little lighter or darker, so neighbours tell apart), door polygons blue, polygons the seed pass
    /// removed red; edges where the mesh ends yellow, the rest faint.
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

    /// <summary>
    /// The original's mesh: polygons magenta (shaded like Meitou's), edges where it ends orange, edges joined to the next tile violet, the rest
    /// faint.
    /// </summary>
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

    /// <summary>
    /// <paramref name="v"/> with each component rounded.
    /// </summary>
    static Vector3 Round(Vector3 v) => new(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z));

    /// <summary>
    /// A polygon's shade, 0.75 to 1, so neighbours differ.
    /// </summary>
    static float Shade(int polygon) => 0.75f + 0.25f * ((polygon * 7919 % 5) / 4f);

    /// <summary>
    /// Adds polygon <paramref name="poly"/> as a triangle fan, relative to <paramref name="o"/> and lifted by <paramref name="lift"/>.
    /// </summary>
    static void Fan(List<float> to, Vector3[] vertices, int[] poly, Vector3 o, Vector4 colour, float lift)
    {
        for (int k = 1; k + 1 < poly.Length; k++)
        {
            Add(to, vertices[poly[0]] - o, colour, lift);
            Add(to, vertices[poly[k]] - o, colour, lift);
            Add(to, vertices[poly[k + 1]] - o, colour, lift);
        }
    }

    /// <summary>
    /// Adds a line from <paramref name="a"/> to <paramref name="b"/>, lifted by <paramref name="lift"/>.
    /// </summary>
    static void Line(List<float> to, Vector3 a, Vector3 b, Vector4 colour, float lift)
    {
        Add(to, a, colour, lift);
        Add(to, b, colour, lift);
    }

    /// <summary>
    /// Adds one vertex: position (lifted by <paramref name="lift"/>) and colour.
    /// </summary>
    static void Add(List<float> to, Vector3 p, Vector4 c, float lift) => to.AddRange([p.X, p.Y + lift, p.Z, c.X, c.Y, c.Z, c.W]);

    #endregion

    #region Inspector hulls

    /// <summary>
    /// The inspector geometry revision last given to its renderer; -1 for none.
    /// </summary>
    int _inspectorShown = -1;

    /// <summary>
    /// Once a frame: the inspector's hulls go to their renderer when they changed (docs/viewer.md "Pick & collision inspector").
    /// </summary>
    internal void UpdateInspectorOverlay()
    {
        if (!host.Player.Inspector.InspectorDrawing)
        {
            if (host.Gpu.InspectorOverlay is { Visible: true } hidden) hidden.Visible = false;
            _inspectorShown = -1;
            return;
        }

        if (host.Gpu.InspectorOverlay is not { } overlay) return;
        overlay.Visible = true;
        if (_inspectorShown == host.Player.Inspector.InspectorRevision) return;
        _inspectorShown = host.Player.Inspector.InspectorRevision;
        var (origin, fill, edges) = host.Player.Inspector.InspectorGeometry;
        overlay.SetMesh(origin, fill, edges);
    }

    #endregion

    #region Path view

    /// <summary>
    /// F11's path view (Meitou only; docs/game/pathfinding.md "Meitou debug view"): a checkbox on the statistics' characters line that draws, over
    /// everything and still once F11 is closed, the path each selected or inspected character follows (CharacterSnapshot.Path): from its feet to the
    /// next waypoint, then the navmesh part, then the provisional long-range part (CharacterSnapshot.PathExact), the next waypoint and the end
    /// marked.
    /// </summary>
    internal bool WalkPaths;

    /// <summary>
    /// How far above the waypoints the path is drawn, so the ground does not swallow it (the navmesh view's lift).
    /// </summary>
    const float PathLift = NavOverlayLift;

    /// <summary>
    /// The navmesh legs' colour.
    /// </summary>
    static readonly Vector4 PathColour = new(0.2f, 1f, 0.35f, 0.95f);

    /// <summary>
    /// The provisional legs' colour.
    /// </summary>
    static readonly Vector4 ProvisionalColour = new(1f, 0.55f, 0.1f, 0.95f);

    /// <summary>
    /// The next waypoint's colour.
    /// </summary>
    static readonly Vector4 NextWaypointColour = new(1f, 1f, 1f, 1f);

    /// <summary>
    /// The end's colour.
    /// </summary>
    static readonly Vector4 PathEndColour = new(1f, 0.25f, 0.25f, 1f);

    /// <summary>
    /// The least clip w in front of the eye.
    /// </summary>
    const float NearW = 0.01f;

    /// <summary>
    /// The paths of the characters the snapshot details, seen through the world camera at the display size.
    /// </summary>
    internal void DrawWalkPaths(DebugOverlay overlay, WorldSnapshot snapshot, WorldCamera camera, int width, int height)
    {
        var viewProjection = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        foreach (var c in snapshot.Characters)
            if (c.Path.Count > 0) DrawWalkPath(overlay, c, viewProjection, width, height);
        overlay.Flush(width, height);
    }

    /// <summary>
    /// The paths' count in a small panel at the top left, below <paramref name="panelsBottom"/>; returns the new bottom (as the fps line does).
    /// </summary>
    internal static float DrawWalkPathsLabel(DebugOverlay overlay, WorldSnapshot snapshot, int width, int height, float panelsBottom)
    {
        int shown = 0, points = 0;
        foreach (var c in snapshot.Characters)
        {
            if (c.Path.Count == 0) continue;
            shown++;
            points += c.Path.Count;
        }
        string label = shown == 0 ? "paths: select a walking character" : $"{shown} path{(shown == 1 ? "" : "s")}, {points} waypoints left";

        float w = label.Length * overlay.CharWidth + 12, h = overlay.LineHeight + 8, top = panelsBottom + 8;
        overlay.Rect(8, top, 8 + w, top + h, DebugOverlay.PanelColour);
        overlay.Text(label, 14, top + 4, PathColour);
        overlay.Flush(width, height);
        return top + h - 8;   // the panels add their own 16 px margin
    }

    /// <summary>
    /// One character's path: the legs (navmesh green, provisional orange), a square at each waypoint, the next one white and the end red.
    /// </summary>
    static void DrawWalkPath(DebugOverlay overlay, CharacterSnapshot c, Matrix4x4 viewProjection, int width, int height)
    {
        var lift = Vector3.UnitY * PathLift;
        var from = c.Position + lift;
        for (int i = 0; i < c.Path.Count; i++)
        {
            var to = c.Path[i] + lift;
            var colour = i >= c.PathExact ? ProvisionalColour : PathColour;
            if (Clip(from, to, viewProjection, width, height) is var (a, b))
                overlay.Line(a.X, a.Y, b.X, b.Y, 2.5f, colour);

            var mark = i == 0 ? NextWaypointColour : i == c.Path.Count - 1 ? PathEndColour : colour;
            float half = i == 0 || i == c.Path.Count - 1 ? 5 : 3;
            if (Screen(to, viewProjection, width, height) is { } at)
                overlay.Rect(at.X - half, at.Y - half, at.X + half, at.Y + half, mark);
            from = to;
        }
    }

    /// <summary>
    /// A world point in pixels, or null behind the eye.
    /// </summary>
    static Vector2? Screen(Vector3 p, Matrix4x4 viewProjection, int width, int height)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), viewProjection);
        return clip.W <= NearW ? null : Pixel(clip, width, height);
    }

    /// <summary>
    /// A segment in pixels, cut where it passes behind the eye; null when all of it is behind.
    /// </summary>
    static (Vector2, Vector2)? Clip(Vector3 a, Vector3 b, Matrix4x4 viewProjection, int width, int height)
    {
        var ca = Vector4.Transform(new Vector4(a, 1), viewProjection);
        var cb = Vector4.Transform(new Vector4(b, 1), viewProjection);
        if (ca.W <= NearW && cb.W <= NearW) return null;
        if (ca.W <= NearW) ca = Vector4.Lerp(ca, cb, (NearW - ca.W) / (cb.W - ca.W));
        else if (cb.W <= NearW) cb = Vector4.Lerp(cb, ca, (NearW - cb.W) / (ca.W - cb.W));
        return (Pixel(ca, width, height), Pixel(cb, width, height));
    }

    /// <summary>
    /// A clip-space point in pixels.
    /// </summary>
    static Vector2 Pixel(Vector4 clip, int width, int height) =>
        new((clip.X / clip.W * 0.5f + 0.5f) * width, (0.5f - clip.Y / clip.W * 0.5f) * height);

    #endregion
}