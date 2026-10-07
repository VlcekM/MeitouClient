using System.Numerics;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The geometry of a far billboard ("impostor") atlas (docs/impostors.md): a grid of <c>Grid × Grid</c> orthographic views of a mesh,
/// one per direction of the upper hemisphere, laid out by the hemi-octahedral map. Frame (i, j) sits at atlas UV
/// <c>[i, i + 1] / Grid × [j, j + 1] / Grid</c> (GL order, row 0 at the bottom) and looks at the mesh from
/// <see cref="FrameDirection"/>(i, j). Each frame covers the bounding sphere: frame-local UV <c>(dot(p − c, right), dot(p − c, up)) / 2r + 0.5</c>,
/// depth <c>dot(p − c, dir) / r × 0.5 + 0.5</c> (1 towards the viewer). The GLSL in <see cref="ImpostorShaders"/> computes the same things;
/// the C# here is the reference the tests check (and what the baker places its cameras with).
/// </summary>
public static class ImpostorLayout
{
    /// <summary>Upper-hemisphere direction (y ≥ 0; lower directions are clamped to the horizon) to hemi-octahedral UV in [0, 1]².</summary>
    public static Vector2 Encode(Vector3 d)
    {
        d.Y = MathF.Max(d.Y, 0);
        float s = MathF.Abs(d.X) + d.Y + MathF.Abs(d.Z);
        if (s < 1e-20f) return new Vector2(0.5f, 0.5f);
        float x = d.X / s, z = d.Z / s;
        return new Vector2(x + z, x - z) * 0.5f + new Vector2(0.5f);
    }

    /// <summary>The inverse of <see cref="Encode"/>: a unit direction with y ≥ 0.</summary>
    public static Vector3 Decode(Vector2 uv)
    {
        var e = uv * 2 - Vector2.One;
        float x = (e.X + e.Y) * 0.5f, z = (e.X - e.Y) * 0.5f;
        float y = 1 - MathF.Abs(x) - MathF.Abs(z);
        return Vector3.Normalize(new Vector3(x, y, z));
    }

    /// <summary>A unit vector to full-sphere octahedral coordinates in [−1, 1]² (how the normal map stores the frame-space normal; the fold is at z = −1, facing away from the frame).</summary>
    public static Vector2 EncodeNormal(Vector3 n)
    {
        float s = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        var p = new Vector2(n.X, n.Y) / MathF.Max(s, 1e-20f);
        if (n.Z < 0) p = new Vector2((1 - MathF.Abs(p.Y)) * (p.X >= 0 ? 1 : -1), (1 - MathF.Abs(p.X)) * (p.Y >= 0 ? 1 : -1));
        return p;
    }

    /// <summary>The inverse of <see cref="EncodeNormal"/>.</summary>
    public static Vector3 DecodeNormal(Vector2 p)
    {
        var n = new Vector3(p.X, p.Y, 1 - MathF.Abs(p.X) - MathF.Abs(p.Y));
        float t = MathF.Max(-n.Z, 0);
        n.X += n.X >= 0 ? -t : t;
        n.Y += n.Y >= 0 ? -t : t;
        return Vector3.Normalize(n);
    }

    /// <summary>The direction frame (i, j) of a <paramref name="grid"/>-wide atlas looks from (towards the viewer, object space).</summary>
    public static Vector3 FrameDirection(int i, int j, int grid) => Decode(new Vector2(i, j) / (grid - 1));

    /// <summary>
    /// A frame's image axes: <paramref name="right"/> and <paramref name="up"/> span the image, <paramref name="d"/> points to the viewer.
    /// Up is the object's +Y projected onto the image plane (−Z when looking straight down).
    /// </summary>
    public static void Basis(Vector3 d, out Vector3 right, out Vector3 up)
    {
        var reference = d.Y > 0.999f ? new Vector3(0, 0, -1) : Vector3.UnitY;
        right = Vector3.Normalize(Vector3.Cross(reference, d));
        up = Vector3.Cross(d, right);
    }

    /// <summary>
    /// The three frames to blend for a view direction (object space, towards the eye) and their weights (sum 1): the grid cell around the
    /// direction's UV is split along its anti-diagonal and the frames at the corners of the half it falls in are weighted barycentrically.
    /// At a frame's own direction that frame has weight 1.
    /// </summary>
    public static (Vector2 A, Vector2 B, Vector2 C, Vector3 Weights) Select(Vector3 viewDirection, int grid)
    {
        var g = Encode(viewDirection) * (grid - 1);
        var cell = new Vector2(Math.Clamp(MathF.Floor(g.X), 0, grid - 2), Math.Clamp(MathF.Floor(g.Y), 0, grid - 2));
        var f = g - cell;
        if (f.X + f.Y <= 1)
            return (cell, cell + Vector2.UnitX, cell + Vector2.UnitY, new Vector3(1 - f.X - f.Y, f.X, f.Y));
        return (cell + Vector2.One, cell + Vector2.UnitX, cell + Vector2.UnitY, new Vector3(f.X + f.Y - 1, 1 - f.Y, 1 - f.X));
    }

    /// <summary>
    /// The ground distance beyond which an instance of bounding radius <paramref name="worldRadius"/> (mesh radius × instance scale) is drawn
    /// as its impostor: where the frame's <paramref name="framePixels"/> cover the sphere's projected diameter at a screen
    /// <paramref name="screenHeight"/> pixels high with a vertical field of view <paramref name="fieldOfView"/> (one atlas texel per pixel or
    /// fewer), and never nearer than <paramref name="minimum"/>.
    /// </summary>
    public static float TransitionDistance(float worldRadius, int framePixels, float screenHeight, float fieldOfView, float minimum) =>
        MathF.Max(minimum, worldRadius * screenHeight / (MathF.Tan(fieldOfView / 2) * framePixels));
}


/// <summary>
/// What an atlas is baked at (docs/impostors.md, "Size classes"): frame pixels and grid. The frame size follows from how big the largest
/// instance of the mesh is on the screen at the reference transition distance (<see cref="For"/>), so a small bush gets a 64-pixel frame and a
/// big tree a 256-pixel one; the grid is the same for all (<see cref="DefaultGrid"/>).
/// </summary>
public readonly record struct ImpostorClass(string Name, int FramePixels, int Grid, float BakeDistance = ImpostorClass.ReferenceDistance)
{
    /// <summary>
    /// The small class (docs/impostors.md section 11): meshes whose largest instance is under <see cref="MinimumRadius"/> but at least
    /// <see cref="SmallMinimumRadius"/> and that have at least <see cref="SmallMinimumTriangles"/> triangles (a few triangles cost less than the
    /// impostor's quad). Small frames (<see cref="SmallFrame"/>), and a transition distance of its own, proportional to the size
    /// (<see cref="Transition"/>): the largest instance is <c>27.8</c> pixels across at its transition whatever its size, as the smallest
    /// medium mesh (radius 48) is at 4000. <c>MEITOU_IMPOSTOR_SMALL=0</c> switches the class off (the meshes stay meshes, as before).
    /// </summary>
    public static readonly bool SmallEnabled = Environment.GetEnvironmentVariable("MEITOU_IMPOSTOR_SMALL") != "0";
    public static readonly float SmallMinimumRadius = EnvFloat("MEITOU_IMPOSTOR_SMALL_MIN_RADIUS", 2, 0);
    public static readonly int SmallMinimumTriangles = (int)EnvFloat("MEITOU_IMPOSTOR_SMALL_MIN_TRIANGLES", 100, 0);
    public static readonly int SmallFrame = (int)EnvFloat("MEITOU_IMPOSTOR_SMALL_FRAME", 32, 8);
    public static readonly int SmallGrid = (int)EnvFloat("MEITOU_IMPOSTOR_SMALL_GRID", 12, 2);

    /// <summary>The small class.</summary>
    public bool IsSmall => Name == "small";

    /// <summary>
    /// The ground distance from which a mesh of this class is its impostor, given the user's impostor distance (<paramref name="impostorDistance"/>, 4000 by
    /// default) and the radius of its largest instance: that distance for medium and large atlases, and in proportion to the radius for the small class.
    /// </summary>
    public float Transition(float worldRadius, float impostorDistance) => IsSmall ? impostorDistance * worldRadius / MinimumRadius : impostorDistance;

    /// <summary>Instances smaller than this (radius in world units at the record's largest scale) get no impostor.</summary>
    public const float MinimumRadius = 48;

    /// <summary>The picture height (pixels) and the transition distance (units) the frame size and the bake's texture detail are chosen for.</summary>
    public const float ReferenceHeight = 1080, ReferenceDistance = 4000;
    static readonly float HalfFovTan = MathF.Tan(25 * MathF.PI / 180);

    static float EnvFloat(string name, float fallback, float min) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && v >= min ? v : fallback;

    /// <summary>Frames per atlas side (<c>MEITOU_IMPOSTOR_GRID</c> overrides, for experiments; it is part of the cache key through the atlas).</summary>
    public static readonly int DefaultGrid = (int)EnvFloat("MEITOU_IMPOSTOR_GRID", 12, 2);
    /// <summary>How much larger than its frame an instance may look on the screen at the reference distance (<c>MEITOU_IMPOSTOR_MAGNIFY</c>).</summary>
    public static readonly float Magnification = EnvFloat("MEITOU_IMPOSTOR_MAGNIFY", 1.4f, 0.5f);
    /// <summary>Scales the bake's LOD bias (<c>MEITOU_IMPOSTOR_BIAS</c>, 1 by default; 0 turns it off, for comparisons).</summary>
    public static readonly float BiasScale = EnvFloat("MEITOU_IMPOSTOR_BIAS", 1f, 0f);
    public const int MinFrame = 64, MaxFrame = 256;

    public static readonly ImpostorClass Medium = new("medium", 128, DefaultGrid);
    public static readonly ImpostorClass Large = new("large", 256, DefaultGrid);

    /// <summary>A sphere of <paramref name="worldRadius"/> units seen from <paramref name="distance"/>: its diameter in pixels at <see cref="ReferenceHeight"/>.</summary>
    public static float ScreenDiameter(float worldRadius, float distance = ReferenceDistance) => worldRadius * ReferenceHeight / (HalfFovTan * Math.Max(distance, 1));

    /// <summary>
    /// The class for a largest instance radius, or null when the mesh is too small to need an impostor: under <see cref="MinimumRadius"/> only the small class
    /// (<see cref="SmallMinimumRadius"/> and up, and <paramref name="triangles"/> at least <see cref="SmallMinimumTriangles"/>; the count is optional, a caller that
    /// does not know it gets the class by size alone).
    /// </summary>
    public static ImpostorClass? For(float worldRadius, int triangles = int.MaxValue)
    {
        if (worldRadius < MinimumRadius)
            return SmallEnabled && worldRadius >= SmallMinimumRadius && triangles >= SmallMinimumTriangles
                ? new ImpostorClass("small", SmallFrame, SmallGrid, ReferenceDistance * worldRadius / MinimumRadius) : null;
        float need = ScreenDiameter(worldRadius) / Magnification;
        int frame = MinFrame;
        while (frame < need && frame < MaxFrame) frame *= 2;
        return new ImpostorClass(frame.ToString(System.Globalization.CultureInfo.InvariantCulture), frame, DefaultGrid);
    }

    /// <summary>
    /// The texture LOD bias the bake uses (docs/impostors.md "Look"): the bake draws the mesh at 2 x the frame size, far finer than the screen
    /// shows it at the transition, so its cut-out (the leaves' mip alpha) and its colours (the leaf texture's mips) would be those of a near mesh,
    /// and the impostor fuller and brighter than the mesh at the same distance. The bias is log2 of the ratio of the bake's texel density to the
    /// screen's at the reference distance for an instance of <paramref name="meanWorldRadius"/> (mesh radius x the mean scale).
    /// </summary>
    public float LodBias(float meanWorldRadius) => Math.Clamp(MathF.Log2(2 * FramePixels / Math.Max(ScreenDiameter(meanWorldRadius, BakeDistance), 1e-3f)), 0, 4) * BiasScale;

    /// <summary>Mip levels baked per frame: down to 4 x 4 pixels, so every level stays aligned to the 4 x 4 blocks of the compressed formats.</summary>
    public int Levels => BitOperations.Log2((uint)FramePixels) - 1;

    public int AtlasPixels => FramePixels * Grid;
}
