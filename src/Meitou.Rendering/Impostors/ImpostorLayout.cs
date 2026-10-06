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
/// What an atlas is baked at, chosen by the mesh's size class (docs/impostors.md, "Size classes"): frame pixels and grid. The class comes from
/// the largest instance radius the placer can produce, the mesh's bounding radius × the record's maximum scale.
/// </summary>
public readonly record struct ImpostorClass(string Name, int FramePixels, int Grid)
{
    /// <summary>Instances smaller than this (radius in world units at the record's largest scale) get no impostor.</summary>
    public const float MinimumRadius = 48;

    public static readonly ImpostorClass Medium = new("medium", 128, 12);
    public static readonly ImpostorClass Large = new("large", 256, 12);

    /// <summary>The class for a largest instance radius, or null when the mesh is too small to need an impostor.</summary>
    public static ImpostorClass? For(float worldRadius) => worldRadius < MinimumRadius ? null : worldRadius < 160 ? Medium : Large;

    /// <summary>Mip levels baked per frame: down to 4 × 4 pixels, so every level stays aligned to the 4 × 4 blocks of the compressed formats.</summary>
    public int Levels => BitOperations.Log2((uint)FramePixels) - 1;

    public int AtlasPixels => FramePixels * Grid;
}
