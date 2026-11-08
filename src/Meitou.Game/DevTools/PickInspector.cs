using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Building;

namespace Meitou.Game.DevTools;

/// <summary>
/// The pick & collision inspector (Meitou only; the host's inspector menu, opened from the F10 menu's Tools row, switches these on and off; docs/viewer.md "Pick & collision inspector"): what is under the cursor and why a
/// click would pick it. The panel is a text list (the ground point, the zone, the building a click would select with its hull, the other buildings the ray passes); the rest is drawn on the world
/// for those buildings: the pick hull a click uses, each collision shape's own hull, and the real collision triangles.
/// </summary>
sealed class PickInspector(PlayerInterface player)
{
    /// <summary>Show the text panel.</summary>
    public bool InspectorPanel { get; set; }
    /// <summary>Draw the pick hull (the merged one a click is tested against) of the buildings along the ray.</summary>
    public bool InspectorHull { get; set; }
    /// <summary>Draw each collision shape's own convex hull.</summary>
    public bool InspectorShapes { get; set; }
    /// <summary>Draw the real collision triangles of the building a click would select.</summary>
    public bool InspectorMesh { get; set; }

    /// <summary>True while anything of the inspector is on (the world is searched under the cursor every frame then).</summary>
    public bool InspectorOn => InspectorPanel || InspectorDrawing;
    /// <summary>True while something is drawn on the world.</summary>
    public bool InspectorDrawing => InspectorHull || InspectorShapes || InspectorMesh;

    /// <summary>Counts up whenever <see cref="InspectorGeometry"/> changed, so the host uploads it only then.</summary>
    public int InspectorRevision { get; private set; }

    /// <summary>The geometry to draw (positions relative to <c>Origin</c>, <c>Meitou.Game.Rendering.Overlays.NavmeshOverlayRenderer</c> layout: 7 floats a vertex).</summary>
    public (Vector3 Origin, float[] Fill, float[] Edges) InspectorGeometry { get; private set; } = (Vector3.Zero, [], []);

    string inspectorKey = "";

    /// <summary>Forgets what was drawn (a switch changed), so the next frame rebuilds it.</summary>
    public void InspectorChanged()
    {
        inspectorKey = "";
        if (!InspectorDrawing) { InspectorGeometry = (Vector3.Zero, [], []); InspectorRevision++; }
    }

    sealed record InspectedBuilding(string Name, string? Record, string Key, float? Hit, Vector2[] Hull, IReadOnlyList<Vector2[]> Shapes, float Y, float YMin, float YMax, Vector3 Position, Quaternion Rotation);

    /// <summary>The buildings a click at <paramref name="ray"/> chooses from: the level data's, with where the ray meets each one's hull prism (null: it does not).</summary>
    List<InspectedBuilding> InspectBuildings((Vector3 Origin, Vector3 Direction) ray, float limit)
    {
        var result = new List<InspectedBuilding>();
        if (player.build.buildHost is not { } host) return result;
        foreach (var candidate in host.Placement.StaticAlong(ray.Origin, ray.Direction, limit))
        {
            var b = candidate.Placement;
            float y = b.WorldY ?? player.ground(b.Position.X, b.Position.Z) + b.Position.Y;
            var hull = candidate.Footprint.Place(b.Position, b.Rotation);
            var info = host.WorldInfo(b);
            result.Add(new(info.Name, b.BuildingId, b.Key, BuildMode.RayPrism(ray.Origin, ray.Direction, hull, y + candidate.Footprint.YMin, y + candidate.Footprint.YMax),
                hull, [.. candidate.Footprint.PlaceShapes(b.Position, b.Rotation)], y, candidate.Footprint.YMin, candidate.Footprint.YMax, b.Position, b.Rotation));
        }
        // The player's own sites (what the hammer pointer and a click on a site test): hull prism from the site's position, turned by its yaw.
        foreach (var site in player.session.CurrentSnapshot.Buildings)
        {
            if (host.Placement.Footprint(site.Record) is not { } footprint) continue;
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Meitou.Game.Building.PlacementCheck.YawOf(site.Rotation));
            var hull = footprint.Place(site.Position, rotation);
            result.Add(new("[site] " + player.build.SiteName(site), site.Record, site.Id, BuildMode.RayPrism(ray.Origin, ray.Direction, hull, site.Position.Y + footprint.YMin, site.Position.Y + footprint.YMax),
                hull, [.. footprint.PlaceShapes(site.Position, rotation)], site.Position.Y, footprint.YMin, footprint.YMax, site.Position, rotation));
        }
        return result;
    }

    static string N(float v, string format = "0") => v.ToString(format, CultureInfo.InvariantCulture);

    static float Area(Vector2[] polygon)
    {
        float a = 0;
        for (int i = 0; i < polygon.Length; i++) a += polygon[i].X * polygon[(i + 1) % polygon.Length].Y - polygon[(i + 1) % polygon.Length].X * polygon[i].Y;
        return MathF.Abs(a) / 2;
    }

    /// <summary>The panel, and the hull drawing when it changed.</summary>
    internal void AddInspectorPanel(List<UiControl> controls)
    {
        if (!InspectorOn || player.Ray(player.mouse) is not { } ray) return;
        var ground = player.picking.GroundHit(ray.Origin, ray.Direction);
        float limit = ground is { } g ? Vector3.Distance(ray.Origin, g) : 6000;
        var buildings = InspectBuildings(ray, limit);
        var hits = buildings.Where(b => b.Hit is { } t && t < limit).OrderBy(b => b.Hit).ToList();
        var picked = hits.FirstOrDefault();
        var lines = new List<PlayerInterface.HudLine>();
        if (ground is { } at)
        {
            var zone = Meitou.Data.World.WorldLayout.ZoneOf(at.X, at.Z);
            lines.Add(new("Ground", $"{N(at.X)}, {N(at.Y)}, {N(at.Z)}"));
            lines.Add(new("Zone", $"{zone.X}, {zone.Y}"));
            lines.Add(new("Ground distance", N(limit)));
        }
        else lines.Add(new("Ground", "the ray leaves the world"));
        lines.Add(new("Click selects", picked is null ? "no building" : picked.Name));
        lines.Add(new("Hammer pointer (your site)", player.build.PickSite(player.mouse) is { } hammered ? player.build.SiteName(hammered) : "none"));
        if (picked is not null)
        {
            var (min, max) = (new Vector2(float.MaxValue), new Vector2(float.MinValue));
            foreach (var p in picked.Hull) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            float shapeArea = picked.Shapes.Sum(Area);
            lines.Add(new("Record", picked.Record ?? "?"));
            lines.Add(new("Instance", picked.Key));
            lines.Add(new("Hit distance", N(picked.Hit!.Value)));
            lines.Add(new("Hull", $"{picked.Hull.Length} corners, {N(max.X - min.X)} x {N(max.Y - min.Y)}, area {N(Area(picked.Hull))}"));
            lines.Add(new("Shapes", picked.Shapes.Count == 0 ? "none read" : $"{picked.Shapes.Count} hulls, area {N(shapeArea)} (the merged hull is {N(Area(picked.Hull) / Math.Max(shapeArea, 1), "0.0")}x)"));
            lines.Add(new("Height", $"{N(picked.Y + picked.YMin)} to {N(picked.Y + picked.YMax)}"));
        }
        if (hits.Count > 1)
        {
            lines.Add(new("Also on the ray", ""));
            foreach (var other in hits.Skip(1).Take(4)) lines.Add(new("  " + other.Name, N(other.Hit!.Value)));
        }
        if (buildings.Count - hits.Count > 0) lines.Add(new("Near, not hit", buildings.Count - hits.Count + " buildings"));
        // Resource rocks (hand mining): a rock is hit as a sphere of its radius, looked for within 200 of the ground point; each line is how far the ray passes from its centre against its radius.
        if (player.furniture.Rocks?.Invoke() is { } rocks && ground is { } rockGround)
        {
            var near = rocks.Near(0, rockGround.X, rockGround.Z, 200);
            var picked2 = player.furniture.PickRockAt(player.mouse);
            lines.Add(new("Rock a click mines", picked2 is { } r ? $"{r.Rock.Record} (hit {N(r.T)})" : near.Count == 0 ? "none (no rocks within 200)" : "none"));
            foreach (var rock in near.OrderBy(r => Vector3.Cross(r.Position - ray.Origin, ray.Direction).Length()).Take(3))
                lines.Add(new("  " + rock.Record, $"misses by {N(Vector3.Cross(rock.Position - ray.Origin, ray.Direction).Length())}, radius {N(rock.Radius)}"));
        }
        else lines.Add(new("Rocks", player.furniture.Rocks is null ? "no rock source" : "none loaded"));
        if (player.buildingSelection.selectedBuilding is { } sel) lines.Add(new("Selected building", sel.Name));
        lines.Add(new("Selected characters", player.SelectedIds().Count.ToString(CultureInfo.InvariantCulture)));
        if (InspectorPanel)
        {
            float scale = player.InventoryScale, row = 20 * scale, width = Math.Min(player.Width * 0.5f, 430 * scale), height = 16 * scale + row * (lines.Count + 1);
            var box = new UiRect(player.Width - width - 8 * scale, 40 * scale, width, height);
            controls.Add(new("inspector", "Widget", box, "", "", "Kenshi_GenericTextBoxSkin", true, new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
            controls.Add(new("inspector-title", "TextBox", new(box.X + 8 * scale, box.Y + 6 * scale, box.Width - 16 * scale, row), "PICK & COLLISION INSPECTOR", "", "", true,
                new Dictionary<string, string> { ["TextAlign"] = "Left Top", ["TextColour"] = PlayerInterface.TitleColour, ["WordWrap"] = "false" }, Vector4.Zero, Pickable: false));
            controls.Add(new("inspector-rows", "Widget", new(box.X + 4 * scale, box.Y + 6 * scale + row, box.Width - 8 * scale, row * lines.Count), "", "", "", true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
            player.AddLines(controls, "inspector-rows", lines, row);
        }
        if (InspectorDrawing) UpdateInspectorGeometry(buildings, picked, ground);
    }

    /// <summary>Rebuilds the drawn hulls when the picked building, the ones along the ray or the level changed.</summary>
    void UpdateInspectorGeometry(List<InspectedBuilding> buildings, InspectedBuilding? picked, Vector3? ground)
    {
        string key = (InspectorHull ? "h" : "") + (InspectorShapes ? "s" : "") + (InspectorMesh ? "m" : "") + "|" + (picked?.Key ?? "") + "|" + string.Join(',', buildings.Select(b => b.Key));
        if (key == inspectorKey) return;
        inspectorKey = key;
        var origin = picked is { } p ? new Vector3(MathF.Round(p.Hull[0].X), p.Y, MathF.Round(p.Hull[0].Y)) : ground ?? Vector3.Zero;
        var fill = new List<float>();
        var edges = new List<float>();
        foreach (var b in buildings)
        {
            bool isPicked = ReferenceEquals(b, picked);
            if (InspectorHull)
            {
                var colour = isPicked ? new Vector4(1f, 0.15f, 0.1f, 0.95f) : new Vector4(1f, 0.6f, 0.1f, 0.6f);
                Prism(edges, b.Hull, b.Y + b.YMin, b.Y + b.YMax, origin, colour);
                if (isPicked) HullFloor(fill, b.Hull, b.Y + b.YMin + 2, origin, new Vector4(1f, 0.15f, 0.1f, 0.18f));
            }
            if (InspectorShapes)
                foreach (var shape in b.Shapes)
                {
                    Prism(edges, shape, b.Y + b.YMin, b.Y + b.YMax, origin, new Vector4(0.2f, 1f, 0.3f, 0.9f));
                    if (isPicked) HullFloor(fill, shape, b.Y + b.YMin + 3, origin, new Vector4(0.2f, 1f, 0.3f, 0.12f));
                }
            if (InspectorMesh && isPicked && player.build.buildHost is { } host && b.Record is { } record)
                CollisionEdges(edges, host.Placement.CollisionMesh(record), b, origin, new Vector4(0.3f, 0.8f, 1f, 0.9f));
        }
        InspectorGeometry = (origin, [.. fill], [.. edges]);
        InspectorRevision++;
    }

    /// <summary>The collision triangles of a building as lines (every triangle's three edges), placed as the building is; at most 20000 triangles.</summary>
    static void CollisionEdges(List<float> edges, IReadOnlyList<(Vector3[] Vertices, int[] Indices)> mesh, InspectedBuilding b, Vector3 origin, Vector4 colour)
    {
        int budget = 20000;
        var q = b.Rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(b.Rotation);
        foreach (var (vertices, indices) in mesh)
        {
            var world = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = Vector3.Transform(vertices[i], q);
                world[i] = new Vector3(v.X + b.Position.X, v.Y + b.Y, v.Z + b.Position.Z) - origin;
            }
            for (int t = 0; t + 2 < indices.Length && budget-- > 0; t += 3)
            {
                Segment(edges, world[indices[t]], world[indices[t + 1]], colour);
                Segment(edges, world[indices[t + 1]], world[indices[t + 2]], colour);
                Segment(edges, world[indices[t + 2]], world[indices[t]], colour);
            }
        }
    }

    static void Vertex(List<float> data, Vector3 p, Vector4 colour) => data.AddRange([p.X, p.Y, p.Z, colour.X, colour.Y, colour.Z, colour.W]);

    static void Segment(List<float> edges, Vector3 a, Vector3 b, Vector4 colour) { Vertex(edges, a, colour); Vertex(edges, b, colour); }

    /// <summary>A convex polygon's outline at the bottom and the top of its prism, and a post at each corner.</summary>
    static void Prism(List<float> edges, Vector2[] hull, float yMin, float yMax, Vector3 origin, Vector4 colour)
    {
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            Vector3 P(Vector2 v, float y) => new Vector3(v.X, y, v.Y) - origin;
            Segment(edges, P(a, yMin), P(b, yMin), colour);
            Segment(edges, P(a, yMax), P(b, yMax), colour);
            Segment(edges, P(a, yMin), P(a, yMax), colour with { W = colour.W * 0.5f });
        }
    }

    /// <summary>A convex polygon as a flat translucent fan at height <paramref name="y"/>.</summary>
    static void HullFloor(List<float> fill, Vector2[] hull, float y, Vector3 origin, Vector4 colour)
    {
        for (int i = 1; i + 1 < hull.Length; i++)
            foreach (var v in new[] { hull[0], hull[i], hull[i + 1] }) Vertex(fill, new Vector3(v.X, y, v.Y) - origin, colour);
    }
}
