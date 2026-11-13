using System.Numerics;

namespace Meitou.Data.World;

/// <summary>
/// The cascade fitting of the Meitou shadows (not the game's; docs/formats/shadows.md, "Meitou shadows"): the splits run from the
/// camera's near plane (quantised, so they do not move every frame while zooming) to the shadow range, so all the cascades cover
/// something visible (the game's halved splits leave the first two in front of the near plane in most views); each cascade is a
/// light-space square around the bounding sphere of its own slice (rotation-invariant size, centre snapped to whole texels as the
/// game's, but on a grid through an anchor near the camera rather than through the world origin, so a turning sun does not slide it),
/// slightly enlarged so that a cascade drawn a few frames ago still covers the view; the box reaches further towards the sun
/// so casters there keep their depth (for the penumbra width).
/// </summary>
public static class MeitouShadowFit
{
    /// <summary>
    /// Weight of the logarithmic split against the uniform one.
    /// </summary>
    public const float Lambda = 0.8f;
    /// <summary>
    /// Share of each slice over which the next cascade is blended in (the next cascade's fit includes it).
    /// </summary>
    public const float BlendBand = 0.15f;
    /// <summary>
    /// Near planes are rounded down to powers of this, so the splits change only in steps.
    /// </summary>
    public const float NearStep = 1.25f;
    /// <summary>
    /// The Meitou shadows' number of cascades by default (the game's CSM has <see cref="KenshiShadows.CascadeCount"/>).
    /// </summary>
    public const int DefaultCascades = 5;

    /// <summary>
    /// How much larger than its slice's sphere the box of cascade <paramref name="i"/> of <paramref name="count"/> is: slack for cascades redrawn
    /// every few frames, more the less often they are (ShadowPass.Cadence): 0.02 for the first, 0.06, 0.08 and 0.08 for the last three, 0.04 for any
    /// between (four cascades: 0.02, 0.06, 0.08, 0.08; five: 0.02, 0.04, 0.06, 0.08, 0.08).
    /// </summary>
    public static float Margin(int i, int count) => i == 0 ? 0.02f : (count - 1 - i) switch { 0 or 1 => 0.08f, 2 => 0.06f, _ => 0.04f };
    /// <summary>
    /// The box's depth reaches this many sphere radii towards the sun beyond the sphere (casters there keep their depth).
    /// </summary>
    public const float SunwardReach = 2;

    /// <summary>
    /// The near plane rounded down to a power of <see cref="NearStep"/> (at least 1).
    /// </summary>
    public static float QuantizedNear(float near) =>
        near <= 1 ? 1 : MathF.Pow(NearStep, MathF.Floor(MathF.Log(near) / MathF.Log(NearStep)));

    /// <summary>
    /// The practical split scheme between <paramref name="near"/> and <paramref name="far"/> (no halving).
    /// </summary>
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

    /// <summary>
    /// The first split is at most this many times the camera's focus distance (<see cref="ShadowView.Focus"/>).
    /// </summary>
    public const float FocusSplit = 2;

    /// <summary>
    /// The splits with the first one sized by the camera's focus (<paramref name="focus"/>, 0: none): <see cref="FocusSplit"/> times it, rounded down
    /// to a power of <see cref="NearStep"/>, between twice the near plane and the practical scheme's own first split, and the other cascades split
    /// from there to <paramref name="far"/>. The practical scheme's first split is mostly its uniform term (a fifth of a quarter of the range: about
    /// 500 units at 10000), so a close-up drew its characters with the texels of a 500-unit slice, several times coarser than the game's
    /// first cascades.
    /// </summary>
    public static float[] Splits(float near, float far, int count, float focus)
    {
        if (count > BaseCascades) return Subdivided(Splits(near, far, BaseCascades, focus), count);
        var plain = Splits(near, far, count);
        if (focus <= 0 || count < 2) return plain;
        float first = Math.Clamp(QuantizedNear(FocusSplit * focus), near * 2, plain[1]);
        if (first >= plain[1]) return plain;
        var rest = Splits(first, far, count - 1);
        return [near, .. rest];
    }

    /// <summary>
    /// The cascades the split scheme is made for; more are fitted by <see cref="Subdivided"/>.
    /// </summary>
    public const int BaseCascades = 4;

    /// <summary>
    /// The splits of more than <see cref="BaseCascades"/> cascades: the four's (<paramref name="baseSplits"/>), with the extra cascades put into the
    /// four's second slice, which is the largest step in depth (its far end several times its near: 87 to 1016 units in a close-up at range 10000,
    /// 600 to 1500 from 900 away), by equal ratios (one extra: at the slice's geometric mean). The first slice and the last two stay as they are, so
    /// the near cascade and the far ones keep their texels; the second slice's texels get finer near its start, where the four's cascade 1 was
    /// coarsest for the depth it was seen at.
    /// </summary>
    public static float[] Subdivided(float[] baseSplits, int count)
    {
        int extra = count - (baseSplits.Length - 1);
        if (extra <= 0) return baseSplits;
        var s = new float[count + 1];
        s[0] = baseSplits[0];
        s[1] = baseSplits[1];
        double ratio = Math.Pow(baseSplits[2] / (double)baseSplits[1], 1.0 / (extra + 1));
        for (int k = 1; k <= extra; k++) s[1 + k] = (float)(baseSplits[1] * Math.Pow(ratio, k));
        for (int k = 2; k < baseSplits.Length; k++) s[k + extra] = baseSplits[k];
        return s;
    }

    /// <summary>
    /// The view depths cascade <paramref name="i"/> covers: its slice, started earlier by the previous slice's blend band.
    /// </summary>
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

    /// <summary>
    /// The light-space centre of the slice's sphere for this view (rotation from <see cref="ShadowCascades.LightRotation"/>).
    /// </summary>
    public static (double X, double Y, double Z) LightCentre(ShadowView view, Matrix4x4 rotation, double along)
    {
        var f = Vector3.Normalize(view.Forward);
        double px = view.Eye.X + f.X * along, py = view.Eye.Y + f.Y * along, pz = view.Eye.Z + f.Z * along;
        return (rotation.M11 * px + rotation.M21 * py + rotation.M31 * pz,
                rotation.M12 * px + rotation.M22 * py + rotation.M32 * pz,
                rotation.M13 * px + rotation.M23 * py + rotation.M33 * pz);
    }

    /// <summary>
    /// Fits cascade <paramref name="i"/> of <paramref name="splits"/> to this view and sun (<paramref name="toSun"/> normalised). The box
    /// is snapped to whole texels on a grid through <paramref name="anchor"/> (world units): the light rotation turns about the world
    /// origin, so a grid through the origin slides past a point by its distance from the origin times the angle the sun turned (at
    /// The Hub, 51000 units out, a time-lapse frame's 0.006 degrees slid it 5.6 units, three texels of the first cascade, and every
    /// shadow edge flickered); through a point near the camera the slide is that distance from the anchor instead.
    /// </summary>
    public static ShadowCascade Fit(ShadowView view, Vector3 toSun, ShadowSettings settings, float[] splits, int i,
        (double X, double Y, double Z) anchor = default)
    {
        var rotation = ShadowCascades.LightRotation(toSun);
        double ox = rotation.M11 * anchor.X + rotation.M21 * anchor.Y + rotation.M31 * anchor.Z,
            oy = rotation.M12 * anchor.X + rotation.M22 * anchor.Y + rotation.M32 * anchor.Z,
            oz = rotation.M13 * anchor.X + rotation.M23 * anchor.Y + rotation.M33 * anchor.Z;
        var (near, far) = Slice(splits, i);
        var (along, radius) = Sphere(near, far, view.FieldOfViewY, view.Aspect);
        var (cx, cy, cz) = LightCentre(view, rotation, along);
        int tile = settings.TileSize;
        double half = radius * (1 + Margin(i, splits.Length - 1));
        double size = 2 * half, texel = size / tile;
        double depth = size + SunwardReach * radius, step = depth / KenshiShadows.DepthSteps;
        // Light space: z grows away from the sun, so the box's centre sits sunward of the sphere's by half the extra reach.
        double zCentre = cz - SunwardReach * radius * 0.5;
        // Snapped on the grid through the anchor's light-space position (o): light space minus o is a whole number of texels at every texel corner.
        var translation = (Math.Floor((-cx + ox) / texel) * texel - ox, Math.Floor((-cy + oy) / texel) * texel - oy,
            Math.Floor((-zCentre + oz) / step) * step - oz);
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
            // No caster bias (FixedBias 0): the bias is the receiver's own, by its plane (MeitouShadowShaders, msCascade). A caster's pushes every
            // shadow off its caster's foot by the caster's slope, whatever receives it.
            FilterRadius = (float)(1.5 / tile),
            Tile = new Vector4(0, 0, 1, 1),   // the whole of its layer (i) of the cascade array
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

    /// <summary>
    /// The landmark shadow map's box (Meitou; docs/formats/shadows.md, "Landmark shadows"): one square light-space box around the bounding
    /// spheres (xyz centre, w radius) of every landmark drawn, so that each casts its shadow however far it is from the camera. A receiver
    /// outside the box is lit (no landmark lies towards the sun from it). Null without spheres. <see cref="ShadowCascade.Index"/> is
    /// <see cref="ShadowCascade.LandmarkIndex"/> (it is no cascade and has a map of its own), <see cref="ShadowCascade.Tile"/> the whole map.
    /// </summary>
    public static ShadowCascade? FitLandmarks(Vector3 toSun, IReadOnlyList<Vector4> spheres, int mapSize)
    {
        if (spheres.Count == 0) return null;
        var rotation = ShadowCascades.LightRotation(toSun);
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        foreach (var s in spheres)
        {
            double x = rotation.M11 * (double)s.X + rotation.M21 * (double)s.Y + rotation.M31 * (double)s.Z;
            double y = rotation.M12 * (double)s.X + rotation.M22 * (double)s.Y + rotation.M32 * (double)s.Z;
            double z = rotation.M13 * (double)s.X + rotation.M23 * (double)s.Y + rotation.M33 * (double)s.Z;
            (x0, x1, y0, y1, z0, z1) = (Math.Min(x0, x - s.W), Math.Max(x1, x + s.W), Math.Min(y0, y - s.W), Math.Max(y1, y + s.W),
                Math.Min(z0, z - s.W), Math.Max(z1, z + s.W));
        }
        double size = Math.Max(Math.Max(x1 - x0, y1 - y0), 1) * 1.02, texel = size / mapSize;
        double depth = Math.Max(z1 - z0, 1) * 1.02;
        return new ShadowCascade
        {
            Index = ShadowCascade.LandmarkIndex,
            Rotation = rotation,
            Translation = (-(x0 + x1) * 0.5, -(y0 + y1) * 0.5, -(z0 + z1) * 0.5),
            Extent = (size, size, depth),
            Texel = texel,
            // Half a texel's slope at 45° plus the usual fixed bias, in depth units.
            FixedBias = (float)Math.Max(KenshiShadows.FixedBiasFactor * 2.0 / depth, texel * 0.5 / depth),
            FilterRadius = 1.5f / mapSize,
            Tile = new Vector4(0, 0, 1, 1),
        };
    }
}