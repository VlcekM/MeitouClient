using System.Numerics;

namespace Meitou.Data.World;

/// <summary>The game's <c>shadow mode</c> setting (settings.cfg; docs/formats/shadows.md).</summary>
public enum ShadowMode
{
    /// <summary>0: <c>Main_Lighting_NoShadow</c>, no shadow map at all.</summary>
    Disabled = 0,
    /// <summary>1: "CSM", four cascades in one atlas (<c>Main_Lighting_CSM</c>, the "legacy" material).</summary>
    Cascaded = 1,
    /// <summary>2: "RTWSM", one rectilinear-texture-warped map (<c>Main_Lighting_RTW</c>). Not reproduced by the viewer yet.</summary>
    Rectilinear = 2,
}

/// <summary>
/// The game's sun-shadow constants (docs/formats/shadows.md), Verified 2026-10-05 against <c>kenshi_x64.exe</c> (the deferred light
/// renderer's shadow-mode switch, the CSM and RTW shadow classes, the options screen and the settings loader) and the shipped
/// <c>data/materials</c> shaders. The cascaded path is what <see cref="ShadowCascades"/> rebuilds.
/// </summary>
public static class KenshiShadows
{
    /// <summary><c>Shadow Range</c> (settings.cfg): the default when the key is missing, and the options slider's limits.</summary>
    public const float DefaultRange = 5000, MinRange = 1000, MaxRange = 9000;
    /// <summary>The shadow map's side, indexed by <c>shadow quality</c> (settings.cfg); a missing key leaves the index at 0.</summary>
    public static readonly int[] QualitySizes = [1024, 2048, 4096];
    /// <summary>The size the renderer uses when it creates its shadows lazily (before the options apply theirs).</summary>
    public const int LazyMapSize = 2048;

    // ---- CSM ----
    /// <summary>Cascades of the CSM mode; they share one square map as a ceil(√n)² grid of tiles (2 × 2).</summary>
    public const int CascadeCount = 4;
    /// <summary>The first split (the start of the split scheme, not the camera's near plane).</summary>
    public const float SplitNear = 1;
    /// <summary>Weight of the logarithmic split against the uniform one.</summary>
    public const float SplitLambda = 0.95f;
    /// <summary>Every split between the first and the last is multiplied by this.</summary>
    public const float SplitScale = 0.5f;
    /// <summary>A light direction whose |y| is above this (and above |z|) takes world +Z as the light view's up, not +Y.</summary>
    public const float VerticalLight = 0.985f;
    /// <summary>The cascade box's depth is grown at each end by max(this × depth, <see cref="MinDepthMargin"/>) plus one depth step.</summary>
    public const float DepthMargin = 0.025f, MinDepthMargin = 2;
    /// <summary>The box centre's depth is snapped to the cascade's size over this.</summary>
    public const float DepthSteps = 65536;
    /// <summary>Caster depth bias: <c>z += min(MaxSlope, Slope · |∇z|) + fixed</c> with fixed = max(FixedFactor · 2 / depth, MinFixed) (depth 0..1).</summary>
    public const float SlopeBias = 4, MaxSlopeBias = 0.04f, FixedBiasFactor = 0.75f, MinFixedBias = 0.001f;
    /// <summary>PCF radius per cascade: max(FilterFactor · 2 / size, MinFilterTexels / tile pixels) in the atlas's UV units.</summary>
    public const float FilterFactor = 0.3f, MinFilterTexels = 0.8f;
    /// <summary>PCF taps of the CSM receiver, and the factor the radius is scaled by before the taps' hexagonal offsets.</summary>
    public const int PcfTaps = 12;
    public const float PcfOffsetScale = 0.3f;
    /// <summary>
    /// The receiver's twelve tap offsets ("HEX12", <c>shadowFunctions.hlsl</c>), in units of the scaled radius, in the D3D atlas frame
    /// (v down). Points of a hexagonal lattice out to √7 ≈ 2.65; the second and third entries are the same point (the game's list repeats
    /// it), so that point weighs twice in the mean and the point opposite it, (−0.5, −0.866), is missing.
    /// </summary>
    public static readonly Vector2[] PcfOffsets =
    [
        new(1, 0), new(-0.5f, 0.866025f), new(-0.5f, 0.866025f),
        new(2.5f, 0.866025f), new(1, 2 * 0.866025f), new(-0.5f, 3 * 0.866025f),
        new(-2, 2 * 0.866025f), new(-2, 0), new(-2, -2 * 0.866025f),
        new(-0.5f, -3 * 0.866025f), new(1, -2 * 0.866025f), new(2.5f, -0.866025f),
    ];
    /// <summary>The noise shifts every tap by noise × this along the atlas's u before the turn.</summary>
    public const float PcfJitterShift = 0.25f;
    /// <summary>The turn is noise × this (the game's 2 · 3.1415, not 2π).</summary>
    public const float PcfJitterAngle = 2 * 3.1415f;
    /// <summary>The noise texture is read at the atlas UV times this (point sampled, wrapped).</summary>
    public const float NoiseScale = 1024;
    /// <summary>The noise texture's file (<c>data/materials</c>), bound to the CSM lighting material's <c>WarpMap</c> unit.</summary>
    public const string NoiseTexture = "white-noise.png";
    /// <summary>
    /// The game camera's near and far clip (Observed: camera.md, sky.md). The receiver picks the cascade by its pixel's clip-space z,
    /// <c>f (d − n) / (f − n)</c>, and the cascades are boxed from the camera's own near plane.
    /// </summary>
    public const float CameraNear = 5, CameraFar = 50000;

    // ---- RTW (facts for a later reproduction) ----
    /// <summary>Side of the importance map; the warp maps are this × 2 (and this + 1 for the built one).</summary>
    public const int RtwImportanceSize = 512;
    /// <summary>The G-buffer is read as one point per this fraction of the screen in each direction.</summary>
    public const float RtwPointScale = 0.25f;
    /// <summary>Caster bias of the RTW map (fixed, slope, max slope).</summary>
    public static readonly Vector3 RtwBias = new(0, 6, 0.002f);

    /// <summary>The map side for a <c>shadow quality</c> value (out-of-range values clamp).</summary>
    public static int MapSize(int quality) => QualitySizes[Math.Clamp(quality, 0, QualitySizes.Length - 1)];

    /// <summary>
    /// The game's split distances: <c>splits[0] = near</c>, <c>splits[n] = far</c>, and in between
    /// <c>scale · (λ · near · (far / near)^(i/n) + (1 − λ) · (near + (far − near) · i/n))</c>. Cascade i serves view depths
    /// <c>splits[i] .. splits[i + 1]</c>.
    /// </summary>
    public static float[] Splits(float near, float far, int count, float lambda = SplitLambda, float scale = SplitScale)
    {
        var s = new float[count + 1];
        s[0] = near;
        for (int i = 1; i < count; i++)
        {
            double t = i / (double)count;
            double log = near * Math.Pow(far / near, t), uniform = near + (far - near) * t;
            s[i] = (float)((lambda * log + (1 - lambda) * uniform) * scale);
        }
        s[count] = far;
        return s;
    }
}

/// <summary>The camera the cascades are fitted to: its eye, view axes and perspective (vertical field of view, width over height).</summary>
public readonly record struct ShadowView(Vector3 Eye, Vector3 Forward, Vector3 Up, float FieldOfViewY, float Aspect, float Near);

/// <summary>What the viewer draws: the map's side, how far shadows reach (view depth), the number of cascades.</summary>
/// <param name="CameraNear">The game camera's near clip: the cascades' boxes start there, and with <paramref name="CameraFar"/> it turns the
/// game's clip-z cascade test into view depths (<see cref="ShadowCascade.SelectDepth"/>).</param>
public sealed record ShadowSettings(int MapSize = KenshiShadows.LazyMapSize, float Range = KenshiShadows.DefaultRange, int Cascades = KenshiShadows.CascadeCount,
    float CameraNear = KenshiShadows.CameraNear, float CameraFar = KenshiShadows.CameraFar)
{
    /// <summary>Tiles per row of the atlas.</summary>
    public int Grid => (int)Math.Ceiling(Math.Sqrt(Cascades));
    /// <summary>Side of one cascade's tile in texels.</summary>
    public int TileSize => MapSize / Grid;
}

/// <summary>
/// One fitted cascade. Light space: x right, y up (the light view's), z away from the sun; a world point maps to
/// <c>ls = R · p + Translation</c>, then to the tile by <c>u = ls.x / Extent.x + ½</c>, <c>v = ls.y / Extent.y + ½</c> and to depth
/// <c>ls.z / Extent.z + ½</c> (0 nearest the sun, D3D/Vulkan style; casters nearer the sun than the box are flattened onto 0).
/// </summary>
public sealed class ShadowCascade
{
    public int Index { get; init; }
    /// <summary>View depths this cascade serves (the previous split up to its own).</summary>
    public float NearDepth { get; init; }
    public float FarDepth { get; init; }
    /// <summary>
    /// The view depth up to which the receiver uses this cascade: the game compares the pixel's clip-space z (Direct3D,
    /// <c>f (d − n) / (f − n)</c> with its camera's near and far) against <c>split[i + 1] − split[0]</c>, which is this depth.
    /// </summary>
    public float SelectDepth { get; init; }
    /// <summary>Whether the whole cascade lies in front of the camera's near plane, so no visible point uses it (the viewer then skips drawing it).</summary>
    public bool Unused { get; init; }
    /// <summary>World → light axes (rows x, y, z as the matrix's columns: System.Numerics row-vector form).</summary>
    public Matrix4x4 Rotation { get; init; }
    /// <summary>The snapped translation, in light space, kept in double so that far from the origin it still lands on whole texels.</summary>
    public (double X, double Y, double Z) Translation { get; init; }
    /// <summary>The box's size in light space (world units), in double like <see cref="Translation"/>.</summary>
    public (double X, double Y, double Z) Extent { get; init; }
    /// <summary>The box's size as floats.</summary>
    public Vector3 Size => new((float)Extent.X, (float)Extent.Y, (float)Extent.Z);
    /// <summary>The snapping step: the stable size over the tile's texels.</summary>
    public double Texel { get; init; }
    /// <summary>The caster bias of this cascade in depth units (0..1 over <see cref="Extent"/>.Z).</summary>
    public float FixedBias { get; init; }
    /// <summary>The PCF radius in the tile's UV units (0..1 across the tile).</summary>
    public float FilterRadius { get; init; }
    /// <summary>
    /// The tile's rectangle in the atlas: x0, y0, width, height in 0..1 (GL texture space, y up). As the game's: cascade i in column
    /// i / grid and Direct3D row i % grid counted from the top, so the whole atlas is the game's picture (taps that leave a tile read the
    /// same neighbour).
    /// </summary>
    public Vector4 Tile { get; init; }

    /// <summary>World → clip: x, y in −1..1 across the tile, z = depth 0..1 (w = 1). For drawing the casters (absolute world positions).</summary>
    public Matrix4x4 WorldToClip()
    {
        var r = Rotation;
        var (tx, ty, tz) = Translation;
        // clip = (R p + t) / (size / 2) for x and y; depth = (R p + t).z / size.z + 1/2.
        double sx = 2.0 / Extent.X, sy = 2.0 / Extent.Y, sz = 1.0 / Extent.Z;
        return new Matrix4x4(
            (float)(r.M11 * sx), (float)(r.M12 * sy), (float)(r.M13 * sz), 0,
            (float)(r.M21 * sx), (float)(r.M22 * sy), (float)(r.M23 * sz), 0,
            (float)(r.M31 * sx), (float)(r.M32 * sy), (float)(r.M33 * sz), 0,
            (float)(tx * sx), (float)(ty * sy), (float)(tz * sz + 0.5), 1);
    }

    /// <summary>
    /// (p − <paramref name="origin"/>) → (u, v, depth) in the tile (0..1 each), the receiver's lookup: the large translation is folded with
    /// the origin in double, so the matrix holds only small numbers.
    /// </summary>
    public Matrix4x4 OriginToTile(Vector3 origin)
    {
        var r = Rotation;
        var (tx, ty, tz) = Translation;
        double ox = r.M11 * (double)origin.X + r.M21 * (double)origin.Y + r.M31 * (double)origin.Z + tx;
        double oy = r.M12 * (double)origin.X + r.M22 * (double)origin.Y + r.M32 * (double)origin.Z + ty;
        double oz = r.M13 * (double)origin.X + r.M23 * (double)origin.Y + r.M33 * (double)origin.Z + tz;
        double sx = 1.0 / Extent.X, sy = 1.0 / Extent.Y, sz = 1.0 / Extent.Z;
        return new Matrix4x4(
            (float)(r.M11 * sx), (float)(r.M12 * sy), (float)(r.M13 * sz), 0,
            (float)(r.M21 * sx), (float)(r.M22 * sy), (float)(r.M23 * sz), 0,
            (float)(r.M31 * sx), (float)(r.M32 * sy), (float)(r.M33 * sz), 0,
            (float)(ox * sx + 0.5), (float)(oy * sy + 0.5), (float)(oz * sz + 0.5), 1);
    }

    /// <summary>
    /// Planes (inward normal, d; <c>n · p + d ≥ 0</c> inside) for culling the casters: the box's four sides and its far end. There is no
    /// near plane: anything between the box and the sun still casts (the game disables near-plane culling and flattens such casters).
    /// </summary>
    public Vector4[] CullPlanes()
    {
        var m = WorldToClip();
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        return [c4 + c1, c4 - c1, c4 + c2, c4 - c2, c4 - c3];
    }

    /// <summary>A world point's (u, v, depth) in the tile, in double (tests and debugging).</summary>
    public Vector3 Project(Vector3 p)
    {
        var r = Rotation;
        var (tx, ty, tz) = Translation;
        double x = r.M11 * (double)p.X + r.M21 * (double)p.Y + r.M31 * (double)p.Z + tx;
        double y = r.M12 * (double)p.X + r.M22 * (double)p.Y + r.M32 * (double)p.Z + ty;
        double z = r.M13 * (double)p.X + r.M23 * (double)p.Y + r.M33 * (double)p.Z + tz;
        return new Vector3((float)(x / Extent.X + 0.5), (float)(y / Extent.Y + 0.5), (float)(z / Extent.Z + 0.5));
    }
}

/// <summary>
/// The game's CSM fitting (docs/formats/shadows.md, "Cascade fitting"), backend-independent: per cascade a light view along the sun,
/// a box around the camera frustum from its near plane to the cascade's split, a size that does not change as the camera turns (from
/// the bounding sphere of the cascade's own slice), and a centre snapped to whole texels so the map does not shimmer as the camera moves.
/// </summary>
public static class ShadowCascades
{
    /// <summary>The light view's rotation for a direction towards the sun: z points away from the sun, y is world +Y (or +Z for a nearly vertical sun) made perpendicular.</summary>
    public static Matrix4x4 LightRotation(Vector3 toSun)
    {
        var d = Vector3.Normalize(toSun);
        float ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        var up = ay > KenshiShadows.VerticalLight && az < ay ? Vector3.UnitZ : Vector3.UnitY;
        var z = -d;
        var y = Vector3.Normalize(up - z * Vector3.Dot(z, up));
        var x = Vector3.Normalize(Vector3.Cross(y, z));
        // Row-vector form: ls = p · M, so the axes are the columns.
        return new Matrix4x4(
            x.X, y.X, z.X, 0,
            x.Y, y.Y, z.Y, 0,
            x.Z, y.Z, z.Z, 0,
            0, 0, 0, 1);
    }

    /// <summary>
    /// The stable size of a cascade serving view depths <paramref name="near"/>..<paramref name="far"/>:
    /// <c>max(√((f + n)² k + (f − n)²), 2 f √k)</c> with the game's <c>k = (aspect · tan(fovY / 2))² + 2 tan(fovY / 2)</c>.
    /// </summary>
    /// <remarks>The textbook bounding sphere of a frustum slice has <c>k = (1 + aspect²) tan²</c>; the game's <c>k</c> (Verified in the
    /// disassembly: the tangent is added twice, not squared) is larger, which only makes the cascade bigger than needed.</remarks>
    public static double StableSize(double near, double far, double fieldOfViewY, double aspect)
    {
        double t = Math.Tan(fieldOfViewY * 0.5);
        double k = aspect * t * (aspect * t) + t + t;
        double a = Math.Sqrt((far + near) * (far + near) * k + (far - near) * (far - near));
        double b = 2 * far * Math.Sqrt(k);
        return Math.Max(a, b);
    }

    /// <summary>Fits every cascade for this camera and sun. <paramref name="toSun"/> must not be zero.</summary>
    public static ShadowCascade[] Fit(ShadowView view, Vector3 toSun, ShadowSettings settings)
    {
        var splits = KenshiShadows.Splits(KenshiShadows.SplitNear, settings.Range, settings.Cascades);
        var rotation = LightRotation(toSun);
        var result = new ShadowCascade[settings.Cascades];
        var forward = Vector3.Normalize(view.Forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, view.Up));
        var up = Vector3.Cross(right, forward);
        double t = Math.Tan(view.FieldOfViewY * 0.5);
        int grid = settings.Grid, tile = settings.TileSize;
        Span<double> corner = stackalloc double[3];
        for (int i = 0; i < settings.Cascades; i++)
        {
            double far = splits[i + 1];
            double size = StableSize(splits[i], far, view.FieldOfViewY, view.Aspect);
            double texel = size / tile, step = size / KenshiShadows.DepthSteps;

            // The light-space box of the camera frustum from its own near plane to this cascade's split.
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            // The game boxes from its camera's near plane; below that the viewer's own (nearer) plane, so every visible point is inside.
            double near = Math.Min(view.Near, settings.CameraNear);
            foreach (double depth in (ReadOnlySpan<double>)[near, far])
                for (int c = 0; c < 4; c++)
                {
                    double h = depth * t, w = h * view.Aspect;
                    double sx = (c & 1) == 0 ? -w : w, sy = (c & 2) == 0 ? -h : h;
                    for (int a = 0; a < 3; a++)
                        corner[a] = view.Eye.Component(a) + forward.Component(a) * depth + right.Component(a) * sx + up.Component(a) * sy;
                    double lx = rotation.M11 * corner[0] + rotation.M21 * corner[1] + rotation.M31 * corner[2];
                    double ly = rotation.M12 * corner[0] + rotation.M22 * corner[1] + rotation.M32 * corner[2];
                    double lz = rotation.M13 * corner[0] + rotation.M23 * corner[1] + rotation.M33 * corner[2];
                    minX = Math.Min(minX, lx); maxX = Math.Max(maxX, lx);
                    minY = Math.Min(minY, ly); maxY = Math.Max(maxY, ly);
                    minZ = Math.Min(minZ, lz); maxZ = Math.Max(maxZ, lz);
                }
            double margin = Math.Max((maxZ - minZ) * KenshiShadows.DepthMargin, KenshiShadows.MinDepthMargin) + step;
            minZ -= margin;
            maxZ += margin;
            var box = (X: Math.Max(size, maxX - minX), Y: Math.Max(size, maxY - minY), Z: Math.Max(size, maxZ - minZ));
            // The translation that centres the box, snapped down to whole texels (x, y) and depth steps (z), in absolute light space.
            double cx = -(minX + maxX) * 0.5, cy = -(minY + maxY) * 0.5, cz = -(minZ + maxZ) * 0.5;
            var translation = (Math.Floor(cx / texel) * texel, Math.Floor(cy / texel) * texel, Math.Floor(cz / step) * step);
            double fixedBias = Math.Max(KenshiShadows.FixedBiasFactor * 2.0 / box.Z, KenshiShadows.MinFixedBias);
            // The game's radius is in the atlas's UV; one tile spans 1 / grid of it.
            double atlasRadius = Math.Max(KenshiShadows.FilterFactor * 2.0 / box.X, KenshiShadows.MinFilterTexels / tile);
            result[i] = new ShadowCascade
            {
                Index = i,
                NearDepth = splits[i],
                Unused = far <= view.Near,
                FarDepth = (float)far,
                SelectDepth = SelectDepth(far - splits[0], settings.CameraNear, settings.CameraFar),
                Rotation = rotation,
                Translation = translation,
                Extent = box,
                Texel = texel,
                FixedBias = (float)fixedBias,
                FilterRadius = (float)(atlasRadius * grid),
                Tile = new Vector4(i / grid / (float)grid, (grid - 1 - i % grid) / (float)grid, 1f / grid, 1f / grid),
            };
        }
        return result;
    }

    /// <summary>The view depth at which the game's clip-space z, <c>f (d − n) / (f − n)</c>, reaches <paramref name="clipZ"/>.</summary>
    public static float SelectDepth(double clipZ, double near, double far) => (float)(near + clipZ * (far - near) / far);

    static double Component(this Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
}
