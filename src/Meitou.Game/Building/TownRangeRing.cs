using System.Numerics;

namespace Meitou.Game.Building;

/// <summary>
/// Meitou only (docs/game/buildings-production.md "Town range ring"): while build mode holds a building, each of the player's outposts draws its
/// radius as a red band on the ground, so the player sees where a new building still joins that outpost (and its power) and where it would stand
/// in no town. Drawn by <see cref="Rendering.Overlays.NavmeshOverlayRenderer"/> (<c>WorldGpu.TownRangeOverlay</c>); the band is rebuilt only when
/// the player's outposts change.
/// </summary>
sealed class TownRangeRing(GameHost host)
{
    #region Ring

    /// <summary>
    /// The band's width inward from the radius, and how far above the terrain it is drawn so the ground does not swallow it.
    /// </summary>
    const float BandWidth = 10, Lift = 3;

    /// <summary>
    /// The longest segment of the circle.
    /// </summary>
    const float SegmentLength = 25;

    /// <summary>
    /// The fewest and most segments one ring has.
    /// </summary>
    const int MinSegments = 64, MaxSegments = 4096;

    /// <summary>
    /// The band's fill and its edges' colour.
    /// </summary>
    static readonly Vector4 FillColour = new(1f, 0.12f, 0.08f, 0.45f), EdgeColour = new(1f, 0.2f, 0.12f, 0.95f);

    /// <summary>
    /// The outposts (number, centre, radius) the band was last built for; null while it is hidden.
    /// </summary>
    (int Number, Vector3 Position, float Radius)[]? _shown;

    /// <summary>
    /// Once a frame: shows the rings while a building is held in build mode, rebuilding them when the player's outposts changed, else hides them.
    /// </summary>
    internal void Update()
    {
        if (host.Gpu.TownRangeOverlay is not { } overlay) return;

        var towns = Placing() ? PlayerTowns() : null;
        if (towns is null or [] || host.Nav is null)
        {
            overlay.Visible = false;
            _shown = null;
            return;
        }

        overlay.Visible = true;
        if (_shown is not null && _shown.AsSpan().SequenceEqual(towns)) return;

        _shown = towns;
        var (origin, fill, edges) = Geometry(towns);
        overlay.SetMesh(origin, fill, edges);
    }

    /// <summary>
    /// Build mode holds a building (the ghost follows the cursor).
    /// </summary>
    bool Placing() => host.Player.Build.BuildModeOn && host.Player.Build.CurrentBuilding() is not null;

    /// <summary>
    /// The player faction's outposts in the latest snapshot.
    /// </summary>
    (int, Vector3, float)[] PlayerTowns()
    {
        int player = host.Session.World.Player.Faction;
        return
        [
            .. host.Session.CurrentSnapshot.Outposts
                .Where(o => o.Owner == player && o.Radius > 0)
                .Select(o => (o.Number, o.Position, o.Radius))
        ];
    }

    /// <summary>
    /// Every ring's band (triangles) and its inner and outer edges (lines), relative to the first outpost's centre.
    /// </summary>
    (Vector3 Origin, float[] Fill, float[] Edges) Geometry((int Number, Vector3 Position, float Radius)[] towns)
    {
        var origin = towns[0].Position;
        var fill = new List<float>();
        var edges = new List<float>();
        foreach (var town in towns) AddRing(town.Position, town.Radius, origin, fill, edges);
        return (origin, [.. fill], [.. edges]);
    }

    /// <summary>
    /// Adds one ring: a band from <paramref name="radius"/> inward by <see cref="BandWidth"/>, following the terrain.
    /// </summary>
    void AddRing(Vector3 centre, float radius, Vector3 origin, List<float> fill, List<float> edges)
    {
        int segments = Math.Clamp((int)MathF.Ceiling(2 * MathF.PI * radius / SegmentLength), MinSegments, MaxSegments);
        var (outerA, innerA) = Pair(centre, radius, 0, origin);
        for (int k = 1; k <= segments; k++)
        {
            var (outerB, innerB) = Pair(centre, radius, 2 * MathF.PI * k / segments, origin);
            AddQuad(fill, outerA, outerB, innerB, innerA);
            AddLine(edges, outerA, outerB);
            AddLine(edges, innerA, innerB);
            (outerA, innerA) = (outerB, innerB);
        }
    }

    /// <summary>
    /// The band's outer and inner points at <paramref name="angle"/>, on the terrain, relative to <paramref name="origin"/>.
    /// </summary>
    (Vector3 Outer, Vector3 Inner) Pair(Vector3 centre, float radius, float angle, Vector3 origin)
    {
        var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        return (OnGround(centre, direction * radius, origin), OnGround(centre, direction * (radius - BandWidth), origin));
    }

    /// <summary>
    /// The terrain point at <paramref name="offset"/> (X/Z) from <paramref name="centre"/>, lifted, relative to <paramref name="origin"/>.
    /// </summary>
    Vector3 OnGround(Vector3 centre, Vector2 offset, Vector3 origin)
    {
        float x = centre.X + offset.X;
        float z = centre.Z + offset.Y;
        float y = host.Nav!.Walkability.TerrainHeight(x, z) + Lift;
        return new Vector3(x, y, z) - origin;
    }

    /// <summary>
    /// Adds the quad a-b-c-d as two triangles.
    /// </summary>
    static void AddQuad(List<float> to, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Add(to, a, FillColour);
        Add(to, b, FillColour);
        Add(to, c, FillColour);
        Add(to, a, FillColour);
        Add(to, c, FillColour);
        Add(to, d, FillColour);
    }

    /// <summary>
    /// Adds an edge line from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    static void AddLine(List<float> to, Vector3 a, Vector3 b)
    {
        Add(to, a, EdgeColour);
        Add(to, b, EdgeColour);
    }

    /// <summary>
    /// Adds one vertex: position and colour (<see cref="Rendering.Overlays.NavmeshOverlayRenderer.Stride"/> floats).
    /// </summary>
    static void Add(List<float> to, Vector3 p, Vector4 c) => to.AddRange([p.X, p.Y, p.Z, c.X, c.Y, c.Z, c.W]);

    #endregion
}
