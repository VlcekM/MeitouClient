using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The cascade fitting of the Meitou shadows (not the game's; docs/formats/shadows.md, "Meitou shadows"): the splits run from the
/// camera's near plane (quantised, so they do not move every frame while zooming) to the shadow range, so all four cascades cover
/// something visible (the game's halved splits leave the first two in front of the near plane in most views); each cascade is a
/// light-space square around the bounding sphere of its own slice (rotation-invariant size, centre snapped to whole texels, as the
/// game's), slightly enlarged so that a cascade drawn a few frames ago still covers the view; the box reaches further towards the sun
/// so casters there keep their depth (for the penumbra width).
/// </summary>
public static class MeitouShadowFit
{
    /// <summary>Weight of the logarithmic split against the uniform one.</summary>
    public const float Lambda = 0.8f;
    /// <summary>Share of each slice over which the next cascade is blended in (the next cascade's fit includes it).</summary>
    public const float BlendBand = 0.15f;
    /// <summary>Near planes are rounded down to powers of this, so the splits change only in steps.</summary>
    public const float NearStep = 1.25f;
    /// <summary>The box is this much larger than its slice's sphere (per cascade): slack for cascades redrawn every few frames.</summary>
    public static readonly float[] Margin = [0.02f, 0.06f, 0.08f, 0.08f];
    /// <summary>The box's depth reaches this many sphere radii towards the sun beyond the sphere (casters there keep their depth).</summary>
    public const float SunwardReach = 2;

    /// <summary>The near plane rounded down to a power of <see cref="NearStep"/> (at least 1).</summary>
    public static float QuantizedNear(float near) =>
        near <= 1 ? 1 : MathF.Pow(NearStep, MathF.Floor(MathF.Log(near) / MathF.Log(NearStep)));

    /// <summary>The practical split scheme between <paramref name="near"/> and <paramref name="far"/> (no halving).</summary>
    public static float[] Splits(float near, float far, int count)
    {
        var s = new float[count + 1];
        s[0] = near;
        for (int i = 1; i < count; i++)
        {
            double t = i / (double)count;
            s[i] = (float)(Lambda * near * Math.Pow(far / near, t) + (1 - Lambda) * (near + (far - near) * t));
        }
        s[count] = far;
        return s;
    }

    /// <summary>The view depths cascade <paramref name="i"/> covers: its slice, started earlier by the previous slice's blend band.</summary>
    public static (float Near, float Far) Slice(float[] splits, int i) =>
        (i == 0 ? splits[0] : splits[i] - BlendBand * (splits[i] - splits[i - 1]), splits[i + 1]);

    /// <summary>
    /// The bounding sphere of the view frustum between view depths <paramref name="near"/> and <paramref name="far"/>: its distance
    /// along the view axis from the eye and its radius (the textbook form; the game's own enlarges it, see <see cref="ShadowCascades.StableSize"/>).
    /// </summary>
    public static (double Centre, double Radius) Sphere(double near, double far, double fieldOfViewY, double aspect)
    {
        double t = Math.Tan(fieldOfViewY * 0.5);
        double k = (1 + aspect * aspect) * t * t;   // a corner's squared distance from the axis per squared depth
        double c = Math.Min(far, (far + near) * 0.5 * (1 + k));
        double r = Math.Max(Math.Sqrt((far - c) * (far - c) + far * far * k), Math.Sqrt((c - near) * (c - near) + near * near * k));
        return (c, r);
    }

    /// <summary>The light-space centre of the slice's sphere for this view (rotation from <see cref="ShadowCascades.LightRotation"/>).</summary>
    public static (double X, double Y, double Z) LightCentre(ShadowView view, Matrix4x4 rotation, double along)
    {
        var f = Vector3.Normalize(view.Forward);
        double px = view.Eye.X + f.X * along, py = view.Eye.Y + f.Y * along, pz = view.Eye.Z + f.Z * along;
        return (rotation.M11 * px + rotation.M21 * py + rotation.M31 * pz,
                rotation.M12 * px + rotation.M22 * py + rotation.M32 * pz,
                rotation.M13 * px + rotation.M23 * py + rotation.M33 * pz);
    }

    /// <summary>Fits cascade <paramref name="i"/> of <paramref name="splits"/> to this view and sun (<paramref name="toSun"/> normalised).</summary>
    public static ShadowCascade Fit(ShadowView view, Vector3 toSun, ShadowSettings settings, float[] splits, int i)
    {
        var rotation = ShadowCascades.LightRotation(toSun);
        var (near, far) = Slice(splits, i);
        var (along, radius) = Sphere(near, far, view.FieldOfViewY, view.Aspect);
        var (cx, cy, cz) = LightCentre(view, rotation, along);
        int grid = settings.Grid, tile = settings.TileSize;
        double half = radius * (1 + Margin[Math.Min(i, Margin.Length - 1)]);
        double size = 2 * half, texel = size / tile;
        double depth = size + SunwardReach * radius, step = depth / KenshiShadows.DepthSteps;
        // Light space: z grows away from the sun, so the box's centre sits sunward of the sphere's by half the extra reach.
        double zCentre = cz - SunwardReach * radius * 0.5;
        var translation = (Math.Floor(-cx / texel) * texel, Math.Floor(-cy / texel) * texel, Math.Floor(-zCentre / step) * step);
        return new ShadowCascade
        {
            Index = i,
            NearDepth = near,
            FarDepth = far,
            Unused = far <= view.Near,
            Rotation = rotation,
            Translation = translation,
            Extent = (size, size, depth),
            Texel = texel,
            FixedBias = (float)Math.Max(KenshiShadows.FixedBiasFactor * 2.0 / depth, KenshiShadows.MinFixedBias * 0.25),
            FilterRadius = (float)(1.5 / tile),
            Tile = new Vector4(i % grid / (float)grid, i / grid / (float)grid, 1f / grid, 1f / grid),
        };
    }

    /// <summary>
    /// Whether <paramref name="cascade"/> (fitted some frames ago) still holds this view's slice, with <paramref name="border"/> world
    /// units to spare at its sides for the receiver's filter; when not, it must be fitted and drawn again.
    /// </summary>
    public static bool Covers(ShadowCascade cascade, ShadowView view, float[] splits, double border)
    {
        var (near, far) = Slice(splits, cascade.Index);
        var (along, radius) = Sphere(near, far, view.FieldOfViewY, view.Aspect);
        var (cx, cy, _) = LightCentre(view, cascade.Rotation, along);
        var (tx, ty, _) = cascade.Translation;
        var (ex, ey, _) = cascade.Extent;
        return Math.Abs(cx + tx) + radius + border <= ex * 0.5 && Math.Abs(cy + ty) + radius + border <= ey * 0.5;
    }
}
